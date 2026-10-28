using FitCore.Analytics.Domain;
using FitCore.Analytics.Persistence;
using FitCore.Application.Core;
using FitCore.Platform.Application;
using FitCore.Platform.Domain;
using FitCore.Platform.Security;
using FitCore.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace FitCore.Analytics.Application;

/// <summary>
/// El alcance con el que se calcula un reporte: gimnasio, sucursales efectivas (siempre dentro de las que el usuario puede ver), la zona
/// horaria de los datos y la fecha local de hoy. Todos los reportes parten de aquí, así que todos aplican el mismo aislamiento.
/// </summary>
internal sealed record AnalyticsScope(
    UserAccess Actor,
    string TimeZoneId,
    DateOnly Today,
    IReadOnlyList<Guid> BranchIds,
    IReadOnlyList<BranchOptionDto> AuthorizedBranches)
{
    public ReportPeriod Window(DateOnly from, DateOnly to) => new(from, to, TimeZoneId);
}

internal static class AnalyticsScopes
{
    /// <summary>
    /// Sucursales pedidas → efectivas. Sin pedir ninguna: todas las que el usuario puede ver. Una sucursal fuera de su alcance (o de otro
    /// gimnasio, que ni siquiera existe para este contexto) se rechaza igual, sin revelar si existe.
    /// </summary>
    public static async Task<Result<AnalyticsScope>> ResolveAsync(
        AnalyticsDbContext db, UserAccess actor, IReadOnlyCollection<Guid> requestedBranchIds, IClock clock, CancellationToken cancellationToken)
    {
        var tenantZone = await db.TenantRefs.Where(t => t.Id == actor.TenantId).Select(t => t.TimeZone).FirstOrDefaultAsync(cancellationToken);
        var zone = string.IsNullOrWhiteSpace(tenantZone) ? Tenant.DefaultTimeZone : tenantZone;

        var all = await db.BranchRefs.OrderBy(b => b.Name).Select(b => new BranchOptionDto(b.Id, b.Code, b.Name, b.TimeZone, b.IsActive)).ToListAsync(cancellationToken);
        var authorized = all.Where(b => actor.CanAccessBranch(b.Id)).ToList();

        var effective = requestedBranchIds.Count == 0
            ? authorized.Select(b => b.Id).ToList()
            : requestedBranchIds.Distinct().ToList();
        if (effective.Any(id => authorized.All(b => b.Id != id)))
        {
            return PlatformErrors.BranchNotAllowed;
        }

        // Una sola sucursal efectiva: sus datos se leen en su propia zona horaria; varias: en la del gimnasio.
        if (effective.Count == 1 && authorized.First(b => b.Id == effective[0]).TimeZone is { Length: > 0 } branchZone)
        {
            zone = branchZone;
        }

        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(zone)).DateTime);
        return new AnalyticsScope(actor, zone, today, effective, authorized);
    }

    /// <summary>Alcance (sucursales, zona horaria, hoy) más el período pedido y su comparable: lo que todo reporte necesita para empezar.</summary>
    public static async Task<Result<(AnalyticsScope Scope, PeriodPair Periods)>> ResolveRequestAsync(
        AnalyticsDbContext db, UserAccess actor, IReadOnlyCollection<Guid> requestedBranchIds, PeriodPreset preset, DateOnly? from, DateOnly? to,
        IClock clock, CancellationToken cancellationToken)
    {
        var scope = await ResolveAsync(db, actor, requestedBranchIds, clock, cancellationToken);
        if (scope.IsFailure)
        {
            return scope.Error;
        }

        var periods = PeriodResolver.Resolve(preset, from, to, scope.Value.Today, scope.Value.TimeZoneId);
        if (periods.IsFailure)
        {
            return periods.Error;
        }

        return (scope.Value, periods.Value);
    }
}
