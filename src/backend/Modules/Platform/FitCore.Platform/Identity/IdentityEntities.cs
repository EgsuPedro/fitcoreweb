using FitCore.SharedKernel;
using Microsoft.AspNetCore.Identity;

namespace FitCore.Platform.Identity;

public sealed class AppUser : IdentityUser<Guid>, ITenantOwned, IAuditable
{
    public Guid TenantId { get; set; }
    public string FullName { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class AppRole : IdentityRole<Guid>, ITenantOwned, IAuditable
{
    public Guid TenantId { get; set; }
    public string? Description { get; set; }

    /// <summary>Rol de plantilla creado con el tenant (no se puede eliminar).</summary>
    public bool IsSystem { get; set; }

    /// <summary>Si es verdadero, quien tenga este rol accede a todas las sucursales del tenant.</summary>
    public bool GrantsAllBranches { get; set; }
}

public sealed class AppUserRole : IdentityUserRole<Guid>, ITenantOwned, IAuditable
{
    public Guid TenantId { get; set; }
}

public sealed class PermissionEntity
{
    public string Code { get; set; } = string.Empty;
    public string Module { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

public sealed class RolePermission : ITenantOwned, IAuditable
{
    public Guid TenantId { get; set; }
    public Guid RoleId { get; set; }
    public string PermissionCode { get; set; } = string.Empty;
}

/// <summary>Sucursales a las que tiene acceso un usuario.</summary>
public sealed class UserBranch : ITenantOwned, IAuditable
{
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public Guid BranchId { get; set; }
}

public sealed class RefreshToken : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }

    /// <summary>Todos los tokens rotados a partir de un mismo login comparten familia.</summary>
    public Guid FamilyId { get; set; }

    public string TokenHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? ReplacedById { get; set; }
    public string? CreatedByIp { get; set; }
    public string? UserAgent { get; set; }

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now;
}
