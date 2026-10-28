using FitCore.Analytics.Domain;
using FitCore.Analytics.Persistence;
using FitCore.Application.Core;
using FitCore.Platform.Domain;
using FitCore.Platform.Security;
using FitCore.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace FitCore.Analytics.Application.Reports.Operations;

internal sealed class BucketRow
{
    public DateOnly Start { get; init; }
    public int Visits { get; init; }
    public int Visitors { get; init; }
}

internal sealed class SlotRow
{
    public int Slot { get; init; }
    public int Visits { get; init; }
}

internal sealed class LabelRow
{
    public string Label { get; init; } = string.Empty;
    public int Value { get; init; }
}

internal sealed class VisitorRow
{
    public Guid MemberId { get; init; }
    public int Visits { get; init; }
    public int Days { get; init; }
    public DateTimeOffset LastVisit { get; init; }
}

internal sealed class GetAttendanceHandler(AnalyticsDbContext db, IAccessContext access, IClock clock)
    : IQueryHandler<AttendanceQuery, AttendanceReportDto>
{
    private static readonly int[] MondayFirst = [1, 2, 3, 4, 5, 6, 0];

    private static readonly string[] FrequencyLabels = ["1", "2-3", "4-7", "8+"];

    private static readonly string[] Weekdays = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];

    public async Task<Result<AttendanceReportDto>> HandleAsync(AttendanceQuery query, CancellationToken cancellationToken)
    {
        var begin = await ReportStart.BeginAsync(db, access, Permissions.ReportsOperationsRead, query.Filter, clock, cancellationToken);
        if (begin.IsFailure)
        {
            return begin.Error;
        }

        var (_, scope, periods) = begin.Value;
        var period = periods.Current;
        var tenant = scope.Actor.TenantId;
        var zone = scope.TimeZoneId;
        Guid[] ids = [.. scope.BranchIds];
        var start = period.StartUtc;
        var end = period.EndUtcExclusive;

        var grouping = query.Grouping != AttendanceGrouping.Auto ? query.Grouping : period.Days <= 31 ? AttendanceGrouping.Day : period.Days <= 120 ? AttendanceGrouping.Week : AttendanceGrouping.Month;
        var unit = grouping switch { AttendanceGrouping.Day => "day", AttendanceGrouping.Week => "week", _ => "month" };

        // Indicadores: visitas y asistentes distintos, del período y del comparable.
        var now = await TotalsAsync(scope.BranchIds, period, cancellationToken);
        var before = await TotalsAsync(scope.BranchIds, periods.Previous, cancellationToken);
        static decimal? Frequency((int Visits, int Visitors) t) => t.Visitors == 0 ? null : Math.Round((decimal)t.Visits / t.Visitors, 2, MidpointRounding.AwayFromZero);

        var summary = new List<KpiDto>
        {
            Kpi.Count("visits", "Visitas", "Entradas al gimnasio registradas en el período.", "Fecha y hora de entrada", now.Visits, before.Visits),
            Kpi.Count("unique_visitors", "Alumnos que asistieron", "Alumnos distintos con al menos una entrada en el período.", "Fecha y hora de entrada", now.Visitors, before.Visitors),
            Kpi.Average("frequency", "Frecuencia de asistencia", "Visitas ÷ alumnos distintos que asistieron (promedio de visitas por asistente).", "Fecha y hora de entrada", Frequency(now), Frequency(before))
        };

        // Tramos con la hora local: el día/semana/mes se calcula en PostgreSQL con la zona del gimnasio o de la sucursal.
        var buckets = await db.Database.SqlQuery<BucketRow>($"""
            SELECT date_trunc({unit}, check_in_at AT TIME ZONE {zone})::date AS "Start", COUNT(*)::int AS "Visits", COUNT(DISTINCT member_id)::int AS "Visitors"
            FROM access.attendance
            WHERE tenant_id = {tenant} AND branch_id = ANY({ids}) AND check_in_at >= {start} AND check_in_at < {end}
            GROUP BY 1
            """).ToListAsync(cancellationToken);
        var series = FillBuckets(buckets, period, grouping);

        var weekdayRows = await db.Database.SqlQuery<SlotRow>($"""
            SELECT EXTRACT(DOW FROM check_in_at AT TIME ZONE {zone})::int AS "Slot", COUNT(*)::int AS "Visits"
            FROM access.attendance
            WHERE tenant_id = {tenant} AND branch_id = ANY({ids}) AND check_in_at >= {start} AND check_in_at < {end}
            GROUP BY 1
            """).ToListAsync(cancellationToken);
        // De lunes a domingo (PostgreSQL numera el domingo como 0).
        var byWeekday = MondayFirst.Select(d => new AttendanceSlotDto(Weekdays[d], weekdayRows.Where(r => r.Slot == d).Sum(r => r.Visits))).ToList();

        var hourRows = await db.Database.SqlQuery<SlotRow>($"""
            SELECT EXTRACT(HOUR FROM check_in_at AT TIME ZONE {zone})::int AS "Slot", COUNT(*)::int AS "Visits"
            FROM access.attendance
            WHERE tenant_id = {tenant} AND branch_id = ANY({ids}) AND check_in_at >= {start} AND check_in_at < {end}
            GROUP BY 1
            """).ToListAsync(cancellationToken);
        var byHour = Enumerable.Range(0, 24).Select(h => new AttendanceSlotDto(h.ToString(System.Globalization.CultureInfo.InvariantCulture), hourRows.Where(r => r.Slot == h).Sum(r => r.Visits))).ToList();

        var branchRows = await db.GymEntries
            .Where(e => scope.BranchIds.Contains(e.BranchId) && e.CheckInAt >= start && e.CheckInAt < end)
            .GroupBy(e => e.BranchId)
            .Select(g => new { BranchId = g.Key, Visits = g.Count(), Visitors = g.Select(e => e.MemberId).Distinct().Count() })
            .ToListAsync(cancellationToken);
        var branchNames = scope.AuthorizedBranches.ToDictionary(b => b.Id, b => b.Name);
        // Todas las sucursales del alcance, también las sin visitas (cero visible, no ausente).
        var byBranch = scope.BranchIds.Select(id =>
        {
            var row = branchRows.FirstOrDefault(r => r.BranchId == id);
            var visits = row?.Visits ?? 0;
            return new AttendanceBranchDto(id, branchNames.GetValueOrDefault(id, string.Empty), visits, row?.Visitors ?? 0, now.Visits > 0 ? Math.Round(visits * 100m / now.Visits, 1, MidpointRounding.AwayFromZero) : null);
        }).OrderByDescending(b => b.Visits).ThenBy(b => b.BranchName).ToList();

        // Cuántos alumnos vinieron 1 vez, 2-3, 4-7 u 8 o más.
        var histogram = await db.Database.SqlQuery<LabelRow>($"""
            SELECT CASE WHEN v = 1 THEN '1' WHEN v <= 3 THEN '2-3' WHEN v <= 7 THEN '4-7' ELSE '8+' END AS "Label", COUNT(*)::int AS "Value"
            FROM (SELECT COUNT(*) AS v FROM access.attendance
                  WHERE tenant_id = {tenant} AND branch_id = ANY({ids}) AND check_in_at >= {start} AND check_in_at < {end}
                  GROUP BY member_id) t
            GROUP BY 1
            """).ToListAsync(cancellationToken);
        var frequency = FrequencyLabels.Select(l => new FrequencyBucketDto(l, histogram.Where(h => h.Label == l).Sum(h => h.Value))).ToList();

        PagedResult<MemberVisitsDto>? members = null;
        if (query.View == AttendanceView.Members)
        {
            members = await MembersAsync(query.Page, tenant, zone, ids, start, end, now.Visitors, cancellationToken);
        }

        return new AttendanceReportDto(
            ReportHeader.Of(scope, periods, clock.UtcNow, "Fecha y hora de entrada, en la hora local"),
            unit,
            summary,
            series,
            byBranch,
            byWeekday,
            byHour,
            frequency,
            members);
    }

    private async Task<(int Visits, int Visitors)> TotalsAsync(IReadOnlyList<Guid> branches, ReportPeriod period, CancellationToken ct)
    {
        var start = period.StartUtc;
        var end = period.EndUtcExclusive;
        var row = await db.GymEntries
            .Where(e => branches.Contains(e.BranchId) && e.CheckInAt >= start && e.CheckInAt < end)
            .GroupBy(_ => 1)
            .Select(g => new { Visits = g.Count(), Visitors = g.Select(e => e.MemberId).Distinct().Count() })
            .FirstOrDefaultAsync(ct);
        return row is null ? (0, 0) : (row.Visits, row.Visitors);
    }

    /// <summary>Un punto por tramo del período, también los tramos sin entradas (valen 0).</summary>
    private static List<AttendancePointDto> FillBuckets(List<BucketRow> rows, ReportPeriod period, AttendanceGrouping grouping)
    {
        var byStart = rows.ToDictionary(r => r.Start);
        var first = grouping switch
        {
            AttendanceGrouping.Week => period.From.AddDays(-(((int)period.From.DayOfWeek + 6) % 7)),
            AttendanceGrouping.Month => new DateOnly(period.From.Year, period.From.Month, 1),
            _ => period.From
        };
        var points = new List<AttendancePointDto>();
        for (var day = first; day <= period.To;)
        {
            var row = byStart.GetValueOrDefault(day);
            points.Add(new AttendancePointDto(day, row?.Visits ?? 0, row?.Visitors ?? 0));
            day = grouping switch { AttendanceGrouping.Week => day.AddDays(7), AttendanceGrouping.Month => day.AddMonths(1), _ => day.AddDays(1) };
        }

        return points;
    }

    private async Task<PagedResult<MemberVisitsDto>> MembersAsync(PageRequest page, Guid tenant, string zone, Guid[] ids, DateTimeOffset start, DateTimeOffset end, int totalMembers, CancellationToken ct)
    {
        var take = page.ReportSize();
        var skip = page.ReportSkip();
        var rows = await db.Database.SqlQuery<VisitorRow>($"""
            SELECT member_id AS member_id, COUNT(*)::int AS visits, COUNT(DISTINCT (check_in_at AT TIME ZONE {zone})::date)::int AS days, MAX(check_in_at) AS last_visit
            FROM access.attendance
            WHERE tenant_id = {tenant} AND branch_id = ANY({ids}) AND check_in_at >= {start} AND check_in_at < {end}
            GROUP BY member_id
            ORDER BY 2 DESC, 4 DESC, 1
            LIMIT {take} OFFSET {skip}
            """).ToListAsync(ct);
        var memberIds = rows.Select(r => r.MemberId).ToList();
        var people = await db.Members.Where(m => memberIds.Contains(m.Id))
            .Join(db.BranchRefs, m => m.BranchId, b => b.Id, (m, b) => new { m.Id, m.Code, m.FirstName, m.LastName, BranchName = b.Name })
            .ToDictionaryAsync(x => x.Id, ct);
        var items = rows.Select(r =>
        {
            var person = people[r.MemberId];
            return new MemberVisitsDto(person.Code, $"{person.FirstName} {person.LastName}", person.BranchName, r.Visits, r.Days, r.LastVisit);
        }).ToList();
        return new PagedResult<MemberVisitsDto>(items, page.SafePage, take, totalMembers);
    }
}
