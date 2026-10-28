using FitCore.SharedKernel;

namespace FitCore.Classes.Domain;

public enum SessionStatus
{
    Scheduled = 1,
    Cancelled = 2,
    Completed = 3
}

public enum SeriesStatus
{
    Active = 1,
    Cancelled = 2
}

/// <summary>
/// Una fecha y hora concreta de una clase. Es lo que el alumno reserva: la definición (<see cref="GroupClass"/>) solo dice qué se ofrece.
/// Nunca se borra: una sesión cancelada conserva su motivo, y su hueco en la serie, para que regenerarla no la resucite.
/// </summary>
public sealed class ClassSession : Entity<Guid>, ITenantOwned, IAuditable
{
    public const int MaxNotesLength = 500;
    public const int MinOverrideReasonLength = 5;
    public const int MaxOverrideReasonLength = 300;
    public const int MaxCancelReasonLength = 300;

    private ClassSession(Guid id)
        : base(id)
    {
    }

    public Guid TenantId { get; private set; }
    public Guid BranchId { get; private set; }
    public Guid ClassId { get; private set; }

    /// <summary>Serie que la generó (null si es una sesión suelta).</summary>
    public Guid? SeriesId { get; private set; }

    public Guid InstructorId { get; private set; }
    public DateTimeOffset StartsAt { get; private set; }
    public DateTimeOffset EndsAt { get; private set; }

    /// <summary>Instante que le tocaba en la serie. Hace idempotente la generación aunque la sesión se haya movido después.</summary>
    public DateTimeOffset OriginalStartsAt { get; private set; }

    public int Capacity { get; private set; }
    public SessionStatus Status { get; private set; } = SessionStatus.Scheduled;
    public string? Notes { get; private set; }
    public string? CancelReason { get; private set; }
    public DateTimeOffset? CancelledAt { get; private set; }
    public Guid? CancelledBy { get; private set; }

    /// <summary>Motivo de la excepción cuando se programó fuera de la disponibilidad declarada del instructor (queda auditada).</summary>
    public string? AvailabilityOverrideReason { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public Guid? CreatedBy { get; private set; }
    public DateTimeOffset? UpdatedAt { get; private set; }
    public Guid? UpdatedBy { get; private set; }

    public bool IsScheduled => Status == SessionStatus.Scheduled;

    /// <summary>¿Se movió o cambió de instructor respecto a lo que generó la serie?</summary>
    public bool IsModified => SeriesId is not null && StartsAt != OriginalStartsAt;

    public static ClassSession Create(
        Guid tenantId,
        Guid branchId,
        Guid classId,
        Guid? seriesId,
        Guid instructorId,
        DateTimeOffset startsAt,
        DateTimeOffset endsAt,
        int capacity,
        string? notes,
        string? availabilityOverrideReason,
        Guid? by,
        DateTimeOffset now) =>
        new(Guid.CreateVersion7())
        {
            TenantId = tenantId,
            BranchId = branchId,
            ClassId = classId,
            SeriesId = seriesId,
            InstructorId = instructorId,
            StartsAt = startsAt,
            EndsAt = endsAt,
            OriginalStartsAt = startsAt,
            Capacity = capacity,
            Notes = Clean(notes),
            AvailabilityOverrideReason = Clean(availabilityOverrideReason),
            CreatedAt = now,
            CreatedBy = by
        };

    /// <summary>Una sesión ya empezada, cancelada o completada no se reprograma ni se edita.</summary>
    public bool CanBeModified(DateTimeOffset now) => IsScheduled && StartsAt > now;

    public void Reschedule(DateTimeOffset startsAt, DateTimeOffset endsAt, Guid instructorId, int capacity, string? notes, string? availabilityOverrideReason, Guid? by, DateTimeOffset now)
    {
        StartsAt = startsAt;
        EndsAt = endsAt;
        InstructorId = instructorId;
        Capacity = capacity;
        Notes = Clean(notes);
        AvailabilityOverrideReason = Clean(availabilityOverrideReason);
        UpdatedAt = now;
        UpdatedBy = by;
    }

    public void Cancel(string reason, Guid? by, DateTimeOffset now)
    {
        Status = SessionStatus.Cancelled;
        CancelReason = reason.Trim();
        CancelledAt = now;
        CancelledBy = by;
        UpdatedAt = now;
        UpdatedBy = by;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// Regla de recurrencia semanal que <b>genera sesiones concretas</b>: la serie no se calcula al vuelo, cada fecha es una fila que se puede
/// reservar, mover o cancelar sin afectar a las demás. La hora es local de la sucursal (no cambia con el horario de verano).
/// </summary>
public sealed class ClassSeries : Entity<Guid>, ITenantOwned, IAuditable
{
    public const int MaxSpanDays = 366;
    public const int MaxSessionsPerRun = 200;

    private ClassSeries(Guid id)
        : base(id)
    {
    }

    public Guid TenantId { get; private set; }
    public Guid BranchId { get; private set; }
    public Guid ClassId { get; private set; }
    public Guid InstructorId { get; private set; }
    public int Capacity { get; private set; }
    public int DurationMinutes { get; private set; }
    public DateOnly StartDate { get; private set; }
    public DateOnly EndDate { get; private set; }

    /// <summary>Hora de inicio local de la sucursal.</summary>
    public TimeOnly StartTime { get; private set; }

    /// <summary>Un bit por día de la semana (bit 0 = domingo, igual que <see cref="DayOfWeek"/>).</summary>
    public int DaysMask { get; private set; }

    public SeriesStatus Status { get; private set; } = SeriesStatus.Active;
    public string? CancelReason { get; private set; }
    public DateTimeOffset? CancelledAt { get; private set; }
    public Guid? CancelledBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public Guid? CreatedBy { get; private set; }
    public DateTimeOffset? UpdatedAt { get; private set; }
    public Guid? UpdatedBy { get; private set; }

    public bool IsActive => Status == SeriesStatus.Active;

    public IReadOnlyList<DayOfWeek> Days => DaysFromMask(DaysMask);

    public static ClassSeries Create(
        Guid tenantId,
        Guid branchId,
        Guid classId,
        Guid instructorId,
        int capacity,
        int durationMinutes,
        DateOnly startDate,
        DateOnly endDate,
        TimeOnly startTime,
        IEnumerable<DayOfWeek> days,
        Guid? by,
        DateTimeOffset now) =>
        new(Guid.CreateVersion7())
        {
            TenantId = tenantId,
            BranchId = branchId,
            ClassId = classId,
            InstructorId = instructorId,
            Capacity = capacity,
            DurationMinutes = durationMinutes,
            StartDate = startDate,
            EndDate = endDate,
            StartTime = startTime,
            DaysMask = MaskOf(days),
            CreatedAt = now,
            CreatedBy = by
        };

    /// <summary>La serie solo se extiende: acortarla es cancelar las sesiones que sobran, una a una o con la serie.</summary>
    public void ExtendTo(DateOnly endDate, Guid? by, DateTimeOffset now)
    {
        EndDate = endDate;
        UpdatedAt = now;
        UpdatedBy = by;
    }

    public void Cancel(string reason, Guid? by, DateTimeOffset now)
    {
        Status = SeriesStatus.Cancelled;
        CancelReason = reason.Trim();
        CancelledAt = now;
        CancelledBy = by;
        UpdatedAt = now;
        UpdatedBy = by;
    }

    public static int MaskOf(IEnumerable<DayOfWeek> days) => days.Distinct().Aggregate(0, (mask, day) => mask | (1 << (int)day));

    public static IReadOnlyList<DayOfWeek> DaysFromMask(int mask) =>
        [.. Enum.GetValues<DayOfWeek>().Where(d => (mask & (1 << (int)d)) != 0).OrderBy(d => d == DayOfWeek.Sunday ? 7 : (int)d)];

    /// <summary>Fechas locales de la serie hasta <paramref name="until"/> (inclusive) que caen en los días elegidos.</summary>
    public IEnumerable<DateOnly> Dates(DateOnly until)
    {
        for (var date = StartDate; date <= until; date = date.AddDays(1))
        {
            if ((DaysMask & (1 << (int)date.DayOfWeek)) != 0)
            {
                yield return date;
            }
        }
    }

    /// <summary>
    /// Instante UTC de una fecha y hora locales de la sucursal, o null si esa hora no existe ese día (salto del horario de verano).
    /// Si la hora se repite (fin del horario de verano) se toma la primera.
    /// </summary>
    public static DateTimeOffset? ToUtc(DateOnly date, TimeOnly time, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
        {
            return null;
        }

        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
}

/// <summary>Franja semanal declarada por el instructor (vista de solo lectura del módulo de bienestar), en hora local.</summary>
public readonly record struct InstructorSlot(DayOfWeek Day, TimeOnly Start, TimeOnly End, Guid? BranchId);

public static class InstructorAvailabilityRules
{
    /// <summary>
    /// Sin franjas declaradas el instructor no tiene restricción. Con franjas, la sesión (en hora local de la sucursal) debe caber entera
    /// en una de ese día y de esa sucursal (o de cualquiera); una sesión que cruza la medianoche no cabe en ninguna.
    /// </summary>
    public static bool Fits(IReadOnlyCollection<InstructorSlot> slots, Guid branchId, DateTimeOffset startsAt, DateTimeOffset endsAt, TimeZoneInfo zone)
    {
        if (slots.Count == 0)
        {
            return true;
        }

        var start = TimeZoneInfo.ConvertTime(startsAt, zone);
        var end = TimeZoneInfo.ConvertTime(endsAt, zone);
        if (start.Date != end.Date)
        {
            return false;
        }

        var from = TimeOnly.FromDateTime(start.DateTime);
        var to = TimeOnly.FromDateTime(end.DateTime);
        return slots.Any(s => s.Day == start.DayOfWeek && (s.BranchId is null || s.BranchId == branchId) && s.Start <= from && to <= s.End);
    }
}

public static class SessionErrors
{
    public static readonly Error NotFound = Error.NotFound("classes.session_not_found", "Sesión no encontrada.");
    public static readonly Error SeriesNotFound = Error.NotFound("classes.series_not_found", "Serie no encontrada.");
    public static readonly Error ClassInactive = Error.Conflict("classes.class_inactive", "La clase está desactivada: reactívela para programar sesiones nuevas.");
    public static readonly Error InPast = Error.Validation("classes.session_in_past", "La sesión debe empezar en el futuro.");
    public static readonly Error NotModifiable = Error.Conflict("classes.session_not_modifiable", "La sesión ya empezó, se canceló o se completó: no se puede modificar.");
    public static readonly Error InstructorOverlap = Error.Conflict("classes.instructor_overlap", "El instructor ya tiene otra sesión en ese horario.");
    public static readonly Error InstructorUnavailable = Error.Conflict("classes.instructor_unavailable", "El horario queda fuera de la disponibilidad declarada del instructor. Si es una excepción, indique el motivo.");
    public static readonly Error Changed = Error.Conflict("classes.session_changed", "La sesión cambió mientras la editaba: vuelva a cargarla e inténtelo de nuevo.");
    public static readonly Error NothingToChange = Error.Validation("classes.session_nothing_to_change", "Indique al menos un dato a modificar.");
    public static readonly Error SeriesCancelled = Error.Conflict("classes.series_cancelled", "La serie está cancelada.");
    public static readonly Error SeriesRangeInvalid = Error.Validation("classes.series_range_invalid", "La fecha de fin debe ser posterior o igual a la de inicio y no superar 366 días.");
    public static readonly Error SeriesInPast = Error.Validation("classes.series_in_past", "La serie no puede empezar en una fecha pasada.");
    public static readonly Error SeriesShrink = Error.Validation("classes.series_cannot_shrink", "La serie solo se puede extender: para acortarla cancele las sesiones que sobran.");
    public static readonly Error SeriesTooManySessions = Error.Validation("classes.series_too_many_sessions", "La serie generaría demasiadas sesiones de una vez (máximo 200): acorte el rango.");
    public static readonly Error SeriesNoSessions = Error.Conflict("classes.series_no_sessions", "Ninguna fecha de la serie se pudo programar: revise los días, las fechas y los conflictos del instructor.");
}
