using FitCore.Application.Core;
using FitCore.Commercial.Persistence;
using FitCore.Platform.Application;
using FitCore.Platform.Security;
using FitCore.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace FitCore.Commercial.Application;

internal static class AccessGuards
{
    public static async Task<Result<UserAccess>> RequireAccessAsync(this IAccessContext access, CancellationToken cancellationToken)
    {
        var current = await access.GetAsync(cancellationToken);
        return current is null ? PlatformErrors.Unauthenticated : current;
    }

    /// <summary>
    /// Sucursal existente, activa y dentro del alcance del usuario. Fuera de su alcance responde 403 (igual que en Platform);
    /// para quien accede a todas, una sucursal inexistente o de otro gimnasio responde 404.
    /// </summary>
    public static async Task<Result<BranchRef>> ResolveBranchAsync(
        this CommercialDbContext db,
        UserAccess actor,
        Guid branchId,
        CancellationToken cancellationToken,
        bool requireActive = true)
    {
        if (!actor.CanAccessBranch(branchId))
        {
            return PlatformErrors.BranchNotAllowed;
        }

        var branch = await db.BranchRefs.AsNoTracking().FirstOrDefaultAsync(b => b.Id == branchId, cancellationToken);
        if (branch is null)
        {
            return CommonErrors.BranchNotFound;
        }

        return requireActive && !branch.IsActive ? CommonErrors.BranchInactive : branch;
    }

    /// <summary>Restringe una consulta con sucursal a las sucursales del usuario (todas, o solo las asignadas).</summary>
    public static IQueryable<T> InScope<T>(this IQueryable<T> query, UserAccess actor, System.Linq.Expressions.Expression<Func<T, Guid>> branchId)
    {
        if (actor.AllBranches)
        {
            return query;
        }

        var ids = actor.BranchIds.ToList();
        var parameter = branchId.Parameters[0];
        var contains = System.Linq.Expressions.Expression.Call(
            typeof(Enumerable),
            nameof(Enumerable.Contains),
            [typeof(Guid)],
            System.Linq.Expressions.Expression.Constant(ids),
            branchId.Body);
        return query.Where(System.Linq.Expressions.Expression.Lambda<Func<T, bool>>(contains, parameter));
    }
}

internal static class CommonErrors
{
    public static readonly Error BranchNotFound = Error.NotFound("branches.not_found", "Sucursal no encontrada.");
    public static readonly Error BranchInactive = Error.Validation("branches.inactive", "La sucursal está inactiva.");
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
