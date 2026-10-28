using FitCore.SharedKernel;

namespace FitCore.Classes.Domain;

/// <summary>Resultado de una reserva confirmada una vez que la sesión empezó.</summary>
public enum AttendanceOutcome
{
    Attended = 1,
    NoShow = 2
}

/// <summary>
/// Asistencia a una clase: el resultado (asistió o no asistió) de <b>una</b> reserva confirmada. Es un registro propio y distinto de la reserva y
/// de la entrada al gimnasio (<c>access.attendance</c>, que no se toca): la reserva dice que el alumno tenía cupo, la asistencia dice qué pasó.
/// Hay a lo sumo uno por reserva, así que registrar dos veces no duplica; corregirlo cambia el resultado y deja la traza en la auditoría.
/// </summary>
public sealed class ClassAttendance : Entity<Guid>, ITenantOwned, IAuditable
{
    public const int MaxNotesLength = 300;

    private ClassAttendance(Guid id)
        : base(id)
    {
    }

    public Guid TenantId { get; private set; }
    public Guid BranchId { get; private set; }
    public Guid SessionId { get; private set; }
    public Guid ReservationId { get; private set; }
    public Guid MemberId { get; private set; }
    public AttendanceOutcome Outcome { get; private set; }
    public string? Notes { get; private set; }

    /// <summary>
    /// Entrada al gimnasio del alumno que coincide con la sesión (misma sucursal, mientras duraba), solo informativa: sirve para contrastar,
    /// no se escribe ni se modifica nada del módulo de asistencia general.
    /// </summary>
    public Guid? GymEntryId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public Guid? CreatedBy { get; private set; }
    public DateTimeOffset? UpdatedAt { get; private set; }
    public Guid? UpdatedBy { get; private set; }

    public static ClassAttendance Record(
        Guid tenantId,
        Guid branchId,
        Guid sessionId,
        Guid reservationId,
        Guid memberId,
        AttendanceOutcome outcome,
        string? notes,
        Guid? gymEntryId,
        Guid? by,
        DateTimeOffset now) =>
        new(Guid.CreateVersion7())
        {
            TenantId = tenantId,
            BranchId = branchId,
            SessionId = sessionId,
            ReservationId = reservationId,
            MemberId = memberId,
            Outcome = outcome,
            Notes = Clean(notes),
            GymEntryId = outcome == AttendanceOutcome.Attended ? gymEntryId : null,
            CreatedAt = now,
            CreatedBy = by
        };

    /// <summary>Corrige un registro equivocado. Devuelve false si no cambia nada (repetir lo mismo no ensucia la auditoría).</summary>
    public bool Correct(AttendanceOutcome outcome, string? notes, Guid? gymEntryId, Guid? by, DateTimeOffset now)
    {
        var newNotes = Clean(notes);
        var newGymEntry = outcome == AttendanceOutcome.Attended ? gymEntryId : null;
        if (Outcome == outcome && Notes == newNotes && GymEntryId == newGymEntry)
        {
            return false;
        }

        Outcome = outcome;
        Notes = newNotes;
        GymEntryId = newGymEntry;
        UpdatedAt = now;
        UpdatedBy = by;
        return true;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public static class ClassAttendanceErrors
{
    public static readonly Error SessionNotStarted = Error.Conflict("classes.session_not_started", "La sesión todavía no empieza: la asistencia se registra desde su hora de inicio.");
    public static readonly Error SessionCancelled = Error.Conflict("classes.session_cancelled", "La sesión está cancelada: no tiene asistencia.");
    public static readonly Error ReservationNotInSession = Error.Validation("classes.reservation_not_in_session", "Alguna de las reservas no pertenece a esta sesión.");
    public static readonly Error ReservationNotAttendable = Error.Conflict("classes.reservation_not_attendable", "Solo las reservas confirmadas tienen asistencia: alguna está en espera, ofrecida o cancelada.");
    public static readonly Error DuplicateEntries = Error.Validation("classes.attendance_duplicate_entries", "La misma reserva aparece más de una vez en la solicitud.");
}
