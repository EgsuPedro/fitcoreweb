using FitCore.SharedKernel;

namespace FitCore.Wellness.Domain;

/// <summary>Cómo se obtuvo la medición (la fuente queda registrada para interpretar el dato).</summary>
public enum MeasurementSource
{
    Manual = 1,
    Scale = 2,
    Tape = 3,
    Caliper = 4,
    Bioimpedance = 5,
    Other = 6
}

/// <summary>Método con que se midió el porcentaje de grasa: sin método no se registra (el sistema nunca lo calcula por su cuenta).</summary>
public enum BodyFatMethod
{
    Caliper = 1,
    Bioimpedance = 2,
    Dexa = 3,
    Other = 4
}

/// <summary>Mediciones de una evaluación; todas opcionales, pero debe haber al menos una. Unidades: kg, cm y %.</summary>
public sealed record Measurements(
    decimal? WeightKg,
    decimal? HeightCm,
    decimal? WaistCm,
    decimal? HipCm,
    decimal? ChestCm,
    decimal? ArmCm,
    decimal? ThighCm,
    decimal? BodyFatPercent,
    BodyFatMethod? BodyFatMethod);

/// <summary>
/// Evaluación física de un alumno. Es un dato sensible y solo se agrega: un error se corrige con una evaluación nueva que apunta a la
/// anterior (<see cref="CorrectsId"/>); nada se sobrescribe ni se borra. No es <see cref="IAuditable"/> a propósito: la auditoría
/// automática copiaría las mediciones; el sistema audita el hecho, no el contenido.
/// </summary>
public sealed class BodyAssessment : Entity<Guid>, ITenantOwned
{
    public const int MaxNotesLength = 1000;

    private BodyAssessment(Guid id)
        : base(id)
    {
    }

    public Guid TenantId { get; private set; }
    public Guid MemberId { get; private set; }
    public DateOnly AssessedOn { get; private set; }
    public decimal? WeightKg { get; private set; }
    public decimal? HeightCm { get; private set; }
    public decimal? WaistCm { get; private set; }
    public decimal? HipCm { get; private set; }
    public decimal? ChestCm { get; private set; }
    public decimal? ArmCm { get; private set; }
    public decimal? ThighCm { get; private set; }
    public decimal? BodyFatPercent { get; private set; }
    public BodyFatMethod? BodyFatMethod { get; private set; }
    public MeasurementSource Source { get; private set; }
    public string? Notes { get; private set; }

    /// <summary>Evaluación anterior que esta corrige (la anterior deja de mostrarse en el historial vigente, pero se conserva).</summary>
    public Guid? CorrectsId { get; private set; }

    public Guid RecordedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static BodyAssessment Create(
        Guid tenantId,
        Guid memberId,
        DateOnly assessedOn,
        Measurements m,
        MeasurementSource source,
        string? notes,
        Guid? correctsId,
        Guid recordedBy,
        DateTimeOffset now) =>
        new(Guid.CreateVersion7())
        {
            TenantId = tenantId,
            MemberId = memberId,
            AssessedOn = assessedOn,
            WeightKg = m.WeightKg,
            HeightCm = m.HeightCm,
            WaistCm = m.WaistCm,
            HipCm = m.HipCm,
            ChestCm = m.ChestCm,
            ArmCm = m.ArmCm,
            ThighCm = m.ThighCm,
            BodyFatPercent = m.BodyFatPercent,
            BodyFatMethod = m.BodyFatPercent is null ? null : m.BodyFatMethod,
            Source = source,
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
            CorrectsId = correctsId,
            RecordedBy = recordedBy,
            CreatedAt = now
        };
}

/// <summary>
/// Reglas de las mediciones y de lo que se deriva de ellas. El IMC es un indicador general (peso / estatura²), no un diagnóstico:
/// solo existe con peso y estatura válidos, y su rango de referencia solo se informa para adultos.
/// </summary>
public static class AssessmentRules
{
    public const decimal MinWeightKg = 2m;
    public const decimal MaxWeightKg = 500m;
    public const decimal MinHeightCm = 50m;
    public const decimal MaxHeightCm = 260m;
    public const decimal MinCircumferenceCm = 5m;
    public const decimal MaxCircumferenceCm = 250m;
    public const decimal MinBodyFatPercent = 2m;
    public const decimal MaxBodyFatPercent = 70m;
    public const int AdultAge = 18;

    /// <summary>Código del primer problema de las mediciones, o null si son coherentes.</summary>
    public static string? Validate(Measurements m)
    {
        if (m.WeightKg is null && m.HeightCm is null && m.WaistCm is null && m.HipCm is null && m.ChestCm is null
            && m.ArmCm is null && m.ThighCm is null && m.BodyFatPercent is null)
        {
            return "assessment.empty";
        }

        if (m.WeightKg is < MinWeightKg or > MaxWeightKg)
        {
            return "assessment.weight_invalid";
        }

        if (m.HeightCm is < MinHeightCm or > MaxHeightCm)
        {
            return "assessment.height_invalid";
        }

        foreach (var value in new[] { m.WaistCm, m.HipCm, m.ChestCm, m.ArmCm, m.ThighCm })
        {
            if (value is < MinCircumferenceCm or > MaxCircumferenceCm)
            {
                return "assessment.circumference_invalid";
            }
        }

        if (m.BodyFatPercent is < MinBodyFatPercent or > MaxBodyFatPercent)
        {
            return "assessment.body_fat_invalid";
        }

        if (m.BodyFatPercent is not null && m.BodyFatMethod is null)
        {
            return "assessment.body_fat_method_required";
        }

        return m.BodyFatPercent is null && m.BodyFatMethod is not null ? "assessment.body_fat_method_without_value" : null;
    }

    /// <summary>IMC con un decimal; nulo si falta el peso o la estatura, o si no son válidos (nunca se divide por cero).</summary>
    public static decimal? Bmi(decimal? weightKg, decimal? heightCm)
    {
        if (weightKg is not { } w || heightCm is not { } h || w < MinWeightKg || w > MaxWeightKg || h < MinHeightCm || h > MaxHeightCm)
        {
            return null;
        }

        var meters = h / 100m;
        return Math.Round(w / (meters * meters), 1, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Rango de referencia general para adultos: <c>Below</c> (&lt; 18,5), <c>Reference</c> (18,5 a 24,9) o <c>Above</c> (≥ 25).
    /// Nulo si no hay IMC o si la persona es menor de edad o no se conoce su edad (los rangos de adultos no aplican).
    /// </summary>
    public static string? BmiReferenceRange(decimal? bmi, int? ageYears) =>
        bmi is not { } value || ageYears is not >= AdultAge ? null : value < 18.5m ? "Below" : value < 25m ? "Reference" : "Above";

    /// <summary>Edad cumplida en una fecha, o nulo si no se conoce la fecha de nacimiento.</summary>
    public static int? AgeOn(DateOnly? birthDate, DateOnly day)
    {
        if (birthDate is not { } birth || birth > day)
        {
            return null;
        }

        var age = day.Year - birth.Year;
        return birth.AddYears(age) > day ? age - 1 : age;
    }

    /// <summary>Relación cintura/cadera con dos decimales; nula si falta alguna medida.</summary>
    public static decimal? WaistHipRatio(decimal? waistCm, decimal? hipCm) =>
        waistCm is { } waist && hipCm is { } hip && hip > 0 ? Math.Round(waist / hip, 2, MidpointRounding.AwayFromZero) : null;
}
