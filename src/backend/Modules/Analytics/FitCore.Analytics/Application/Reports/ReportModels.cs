using FitCore.Analytics.Domain;
using FitCore.Application.Core;
using Microsoft.EntityFrameworkCore;

namespace FitCore.Analytics.Application.Reports;

/// <summary>Los filtros de fecha y sucursal que comparten todos los reportes.</summary>
public sealed record ReportFilter(PeriodPreset Preset, DateOnly? From, DateOnly? To, IReadOnlyList<Guid> BranchIds);

/// <summary>
/// Encabezado de cualquier reporte: qué período se aplicó (y su comparable), en qué zona horaria, en qué moneda, para qué sucursales y
/// qué fecha usa el reporte. Así un número nunca viaja sin su contexto.
/// </summary>
/// <param name="Basis">Qué fecha o instante usa el reporte (de pago, de emisión, de reversión, hoy…).</param>
public sealed record ReportHeader(
    string Currency,
    string TimeZone,
    DateOnly Today,
    DateTimeOffset GeneratedAt,
    PeriodDto Period,
    PeriodDto Comparable,
    IReadOnlyList<Guid> BranchIds,
    string Basis)
{
    internal static ReportHeader Of(AnalyticsScope scope, PeriodPair periods, DateTimeOffset now, string basis) =>
        new("USD", scope.TimeZoneId, scope.Today, now, PeriodDto.Of(periods.Current), PeriodDto.Of(periods.Previous), scope.BranchIds, basis);
}

internal static class Paging
{
    public static async Task<PagedResult<T>> ToPagedAsync<T>(this IQueryable<T> query, PageRequest page, CancellationToken cancellationToken)
    {
        var total = await query.CountAsync(cancellationToken);
        var items = await query.Skip(page.Skip).Take(page.SafePageSize).ToListAsync(cancellationToken);
        return new PagedResult<T>(items, page.SafePage, page.SafePageSize, total);
    }
}
