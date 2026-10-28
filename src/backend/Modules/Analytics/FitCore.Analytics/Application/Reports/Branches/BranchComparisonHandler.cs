using FitCore.Analytics.Application.Reports.Members;
using FitCore.Analytics.Application.Reports.Operations;
using FitCore.Analytics.Domain;
using FitCore.Analytics.Persistence;
using FitCore.Application.Core;
using FitCore.Platform.Domain;
using FitCore.Platform.Security;
using FitCore.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace FitCore.Analytics.Application.Reports.Branches;

internal sealed class MonthlyRow
{
    public Guid BranchId { get; init; }
    public DateOnly Month { get; init; }
    public decimal Value { get; init; }
}

internal sealed class GetBranchComparisonHandler(AnalyticsDbContext db, IAccessContext access, IClock clock)
    : IQueryHandler<BranchComparisonQuery, BranchComparisonReportDto>
{
    private sealed record ClassNumbers(int Held, int Cancelled, int Capacity, int Booked);

    private sealed record OperationsNumbers(int Visits, ClassNumbers Classes, int ReservationsCancelled);

    public async Task<Result<BranchComparisonReportDto>> HandleAsync(BranchComparisonQuery query, CancellationToken cancellationToken)
    {
        var begin = await ReportStart.BeginAsync(db, access, Permissions.ReportsCompare, query.Filter, clock, cancellationToken);
        if (begin.IsFailure)
        {
            return begin.Error;
        }

        var (actor, scope, periods) = begin.Value;
        var branches = scope.BranchIds;
        var zone = scope.TimeZoneId;
        var financial = actor.Has(Permissions.ReportsFinancialRead);
        var members = actor.Has(Permissions.ReportsMembersRead);
        var operations = actor.Has(Permissions.ReportsOperationsRead);
        var omitted = new List<string>();
        if (!financial) { omitted.Add("financial"); }
        if (!members) { omitted.Add("members"); }
        if (!operations) { omitted.Add("operations"); }

        // Cada indicador se calcula UNA vez agrupando por sucursal (no una consulta por sucursal) y con el mismo período para todas.
        Dictionary<Guid, decimal> collectedNow = [], collectedBefore = [];
        Dictionary<Guid, (decimal Outstanding, decimal Overdue)> balances = [];
        if (financial)
        {
            collectedNow = await CollectedAsync(branches, periods.Current, cancellationToken);
            collectedBefore = await CollectedAsync(branches, periods.Previous, cancellationToken);
            balances = await BalancesAsync(branches, scope.Today, cancellationToken);
        }

        Dictionary<Guid, int> activeNow = [], activeBefore = [];
        Dictionary<Guid, (int New, int Renewals)> flowNow = [], flowBefore = [];
        if (members)
        {
            activeNow = await ActiveAsync(branches, periods.Current.To, zone, cancellationToken);
            activeBefore = await ActiveAsync(branches, periods.Previous.To, zone, cancellationToken);
            flowNow = await FlowAsync(branches, periods.Current, cancellationToken);
            flowBefore = await FlowAsync(branches, periods.Previous, cancellationToken);
        }

        Dictionary<Guid, OperationsNumbers> opsNow = [], opsBefore = [];
        if (operations)
        {
            opsNow = await OperationsAsync(branches, periods.Current, cancellationToken);
            opsBefore = await OperationsAsync(branches, periods.Previous, cancellationToken);
        }

        var totalCollected = collectedNow.Values.Sum();
        var names = scope.AuthorizedBranches.ToDictionary(b => b.Id);

        var rows = branches.Select(id =>
        {
            var info = names[id];
            return BuildRow(
                id, info.Name, info.IsActive, financial, members, operations,
                collectedNow.GetValueOrDefault(id), collectedBefore.GetValueOrDefault(id), balances.GetValueOrDefault(id),
                activeNow.GetValueOrDefault(id), activeBefore.GetValueOrDefault(id), flowNow.GetValueOrDefault(id), flowBefore.GetValueOrDefault(id),
                opsNow.GetValueOrDefault(id), opsBefore.GetValueOrDefault(id), totalCollected);
        }).OrderByDescending(r => r.Financial?.Collected ?? 0m).ThenByDescending(r => r.Members?.ActiveMembers ?? 0).ThenBy(r => r.BranchName).ToList();

        // El total de las seleccionadas se arma con las mismas cuentas: sumas de cantidades y razones calculadas sobre las sumas.
        var total = BuildRow(
            Guid.Empty, "Total de las sucursales seleccionadas", true, financial, members, operations,
            collectedNow.Values.Sum(), collectedBefore.Values.Sum(), (balances.Values.Sum(b => b.Outstanding), balances.Values.Sum(b => b.Overdue)),
            activeNow.Values.Sum(), activeBefore.Values.Sum(), (flowNow.Values.Sum(f => f.New), flowNow.Values.Sum(f => f.Renewals)), (flowBefore.Values.Sum(f => f.New), flowBefore.Values.Sum(f => f.Renewals)),
            Sum(opsNow.Values), Sum(opsBefore.Values), totalCollected);

        var months = MemberStatusMath.MonthBuckets(periods.Current);
        var evolution = await EvolutionAsync(scope, periods.Current, months, financial, members, operations, cancellationToken);

        return new BranchComparisonReportDto(
            ReportHeader.Of(scope, periods, clock.UtcNow, "Las mismas fechas de cada indicador (pago, venta de la membresía, entrada, inicio de sesión) para todas las sucursales"),
            rows,
            total,
            [.. months.Select(m => new MonthRangeDto(m.From, m.To))],
            [.. evolution.OrderBy(e => rows.FindIndex(r => r.BranchId == e.BranchId))],
            omitted);
    }

    private static OperationsNumbers Sum(IEnumerable<OperationsNumbers> all)
    {
        var list = all.ToList();
        return new OperationsNumbers(
            list.Sum(o => o.Visits),
            new ClassNumbers(list.Sum(o => o.Classes.Held), list.Sum(o => o.Classes.Cancelled), list.Sum(o => o.Classes.Capacity), list.Sum(o => o.Classes.Booked)),
            list.Sum(o => o.ReservationsCancelled));
    }

    private static BranchComparisonRowDto BuildRow(
        Guid id, string name, bool isActive, bool financial, bool members, bool operations,
        decimal collected, decimal previousCollected, (decimal Outstanding, decimal Overdue) balance,
        int active, int previousActive, (int New, int Renewals) flow, (int New, int Renewals) previousFlow,
        OperationsNumbers? ops, OperationsNumbers? previousOps, decimal collectedTotal)
    {
        FinancialComparisonDto? f = null;
        if (financial)
        {
            f = new FinancialComparisonDto(
                collected, previousCollected, Variation.Of(collected, previousCollected),
                collectedTotal > 0m ? Math.Round(collected * 100m / collectedTotal, 1, MidpointRounding.AwayFromZero) : null,
                balance.Outstanding, balance.Overdue);
        }

        MembersComparisonDto? m = null;
        if (members)
        {
            m = new MembersComparisonDto(
                active, previousActive, Variation.Of(active, previousActive),
                flow.New, previousFlow.New, Variation.Of(flow.New, previousFlow.New),
                flow.Renewals, previousFlow.Renewals, Variation.Of(flow.Renewals, previousFlow.Renewals));
        }

        OperationsComparisonDto? o = null;
        if (operations)
        {
            var now = ops ?? new OperationsNumbers(0, new ClassNumbers(0, 0, 0, 0), 0);
            var before = previousOps ?? new OperationsNumbers(0, new ClassNumbers(0, 0, 0, 0), 0);
            var occupancy = ClassReports.Percent(now.Classes.Booked, now.Classes.Capacity);
            var previousOccupancy = ClassReports.Percent(before.Classes.Booked, before.Classes.Capacity);
            o = new OperationsComparisonDto(
                now.Visits, before.Visits, Variation.Of(now.Visits, before.Visits),
                now.Classes.Held, now.Classes.Cancelled, now.Classes.Capacity, now.Classes.Booked,
                occupancy, previousOccupancy, Variation.OfRatio(occupancy, previousOccupancy),
                now.ReservationsCancelled, ClassReports.Percent(now.ReservationsCancelled, now.Classes.Booked + now.ReservationsCancelled));
        }

        var hasActivity = (f is not null && (f.Collected > 0m || f.PreviousCollected > 0m || f.Outstanding > 0m))
            || (m is not null && (m.ActiveMembers > 0 || m.PreviousActiveMembers > 0 || m.NewEnrollments > 0 || m.Renewals > 0))
            || (o is not null && (o.Visits > 0 || o.PreviousVisits > 0 || o.SessionsHeld > 0 || o.SessionsCancelled > 0 || o.Capacity > 0));
        return new BranchComparisonRowDto(id, name, isActive, hasActivity, f, m, o);
    }

    // ---- Consultas agrupadas por sucursal ------------------------------------------------------------------------------

    private Task<Dictionary<Guid, decimal>> CollectedAsync(IReadOnlyList<Guid> branches, ReportPeriod period, CancellationToken ct)
    {
        var start = period.StartUtc;
        var end = period.EndUtcExclusive;
        return db.Payments.Where(p => branches.Contains(p.BranchId) && p.Status == "Confirmed" && p.PaidAt >= start && p.PaidAt < end)
            .GroupBy(p => p.BranchId).Select(g => new { g.Key, Amount = g.Sum(p => p.Amount) })
            .ToDictionaryAsync(x => x.Key, x => x.Amount, ct);
    }

    private async Task<Dictionary<Guid, (decimal Outstanding, decimal Overdue)>> BalancesAsync(IReadOnlyList<Guid> branches, DateOnly today, CancellationToken ct)
    {
        var rows = await Metrics.OpenBalances(db, branches, null).GroupBy(b => b.BranchId)
            .Select(g => new { g.Key, Outstanding = g.Sum(b => b.Remaining), Overdue = g.Sum(b => b.DueDate < today ? b.Remaining : 0m) })
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.Key, r => (r.Outstanding, r.Overdue));
    }

    private async Task<Dictionary<Guid, int>> ActiveAsync(IReadOnlyList<Guid> branches, DateOnly day, string zone, CancellationToken ct)
    {
        var ids = Metrics.ActiveMemberIds(db, branches, day, zone);
        return await db.Members.Where(m => ids.Contains(m.Id)).GroupBy(m => m.BranchId)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
    }

    private async Task<Dictionary<Guid, (int New, int Renewals)>> FlowAsync(IReadOnlyList<Guid> branches, ReportPeriod period, CancellationToken ct)
    {
        var start = period.StartUtc;
        var end = period.EndUtcExclusive;
        var rows = await db.Memberships.Where(m => branches.Contains(m.BranchId) && m.CreatedAt >= start && m.CreatedAt < end)
            .Select(m => new { m.BranchId, Renewal = m.RenewedFromId != null, HasEarlier = db.Memberships.Any(x => x.MemberId == m.MemberId && x.CreatedAt < m.CreatedAt) })
            .GroupBy(x => new { x.BranchId, x.Renewal, x.HasEarlier })
            .Select(g => new { g.Key.BranchId, g.Key.Renewal, g.Key.HasEarlier, Count = g.Count() })
            .ToListAsync(ct);
        return rows.GroupBy(r => r.BranchId).ToDictionary(
            g => g.Key,
            g => (g.Where(r => !r.Renewal && !r.HasEarlier).Sum(r => r.Count), g.Where(r => r.Renewal).Sum(r => r.Count)));
    }

    private async Task<Dictionary<Guid, OperationsNumbers>> OperationsAsync(IReadOnlyList<Guid> branches, ReportPeriod period, CancellationToken ct)
    {
        var start = period.StartUtc;
        var end = period.EndUtcExclusive;
        var visits = await db.GymEntries.Where(e => branches.Contains(e.BranchId) && e.CheckInAt >= start && e.CheckInAt < end)
            .GroupBy(e => e.BranchId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        var stats = ClassReports.Stats(db, branches, period, null, null, null);
        var classes = await stats.GroupBy(s => s.BranchId).Select(g => new
        {
            g.Key,
            Held = g.Count(s => s.Status == "Completed"),
            Cancelled = g.Count(s => s.Status == "Cancelled"),
            Capacity = g.Sum(s => s.Status != "Cancelled" && s.Capacity > 0 ? s.Capacity : 0),
            Booked = g.Sum(s => s.Status != "Cancelled" && s.Capacity > 0 ? s.Confirmed : 0)
        }).ToDictionaryAsync(x => x.Key, ct);

        var cancelled = await (from r in db.Reservations
                               join s in stats on r.SessionId equals s.Id
                               where s.Status != "Cancelled" && r.Status == "Cancelled"
                               group r by s.BranchId into g
                               select new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        return branches.ToDictionary(
            id => id,
            id =>
            {
                var c = classes.GetValueOrDefault(id);
                return new OperationsNumbers(
                    visits.GetValueOrDefault(id),
                    c is null ? new ClassNumbers(0, 0, 0, 0) : new ClassNumbers(c.Held, c.Cancelled, c.Capacity, c.Booked),
                    cancelled.GetValueOrDefault(id));
            });
    }

    /// <summary>Evolución por mes calendario y sucursal: tres consultas en total, con el mes calculado en la hora local por PostgreSQL.</summary>
    private async Task<List<BranchEvolutionDto>> EvolutionAsync(
        AnalyticsScope scope, ReportPeriod period, IReadOnlyList<(DateOnly From, DateOnly To)> months, bool financial, bool members, bool operations, CancellationToken ct)
    {
        var tenant = scope.Actor.TenantId;
        var zone = scope.TimeZoneId;
        Guid[] ids = [.. scope.BranchIds];
        var start = period.StartUtc;
        var end = period.EndUtcExclusive;

        List<MonthlyRow> collected = [], enrollments = [], visits = [];
        if (financial)
        {
            collected = await db.Database.SqlQuery<MonthlyRow>($"""
                SELECT branch_id, date_trunc('month', paid_at AT TIME ZONE {zone})::date AS month, SUM(amount) AS value
                FROM billing.payments
                WHERE tenant_id = {tenant} AND status = 'Confirmed' AND branch_id = ANY({ids}) AND paid_at >= {start} AND paid_at < {end}
                GROUP BY 1, 2
                """).ToListAsync(ct);
        }

        if (members)
        {
            enrollments = await db.Database.SqlQuery<MonthlyRow>($"""
                SELECT m.branch_id, date_trunc('month', m.created_at AT TIME ZONE {zone})::date AS month, COUNT(*)::numeric AS value
                FROM members.memberships m
                WHERE m.tenant_id = {tenant} AND m.branch_id = ANY({ids}) AND m.created_at >= {start} AND m.created_at < {end} AND m.renewed_from_id IS NULL
                  AND NOT EXISTS (SELECT 1 FROM members.memberships x WHERE x.tenant_id = m.tenant_id AND x.member_id = m.member_id AND x.created_at < m.created_at)
                GROUP BY 1, 2
                """).ToListAsync(ct);
        }

        if (operations)
        {
            visits = await db.Database.SqlQuery<MonthlyRow>($"""
                SELECT branch_id, date_trunc('month', check_in_at AT TIME ZONE {zone})::date AS month, COUNT(*)::numeric AS value
                FROM access.attendance
                WHERE tenant_id = {tenant} AND branch_id = ANY({ids}) AND check_in_at >= {start} AND check_in_at < {end}
                GROUP BY 1, 2
                """).ToListAsync(ct);
        }

        var names = scope.AuthorizedBranches.ToDictionary(b => b.Id, b => b.Name);
        decimal Value(List<MonthlyRow> rows, Guid branch, DateOnly month) =>
            rows.Where(r => r.BranchId == branch && r.Month == new DateOnly(month.Year, month.Month, 1)).Sum(r => r.Value);

        return [.. ids.Select(id => new BranchEvolutionDto(
            id,
            names.GetValueOrDefault(id, string.Empty),
            financial ? [.. months.Select(m => Value(collected, id, m.From))] : null,
            members ? [.. months.Select(m => (int)Value(enrollments, id, m.From))] : null,
            operations ? [.. months.Select(m => (int)Value(visits, id, m.From))] : null))];
    }
}
