using FitCore.Analytics.Domain;
using FitCore.Analytics.Persistence;
using FitCore.Application.Core;
using FitCore.Platform.Domain;
using FitCore.Platform.Security;
using FitCore.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace FitCore.Analytics.Application.Reports.Members;

internal static class MemberReportErrors
{
    public static readonly Error ExpirationDaysInvalid = Error.Validation("analytics.expiration_days_invalid", "Los días para próximos vencimientos pueden ser 7, 15, 30, 60 o 90.");
}

internal static class MemberStatusMath
{
    public static string EffectiveStatus(string status, DateOnly endDate, DateOnly today) =>
        status is "Cancelled" or "Frozen" ? status : endDate < today ? "Expired" : status;

    public static decimal? Percent(int part, int total) => total > 0 ? Math.Round(part * 100m / total, 1, MidpointRounding.AwayFromZero) : null;

    /// <summary>Los meses calendario que toca el período, recortados a sus extremos (el primero y el último pueden ser parciales).</summary>
    public static IReadOnlyList<(DateOnly From, DateOnly To)> MonthBuckets(ReportPeriod period)
    {
        var buckets = new List<(DateOnly, DateOnly)>();
        for (var start = period.From; start <= period.To;)
        {
            var monthEnd = new DateOnly(start.Year, start.Month, DateTime.DaysInMonth(start.Year, start.Month));
            var end = monthEnd < period.To ? monthEnd : period.To;
            buckets.Add((start, end));
            start = end.AddDays(1);
        }

        return buckets;
    }
}

// ---- Resumen ------------------------------------------------------------------------------------------------------------

internal sealed class GetMembersSummaryHandler(AnalyticsDbContext db, IAccessContext access, IClock clock)
    : IQueryHandler<MembersSummaryQuery, MembersSummaryDto>
{
    public async Task<Result<MembersSummaryDto>> HandleAsync(MembersSummaryQuery query, CancellationToken cancellationToken)
    {
        var begin = await ReportStart.BeginAsync(db, access, Permissions.ReportsMembersRead, query.Filter, clock, cancellationToken);
        if (begin.IsFailure)
        {
            return begin.Error;
        }

        var (_, scope, periods) = begin.Value;
        var branches = scope.BranchIds;
        var zone = scope.TimeZoneId;
        var today = scope.Today;
        var planId = query.PlanId;

        var activeNow = await Metrics.ActiveMemberIds(db, branches, periods.Current.To, zone, planId).CountAsync(cancellationToken);
        var activeBefore = await Metrics.ActiveMemberIds(db, branches, periods.Previous.To, zone, planId).CountAsync(cancellationToken);
        var newNow = await Metrics.NewMembersAsync(db, branches, periods.Current, cancellationToken);
        var newBefore = await Metrics.NewMembersAsync(db, branches, periods.Previous, cancellationToken);
        var inactive = await db.Members.CountAsync(m => branches.Contains(m.BranchId) && m.Status == "Inactive", cancellationToken);
        var withBalance = await Metrics.OpenBalances(db, branches, planId).Select(b => b.MemberId).Distinct().CountAsync(cancellationToken);

        var kpis = new List<KpiDto>
        {
            Kpi.Count("active_members", "Alumnos activos", "Alumnos con ficha activa y una membresía vigente el último día del período.", "Último día del período", activeNow, activeBefore),
            Kpi.Count("new_members", "Alumnos nuevos", "Fichas de alumno creadas en el período.", "Fecha de alta del alumno", newNow, newBefore),
            Kpi.SnapshotCount("inactive_members", "Alumnos inactivos", "Alumnos con la ficha marcada como inactiva (foto de hoy).", "Hoy", inactive),
            Kpi.SnapshotCount("members_with_balance", "Alumnos con saldo pendiente", "Alumnos con alguna obligación abierta que aún se debe (foto de hoy).", "Hoy", withBalance, "financial/outstanding-balances")
        };

        // Estado de las membresías hoy (el vencido se deriva de la fecha de fin, como en el resto del sistema).
        var memberships = db.Memberships.Where(m => branches.Contains(m.BranchId) && (planId == null || m.PlanId == planId));
        var states = await memberships.GroupBy(_ => 1).Select(g => new
        {
            Active = g.Count(m => m.Status == "Active" && m.EndDate >= today),
            Frozen = g.Count(m => m.Status == "Frozen"),
            Expired = g.Count(m => (m.Status == "Active" || m.Status == "PendingPayment") && m.EndDate < today),
            Cancelled = g.Count(m => m.Status == "Cancelled"),
            Pending = g.Count(m => m.Status == "PendingPayment" && m.EndDate >= today)
        }).FirstOrDefaultAsync(cancellationToken);
        List<CountRowDto> stateRows =
        [
            new("Active", "Activas", states?.Active ?? 0),
            new("Frozen", "Congeladas", states?.Frozen ?? 0),
            new("Expired", "Vencidas", states?.Expired ?? 0),
            new("Cancelled", "Canceladas", states?.Cancelled ?? 0),
            new("PendingPayment", "Pendientes de pago", states?.Pending ?? 0)
        ];

        var validToday = Metrics.ValidOn(db, branches, today, zone);
        if (planId is { } plan)
        {
            validToday = validToday.Where(v => v.PlanId == plan);
        }

        var byPlan = await validToday
            .Where(v => db.Members.Any(m => m.Id == v.MemberId && m.Status == "Active"))
            .GroupBy(v => new { v.PlanId, v.PlanName })
            .Select(g => new MembersByPlanDto(g.Key.PlanId, g.Key.PlanName, g.Select(v => v.MemberId).Distinct().Count()))
            .ToListAsync(cancellationToken);

        var branchNames = scope.AuthorizedBranches.ToDictionary(b => b.Id, b => b.Name);
        var byBranchRaw = await db.Members.Where(m => branches.Contains(m.BranchId))
            .GroupBy(m => m.BranchId)
            .Select(g => new
            {
                BranchId = g.Key,
                Active = g.Count(m => m.Status == "Active" && validToday.Any(v => v.MemberId == m.Id)),
                Inactive = g.Count(m => m.Status == "Inactive")
            }).ToListAsync(cancellationToken);
        var byBranch = byBranchRaw.OrderByDescending(b => b.Active)
            .Select(b => new MembersByBranchDto(b.BranchId, branchNames.GetValueOrDefault(b.BranchId, string.Empty), b.Active, b.Inactive)).ToList();

        return new MembersSummaryDto(
            ReportHeader.Of(scope, periods, clock.UtcNow, "Último día del período (activos), fecha de alta (nuevos) y hoy (estados, distribución y saldos)"),
            kpis,
            stateRows,
            byPlan.OrderByDescending(p => p.Members).ThenBy(p => p.PlanName).ToList(),
            byBranch);
    }
}

// ---- Altas, inscripciones y bajas -----------------------------------------------------------------------------------------------

internal sealed class GetAcquisitionsHandler(AnalyticsDbContext db, IAccessContext access, IClock clock)
    : IQueryHandler<AcquisitionsQuery, AcquisitionsReportDto>
{
    public async Task<Result<AcquisitionsReportDto>> HandleAsync(AcquisitionsQuery query, CancellationToken cancellationToken)
    {
        var begin = await ReportStart.BeginAsync(db, access, Permissions.ReportsMembersRead, query.Filter, clock, cancellationToken);
        if (begin.IsFailure)
        {
            return begin.Error;
        }

        var (_, scope, periods) = begin.Value;
        var branches = scope.BranchIds;
        var zone = scope.TimeZoneId;
        var planId = query.PlanId;

        async Task<(int NewMembers, MembershipFlow Flow, int Losses)> TotalsAsync(ReportPeriod period)
        {
            var newMembers = await Metrics.NewMembersAsync(db, branches, period, cancellationToken);
            var flow = await Metrics.MembershipFlowAsync(db, branches, period, cancellationToken, planId);
            var (cohort, retained) = await Metrics.RetentionAsync(db, branches, period.From.AddDays(-1), period.To, zone, planId, cancellationToken);
            return (newMembers, flow, cohort - retained);
        }

        var now = await TotalsAsync(periods.Current);
        var before = await TotalsAsync(periods.Previous);
        int Gains(MembershipFlow f) => f.NewEnrollments + f.Reincorporations;

        var summary = new List<KpiDto>
        {
            Kpi.Count("new_members", "Alumnos nuevos", "Fichas de alumno creadas en el período.", "Fecha de alta del alumno", now.NewMembers, before.NewMembers),
            Kpi.Count("new_enrollments", "Nuevas inscripciones", "Membresías vendidas al primer contrato de un alumno.", "Fecha de venta de la membresía", now.Flow.NewEnrollments, before.Flow.NewEnrollments),
            Kpi.Count("renewals", "Renovaciones", "Membresías vendidas como renovación de otra.", "Fecha de venta de la membresía", now.Flow.Renewals, before.Flow.Renewals),
            Kpi.Count("reincorporations", "Reincorporaciones", "Membresías vendidas a alumnos que ya tuvieron otras, sin ser renovación.", "Fecha de venta de la membresía", now.Flow.Reincorporations, before.Flow.Reincorporations),
            Kpi.Count("gains", "Altas", "Inscripciones nuevas + reincorporaciones.", "Fecha de venta de la membresía", Gains(now.Flow), Gains(before.Flow)),
            Kpi.Count("losses", "Bajas", "Alumnos activos el día anterior al período que ya no tienen membresía vigente al terminarlo. Una membresía cancelada no es una baja si el alumno tiene otra vigente.", "Último día del período", now.Losses, before.Losses, "members/retention")
        };

        // Tendencia por mes calendario (el primero y el último pueden ser parciales).
        var trend = new List<TrendRowDto>();
        foreach (var (from, to) in MemberStatusMath.MonthBuckets(periods.Current))
        {
            var bucket = scope.Window(from, to);
            var members = await Metrics.NewMembersAsync(db, branches, bucket, cancellationToken);
            var flow = await Metrics.MembershipFlowAsync(db, branches, bucket, cancellationToken, planId);
            var (cohort, retained) = await Metrics.RetentionAsync(db, branches, from.AddDays(-1), to, zone, planId, cancellationToken);
            trend.Add(new TrendRowDto(from, to, members, flow.NewEnrollments, flow.Renewals, flow.Reincorporations, Gains(flow), cohort - retained));
        }

        var list = await ListAsync(query, scope, periods.Current, cancellationToken);
        return new AcquisitionsReportDto(ReportHeader.Of(scope, periods, clock.UtcNow, "Fecha de venta de la membresía; las bajas miden el último día de cada tramo"), summary, trend, list);
    }

    private async Task<PagedResult<MembershipRowDto>> ListAsync(AcquisitionsQuery query, AnalyticsScope scope, ReportPeriod period, CancellationToken ct)
    {
        var start = period.StartUtc;
        var end = period.EndUtcExclusive;
        var branches = scope.BranchIds;
        var planId = query.PlanId;
        var kind = query.Kind;
        var today = scope.Today;

        var rows = from m in db.Memberships
                   where branches.Contains(m.BranchId) && m.CreatedAt >= start && m.CreatedAt < end && (planId == null || m.PlanId == planId)
                   let hasEarlier = db.Memberships.Any(x => x.MemberId == m.MemberId && x.CreatedAt < m.CreatedAt)
                   let kindCode = m.RenewedFromId != null ? 2 : hasEarlier ? 3 : 1
                   where kind == MembershipKind.Any || kindCode == (int)kind
                   join mem in db.Members on m.MemberId equals mem.Id
                   join br in db.BranchRefs on m.BranchId equals br.Id
                   orderby m.CreatedAt descending, m.Id
                   select new { m.Id, m.CreatedAt, KindCode = kindCode, mem.Code, mem.FirstName, mem.LastName, m.BranchId, BranchName = br.Name, m.PlanName, m.StartDate, m.EndDate, m.Status, m.AgreedPrice };
        var total = await rows.CountAsync(ct);
        var page = await rows.Skip(query.Page.ReportSkip()).Take(query.Page.ReportSize()).ToListAsync(ct);
        var items = page.Select(r => new MembershipRowDto(
            r.Id, r.CreatedAt, r.KindCode switch { 2 => "Renewal", 3 => "Reincorporation", _ => "New" }, r.Code, $"{r.FirstName} {r.LastName}", r.BranchId, r.BranchName,
            r.PlanName, r.StartDate, r.EndDate, MemberStatusMath.EffectiveStatus(r.Status, r.EndDate, today), r.AgreedPrice)).ToList();
        return new PagedResult<MembershipRowDto>(items, query.Page.SafePage, query.Page.ReportSize(), total);
    }
}

// ---- Retención ---------------------------------------------------------------------------------------------------------------------

internal sealed class GetRetentionHandler(AnalyticsDbContext db, IAccessContext access, IClock clock)
    : IQueryHandler<RetentionQuery, RetentionReportDto>
{
    public async Task<Result<RetentionReportDto>> HandleAsync(RetentionQuery query, CancellationToken cancellationToken)
    {
        var begin = await ReportStart.BeginAsync(db, access, Permissions.ReportsMembersRead, query.Filter, clock, cancellationToken);
        if (begin.IsFailure)
        {
            return begin.Error;
        }

        var (_, scope, periods) = begin.Value;
        var branches = scope.BranchIds;
        var zone = scope.TimeZoneId;
        var planId = query.PlanId;
        var cohortDay = periods.Current.From.AddDays(-1);
        var endDay = periods.Current.To;

        var (cohort, retained) = await Metrics.RetentionAsync(db, branches, cohortDay, endDay, zone, planId, cancellationToken);
        var (previousCohort, previousRetained) = await Metrics.RetentionAsync(db, branches, periods.Previous.From.AddDays(-1), periods.Previous.To, zone, planId, cancellationToken);
        var percent = MemberStatusMath.Percent(retained, cohort);
        var previousPercent = MemberStatusMath.Percent(previousRetained, previousCohort);

        var cohortIds = Metrics.ActiveMemberIds(db, branches, cohortDay, zone, planId);
        var endingIds = Metrics.ActiveMemberIds(db, branches, endDay, zone);

        var branchNames = scope.AuthorizedBranches.ToDictionary(b => b.Id, b => b.Name);
        var byBranchRaw = await db.Members.Where(m => cohortIds.Contains(m.Id))
            .GroupBy(m => m.BranchId)
            .Select(g => new { BranchId = g.Key, Cohort = g.Count(), Retained = g.Count(m => endingIds.Contains(m.Id)) })
            .ToListAsync(cancellationToken);
        var byBranch = byBranchRaw.OrderByDescending(b => b.Cohort)
            .Select(b => new RetentionGroupDto(b.BranchId.ToString(), branchNames.GetValueOrDefault(b.BranchId, string.Empty), b.Cohort, b.Retained, MemberStatusMath.Percent(b.Retained, b.Cohort)))
            .ToList();

        var byPlanRaw = await Metrics.ValidOn(db, branches, cohortDay, zone)
            .Where(v => cohortIds.Contains(v.MemberId))
            .GroupBy(v => new { v.PlanId, v.PlanName })
            .Select(g => new
            {
                g.Key.PlanId,
                g.Key.PlanName,
                Cohort = g.Select(v => v.MemberId).Distinct().Count(),
                Retained = g.Where(v => endingIds.Contains(v.MemberId)).Select(v => v.MemberId).Distinct().Count()
            }).ToListAsync(cancellationToken);
        var byPlan = byPlanRaw.OrderByDescending(p => p.Cohort)
            .Select(p => new RetentionGroupDto(p.PlanId.ToString(), p.PlanName, p.Cohort, p.Retained, MemberStatusMath.Percent(p.Retained, p.Cohort)))
            .ToList();

        // Quiénes salieron de la cohorte (código y nombre, para que recepción los contacte; nunca teléfono ni correo).
        var lost = from m in db.Members
                   where cohortIds.Contains(m.Id) && !endingIds.Contains(m.Id)
                   join br in db.BranchRefs on m.BranchId equals br.Id
                   orderby m.Code
                   select new
                   {
                       m.Code,
                       m.FirstName,
                       m.LastName,
                       BranchName = br.Name,
                       LastPlan = db.Memberships.Where(x => x.MemberId == m.Id).OrderByDescending(x => x.EndDate).Select(x => x.PlanName).FirstOrDefault(),
                       LastEnd = db.Memberships.Where(x => x.MemberId == m.Id).OrderByDescending(x => x.EndDate).Select(x => (DateOnly?)x.EndDate).FirstOrDefault()
                   };
        var total = await lost.CountAsync(cancellationToken);
        var page = await lost.Skip(query.Page.ReportSkip()).Take(query.Page.ReportSize()).ToListAsync(cancellationToken);
        var lostPage = new PagedResult<LostMemberDto>(
            page.Select(r => new LostMemberDto(r.Code, $"{r.FirstName} {r.LastName}", r.BranchName, r.LastPlan, r.LastEnd)).ToList(), query.Page.SafePage, query.Page.ReportSize(), total);

        return new RetentionReportDto(
            ReportHeader.Of(scope, periods, clock.UtcNow, "Alumnos activos el último día anterior al período (cohorte) y el último día del período"),
            cohortDay,
            endDay,
            cohort,
            retained,
            cohort - retained,
            percent,
            previousPercent,
            Variation.OfRatio(percent, previousPercent),
            byBranch,
            byPlan,
            lostPage);
    }
}

// ---- Renovaciones ------------------------------------------------------------------------------------------------------------------

internal sealed class GetRenewalsHandler(AnalyticsDbContext db, IAccessContext access, IClock clock)
    : IQueryHandler<RenewalsQuery, RenewalsReportDto>
{
    public async Task<Result<RenewalsReportDto>> HandleAsync(RenewalsQuery query, CancellationToken cancellationToken)
    {
        var begin = await ReportStart.BeginAsync(db, access, Permissions.ReportsMembersRead, query.Filter, clock, cancellationToken);
        if (begin.IsFailure)
        {
            return begin.Error;
        }

        var (_, scope, periods) = begin.Value;
        var today = scope.Today;
        var now = Due(scope.BranchIds, periods.Current, today, query.PlanId);
        var before = Due(scope.BranchIds, periods.Previous, today, query.PlanId);

        var (due, renewed) = await CountsAsync(now, cancellationToken);
        var (previousDue, previousRenewed) = await CountsAsync(before, cancellationToken);
        var rate = MemberStatusMath.Percent(renewed, due);
        var previousRate = MemberStatusMath.Percent(previousRenewed, previousDue);

        var byPlanRaw = await now.GroupBy(m => new { m.PlanId, m.PlanName })
            .Select(g => new { g.Key.PlanId, g.Key.PlanName, Due = g.Count(), Renewed = g.Count(m => db.Memberships.Any(r => r.RenewedFromId == m.Id && r.Status != "Cancelled")) })
            .ToListAsync(cancellationToken);
        var branchNames = scope.AuthorizedBranches.ToDictionary(b => b.Id, b => b.Name);
        var byBranchRaw = await now.GroupBy(m => m.BranchId)
            .Select(g => new { BranchId = g.Key, Due = g.Count(), Renewed = g.Count(m => db.Memberships.Any(r => r.RenewedFromId == m.Id && r.Status != "Cancelled")) })
            .ToListAsync(cancellationToken);

        var notRenewed = from m in now
                         where !db.Memberships.Any(r => r.RenewedFromId == m.Id && r.Status != "Cancelled")
                         join mem in db.Members on m.MemberId equals mem.Id
                         join br in db.BranchRefs on m.BranchId equals br.Id
                         orderby m.EndDate descending, mem.Code
                         select new { mem.Code, mem.FirstName, mem.LastName, BranchName = br.Name, m.PlanName, m.EndDate };
        var total = await notRenewed.CountAsync(cancellationToken);
        var page = await notRenewed.Skip(query.Page.ReportSkip()).Take(query.Page.ReportSize()).ToListAsync(cancellationToken);
        var list = new PagedResult<NotRenewedDto>(
            page.Select(r => new NotRenewedDto(r.Code, $"{r.FirstName} {r.LastName}", r.BranchName, r.PlanName, r.EndDate, today.DayNumber - r.EndDate.DayNumber)).ToList(),
            query.Page.SafePage, query.Page.ReportSize(), total);

        return new RenewalsReportDto(
            ReportHeader.Of(scope, periods, clock.UtcNow, "Fecha de fin de la membresía que venció; la renovación cuenta cuando exista (a la fecha de consulta)"),
            due,
            renewed,
            due - renewed,
            rate,
            previousRate,
            Variation.OfRatio(rate, previousRate),
            byPlanRaw.OrderByDescending(p => p.Due).Select(p => new RenewalGroupDto(p.PlanId.ToString(), p.PlanName, p.Due, p.Renewed, MemberStatusMath.Percent(p.Renewed, p.Due))).ToList(),
            byBranchRaw.OrderByDescending(b => b.Due).Select(b => new RenewalGroupDto(b.BranchId.ToString(), branchNames.GetValueOrDefault(b.BranchId, string.Empty), b.Due, b.Renewed, MemberStatusMath.Percent(b.Renewed, b.Due))).ToList(),
            list);
    }

    /// <summary>La cohorte: membresías activas (no canceladas ni sin cobrar) cuyo fin cayó en el período y ya pasó.</summary>
    private IQueryable<MembershipRef> Due(IReadOnlyList<Guid> branches, ReportPeriod period, DateOnly today, Guid? planId)
    {
        var from = period.From;
        var to = period.To;
        return db.Memberships.Where(m => branches.Contains(m.BranchId) && m.Status == "Active" && m.EndDate >= from && m.EndDate <= to && m.EndDate < today
            && (planId == null || m.PlanId == planId));
    }

    private async Task<(int Due, int Renewed)> CountsAsync(IQueryable<MembershipRef> due, CancellationToken ct)
    {
        var row = await due.GroupBy(_ => 1)
            .Select(g => new { Due = g.Count(), Renewed = g.Count(m => db.Memberships.Any(r => r.RenewedFromId == m.Id && r.Status != "Cancelled")) })
            .FirstOrDefaultAsync(ct);
        return row is null ? (0, 0) : (row.Due, row.Renewed);
    }
}

// ---- Próximos vencimientos ---------------------------------------------------------------------------------------------------------------

internal sealed class GetExpirationsHandler(AnalyticsDbContext db, IAccessContext access, IClock clock)
    : IQueryHandler<ExpirationsQuery, ExpirationsReportDto>
{
    private static readonly int[] AllowedDays = [7, 15, 30, 60, 90];

    public async Task<Result<ExpirationsReportDto>> HandleAsync(ExpirationsQuery query, CancellationToken cancellationToken)
    {
        if (!AllowedDays.Contains(query.Days))
        {
            return MemberReportErrors.ExpirationDaysInvalid;
        }

        var begin = await ReportStart.BeginAsync(db, access, Permissions.ReportsMembersRead, query.Filter, clock, cancellationToken);
        if (begin.IsFailure)
        {
            return begin.Error;
        }

        var (_, scope, periods) = begin.Value;
        var branches = scope.BranchIds;
        var today = scope.Today;
        var limit = today.AddDays(query.Days);
        var planId = query.PlanId;

        var all = db.Memberships.Where(m => branches.Contains(m.BranchId) && m.Status == "Active" && m.EndDate >= today && m.EndDate <= limit && (planId == null || m.PlanId == planId));
        var d7 = today.AddDays(7);
        var d15 = today.AddDays(15);
        var d30 = today.AddDays(30);
        var d60 = today.AddDays(60);
        var counts = await all.GroupBy(_ => 1).Select(g => new
        {
            Total = g.Count(),
            Renewed = g.Count(m => db.Memberships.Any(r => r.RenewedFromId == m.Id && r.Status != "Cancelled")),
            B1 = g.Count(m => m.EndDate <= d7),
            B2 = g.Count(m => m.EndDate > d7 && m.EndDate <= d15),
            B3 = g.Count(m => m.EndDate > d15 && m.EndDate <= d30),
            B4 = g.Count(m => m.EndDate > d30 && m.EndDate <= d60),
            B5 = g.Count(m => m.EndDate > d60)
        }).FirstOrDefaultAsync(cancellationToken);

        List<CountRowDto> buckets = [new("0-7", "En 0 a 7 días", counts?.B1 ?? 0), new("8-15", "En 8 a 15 días", counts?.B2 ?? 0), new("16-30", "En 16 a 30 días", counts?.B3 ?? 0), new("31-60", "En 31 a 60 días", counts?.B4 ?? 0), new("61-90", "En 61 a 90 días", counts?.B5 ?? 0)];
        buckets = [.. buckets.Where(b => query.Days >= int.Parse(b.Key.Split('-')[0], System.Globalization.CultureInfo.InvariantCulture))];

        var listed = query.HideRenewed ? all.Where(m => !db.Memberships.Any(r => r.RenewedFromId == m.Id && r.Status != "Cancelled")) : all;
        var rows = from m in listed
                   join mem in db.Members on m.MemberId equals mem.Id
                   join br in db.BranchRefs on m.BranchId equals br.Id
                   orderby m.EndDate, mem.Code
                   select new
                   {
                       m.Id,
                       m.MemberId,
                       mem.Code,
                       mem.FirstName,
                       mem.LastName,
                       BranchName = br.Name,
                       m.PlanName,
                       m.EndDate,
                       Renewed = db.Memberships.Any(r => r.RenewedFromId == m.Id && r.Status != "Cancelled")
                   };
        var total = await rows.CountAsync(cancellationToken);
        var page = await rows.Skip(query.Page.ReportSkip()).Take(query.Page.ReportSize()).ToListAsync(cancellationToken);

        // Lo que debe cada alumno de la página (una consulta para todos).
        var memberIds = page.Select(r => r.MemberId).Distinct().ToList();
        var balances = await Metrics.OpenBalances(db, branches, null)
            .Where(b => memberIds.Contains(b.MemberId))
            .GroupBy(b => b.MemberId)
            .Select(g => new { MemberId = g.Key, Owed = g.Sum(b => b.Remaining) })
            .ToDictionaryAsync(x => x.MemberId, x => x.Owed, cancellationToken);

        var items = page.Select(r => new ExpiringRowDto(
            r.Id, r.Code, $"{r.FirstName} {r.LastName}", r.BranchName, r.PlanName, r.EndDate, r.EndDate.DayNumber - today.DayNumber, r.Renewed, balances.GetValueOrDefault(r.MemberId))).ToList();

        return new ExpirationsReportDto(
            ReportHeader.Of(scope, periods, clock.UtcNow, "Hoy y fecha de fin de la membresía (el período no aplica: es una foto de hoy)"),
            query.Days,
            counts?.Total ?? 0,
            counts?.Renewed ?? 0,
            buckets,
            new PagedResult<ExpiringRowDto>(items, query.Page.SafePage, query.Page.ReportSize(), total));
    }
}
