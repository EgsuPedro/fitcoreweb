using System.Net;
using System.Net.Http.Json;
using FitCore.Api.IntegrationTests.Infrastructure;
using static FitCore.Api.IntegrationTests.Infrastructure.CommercialTestApi;

namespace FitCore.Api.IntegrationTests;

[Collection(ApiCollection4.Name)]
public class PromotionTests(ApiFixture api)
{
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    [Fact]
    public async Task Crear_una_promocion_valida_normaliza_el_codigo_y_queda_activa()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var plan = await CreatePlanAsync(tenant.Owner, "Mensual");

        var promotion = await CreatePromotionAsync(tenant.Owner, "Verano", code: " verano-20 ", value: 20m, planIds: [plan.Id]);

        Assert.Equal("VERANO-20", promotion.Code);
        Assert.Equal("Percentage", promotion.DiscountType);
        Assert.Equal("Active", promotion.Status);
        Assert.Equal(0, promotion.UsedCount);
        Assert.Equal([plan.Id], promotion.PlanIds);
        Assert.Equal(promotion.Id, (await tenant.Owner.GetFromJsonAsync<PromotionDto>($"/api/v1/promotions/{promotion.Id}"))!.Id);
    }

    [Fact]
    public async Task El_descuento_no_puede_superar_el_maximo_autorizado_del_gimnasio()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        await CreatePlanAsync(tenant.Owner, "Mensual", price: 50m);

        // Por defecto el máximo es 30 %.
        var above = await tenant.Owner.PostAsJsonAsync("/api/v1/promotions", PromotionBody("Regalo", value: 31m));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, above.StatusCode);
        Assert.Equal("promotions.discount_above_maximum", await TestApi.ProblemCodeAsync(above));
        Assert.Equal(HttpStatusCode.Created, (await tenant.Owner.PostAsJsonAsync("/api/v1/promotions", PromotionBody("Justo", value: 30m))).StatusCode);

        // Un monto fijo se compara contra el plan más barato: 30 % de 50.00 = 15.00.
        var fixedAbove = await tenant.Owner.PostAsJsonAsync("/api/v1/promotions", PromotionBody("Fijo alto", "FixedAmount", 15.01m));
        Assert.Equal("promotions.discount_above_maximum", await TestApi.ProblemCodeAsync(fixedAbove));
        Assert.Equal(HttpStatusCode.Created, (await tenant.Owner.PostAsJsonAsync("/api/v1/promotions", PromotionBody("Fijo justo", "FixedAmount", 15m))).StatusCode);

        // Al autorizar un máximo mayor, el mismo descuento pasa.
        var settings = await tenant.Owner.PutAsJsonAsync("/api/v1/commercial-settings",
            new { accessPolicy = "AllowUntilOverdue", minPaymentPercent = 50, dueDays = 7, maxDiscountPercent = 50 });
        Assert.Equal(HttpStatusCode.OK, settings.StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await tenant.Owner.PostAsJsonAsync("/api/v1/promotions", PromotionBody("Ahora si", value: 31m))).StatusCode);
    }

    [Fact]
    public async Task Validacion_rechaza_valores_fechas_usos_y_codigos_invalidos()
    {
        var tenant = await TestApi.NewTenantAsync(api);

        async Task<HttpResponseMessage> PostAsync(object body) => await tenant.Owner.PostAsJsonAsync("/api/v1/promotions", body);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostAsync(PromotionBody("P", value: 0m))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostAsync(PromotionBody("P", value: -5m))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostAsync(PromotionBody("P", value: 101m))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostAsync(PromotionBody("", value: 5m))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostAsync(PromotionBody("P", value: 5m, validFrom: Today.AddDays(5), validUntil: Today))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostAsync(PromotionBody("P", value: 5m, maxUses: 0))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostAsync(PromotionBody("P", value: 5m, code: "a b"))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostAsync(PromotionBody("P", value: 5m, code: "AB"))).StatusCode);

        var none = await tenant.Owner.GetFromJsonAsync<PagedDto<PromotionDto>>("/api/v1/promotions");
        Assert.Empty(none!.Items);
    }

    [Fact]
    public async Task El_codigo_no_se_repite_en_el_gimnasio_pero_si_en_otro_y_varias_sin_codigo_conviven()
    {
        var a = await TestApi.NewTenantAsync(api);
        var b = await TestApi.NewTenantAsync(api);
        await CreatePromotionAsync(a.Owner, "Primera", code: "PROMO10");

        var duplicate = await a.Owner.PostAsJsonAsync("/api/v1/promotions", PromotionBody("Segunda", code: "promo10"));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("promotions.code_taken", await TestApi.ProblemCodeAsync(duplicate));
        Assert.Equal(HttpStatusCode.Created, (await b.Owner.PostAsJsonAsync("/api/v1/promotions", PromotionBody("Otra", code: "PROMO10"))).StatusCode);

        await CreatePromotionAsync(a.Owner, "Sin código 1");
        await CreatePromotionAsync(a.Owner, "Sin código 2");
    }

    [Fact]
    public async Task El_estado_refleja_vigencia_y_activacion_y_se_puede_filtrar()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var active = await CreatePromotionAsync(tenant.Owner, "Vigente");
        var expired = (await (await tenant.Owner.PostAsJsonAsync("/api/v1/promotions",
            PromotionBody("Vencida", validFrom: Today.AddDays(-20), validUntil: Today.AddDays(-1)))).Content.ReadFromJsonAsync<PromotionDto>())!;
        var scheduled = (await (await tenant.Owner.PostAsJsonAsync("/api/v1/promotions",
            PromotionBody("Futura", validFrom: Today.AddDays(5), validUntil: Today.AddDays(15)))).Content.ReadFromJsonAsync<PromotionDto>())!;
        var inactive = (await (await tenant.Owner.PostAsJsonAsync("/api/v1/promotions", PromotionBody("Apagada", isActive: false))).Content.ReadFromJsonAsync<PromotionDto>())!;

        Assert.Equal("Expired", expired.Status);
        Assert.Equal("Scheduled", scheduled.Status);
        Assert.Equal("Inactive", inactive.Status);

        async Task<Guid[]> ListAsync(string status) => [.. (await tenant.Owner.GetFromJsonAsync<PagedDto<PromotionDto>>($"/api/v1/promotions?status={status}"))!.Items.Select(p => p.Id)];
        Assert.Equal([active.Id], await ListAsync("Active"));
        Assert.Equal([expired.Id], await ListAsync("Expired"));
        Assert.Equal([scheduled.Id], await ListAsync("Scheduled"));
        Assert.Equal([inactive.Id], await ListAsync("Inactive"));
    }

    [Fact]
    public async Task Una_promocion_no_puede_apuntar_a_planes_inexistentes_ni_de_otro_gimnasio()
    {
        var a = await TestApi.NewTenantAsync(api);
        var b = await TestApi.NewTenantAsync(api);
        var planB = await CreatePlanAsync(b.Owner, "Plan de B");

        var foreign = await a.Owner.PostAsJsonAsync("/api/v1/promotions", PromotionBody("Ajena", planIds: [planB.Id]));
        var missing = await a.Owner.PostAsJsonAsync("/api/v1/promotions", PromotionBody("Nada", planIds: [Guid.NewGuid()]));

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal("plans.not_found", await TestApi.ProblemCodeAsync(foreign));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Editar_una_promocion_cambia_sus_datos_y_la_puede_desactivar()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var promotion = await CreatePromotionAsync(tenant.Owner, "Original", code: "ORIG", value: 10m);

        var response = await tenant.Owner.PutAsJsonAsync($"/api/v1/promotions/{promotion.Id}",
            PromotionBody("Editada", "FixedAmount", 5m, code: "ORIG", maxUses: 100, isActive: false));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = (await response.Content.ReadFromJsonAsync<PromotionDto>())!;
        Assert.Equal("Editada", updated.Name);
        Assert.Equal("FixedAmount", updated.DiscountType);
        Assert.Equal(100, updated.MaxUses);
        Assert.Equal("Inactive", updated.Status);

        // Conservar su propio código no cuenta como duplicado.
        Assert.Equal("ORIG", updated.Code);
    }

    [Fact]
    public async Task Los_permisos_se_aplican_y_solo_quien_accede_a_todas_las_sucursales_define_descuentos()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var branch = await TestApi.CreateBranchAsync(tenant.Owner, "PR");
        var promotion = await CreatePromotionAsync(tenant.Owner, "Existente");
        await TestApi.CreateUserAsync(tenant.Owner, "recepcion@gym.test", "Recepcion", branch);
        using var reception = TestApi.Authenticated(api, await TestApi.LoginAsync(api, tenant.Slug, "recepcion@gym.test", TestApi.UserPassword));
        using var scopedManager = await ClientWithRoleAsync(api, tenant, "gestor@gym.test", false, ["promotions.read", "promotions.manage", "plans.read"], branch);

        Assert.Equal(HttpStatusCode.OK, (await reception.GetAsync($"/api/v1/promotions/{promotion.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reception.PostAsJsonAsync("/api/v1/promotions", PromotionBody("X"))).StatusCode);

        var create = await scopedManager.PostAsJsonAsync("/api/v1/promotions", PromotionBody("Con alcance parcial"));
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal("auth.all_branches_required", await TestApi.ProblemCodeAsync(create));
        Assert.Equal(HttpStatusCode.Forbidden, (await scopedManager.PutAsJsonAsync($"/api/v1/promotions/{promotion.Id}", PromotionBody("Hack"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().GetAsync("/api/v1/promotions")).StatusCode);
    }

    [Fact]
    public async Task Una_promocion_de_otro_gimnasio_responde_404_y_no_aparece_en_listados()
    {
        var a = await TestApi.NewTenantAsync(api);
        var b = await TestApi.NewTenantAsync(api);
        var promotionB = await CreatePromotionAsync(b.Owner, "De B");
        await CreatePromotionAsync(a.Owner, "De A");

        Assert.Equal(HttpStatusCode.NotFound, (await a.Owner.GetAsync($"/api/v1/promotions/{promotionB.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.Owner.PutAsJsonAsync($"/api/v1/promotions/{promotionB.Id}", PromotionBody("Hack"))).StatusCode);
        var list = await a.Owner.GetFromJsonAsync<PagedDto<PromotionDto>>("/api/v1/promotions?pageSize=100");
        Assert.Equal(["De A"], list!.Items.Select(p => p.Name));
    }
}
