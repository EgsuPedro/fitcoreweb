using FitCore.Wellness.Domain;

namespace FitCore.Wellness.UnitTests;

public class GoalAndAdherenceTests
{
    private static readonly DateOnly Start = new(2026, 3, 2);

    // ---- Definición de objetivos ----

    [Fact]
    public void Una_medida_corporal_necesita_su_medida_y_una_meta_en_rango()
    {
        Assert.Null(GoalRules.Validate(GoalCategory.BodyMetric, GoalMetric.WeightKg, 72m, Start, null));
        Assert.Null(GoalRules.Validate(GoalCategory.BodyMetric, GoalMetric.WaistCm, 80m, Start, Start.AddMonths(3)));
        Assert.Equal("goal.metric_invalid", GoalRules.Validate(GoalCategory.BodyMetric, null, 72m, Start, null));
        Assert.Equal("goal.metric_invalid", GoalRules.Validate(GoalCategory.BodyMetric, GoalMetric.SessionsPerWeek, 3m, Start, null));
        Assert.Equal("goal.target_invalid", GoalRules.Validate(GoalCategory.BodyMetric, GoalMetric.WeightKg, null, Start, null));
        Assert.Equal("goal.target_invalid", GoalRules.Validate(GoalCategory.BodyMetric, GoalMetric.WeightKg, 1m, Start, null));
        Assert.Equal("goal.target_invalid", GoalRules.Validate(GoalCategory.BodyMetric, GoalMetric.BodyFatPercent, 80m, Start, null));
        Assert.Equal("goal.target_invalid", GoalRules.Validate(GoalCategory.BodyMetric, GoalMetric.WaistCm, 300m, Start, null));
    }

    [Fact]
    public void La_constancia_pide_entre_1_y_14_sesiones_enteras_por_semana()
    {
        Assert.Null(GoalRules.Validate(GoalCategory.SessionsPerWeek, null, 3m, Start, null));
        Assert.Null(GoalRules.Validate(GoalCategory.SessionsPerWeek, GoalMetric.SessionsPerWeek, 14m, Start, null));
        Assert.Equal("goal.target_invalid", GoalRules.Validate(GoalCategory.SessionsPerWeek, null, 0m, Start, null));
        Assert.Equal("goal.target_invalid", GoalRules.Validate(GoalCategory.SessionsPerWeek, null, 15m, Start, null));
        Assert.Equal("goal.target_invalid", GoalRules.Validate(GoalCategory.SessionsPerWeek, null, 2.5m, Start, null));
        Assert.Equal("goal.metric_invalid", GoalRules.Validate(GoalCategory.SessionsPerWeek, GoalMetric.WeightKg, 3m, Start, null));
    }

    [Fact]
    public void Un_objetivo_libre_no_lleva_medida_ni_meta()
    {
        Assert.Null(GoalRules.Validate(GoalCategory.Custom, null, null, Start, null));
        Assert.Equal("goal.custom_has_metric", GoalRules.Validate(GoalCategory.Custom, GoalMetric.WeightKg, null, Start, null));
        Assert.Equal("goal.custom_has_metric", GoalRules.Validate(GoalCategory.Custom, null, 5m, Start, null));
    }

    [Fact]
    public void La_fecha_meta_es_posterior_al_inicio_y_no_pasa_de_cinco_anos()
    {
        Assert.Equal("goal.date_invalid", GoalRules.Validate(GoalCategory.Custom, null, null, Start, Start));
        Assert.Equal("goal.date_invalid", GoalRules.Validate(GoalCategory.Custom, null, null, Start, Start.AddDays(-1)));
        Assert.Equal("goal.date_invalid", GoalRules.Validate(GoalCategory.Custom, null, null, Start, Start.AddYears(5).AddDays(1)));
        Assert.Null(GoalRules.Validate(GoalCategory.Custom, null, null, Start, Start.AddYears(5)));
    }

    [Fact]
    public void Las_unidades_salen_de_la_medida()
    {
        Assert.Equal("kg", GoalRules.UnitOf(GoalMetric.WeightKg));
        Assert.Equal("cm", GoalRules.UnitOf(GoalMetric.ThighCm));
        Assert.Equal("%", GoalRules.UnitOf(GoalMetric.BodyFatPercent));
        Assert.Equal("sesiones/semana", GoalRules.UnitOf(GoalMetric.SessionsPerWeek));
    }

    // ---- Salvaguarda de peso ----

    [Fact]
    public void Una_meta_de_peso_que_deja_el_imc_bajo_el_rango_no_se_acepta_a_un_adulto_con_estatura()
    {
        // 175 cm: 56 kg → IMC 18,3 (bajo); 57 kg → 18,6.
        Assert.True(GoalRules.WeightTargetIsUnsafe(56m, 175m, 30));
        Assert.False(GoalRules.WeightTargetIsUnsafe(57m, 175m, 30));
        Assert.False(GoalRules.WeightTargetIsUnsafe(40m, null, 30));
        Assert.False(GoalRules.WeightTargetIsUnsafe(40m, 175m, null));
        Assert.False(GoalRules.WeightTargetIsUnsafe(40m, 175m, 15));
    }

    // ---- Progreso ----

    [Theory]
    [InlineData(90, 80, 85, 50)]
    [InlineData(90, 80, 90, 0)]
    [InlineData(90, 80, 80, 100)]
    [InlineData(90, 80, 75, 100)]
    [InlineData(90, 80, 95, 0)]
    [InlineData(60, 70, 65, 50)]
    public void El_progreso_va_de_la_partida_a_la_meta_subiendo_o_bajando(double baseline, double target, double current, double expected) =>
        Assert.Equal((decimal)expected, GoalRules.ProgressPercent((decimal)baseline, (decimal)target, (decimal)current));

    [Fact]
    public void Sin_partida_meta_o_dato_actual_o_sin_recorrido_no_hay_progreso()
    {
        Assert.Null(GoalRules.ProgressPercent(null, 80m, 85m));
        Assert.Null(GoalRules.ProgressPercent(90m, null, 85m));
        Assert.Null(GoalRules.ProgressPercent(90m, 80m, null));
        Assert.Null(GoalRules.ProgressPercent(80m, 80m, 80m));
        Assert.Null(GoalRules.Reached(80m, 80m, 80m));
    }

    [Fact]
    public void La_meta_se_alcanza_en_la_direccion_que_va_de_la_partida_a_la_meta()
    {
        Assert.True(GoalRules.Reached(90m, 80m, 79m));
        Assert.True(GoalRules.Reached(90m, 80m, 80m));
        Assert.False(GoalRules.Reached(90m, 80m, 81m));
        Assert.True(GoalRules.Reached(60m, 70m, 71m));
        Assert.False(GoalRules.Reached(60m, 70m, 69m));
        Assert.Null(GoalRules.Reached(60m, 70m, null));
    }

    // ---- Entidad ----

    [Fact]
    public void El_objetivo_se_cierra_con_fecha_y_nota_limpia()
    {
        var goal = Goal.Create(Guid.NewGuid(), Guid.NewGuid(), "  Bajar cintura  ", null, GoalCategory.BodyMetric, GoalMetric.WaistCm, 80m, 90m, Start, null, Guid.NewGuid(), DateTimeOffset.UnixEpoch);
        Assert.Equal(("Bajar cintura", GoalStatus.Active), (goal.Title, goal.Status));

        goal.Close(GoalStatus.Abandoned, Start.AddDays(10), "  Lesión  ");

        Assert.Equal((GoalStatus.Abandoned, Start.AddDays(10), "Lesión"), (goal.Status, goal.ClosedOn, goal.CloseNote));
    }

    // ---- Adherencia ----

    [Theory]
    [InlineData("2026-03-02", "2026-03-02")]
    [InlineData("2026-03-04", "2026-03-02")]
    [InlineData("2026-03-08", "2026-03-02")]
    [InlineData("2026-03-09", "2026-03-09")]
    public void Las_semanas_van_de_lunes_a_domingo(string day, string monday) =>
        Assert.Equal(DateOnly.Parse(monday), AdherenceRules.WeekStart(DateOnly.Parse(day)));

    [Theory]
    [InlineData(3, 7, 3)]
    [InlineData(3, 3, 2)]
    [InlineData(3, 1, 1)]
    [InlineData(4, 0, 0)]
    [InlineData(0, 7, 0)]
    [InlineData(5, 7, 5)]
    public void Las_sesiones_esperadas_son_los_dias_de_la_rutina_prorrateados_hacia_arriba(int planDays, int covered, int expected) =>
        Assert.Equal(expected, AdherenceRules.ExpectedSessions(planDays, covered));

    [Fact]
    public void El_porcentaje_de_sesiones_no_pasa_de_100_y_es_nulo_sin_nada_esperado()
    {
        Assert.Equal(67m, AdherenceRules.Percent(2, 3));
        Assert.Equal(100m, AdherenceRules.Percent(5, 3));
        Assert.Equal(0m, AdherenceRules.Percent(0, 3));
        Assert.Null(AdherenceRules.Percent(2, 0));
    }

    [Fact]
    public void En_nutricion_las_parciales_valen_la_mitad_y_lo_no_registrado_cuenta_cero()
    {
        Assert.Equal(75m, AdherenceRules.NutritionPercent(2, 2, 4));
        Assert.Equal(50m, AdherenceRules.NutritionPercent(2, 0, 4));
        Assert.Equal(0m, AdherenceRules.NutritionPercent(0, 0, 4));
        Assert.Null(AdherenceRules.NutritionPercent(0, 0, 0));
    }
}
