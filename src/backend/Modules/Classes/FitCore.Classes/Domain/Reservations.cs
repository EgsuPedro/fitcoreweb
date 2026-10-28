using FitCore.SharedKernel;

namespace FitCore.Classes.Domain;

/// <summary>
/// Estado de la reserva. «Asistió» y «No asistió» no están aquí: son el resultado de la asistencia (bloque 6) y se guardan aparte, así una
/// reserva sigue siendo «confirmada» aunque después se registre que el alumno no vino.
/// </summary>
public enum ReservationStatus
{
    /// <summary>Tiene cupo en la sesión.</summary>
    Confirmed = 1,

    /// <summary>En lista de espera (bloque 5): no ocupa cupo.</summary>
    Waitlisted = 2,

    /// <summary>Se le ofreció un cupo liberado y tiene un plazo para aceptarlo (bloque 5): el cupo ya está reservado para él.</summary>
    Offered = 3,

    Cancelled = 4,

    /// <summary>La invitación de la lista de espera venció sin respuesta (bloque 5).</summary>
    Expired = 5
}

public enum ReservationSource
{
    /// <summary>La hizo el propio alumno desde su portal.</summary>
    Member = 1,

    /// <summary>La hizo el personal en nombre del alumno.</summary>
    Staff = 2
}

/// <summary>
/// Reserva de un alumno en una sesión. Nunca se borra: cancelar cambia su estado y libera el cupo. Un alumno tiene a lo sumo una reserva
/// viva (confirmada, en espera u ofrecida) por sesión; las canceladas conservan la historia y no impiden volver a reservar.
/// </summary>
public sealed class ClassReservation : Entity<Guid>, ITenantOwned, IAuditable
{
    public const int MinLateReasonLength = 5;
    public const int MaxReasonLength = 300;

    private ClassReservation(Guid id)
        : base(id)
    {
    }

    public Guid TenantId { get; private set; }
    public Guid BranchId { get; private set; }
    public Guid SessionId { get; private set; }
    public Guid MemberId { get; private set; }
    public ReservationStatus Status { get; private set; }
    public ReservationSource Source { get; private set; }
    public DateTimeOffset? CancelledAt { get; private set; }
    public Guid? CancelledBy { get; private set; }
    public string? CancelReason { get; private set; }

    /// <summary>Se canceló pasado el plazo: solo el personal puede hacerlo, con motivo, y queda marcado.</summary>
    public bool LateCancellation { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public Guid? CreatedBy { get; private set; }
    public DateTimeOffset? UpdatedAt { get; private set; }
    public Guid? UpdatedBy { get; private set; }

    /// <summary>Confirmada, en espera u ofrecida.</summary>
    public bool IsLive => Status is ReservationStatus.Confirmed or ReservationStatus.Waitlisted or ReservationStatus.Offered;

    /// <summary>Ocupa cupo de la sesión: la confirmada y la ofrecida (el cupo ofrecido está reservado para quien lo recibió).</summary>
    public bool HoldsSeat => Status is ReservationStatus.Confirmed or ReservationStatus.Offered;

    public static ClassReservation Confirm(Guid tenantId, Guid branchId, Guid sessionId, Guid memberId, ReservationSource source, Guid? by, DateTimeOffset now) =>
        new(Guid.CreateVersion7())
        {
            TenantId = tenantId,
            BranchId = branchId,
            SessionId = sessionId,
            MemberId = memberId,
            Status = ReservationStatus.Confirmed,
            Source = source,
            CreatedAt = now,
            CreatedBy = by
        };

    public void Cancel(string? reason, bool late, Guid? by, DateTimeOffset now)
    {
        Status = ReservationStatus.Cancelled;
        CancelReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        LateCancellation = late;
        CancelledAt = now;
        CancelledBy = by;
        UpdatedAt = now;
        UpdatedBy = by;
    }
}

/// <summary>Respuesta guardada de una solicitud con <c>Idempotency-Key</c>: repetirla devuelve lo mismo sin repetir el efecto.</summary>
public sealed class IdempotencyKey : ITenantOwned
{
    public Guid TenantId { get; init; }
    public string Key { get; init; } = string.Empty;
    public string Scope { get; init; } = string.Empty;
    public string RequestHash { get; init; } = string.Empty;
    public Guid? ActorId { get; init; }
    public int? ResponseStatus { get; init; }
    public string? ResponseBody { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>Estado de una obligación de pago de la membresía, para aplicar la política de acceso con deuda.</summary>
public readonly record struct ObligationStanding(decimal Payable, decimal Paid, DateOnly DueDate)
{
    public decimal Owed => Math.Max(0m, Payable - Paid);
}

public enum DebtPolicy
{
    RequireFullPayment = 1,
    AllowUntilOverdue = 2,
    AllowWithMinimumPayment = 3
}

/// <summary>
/// Reglas de elegibilidad para reservar. La política de acceso con deuda es la <b>misma</b> que el gimnasio ya configuró para la entrada
/// (ADR 0008): aquí se reexpresa porque los módulos no se referencian, y una prueba de arquitectura comprueba que ambas decidan igual.
/// </summary>
public static class EligibilityRules
{
    /// <summary>Error por deuda según la política, o null si la deuda no impide reservar.</summary>
    public static Error? DebtDenial(DebtPolicy policy, int minPaymentPercent, DateOnly today, IReadOnlyCollection<ObligationStanding> obligations)
    {
        switch (policy)
        {
            case DebtPolicy.RequireFullPayment:
                return obligations.Any(o => o.Owed > 0m) ? ReservationErrors.DebtPending : null;

            case DebtPolicy.AllowWithMinimumPayment:
                var payable = obligations.Sum(o => o.Payable);
                var paid = obligations.Sum(o => Math.Min(o.Paid, o.Payable));
                return payable > 0m && paid * 100m < payable * minPaymentPercent ? ReservationErrors.MinimumPaymentNotMet : null;

            default:
                return obligations.Any(o => o.Owed > 0m && o.DueDate < today) ? ReservationErrors.DebtOverdue : null;
        }
    }

    /// <summary>Edad cumplida en una fecha.</summary>
    public static int AgeOn(DateOnly birthDate, DateOnly day)
    {
        var age = day.Year - birthDate.Year;
        return birthDate.AddYears(age) > day ? age - 1 : age;
    }

    /// <summary>Se reserva hasta <paramref name="minNoticeMinutes"/> antes del inicio y desde <paramref name="maxAdvanceDays"/> días antes.</summary>
    public static Error? BookingWindow(DateTimeOffset startsAt, int minNoticeMinutes, int maxAdvanceDays, DateTimeOffset now)
    {
        if (now < startsAt.AddDays(-maxAdvanceDays))
        {
            return ReservationErrors.BookingNotOpen;
        }

        return now > startsAt.AddMinutes(-minNoticeMinutes) ? ReservationErrors.BookingClosed : null;
    }

    /// <summary>¿Cancelar a tiempo? Hasta <paramref name="deadlineMinutes"/> antes del inicio (0 = hasta el inicio).</summary>
    public static bool CancellationOnTime(DateTimeOffset startsAt, int deadlineMinutes, DateTimeOffset now) =>
        now <= startsAt.AddMinutes(-deadlineMinutes);
}

public static class ReservationErrors
{
    public static readonly Error NotFound = Error.NotFound("classes.reservation_not_found", "Reserva no encontrada.");
    public static readonly Error MemberNotFound = Error.NotFound("classes.member_not_found", "Alumno no encontrado.");
    public static readonly Error AlreadyBooked = Error.Conflict("classes.already_booked", "El alumno ya tiene una reserva en esta sesión.");
    public static readonly Error SessionFull = Error.Conflict("classes.session_full", "La sesión está llena.");
    public static readonly Error SessionNotBookable = Error.Conflict("classes.session_not_bookable", "La sesión está cancelada, ya empezó o terminó: no admite reservas.");
    public static readonly Error BookingNotOpen = Error.Conflict("classes.booking_not_open", "Todavía no se abren las reservas de esta sesión.");
    public static readonly Error BookingClosed = Error.Conflict("classes.booking_closed", "Las reservas de esta sesión ya cerraron.");
    public static readonly Error ScheduleConflict = Error.Conflict("classes.member_schedule_conflict", "El alumno ya tiene otra reserva que se cruza con este horario.");
    public static readonly Error CancellationDeadlinePassed = Error.Conflict("classes.cancellation_deadline_passed", "Ya pasó el plazo para cancelar esta reserva.");
    public static readonly Error LateReasonRequired = Error.Validation("classes.late_cancellation_reason_required", "Cancelar fuera de plazo es una excepción: indique el motivo (al menos 5 caracteres).");
    public static readonly Error SessionStarted = Error.Conflict("classes.session_started", "La sesión ya empezó: la reserva no se puede cancelar.");
    public static readonly Error NotCancellable = Error.Conflict("classes.reservation_not_cancellable", "La reserva ya no está vigente.");
    public static readonly Error Changed = Error.Conflict("classes.reservation_changed", "La reserva cambió mientras la editaba: vuelva a cargarla.");

    public static readonly Error MemberInactive = Error.Conflict("classes.member_inactive", "El alumno está inactivo.");
    public static readonly Error NoActiveMembership = Error.Conflict("classes.no_active_membership", "El alumno no tiene una membresía activa que cubra la fecha de la sesión.");
    public static readonly Error MembershipFrozen = Error.Conflict("classes.membership_frozen", "La membresía del alumno está congelada.");
    public static readonly Error MembershipPendingPayment = Error.Conflict("classes.membership_pending_payment", "La membresía del alumno está pendiente de pago.");
    public static readonly Error PlanBranchRestricted = Error.Conflict("classes.plan_branch_restricted", "El plan del alumno no permite asistir a esta sucursal.");
    public static readonly Error DebtPending = Error.Conflict("classes.debt_pending", "El alumno tiene saldo pendiente y su gimnasio exige el pago completo.");
    public static readonly Error DebtOverdue = Error.Conflict("classes.debt_overdue", "El alumno tiene una cuota vencida sin pagar.");
    public static readonly Error MinimumPaymentNotMet = Error.Conflict("classes.minimum_payment_not_met", "El alumno no ha pagado el mínimo que exige su gimnasio.");
    public static readonly Error BelowMinAge = Error.Conflict("classes.below_min_age", "El alumno no cumple la edad mínima de esta clase.");
    public static readonly Error AgeUnknown = Error.Conflict("classes.member_age_unknown", "La clase exige edad mínima y el alumno no tiene fecha de nacimiento registrada.");
}
