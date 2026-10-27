using FitCore.Application.Core;
using FitCore.Platform.Domain;
using FitCore.Platform.Identity;
using FitCore.Platform.Persistence;
using FitCore.SharedKernel;
using FluentValidation;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace FitCore.Platform.Application.Tenants;

public sealed record TenantDto(
    Guid Id,
    string Slug,
    string Name,
    string Status,
    string TimeZone,
    string DefaultCurrency,
    string Locale,
    DateTimeOffset CreatedAt)
{
    public static TenantDto From(Tenant t) =>
        new(t.Id, t.Slug, t.Name, t.Status.ToString(), t.TimeZone, t.DefaultCurrency, t.Locale, t.CreatedAt);
}

public sealed record CreateTenantRequest(
    string Slug,
    string Name,
    string TimeZone,
    string Locale,
    string OwnerEmail,
    string OwnerFullName,
    string OwnerPassword);

public sealed record CreateTenantCommand(CreateTenantRequest Request) : ICommand<TenantDto>;

public sealed record ListTenantsQuery(PageRequest Page) : IQuery<PagedResult<TenantDto>>;

public sealed record SetTenantStatusCommand(Guid TenantId, bool Active) : ICommand<TenantDto>;

public static class SupportedValues
{
    public static readonly IReadOnlyList<string> Locales = ["es-EC", "es", "en"];

    public static bool IsValidTimeZone(string id) => TimeZoneInfo.TryFindSystemTimeZoneById(id, out _);
}

public sealed class CreateTenantRequestValidator : AbstractValidator<CreateTenantRequest>
{
    public CreateTenantRequestValidator()
    {
        RuleFor(x => x.Slug)
            .NotEmpty()
            .Must(s => Tenant.IsValidSlug(s.Trim().ToLowerInvariant())).WithMessage("Use 3 a 40 caracteres: minúsculas, números y guiones.")
            .Must(s => !string.Equals(s.Trim(), Tenant.PlatformSlug, StringComparison.OrdinalIgnoreCase)).WithMessage("Ese identificador está reservado.");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.TimeZone).NotEmpty().Must(SupportedValues.IsValidTimeZone).WithMessage("Zona horaria no válida.");
        RuleFor(x => x.Locale).Must(l => SupportedValues.Locales.Contains(l)).WithMessage("Idioma no soportado.");
        RuleFor(x => x.OwnerEmail).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(x => x.OwnerFullName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.OwnerPassword).NotEmpty().MaximumLength(200);
    }
}

/// <summary>
/// Crea un gimnasio con sus roles de plantilla y su usuario propietario en una sola transacción.
/// Lo usan el alta desde la plataforma y el arranque (administrador de plataforma).
/// </summary>
public sealed class TenantProvisioningService(
    PlatformDbContext db,
    UserManager<AppUser> users,
    ITenantContext tenantContext,
    IClock clock)
{
    public async Task<Result<TenantDto>> ProvisionAsync(CreateTenantRequest request, bool isPlatformTenant, CancellationToken cancellationToken)
    {
        var slug = request.Slug.Trim().ToLowerInvariant();
        if (await db.Tenants.AnyAsync(t => t.Slug == slug, cancellationToken))
        {
            return Error.Conflict("tenants.slug_taken", "Ya existe un gimnasio con ese identificador.");
        }

        var originalTenant = tenantContext.TenantId;
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            var tenant = Tenant.Create(slug, request.Name, request.TimeZone, request.Locale, clock.UtcNow);
            db.Tenants.Add(tenant);
            await db.UseTenantAsync(tenant.Id, cancellationToken);

            var templates = isPlatformTenant ? [RoleTemplates.ForPlatform] : RoleTemplates.ForTenant;
            var roles = new Dictionary<string, AppRole>();
            foreach (var template in templates)
            {
                var role = new AppRole
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenant.Id,
                    Name = template.Name,
                    NormalizedName = template.Name.ToUpperInvariant(),
                    ConcurrencyStamp = Guid.NewGuid().ToString(),
                    Description = template.Description,
                    IsSystem = true,
                    GrantsAllBranches = template.GrantsAllBranches
                };
                roles[template.Name] = role;
                db.Roles.Add(role);

                foreach (var permission in template.Permissions)
                {
                    db.RolePermissions.Add(new RolePermission { TenantId = tenant.Id, RoleId = role.Id, PermissionCode = permission });
                }
            }

            await db.SaveChangesAsync(cancellationToken);

            var email = request.OwnerEmail.Trim();
            var owner = new AppUser
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenant.Id,
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                FullName = request.OwnerFullName.Trim(),
                IsActive = true,
                CreatedAt = clock.UtcNow
            };

            var created = await users.CreateAsync(owner, request.OwnerPassword);
            if (!created.Succeeded)
            {
                return created.ToError();
            }

            var ownerRole = roles[isPlatformTenant ? RoleTemplates.PlatformAdmin : RoleTemplates.Owner];
            db.UserRoles.Add(new AppUserRole { TenantId = tenant.Id, UserId = owner.Id, RoleId = ownerRole.Id });
            await db.SaveChangesAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return TenantDto.From(tenant);
        }
        finally
        {
            if (originalTenant is { } original)
            {
                await db.UseTenantAsync(original, cancellationToken);
            }
            else
            {
                await db.ClearTenantAsync(cancellationToken);
            }
        }
    }
}

internal sealed class CreateTenantHandler(TenantProvisioningService provisioning) : ICommandHandler<CreateTenantCommand, TenantDto>
{
    public Task<Result<TenantDto>> HandleAsync(CreateTenantCommand command, CancellationToken cancellationToken) =>
        provisioning.ProvisionAsync(command.Request, isPlatformTenant: false, cancellationToken);
}

internal sealed class ListTenantsHandler(PlatformDbContext db) : IQueryHandler<ListTenantsQuery, PagedResult<TenantDto>>
{
    public async Task<Result<PagedResult<TenantDto>>> HandleAsync(ListTenantsQuery query, CancellationToken cancellationToken)
    {
        var paged = await db.Tenants.AsNoTracking().OrderBy(t => t.CreatedAt).ToPagedAsync(query.Page, cancellationToken);
        return new PagedResult<TenantDto>([.. paged.Items.Select(TenantDto.From)], paged.Page, paged.PageSize, paged.TotalCount);
    }
}

internal sealed class SetTenantStatusHandler(PlatformDbContext db) : ICommandHandler<SetTenantStatusCommand, TenantDto>
{
    public async Task<Result<TenantDto>> HandleAsync(SetTenantStatusCommand command, CancellationToken cancellationToken)
    {
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == command.TenantId, cancellationToken);
        if (tenant is null)
        {
            return Error.NotFound("tenants.not_found", "Gimnasio no encontrado.");
        }

        if (tenant.Slug == Tenant.PlatformSlug)
        {
            return Error.Conflict("tenants.platform_protected", "El tenant de plataforma no puede suspenderse.");
        }

        if (command.Active)
        {
            tenant.Activate();
        }
        else
        {
            tenant.Suspend();
        }

        await db.SaveChangesAsync(cancellationToken);
        return TenantDto.From(tenant);
    }
}
