using FitCore.Application.Core;
using FitCore.Platform.Domain;
using FitCore.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FitCore.Platform.Security;

/// <summary>Lo que el usuario autenticado puede hacer, leído de la BD en cada solicitud (sin permisos en el JWT).</summary>
public sealed record UserAccess(
    Guid UserId,
    Guid TenantId,
    bool IsActive,
    IReadOnlySet<string> Permissions,
    IReadOnlySet<Guid> BranchIds,
    bool AllBranches)
{
    public bool Has(string permission) => Permissions.Contains(permission);

    public bool CanAccessBranch(Guid branchId) => AllBranches || BranchIds.Contains(branchId);
}

public interface IAccessContext
{
    /// <summary>Acceso del usuario actual, o null si no está autenticado, está inactivo o su gimnasio está suspendido.</summary>
    Task<UserAccess?> GetAsync(CancellationToken cancellationToken = default);
}

internal sealed class AccessContext(PlatformDbContext db, ICurrentUser currentUser) : IAccessContext
{
    private Task<UserAccess?>? _cached;

    public Task<UserAccess?> GetAsync(CancellationToken cancellationToken = default) =>
        _cached ??= LoadAsync(cancellationToken);

    private async Task<UserAccess?> LoadAsync(CancellationToken cancellationToken)
    {
        if (currentUser is not { IsAuthenticated: true, UserId: { } userId, TenantId: { } tenantId })
        {
            return null;
        }

        await db.UseTenantAsync(tenantId, cancellationToken);

        var tenantActive = await db.Tenants.AnyAsync(t => t.Id == tenantId && t.Status == TenantStatus.Active, cancellationToken);
        var userActive = await db.Users.AnyAsync(u => u.Id == userId && u.IsActive, cancellationToken);
        if (!tenantActive || !userActive)
        {
            return null;
        }

        var roles = await db.UserRoles
            .Where(ur => ur.UserId == userId)
            .Join(db.Roles, ur => ur.RoleId, r => r.Id, (_, r) => new { r.Id, r.GrantsAllBranches })
            .ToListAsync(cancellationToken);
        var roleIds = roles.Select(r => r.Id).ToList();

        var permissions = await db.RolePermissions
            .Where(rp => roleIds.Contains(rp.RoleId))
            .Select(rp => rp.PermissionCode)
            .Distinct()
            .ToListAsync(cancellationToken);

        var branchIds = await db.UserBranches
            .Where(ub => ub.UserId == userId)
            .Select(ub => ub.BranchId)
            .ToListAsync(cancellationToken);

        return new UserAccess(
            userId,
            tenantId,
            IsActive: true,
            permissions.ToHashSet(),
            branchIds.ToHashSet(),
            roles.Any(r => r.GrantsAllBranches));
    }
}
