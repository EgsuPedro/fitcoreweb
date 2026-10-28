using FitCore.Application.Core;
using FitCore.Classes.Application.Sessions;
using FitCore.Classes.Domain;
using FitCore.Classes.Persistence;
using FitCore.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace FitCore.Classes.Application.Reservations;

/// <summary>
/// Lista de espera con invitación con plazo. Al liberarse un cupo se <b>reserva</b> para el primero elegible de la fila (pasa a
/// «ofrecida» y ocupa cupo, así nadie más puede tomarlo por la puerta de atrás); si lo acepta a tiempo queda confirmado, y si lo rechaza o
/// el plazo vence, el cupo pasa al siguiente. Todo se decide con la fila de la sesión bloqueada.
/// </summary>
internal static class Waitlist
{
    /// <summary>
    /// Ofrece los cupos libres a los primeros elegibles de la fila. <b>Debe llamarse dentro de una transacción que ya bloqueó la sesión</b>
    /// y con la sesión recién leída. Los cambios quedan en el contexto para que el llamador los guarde junto con los suyos. Los que no son
    /// elegibles ahora (por ejemplo, deben una cuota) se saltan sin perder su lugar. Mientras la ventana de reserva esté cerrada no se
    /// ofrece nada: nadie podría reservar ese cupo de todos modos.
    /// </summary>
    public static async Task<IReadOnlyList<ClassReservation>> PromoteAsync(
        ClassesDbContext db,
        GroupClass groupClass,
        ClassSession session,
        DateTimeOffset now,
        Guid? by,
        CancellationToken cancellationToken)
    {
        if (!groupClass.WaitlistEnabled || !groupClass.IsActive || !session.IsScheduled || session.StartsAt <= now
            || EligibilityRules.BookingWindow(session.StartsAt, groupClass.MinBookingNoticeMinutes, groupClass.MaxBookingAdvanceDays, now) is not null)
        {
            return [];
        }

        var taken = await db.Reservations.CountAsync(
            r => r.SessionId == session.Id && (r.Status == ReservationStatus.Confirmed || r.Status == ReservationStatus.Offered),
            cancellationToken);
        var free = session.Capacity - taken;
        if (free <= 0)
        {
            return [];
        }

        // Orden estable: por hora de llegada y, a igualdad, por identificador (que es creciente).
        var waiting = await db.Reservations
            .Where(r => r.SessionId == session.Id && r.Status == ReservationStatus.Waitlisted)
            .OrderBy(r => r.CreatedAt).ThenBy(r => r.Id)
            .ToListAsync(cancellationToken);
        if (waiting.Count == 0)
        {
            return [];
        }

        var zone = await SessionScheduling.ZoneAsync(db, session.BranchId, cancellationToken);
        var expiresAt = Earlier(now.AddMinutes(groupClass.OfferWindowMinutes), session.StartsAt);
        var offered = new List<ClassReservation>();
        foreach (var candidate in waiting)
        {
            if (offered.Count >= free)
            {
                break;
            }

            var member = await db.MemberRefs.AsNoTracking().FirstOrDefaultAsync(m => m.Id == candidate.MemberId, cancellationToken);
            if (member is null
                || await Eligibility.CheckAsync(db, groupClass, member, session, zone, now, cancellationToken) is not null
                || await Booking.CrossesAsync(db, member.Id, session, cancellationToken))
            {
                continue;
            }

            candidate.Offer(expiresAt, by, now);
            offered.Add(candidate);
        }

        return offered;
    }

    /// <summary>Lo mismo en su propia transacción (al subir la capacidad de una sesión y en el barrido del Worker).</summary>
    public static async Task<int> PromoteSessionAsync(ClassesDbContext db, IClock clock, Guid sessionId, Guid? by, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await SessionScheduling.LockSessionsAsync(db, [sessionId], cancellationToken);
            var session = await db.ClassSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
            if (session is null)
            {
                return 0;
            }

            var groupClass = await db.GroupClasses.AsNoTracking().FirstAsync(c => c.Id == session.ClassId, cancellationToken);
            var offered = await PromoteAsync(db, groupClass, session, clock.UtcNow, by, cancellationToken);
            if (offered.Count == 0)
            {
                return 0;
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return offered.Count;
        }
        catch (Exception ex) when (SessionScheduling.IsRetryable(ex))
        {
            // Otra solicitud llegó primero: el próximo barrido lo completa.
            db.ChangeTracker.Clear();
            return 0;
        }
    }

    /// <summary>
    /// Vence una invitación que no se respondió a tiempo y ofrece el cupo al siguiente. Devuelve false si ya no hacía falta (la respondieron
    /// o alguien la cambió mientras tanto).
    /// </summary>
    public static async Task<bool> ExpireOfferAsync(ClassesDbContext db, IClock clock, Guid reservationId, CancellationToken cancellationToken)
    {
        var sessionId = await db.Reservations.AsNoTracking().Where(r => r.Id == reservationId).Select(r => (Guid?)r.SessionId).FirstOrDefaultAsync(cancellationToken);
        if (sessionId is null)
        {
            return false;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await SessionScheduling.LockSessionsAsync(db, [sessionId.Value], cancellationToken);
            var now = clock.UtcNow;
            var reservation = await db.Reservations.FirstOrDefaultAsync(r => r.Id == reservationId, cancellationToken);
            if (reservation is not { Status: ReservationStatus.Offered } || reservation.OfferExpiresAt > now)
            {
                return false;
            }

            reservation.Expire(null, now);
            var session = await db.ClassSessions.AsNoTracking().FirstAsync(s => s.Id == sessionId.Value, cancellationToken);
            var groupClass = await db.GroupClasses.AsNoTracking().FirstAsync(c => c.Id == session.ClassId, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            if ((await PromoteAsync(db, groupClass, session, now, null, cancellationToken)).Count > 0)
            {
                await db.SaveChangesAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch (Exception ex) when (SessionScheduling.IsRetryable(ex))
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }

    /// <summary>
    /// Acepta una invitación. Con la sesión bloqueada se comprueba que siga ofrecida, que el plazo no haya vencido y que el alumno siga siendo
    /// elegible. El cupo ya estaba reservado para él, así que no cambia la ocupación.
    /// </summary>
    public static async Task<Result<ReservationDto>> AcceptAsync(
        ClassesDbContext db,
        IClock clock,
        Guid? actorUserId,
        ClassReservation reservation,
        CancellationToken cancellationToken)
    {
        if (reservation.Status == ReservationStatus.Confirmed && reservation.OfferedAt is not null)
        {
            // Aceptar dos veces devuelve lo mismo.
            return (await ReservationMapper.ToDtosAsync(db, [reservation], cancellationToken))[0];
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await SessionScheduling.LockSessionsAsync(db, [reservation.SessionId], cancellationToken);
        await db.Entry(reservation).ReloadAsync(cancellationToken);
        if (reservation.Status == ReservationStatus.Confirmed && reservation.OfferedAt is not null)
        {
            return (await ReservationMapper.ToDtosAsync(db, [reservation], cancellationToken))[0];
        }

        if (reservation.Status != ReservationStatus.Offered)
        {
            return ReservationErrors.NotOffered;
        }

        var now = clock.UtcNow;
        if (reservation.OfferExpiresAt <= now)
        {
            return ReservationErrors.OfferExpired;
        }

        var session = await db.ClassSessions.AsNoTracking().FirstAsync(s => s.Id == reservation.SessionId, cancellationToken);
        var groupClass = await db.GroupClasses.AsNoTracking().FirstAsync(c => c.Id == session.ClassId, cancellationToken);
        if (!groupClass.IsActive)
        {
            return SessionErrors.ClassInactive;
        }

        if (!session.IsScheduled || session.StartsAt <= now)
        {
            return ReservationErrors.SessionNotBookable;
        }

        var member = await db.MemberRefs.AsNoTracking().FirstOrDefaultAsync(m => m.Id == reservation.MemberId, cancellationToken);
        if (member is null)
        {
            return ReservationErrors.MemberNotFound;
        }

        var zone = await SessionScheduling.ZoneAsync(db, session.BranchId, cancellationToken);
        if (await Eligibility.CheckAsync(db, groupClass, member, session, zone, now, cancellationToken) is { } denied)
        {
            return denied;
        }

        if (await Booking.CrossesAsync(db, member.Id, session, cancellationToken))
        {
            return ReservationErrors.ScheduleConflict;
        }

        reservation.Accept(actorUserId, now);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ReservationErrors.Changed;
        }

        return (await ReservationMapper.ToDtosAsync(db, [reservation], cancellationToken))[0];
    }

    /// <summary>Rechazar una invitación es cancelarla: el cupo pasa al siguiente en la misma transacción.</summary>
    public static async Task<Result<ReservationDto>> DeclineAsync(
        ClassesDbContext db,
        IClock clock,
        Guid? actorUserId,
        ClassReservation reservation,
        bool isSelf,
        CancellationToken cancellationToken)
    {
        if (reservation.Status == ReservationStatus.Cancelled)
        {
            return (await ReservationMapper.ToDtosAsync(db, [reservation], cancellationToken))[0];
        }

        if (reservation.Status != ReservationStatus.Offered)
        {
            return ReservationErrors.NotOffered;
        }

        return await Cancelling.CancelAsync(db, clock, actorUserId, reservation, isSelf, "Invitación rechazada", cancellationToken, onlyOffered: true);
    }

    /// <summary>Posición (1 = la siguiente en recibir un cupo) de cada reserva en espera de las sesiones indicadas.</summary>
    public static async Task<Dictionary<Guid, int>> PositionsAsync(ClassesDbContext db, IReadOnlyCollection<Guid> sessionIds, CancellationToken cancellationToken)
    {
        if (sessionIds.Count == 0)
        {
            return [];
        }

        var waiting = await db.Reservations.AsNoTracking()
            .Where(r => sessionIds.Contains(r.SessionId) && r.Status == ReservationStatus.Waitlisted)
            .Select(r => new { r.Id, r.SessionId, r.CreatedAt })
            .ToListAsync(cancellationToken);
        return waiting
            .GroupBy(r => r.SessionId)
            .SelectMany(g => g.OrderBy(r => r.CreatedAt).ThenBy(r => r.Id).Select((r, index) => (r.Id, Position: index + 1)))
            .ToDictionary(x => x.Id, x => x.Position);
    }

    private static DateTimeOffset Earlier(DateTimeOffset a, DateTimeOffset b) => a <= b ? a : b;
}
