using FitCore.Application.Core;
using FitCore.Platform.Domain;
using FitCore.Platform.Identity;
using FitCore.Platform.Persistence;
using FitCore.Platform.Security;
using FitCore.SharedKernel;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace FitCore.Platform.Application.Roles;

public sealed record CreateRoleRequest(string Name, string? Description, bool GrantsAllBranches, IReadOnlyList<string>? Permissions);

public sealed record UpdateRoleRequest(string Name, string? Description, bool GrantsAllBranches, IReadOnlyList<string>? Permissions);

public sealed record CreateRoleCommand(CreateRoleRequest Request) : ICommand<RoleDto>;

public sealed record UpdateRoleCommand(Guid Id, UpdateRoleRequest Request) : ICommand<RoleDto>;

public sealed record DeleteRoleCommand(Guid Id) : ICommand<Unit>;

public sealed class CreateRoleRequestValidator : AbstractValidator<CreateRoleRequest>
{
    public CreateRoleRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MinimumLength(2).MaximumLength(60);
        RuleFor(x => x.Description).MaximumLength(300);
    }
}

public sealed class UpdateRoleRequestValidator : AbstractValidator<UpdateRoleRequest>
{
    public UpdateRoleRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MinimumLength(2).MaximumLength(60);
        RuleFor(x => x.Description).MaximumLength(300);
    }
}

internal static class RoleErrors
{
    public static readonly Error NotFound = Error.NotFound("roles.not_found", "Rol no encontrado.");
    public static readonly Error NameTaken = Error.Conflict("roles.name_taken", "Ya existe un rol con ese nombre.");
    public static readonly Error InUse = Error.Conflict("roles.in_use", "El rol está asignado a usuarios. Quíteselo antes de eliminarlo.");
    public static readonly Error SystemProtected = Error.Conflict("roles.system_protected", "Los roles de plantilla no se pueden eliminar ni renombrar.");
    public static readonly Error OwnerProtected = Error.Forbidden("roles.owner_protected", "El rol Propietario no se puede modificar.");
    public static readonly Error PermissionNotFound = Error.Validation("roles.permission_not_found", "Alguno de los permisos no existe.");
    public static readonly Error PermissionNotGrantable = Error.Forbidden("roles.permission_not_grantable", "No puede conceder permisos que usted no tiene.");
    public static readonly Error NotManageable = Error.Forbidden("roles.not_manageable", "No puede modificar un rol con más privilegios que los suyos.");
    public static readonly Error AllBranchesNotGrantable = Error.Forbidden("roles.all_branches_not_grantable", "Solo quien accede a todas las sucursales puede crear roles que las abarquen.");
}

/// <summary>Reglas comunes de la gestión de roles: validación de permisos y anti-escalada.</summary>
internal sealed class RoleAdministration(PlatformDbContext db)
{
    /// <summary>Los permisos de plataforma nunca se conceden desde la API de un gimnasio.</summary>
    public async Task<Error?> ValidateGrantAsync(
        UserAccess actor,
        IReadOnlyCollection<string> permissions,
        bool grantsAllBranches,
        CancellationToken cancellationToken)
    {
        var known = await db.PermissionCatalog.AsNoTracking()
            .Where(p => permissions.Contains(p.Code) && !p.Code.StartsWith("platform."))
            .CountAsync(cancellationToken);
        if (known != permissions.Count)
        {
            return RoleErrors.PermissionNotFound;
        }

        if (!permissions.All(actor.Has))
        {
            return RoleErrors.PermissionNotGrantable;
        }

        return grantsAllBranches && !actor.AllBranches ? RoleErrors.AllBranchesNotGrantable : null;
    }

    public async Task<RoleDto> ToDtoAsync(AppRole role, CancellationToken cancellationToken)
    {
        var permissions = await db.RolePermissions.AsNoTracking()
            .Where(rp => rp.RoleId == role.Id)
            .Select(rp => rp.PermissionCode)
            .OrderBy(c => c)
            .ToListAsync(cancellationToken);
        return new RoleDto(role.Id, role.Name!, role.Description, role.IsSystem, role.GrantsAllBranches, permissions);
    }
}

internal sealed class CreateRoleHandler(PlatformDbContext db, IAccessContext access, RoleAdministration administration)
    : ICommandHandler<CreateRoleCommand, RoleDto>
{
    public async Task<Result<RoleDto>> HandleAsync(CreateRoleCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var request = command.Request;
        var permissions = request.Permissions?.Distinct().ToList() ?? [];

        var invalid = await administration.ValidateGrantAsync(actor.Value, permissions, request.GrantsAllBranches, cancellationToken);
        if (invalid is not null)
        {
            return invalid;
        }

        var name = request.Name.Trim();
        var normalized = name.ToUpperInvariant();
        if (await db.Roles.AnyAsync(r => r.NormalizedName == normalized, cancellationToken))
        {
            return RoleErrors.NameTaken;
        }

        var role = new AppRole
        {
            Id = Guid.CreateVersion7(),
            TenantId = actor.Value.TenantId,
            Name = name,
            NormalizedName = normalized,
            ConcurrencyStamp = Guid.NewGuid().ToString(),
            Description = request.Description?.Trim(),
            IsSystem = false,
            GrantsAllBranches = request.GrantsAllBranches
        };
        db.Roles.Add(role);
        foreach (var permission in permissions)
        {
            db.RolePermissions.Add(new RolePermission { TenantId = role.TenantId, RoleId = role.Id, PermissionCode = permission });
        }

        await db.SaveChangesAsync(cancellationToken);
        return await administration.ToDtoAsync(role, cancellationToken);
    }
}

internal sealed class UpdateRoleHandler(PlatformDbContext db, IAccessContext access, RoleAdministration administration)
    : ICommandHandler<UpdateRoleCommand, RoleDto>
{
    public async Task<Result<RoleDto>> HandleAsync(UpdateRoleCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var role = await db.Roles.FirstOrDefaultAsync(r => r.Id == command.Id, cancellationToken);
        if (role is null)
        {
            return RoleErrors.NotFound;
        }

        if (role.IsSystem && role.Name == RoleTemplates.Owner)
        {
            return RoleErrors.OwnerProtected;
        }

        var request = command.Request;
        var permissions = request.Permissions?.Distinct().ToList() ?? [];
        var current = await db.RolePermissions.Where(rp => rp.RoleId == role.Id).ToListAsync(cancellationToken);

        // No se puede modificar un rol que tenga más alcance que uno mismo (evita rebajar o reutilizar roles ajenos).
        if (!current.All(rp => actor.Value.Has(rp.PermissionCode)) || (role.GrantsAllBranches && !actor.Value.AllBranches))
        {
            return RoleErrors.NotManageable;
        }

        var invalid = await administration.ValidateGrantAsync(actor.Value, permissions, request.GrantsAllBranches, cancellationToken);
        if (invalid is not null)
        {
            return invalid;
        }

        var name = request.Name.Trim();
        if (!string.Equals(name, role.Name, StringComparison.Ordinal))
        {
            if (role.IsSystem)
            {
                return RoleErrors.SystemProtected;
            }

            var normalized = name.ToUpperInvariant();
            if (await db.Roles.AnyAsync(r => r.NormalizedName == normalized && r.Id != role.Id, cancellationToken))
            {
                return RoleErrors.NameTaken;
            }

            role.Name = name;
            role.NormalizedName = normalized;
        }

        role.Description = request.Description?.Trim();
        role.GrantsAllBranches = request.GrantsAllBranches;

        db.RolePermissions.RemoveRange(current.Where(rp => !permissions.Contains(rp.PermissionCode)));
        var existing = current.Select(rp => rp.PermissionCode).ToHashSet();
        foreach (var permission in permissions.Where(p => !existing.Contains(p)))
        {
            db.RolePermissions.Add(new RolePermission { TenantId = role.TenantId, RoleId = role.Id, PermissionCode = permission });
        }

        await db.SaveChangesAsync(cancellationToken);
        return await administration.ToDtoAsync(role, cancellationToken);
    }
}

internal sealed class DeleteRoleHandler(PlatformDbContext db, IAccessContext access) : ICommandHandler<DeleteRoleCommand, Unit>
{
    public async Task<Result<Unit>> HandleAsync(DeleteRoleCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var role = await db.Roles.FirstOrDefaultAsync(r => r.Id == command.Id, cancellationToken);
        if (role is null)
        {
            return RoleErrors.NotFound;
        }

        if (role.IsSystem)
        {
            return role.Name == RoleTemplates.Owner ? RoleErrors.OwnerProtected : RoleErrors.SystemProtected;
        }

        var permissions = await db.RolePermissions.Where(rp => rp.RoleId == role.Id).ToListAsync(cancellationToken);
        if (!permissions.All(rp => actor.Value.Has(rp.PermissionCode)) || (role.GrantsAllBranches && !actor.Value.AllBranches))
        {
            return RoleErrors.NotManageable;
        }

        if (await db.UserRoles.AnyAsync(ur => ur.RoleId == role.Id, cancellationToken))
        {
            return RoleErrors.InUse;
        }

        db.RolePermissions.RemoveRange(permissions);
        db.Roles.Remove(role);
        await db.SaveChangesAsync(cancellationToken);
        return new Unit();
    }
}
