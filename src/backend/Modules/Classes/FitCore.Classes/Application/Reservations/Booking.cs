using FitCore.Application.Core;
using FitCore.Classes.Application.Sessions;
using FitCore.Classes.Domain;
using FitCore.Classes.Persistence;
using FitCore.Platform.Security;
using FitCore.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FitCore.Classes.Application.Reservations;

/// <summary>Decide si un alumno puede reservar una sesión según las reglas de la clase (todo en el servidor, nunca solo en Angular).</summary>
internal static class Eligibility
{
    /// <summary>Error de elegibilidad, o null si el alumno puede reservar.</summary>
    public static async Task<Error?> CheckAsync(ClassesDbContext db, GroupClass groupClass, MemberRef member, ClassSession session, TimeZoneInfo zone, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!member.IsActive)
        {
            return ReservationErrors.MemberInactive;
        }

        var sessionDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(session.StartsAt, zone).DateTime);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);

        if (groupClass.MinAge is { } minAge)
        {
            if (member.BirthDate is not { } birthDate)
            {
                return ReservationErrors.AgeUnknown;
            }

            if (EligibilityRules.AgeOn(birthDate, sessionDay) < minAge)
            {
                return ReservationErrors.BelowMinAge;
            }
        }

        if (!groupClass.RequireActiveMembership)
        {
            return null;
        }

        // La membresía debe estar activa y cubrir la fecha de la sesión (una renovación que empieza la semana próxima sirve para reservar esa semana).
        var memberships = await db.MembershipRefs.AsNoTracking()
            .Where(m => m.MemberId == member.Id && m.Status != "Cancelled")
            .OrderByDescending(m => m.StartDate).Take(20)
            .ToListAsync(cancellationToken);
        var covering = memberships.FirstOrDefault(m => m.Status == "Active" && m.StartDate <= sessionDay && sessionDay <= m.EndDate);
        if (covering is null)
        {
            if (memberships.Any(m => m.Status == "Frozen"))
            {
                return ReservationErrors.MembershipFrozen;
            }

            return memberships.Any(m => m.Status == "PendingPayment" && m.EndDate >= sessionDay)
                ? ReservationErrors.MembershipPendingPayment
                : ReservationErrors.NoActiveMembership;
        }

        var planBranches = await db.PlanBranchRefs.AsNoTracking().Where(p => p.PlanId == covering.PlanId).Select(p => p.BranchId).ToListAsync(cancellationToken);
        if (planBranches.Count > 0 && !planBranches.Contains(session.BranchId))
        {
            return ReservationErrors.PlanBranchRestricted;
        }

        if (!groupClass.ApplyPaymentPolicy)
        {
            return null;
        }

        // Misma política de acceso con deuda que la entrada al gimnasio, con los saldos de hoy (solo cuenta lo confirmado).
        var obligations = await db.ObligationRefs.AsNoTracking().Where(o => o.MembershipId == covering.Id && o.Status != "Voided").ToListAsync(cancellationToken);
        var obligationIds = obligations.Select(o => o.Id).ToList();
        var paid = (await (from a in db.AllocationRefs.AsNoTracking()
                           join p in db.PaymentRefs.AsNoTracking() on a.PaymentId equals p.Id
                           where obligationIds.Contains(a.ObligationId) && p.Status == "Confirmed"
                           select new { a.ObligationId, a.Amount }).ToListAsync(cancellationToken))
            .GroupBy(x => x.ObligationId).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));
        var standings = obligations.Select(o => new ObligationStanding(o.PayableAmount, paid.GetValueOrDefault(o.Id), o.DueDate)).ToList();

        var settings = await db.SettingsRefs.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        var policy = settings is not null && Enum.TryParse<DebtPolicy>(settings.AccessPolicy, out var parsed) ? parsed : DebtPolicy.AllowUntilOverdue;
        return EligibilityRules.DebtDenial(policy, settings?.MinPaymentPercent ?? 50, today, standings);
    }
}

internal static class Booking
{
    public const string CreateScope = "reservation.create";

    /// <summary>
    /// Reserva una sesión para un alumno. Todo ocurre en una transacción: la clave de idempotencia, el bloqueo de la fila de la sesión (quien
    /// llegue segundo espera y ve el recuento ya actualizado, así el último cupo nunca lo ocupan dos) y la inserción. La base repite la
    /// garantía (índice único por alumno y sesión, y disparador de capacidad) por si algo se salta la aplicación.
    /// </summary>
    public static async Task<Result<ReservationDto>> BookAsync(
        ClassesDbContext db,
        IClock clock,
        UserAccess actor,
        MemberRef member,
        ClassSession authorizedSession,
        ReservationSource source,
        bool joinWaitlist,
        string? idempotencyKey,
        object request,
        CancellationToken cancellationToken)
    {
        if (Idempotency.Validate(idempotencyKey) is { } keyError)
        {
            return keyError;
        }

        var key = idempotencyKey!.Trim();
        var hash = Idempotency.Hash(request, actor.UserId);
        var now = clock.UtcNow;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            // Primero la idempotencia: un reintento de una reserva que ya salió bien devuelve esa misma respuesta, aunque ahora la ventana
            // haya cerrado o el cupo ya esté ocupado por ella misma.
            var begin = await Idempotency.BeginAsync<ReservationDto>(db, actor.TenantId, key, CreateScope, hash, actor.UserId, now, cancellationToken);
            if (begin.IsFailure)
            {
                return begin.Error;
            }

            if (begin.Value.IsReplay && begin.Value.Response is { } replay)
            {
                return replay;
            }

            var groupClass = await db.GroupClasses.AsNoTracking().FirstOrDefaultAsync(c => c.Id == authorizedSession.ClassId, cancellationToken);
            if (groupClass is null)
            {
                return GroupClassErrors.NotFound;
            }

            if (!groupClass.IsActive)
            {
                return SessionErrors.ClassInactive;
            }

            if (Availability(authorizedSession, groupClass, now) is { } closed)
            {
                return closed;
            }

            var zone = await SessionScheduling.ZoneAsync(db, authorizedSession.BranchId, cancellationToken);
            if (await Eligibility.CheckAsync(db, groupClass, member, authorizedSession, zone, now, cancellationToken) is { } denied)
            {
                return denied;
            }

            // Desde aquí, bajo el bloqueo de la sesión: se relee todo porque pudo cambiar mientras se evaluaba la elegibilidad.
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM classes.class_sessions WHERE id = {authorizedSession.Id} FOR UPDATE", cancellationToken);
            var session = await db.ClassSessions.AsNoTracking().FirstAsync(s => s.Id == authorizedSession.Id, cancellationToken);
            if (Availability(session, groupClass, now) is { } closedNow)
            {
                return closedNow;
            }

            if (await db.Reservations.AnyAsync(
                    r => r.SessionId == session.Id && r.MemberId == member.Id
                         && (r.Status == ReservationStatus.Confirmed || r.Status == ReservationStatus.Waitlisted || r.Status == ReservationStatus.Offered),
                    cancellationToken))
            {
                return ReservationErrors.AlreadyBooked;
            }

            if (await CrossesAsync(db, member.Id, session, cancellationToken))
            {
                return ReservationErrors.ScheduleConflict;
            }

            // Un cupo liberado que aún no se ofreció se ofrece antes de contar: nadie se cuela delante de la fila.
            if ((await Waitlist.PromoteAsync(db, groupClass, session, now, actor.UserId, cancellationToken)).Count > 0)
            {
                await db.SaveChangesAsync(cancellationToken);
            }

            var taken = await db.Reservations.CountAsync(r => r.SessionId == session.Id && (r.Status == ReservationStatus.Confirmed || r.Status == ReservationStatus.Offered), cancellationToken);
            ClassReservation reservation;
            if (taken < session.Capacity)
            {
                reservation = ClassReservation.Confirm(actor.TenantId, session.BranchId, session.Id, member.Id, source, actor.UserId, now);
            }
            else
            {
                // Sesión llena: solo entra a la lista de espera quien lo pide expresamente y la clase la admite.
                if (!joinWaitlist)
                {
                    return ReservationErrors.SessionFull;
                }

                if (!groupClass.WaitlistEnabled)
                {
                    return ReservationErrors.WaitlistDisabled;
                }

                var waiting = await db.Reservations.CountAsync(r => r.SessionId == session.Id && r.Status == ReservationStatus.Waitlisted, cancellationToken);
                if (waiting >= groupClass.WaitlistCapacity)
                {
                    return ReservationErrors.WaitlistFull;
                }

                reservation = ClassReservation.Waitlist(actor.TenantId, session.BranchId, session.Id, member.Id, source, actor.UserId, now);
            }

            db.Reservations.Add(reservation);
            await db.SaveChangesAsync(cancellationToken);

            var dto = (await ReservationMapper.ToDtosAsync(db, [reservation], cancellationToken))[0];
            await Idempotency.CompleteAsync(db, actor.TenantId, key, 201, dto, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return dto;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg && DatabaseError(pg.SqlState) is not null)
        {
            return DatabaseError(((PostgresException)ex.InnerException).SqlState)!;
        }
    }

    /// <summary>Lo que la base rechazó cuando la aplicación no llegó a verlo (dos solicitudes a la vez): reserva repetida, sin cupo o sesión no disponible.</summary>
    private static Error? DatabaseError(string sqlState) => sqlState switch
    {
        PostgresErrorCodes.UniqueViolation => ReservationErrors.AlreadyBooked,
        CapacityFull => ReservationErrors.SessionFull,
        SessionNotOpen => ReservationErrors.SessionNotBookable,
        WaitlistOff => ReservationErrors.WaitlistDisabled,
        WaitlistIsFull => ReservationErrors.WaitlistFull,
        _ => null
    };

    /// <summary>¿El alumno ya tiene una reserva con cupo (confirmada u ofrecida) que se cruza con la sesión?</summary>
    public static Task<bool> CrossesAsync(ClassesDbContext db, Guid memberId, ClassSession session, CancellationToken cancellationToken) =>
        (from r in db.Reservations.AsNoTracking()
         join s in db.ClassSessions.AsNoTracking() on r.SessionId equals s.Id
         where r.MemberId == memberId && r.SessionId != session.Id && (r.Status == ReservationStatus.Confirmed || r.Status == ReservationStatus.Offered)
               && s.Status == SessionStatus.Scheduled && s.StartsAt < session.EndsAt && s.EndsAt > session.StartsAt
         select r.Id).AnyAsync(cancellationToken);

    // Códigos propios del disparador de capacidad (ver la migración AddReservationsSecurity).
    public const string CapacityFull = "FC001";
    public const string SessionNotOpen = "FC002";
    public const string CapacityBelowBooked = "FC003";
    public const string WaitlistOff = "FC004";
    public const string WaitlistIsFull = "FC005";

    /// <summary>La sesión debe estar programada, no haber empezado y estar dentro de la ventana de reserva de su clase.</summary>
    private static Error? Availability(ClassSession session, GroupClass groupClass, DateTimeOffset now)
    {
        if (!session.IsScheduled || session.StartsAt <= now)
        {
            return ReservationErrors.SessionNotBookable;
        }

        return EligibilityRules.BookingWindow(session.StartsAt, groupClass.MinBookingNoticeMinutes, groupClass.MaxBookingAdvanceDays, now);
    }
}

internal static class Cancelling
{
    /// <summary>
    /// Cancela una reserva. El alumno solo puede dentro del plazo de la clase; el personal también fuera de plazo, como excepción explícita
    /// (motivo obligatorio) que queda marcada y auditada. Nadie cancela una sesión que ya empezó. Si la reserva ocupaba un cupo, el cupo
    /// liberado se ofrece al primero elegible de la lista de espera <b>en la misma transacción</b>, con la sesión bloqueada: nadie puede
    /// colarse entre la cancelación y la oferta. Repetir la cancelación devuelve la reserva tal como quedó.
    /// </summary>
    public static async Task<Result<ReservationDto>> CancelAsync(
        ClassesDbContext db,
        IClock clock,
        Guid? actorUserId,
        ClassReservation reservation,
        bool isSelf,
        string? reason,
        CancellationToken cancellationToken,
        bool onlyOffered = false)
    {
        if (reservation.Status == ReservationStatus.Cancelled)
        {
            return (await ReservationMapper.ToDtosAsync(db, [reservation], cancellationToken))[0];
        }

        if (!reservation.IsLive)
        {
            return ReservationErrors.NotCancellable;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await SessionScheduling.LockSessionsAsync(db, [reservation.SessionId], cancellationToken);
        await db.Entry(reservation).ReloadAsync(cancellationToken);
        if (reservation.Status == ReservationStatus.Cancelled)
        {
            return (await ReservationMapper.ToDtosAsync(db, [reservation], cancellationToken))[0];
        }

        if (!reservation.IsLive)
        {
            return ReservationErrors.NotCancellable;
        }

        // Rechazar solo vale sobre una invitación: si mientras tanto la aceptaron, no se cancela una reserva confirmada por error.
        if (onlyOffered && reservation.Status != ReservationStatus.Offered)
        {
            return ReservationErrors.NotOffered;
        }

        var session = await db.ClassSessions.AsNoTracking().FirstAsync(s => s.Id == reservation.SessionId, cancellationToken);
        var groupClass = await db.GroupClasses.AsNoTracking().FirstAsync(c => c.Id == session.ClassId, cancellationToken);
        var now = clock.UtcNow;
        if (session.StartsAt <= now)
        {
            return ReservationErrors.SessionStarted;
        }

        // Solo quien ocupa un cupo confirmado debe avisar con tiempo: salir de la lista de espera o rechazar una invitación no tiene plazo.
        var late = reservation.Status == ReservationStatus.Confirmed && !EligibilityRules.CancellationOnTime(session.StartsAt, groupClass.CancellationDeadlineMinutes, now);
        if (late)
        {
            if (isSelf)
            {
                return ReservationErrors.CancellationDeadlinePassed;
            }

            if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < ClassReservation.MinLateReasonLength)
            {
                return ReservationErrors.LateReasonRequired;
            }
        }

        var heldSeat = reservation.HoldsSeat;
        reservation.Cancel(reason, late, actorUserId, now);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            if (heldSeat && (await Waitlist.PromoteAsync(db, groupClass, session, now, actorUserId, cancellationToken)).Count > 0)
            {
                await db.SaveChangesAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Otra solicitud la modificó a la vez: si ya está cancelada el resultado es el que se pedía.
            db.ChangeTracker.Clear();
            var current = await db.Reservations.AsNoTracking().FirstOrDefaultAsync(r => r.Id == reservation.Id, cancellationToken);
            if (current?.Status == ReservationStatus.Cancelled)
            {
                return (await ReservationMapper.ToDtosAsync(db, [current], cancellationToken))[0];
            }

            return ReservationErrors.Changed;
        }

        return (await ReservationMapper.ToDtosAsync(db, [reservation], cancellationToken))[0];
    }
}

internal static class ReservationMapper
{
    public static IQueryable<ClassReservation> VisibleTo(this IQueryable<ClassReservation> reservations, UserAccess actor)
    {
        if (actor.AllBranches)
        {
            return reservations;
        }

        var branchIds = actor.BranchIds.ToList();
        return reservations.Where(r => branchIds.Contains(r.BranchId));
    }

    public static async Task<IReadOnlyList<ReservationDto>> ToDtosAsync(ClassesDbContext db, IReadOnlyList<ClassReservation> reservations, CancellationToken cancellationToken)
    {
        var sessionIds = reservations.Select(r => r.SessionId).Distinct().ToList();
        var memberIds = reservations.Select(r => r.MemberId).Distinct().ToList();
        var sessions = await db.ClassSessions.AsNoTracking().Where(s => sessionIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, cancellationToken);
        var members = await db.MemberRefs.AsNoTracking().Where(m => memberIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, cancellationToken);
        var classes = await SessionScheduling.ClassNamesAsync(db, [.. sessions.Values.Select(s => s.ClassId).Distinct()], cancellationToken);
        var instructors = await SessionScheduling.InstructorNamesAsync(db, [.. sessions.Values.Select(s => s.InstructorId).Distinct()], cancellationToken);
        var branches = await SessionScheduling.BranchNamesAsync(db, [.. sessions.Values.Select(s => s.BranchId).Distinct()], cancellationToken);
        var positions = await Waitlist.PositionsAsync(db, [.. reservations.Where(r => r.Status == ReservationStatus.Waitlisted).Select(r => r.SessionId).Distinct()], cancellationToken);

        return
        [
            .. reservations.Select(r =>
            {
                sessions.TryGetValue(r.SessionId, out var session);
                members.TryGetValue(r.MemberId, out var member);
                var knownClass = session is not null && classes.TryGetValue(session.ClassId, out var c);
                var (className, activity) = knownClass ? classes[session!.ClassId] : (string.Empty, string.Empty);
                return new ReservationDto(
                    r.Id,
                    r.SessionId,
                    session?.ClassId ?? Guid.Empty,
                    className,
                    activity,
                    r.BranchId,
                    branches.GetValueOrDefault(r.BranchId, string.Empty),
                    session is null ? string.Empty : instructors.GetValueOrDefault(session.InstructorId, string.Empty),
                    session?.StartsAt ?? default,
                    session?.EndsAt ?? default,
                    session?.Status.ToString() ?? string.Empty,
                    r.MemberId,
                    member?.FullName ?? string.Empty,
                    member?.Code ?? string.Empty,
                    r.Status.ToString(),
                    r.Source.ToString(),
                    r.CreatedAt,
                    r.CancelledAt,
                    r.CancelReason,
                    r.LateCancellation,
                    positions.TryGetValue(r.Id, out var position) ? position : null,
                    r.OfferedAt,
                    r.OfferExpiresAt);
            })
        ];
    }
}
