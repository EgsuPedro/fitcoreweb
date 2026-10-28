namespace FitCore.Wellness.Domain;

/// <summary>
/// Adherencia = cumplimiento <b>registrado</b> frente a lo asignado. No mide resultados fisiológicos ni la calidad de lo hecho, y lo que
/// no se registró se cuenta aparte: no se supone cumplido ni incumplido.
/// </summary>
public static class AdherenceRules
{
    public const int MaxRangeDays = 92;
    public const int DefaultRangeDays = 28;

    /// <summary>Lunes de la semana a la que pertenece el día (las semanas van de lunes a domingo).</summary>
    public static DateOnly WeekStart(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    /// <summary>
    /// Sesiones esperadas en una semana: los días de la rutina vigente, prorrateados (hacia arriba) por los días de la semana en que la
    /// rutina estuvo vigente. Es un supuesto: una rutina de N días se entrena N veces por semana.
    /// </summary>
    public static int ExpectedSessions(int planDays, int daysCovered) =>
        planDays <= 0 || daysCovered <= 0 ? 0 : (int)Math.Ceiling(planDays * Math.Min(daysCovered, 7) / 7m);

    /// <summary>Porcentaje cumplido (0 a 100, redondeado); nulo si no había nada esperado. Lo hecho de más no suma sobre 100.</summary>
    public static decimal? Percent(int done, int expected) =>
        expected <= 0 ? null : Math.Round(Math.Min(100m, done * 100m / expected), 0, MidpointRounding.AwayFromZero);

    /// <summary>Cumplimiento de comidas: completas cuentan 1 y parciales 0,5; omitidas y sin registrar, 0.</summary>
    public static decimal? NutritionPercent(int completed, int partial, int planned) =>
        planned <= 0 ? null : Math.Round(Math.Min(100m, (completed + partial * 0.5m) * 100m / planned), 0, MidpointRounding.AwayFromZero);
}
