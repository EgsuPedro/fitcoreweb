using FitCore.Analytics.Domain;

namespace FitCore.Analytics.UnitTests;

public class VariationTests
{
    [Fact]
    public void Con_base_valida_calcula_la_diferencia_y_el_porcentaje()
    {
        var growth = Variation.Of(150m, 100m);
        Assert.Equal(VariationState.Ok, growth.State);
        Assert.Equal(50m, growth.Absolute);
        Assert.Equal(50.0m, growth.Percent);

        var drop = Variation.Of(75m, 100m);
        Assert.Equal(-25m, drop.Absolute);
        Assert.Equal(-25.0m, drop.Percent);
    }

    [Fact]
    public void El_porcentaje_se_redondea_a_un_decimal()
    {
        Assert.Equal(33.3m, Variation.Of(4m, 3m).Percent);
        Assert.Equal(-66.7m, Variation.Of(1m, 3m).Percent);
    }

    [Fact]
    public void Sin_base_de_comparacion_no_hay_porcentaje_pero_si_la_diferencia()
    {
        var fromZero = Variation.Of(120m, 0m);
        Assert.Equal(VariationState.NoBaseline, fromZero.State);
        Assert.Null(fromZero.Percent);
        Assert.Equal(120m, fromZero.Absolute);

        var bothZero = Variation.Of(0m, 0m);
        Assert.Equal(VariationState.NoBaseline, bothZero.State);
        Assert.Null(bothZero.Percent);
        Assert.Equal(0m, bothZero.Absolute);

        var missing = Variation.Of(10m, null);
        Assert.Equal(VariationState.NoBaseline, missing.State);
        Assert.Null(missing.Absolute);
    }

    [Fact]
    public void Si_no_hay_valor_actual_no_se_compara()
    {
        var variation = Variation.Of(null, 100m);
        Assert.Equal(VariationState.NoData, variation.State);
        Assert.Null(variation.Absolute);
        Assert.Null(variation.Percent);
    }

    [Fact]
    public void Un_cero_actual_con_base_positiva_es_una_caida_del_cien_por_ciento()
    {
        var variation = Variation.Of(0m, 80m);
        Assert.Equal(VariationState.Ok, variation.State);
        Assert.Equal(-100.0m, variation.Percent);
    }

    [Fact]
    public void Los_importes_conservan_su_precision_decimal()
    {
        var variation = Variation.Of(1234.57m, 1234.56m);
        Assert.Equal(0.01m, variation.Absolute);
    }

    [Fact]
    public void Las_razones_se_comparan_en_puntos_porcentuales_nunca_como_porcentaje_de_porcentaje()
    {
        var variation = Variation.OfRatio(62.5m, 50m);
        Assert.Equal(VariationState.Ok, variation.State);
        Assert.Equal(12.5m, variation.Absolute);
        Assert.Null(variation.Percent);

        // Una razón anterior de 0 % es una base legítima (a diferencia de una cantidad sin datos).
        Assert.Equal(VariationState.Ok, Variation.OfRatio(10m, 0m).State);
        Assert.Equal(VariationState.NoBaseline, Variation.OfRatio(10m, null).State);
        Assert.Equal(VariationState.NoData, Variation.OfRatio(null, 10m).State);
    }
}
