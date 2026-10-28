using FitCore.Wellness.Domain;

namespace FitCore.Wellness.UnitTests;

public class NutritionDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 3, 10);

    private static FoodPortion Food(decimal quantity = 100, string unit = "g", int? kcal = null, decimal? p = null, decimal? c = null, decimal? f = null) =>
        new("Arroz", quantity, unit, new FoodNutrients(kcal, p, c, f), null);

    // ---- Alimentos ----

    [Fact]
    public void Un_alimento_sin_valores_nutricionales_es_valido()
    {
        Assert.Null(NutritionRules.ValidatePortion(Food()));
        Assert.Null(NutritionRules.ValidatePortion(Food(kcal: 130, p: 2.7m, c: 28m, f: 0.3m)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(100001)]
    public void La_cantidad_debe_ser_positiva_y_razonable(int quantity) =>
        Assert.Equal("nutrition.quantity_invalid", NutritionRules.ValidatePortion(Food(quantity)));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void La_unidad_es_obligatoria(string unit) =>
        Assert.Equal("nutrition.unit_required", NutritionRules.ValidatePortion(Food(unit: unit)));

    [Fact]
    public void Los_valores_nutricionales_no_pueden_ser_negativos_ni_absurdos()
    {
        Assert.Equal("nutrition.nutrients_invalid", NutritionRules.ValidatePortion(Food(kcal: -1)));
        Assert.Equal("nutrition.nutrients_invalid", NutritionRules.ValidatePortion(Food(kcal: 5001)));
        Assert.Equal("nutrition.nutrients_invalid", NutritionRules.ValidatePortion(Food(p: -0.1m)));
        Assert.Equal("nutrition.nutrients_invalid", NutritionRules.ValidatePortion(Food(c: 1001m)));
        Assert.Equal("nutrition.nutrients_invalid", NutritionRules.ValidatePortion(Food(f: 2000m)));
        Assert.Null(NutritionRules.ValidatePortion(Food(kcal: 0, p: 0m, c: 0m, f: 0m)));
    }

    // ---- Objetivos ----

    [Fact]
    public void Los_objetivos_son_opcionales_pero_si_se_dan_deben_ser_razonables()
    {
        Assert.Null(NutritionRules.ValidateTargets(new NutritionTargets(null, null, null, null)));
        Assert.Null(NutritionRules.ValidateTargets(new NutritionTargets(2000, 120, 250, 70)));
        Assert.Equal("nutrition.targets_invalid", NutritionRules.ValidateTargets(new NutritionTargets(299, null, null, null)));
        Assert.Equal("nutrition.targets_invalid", NutritionRules.ValidateTargets(new NutritionTargets(10001, null, null, null)));
        Assert.Equal("nutrition.targets_invalid", NutritionRules.ValidateTargets(new NutritionTargets(2000, -1, null, null)));
        Assert.Equal("nutrition.targets_invalid", NutritionRules.ValidateTargets(new NutritionTargets(2000, null, 1001, null)));
        Assert.Equal("nutrition.targets_invalid", NutritionRules.ValidateTargets(new NutritionTargets(null, null, null, 1001)));
    }

    // ---- Estimación ----

    [Fact]
    public void Las_calorias_de_una_comida_suman_solo_lo_conocido_y_no_inventan_un_cero()
    {
        Assert.Equal(430, NutritionRules.EstimatedCalories([130, null, 300]));
        Assert.Null(NutritionRules.EstimatedCalories([null, null]));
        Assert.Null(NutritionRules.EstimatedCalories([]));
        Assert.Equal(0, NutritionRules.EstimatedCalories([0]));
    }

    // ---- Perfil alimentario ----

    [Fact]
    public void El_perfil_alimentario_limpia_vacios_y_repetidos_sin_distinguir_mayusculas()
    {
        var profile = DietaryProfile.Create(
            Guid.NewGuid(), Guid.NewGuid(), ["  Maní ", "maní", "", "Mariscos"], ["lactosa", "LACTOSA"], null, "  Alergia severa al maní  ", Guid.NewGuid(), Now);

        Assert.Equal(["Maní", "Mariscos"], profile.Allergies);
        Assert.Equal(["lactosa"], profile.Intolerances);
        Assert.Empty(profile.Restrictions);
        Assert.Equal("Alergia severa al maní", profile.Notes);
    }

    // ---- Versiones ----

    [Fact]
    public void La_cabecera_solo_apunta_a_la_version_vigente_y_cada_version_conserva_sus_datos()
    {
        var tenant = Guid.NewGuid();
        var plan = NutritionPlan.Create(tenant, Guid.NewGuid(), Now);
        Assert.Equal(0, plan.CurrentVersion);

        var header = new NutritionHeader("Definición", "Perder grasa", null, new NutritionTargets(1800, 130, 180, 55), null, null, null);
        var v1 = NutritionPlanVersion.Create(tenant, plan.Id, 1, header, "  Inicial  ", null, Now);
        plan.PublishVersion(v1, null, Now);
        var v2 = NutritionPlanVersion.Create(tenant, plan.Id, 2, header with { Name = "Definición 2", Targets = new NutritionTargets(1700, 130, 160, 55) }, null, null, Now.AddDays(7));
        plan.PublishVersion(v2, null, Now.AddDays(7));

        Assert.Equal(2, plan.CurrentVersion);
        Assert.Equal(v2.Id, plan.CurrentVersionId);
        Assert.Equal("Inicial", v1.ChangeNote);
        Assert.Equal((1800, "Definición"), (v1.TargetCalories, v1.Name));
        Assert.Equal((1700, "Definición 2"), (v2.TargetCalories, v2.Name));
    }

    // ---- Asignaciones ----

    private static NutritionAssignment Assignment(DateOnly start, DateOnly? end = null) =>
        NutritionAssignment.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), start, end, Guid.NewGuid(), Now);

    [Fact]
    public void Una_asignacion_rige_desde_su_inicio_hasta_su_fin_programado_o_su_cierre()
    {
        var a = Assignment(Today, Today.AddDays(10));
        Assert.False(a.IsCurrentOn(Today.AddDays(-1)));
        Assert.True(a.IsCurrentOn(Today));
        Assert.True(a.IsCurrentOn(Today.AddDays(10)));
        Assert.False(a.IsCurrentOn(Today.AddDays(11)));

        a.End(Today.AddDays(4), "  Cambio de objetivo  ");
        Assert.Equal(Today.AddDays(4), a.LastDay);
        Assert.Equal("Cambio de objetivo", a.EndReason);
        Assert.False(a.IsCurrentOn(Today.AddDays(5)));
    }

    [Fact]
    public void Una_asignacion_cerrada_antes_de_empezar_nunca_rige()
    {
        var a = Assignment(Today.AddDays(5));
        a.End(Today.AddDays(4), "Se canceló antes de empezar");

        Assert.False(a.IsCurrentOn(Today.AddDays(5)));
        Assert.False(a.IsCurrentOn(Today.AddDays(9)));
    }

    // ---- Registros de cumplimiento ----

    [Fact]
    public void El_registro_de_una_comida_guarda_la_version_con_que_se_hizo_y_limpia_la_nota()
    {
        var version = Guid.NewGuid();
        var log = MealLog.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), version, Guid.NewGuid(), Today, MealLogStatus.Partial, "  Comí la mitad  ", Guid.NewGuid(), Now);

        Assert.Equal(version, log.PlanVersionId);
        Assert.Equal(MealLogStatus.Partial, log.Status);
        Assert.Equal("Comí la mitad", log.Notes);
    }
}
