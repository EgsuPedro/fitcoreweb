using FitCore.Analytics.Domain;

namespace FitCore.Analytics.Application;

public enum KpiUnit
{
    Count = 1,
    Currency = 2,
    Percent = 3
}

/// <summary>
/// Un indicador con todo lo necesario para leerlo sin ambigüedad: qué es, qué fecha usa, en qué unidad, su valor, el del período
/// comparable y la variación (que puede no existir: ver <see cref="VariationState"/>).
/// </summary>
/// <param name="Key">Identificador estable (las pantallas lo usan para traducir el nombre y la definición).</param>
/// <param name="Basis">Qué fecha o instante usa el cálculo (fecha de pago, de emisión, de reversión, hoy…).</param>
/// <param name="Currency">Solo para importes; hoy siempre USD.</param>
/// <param name="Value">Nulo si no se pudo calcular (por ejemplo, una razón sin capacidad).</param>
/// <param name="Previous">Valor del período comparable; nulo para medidas de un instante.</param>
/// <param name="IsEmpty">No hay información en el período ni en el comparable: la pantalla muestra un estado vacío, no un cero engañoso.</param>
/// <param name="Report">Reporte detallado al que lleva el indicador, si existe.</param>
public sealed record KpiDto(
    string Key,
    string Name,
    string Definition,
    string Basis,
    KpiUnit Unit,
    string? Currency,
    decimal? Value,
    decimal? Previous,
    Variation Variation,
    bool IsEmpty,
    string? Report);

internal static class Kpi
{
    private const string Usd = "USD";

    public static KpiDto Count(string key, string name, string definition, string basis, int current, int previous, string? report = null) =>
        Build(key, name, definition, basis, KpiUnit.Count, null, current, previous, Variation.Of(current, previous), report);

    public static KpiDto Money(string key, string name, string definition, string basis, decimal current, decimal previous, string? report = null) =>
        Build(key, name, definition, basis, KpiUnit.Currency, Usd, current, previous, Variation.Of(current, previous), report);

    /// <summary>Razón en porcentaje (ocupación, retención). Sin datos (nulo) no se muestra 0 %.</summary>
    public static KpiDto Ratio(string key, string name, string definition, string basis, decimal? current, decimal? previous, string? report = null) =>
        Build(key, name, definition, basis, KpiUnit.Percent, null, current, previous, Variation.OfRatio(current, previous), report);

    /// <summary>Medida de un instante (hoy): no tiene valor comparable.</summary>
    public static KpiDto SnapshotCount(string key, string name, string definition, string basis, int current, string? report = null) =>
        Build(key, name, definition, basis, KpiUnit.Count, null, current, null, Variation.Snapshot, report);

    public static KpiDto SnapshotMoney(string key, string name, string definition, string basis, decimal current, string? report = null) =>
        Build(key, name, definition, basis, KpiUnit.Currency, Usd, current, null, Variation.Snapshot, report);

    private static KpiDto Build(string key, string name, string definition, string basis, KpiUnit unit, string? currency, decimal? value, decimal? previous, Variation variation, string? report) =>
        new(key, name, definition, basis, unit, currency, value, previous, variation, IsEmpty: (value ?? 0m) == 0m && (previous ?? 0m) == 0m, report);
}
