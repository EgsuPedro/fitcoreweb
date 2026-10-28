using FitCore.Analytics.Domain;

namespace FitCore.Analytics.UnitTests;

public class PeriodTests
{
    private const string Guayaquil = "America/Guayaquil";

    private static PeriodPair Resolve(PeriodPreset preset, string today, DateOnly? from = null, DateOnly? to = null)
    {
        var result = PeriodResolver.Resolve(preset, from, to, DateOnly.Parse(today), Guayaquil);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Code : "");
        return result.Value;
    }

    private static void AssertRange(ReportPeriod period, string from, string to)
    {
        Assert.Equal(DateOnly.Parse(from), period.From);
        Assert.Equal(DateOnly.Parse(to), period.To);
    }

    [Fact]
    public void Hoy_se_compara_con_ayer()
    {
        var pair = Resolve(PeriodPreset.Today, "2026-03-15");
        AssertRange(pair.Current, "2026-03-15", "2026-03-15");
        AssertRange(pair.Previous, "2026-03-14", "2026-03-14");
    }

    [Fact]
    public void Los_ultimos_7_y_30_dias_incluyen_hoy_y_se_comparan_con_los_inmediatamente_anteriores()
    {
        var week = Resolve(PeriodPreset.Last7Days, "2026-03-15");
        AssertRange(week.Current, "2026-03-09", "2026-03-15");
        AssertRange(week.Previous, "2026-03-02", "2026-03-08");

        var month = Resolve(PeriodPreset.Last30Days, "2026-03-15");
        Assert.Equal(30, month.Current.Days);
        Assert.Equal(30, month.Previous.Days);
        Assert.Equal(month.Current.From.AddDays(-1), month.Previous.To);
    }

    [Fact]
    public void El_mes_actual_parcial_se_compara_con_los_mismos_dias_del_mes_anterior()
    {
        var pair = Resolve(PeriodPreset.CurrentMonth, "2026-03-15");
        AssertRange(pair.Current, "2026-03-01", "2026-03-15");
        AssertRange(pair.Previous, "2026-02-01", "2026-02-15");
    }

    [Fact]
    public void El_mes_actual_no_pasa_del_largo_del_mes_anterior()
    {
        // 31 de marzo contra febrero de 28 días: se compara con febrero completo, no con días que no existen.
        var pair = Resolve(PeriodPreset.CurrentMonth, "2026-03-31");
        AssertRange(pair.Current, "2026-03-01", "2026-03-31");
        AssertRange(pair.Previous, "2026-02-01", "2026-02-28");
    }

    [Fact]
    public void El_mes_anterior_es_completo_y_se_compara_con_el_mes_previo()
    {
        var pair = Resolve(PeriodPreset.PreviousMonth, "2026-03-15");
        AssertRange(pair.Current, "2026-02-01", "2026-02-28");
        AssertRange(pair.Previous, "2026-01-01", "2026-01-31");

        // Cruce de año.
        var january = Resolve(PeriodPreset.PreviousMonth, "2026-01-10");
        AssertRange(january.Current, "2025-12-01", "2025-12-31");
        AssertRange(january.Previous, "2025-11-01", "2025-11-30");
    }

    [Fact]
    public void El_ano_actual_se_compara_con_los_mismos_dias_del_ano_anterior()
    {
        var pair = Resolve(PeriodPreset.CurrentYear, "2026-03-15");
        AssertRange(pair.Current, "2026-01-01", "2026-03-15");
        AssertRange(pair.Previous, "2025-01-01", "2025-03-15");

        // 2024 es bisiesto: el 31 de diciembre de 2025 (365 días) cabe completo en 2024 (366).
        var full = Resolve(PeriodPreset.CurrentYear, "2025-12-31");
        Assert.Equal(365, full.Current.Days);
        Assert.Equal(365, full.Previous.Days);
    }

    [Fact]
    public void Un_rango_personalizado_se_compara_con_el_inmediatamente_anterior_de_igual_duracion()
    {
        var pair = Resolve(PeriodPreset.Custom, "2026-03-15", new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 10));
        AssertRange(pair.Current, "2026-03-01", "2026-03-10");
        AssertRange(pair.Previous, "2026-02-19", "2026-02-28");
    }

    [Theory]
    [InlineData(PeriodPreset.Custom, null, null, "analytics.period_required")]
    [InlineData(PeriodPreset.Custom, "2026-03-01", null, "analytics.period_required")]
    [InlineData(PeriodPreset.Custom, "2026-03-10", "2026-03-01", "analytics.period_invalid")]
    [InlineData(PeriodPreset.Custom, "2025-01-01", "2026-03-01", "analytics.period_too_long")]
    [InlineData(PeriodPreset.Custom, "1999-12-01", "1999-12-31", "analytics.period_too_old")]
    [InlineData(PeriodPreset.Last30Days, "2026-03-01", "2026-03-02", "analytics.period_preset_with_dates")]
    public void Los_rangos_invalidos_se_rechazan(PeriodPreset preset, string? from, string? to, string code)
    {
        var result = PeriodResolver.Resolve(preset, from is null ? null : DateOnly.Parse(from), to is null ? null : DateOnly.Parse(to), new DateOnly(2026, 3, 15), Guayaquil);
        Assert.True(result.IsFailure);
        Assert.Equal(code, result.Error.Code);
    }

    [Fact]
    public void El_limite_de_366_dias_es_inclusivo()
    {
        var ok = PeriodResolver.Resolve(PeriodPreset.Custom, new DateOnly(2025, 3, 1), new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 15), Guayaquil);
        Assert.True(ok.IsSuccess);
        Assert.Equal(366, ok.Value.Current.Days);

        var tooLong = PeriodResolver.Resolve(PeriodPreset.Custom, new DateOnly(2025, 2, 28), new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 15), Guayaquil);
        Assert.True(tooLong.IsFailure);
    }

    [Fact]
    public void Las_fechas_locales_se_convierten_a_instantes_UTC_con_el_final_medio_abierto()
    {
        // Ecuador continental es UTC−5 todo el año.
        var period = new ReportPeriod(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), Guayaquil);
        Assert.Equal(DateTimeOffset.Parse("2026-03-01T05:00:00Z"), period.StartUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-04-01T05:00:00Z"), period.EndUtcExclusive);
        Assert.Equal(31, period.Days);

        // Un cobro a las 23:30 locales del último día (04:30 UTC del día siguiente) cae dentro; a las 00:00 locales del día siguiente, fuera.
        var lastMinuteInside = DateTimeOffset.Parse("2026-04-01T04:30:00Z");
        var firstMinuteOutside = DateTimeOffset.Parse("2026-04-01T05:00:00Z");
        Assert.True(lastMinuteInside >= period.StartUtc && lastMinuteInside < period.EndUtcExclusive);
        Assert.False(firstMinuteOutside < period.EndUtcExclusive);
    }

    [Fact]
    public void Una_zona_con_otro_desfase_mueve_el_inicio()
    {
        var galapagos = new ReportPeriod(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 1), "Pacific/Galapagos");
        Assert.Equal(DateTimeOffset.Parse("2026-03-01T06:00:00Z"), galapagos.StartUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-03-02T06:00:00Z"), galapagos.EndUtcExclusive);
    }

    [Fact]
    public void Un_cambio_de_horario_dentro_del_rango_se_respeta()
    {
        // Nueva York pasa a horario de verano el 8 de marzo de 2026: el día tiene 23 horas.
        var period = new ReportPeriod(new DateOnly(2026, 3, 8), new DateOnly(2026, 3, 8), "America/New_York");
        Assert.Equal(DateTimeOffset.Parse("2026-03-08T05:00:00Z"), period.StartUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-03-09T04:00:00Z"), period.EndUtcExclusive);
        Assert.Equal(TimeSpan.FromHours(23), period.EndUtcExclusive - period.StartUtc);
    }
}
