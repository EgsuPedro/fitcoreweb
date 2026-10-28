using FitCore.Analytics.Domain;
using FitCore.Analytics.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FitCore.Analytics.Application;

internal sealed class OpenBalance
{
    public Guid ObligationId { get; init; }
    public Guid MemberId { get; init; }
    public Guid BranchId { get; init; }
    public Guid? MembershipId { get; init; }
    public DateOnly DueDate { get; init; }
    public decimal Remaining { get; init; }
}

internal sealed record PaymentTotals(decimal Collected, int CollectedCount, decimal PendingVerification);

internal sealed record MethodTotal(string Method, decimal Amount, int Count);

internal sealed record ReversalTotals(decimal Amount, int Count);

internal sealed record OutstandingTotals(decimal Outstanding, decimal Overdue, int OpenCount, int OverdueCount);

internal sealed record MembershipFlow(int NewEnrollments, int Renewals, int Reincorporations);

internal sealed record SessionCounts(int Held, int Cancelled, int Upcoming, int PendingClosure);

internal sealed record Occupancy(int Booked, int Capacity, int Sessions)
{
    /// <summary>Ocupación ponderada (Σ ocupados ÷ Σ capacidad), nunca el promedio de porcentajes. Sin capacidad no hay dato.</summary>
    public decimal? Percent => Capacity > 0 ? Math.Round(Booked * 100m / Capacity, 1, MidpointRounding.AwayFromZero) : null;
}

/// <summary>
/// Consultas de agregación de los indicadores. Todas se ejecutan en PostgreSQL (sumas y conteos), parten de un alcance ya validado
/// (sucursales autorizadas) y leen únicamente las columnas necesarias. Las reglas (qué es cobrado, vigente, ocupación…) están
/// documentadas en el ADR 0011 y viven solo aquí.
/// </summary>
internal static class Metrics
{
    // ---- Finanzas -------------------------------------------------------------------------------------------------------

    /// <summary>Cobrado = pagos Confirmados por fecha de pago; por verificar = transferencias aún sin confirmar. Se suma el importe del pago, nunca sus asignaciones.</summary>
    public static async Task<PaymentTotals> PaymentsAsync(AnalyticsDbContext db, IReadOnlyList<Guid> branches, ReportPeriod period, CancellationToken ct)
    {
        var start = period.StartUtc;
        var end = period.EndUtcExclusive;
        var row = await db.Payments
            .Where(p => branches.Contains(p.BranchId) && p.PaidAt >= start && p.PaidAt < end)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Collected = g.Sum(p => p.Status == "Confirmed" ? p.Amount : 0m),
                CollectedCount = g.Count(p => p.Status == "Confirmed"),
                Pending = g.Sum(p => p.Status == "PendingVerification" ? p.Amount : 0m)
            })
            .FirstOrDefaultAsync(ct);
        return row is null ? new PaymentTotals(0m, 0, 0m) : new PaymentTotals(row.Collected, row.CollectedCount, row.Pending);
    }

    public static async Task<IReadOnlyList<MethodTotal>> PaymentMethodsAsync(AnalyticsDbContext db, IReadOnlyList<Guid> branches, ReportPeriod period, CancellationToken ct)
    {
        var start = period.StartUtc;
        var end = period.EndUtcExclusive;
        return await db.Payments
            .Where(p => branches.Contains(p.BranchId) && p.Status == "Confirmed" && p.PaidAt >= start && p.PaidAt < end)
            .GroupBy(p => p.Method)
            .Select(g => new MethodTotal(g.Key, g.Sum(p => p.Amount), g.Count()))
            .ToListAsync(ct);
    }

    /// <summary>Revertido = pagos Reversed por fecha de reversión (no por la de pago).</summary>
    public static async Task<ReversalTotals> ReversalsAsync(AnalyticsDbContext db, IReadOnlyList<Guid> branches, ReportPeriod period, CancellationToken ct)
    {
        var start = period.StartUtc;
        var end = period.EndUtcExclusive;
        var row = await db.Payments
            .Where(p => branches.Contains(p.BranchId) && p.Status == "Reversed" && p.ReversedAt >= start && p.ReversedAt < end)
            .GroupBy(_ => 1)
            .Select(g => new { Amount = g.Sum(p => p.Amount), Count = g.Count() })
            .FirstOrDefaultAsync(ct);
        return row is null ? new ReversalTotals(0m, 0) : new ReversalTotals(row.Amount, row.Count);
    }

    /// <summary>Obligaciones generadas = exigible de las no anuladas, por fecha de emisión.</summary>
    public static async Task<decimal> ObligationsIssuedAsync(AnalyticsDbContext db, IReadOnlyList<Guid> branches, ReportPeriod period, CancellationToken ct)
    {
        var from = period.From;
        var to = period.To;
        return await db.Obligations
            .Where(o => branches.Contains(o.BranchId) && o.Status != "Voided" && o.IssueDate >= from && o.IssueDate <= to)
            .SumAsync(o => (decimal?)o.PayableAmount, ct) ?? 0m;
    }

    /// <summary>
    /// Saldo pendiente de hoy: de las obligaciones Pendiente o Parcial, lo exigible menos lo cobrado (pagos Confirmados, la misma regla del
    /// <c>Ledger</c> de cartera). Vencido = el saldo de las que ya pasaron su fecha de vencimiento. No existe el saldo de una fecha pasada.
    /// </summary>
    public static async Task<OutstandingTotals> OutstandingAsync(AnalyticsDbContext db, IReadOnlyList<Guid> branches, DateOnly today, CancellationToken ct)
    {
        var owed = from o in db.Obligations
                   where branches.Contains(o.BranchId) && (o.Status == "Pending" || o.Status == "PartiallyPaid")
                   let paid = (from a in db.Allocations
                               join p in db.Payments on a.PaymentId equals p.Id
                               where a.ObligationId == o.Id && p.Status == "Confirmed"
                               select (decimal?)a.Amount).Sum() ?? 0m
                   let remaining = o.PayableAmount - paid
                   where remaining > 0m
                   select new { o.DueDate, Remaining = remaining };

        var row = await owed
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Total = g.Sum(x => x.Remaining),
                Count = g.Count(),
                Overdue = g.Sum(x => x.DueDate < today ? x.Remaining : 0m),
                OverdueCount = g.Count(x => x.DueDate < today)
            })
            .FirstOrDefaultAsync(ct);
        return row is null ? new OutstandingTotals(0m, 0m, 0, 0) : new OutstandingTotals(row.Total, row.Overdue, row.Count, row.OverdueCount);
    }

    /// <summary>
    /// Obligaciones Pendiente o Parcial con lo que aún se debe (exigible − pagos Confirmados), la misma regla de la cartera existente. Solo las
    /// que todavía deben algo.
    /// </summary>
    public static IQueryable<OpenBalance> OpenBalances(AnalyticsDbContext db, IReadOnlyList<Guid> branches, Guid? planId) =>
        from o in db.Obligations
        where branches.Contains(o.BranchId) && (o.Status == "Pending" || o.Status == "PartiallyPaid")
              && (planId == null || db.Memberships.Any(m => m.Id == o.MembershipId && m.PlanId == planId))
        let paid = (from a in db.Allocations
                    join p in db.Payments on a.PaymentId equals p.Id
                    where a.ObligationId == o.Id && p.Status == "Confirmed"
                    select (decimal?)a.Amount).Sum() ?? 0m
        let remaining = o.PayableAmount - paid
        where remaining > 0m
        select new OpenBalance { ObligationId = o.Id, MemberId = o.MemberId, BranchId = o.BranchId, MembershipId = o.MembershipId, DueDate = o.DueDate, Remaining = remaining };

    // ---- Alumnos y membresías -------------------------------------------------------------------------------------------

    /// <summary>
    /// Membresías vigentes en la fecha D: activadas (evento <c>Activated</c>) antes de que termine D, con inicio ≤ D ≤ fin, y no canceladas
    /// antes de que termine D. Congelada cuenta como vigente (conserva su derecho). Es histórica: se puede calcular para cualquier fecha.
    /// </summary>
    public static IQueryable<MembershipRef> ValidOn(AnalyticsDbContext db, IReadOnlyList<Guid> branches, DateOnly day, string timeZoneId)
    {
        var endOfDay = new ReportPeriod(day, day, timeZoneId).EndUtcExclusive;
        return db.Memberships.Where(m =>
            branches.Contains(m.BranchId)
            && m.StartDate <= day && m.EndDate >= day
            && (m.CancelledAt == null || m.CancelledAt >= endOfDay)
            && db.MembershipEvents.Any(e => e.MembershipId == m.Id && e.Type == "Activated" && e.OccurredAt < endOfDay));
    }

    /// <summary>Alumnos activos al cierre del día D: ficha Activa con al menos una membresía vigente ese día.</summary>
    public static Task<int> ActiveMembersAsync(AnalyticsDbContext db, IReadOnlyList<Guid> branches, DateOnly day, string timeZoneId, CancellationToken ct)
    {
        var valid = ValidOn(db, branches, day, timeZoneId);
        return db.Members
            .Where(m => branches.Contains(m.BranchId) && m.Status == "Active" && valid.Any(v => v.MemberId == m.Id))
            .CountAsync(ct);
    }

    /// <summary>Los ids de los alumnos activos al cierre del día D (misma definición que <see cref="ActiveMembersAsync"/>).</summary>
    public static IQueryable<Guid> ActiveMemberIds(AnalyticsDbContext db, IReadOnlyList<Guid> branches, DateOnly day, string timeZoneId, Guid? planId = null)
    {
        var valid = ValidOn(db, branches, day, timeZoneId);
        if (planId is { } plan)
        {
            valid = valid.Where(v => v.PlanId == plan);
        }

        return db.Members
            .Where(m => branches.Contains(m.BranchId) && m.Status == "Active" && valid.Any(v => v.MemberId == m.Id))
            .Select(m => m.Id);
    }

    /// <summary>Cohorte y retenidos: de los activos el día <paramref name="cohortDay"/>, cuántos siguen activos el día <paramref name="endDay"/>.</summary>
    public static async Task<(int Cohort, int Retained)> RetentionAsync(AnalyticsDbContext db, IReadOnlyList<Guid> branches, DateOnly cohortDay, DateOnly endDay, string timeZoneId, Guid? planId, CancellationToken ct)
    {
        var cohort = ActiveMemberIds(db, branches, cohortDay, timeZoneId, planId);
        var ending = ActiveMemberIds(db, branches, endDay, timeZoneId);
        var total = await cohort.CountAsync(ct);
        var retained = total == 0 ? 0 : await cohort.CountAsync(id => ending.Contains(id), ct);
        return (total, retained);
    }

    public static Task<int> NewMembersAsync(AnalyticsDbContext db, IReadOnlyList<Guid> branches, ReportPeriod period, CancellationToken ct)
    {
        var from = period.From;
        var to = period.To;
        return db.Members.Where(m => branches.Contains(m.BranchId) && m.EnrolledOn >= from && m.EnrolledOn <= to).CountAsync(ct);
    }

    /// <summary>
    /// Membresías creadas en el período: renovación (viene de otra), inscripción nueva (primera del alumno) o reincorporación (tiene
    /// membresías previas pero no es renovación). Se cuenta la fecha de creación (venta).
    /// </summary>
    public static async Task<MembershipFlow> MembershipFlowAsync(AnalyticsDbContext db, IReadOnlyList<Guid> branches, ReportPeriod period, CancellationToken ct, Guid? planId = null)
    {
        var start = period.StartUtc;
        var end = period.EndUtcExclusive;
        var rows = await db.Memberships
            .Where(m => branches.Contains(m.BranchId) && m.CreatedAt >= start && m.CreatedAt < end && (planId == null || m.PlanId == planId))
            .Select(m => new
            {
                Renewal = m.RenewedFromId != null,
                HasEarlier = db.Memberships.Any(x => x.MemberId == m.MemberId && x.CreatedAt < m.CreatedAt)
            })
            .GroupBy(x => new { x.Renewal, x.HasEarlier })
            .Select(g => new { g.Key.Renewal, g.Key.HasEarlier, Count = g.Count() })
            .ToListAsync(ct);

        return new MembershipFlow(
            rows.Where(r => !r.Renewal && !r.HasEarlier).Sum(r => r.Count),
            rows.Where(r => r.Renewal).Sum(r => r.Count),
            rows.Where(r => !r.Renewal && r.HasEarlier).Sum(r => r.Count));
    }

    /// <summary>Membresías que vencieron en el período: fin dentro del rango, ya pasado, sin cancelar ni congelar (nunca activadas no cuentan).</summary>
    public static Task<int> ExpiredInPeriodAsync(AnalyticsDbContext db, IReadOnlyList<Guid> branches, ReportPeriod period, DateOnly today, CancellationToken ct)
    {
        var from = period.From;
        var to = period.To;
        return db.Memberships
            .Where(m => branches.Contains(m.BranchId) && m.Status == "Active" && m.EndDate >= from && m.EndDate <= to && m.EndDate < today)
            .CountAsync(ct);
    }

    /// <summary>Próximas a vencer: activas cuyo fin cae entre hoy y hoy + N días (foto de hoy).</summary>
    public static Task<int> ExpiringSoonAsync(AnalyticsDbContext db, IReadOnlyList<Guid> branches, DateOnly today, int days, CancellationToken ct)
    {
        var limit = today.AddDays(days);
        return db.Memberships
            .Where(m => branches.Contains(m.BranchId) && m.Status == "Active" && m.EndDate >= today && m.EndDate <= limit)
            .CountAsync(ct);
    }

    // ---- Operación ------------------------------------------------------------------------------------------------------

    public static Task<int> GymVisitsAsync(AnalyticsDbContext db, IReadOnlyList<Guid> branches, ReportPeriod period, CancellationToken ct)
    {
        var start = period.StartUtc;
        var end = period.EndUtcExclusive;
        return db.GymEntries.Where(e => branches.Contains(e.BranchId) && e.CheckInAt >= start && e.CheckInAt < end).CountAsync(ct);
    }

    /// <summary>
    /// Programadas con aún por ocurrir, realizadas (Completed), canceladas, y pendientes de cierre (Scheduled cuya hora ya pasó: el
    /// sistema no las cierra solo, nunca se cuentan como realizadas). <paramref name="instructorUserId"/> limita a las sesiones de ese instructor.
    /// </summary>
    public static async Task<SessionCounts> SessionCountsAsync(AnalyticsDbContext db, IReadOnlyList<Guid> branches, ReportPeriod period, DateTimeOffset now, Guid? instructorUserId, CancellationToken ct)
    {
        var rows = await Sessions(db, branches, period, instructorUserId)
            .GroupBy(s => new { s.Status, Past = s.EndsAt < now })
            .Select(g => new { g.Key.Status, g.Key.Past, Count = g.Count() })
            .ToListAsync(ct);
        return new SessionCounts(
            rows.Where(r => r.Status == "Completed").Sum(r => r.Count),
            rows.Where(r => r.Status == "Cancelled").Sum(r => r.Count),
            rows.Where(r => r.Status == "Scheduled" && !r.Past).Sum(r => r.Count),
            rows.Where(r => r.Status == "Scheduled" && r.Past).Sum(r => r.Count));
    }

    /// <summary>
    /// Ocupación reservada: Σ reservas Confirmadas ÷ Σ capacidad, sobre sesiones no canceladas con capacidad. Lista de espera, cupos ofrecidos
    /// y reservas canceladas no cuentan como participación.
    /// </summary>
    public static async Task<Occupancy> OccupancyAsync(AnalyticsDbContext db, IReadOnlyList<Guid> branches, ReportPeriod period, Guid? instructorUserId, CancellationToken ct)
    {
        var row = await Sessions(db, branches, period, instructorUserId)
            .Where(s => s.Status != "Cancelled" && s.Capacity > 0)
            .Select(s => new { s.Capacity, Booked = db.Reservations.Count(r => r.SessionId == s.Id && r.Status == "Confirmed") })
            .GroupBy(_ => 1)
            .Select(g => new { Capacity = g.Sum(x => x.Capacity), Booked = g.Sum(x => x.Booked), Sessions = g.Count() })
            .FirstOrDefaultAsync(ct);
        return row is null ? new Occupancy(0, 0, 0) : new Occupancy(row.Booked, row.Capacity, row.Sessions);
    }

    /// <summary>Reservas de las sesiones del período: confirmadas, y canceladas por el alumno o el personal (no por cancelar la sesión).</summary>
    public static async Task<(int Confirmed, int Cancelled)> ReservationCountsAsync(AnalyticsDbContext db, IReadOnlyList<Guid> branches, ReportPeriod period, Guid? instructorUserId, CancellationToken ct)
    {
        var rows = await (from r in db.Reservations
                          join s in Sessions(db, branches, period, instructorUserId) on r.SessionId equals s.Id
                          where r.Status == "Confirmed" || (r.Status == "Cancelled" && s.Status != "Cancelled")
                          group r by r.Status into g
                          select new { Status = g.Key, Count = g.Count() }).ToListAsync(ct);
        return (rows.Where(r => r.Status == "Confirmed").Sum(r => r.Count), rows.Where(r => r.Status == "Cancelled").Sum(r => r.Count));
    }

    /// <summary>Inasistencias registradas (NoShow) y asistencias (Attended) a clases de las sesiones del período.</summary>
    public static async Task<(int Attended, int NoShow)> ClassAttendanceAsync(AnalyticsDbContext db, IReadOnlyList<Guid> branches, ReportPeriod period, Guid? instructorUserId, CancellationToken ct)
    {
        var rows = await (from a in db.ClassAttendances
                          join s in Sessions(db, branches, period, instructorUserId) on a.SessionId equals s.Id
                          group a by a.Outcome into g
                          select new { Outcome = g.Key, Count = g.Count() }).ToListAsync(ct);
        return (rows.Where(r => r.Outcome == "Attended").Sum(r => r.Count), rows.Where(r => r.Outcome == "NoShow").Sum(r => r.Count));
    }

    private static IQueryable<ClassSessionRef> Sessions(AnalyticsDbContext db, IReadOnlyList<Guid> branches, ReportPeriod period, Guid? instructorUserId)
    {
        var start = period.StartUtc;
        var end = period.EndUtcExclusive;
        var sessions = db.ClassSessions.Where(s => branches.Contains(s.BranchId) && s.StartsAt >= start && s.StartsAt < end);
        return instructorUserId is { } userId
            ? sessions.Where(s => db.Trainers.Any(t => t.Id == s.InstructorId && t.UserId == userId))
            : sessions;
    }
}
