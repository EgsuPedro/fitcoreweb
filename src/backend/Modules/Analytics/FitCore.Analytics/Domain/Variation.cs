namespace FitCore.Analytics.Domain;

public enum VariationState
{
    /// <summary>Hay valor actual y base de comparación válida.</summary>
    Ok = 1,

    /// <summary>El período anterior vale cero (o no hay dato): un porcentaje sería engañoso, solo se informa la diferencia absoluta.</summary>
    NoBaseline = 2,

    /// <summary>No hay dato en el período actual (la medida no se pudo calcular): no se compara.</summary>
    NoData = 3,

    /// <summary>Medida de un instante (saldo pendiente de hoy, vencimientos próximos): no existe el valor de un período anterior, así que no se compara.</summary>
    NotApplicable = 4
}

/// <param name="Absolute">Actual − anterior (en puntos porcentuales si la medida es una razón).</param>
/// <param name="Percent">(actual − anterior) ÷ anterior × 100, redondeado a 1 decimal. Solo con <see cref="VariationState.Ok"/> y medidas no porcentuales.</param>
public sealed record Variation(VariationState State, decimal? Absolute, decimal? Percent)
{
    public static readonly Variation NoDataYet = new(VariationState.NoData, null, null);

    public static readonly Variation Snapshot = new(VariationState.NotApplicable, null, null);

    /// <summary>Compara dos cantidades o importes. Sin base (anterior = 0 o ausente) no hay porcentaje.</summary>
    public static Variation Of(decimal? current, decimal? previous)
    {
        if (current is not { } now)
        {
            return NoDataYet;
        }

        if (previous is null)
        {
            return new Variation(VariationState.NoBaseline, null, null);
        }

        var before = previous.Value;
        return before == 0m
            ? new Variation(VariationState.NoBaseline, now - before, null)
            : new Variation(VariationState.Ok, now - before, Math.Round((now - before) / before * 100m, 1, MidpointRounding.AwayFromZero));
    }

    /// <summary>Compara dos razones (ocupación, retención): la diferencia son puntos porcentuales y nunca un porcentaje de un porcentaje.</summary>
    public static Variation OfRatio(decimal? current, decimal? previous)
    {
        if (current is not { } now)
        {
            return NoDataYet;
        }

        return previous is null
            ? new Variation(VariationState.NoBaseline, null, null)
            : new Variation(VariationState.Ok, Math.Round(now - previous.Value, 1, MidpointRounding.AwayFromZero), null);
    }
}
