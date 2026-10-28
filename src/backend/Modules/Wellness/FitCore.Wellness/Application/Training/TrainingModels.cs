using FitCore.Application.Core;
using FitCore.SharedKernel;
using FitCore.Wellness.Domain;
using FluentValidation;

namespace FitCore.Wellness.Application.Training;

// ---- Rutinas -------------------------------------------------------------------------------------------------------

public sealed record PlanSummaryDto(
    Guid Id,
    string Name,
    string? Goal,
    string Level,
    int? EstimatedMinutes,
    string Status,
    int Version,
    DateOnly? ValidFrom,
    DateOnly? ValidUntil,
    int DayCount,
    int ExerciseCount,
    int ActiveAssignments,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);

public sealed record PlanExerciseDto(
    Guid Id,
    Guid ExerciseId,
    int Position,
    string ExerciseName,
    string TrackingMode,
    int Sets,
    int? Reps,
    int? DurationSeconds,
    int RestSeconds,
    decimal? RecommendedLoadKg,
    string? Notes);

public sealed record PlanDayDto(Guid Id, int Position, string Name, string? Notes, IReadOnlyList<PlanExerciseDto> Exercises);

public sealed record PlanDetailDto(
    Guid Id,
    string Status,
    int Version,
    Guid VersionId,
    bool IsCurrentVersion,
    string Name,
    string? Goal,
    string? Description,
    string Level,
    int? EstimatedMinutes,
    DateOnly? ValidFrom,
    DateOnly? ValidUntil,
    string? ChangeNote,
    IReadOnlyList<PlanDayDto> Days,
    DateTimeOffset VersionCreatedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);

public sealed record PlanVersionDto(Guid Id, int Number, string Name, string? ChangeNote, DateTimeOffset CreatedAt, Guid? CreatedBy);

public sealed record PlanExerciseRequest(Guid ExerciseId, int Sets, int? Reps, int? DurationSeconds, int RestSeconds, decimal? RecommendedLoadKg, string? Notes);

public sealed record PlanDayRequest(string Name, string? Notes, IReadOnlyList<PlanExerciseRequest>? Exercises);

public sealed record TrainingPlanRequest(
    string Name,
    string? Goal,
    string? Description,
    Difficulty Level,
    int? EstimatedMinutes,
    DateOnly? ValidFrom,
    DateOnly? ValidUntil,
    string? ChangeNote,
    IReadOnlyList<PlanDayRequest>? Days);

public sealed record SetPlanStatusRequest(bool Active);

public sealed record ListPlansQuery(PageRequest Page, string? Search, PlanStatus? Status, Difficulty? Level) : IQuery<PagedResult<PlanSummaryDto>>;

public sealed record GetPlanQuery(Guid Id, int? Version) : IQuery<PlanDetailDto>;

public sealed record ListPlanVersionsQuery(Guid Id) : IQuery<IReadOnlyList<PlanVersionDto>>;

public sealed record CreatePlanCommand(TrainingPlanRequest Request) : ICommand<PlanDetailDto>;

/// <summary>Modificar una rutina crea una versión nueva; las asignaciones y sesiones anteriores conservan la suya.</summary>
public sealed record UpdatePlanCommand(Guid Id, TrainingPlanRequest Request) : ICommand<PlanDetailDto>;

public sealed record DuplicatePlanCommand(Guid Id) : ICommand<PlanDetailDto>;

public sealed record SetPlanStatusCommand(Guid Id, SetPlanStatusRequest Request) : ICommand<PlanSummaryDto>;

// ---- Asignaciones --------------------------------------------------------------------------------------------------

public sealed record AssignPlanRequest(Guid MemberId, DateOnly? StartDate, DateOnly? EndDate);

public sealed record EndAssignmentRequest(string Reason);

public sealed record TrainingAssignmentDto(
    Guid Id,
    Guid PlanId,
    Guid PlanVersionId,
    string PlanName,
    int PlanVersion,
    Guid MemberId,
    string MemberName,
    string MemberCode,
    DateOnly StartDate,
    DateOnly? EndDate,
    DateOnly? EndedOn,
    string? EndReason,
    string Status);

public sealed record ExerciseGuideDto(
    Guid Id,
    string Name,
    string? Description,
    string PrimaryMuscle,
    string Equipment,
    IReadOnlyList<string> Instructions,
    IReadOnlyList<string> CommonMistakes,
    string? Warnings,
    string? VideoUrl);

public sealed record MemberPlanExerciseDto(PlanExerciseDto Prescription, ExerciseGuideDto? Guide);

public sealed record MemberPlanDayDto(Guid Id, int Position, string Name, string? Notes, IReadOnlyList<MemberPlanExerciseDto> Exercises);

public sealed record MemberTrainingPlanDto(
    TrainingAssignmentDto Assignment,
    string? Goal,
    string? Description,
    string Level,
    int? EstimatedMinutes,
    IReadOnlyList<MemberPlanDayDto> Days);

public sealed record AssignPlanCommand(Guid PlanId, AssignPlanRequest Request) : ICommand<TrainingAssignmentDto>;

public sealed record EndAssignmentCommand(Guid Id, EndAssignmentRequest Request) : ICommand<TrainingAssignmentDto>;

public sealed record GetMemberPlanQuery(Guid MemberId) : IQuery<MemberTrainingPlanDto>;

public sealed record ListMemberTrainingAssignmentsQuery(Guid MemberId) : IQuery<IReadOnlyList<TrainingAssignmentDto>>;

// ---- Sesiones ------------------------------------------------------------------------------------------------------

public sealed record SetRequest(int? Reps, int? DurationSeconds, decimal? LoadKg);

public sealed record SessionExerciseRequest(Guid PlanExerciseId, bool Completed, string? Notes, IReadOnlyList<SetRequest>? Sets);

/// <param name="MemberId">Solo lo usa el personal; un alumno siempre registra la suya (se ignora y, si no es la suya, se rechaza).</param>
public sealed record CreateWorkoutSessionRequest(
    Guid? MemberId,
    Guid DayId,
    DateOnly? PerformedOn,
    int? DurationMinutes,
    string? Notes,
    IReadOnlyList<SessionExerciseRequest>? Exercises);

public sealed record TrainerCommentRequest(string Comment);

public sealed record SetDto(int SetNumber, int? Reps, int? DurationSeconds, decimal? LoadKg);

public sealed record SessionExerciseDto(
    Guid PlanExerciseId,
    Guid ExerciseId,
    string ExerciseName,
    string TrackingMode,
    bool Completed,
    string? Notes,
    int PlannedSets,
    int? PlannedReps,
    int? PlannedDurationSeconds,
    decimal? PlannedLoadKg,
    IReadOnlyList<SetDto> Sets);

public sealed record WorkoutSessionDto(
    Guid Id,
    Guid MemberId,
    DateOnly PerformedOn,
    Guid DayId,
    string DayName,
    Guid PlanId,
    string PlanName,
    int PlanVersion,
    int? DurationMinutes,
    string? MemberNotes,
    string? TrainerComment,
    DateTimeOffset? TrainerCommentAt,
    int CompletedExercises,
    int TotalExercises,
    int TotalSets,
    decimal VolumeKg,
    IReadOnlyList<SessionExerciseDto> Exercises);

public sealed record ExerciseTotalsDto(int Sets, int TotalReps, int TotalDurationSeconds, decimal? MaxLoadKg, decimal VolumeKg);

public sealed record ExerciseComparisonDto(
    Guid ExerciseId,
    string ExerciseName,
    string TrackingMode,
    ExerciseTotalsDto? A,
    ExerciseTotalsDto? B,
    decimal VolumeDeltaKg,
    decimal? MaxLoadDeltaKg,
    int RepsDelta,
    int DurationDeltaSeconds);

public sealed record SessionComparisonDto(WorkoutSessionDto A, WorkoutSessionDto B, IReadOnlyList<ExerciseComparisonDto> Exercises);

public sealed record ProgressPointDto(Guid SessionId, DateOnly Date, decimal? MaxLoadKg, decimal VolumeKg, int TotalReps, int TotalDurationSeconds);

public sealed record ExerciseProgressDto(Guid ExerciseId, string ExerciseName, string TrackingMode, IReadOnlyList<ProgressPointDto> Points);

public sealed record CreateWorkoutSessionCommand(CreateWorkoutSessionRequest Request) : ICommand<WorkoutSessionDto>;

public sealed record AddTrainerCommentCommand(Guid SessionId, TrainerCommentRequest Request) : ICommand<WorkoutSessionDto>;

public sealed record ListMemberSessionsQuery(Guid MemberId, DateOnly? From, DateOnly? To, PageRequest Page) : IQuery<PagedResult<WorkoutSessionDto>>;

public sealed record GetWorkoutSessionQuery(Guid Id) : IQuery<WorkoutSessionDto>;

public sealed record CompareSessionsQuery(Guid MemberId, Guid SessionA, Guid SessionB) : IQuery<SessionComparisonDto>;

public sealed record ExerciseProgressQuery(Guid MemberId, Guid ExerciseId, DateOnly? From, DateOnly? To) : IQuery<ExerciseProgressDto>;

// ---- Validación ----------------------------------------------------------------------------------------------------

public sealed class TrainingPlanRequestValidator : AbstractValidator<TrainingPlanRequest>
{
    public TrainingPlanRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MinimumLength(2).MaximumLength(120);
        RuleFor(x => x.Goal).MaximumLength(300);
        RuleFor(x => x.Description).MaximumLength(2000);
        RuleFor(x => x.ChangeNote).MaximumLength(300);
        RuleFor(x => x.Level).IsInEnum();
        RuleFor(x => x.EstimatedMinutes).InclusiveBetween(5, 480).When(x => x.EstimatedMinutes is not null);
        RuleFor(x => x.ValidUntil).GreaterThanOrEqualTo(x => x.ValidFrom!.Value).When(x => x.ValidFrom is not null && x.ValidUntil is not null)
            .WithMessage("La vigencia no puede terminar antes de empezar.");
        RuleFor(x => x.Days).NotEmpty().WithMessage("La rutina necesita al menos un día de entrenamiento.");
        RuleFor(x => x.Days).Must(d => d is null || d.Count <= TrainingPlanVersion.MaxDays).WithMessage($"Máximo {TrainingPlanVersion.MaxDays} días.");
        RuleForEach(x => x.Days).ChildRules(day =>
        {
            day.RuleFor(d => d.Name).NotEmpty().MaximumLength(120);
            day.RuleFor(d => d.Notes).MaximumLength(500);
            day.RuleFor(d => d.Exercises).NotEmpty().WithMessage("Cada día necesita al menos un ejercicio.");
            day.RuleFor(d => d.Exercises).Must(e => e is null || e.Count <= TrainingPlanVersion.MaxExercisesPerDay)
                .WithMessage($"Máximo {TrainingPlanVersion.MaxExercisesPerDay} ejercicios por día.");
            day.RuleForEach(d => d.Exercises).ChildRules(ex =>
            {
                ex.RuleFor(e => e.ExerciseId).NotEmpty();
                ex.RuleFor(e => e.Notes).MaximumLength(500);
            });
        });
    }
}

public sealed class SetPlanStatusRequestValidator : AbstractValidator<SetPlanStatusRequest>;

public sealed class AssignPlanRequestValidator : AbstractValidator<AssignPlanRequest>
{
    public AssignPlanRequestValidator()
    {
        RuleFor(x => x.MemberId).NotEmpty();
    }
}

public sealed class EndAssignmentRequestValidator : AbstractValidator<EndAssignmentRequest>
{
    public EndAssignmentRequestValidator()
    {
        RuleFor(x => x.Reason).NotEmpty().MinimumLength(3).MaximumLength(300);
    }
}

public sealed class CreateWorkoutSessionRequestValidator : AbstractValidator<CreateWorkoutSessionRequest>
{
    public CreateWorkoutSessionRequestValidator()
    {
        RuleFor(x => x.DayId).NotEmpty();
        RuleFor(x => x.DurationMinutes).InclusiveBetween(1, 600).When(x => x.DurationMinutes is not null);
        RuleFor(x => x.Notes).MaximumLength(WorkoutSession.MaxNotesLength);
        RuleFor(x => x.Exercises).NotEmpty().WithMessage("Registre al menos un ejercicio.");
        RuleForEach(x => x.Exercises).ChildRules(ex =>
        {
            ex.RuleFor(e => e.PlanExerciseId).NotEmpty();
            ex.RuleFor(e => e.Notes).MaximumLength(500);
            ex.RuleFor(e => e.Sets).Must(s => s is null || s.Count <= TrainingRules.MaxSets).WithMessage($"Máximo {TrainingRules.MaxSets} series por ejercicio.");
        });
    }
}

public sealed class TrainerCommentRequestValidator : AbstractValidator<TrainerCommentRequest>
{
    public TrainerCommentRequestValidator()
    {
        RuleFor(x => x.Comment).NotEmpty().MaximumLength(WorkoutSession.MaxNotesLength);
    }
}

public static class TrainingErrors
{
    public static readonly Error PlanNotFound = Error.NotFound("training.plan_not_found", "Rutina no encontrada.");
    public static readonly Error PlanArchived = Error.Conflict("training.plan_archived", "La rutina está archivada: actívela para modificarla o asignarla.");
    public static readonly Error PlanExpired = Error.Conflict("training.plan_expired", "La vigencia de la rutina ya terminó.");
    public static readonly Error VersionNotFound = Error.NotFound("training.version_not_found", "Versión de la rutina no encontrada.");
    public static readonly Error ExerciseNotFound = Error.Validation("training.exercise_not_found", "Alguno de los ejercicios no existe en la biblioteca.");
    public static readonly Error ExerciseInactive = Error.Validation("training.exercise_inactive", "Alguno de los ejercicios está desactivado y no puede agregarse a una rutina nueva.");
    public static readonly Error DuplicateExercise = Error.Validation("training.duplicate_exercise", "Un ejercicio no puede repetirse dentro del mismo día.");
    public static readonly Error MemberNotFound = Error.NotFound("training.member_not_found", "Alumno no encontrado.");
    public static readonly Error MemberInactive = Error.Conflict("training.member_inactive", "El alumno está inactivo.");
    public static readonly Error NoActivePlan = Error.NotFound("training.no_active_plan", "El alumno no tiene una rutina vigente.");
    public static readonly Error AssignmentNotFound = Error.NotFound("training.assignment_not_found", "Asignación no encontrada.");
    public static readonly Error AssignmentEnded = Error.Conflict("training.assignment_ended", "La asignación ya está cerrada.");
    public static readonly Error InvalidDates = Error.Validation("training.dates_invalid", "Las fechas no son válidas: el inicio no puede ser anterior a hoy ni el fin anterior al inicio.");
    public static readonly Error SessionNotFound = Error.NotFound("training.session_not_found", "Sesión no encontrada.");
    public static readonly Error SessionDateInvalid = Error.Validation("training.session_date_invalid", "La fecha de la sesión no puede ser futura, anterior a 30 días ni fuera de la vigencia de la rutina.");
    public static readonly Error DayNotInPlan = Error.Validation("training.day_not_in_plan", "El día no pertenece a la rutina asignada.");
    public static readonly Error ExerciseNotInDay = Error.Validation("training.exercise_not_in_day", "Alguno de los ejercicios no pertenece a ese día de la rutina.");
    public static readonly Error SessionEmpty = Error.Validation("training.session_empty", "Marque al menos un ejercicio como completado.");
    public static readonly Error CompletedWithoutSets = Error.Validation("training.completed_without_sets", "Un ejercicio completado necesita al menos una serie registrada.");
    public static readonly Error OtherMember = Error.Forbidden("training.other_member", "Solo puede registrar sus propias sesiones.");
    public static readonly Error CommentExists = Error.Conflict("training.comment_exists", "La sesión ya tiene comentario del entrenador.");
    public static readonly Error CommentNotAllowed = Error.Forbidden("training.comment_not_allowed", "Solo el entrenador puede comentar la sesión de un alumno.");
    public static readonly Error CompareDifferentMembers = Error.Validation("training.compare_invalid", "Las dos sesiones deben ser del mismo alumno.");
    public static readonly Error ProgressExerciseNotFound = Error.NotFound("training.progress_exercise_not_found", "El alumno no ha entrenado ese ejercicio.");
}
