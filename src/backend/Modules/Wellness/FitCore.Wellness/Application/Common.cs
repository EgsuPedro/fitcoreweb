using FitCore.Application.Core;
using FitCore.Platform.Application;
using FitCore.Platform.Security;
using FitCore.SharedKernel;
using FitCore.Wellness.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FitCore.Wellness.Application;

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

internal static class LocalDates
{
    /// <summary>Fecha de hoy en una zona horaria (la de la sucursal: Galápagos tiene una hora menos que el continente).</summary>
    public static DateOnly Today(IClock clock, string? timeZone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(string.IsNullOrWhiteSpace(timeZone) ? FitCore.Platform.Domain.Tenant.DefaultTimeZone : timeZone)).DateTime);

    public static async Task<DateOnly> TodayAtBranchAsync(WellnessDbContext db, IClock clock, Guid branchId, CancellationToken cancellationToken) =>
        Today(clock, await db.BranchRefs.AsNoTracking().Where(b => b.Id == branchId).Select(b => b.TimeZone).FirstOrDefaultAsync(cancellationToken));
}

internal static class BranchAccess
{
    /// <summary>¿El usuario (no el que consulta) puede trabajar en esa sucursal? Sí si la tiene asignada o su rol abarca todas.</summary>
    public static async Task<bool> UserCanWorkAtAsync(WellnessDbContext db, Guid userId, Guid branchId, CancellationToken cancellationToken) =>
        await db.UserBranchRefs.AnyAsync(ub => ub.UserId == userId && ub.BranchId == branchId, cancellationToken)
        || await db.UserRoleRefs.AnyAsync(
            ur => ur.UserId == userId && db.RoleRefs.Any(r => r.Id == ur.RoleId && r.GrantsAllBranches),
            cancellationToken);
}
