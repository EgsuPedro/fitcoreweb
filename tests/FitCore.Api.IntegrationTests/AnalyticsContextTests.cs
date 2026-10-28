using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FitCore.Api.IntegrationTests.Infrastructure;

namespace FitCore.Api.IntegrationTests;

/// <summary>El contexto común de los reportes (bloque 2): período en la hora del gimnasio, sucursales autorizadas y áreas permitidas.</summary>
[Collection(ApiCollection3.Name)]
public class AnalyticsContextTests(ApiFixture api)
{
    private sealed record PeriodRow(string From, string To, int Days, DateTimeOffset StartUtc, DateTimeOffset EndUtcExclusive);

    private sealed record BranchRow(Guid Id, string Code, string Name, string TimeZone, bool IsActive);

    private sealed record SectionsRow(bool Financial, bool Members, bool Operations, bool Instructor, bool Compare, bool Export);

    private sealed record ContextRow(
        string Currency, string TimeZone, string Today, string Preset, PeriodRow Period, PeriodRow Comparable, Guid[] BranchIds, BranchRow[] Branches,
        SectionsRow Sections, int MaxRangeDays);

    private static async Task<ContextRow> ContextAsync(HttpClient client, string query = "")
    {
        var response = await client.GetAsync($"/api/v1/analytics/context{query}");
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Respondió {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<ContextRow>(new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.True(response.StatusCode == status, $"Respondió {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        Assert.Equal(code, await TestApi.ProblemCodeAsync(response));
    }

    private async Task<HttpClient> StaffAsync(TestTenant tenant, string email, string role, params Guid[] branches)
    {
        await TestApi.CreateUserAsync(tenant.Owner, email, role, branches);
        return TestApi.Authenticated(api, await TestApi.LoginAsync(api, tenant.Slug, email, TestApi.UserPassword));
    }

    [Fact]
    public async Task El_propietario_obtiene_el_periodo_en_la_hora_del_gimnasio_con_su_equivalente_en_UTC_y_el_comparable()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var uio = await TestApi.CreateBranchAsync(tenant.Owner, "UIO");
        var gye = await TestApi.CreateBranchAsync(tenant.Owner, "GYE");

        var context = await ContextAsync(tenant.Owner, "?preset=Custom&from=2026-03-01&to=2026-03-10");
        Assert.Equal("USD", context.Currency);
        Assert.Equal("America/Guayaquil", context.TimeZone);
        Assert.Equal(10, context.Period.Days);

        // 1 de marzo 00:00 en Guayaquil (UTC−5) = 05:00 UTC; el final es el 11 de marzo 00:00 local, excluido.
        Assert.Equal(DateTimeOffset.Parse("2026-03-01T05:00:00Z"), context.Period.StartUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-03-11T05:00:00Z"), context.Period.EndUtcExclusive);

        // El comparable dura lo mismo y termina justo antes: 19–28 de febrero.
        Assert.Equal("2026-02-19", context.Comparable.From);
        Assert.Equal("2026-02-28", context.Comparable.To);
        Assert.Equal(10, context.Comparable.Days);

        Assert.Equal(new[] { uio, gye }.Order(), context.BranchIds.Order());
        Assert.Equal(2, context.Branches.Length);
        Assert.Equal(366, context.MaxRangeDays);
        Assert.True(context.Sections.Financial && context.Sections.Members && context.Sections.Operations && context.Sections.Compare && context.Sections.Export);
    }

    [Fact]
    public async Task Una_sola_sucursal_usa_su_propia_zona_horaria()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var uio = await TestApi.CreateBranchAsync(tenant.Owner, "UIO");
        var gps = await TestApi.CreateBranchAsync(tenant.Owner, "GPS");
        var update = await tenant.Owner.PutAsJsonAsync($"/api/v1/branches/{gps}", new { name = "Galápagos", timeZone = "Pacific/Galapagos", isActive = true });
        Assert.True(update.IsSuccessStatusCode, await update.Content.ReadAsStringAsync());

        var galapagos = await ContextAsync(tenant.Owner, $"?preset=Custom&from=2026-03-01&to=2026-03-01&branchId={gps}");
        Assert.Equal("Pacific/Galapagos", galapagos.TimeZone);
        Assert.Equal(DateTimeOffset.Parse("2026-03-01T06:00:00Z"), galapagos.Period.StartUtc);

        var continental = await ContextAsync(tenant.Owner, $"?preset=Custom&from=2026-03-01&to=2026-03-01&branchId={uio}");
        Assert.Equal("America/Guayaquil", continental.TimeZone);

        // Varias sucursales: la zona del gimnasio.
        var both = await ContextAsync(tenant.Owner, $"?preset=Custom&from=2026-03-01&to=2026-03-01&branchId={uio}&branchId={gps}");
        Assert.Equal("America/Guayaquil", both.TimeZone);
    }

    [Fact]
    public async Task Los_rangos_invalidos_se_rechazan_con_su_codigo()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        await TestApi.CreateBranchAsync(tenant.Owner, "UIO");

        await AssertProblemAsync(await tenant.Owner.GetAsync("/api/v1/analytics/context?preset=Custom"), HttpStatusCode.UnprocessableEntity, "analytics.period_required");
        await AssertProblemAsync(await tenant.Owner.GetAsync("/api/v1/analytics/context?preset=Custom&from=2026-03-10&to=2026-03-01"), HttpStatusCode.UnprocessableEntity, "analytics.period_invalid");
        await AssertProblemAsync(await tenant.Owner.GetAsync("/api/v1/analytics/context?preset=Custom&from=2025-01-01&to=2026-03-01"), HttpStatusCode.UnprocessableEntity, "analytics.period_too_long");
        await AssertProblemAsync(await tenant.Owner.GetAsync("/api/v1/analytics/context?preset=Last7Days&from=2026-03-01&to=2026-03-02"), HttpStatusCode.UnprocessableEntity, "analytics.period_preset_with_dates");
        Assert.Equal(HttpStatusCode.BadRequest, (await tenant.Owner.GetAsync("/api/v1/analytics/context?preset=Siempre")).StatusCode);

        // Exactamente 366 días sí se permite.
        var allowed = await ContextAsync(tenant.Owner, "?preset=Custom&from=2025-03-01&to=2026-03-01");
        Assert.Equal(366, allowed.Period.Days);
    }

    [Fact]
    public async Task Cada_usuario_ve_solo_sus_sucursales_y_las_areas_de_su_rol()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var uio = await TestApi.CreateBranchAsync(tenant.Owner, "UIO");
        var gye = await TestApi.CreateBranchAsync(tenant.Owner, "GYE");

        var manager = await StaffAsync(tenant, "gerente@gym.test", "AdministradorSucursal", uio);
        var managerContext = await ContextAsync(manager);
        Assert.Equal([uio], managerContext.BranchIds);
        Assert.Equal([uio], managerContext.Branches.Select(b => b.Id));
        Assert.True(managerContext.Sections.Financial && managerContext.Sections.Compare);

        // Pedir otra sucursal del mismo gimnasio, o una que no existe en este gimnasio, se rechaza igual (sin revelar si existe).
        await AssertProblemAsync(await manager.GetAsync($"/api/v1/analytics/context?branchId={gye}"), HttpStatusCode.Forbidden, "branches.not_allowed");
        await AssertProblemAsync(await manager.GetAsync($"/api/v1/analytics/context?branchId={Guid.NewGuid()}"), HttpStatusCode.Forbidden, "branches.not_allowed");

        var cashier = await StaffAsync(tenant, "cajero@gym.test", "Cajero", uio);
        var cashierSections = (await ContextAsync(cashier)).Sections;
        Assert.True(cashierSections.Financial);
        Assert.False(cashierSections.Members || cashierSections.Operations || cashierSections.Compare || cashierSections.Export || cashierSections.Instructor);

        var reception = await StaffAsync(tenant, "recep@gym.test", "Recepcion", uio);
        var receptionSections = (await ContextAsync(reception)).Sections;
        Assert.True(receptionSections.Operations);
        Assert.False(receptionSections.Financial || receptionSections.Members || receptionSections.Compare || receptionSections.Export);

        var trainer = await StaffAsync(tenant, "entrenador@gym.test", "Entrenador", uio);
        var trainerSections = (await ContextAsync(trainer)).Sections;
        Assert.True(trainerSections.Instructor);
        Assert.False(trainerSections.Financial || trainerSections.Members || trainerSections.Operations || trainerSections.Compare || trainerSections.Export);

        var accountant = await StaffAsync(tenant, "contador@gym.test", "Contador", uio);
        var accountantContext = await ContextAsync(accountant);
        Assert.Equal(2, accountantContext.Branches.Length);
        Assert.True(accountantContext.Sections.Financial && accountantContext.Sections.Members && accountantContext.Sections.Compare && accountantContext.Sections.Export);
        Assert.False(accountantContext.Sections.Operations);
    }

    [Fact]
    public async Task Sin_el_permiso_de_analitica_o_sin_sesion_no_hay_contexto_y_un_gimnasio_no_ve_las_sucursales_de_otro()
    {
        var a = await TestApi.NewTenantAsync(api);
        var b = await TestApi.NewTenantAsync(api);
        var branchA = await TestApi.CreateBranchAsync(a.Owner, "AAA");
        var branchB = await TestApi.CreateBranchAsync(b.Owner, "BBB");

        var nutritionist = await StaffAsync(a, "nutri@gym.test", "Nutricionista", branchA);
        Assert.Equal(HttpStatusCode.Forbidden, (await nutritionist.GetAsync("/api/v1/analytics/context")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().GetAsync("/api/v1/analytics/context")).StatusCode);

        // El propietario de A solo ve su sucursal: la de B no existe para él, ni al listar ni al pedirla.
        var contextA = await ContextAsync(a.Owner);
        Assert.Equal([branchA], contextA.Branches.Select(x => x.Id));
        await AssertProblemAsync(await a.Owner.GetAsync($"/api/v1/analytics/context?branchId={branchB}"), HttpStatusCode.Forbidden, "branches.not_allowed");
        Assert.Equal([branchB], (await ContextAsync(b.Owner)).Branches.Select(x => x.Id));
    }
}
