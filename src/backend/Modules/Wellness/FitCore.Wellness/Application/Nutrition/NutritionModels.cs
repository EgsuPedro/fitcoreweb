using FitCore.Application.Core;
using FitCore.SharedKernel;
using FitCore.Wellness.Domain;
using FluentValidation;

namespace FitCore.Wellness.Application.Nutrition;

// ---- Planes --------------------------------------------------------------------------------------------------------

public sealed record NutritionPlanSummaryDto(
    Guid Id,
    string Name,
    string? Goal,
    int? TargetCalories,
    string Status,
    int Version,
    DateOnly? ValidFrom,
    DateOnly? ValidUntil,
    int MealCount,
    int ActiveAssignments,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);

public sealed record MealItemDto(
    Guid Id,
    int Position,
    string FoodName,
    decimal Quantity,
    string Unit,
    int? Calories,
    decimal? ProteinG,
    decimal? CarbsG,
    decimal? FatG,
    string? Notes);

/// <param name="EstimatedCalories">Suma de los alimentos que tienen el dato; es una estimación orientativa, nunca un valor exacto.</param>
public sealed record MealDto(Guid Id, int Position, string Name, TimeOnly? SuggestedTime, string? Notes, int? EstimatedCalories, IReadOnlyList<MealItemDto> Items);

public sealed record NutritionPlanDetailDto(
    Guid Id,
    string Status,
    int Version,
    Guid VersionId,
    bool IsCurrentVersion,
    string Name,
    string? Goal,
    string? Description,
    int? TargetCalories,
    int? TargetProteinG,
    int? TargetCarbsG,
    int? TargetFatG,
    string? Recommendations,
    DateOnly? ValidFrom,
    DateOnly? ValidUntil,
    string? ChangeNote,
    IReadOnlyList<MealDto> Meals,
    DateTimeOffset VersionCreatedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);

public sealed record NutritionVersionDto(Guid Id, int Number, string Name, string? ChangeNote, DateTimeOffset CreatedAt, Guid? CreatedBy);

public sealed record MealItemRequest(string FoodName, decimal Quantity, string Unit, int? Calories, decimal? ProteinG, decimal? CarbsG, decimal? FatG, string? Notes);

public sealed record MealRequest(string Name, TimeOnly? SuggestedTime, string? Notes, IReadOnlyList<MealItemRequest>? Items);

public sealed record NutritionPlanRequest(
    string Name,
    string? Goal,
    string? Description,
    int? TargetCalories,
    int? TargetProteinG,
    int? TargetCarbsG,
    int? TargetFatG,
    string? Recommendations,
    DateOnly? ValidFrom,
    DateOnly? ValidUntil,
    string? ChangeNote,
    IReadOnlyList<MealRequest>? Meals);

public sealed record SetNutritionPlanStatusRequest(bool Active);

public sealed record ListNutritionPlansQuery(PageRequest Page, string? Search, PlanStatus? Status) : IQuery<PagedResult<NutritionPlanSummaryDto>>;

public sealed record GetNutritionPlanQuery(Guid Id, int? Version) : IQuery<NutritionPlanDetailDto>;

public sealed record ListNutritionVersionsQuery(Guid Id) : IQuery<IReadOnlyList<NutritionVersionDto>>;

public sealed record CreateNutritionPlanCommand(NutritionPlanRequest Request) : ICommand<NutritionPlanDetailDto>;

/// <summary>Modificar un plan crea una versión nueva; las asignaciones y registros anteriores conservan la suya.</summary>
public sealed record UpdateNutritionPlanCommand(Guid Id, NutritionPlanRequest Request) : ICommand<NutritionPlanDetailDto>;

public sealed record DuplicateNutritionPlanCommand(Guid Id) : ICommand<NutritionPlanDetailDto>;

public sealed record SetNutritionPlanStatusCommand(Guid Id, SetNutritionPlanStatusRequest Request) : ICommand<NutritionPlanSummaryDto>;

// ---- Asignaciones --------------------------------------------------------------------------------------------------

public sealed record AssignNutritionPlanRequest(Guid MemberId, DateOnly? StartDate, DateOnly? EndDate);

public sealed record EndNutritionAssignmentRequest(string Reason);

public sealed record NutritionAssignmentDto(
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

/// <summary>Plan vigente de un alumno junto con sus alergias y restricciones vigentes, para leerlos juntos.</summary>
public sealed record MemberNutritionPlanDto(
    NutritionAssignmentDto Assignment,
    NutritionPlanDetailDto Plan,
    DietaryProfileDto? Profile);

public sealed record AssignNutritionPlanCommand(Guid PlanId, AssignNutritionPlanRequest Request) : ICommand<NutritionAssignmentDto>;

public sealed record EndNutritionAssignmentCommand(Guid Id, EndNutritionAssignmentRequest Request) : ICommand<NutritionAssignmentDto>;

public sealed record GetMemberNutritionPlanQuery(Guid MemberId) : IQuery<MemberNutritionPlanDto>;

public sealed record ListMemberNutritionAssignmentsQuery(Guid MemberId) : IQuery<IReadOnlyList<NutritionAssignmentDto>>;

// ---- Alergias y restricciones --------------------------------------------------------------------------------------

public sealed record DietaryProfileRequest(
    IReadOnlyList<string>? Allergies,
    IReadOnlyList<string>? Intolerances,
    IReadOnlyList<string>? Restrictions,
    string? Notes);

public sealed record DietaryProfileDto(
    Guid Id,
    Guid MemberId,
    IReadOnlyList<string> Allergies,
    IReadOnlyList<string> Intolerances,
    IReadOnlyList<string> Restrictions,
    string? Notes,
    DateTimeOffset RecordedAt,
    Guid RecordedBy);

public sealed record SaveDietaryProfileCommand(Guid MemberId, DietaryProfileRequest Request) : ICommand<DietaryProfileDto>;

/// <summary>El perfil vigente (nulo si nunca se registró) y, con <paramref name="History"/>, los anteriores.</summary>
public sealed record GetDietaryProfileQuery(Guid MemberId, bool History) : IQuery<IReadOnlyList<DietaryProfileDto>>;

// ---- Cumplimiento --------------------------------------------------------------------------------------------------

/// <param name="MemberId">Solo lo usa el personal; un alumno siempre registra el suyo (se ignora y, si no es el suyo, se rechaza).</param>
public sealed record LogMealRequest(Guid? MemberId, Guid MealId, DateOnly? Date, MealLogStatus Status, string? Notes);

public sealed record MealLogDto(Guid Id, Guid MemberId, Guid MealId, string MealName, DateOnly Date, string Status, string? Notes, int PlanVersion, DateTimeOffset CreatedAt);

public sealed record DayAdherenceDto(DateOnly Date, int PlannedMeals, int Completed, int Partial, int Skipped, int NotLogged);

public sealed record NutritionLogsDto(DateOnly From, DateOnly To, IReadOnlyList<DayAdherenceDto> Days, IReadOnlyList<MealLogDto> Logs);

public sealed record LogMealCommand(LogMealRequest Request) : ICommand<MealLogDto>;

public sealed record ListMealLogsQuery(Guid MemberId, DateOnly? From, DateOnly? To) : IQuery<NutritionLogsDto>;

// ---- Validación ----------------------------------------------------------------------------------------------------

public sealed class NutritionPlanRequestValidator : AbstractValidator<NutritionPlanRequest>
{
    public NutritionPlanRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MinimumLength(2).MaximumLength(120);
        RuleFor(x => x.Goal).MaximumLength(300);
        RuleFor(x => x.Description).MaximumLength(2000);
        RuleFor(x => x.Recommendations).MaximumLength(2000);
        RuleFor(x => x.ChangeNote).MaximumLength(300);
        RuleFor(x => x.ValidUntil).GreaterThanOrEqualTo(x => x.ValidFrom!.Value).When(x => x.ValidFrom is not null && x.ValidUntil is not null)
            .WithMessage("La vigencia no puede terminar antes de empezar.");
        RuleFor(x => x.Meals).NotEmpty().WithMessage("El plan necesita al menos una comida.");
        RuleFor(x => x.Meals).Must(m => m is null || m.Count <= NutritionPlanVersion.MaxMeals).WithMessage($"Máximo {NutritionPlanVersion.MaxMeals} comidas.");
        RuleForEach(x => x.Meals).ChildRules(meal =>
        {
            meal.RuleFor(m => m.Name).NotEmpty().MaximumLength(80);
            meal.RuleFor(m => m.Notes).MaximumLength(500);
            meal.RuleFor(m => m.Items).NotEmpty().WithMessage("Cada comida necesita al menos un alimento.");
            meal.RuleFor(m => m.Items).Must(i => i is null || i.Count <= NutritionPlanVersion.MaxItemsPerMeal)
                .WithMessage($"Máximo {NutritionPlanVersion.MaxItemsPerMeal} alimentos por comida.");
            meal.RuleForEach(m => m.Items).ChildRules(item =>
            {
                item.RuleFor(i => i.FoodName).NotEmpty().MaximumLength(120);
                item.RuleFor(i => i.Unit).MaximumLength(20);
                item.RuleFor(i => i.Notes).MaximumLength(500);
            });
        });
    }
}

public sealed class SetNutritionPlanStatusRequestValidator : AbstractValidator<SetNutritionPlanStatusRequest>;

public sealed class AssignNutritionPlanRequestValidator : AbstractValidator<AssignNutritionPlanRequest>
{
    public AssignNutritionPlanRequestValidator()
    {
        RuleFor(x => x.MemberId).NotEmpty();
    }
}

public sealed class EndNutritionAssignmentRequestValidator : AbstractValidator<EndNutritionAssignmentRequest>
{
    public EndNutritionAssignmentRequestValidator()
    {
        RuleFor(x => x.Reason).NotEmpty().MinimumLength(3).MaximumLength(300);
    }
}

public sealed class DietaryProfileRequestValidator : AbstractValidator<DietaryProfileRequest>
{
    public DietaryProfileRequestValidator()
    {
        RuleFor(x => x.Notes).MaximumLength(DietaryProfile.MaxNotesLength);
        RuleFor(x => x.Allergies).Must(Valid).WithMessage("Alergias: máximo 20 y cada una de hasta 80 caracteres.");
        RuleFor(x => x.Intolerances).Must(Valid).WithMessage("Intolerancias: máximo 20 y cada una de hasta 80 caracteres.");
        RuleFor(x => x.Restrictions).Must(Valid).WithMessage("Restricciones: máximo 20 y cada una de hasta 80 caracteres.");
    }

    private static bool Valid(IReadOnlyList<string>? list) =>
        list is null || (DietaryProfile.Normalize(list).Length <= DietaryProfile.MaxEntries && list.All(v => v is not null && v.Trim().Length <= DietaryProfile.MaxEntryLength));
}

public sealed class LogMealRequestValidator : AbstractValidator<LogMealRequest>
{
    public LogMealRequestValidator()
    {
        RuleFor(x => x.MealId).NotEmpty();
        RuleFor(x => x.Status).IsInEnum();
        RuleFor(x => x.Notes).MaximumLength(MealLog.MaxNotesLength);
    }
}

public static class NutritionErrors
{
    public static readonly Error PlanNotFound = Error.NotFound("nutrition.plan_not_found", "Plan nutricional no encontrado.");
    public static readonly Error PlanArchived = Error.Conflict("nutrition.plan_archived", "El plan está archivado: actívelo para modificarlo o asignarlo.");
    public static readonly Error PlanExpired = Error.Conflict("nutrition.plan_expired", "La vigencia del plan ya terminó.");
    public static readonly Error VersionNotFound = Error.NotFound("nutrition.version_not_found", "Versión del plan no encontrada.");
    public static readonly Error MemberNotFound = Error.NotFound("nutrition.member_not_found", "Alumno no encontrado.");
    public static readonly Error MemberInactive = Error.Conflict("nutrition.member_inactive", "El alumno está inactivo.");
    public static readonly Error NoActivePlan = Error.NotFound("nutrition.no_active_plan", "El alumno no tiene un plan nutricional vigente.");
    public static readonly Error AssignmentNotFound = Error.NotFound("nutrition.assignment_not_found", "Asignación no encontrada.");
    public static readonly Error AssignmentEnded = Error.Conflict("nutrition.assignment_ended", "La asignación ya está cerrada.");
    public static readonly Error InvalidDates = Error.Validation("nutrition.dates_invalid", "Las fechas no son válidas: el inicio no puede ser anterior a hoy ni el fin anterior al inicio.");
    public static readonly Error DuplicateMeal = Error.Validation("nutrition.duplicate_meal", "Dos comidas del plan no pueden llamarse igual.");
    public static readonly Error MealNotInPlan = Error.Validation("nutrition.meal_not_in_plan", "La comida no pertenece al plan vigente del alumno.");
    public static readonly Error MealAlreadyLogged = Error.Conflict("nutrition.meal_already_logged", "Esa comida ya fue registrada para ese día.");
    public static readonly Error LogDateInvalid = Error.Validation("nutrition.log_date_invalid", "La fecha del registro no puede ser futura, anterior a 7 días ni fuera de la vigencia del plan.");
    public static readonly Error OtherMember = Error.Forbidden("nutrition.other_member", "Solo puede registrar su propio cumplimiento.");
    public static readonly Error RangeInvalid = Error.Validation("nutrition.range_invalid", "El rango de fechas no es válido (máximo 92 días).");
    public static readonly Error ProfileEmpty = Error.Validation("nutrition.profile_empty", "Indique al menos una alergia, intolerancia, restricción o nota. Para dejarlo sin datos, registre una nota («Sin alergias conocidas»).");
}
