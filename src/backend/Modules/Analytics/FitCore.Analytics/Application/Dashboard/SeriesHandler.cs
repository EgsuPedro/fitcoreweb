using FitCore.Analytics.Domain;
using FitCore.Analytics.Persistence;
using FitCore.Application.Core;
using FitCore.Platform.Domain;
using FitCore.Platform.Security;
using FitCore.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace FitCore.Analytics.Application.Dashboard;

/// <param name="Starts">Inicio (fecha local) de cada tramo del período; el último puede ser más corto.</param>
/// <param name="Values">Un valor por tramo, en el mismo orden que <paramref name="Starts"/>. Un tramo sin actividad vale 0 (no se omite).</param>
public sealed record SeriesPeriodDto(IReadOnlyList<DateOnly> Starts, IReadOnlyList<decimal> Values);

public sealed record SeriesDto(string Key, string Name, KpiUnit Unit, string? Currency, string Basis, SeriesPeriodDto Current, SeriesPeriodDto Previous);

/// <summary>Evolución de los indicadores principales: el período y su comparable, partidos en tramos de <paramref name="BucketDays"/> días.</summary>
public sealed record DashboardSeriesDto(
    string TimeZone,
    int BucketDays,
    PeriodDto Period,
    PeriodDto Comparable,
    IReadOnlyList<Guid> BranchIds,
    IReadOnlyList<SeriesDto> Series);

public sealed record GetDashboardSeriesQuery(PeriodPreset Preset, DateOnly? From, DateOnly? To, IReadOnlyList<Guid> BranchIds) : IQuery<DashboardSeriesDto>;

internal sealed class DayValue
{
    public DateOnly Day { get; init; }
    public decimal Value { get; init; }
}

internal sealed class GetDashboardSeriesHandler(AnalyticsDbContext db, IAccessContext access, IClock clock)
    : IQueryHandler<GetDashboardSeriesQuery, DashboardSeriesDto>
{
    public async Task<Result<DashboardSeriesDto>> HandleAsync(GetDashboardSeriesQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequirePermissionAsync(Permissions.AnalyticsDashboardRead, cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var request = await AnalyticsScopes.ResolveRequestAsync(db, actor.Value, query.BranchIds, query.Preset, query.From, query.To, clock, cancellationToken);
        if (request.IsFailure)
        {
            return request.Error;
        }

        var (scope, periods) = request.Value;
        var user = actor.Value;
        var bucketDays = periods.Current.Days <= 31 ? 1 : periods.Current.Days <= 120 ? 7 : 30;
        var series = new List<SeriesDto>();

        if (user.Has(Permissions.ReportsFinancialRead))
        {
            series.Add(await BuildAsync("collected", "Total cobrado", KpiUnit.Currency, "USD", "Fecha de pago", scope, periods, bucketDays, CollectedAsync, cancellationToken));
        }

        if (user.Has(Permissions.ReportsMembersRead))
        {
            series.Add(await BuildAsync("memberships_sold", "Membresías vendidas", KpiUnit.Count, null, "Fecha de venta de la membresía", scope, periods, bucketDays, MembershipsSoldAsync, cancellationToken));
        }

        if (user.Has(Permissions.ReportsOperationsRead))
        {
            series.Add(await BuildAsync("gym_visits", "Asistencia al gimnasio", KpiUnit.Count, null, "Fecha y hora de entrada", scope, periods, bucketDays, GymVisitsAsync, cancellationToken));
            series.Add(await BuildAsync("reservations_confirmed", "Reservas confirmadas", KpiUnit.Count, null, "Inicio de la sesión", scope, periods, bucketDays, ReservationsAsync, cancellationToken));
        }

        return new DashboardSeriesDto(scope.TimeZoneId, bucketDays, PeriodDto.Of(periods.Current), PeriodDto.Of(periods.Previous), scope.BranchIds, series);
    }

    private delegate Task<List<DayValue>> DailySource(AnalyticsScope scope, ReportPeriod period, CancellationToken ct);

    private static async Task<SeriesDto> BuildAsync(
        string key, string name, KpiUnit unit, string? currency, string basis, AnalyticsScope scope, PeriodPair periods, int bucketDays, DailySource source, CancellationToken ct)
    {
        // Las consultas de un mismo contexto no pueden ir en paralelo: una tras otra.
        var current = Bucket(await source(scope, periods.Current, ct), periods.Current, bucketDays);
        var previous = Bucket(await source(scope, periods.Previous, ct), periods.Previous, bucketDays);
        return new SeriesDto(key, name, unit, currency, basis, current, previous);
    }

    /// <summary>Reparte los totales diarios en tramos contiguos desde el primer día; los días sin filas valen 0.</summary>
    private static SeriesPeriodDto Bucket(List<DayValue> days, ReportPeriod period, int bucketDays)
    {
        var byDay = days.ToDictionary(d => d.Day, d => d.Value);
        var starts = new List<DateOnly>();
        var values = new List<decimal>();
        for (var start = period.From; start <= period.To; start = start.AddDays(bucketDays))
        {
            var sum = 0m;
            for (var day = start; day <= period.To && day < start.AddDays(bucketDays); day = day.AddDays(1))
            {
                sum += byDay.GetValueOrDefault(day);
            }

            starts.Add(start);
            values.Add(sum);
        }

        return new SeriesPeriodDto(starts, values);
    }

    // Totales por día local: la zona horaria se aplica en PostgreSQL (AT TIME ZONE). Las consultas crudas no pasan por el filtro de EF,
    // así que llevan el tenant explícito además de la Row-Level Security de la conexión.
    private Task<List<DayValue>> CollectedAsync(AnalyticsScope scope, ReportPeriod period, CancellationToken ct)
    {
        var (tenant, zone, ids, start, end) = Args(scope, period);
        return db.Database.SqlQuery<DayValue>($"""
            SELECT (paid_at AT TIME ZONE {zone})::date AS "Day", SUM(amount) AS "Value"
            FROM billing.payments
            WHERE tenant_id = {tenant} AND status = 'Confirmed' AND branch_id = ANY({ids}) AND paid_at >= {start} AND paid_at < {end}
            GROUP BY 1
            """).ToListAsync(ct);
    }

    private Task<List<DayValue>> MembershipsSoldAsync(AnalyticsScope scope, ReportPeriod period, CancellationToken ct)
    {
        var (tenant, zone, ids, start, end) = Args(scope, period);
        return db.Database.SqlQuery<DayValue>($"""
            SELECT (created_at AT TIME ZONE {zone})::date AS "Day", COUNT(*)::numeric AS "Value"
            FROM members.memberships
            WHERE tenant_id = {tenant} AND branch_id = ANY({ids}) AND created_at >= {start} AND created_at < {end}
            GROUP BY 1
            """).ToListAsync(ct);
    }

    private Task<List<DayValue>> GymVisitsAsync(AnalyticsScope scope, ReportPeriod period, CancellationToken ct)
    {
        var (tenant, zone, ids, start, end) = Args(scope, period);
        return db.Database.SqlQuery<DayValue>($"""
            SELECT (check_in_at AT TIME ZONE {zone})::date AS "Day", COUNT(*)::numeric AS "Value"
            FROM access.attendance
            WHERE tenant_id = {tenant} AND branch_id = ANY({ids}) AND check_in_at >= {start} AND check_in_at < {end}
            GROUP BY 1
            """).ToListAsync(ct);
    }

    private Task<List<DayValue>> ReservationsAsync(AnalyticsScope scope, ReportPeriod period, CancellationToken ct)
    {
        var (tenant, zone, ids, start, end) = Args(scope, period);
        return db.Database.SqlQuery<DayValue>($"""
            SELECT (s.starts_at AT TIME ZONE {zone})::date AS "Day", COUNT(*)::numeric AS "Value"
            FROM classes.class_reservations r
            JOIN classes.class_sessions s ON s.tenant_id = r.tenant_id AND s.id = r.session_id
            WHERE r.tenant_id = {tenant} AND r.status = 'Confirmed' AND s.branch_id = ANY({ids}) AND s.starts_at >= {start} AND s.starts_at < {end}
            GROUP BY 1
            """).ToListAsync(ct);
    }

    private static (Guid Tenant, string Zone, Guid[] Ids, DateTimeOffset Start, DateTimeOffset End) Args(AnalyticsScope scope, ReportPeriod period) =>
        (scope.Actor.TenantId, scope.TimeZoneId, [.. scope.BranchIds], period.StartUtc, period.EndUtcExclusive);
}
