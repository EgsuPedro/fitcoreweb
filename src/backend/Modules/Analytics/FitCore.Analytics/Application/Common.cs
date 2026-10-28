using FitCore.Platform.Application;
using FitCore.Platform.Security;
using FitCore.SharedKernel;

namespace FitCore.Analytics.Application;

internal static class AccessGuards
{
    public static async Task<Result<UserAccess>> RequireAccessAsync(this IAccessContext access, CancellationToken cancellationToken)
    {
        var current = await access.GetAsync(cancellationToken);
        return current is null ? PlatformErrors.Unauthenticated : current;
    }

    /// <summary>Acceso del usuario con el permiso exigido; sin él, 403 (la política del endpoint ya lo exige, esto lo repite dentro del caso de uso).</summary>
    public static async Task<Result<UserAccess>> RequirePermissionAsync(this IAccessContext access, string permission, CancellationToken cancellationToken)
    {
        var current = await access.RequireAccessAsync(cancellationToken);
        if (current.IsFailure)
        {
            return current;
        }

        return current.Value.Has(permission) ? current : PlatformErrors.Forbidden;
    }
}
