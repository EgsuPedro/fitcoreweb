using FitCore.Application.Core;
using FitCore.SharedKernel;
using FitCore.Wellness.Domain;
using FluentValidation;

namespace FitCore.Wellness.Application.Assessments;

/// <param name="CorrectsId">Si se indica, esta evaluación corrige a otra del mismo alumno (la anterior se conserva).</param>
public sealed record AssessmentRequest(
    DateOnly? AssessedOn,
    decimal? WeightKg,
    decimal? HeightCm,
    decimal? WaistCm,
    decimal? HipCm,
    decimal? ChestCm,
    decimal? ArmCm,
    decimal? ThighCm,
    decimal? BodyFatPercent,
    BodyFatMethod? BodyFatMethod,
    MeasurementSource Source,
    string? Notes,
    Guid? CorrectsId);

/// <param name="Bmi">Indicador general peso / estatura²; nulo si falta el peso o una estatura válida.</param>
/// <param name="HeightCarried">La estatura no es de esta evaluación: se tomó de la más reciente anterior que la tenía.</param>
/// <param name="BmiReferenceRange">Below / Reference / Above, solo para adultos. No es un diagnóstico.</param>
public sealed record AssessmentDto(
    Guid Id,
    Guid MemberId,
    DateOnly AssessedOn,
    decimal? WeightKg,
    decimal? HeightCm,
    decimal? WaistCm,
    decimal? HipCm,
    decimal? ChestCm,
    decimal? ArmCm,
    decimal? ThighCm,
    decimal? BodyFatPercent,
    string? BodyFatMethod,
    string Source,
    string? Notes,
    Guid? CorrectsId,
    bool IsCorrected,
    decimal? Bmi,
    bool HeightCarried,
    string? BmiReferenceRange,
    decimal? WaistHipRatio,
    DateTimeOffset CreatedAt);

public sealed record MetricChangeDto(string Metric, string Unit, decimal? From, decimal? To, decimal? Delta);

public sealed record AssessmentComparisonDto(AssessmentDto A, AssessmentDto B, int DaysBetween, IReadOnlyList<MetricChangeDto> Changes);

public sealed record AssessmentPointDto(Guid AssessmentId, DateOnly Date, decimal? WeightKg, decimal? Bmi, decimal? WaistCm, decimal? HipCm, decimal? ChestCm, decimal? ArmCm, decimal? ThighCm, decimal? BodyFatPercent);

public sealed record CreateAssessmentCommand(Guid MemberId, AssessmentRequest Request) : ICommand<AssessmentDto>;

public sealed record ListAssessmentsQuery(Guid MemberId, bool IncludeCorrected, DateOnly? From, DateOnly? To, PageRequest Page) : IQuery<PagedResult<AssessmentDto>>;

public sealed record CompareAssessmentsQuery(Guid MemberId, Guid A, Guid B) : IQuery<AssessmentComparisonDto>;

public sealed record AssessmentProgressQuery(Guid MemberId, DateOnly? From, DateOnly? To) : IQuery<IReadOnlyList<AssessmentPointDto>>;

public sealed class AssessmentRequestValidator : AbstractValidator<AssessmentRequest>
{
    public AssessmentRequestValidator()
    {
        RuleFor(x => x.Source).IsInEnum();
        RuleFor(x => x.BodyFatMethod).IsInEnum().When(x => x.BodyFatMethod is not null);
        RuleFor(x => x.Notes).MaximumLength(BodyAssessment.MaxNotesLength);
    }
}

public static class AssessmentErrors
{
    public static readonly Error MemberNotFound = Error.NotFound("assessment.member_not_found", "Alumno no encontrado.");
    public static readonly Error MemberInactive = Error.Conflict("assessment.member_inactive", "El alumno está inactivo.");
    public static readonly Error NotFound = Error.NotFound("assessment.not_found", "Evaluación no encontrada.");
    public static readonly Error DateInvalid = Error.Validation("assessment.date_invalid", "La fecha de la evaluación no puede ser futura ni anterior a diez años.");
    public static readonly Error AlreadyCorrected = Error.Conflict("assessment.already_corrected", "Esa evaluación ya fue corregida; corrija la más reciente.");
    public static readonly Error RangeInvalid = Error.Validation("assessment.range_invalid", "El rango de fechas no es válido.");
    public static readonly Error CompareInvalid = Error.Validation("assessment.compare_invalid", "Elija dos evaluaciones distintas.");
}
