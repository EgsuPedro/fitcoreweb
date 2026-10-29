using FitCore.SharedKernel;
using FitCore.Subscriptions.Application.Plans;
using FitCore.Subscriptions.Domain;

namespace FitCore.Subscriptions.UnitTests;

public class PlanTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    private static Dictionary<string, long?> Limits(long? members = 100) => new()
    {
        [Capabilities.Limits.Branches] = 1,
        [Capabilities.Limits.InternalUsers] = 5,
        [Capabilities.Limits.Members] = members,
        [Capabilities.Limits.StorageMegabytes] = null
    };

    private static PlanTerms Terms(decimal price = 30m, long? members = 100, params string[] features) =>
        new("USD", BillingPeriod.Monthly, price, 14, null, features.Length == 0 ? [Capabilities.Features.Members] : features, Limits(members));

    [Theory]
    [InlineData("free", true)]
    [InlineData("pro-2026", true)]
    [InlineData("plan_basico", true)]
    [InlineData("a", false)]
    [InlineData("ab", false)]
    [InlineData("-free", false)]
    [InlineData("free-", false)]
    [InlineData("Con Espacios", false)]
    [InlineData("ñandú", false)]
    public void El_codigo_de_un_plan_usa_un_formato_estable(string code, bool valid) =>
        Assert.Equal(valid, Plan.IsValidCode(code));

    [Theory]
    [InlineData(BillingPeriod.Monthly, 1)]
    [InlineData(BillingPeriod.Quarterly, 3)]
    [InlineData(BillingPeriod.Yearly, 12)]
    public void Cada_periodicidad_cubre_sus_meses(BillingPeriod period, int months) =>
        Assert.Equal(months, BillingPeriods.Months(period));

    [Fact]
    public void Crear_un_plan_normaliza_el_codigo_y_abre_la_version_uno()
    {
        var plan = Plan.Create("  PRO-2026 ", "  Profesional ", "  ", " reglas ", true, Terms(), "Alta", null, Now);

        Assert.Equal("pro-2026", plan.Code);
        Assert.Equal("Profesional", plan.Name);
        Assert.Null(plan.Description);
        Assert.Equal("reglas", plan.Rules);
        Assert.Single(plan.Versions);
        Assert.Equal(1, plan.Current.Number);
        Assert.Equal(30m, plan.Current.Price);
    }

    [Fact]
    public void Agregar_una_version_conserva_la_anterior_intacta_y_la_nueva_es_la_vigente()
    {
        var plan = Plan.Create("pro", "Pro", null, null, true, Terms(30m), null, null, Now);
        var first = plan.Current;

        var second = plan.AddVersion(Terms(45m), "Sube el precio", null, Now.AddDays(1));

        Assert.Equal(2, plan.Versions.Count);
        Assert.Same(second, plan.Current);
        Assert.Equal(2, second.Number);
        Assert.Equal(30m, first.Price);
        Assert.Equal(45m, second.Price);
        Assert.Equal(Now.AddDays(1), plan.UpdatedAt);
    }

    [Fact]
    public void Las_mismas_condiciones_no_cuentan_como_cambio_sin_importar_el_orden()
    {
        var a = Terms(30m, 100, Capabilities.Features.Members, Capabilities.Features.Classes);
        var b = Terms(30m, 100, Capabilities.Features.Classes, Capabilities.Features.Members);

        Assert.True(a.SameAs(b));
    }

    [Fact]
    public void Cualquier_condicion_comercial_distinta_es_un_cambio()
    {
        var baseTerms = Terms();

        Assert.False(baseTerms.SameAs(Terms(price: 30.01m)));
        Assert.False(baseTerms.SameAs(Terms(members: 101)));
        Assert.False(baseTerms.SameAs(Terms(members: null)));
        Assert.False(baseTerms.SameAs(Terms(features: [Capabilities.Features.Members, Capabilities.Features.Exports])));
        Assert.False(baseTerms.SameAs(baseTerms with { TrialDays = 0 }));
        Assert.False(baseTerms.SameAs(baseTerms with { GraceDays = 3 }));
        Assert.False(baseTerms.SameAs(baseTerms with { Period = BillingPeriod.Yearly }));
        Assert.False(baseTerms.SameAs(baseTerms with { Currency = "EUR" }));
    }

    [Fact]
    public void Los_terminos_de_una_version_se_reconstruyen_igual_que_se_guardaron()
    {
        var terms = Terms(19.99m, null, Capabilities.Features.Attendance, Capabilities.Features.Members);
        var plan = Plan.Create("pro", "Pro", null, null, true, terms, null, null, Now);

        Assert.True(plan.Current.ToTerms().SameAs(terms));
    }

    [Fact]
    public void Desactivar_y_editar_datos_descriptivos_no_cambian_las_condiciones()
    {
        var plan = Plan.Create("pro", "Pro", null, null, true, Terms(), null, null, Now);
        var before = plan.Current.ToTerms();

        plan.SetActive(false, null, Now.AddHours(1));
        plan.UpdateDescriptive("Otro nombre", "Otra", null, null, Now.AddHours(2));

        Assert.False(plan.IsActive);
        Assert.Single(plan.Versions);
        Assert.True(plan.Current.ToTerms().SameAs(before));
    }

    [Fact]
    public void El_catalogo_de_capacidades_no_repite_codigos()
    {
        Assert.Equal(Capabilities.FeatureCatalog.Count, Capabilities.FeatureCatalog.Select(f => f.Code).Distinct().Count());
        Assert.Equal(Capabilities.LimitCatalog.Count, Capabilities.LimitCatalog.Select(l => l.Code).Distinct().Count());
        Assert.True(Capabilities.IsFeature("classes"));
        Assert.False(Capabilities.IsFeature("storage_mb"));
        Assert.True(Capabilities.IsLimit("storage_mb"));
        Assert.False(Capabilities.IsLimit("classes"));
    }
}

public class PlanValidatorTests
{
    private static readonly PlanLimitDto[] AllLimits =
        [new("branches", 1), new("internal_users", 5), new("members", 100), new("storage_mb", null)];

    private static CreatePlanRequest Valid(
        string code = "pro", decimal price = 30m, int trial = 14, int? grace = null, string[]? features = null, PlanLimitDto[]? limits = null, string currency = "USD") =>
        new(code, "Pro", null, null, true, currency, BillingPeriod.Monthly, price, trial, grace, features ?? ["members"], limits ?? AllLimits, null);

    private static bool IsValid(CreatePlanRequest request) => new CreatePlanRequestValidator().Validate(request).IsValid;

    [Fact]
    public void Una_solicitud_correcta_es_valida() => Assert.True(IsValid(Valid()));

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1000000.01)]
    [InlineData(10.999)]
    public void Los_precios_fuera_de_rango_o_con_mas_de_dos_decimales_se_rechazan(double price) =>
        Assert.False(IsValid(Valid(price: (decimal)price)));

    [Theory]
    [InlineData(0)]
    [InlineData(29.99)]
    [InlineData(1000000)]
    public void Los_precios_validos_incluido_el_cero_se_aceptan(double price) =>
        Assert.True(IsValid(Valid(price: (decimal)price)));

    [Fact]
    public void Un_limite_omitido_no_se_interpreta_como_ilimitado() =>
        Assert.False(IsValid(Valid(limits: [new("branches", 1), new("members", 10)])));

    [Fact]
    public void Un_limite_nulo_explicito_significa_sin_limite() =>
        Assert.True(IsValid(Valid(limits: [new("branches", null), new("internal_users", null), new("members", null), new("storage_mb", null)])));

    [Fact]
    public void Los_limites_negativos_repetidos_o_desconocidos_se_rechazan()
    {
        Assert.False(IsValid(Valid(limits: [new("branches", -1), new("internal_users", 1), new("members", 1), new("storage_mb", 1)])));
        Assert.False(IsValid(Valid(limits: [.. AllLimits, new PlanLimitDto("members", 5)])));
        Assert.False(IsValid(Valid(limits: [.. AllLimits, new PlanLimitDto("otra_cosa", 5)])));
    }

    [Fact]
    public void Las_funciones_desconocidas_o_repetidas_se_rechazan()
    {
        Assert.False(IsValid(Valid(features: ["members", "magia"])));
        Assert.False(IsValid(Valid(features: ["members", "members"])));
        Assert.True(IsValid(Valid(features: [])));
    }

    [Theory]
    [InlineData(-1, null)]
    [InlineData(366, null)]
    [InlineData(0, -1)]
    [InlineData(0, 91)]
    public void La_prueba_y_la_gracia_tienen_rangos(int trial, int? grace) =>
        Assert.False(IsValid(Valid(trial: trial, grace: grace)));

    [Fact]
    public void Solo_se_admiten_las_monedas_configuradas()
    {
        Assert.True(IsValid(Valid(currency: "USD")));
        Assert.False(IsValid(Valid(currency: "EUR")));
        Assert.False(IsValid(Valid(currency: "usd")));
    }
}
