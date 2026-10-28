using FitCore.Application.Core;
using FitCore.SharedKernel;
using FitCore.Wellness.Domain;
using FluentValidation;

namespace FitCore.Wellness.Application.Goals;

/// <param name="BaselineValue">Opcional: si no se indica, se toma la última medición del alumno para esa medida.</param>
public sealed record GoalRequest(
    string Title,
    string? Description,
    GoalCategory Category,
    GoalMetric? Metric,
    decimal? TargetValue,
    decimal? BaselineValue,
    DateOnly? TargetDate);

public sealed record CloseGoalRequest(GoalStatus Status, string? Note);

/// <param name="CurrentValue">Último valor registrado (o sesiones de los últimos 7 días); nulo si aún no hay datos.</param>
/// <param name="ProgressPercent">Avance de la partida a la meta (0–100); nulo si falta partida, meta o dato actual.</param>
/// <param name="Reached">Si ya alcanzó la meta. El objetivo no se cierra solo: lo cierra una persona.</param>
public sealed record GoalDto(
    Guid Id,
    Guid MemberId,
    string Title,
    string? Description,
    string Category,
    string? Metric,
    string? Unit,
    decimal? TargetValue,
    decimal? BaselineValue,
    decimal? CurrentValue,
    DateOnly? CurrentAsOf,
    decimal? ProgressPercent,
    bool? Reached,
    DateOnly StartDate,
    DateOnly? TargetDate,
    bool Overdue,
    string Status,
    DateOnly? ClosedOn,
    string? CloseNote,
    DateTimeOffset CreatedAt);

public sealed record CreateGoalCommand(Guid MemberId, GoalRequest Request) : ICommand<GoalDto>;

public sealed record CloseGoalCommand(Guid Id, CloseGoalRequest Request) : ICommand<GoalDto>;

public sealed record ListGoalsQuery(Guid MemberId, GoalStatus? Status) : IQuery<IReadOnlyList<GoalDto>>;

public sealed class GoalRequestValidator : AbstractValidator<GoalRequest>
{
    public GoalRequestValidator()
    {
        RuleFor(x => x.Title).NotEmpty().MinimumLength(3).MaximumLength(Goal.MaxTitleLength);
        RuleFor(x => x.Description).MaximumLength(Goal.MaxTextLength);
        RuleFor(x => x.Category).IsInEnum();
        RuleFor(x => x.Metric).IsInEnum().When(x => x.Metric is not null);
    }
}

public sealed class CloseGoalRequestValidator : AbstractValidator<CloseGoalRequest>
{
    public CloseGoalRequestValidator()
    {
        RuleFor(x => x.Status).Must(s => s is GoalStatus.Achieved or GoalStatus.Abandoned).WithMessage("Un objetivo se cierra como alcanzado o abandonado.");
        RuleFor(x => x.Note).MaximumLength(Goal.MaxTextLength);
        RuleFor(x => x.Note).NotEmpty().MinimumLength(3).When(x => x.Status == GoalStatus.Abandoned).WithMessage("Indique el motivo al abandonar un objetivo.");
    }
}

public static class GoalErrors
{
    public static readonly Error MemberNotFound = Error.NotFound("goal.member_not_found", "Alumno no encontrado.");
    public static readonly Error MemberInactive = Error.Conflict("goal.member_inactive", "El alumno está inactivo.");
    public static readonly Error NotFound = Error.NotFound("goal.not_found", "Objetivo no encontrado.");
    public static readonly Error AlreadyClosed = Error.Conflict("goal.already_closed", "El objetivo ya está cerrado.");
    public static readonly Error TooManyActive = Error.Conflict("goal.too_many_active", "El alumno ya tiene el máximo de objetivos activos (5): cierre alguno antes de crear otro.");
    public static readonly Error DuplicateMetric = Error.Conflict("goal.duplicate_metric", "El alumno ya tiene un objetivo activo para esa medida: ciérrelo antes de crear otro.");
    public static readonly Error TargetUnsafe = Error.Validation("goal.target_unsafe", "No se registra una meta de peso que deje el IMC por debajo del rango de referencia para adultos (18,5).");
}
