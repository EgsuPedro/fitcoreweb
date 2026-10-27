using FitCore.Application.Core;
using FitCore.Platform.Application.Auth;
using FitCore.Platform.Identity;
using FitCore.Platform.Persistence;
using FitCore.Platform.Security;
using FitCore.SharedKernel;
using FluentValidation;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace FitCore.Platform.Application.Users;

public sealed record UserRoleDto(Guid Id, string Name);

public sealed record UserDto(
    Guid Id,
    string Email,
    string FullName,
    bool IsActive,
    IReadOnlyList<UserRoleDto> Roles,
    IReadOnlyList<Guid> BranchIds,
    DateTimeOffset CreatedAt);

public sealed record CreateUserRequest(
    string Email,
    string FullName,
    string Password,
    IReadOnlyList<Guid>? RoleIds,
    IReadOnlyList<Guid>? BranchIds);

public sealed record SetUserAccessRequest(IReadOnlyList<Guid>? RoleIds, IReadOnlyList<Guid>? BranchIds);

public sealed record SetUserStatusRequest(bool IsActive);

public sealed record ListUsersQuery(PageRequest Page, string? Search, Guid? BranchId, bool? IsActive) : IQuery<PagedResult<UserDto>>;

public sealed record GetUserQuery(Guid Id) : IQuery<UserDto>;

public sealed record CreateUserCommand(CreateUserRequest Request) : ICommand<UserDto>;

public sealed record SetUserAccessCommand(Guid Id, SetUserAccessRequest Request) : ICommand<UserDto>;

public sealed record SetUserStatusCommand(Guid Id, SetUserStatusRequest Request) : ICommand<UserDto>;

public sealed class CreateUserRequestValidator : AbstractValidator<CreateUserRequest>
{
    public CreateUserRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(x => x.FullName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Password).NotEmpty().MaximumLength(200);
    }
}

internal static class UserErrors
{
    public static readonly Error NotFound = Error.NotFound("users.not_found", "Usuario no encontrado.");
    public static readonly Error RoleNotFound = Error.Validation("users.role_not_found", "Alguno de los roles no existe.");
    public static readonly Error BranchNotFound = Error.Validation("users.branch_not_found", "Alguna de las sucursales no existe.");
    public static readonly Error RoleNotAssignable = Error.Forbidden("users.role_not_assignable", "No puede asignar ni modificar un rol con más privilegios que los suyos.");
    public static readonly Error CannotEditSelf = Error.Forbidden("users.cannot_edit_self", "No puede modificar su propio acceso ni su estado.");
}

/// <summary>Reglas de alcance y anti-escalada de privilegios para administrar usuarios.</summary>
internal sealed class UserAdministration(PlatformDbContext db)
{
    /// <summary>Usuarios que el actor puede ver y administrar: todos los del tenant o los que comparten alguna de sus sucursales.</summary>
    public IQueryable<AppUser> VisibleUsers(UserAccess actor)
    {
        var users = db.Users.AsQueryable();
        if (actor.AllBranches)
        {
            return users;
        }

        var ids = actor.BranchIds.ToList();
        return users.Where(u => db.UserBranches.Any(ub => ub.UserId == u.Id && ids.Contains(ub.BranchId)));
    }

    /// <summary>Un rol es asignable si el actor ya posee todos sus permisos y (si abarca todas las sucursales) el actor también.</summary>
    public async Task<bool> CanManageRolesAsync(UserAccess actor, IReadOnlyCollection<Guid> roleIds, CancellationToken cancellationToken)
    {
        if (roleIds.Count == 0)
        {
            return true;
        }

        var roles = await db.Roles.AsNoTracking().Where(r => roleIds.Contains(r.Id)).ToListAsync(cancellationToken);
        if (roles.Any(r => r.GrantsAllBranches) && !actor.AllBranches)
        {
            return false;
        }

        var permissions = await db.RolePermissions.AsNoTracking()
            .Where(rp => roleIds.Contains(rp.RoleId))
            .Select(rp => rp.PermissionCode)
            .Distinct()
            .ToListAsync(cancellationToken);

        return permissions.All(actor.Has);
    }

    public async Task<UserDto> ToDtoAsync(AppUser user, CancellationToken cancellationToken)
    {
        var dtos = await ToDtosAsync([user], cancellationToken);
        return dtos[0];
    }

    public async Task<IReadOnlyList<UserDto>> ToDtosAsync(IReadOnlyList<AppUser> users, CancellationToken cancellationToken)
    {
        var ids = users.Select(u => u.Id).ToList();

        var roles = await db.UserRoles.AsNoTracking()
            .Where(ur => ids.Contains(ur.UserId))
            .Join(db.Roles, ur => ur.RoleId, r => r.Id, (ur, r) => new { ur.UserId, r.Id, r.Name })
            .ToListAsync(cancellationToken);
        var branches = await db.UserBranches.AsNoTracking()
            .Where(ub => ids.Contains(ub.UserId))
            .Select(ub => new { ub.UserId, ub.BranchId })
            .ToListAsync(cancellationToken);

        return
        [
            .. users.Select(u => new UserDto(
                u.Id,
                u.Email ?? string.Empty,
                u.FullName,
                u.IsActive,
                [.. roles.Where(r => r.UserId == u.Id).Select(r => new UserRoleDto(r.Id, r.Name!)).OrderBy(r => r.Name)],
                [.. branches.Where(b => b.UserId == u.Id).Select(b => b.BranchId)],
                u.CreatedAt))
        ];
    }

    /// <summary>Valida que los roles y sucursales existan y sean asignables por el actor.</summary>
    public async Task<Error?> ValidateAccessAsync(
        UserAccess actor,
        IReadOnlyCollection<Guid> roleIds,
        IReadOnlyCollection<Guid> branchIds,
        CancellationToken cancellationToken)
    {
        if (await db.Roles.CountAsync(r => roleIds.Contains(r.Id), cancellationToken) != roleIds.Count)
        {
            return UserErrors.RoleNotFound;
        }

        if (await db.Branches.CountAsync(b => branchIds.Contains(b.Id), cancellationToken) != branchIds.Count)
        {
            return UserErrors.BranchNotFound;
        }

        if (!actor.AllBranches && branchIds.Any(b => !actor.CanAccessBranch(b)))
        {
            return PlatformErrors.BranchNotAllowed;
        }

        return await CanManageRolesAsync(actor, roleIds, cancellationToken) ? null : UserErrors.RoleNotAssignable;
    }
}

internal sealed class ListUsersHandler(PlatformDbContext db, IAccessContext access, UserAdministration administration)
    : IQueryHandler<ListUsersQuery, PagedResult<UserDto>>
{
    public async Task<Result<PagedResult<UserDto>>> HandleAsync(ListUsersQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var users = administration.VisibleUsers(actor.Value).AsNoTracking();

        if (query.BranchId is { } branchId)
        {
            if (!actor.Value.CanAccessBranch(branchId))
            {
                return PlatformErrors.BranchNotAllowed;
            }

            users = users.Where(u => db.UserBranches.Any(ub => ub.UserId == u.Id && ub.BranchId == branchId));
        }

        if (query.IsActive is { } isActive)
        {
            users = users.Where(u => u.IsActive == isActive);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var pattern = $"%{Branches.LikePattern.Escape(query.Search.Trim())}%";
            users = users.Where(u => EF.Functions.ILike(u.FullName, pattern) || EF.Functions.ILike(u.Email!, pattern));
        }

        var paged = await users.OrderBy(u => u.FullName).ThenBy(u => u.Id).ToPagedAsync(query.Page, cancellationToken);
        var items = await administration.ToDtosAsync([.. paged.Items], cancellationToken);
        return new PagedResult<UserDto>(items, paged.Page, paged.PageSize, paged.TotalCount);
    }
}

internal sealed class GetUserHandler(IAccessContext access, UserAdministration administration) : IQueryHandler<GetUserQuery, UserDto>
{
    public async Task<Result<UserDto>> HandleAsync(GetUserQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var user = await administration.VisibleUsers(actor.Value).AsNoTracking().FirstOrDefaultAsync(u => u.Id == query.Id, cancellationToken);
        return user is null ? UserErrors.NotFound : await administration.ToDtoAsync(user, cancellationToken);
    }
}

internal sealed class CreateUserHandler(
    PlatformDbContext db,
    UserManager<AppUser> users,
    IAccessContext access,
    UserAdministration administration,
    IClock clock) : ICommandHandler<CreateUserCommand, UserDto>
{
    public async Task<Result<UserDto>> HandleAsync(CreateUserCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var request = command.Request;
        var roleIds = request.RoleIds?.Distinct().ToList() ?? [];
        var branchIds = request.BranchIds?.Distinct().ToList() ?? [];

        var invalid = await administration.ValidateAccessAsync(actor.Value, roleIds, branchIds, cancellationToken);
        if (invalid is not null)
        {
            return invalid;
        }

        var email = request.Email.Trim();
        if (await users.FindByEmailAsync(email) is not null)
        {
            return Error.Conflict("users.email_taken", "Ya existe un usuario con ese correo.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var user = new AppUser
        {
            Id = Guid.CreateVersion7(),
            TenantId = actor.Value.TenantId,
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            FullName = request.FullName.Trim(),
            IsActive = true,
            CreatedAt = clock.UtcNow
        };

        var created = await users.CreateAsync(user, request.Password);
        if (!created.Succeeded)
        {
            return created.ToError();
        }

        foreach (var roleId in roleIds)
        {
            db.UserRoles.Add(new AppUserRole { TenantId = user.TenantId, UserId = user.Id, RoleId = roleId });
        }

        foreach (var branchId in branchIds)
        {
            db.UserBranches.Add(new UserBranch { TenantId = user.TenantId, UserId = user.Id, BranchId = branchId });
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return await administration.ToDtoAsync(user, cancellationToken);
    }
}

internal sealed class SetUserAccessHandler(PlatformDbContext db, IAccessContext access, UserAdministration administration)
    : ICommandHandler<SetUserAccessCommand, UserDto>
{
    public async Task<Result<UserDto>> HandleAsync(SetUserAccessCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        if (command.Id == actor.Value.UserId)
        {
            return UserErrors.CannotEditSelf;
        }

        var user = await administration.VisibleUsers(actor.Value).FirstOrDefaultAsync(u => u.Id == command.Id, cancellationToken);
        if (user is null)
        {
            return UserErrors.NotFound;
        }

        var roleIds = command.Request.RoleIds?.Distinct().ToList() ?? [];
        var branchIds = command.Request.BranchIds?.Distinct().ToList() ?? [];

        var invalid = await administration.ValidateAccessAsync(actor.Value, roleIds, branchIds, cancellationToken);
        if (invalid is not null)
        {
            return invalid;
        }

        // No se puede modificar a alguien con más privilegios que uno mismo.
        var currentRoles = await db.UserRoles.Where(ur => ur.UserId == user.Id).ToListAsync(cancellationToken);
        if (!await administration.CanManageRolesAsync(actor.Value, [.. currentRoles.Select(r => r.RoleId)], cancellationToken))
        {
            return UserErrors.RoleNotAssignable;
        }

        db.UserRoles.RemoveRange(currentRoles.Where(ur => !roleIds.Contains(ur.RoleId)));
        var existingRoleIds = currentRoles.Select(ur => ur.RoleId).ToHashSet();
        foreach (var roleId in roleIds.Where(id => !existingRoleIds.Contains(id)))
        {
            db.UserRoles.Add(new AppUserRole { TenantId = user.TenantId, UserId = user.Id, RoleId = roleId });
        }

        // Quien no ve todas las sucursales solo puede quitar las que él mismo ve; las demás del usuario se conservan.
        var currentBranches = await db.UserBranches.Where(ub => ub.UserId == user.Id).ToListAsync(cancellationToken);
        db.UserBranches.RemoveRange(currentBranches.Where(ub => actor.Value.CanAccessBranch(ub.BranchId) && !branchIds.Contains(ub.BranchId)));
        var existingBranchIds = currentBranches.Select(ub => ub.BranchId).ToHashSet();
        foreach (var branchId in branchIds.Where(id => !existingBranchIds.Contains(id)))
        {
            db.UserBranches.Add(new UserBranch { TenantId = user.TenantId, UserId = user.Id, BranchId = branchId });
        }

        await db.SaveChangesAsync(cancellationToken);
        return await administration.ToDtoAsync(user, cancellationToken);
    }
}

internal sealed class SetUserStatusHandler(
    PlatformDbContext db,
    IAccessContext access,
    UserAdministration administration,
    SessionService sessions) : ICommandHandler<SetUserStatusCommand, UserDto>
{
    public async Task<Result<UserDto>> HandleAsync(SetUserStatusCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        if (command.Id == actor.Value.UserId)
        {
            return UserErrors.CannotEditSelf;
        }

        var user = await administration.VisibleUsers(actor.Value).FirstOrDefaultAsync(u => u.Id == command.Id, cancellationToken);
        if (user is null)
        {
            return UserErrors.NotFound;
        }

        var roleIds = await db.UserRoles.Where(ur => ur.UserId == user.Id).Select(ur => ur.RoleId).ToListAsync(cancellationToken);
        if (!await administration.CanManageRolesAsync(actor.Value, roleIds, cancellationToken))
        {
            return UserErrors.RoleNotAssignable;
        }

        user.IsActive = command.Request.IsActive;
        await db.SaveChangesAsync(cancellationToken);

        if (!user.IsActive)
        {
            await sessions.RevokeAllForUserAsync(user.Id, cancellationToken);
        }

        return await administration.ToDtoAsync(user, cancellationToken);
    }
}
