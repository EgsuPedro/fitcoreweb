using FitCore.Application.Core;
using FitCore.Classes.Persistence;
using FitCore.Platform.Application;
using FitCore.Platform.Security;
using FitCore.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace FitCore.Classes.Application;

internal static class AccessGuards
{
    public static async Task<Result<UserAccess>> RequireAccessAsync(this IAccessContext access, CancellationToken cancellationToken)
    {
        var current = await access.GetAsync(cancellationToken);
        return current is null ? PlatformErrors.Unauthenticated : current;
    }
}

internal static class QueryExtensions
{
    public static async Task<PagedResult<T>> ToPagedAsync<T>(this IQueryable<T> query, PageRequest page, CancellationToken cancellationToken)
    {
        var total = await query.CountAsync(cancellationToken);
        var items = await query.Skip(page.Skip).Take(page.SafePageSize).ToListAsync(cancellationToken);
        return new PagedResult<T>(items, page.SafePage, page.SafePageSize, total);
    }
}

internal static class LikePattern
{
    public static string Contains(string value) => "%" + Escape(value) + "%";

    /// <summary>Coincidencia exacta sin distinguir mayúsculas (para ILIKE sin comodines).</summary>
    public static string Exact(string value) => Escape(value);

    private static string Escape(string value) =>
        value.Trim()
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
}

internal static class BranchAccess
{
    /// <summary>¿El usuario (no el que consulta) puede trabajar en esa sucursal? Sí si la tiene asignada o su rol abarca todas.</summary>
    public static async Task<bool> UserCanWorkAtAsync(ClassesDbContext db, Guid userId, Guid branchId, CancellationToken cancellationToken) =>
        await db.UserBranchRefs.AnyAsync(ub => ub.UserId == userId && ub.BranchId == branchId, cancellationToken)
        || await db.UserRoleRefs.AnyAsync(
            ur => ur.UserId == userId && db.RoleRefs.Any(r => r.Id == ur.RoleId && r.GrantsAllBranches),
            cancellationToken);
}
