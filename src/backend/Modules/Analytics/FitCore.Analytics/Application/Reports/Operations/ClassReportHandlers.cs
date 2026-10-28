using FitCore.Analytics.Application.Dashboard;
using FitCore.Analytics.Domain;
using FitCore.Analytics.Persistence;
using FitCore.Application.Core;
using FitCore.Platform.Application;
using FitCore.Platform.Domain;
using FitCore.Platform.Security;
using FitCore.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace FitCore.Analytics.Application.Reports.Operations;

/// <summary>Una sesión con sus números ya calculados en la base: es la base común de todas las agregaciones de clases.</summary>
internal sealed class SessionStat
{
    public Guid Id { get; init; }
    public Guid BranchId { get; init; }
    public Guid ClassId { get; init; }
    public Guid InstructorId { get; init; }
    public string ClassName { get; init; } = string.Empty;
    public string Activity { get; init; } = string.Empty;
    public DateTimeOffset StartsAt { get; init; }
    public DateTimeOffset EndsAt { get; init; }
    public int Capacity { get; init; }
    public string Status { get; init; } = string.Empty;
    public int Confirmed { get; init; }
    public int Waitlisted { get; init; }
    public int Attended { get; init; }
    public int NoShow { get; init; }
}

internal sealed class HeatRow
{
    public int Weekday { get; init; }
    public int Hour { get; init; }
    public int Sessions { get; init; }
    public int Capacity { get; init; }
    public int Booked { get; init; }
}

/// <summary>Lo común de los reportes de clases: permiso (operación o instructor), alcance y la consulta base de sesiones.</summary>
internal static class ClassReports
{
    public const string Weekday0 = "Sunday";

    private static readonly string[] Weekdays = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];

    public static string WeekdayName(int postgresDow) => Weekdays[postgresDow];

    /// <summary>
    /// Quien tiene operación ve todas las clases de sus sucursales; quien solo tiene «instructor» (el entrenador) ve únicamente las sesiones donde es
    /// el instructor. Sin ninguno de los dos, 403.
    /// </summary>
    public static async Task<Result<(UserAccess Actor, AnalyticsScope Scope, PeriodPair Periods, bool InstructorOnly)>> BeginAsync(
        AnalyticsDbContext db, IAccessContext access, ReportFilter filter, IClock clock, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var user = actor.Value;
        if (!user.Has(Permissions.ReportsOperationsRead) && !user.Has(Permissions.ReportsInstructorRead))
        {
            return PlatformErrors.Forbidden;
        }

        var request = await AnalyticsScopes.ResolveRequestAsync(db, user, filter.BranchIds, filter.Preset, filter.From, filter.To, clock, cancellationToken);
        if (request.IsFailure)
        {
            return request.Error;
        }

        return (user, request.Value.Scope, request.Value.Periods, !user.Has(Permissions.ReportsOperationsRead));
    }

    public static IQueryable<SessionStat> Stats(AnalyticsDbContext db, IReadOnlyList<Guid> branches, ReportPeriod period, Guid? instructorUserId, string? activity, Guid? classId)
    {
        var start = period.StartUtc;
        var end = period.EndUtcExclusive;
        return from s in db.ClassSessions
               where branches.Contains(s.BranchId) && s.StartsAt >= start && s.StartsAt < end && (classId == null || s.ClassId == classId)
                     && (instructorUserId == null || db.Trainers.Any(t => t.Id == s.InstructorId && t.UserId == instructorUserId))
               join c in db.GroupClasses on s.ClassId equals c.Id
               where activity == null || c.Activity == activity
               select new SessionStat
               {
                   Id = s.Id,
                   BranchId = s.BranchId,
                   ClassId = s.ClassId,
                   InstructorId = s.InstructorId,
                   ClassName = c.Name,
                   Activity = c.Activity,
                   StartsAt = s.StartsAt,
                   EndsAt = s.EndsAt,
                   Capacity = s.Capacity,
                   Status = s.Status,
                   Confirmed = db.Reservations.Count(r => r.SessionId == s.Id && r.Status == "Confirmed"),
                   Waitlisted = db.Reservations.Count(r => r.SessionId == s.Id && (r.Status == "Waitlisted" || r.Status == "Offered")),
                   Attended = db.ClassAttendances.Count(a => a.SessionId == s.Id && a.Outcome == "Attended"),
                   NoShow = db.ClassAttendances.Count(a => a.SessionId == s.Id && a.Outcome == "NoShow")
               };
    }

    public static decimal? Percent(int part, int total) => total > 0 ? Math.Round(part * 100m / total, 1, MidpointRounding.AwayFromZero) : null;
}

internal sealed class GetClassOccupancyHandler(AnalyticsDbContext db, IAccessContext access, IClock clock)
    : IQueryHandler<ClassOccupancyQuery, ClassOccupancyReportDto>
{
    private sealed record PeriodStats(int Held, int Cancelled, int Upcoming, int Pending, int Capacity, int Booked, int Waitlisted, int AttCapacity, int AttendedOnRecorded, int Attended, int NoShow);

    public async Task<Result<ClassOccupancyReportDto>> HandleAsync(ClassOccupancyQuery query, CancellationToken cancellationToken)
    {
        var begin = await ClassReports.BeginAsync(db, access, query.Filter, clock, cancellationToken);
        if (begin.IsFailure)
        {
            return begin.Error;
        }

        var (actor, scope, periods, instructorOnly) = begin.Value;
        Guid? mine = instructorOnly ? actor.UserId : null;
        var branches = scope.BranchIds;
        var now = clock.UtcNow;

        var current = await StatsAsync(branches, periods.Current, mine, query, now, cancellationToken);
        var previous = await StatsAsync(branches, periods.Previous, mine, query, now, cancellationToken);
        var occupancy = ClassReports.Percent(current.Booked, current.Capacity);
        var attendedOccupancy = ClassReports.Percent(current.AttendedOnRecorded, current.AttCapacity);
        var attendanceRate = ClassReports.Percent(current.Attended, current.Attended + current.NoShow);

        var summary = new List<KpiDto>
        {
            Kpi.Count("sessions_held", "Clases realizadas", "Sesiones cerradas como realizadas. Las ya pasadas que nadie cerró se informan aparte como pendientes de cierre.", "Inicio de la sesión", current.Held, previous.Held),
            Kpi.Count("sessions_cancelled", "Clases canceladas", "Sesiones canceladas cuyo inicio caía en el período.", "Inicio de la sesión", current.Cancelled, previous.Cancelled, "classes/cancellations"),
            Kpi.Ratio("occupancy_reserved", "Ocupación reservada", "Reservas confirmadas ÷ capacidad de las sesiones no canceladas (ponderada). No cuenta lista de espera, cupos ofrecidos ni reservas canceladas.", "Inicio de la sesión", occupancy, ClassReports.Percent(previous.Booked, previous.Capacity)),
            Kpi.Ratio("occupancy_attended", "Ocupación asistida", "Asistentes ÷ capacidad de las sesiones realizadas con asistencia registrada. Sin registros no hay dato.", "Inicio de la sesión", attendedOccupancy, ClassReports.Percent(previous.AttendedOnRecorded, previous.AttCapacity)),
            Kpi.Count("reservations_confirmed", "Reservas confirmadas", "Reservas confirmadas de las sesiones no canceladas del período.", "Inicio de la sesión", current.Booked, previous.Booked),
            Kpi.Count("waitlisted", "Demanda en espera", "Personas en lista de espera o con un cupo ofrecido (querían entrar y no había lugar). No es ocupación.", "Inicio de la sesión", current.Waitlisted, previous.Waitlisted),
            Kpi.Count("no_shows", "Inasistencias", "Reservas confirmadas marcadas como «no asistió».", "Inicio de la sesión", current.NoShow, previous.NoShow),
            Kpi.Ratio("attendance_rate", "Tasa de asistencia", "Asistió ÷ (asistió + no asistió) entre lo registrado.", "Inicio de la sesión", attendanceRate, ClassReports.Percent(previous.Attended, previous.Attended + previous.NoShow))
        };

        var stats = ClassReports.Stats(db, branches, periods.Current, mine, query.Activity, query.ClassId).Where(s => s.Status != "Cancelled");
        var byClassRaw = await stats.GroupBy(s => new { s.ClassId, s.ClassName })
            .Select(g => new { g.Key.ClassId, g.Key.ClassName, Sessions = g.Count(), Capacity = g.Sum(s => s.Capacity), Confirmed = g.Sum(s => s.Confirmed), Waitlisted = g.Sum(s => s.Waitlisted) })
            .ToListAsync(cancellationToken);
        var byActivityRaw = await stats.GroupBy(s => s.Activity)
            .Select(g => new { Activity = g.Key, Sessions = g.Count(), Capacity = g.Sum(s => s.Capacity), Confirmed = g.Sum(s => s.Confirmed), Waitlisted = g.Sum(s => s.Waitlisted) })
            .ToListAsync(cancellationToken);

        // La demanda ordena las actividades: lo confirmado más lo que quedó esperando.
        var byClass = byClassRaw.OrderByDescending(c => c.Confirmed + c.Waitlisted).ThenBy(c => c.ClassName)
            .Select(c => new OccupancyGroupDto(c.ClassId.ToString(), c.ClassName, c.Sessions, c.Capacity, c.Confirmed, c.Waitlisted, ClassReports.Percent(c.Confirmed, c.Capacity))).ToList();
        var byActivity = byActivityRaw.OrderByDescending(a => a.Confirmed + a.Waitlisted).ThenBy(a => a.Activity)
            .Select(a => new OccupancyGroupDto(a.Activity, a.Activity, a.Sessions, a.Capacity, a.Confirmed, a.Waitlisted, ClassReports.Percent(a.Confirmed, a.Capacity))).ToList();

        var topSlots = await TopSlotsAsync(scope, periods.Current, mine, query, cancellationToken);

        var branchNames = scope.AuthorizedBranches.ToDictionary(b => b.Id, b => b.Name);
        var allStats = ClassReports.Stats(db, branches, periods.Current, mine, query.Activity, query.ClassId);
        var byBranchRaw = await allStats.GroupBy(s => s.BranchId).Select(g => new
        {
            BranchId = g.Key,
            Held = g.Count(s => s.Status == "Completed"),
            Cancelled = g.Count(s => s.Status == "Cancelled"),
            Capacity = g.Sum(s => s.Status != "Cancelled" && s.Capacity > 0 ? s.Capacity : 0),
            Confirmed = g.Sum(s => s.Status != "Cancelled" && s.Capacity > 0 ? s.Confirmed : 0),
            NoShows = g.Sum(s => s.NoShow)
        }).ToListAsync(cancellationToken);
        // Todas las sucursales del alcance, también las sin clases (cero visible).
        var byBranch = branches.Select(id =>
        {
            var row = byBranchRaw.FirstOrDefault(r => r.BranchId == id);
            return new ClassBranchPerformanceDto(id, branchNames.GetValueOrDefault(id, string.Empty), row?.Held ?? 0, row?.Cancelled ?? 0, row?.Capacity ?? 0, row?.Confirmed ?? 0,
                row is null ? null : ClassReports.Percent(row.Confirmed, row.Capacity), row?.NoShows ?? 0);
        }).OrderByDescending(b => b.Held + b.Cancelled).ThenBy(b => b.BranchName).ToList();

        var list = await SessionListAsync(allStats, branchNames, query.Page, now, cancellationToken);
        return new ClassOccupancyReportDto(
            ReportHeader.Of(scope, periods, clock.UtcNow, "Inicio de la sesión, en la hora local"),
            instructorOnly,
            summary,
            new SessionBreakdownDto(current.Held, current.Cancelled, current.Upcoming, current.Pending),
            byClass,
            byActivity,
            topSlots,
            byBranch,
            list);
    }

    private async Task<PeriodStats> StatsAsync(IReadOnlyList<Guid> branches, ReportPeriod period, Guid? mine, ClassOccupancyQuery query, DateTimeOffset now, CancellationToken ct)
    {
        var row = await ClassReports.Stats(db, branches, period, mine, query.Activity, query.ClassId)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Held = g.Count(s => s.Status == "Completed"),
                Cancelled = g.Count(s => s.Status == "Cancelled"),
                Upcoming = g.Count(s => s.Status == "Scheduled" && s.EndsAt >= now),
                Pending = g.Count(s => s.Status == "Scheduled" && s.EndsAt < now),
                Capacity = g.Sum(s => s.Status != "Cancelled" && s.Capacity > 0 ? s.Capacity : 0),
                Booked = g.Sum(s => s.Status != "Cancelled" && s.Capacity > 0 ? s.Confirmed : 0),
                Waitlisted = g.Sum(s => s.Status != "Cancelled" ? s.Waitlisted : 0),
                AttCapacity = g.Sum(s => s.Status == "Completed" && s.Capacity > 0 && s.Attended + s.NoShow > 0 ? s.Capacity : 0),
                AttendedOnRecorded = g.Sum(s => s.Status == "Completed" && s.Capacity > 0 && s.Attended + s.NoShow > 0 ? s.Attended : 0),
                Attended = g.Sum(s => s.Status == "Completed" ? s.Attended : 0),
                NoShow = g.Sum(s => s.Status == "Completed" ? s.NoShow : 0)
            })
            .FirstOrDefaultAsync(ct);
        return row is null
            ? new PeriodStats(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)
            : new PeriodStats(row.Held, row.Cancelled, row.Upcoming, row.Pending, row.Capacity, row.Booked, row.Waitlisted, row.AttCapacity, row.AttendedOnRecorded, row.Attended, row.NoShow);
    }

    /// <summary>Los horarios (día de la semana y hora local) con mayor ocupación; la hora sale de PostgreSQL con la zona del gimnasio.</summary>
    private async Task<List<OccupancySlotDto>> TopSlotsAsync(AnalyticsScope scope, ReportPeriod period, Guid? mine, ClassOccupancyQuery query, CancellationToken ct)
    {
        var tenant = scope.Actor.TenantId;
        var zone = scope.TimeZoneId;
        Guid[] ids = [.. scope.BranchIds];
        var start = period.StartUtc;
        var end = period.EndUtcExclusive;
        var onlyMine = mine is not null;
        var userId = mine ?? Guid.Empty;
        var activity = query.Activity ?? string.Empty;
        var classId = query.ClassId ?? Guid.Empty;

        var rows = await db.Database.SqlQuery<HeatRow>($"""
            SELECT EXTRACT(DOW FROM s.starts_at AT TIME ZONE {zone})::int AS "Weekday", EXTRACT(HOUR FROM s.starts_at AT TIME ZONE {zone})::int AS "Hour",
                   COUNT(*)::int AS "Sessions", SUM(s.capacity)::int AS "Capacity",
                   COALESCE(SUM((SELECT COUNT(*) FROM classes.class_reservations r WHERE r.tenant_id = s.tenant_id AND r.session_id = s.id AND r.status = 'Confirmed')), 0)::int AS "Booked"
            FROM classes.class_sessions s
            JOIN classes.group_classes c ON c.tenant_id = s.tenant_id AND c.id = s.class_id
            WHERE s.tenant_id = {tenant} AND s.branch_id = ANY({ids}) AND s.status <> 'Cancelled' AND s.capacity > 0
              AND s.starts_at >= {start} AND s.starts_at < {end}
              AND ({activity} = '' OR c.activity = {activity}) AND ({classId} = '00000000-0000-0000-0000-000000000000'::uuid OR s.class_id = {classId})
              AND (NOT {onlyMine} OR s.instructor_id IN (SELECT t.id FROM training.trainer_profiles t WHERE t.tenant_id = s.tenant_id AND t.user_id = {userId}))
            GROUP BY 1, 2
            """).ToListAsync(ct);
        return [.. rows
            .Select(r => new OccupancySlotDto(ClassReports.WeekdayName(r.Weekday), r.Hour, r.Sessions, r.Capacity, r.Booked, ClassReports.Percent(r.Booked, r.Capacity)))
            .OrderByDescending(s => s.OccupancyPercent).ThenByDescending(s => s.Sessions).ThenBy(s => s.Weekday).ThenBy(s => s.Hour)
            .Take(10)];
    }

    private async Task<PagedResult<SessionOccupancyDto>> SessionListAsync(IQueryable<SessionStat> stats, Dictionary<Guid, string> branchNames, PageRequest page, DateTimeOffset now, CancellationToken ct)
    {
        var rows = from s in stats
                   join t in db.Trainers on s.InstructorId equals t.Id
                   join u in db.Users on t.UserId equals u.Id
                   orderby s.StartsAt descending, s.Id
                   select new { s, Instructor = u.FullName };
        var total = await rows.CountAsync(ct);
        var pageRows = await rows.Skip(page.Skip).Take(page.SafePageSize).ToListAsync(ct);
        var items = pageRows.Select(r => new SessionOccupancyDto(
            r.s.Id, r.s.StartsAt, r.s.ClassName, r.s.Activity, r.Instructor, r.s.BranchId, branchNames.GetValueOrDefault(r.s.BranchId, string.Empty),
            r.s is { Status: "Scheduled", EndsAt: var ends } && ends < now ? "PendingClosure" : r.s.Status,
            r.s.Capacity, r.s.Confirmed, r.s.Waitlisted, r.s.Attended, r.s.NoShow,
            r.s.Status != "Cancelled" ? ClassReports.Percent(r.s.Confirmed, r.s.Capacity) : null)).ToList();
        return new PagedResult<SessionOccupancyDto>(items, page.SafePage, page.SafePageSize, total);
    }
}

internal sealed class GetClassCancellationsHandler(AnalyticsDbContext db, IAccessContext access, IClock clock)
    : IQueryHandler<ClassCancellationsQuery, ClassCancellationsReportDto>
{
    private sealed record Counts(int SessionsCancelled, int Confirmed, int Cancelled, int Late, int Expired, int NoShow);

    public async Task<Result<ClassCancellationsReportDto>> HandleAsync(ClassCancellationsQuery query, CancellationToken cancellationToken)
    {
        var begin = await ClassReports.BeginAsync(db, access, query.Filter, clock, cancellationToken);
        if (begin.IsFailure)
        {
            return begin.Error;
        }

        var (actor, scope, periods, instructorOnly) = begin.Value;
        Guid? mine = instructorOnly ? actor.UserId : null;
        var branches = scope.BranchIds;
        var now = await CountsAsync(branches, periods.Current, mine, cancellationToken);
        var before = await CountsAsync(branches, periods.Previous, mine, cancellationToken);
        static decimal? Rate(Counts c) => ClassReports.Percent(c.Cancelled, c.Confirmed + c.Cancelled);

        var summary = new List<KpiDto>
        {
            Kpi.Count("sessions_cancelled", "Clases canceladas", "Sesiones canceladas por el gimnasio cuyo inicio caía en el período.", "Inicio de la sesión", now.SessionsCancelled, before.SessionsCancelled),
            Kpi.Count("reservations_cancelled", "Reservas canceladas", "Reservas canceladas por el alumno o el personal en sesiones no canceladas (no incluye las canceladas al cancelar la sesión).", "Inicio de la sesión", now.Cancelled, before.Cancelled),
            Kpi.Count("late_cancellations", "Cancelaciones fuera de plazo", "Reservas canceladas después del plazo de cancelación de la clase.", "Inicio de la sesión", now.Late, before.Late),
            Kpi.Ratio("cancellation_rate", "Tasa de cancelación de reservas", "Canceladas ÷ (confirmadas + canceladas) de las sesiones no canceladas.", "Inicio de la sesión", Rate(now), Rate(before)),
            Kpi.Count("offers_expired", "Cupos ofrecidos que vencieron", "Invitaciones de la lista de espera que no se aceptaron a tiempo.", "Inicio de la sesión", now.Expired, before.Expired),
            Kpi.Count("no_shows", "Inasistencias", "Reservas confirmadas marcadas como «no asistió».", "Inicio de la sesión", now.NoShow, before.NoShow)
        };

        var stats = ClassReports.Stats(db, branches, periods.Current, mine, null, null);
        var byClassRaw = await stats.GroupBy(s => new { s.ClassId, s.ClassName })
            .Select(g => new { g.Key.ClassName, Sessions = g.Count(), Cancelled = g.Count(s => s.Status == "Cancelled") })
            .ToListAsync(cancellationToken);
        var byClass = byClassRaw.OrderByDescending(c => c.Cancelled).ThenBy(c => c.ClassName)
            .Select(c => new CancellationByClassDto(c.ClassName, c.Sessions, c.Cancelled, ClassReports.Percent(c.Cancelled, c.Sessions))).ToList();

        var start = periods.Current.StartUtc;
        var end = periods.Current.EndUtcExclusive;
        var cancelled = from s in db.ClassSessions
                        where branches.Contains(s.BranchId) && s.Status == "Cancelled" && s.StartsAt >= start && s.StartsAt < end
                              && (mine == null || db.Trainers.Any(t => t.Id == s.InstructorId && t.UserId == mine))
                        join c in db.GroupClasses on s.ClassId equals c.Id
                        join t in db.Trainers on s.InstructorId equals t.Id
                        join u in db.Users on t.UserId equals u.Id
                        join br in db.BranchRefs on s.BranchId equals br.Id
                        orderby s.StartsAt descending, s.Id
                        select new
                        {
                            s.Id,
                            s.StartsAt,
                            s.CancelledAt,
                            ClassName = c.Name,
                            Instructor = u.FullName,
                            BranchName = br.Name,
                            s.CancelReason,
                            // Las reservas que se cancelaron junto con la sesión (mismo instante, con un margen de segundos).
                            Affected = db.Reservations.Count(r => r.SessionId == s.Id && r.Status == "Cancelled" && r.CancelledAt >= s.CancelledAt!.Value.AddSeconds(-5))
                        };
        var total = await cancelled.CountAsync(cancellationToken);
        var pageRows = await cancelled.Skip(query.Page.Skip).Take(query.Page.SafePageSize).ToListAsync(cancellationToken);
        var list = new PagedResult<CancelledSessionDto>(
            pageRows.Select(r => new CancelledSessionDto(r.Id, r.StartsAt, r.CancelledAt, r.ClassName, r.Instructor, r.BranchName, r.CancelReason, r.Affected)).ToList(),
            query.Page.SafePage, query.Page.SafePageSize, total);

        return new ClassCancellationsReportDto(ReportHeader.Of(scope, periods, clock.UtcNow, "Inicio de la sesión, en la hora local"), instructorOnly, summary, byClass, list);
    }

    private async Task<Counts> CountsAsync(IReadOnlyList<Guid> branches, ReportPeriod period, Guid? mine, CancellationToken ct)
    {
        var sessions = ClassReports.Stats(db, branches, period, mine, null, null);
        var cancelledSessions = await sessions.CountAsync(s => s.Status == "Cancelled", ct);
        var reservations = await (from r in db.Reservations
                                  join s in sessions on r.SessionId equals s.Id
                                  where s.Status != "Cancelled"
                                  group r by 1 into g
                                  select new
                                  {
                                      Confirmed = g.Count(r => r.Status == "Confirmed"),
                                      Cancelled = g.Count(r => r.Status == "Cancelled"),
                                      Late = g.Count(r => r.Status == "Cancelled" && r.LateCancellation),
                                      Expired = g.Count(r => r.Status == "Expired")
                                  }).FirstOrDefaultAsync(ct);
        var noShow = await sessions.SumAsync(s => s.Status == "Completed" ? s.NoShow : 0, ct);
        return new Counts(cancelledSessions, reservations?.Confirmed ?? 0, reservations?.Cancelled ?? 0, reservations?.Late ?? 0, reservations?.Expired ?? 0, noShow);
    }
}
