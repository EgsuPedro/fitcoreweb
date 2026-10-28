using FitCore.SharedKernel;

namespace FitCore.Wellness.Domain;

public enum PlanStatus
{
    Active = 1,
    Archived = 2
}

/// <summary>
/// Rutina de entrenamiento. La cabecera solo apunta a su versión vigente: el contenido (días y ejercicios) vive en versiones inmutables,
/// así que modificar la rutina crea una versión nueva y nada de lo ya asignado o entrenado cambia.
/// </summary>
public sealed class TrainingPlan : Entity<Guid>, ITenantOwned, IAuditable
{
    private TrainingPlan(Guid id)
        : base(id)
    {
    }

    public Guid TenantId { get; private set; }
    public PlanStatus Status { get; private set; } = PlanStatus.Active;
    public int CurrentVersion { get; private set; }
    public Guid? CurrentVersionId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public Guid? CreatedBy { get; private set; }
    public DateTimeOffset? UpdatedAt { get; private set; }
    public Guid? UpdatedBy { get; private set; }

    public static TrainingPlan Create(Guid tenantId, Guid? createdBy, DateTimeOffset now) =>
        new(Guid.CreateVersion7()) { TenantId = tenantId, CreatedAt = now, CreatedBy = createdBy };

    /// <summary>Registra la versión vigente (se llama al crear la rutina y cada vez que se modifica).</summary>
    public void PublishVersion(TrainingPlanVersion version, Guid? by, DateTimeOffset now)
    {
        CurrentVersion = version.Number;
        CurrentVersionId = version.Id;
        UpdatedAt = now;
        UpdatedBy = by;
    }

    public void SetStatus(PlanStatus status, Guid? by, DateTimeOffset now)
    {
        Status = status;
        UpdatedAt = now;
        UpdatedBy = by;
    }
}

/// <summary>Contenido inmutable de una rutina en un momento dado.</summary>
public sealed class TrainingPlanVersion : Entity<Guid>, ITenantOwned
{
    public const int MaxDays = 14;
    public const int MaxExercisesPerDay = 30;

    private TrainingPlanVersion(Guid id)
        : base(id)
    {
    }

    public Guid TenantId { get; private set; }
    public Guid PlanId { get; private set; }
    public int Number { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string? Goal { get; private set; }
    public string? Description { get; private set; }
    public Difficulty Level { get; private set; }
    public int? EstimatedMinutes { get; private set; }
    public DateOnly? ValidFrom { get; private set; }
    public DateOnly? ValidUntil { get; private set; }
    public string? ChangeNote { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public Guid? CreatedBy { get; private set; }

    public static TrainingPlanVersion Create(Guid tenantId, Guid planId, int number, PlanHeader header, string? changeNote, Guid? createdBy, DateTimeOffset now) =>
        new(Guid.CreateVersion7())
        {
            TenantId = tenantId,
            PlanId = planId,
            Number = number,
            Name = header.Name.Trim(),
            Goal = Clean(header.Goal),
            Description = Clean(header.Description),
            Level = header.Level,
            EstimatedMinutes = header.EstimatedMinutes,
            ValidFrom = header.ValidFrom,
            ValidUntil = header.ValidUntil,
            ChangeNote = Clean(changeNote),
            CreatedAt = now,
            CreatedBy = createdBy
        };

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record PlanHeader(string Name, string? Goal, string? Description, Difficulty Level, int? EstimatedMinutes, DateOnly? ValidFrom, DateOnly? ValidUntil);

public sealed class TrainingPlanDay : Entity<Guid>, ITenantOwned
{
    private TrainingPlanDay(Guid id)
        : base(id)
    {
    }

    public Guid TenantId { get; private set; }
    public Guid VersionId { get; private set; }

    /// <summary>Orden del día dentro de la rutina (1, 2, 3…).</summary>
    public int Position { get; private set; }

    public string Name { get; private set; } = string.Empty;
    public string? Notes { get; private set; }

    public static TrainingPlanDay Create(Guid tenantId, Guid versionId, int position, string name, string? notes) =>
        new(Guid.CreateVersion7()) { TenantId = tenantId, VersionId = versionId, Position = position, Name = name.Trim(), Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim() };
}

/// <summary>
/// Ejercicio prescrito en un día. Guarda una copia del nombre y del modo de registro del ejercicio en el momento de prescribirlo:
/// aunque la biblioteca cambie, la rutina y su historial se siguen leyendo igual.
/// </summary>
public sealed class TrainingPlanExercise : Entity<Guid>, ITenantOwned
{
    private TrainingPlanExercise(Guid id)
        : base(id)
    {
    }

    public Guid TenantId { get; private set; }
    public Guid DayId { get; private set; }
    public Guid ExerciseId { get; private set; }
    public int Position { get; private set; }
    public string ExerciseName { get; private set; } = string.Empty;
    public TrackingMode TrackingMode { get; private set; }
    public int Sets { get; private set; }
    public int? Reps { get; private set; }
    public int? DurationSeconds { get; private set; }
    public int RestSeconds { get; private set; }
    public decimal? RecommendedLoadKg { get; private set; }
    public string? Notes { get; private set; }

    public static TrainingPlanExercise Create(
        Guid tenantId,
        Guid dayId,
        Guid exerciseId,
        int position,
        string exerciseName,
        TrackingMode trackingMode,
        Prescription prescription) =>
        new(Guid.CreateVersion7())
        {
            TenantId = tenantId,
            DayId = dayId,
            ExerciseId = exerciseId,
            Position = position,
            ExerciseName = exerciseName,
            TrackingMode = trackingMode,
            Sets = prescription.Sets,
            Reps = prescription.Reps,
            DurationSeconds = prescription.DurationSeconds,
            RestSeconds = prescription.RestSeconds,
            RecommendedLoadKg = prescription.RecommendedLoadKg,
            Notes = string.IsNullOrWhiteSpace(prescription.Notes) ? null : prescription.Notes.Trim()
        };
}

public sealed record Prescription(int Sets, int? Reps, int? DurationSeconds, int RestSeconds, decimal? RecommendedLoadKg, string? Notes);

/// <summary>Rutina asignada a un alumno, fijada a una versión concreta. Cambiar de rutina cierra la anterior; nada se borra.</summary>
public sealed class TrainingAssignment : Entity<Guid>, ITenantOwned, IAuditable
{
    public const int MaxDurationDays = 730;

    private TrainingAssignment(Guid id)
        : base(id)
    {
    }

    public Guid TenantId { get; private set; }
    public Guid PlanId { get; private set; }
    public Guid PlanVersionId { get; private set; }
    public Guid MemberId { get; private set; }
    public Guid BranchId { get; private set; }
    public DateOnly StartDate { get; private set; }

    /// <summary>Fin programado (opcional).</summary>
    public DateOnly? EndDate { get; private set; }

    /// <summary>
    /// Cierre explícito (cambio de rutina o decisión del entrenador): el ÚLTIMO día en que la asignación rige; nulo mientras no se haya
    /// cerrado. Puede ser el día anterior al inicio (una asignación que se cerró antes de empezar nunca rigió).
    /// </summary>
    public DateOnly? EndedOn { get; private set; }

    public string? EndReason { get; private set; }
    public Guid AssignedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Último día en que la asignación rige (el menor entre el fin programado y el cierre), o nulo si no tiene fin.</summary>
    public DateOnly? LastDay => (EndDate, EndedOn) switch
    {
        ({ } scheduled, { } closed) => scheduled < closed ? scheduled : closed,
        ({ } scheduled, null) => scheduled,
        (null, { } closed) => closed,
        _ => null
    };

    public bool IsCurrentOn(DateOnly day) => StartDate <= day && (LastDay is not { } last || last >= day);

    public static TrainingAssignment Create(
        Guid tenantId,
        Guid planId,
        Guid planVersionId,
        Guid memberId,
        Guid branchId,
        DateOnly startDate,
        DateOnly? endDate,
        Guid assignedBy,
        DateTimeOffset now) =>
        new(Guid.CreateVersion7())
        {
            TenantId = tenantId,
            PlanId = planId,
            PlanVersionId = planVersionId,
            MemberId = memberId,
            BranchId = branchId,
            StartDate = startDate,
            EndDate = endDate,
            AssignedBy = assignedBy,
            CreatedAt = now
        };

    public void End(DateOnly day, string reason)
    {
        EndedOn = day;
        EndReason = reason.Trim();
    }
}

/// <summary>Sesión de entrenamiento realizada. Guarda la versión de la rutina con la que se hizo: editar la rutina después no la cambia.</summary>
public sealed class WorkoutSession : Entity<Guid>, ITenantOwned
{
    public const int MaxNotesLength = 1000;

    private WorkoutSession(Guid id)
        : base(id)
    {
    }

    public Guid TenantId { get; private set; }
    public Guid MemberId { get; private set; }
    public Guid AssignmentId { get; private set; }
    public Guid PlanVersionId { get; private set; }
    public Guid DayId { get; private set; }
    public DateOnly PerformedOn { get; private set; }
    public int? DurationMinutes { get; private set; }
    public string? MemberNotes { get; private set; }
    public string? TrainerComment { get; private set; }
    public DateTimeOffset? TrainerCommentAt { get; private set; }
    public Guid RecordedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static WorkoutSession Create(
        Guid tenantId,
        Guid memberId,
        Guid assignmentId,
        Guid planVersionId,
        Guid dayId,
        DateOnly performedOn,
        int? durationMinutes,
        string? memberNotes,
        Guid recordedBy,
        DateTimeOffset now) =>
        new(Guid.CreateVersion7())
        {
            TenantId = tenantId,
            MemberId = memberId,
            AssignmentId = assignmentId,
            PlanVersionId = planVersionId,
            DayId = dayId,
            PerformedOn = performedOn,
            DurationMinutes = durationMinutes,
            MemberNotes = string.IsNullOrWhiteSpace(memberNotes) ? null : memberNotes.Trim(),
            RecordedBy = recordedBy,
            CreatedAt = now
        };

    /// <summary>El entrenador agrega su comentario (una sola vez por sesión; el comentario no se reescribe).</summary>
    public void AddTrainerComment(string comment, DateTimeOffset now)
    {
        TrainerComment = comment.Trim();
        TrainerCommentAt = now;
    }
}

public sealed class WorkoutSessionExercise : Entity<Guid>, ITenantOwned
{
    private WorkoutSessionExercise(Guid id)
        : base(id)
    {
    }

    public Guid TenantId { get; private set; }
    public Guid SessionId { get; private set; }
    public Guid PlanExerciseId { get; private set; }
    public Guid ExerciseId { get; private set; }
    public string ExerciseName { get; private set; } = string.Empty;
    public TrackingMode TrackingMode { get; private set; }
    public bool Completed { get; private set; }
    public string? Notes { get; private set; }

    public static WorkoutSessionExercise Create(
        Guid tenantId,
        Guid sessionId,
        Guid planExerciseId,
        Guid exerciseId,
        string exerciseName,
        TrackingMode trackingMode,
        bool completed,
        string? notes) =>
        new(Guid.CreateVersion7())
        {
            TenantId = tenantId,
            SessionId = sessionId,
            PlanExerciseId = planExerciseId,
            ExerciseId = exerciseId,
            ExerciseName = exerciseName,
            TrackingMode = trackingMode,
            Completed = completed,
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim()
        };
}

/// <summary>Serie realizada: lo que el alumno hizo de verdad (repeticiones, tiempo o carga reales).</summary>
public sealed class WorkoutSet : Entity<Guid>, ITenantOwned
{
    private WorkoutSet(Guid id)
        : base(id)
    {
    }

    public Guid TenantId { get; private set; }
    public Guid SessionExerciseId { get; private set; }
    public int SetNumber { get; private set; }
    public int? Reps { get; private set; }
    public int? DurationSeconds { get; private set; }
    public decimal? LoadKg { get; private set; }

    public static WorkoutSet Create(Guid tenantId, Guid sessionExerciseId, int setNumber, ActualSet actual) =>
        new(Guid.CreateVersion7())
        {
            TenantId = tenantId,
            SessionExerciseId = sessionExerciseId,
            SetNumber = setNumber,
            Reps = actual.Reps,
            DurationSeconds = actual.DurationSeconds,
            LoadKg = actual.LoadKg
        };
}

public sealed record ActualSet(int? Reps, int? DurationSeconds, decimal? LoadKg);

/// <summary>Coherencia de lo prescrito y de lo realizado según cómo se registra cada ejercicio.</summary>
public static class TrainingRules
{
    public const int MaxSets = 20;
    public const int MaxReps = 200;
    public const int MaxRestSeconds = 1800;
    public const int MaxDurationSeconds = 14400;
    public const decimal MaxLoadKg = 1000m;

    /// <summary>Código del primer problema de una prescripción, o null si es coherente con el modo de registro del ejercicio.</summary>
    public static string? ValidatePrescription(TrackingMode mode, Prescription p)
    {
        if (p.Sets is < 1 or > MaxSets)
        {
            return "training.sets_invalid";
        }

        if (p.RestSeconds is < 0 or > MaxRestSeconds)
        {
            return "training.rest_invalid";
        }

        switch (mode)
        {
            case TrackingMode.RepsAndLoad:
                if (p.Reps is not (>= 1 and <= MaxReps) || p.DurationSeconds is not null)
                {
                    return "training.prescription_reps_required";
                }

                break;
            case TrackingMode.Reps:
                if (p.Reps is not (>= 1 and <= MaxReps) || p.DurationSeconds is not null)
                {
                    return "training.prescription_reps_required";
                }

                if (p.RecommendedLoadKg is not null)
                {
                    return "training.load_not_applicable";
                }

                break;
            case TrackingMode.Duration:
                if (p.DurationSeconds is not (>= 5 and <= MaxDurationSeconds) || p.Reps is not null)
                {
                    return "training.prescription_duration_required";
                }

                if (p.RecommendedLoadKg is not null)
                {
                    return "training.load_not_applicable";
                }

                break;
        }

        return p.RecommendedLoadKg is < 0 or > MaxLoadKg ? "training.load_invalid" : null;
    }

    /// <summary>Código del primer problema de una serie realizada, o null si es coherente con el modo de registro.</summary>
    public static string? ValidateActualSet(TrackingMode mode, ActualSet s)
    {
        switch (mode)
        {
            case TrackingMode.RepsAndLoad:
                if (s.Reps is not (>= 1 and <= MaxReps) || s.DurationSeconds is not null)
                {
                    return "training.set_reps_required";
                }

                break;
            case TrackingMode.Reps:
                if (s.Reps is not (>= 1 and <= MaxReps) || s.DurationSeconds is not null)
                {
                    return "training.set_reps_required";
                }

                if (s.LoadKg is not null)
                {
                    return "training.load_not_applicable";
                }

                break;
            case TrackingMode.Duration:
                if (s.DurationSeconds is not (>= 1 and <= MaxDurationSeconds) || s.Reps is not null)
                {
                    return "training.set_duration_required";
                }

                if (s.LoadKg is not null)
                {
                    return "training.load_not_applicable";
                }

                break;
        }

        return s.LoadKg is < 0 or > MaxLoadKg ? "training.load_invalid" : null;
    }

    /// <summary>Volumen de una serie en kg (repeticiones × carga); sin carga o sin repeticiones no suma.</summary>
    public static decimal Volume(ActualSet s) => s.Reps is { } reps && s.LoadKg is { } load ? reps * load : 0m;
}
