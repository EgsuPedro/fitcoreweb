using FitCore.SharedKernel;

namespace FitCore.Wellness.Domain;

public enum GoalCategory
{
    /// <summary>Una medida corporal (peso, perímetro o grasa) que se quiere llevar a un valor.</summary>
    BodyMetric = 1,

    /// <summary>Constancia: cuántas sesiones de entrenamiento por semana.</summary>
    SessionsPerWeek = 2,

    /// <summary>Objetivo libre que el profesional y el alumno siguen a mano (sin progreso automático).</summary>
    Custom = 3
}

public enum GoalMetric
{
    WeightKg = 1,
    WaistCm = 2,
    HipCm = 3,
    ChestCm = 4,
    ArmCm = 5,
    ThighCm = 6,
    BodyFatPercent = 7,
    SessionsPerWeek = 8
}

public enum GoalStatus
{
    Active = 1,
    Achieved = 2,
    Abandoned = 3
}

/// <summary>
/// Objetivo de un alumno. Lo define siempre una persona (el sistema nunca propone metas a partir del peso o la apariencia). Una vez
/// creado solo cambia al cerrarse (alcanzado o abandonado); para cambiarlo se cierra y se crea otro, así el historial no se reescribe.
/// El progreso se calcula al leer, a partir de las evaluaciones y sesiones registradas; no se guarda.
/// No es <see cref="IAuditable"/> a propósito: es un dato de salud y la auditoría automática copiaría su contenido.
/// </summary>
public sealed class Goal : Entity<Guid>, ITenantOwned
{
    public const int MaxActivePerMember = 5;
    public const int MaxTitleLength = 120;
    public const int MaxTextLength = 1000;
    public const int MaxHorizonYears = 5;

    private Goal(Guid id)
        : base(id)
    {
    }

    public Guid TenantId { get; private set; }
    public Guid MemberId { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public GoalCategory Category { get; private set; }
    public GoalMetric? Metric { get; private set; }
    public decimal? TargetValue { get; private set; }

    /// <summary>Valor de partida: el último medido al crear el objetivo (o el que indicó quien lo creó).</summary>
    public decimal? BaselineValue { get; private set; }

    public DateOnly StartDate { get; private set; }
    public DateOnly? TargetDate { get; private set; }
    public GoalStatus Status { get; private set; } = GoalStatus.Active;
    public DateOnly? ClosedOn { get; private set; }
    public string? CloseNote { get; private set; }
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static Goal Create(
        Guid tenantId,
        Guid memberId,
        string title,
        string? description,
        GoalCategory category,
        GoalMetric? metric,
        decimal? targetValue,
        decimal? baselineValue,
        DateOnly startDate,
        DateOnly? targetDate,
        Guid createdBy,
        DateTimeOffset now) =>
        new(Guid.CreateVersion7())
        {
            TenantId = tenantId,
            MemberId = memberId,
            Title = title.Trim(),
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            Category = category,
            Metric = metric,
            TargetValue = targetValue,
            BaselineValue = baselineValue,
            StartDate = startDate,
            TargetDate = targetDate,
            CreatedBy = createdBy,
            CreatedAt = now
        };

    public void Close(GoalStatus status, DateOnly day, string? note)
    {
        Status = status;
        ClosedOn = day;
        CloseNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
    }
}

/// <summary>Coherencia de los objetivos y cálculo de su progreso a partir de lo registrado.</summary>
public static class GoalRules
{
    public const decimal MinAdultBmi = 18.5m;
    public const int MaxSessionsPerWeek = 14;

    public static string UnitOf(GoalMetric metric) => metric switch
    {
        GoalMetric.WeightKg => "kg",
        GoalMetric.BodyFatPercent => "%",
        GoalMetric.SessionsPerWeek => "sesiones/semana",
        _ => "cm"
    };

    public static bool IsBodyMetric(GoalMetric metric) => metric != GoalMetric.SessionsPerWeek;

    /// <summary>Código del primer problema de la definición del objetivo, o null si es coherente.</summary>
    public static string? Validate(GoalCategory category, GoalMetric? metric, decimal? target, DateOnly start, DateOnly? targetDate)
    {
        switch (category)
        {
            case GoalCategory.BodyMetric:
                if (metric is not { } m || !IsBodyMetric(m))
                {
                    return "goal.metric_invalid";
                }

                if (target is not { } value || !InRange(m, value))
                {
                    return "goal.target_invalid";
                }

                break;
            case GoalCategory.SessionsPerWeek:
                if (metric is not null and not GoalMetric.SessionsPerWeek)
                {
                    return "goal.metric_invalid";
                }

                if (target is not (>= 1 and <= MaxSessionsPerWeek) || target != decimal.Truncate(target.Value))
                {
                    return "goal.target_invalid";
                }

                break;
            case GoalCategory.Custom:
                if (metric is not null || target is not null)
                {
                    return "goal.custom_has_metric";
                }

                break;
        }

        return targetDate is { } end && (end <= start || end > start.AddYears(Goal.MaxHorizonYears)) ? "goal.date_invalid" : null;
    }

    private static bool InRange(GoalMetric metric, decimal value) => metric switch
    {
        GoalMetric.WeightKg => value is >= AssessmentRules.MinWeightKg and <= AssessmentRules.MaxWeightKg,
        GoalMetric.BodyFatPercent => value is >= AssessmentRules.MinBodyFatPercent and <= AssessmentRules.MaxBodyFatPercent,
        _ => value is >= AssessmentRules.MinCircumferenceCm and <= AssessmentRules.MaxCircumferenceCm
    };

    /// <summary>
    /// Salvaguarda de peso: a un adulto con estatura conocida no se le registra una meta de peso cuyo IMC quede por debajo del rango
    /// de referencia (18,5). Sin estatura o sin edad adulta no hay base para juzgarlo y no se bloquea.
    /// </summary>
    public static bool WeightTargetIsUnsafe(decimal targetKg, decimal? heightCm, int? ageYears) =>
        ageYears is >= AssessmentRules.AdultAge && AssessmentRules.Bmi(targetKg, heightCm) is { } bmi && bmi < MinAdultBmi;

    /// <summary>
    /// Avance entre el valor de partida y la meta, de 0 a 100. Sirve tanto para subir como para bajar (la dirección sale de partida y meta).
    /// Nulo si falta algún dato o si la partida ya es la meta (no hay recorrido que medir).
    /// </summary>
    public static decimal? ProgressPercent(decimal? baseline, decimal? target, decimal? current)
    {
        if (baseline is not { } from || target is not { } to || current is not { } now || from == to)
        {
            return null;
        }

        return Math.Round(Math.Clamp((now - from) / (to - from) * 100m, 0m, 100m), 0, MidpointRounding.AwayFromZero);
    }

    /// <summary>Si el valor actual ya alcanzó o superó la meta en la dirección que va de la partida a la meta.</summary>
    public static bool? Reached(decimal? baseline, decimal? target, decimal? current)
    {
        if (baseline is not { } from || target is not { } to || current is not { } now || from == to)
        {
            return null;
        }

        return to > from ? now >= to : now <= to;
    }
}
