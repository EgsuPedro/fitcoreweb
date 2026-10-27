using System.Net;
using System.Net.Http.Json;
using FitCore.Api.IntegrationTests.Infrastructure;
using static FitCore.Api.IntegrationTests.Infrastructure.CommercialTestApi;

namespace FitCore.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
public class SettingsAndTaxTests(ApiFixture api)
{
    [Fact]
    public async Task Sin_configurar_se_usan_las_politicas_por_defecto_acordadas()
    {
        var tenant = await TestApi.NewTenantAsync(api);

        var settings = await tenant.Owner.GetFromJsonAsync<SettingsDto>("/api/v1/commercial-settings");

        Assert.Equal("AllowUntilOverdue", settings!.AccessPolicy);
        Assert.Equal(50, settings.MinPaymentPercent);
        Assert.Equal(7, settings.DueDays);
        Assert.Equal(30, settings.MaxDiscountPercent);
    }

    [Fact]
    public async Task Actualizar_las_politicas_las_persiste_y_queda_auditado()
    {
        var tenant = await TestApi.NewTenantAsync(api);

        var update = await tenant.Owner.PutAsJsonAsync("/api/v1/commercial-settings",
            new { accessPolicy = "RequireFullPayment", minPaymentPercent = 80, dueDays = 15, maxDiscountPercent = 20 });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        var settings = await tenant.Owner.GetFromJsonAsync<SettingsDto>("/api/v1/commercial-settings");
        Assert.Equal(new SettingsDto("RequireFullPayment", 80, 15, 20), settings);

        var again = await tenant.Owner.PutAsJsonAsync("/api/v1/commercial-settings",
            new { accessPolicy = "AllowWithMinimumPayment", minPaymentPercent = 60, dueDays = 0, maxDiscountPercent = 0 });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);

        var audit = await tenant.Owner.GetFromJsonAsync<PagedDto<AuditDto>>("/api/v1/audit-logs?pageSize=100");
        Assert.Contains(audit!.Items, l => l.Action == "CommercialSettings.Created");
        Assert.Contains(audit.Items, l => l.Action == "CommercialSettings.Updated");
    }

    [Fact]
    public async Task Las_politicas_invalidas_se_rechazan()
    {
        var tenant = await TestApi.NewTenantAsync(api);

        foreach (var body in new object[]
        {
            new { accessPolicy = "AllowUntilOverdue", minPaymentPercent = 0, dueDays = 7, maxDiscountPercent = 30 },
            new { accessPolicy = "AllowUntilOverdue", minPaymentPercent = 101, dueDays = 7, maxDiscountPercent = 30 },
            new { accessPolicy = "AllowUntilOverdue", minPaymentPercent = 50, dueDays = -1, maxDiscountPercent = 30 },
            new { accessPolicy = "AllowUntilOverdue", minPaymentPercent = 50, dueDays = 91, maxDiscountPercent = 30 },
            new { accessPolicy = "AllowUntilOverdue", minPaymentPercent = 50, dueDays = 7, maxDiscountPercent = 101 }
        })
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await tenant.Owner.PutAsJsonAsync("/api/v1/commercial-settings", body)).StatusCode);
        }
    }

    [Fact]
    public async Task Las_politicas_son_de_cada_gimnasio_y_solo_las_cambia_quien_accede_a_todas_las_sucursales()
    {
        var a = await TestApi.NewTenantAsync(api);
        var b = await TestApi.NewTenantAsync(api);
        var branch = await TestApi.CreateBranchAsync(a.Owner, "SET");
        await a.Owner.PutAsJsonAsync("/api/v1/commercial-settings", new { accessPolicy = "RequireFullPayment", minPaymentPercent = 90, dueDays = 3, maxDiscountPercent = 10 });
        using var scoped = await ClientWithRoleAsync(api, a, "gestor@gym.test", false, ["plans.read", "commercial.settings.manage"], branch);
        await TestApi.CreateUserAsync(a.Owner, "recepcion@gym.test", "Recepcion", branch);
        using var reception = TestApi.Authenticated(api, await TestApi.LoginAsync(api, a.Slug, "recepcion@gym.test", TestApi.UserPassword));

        Assert.Equal("AllowUntilOverdue", (await b.Owner.GetFromJsonAsync<SettingsDto>("/api/v1/commercial-settings"))!.AccessPolicy);

        var body = new { accessPolicy = "AllowUntilOverdue", minPaymentPercent = 50, dueDays = 7, maxDiscountPercent = 30 };
        var scopedUpdate = await scoped.PutAsJsonAsync("/api/v1/commercial-settings", body);
        Assert.Equal(HttpStatusCode.Forbidden, scopedUpdate.StatusCode);
        Assert.Equal("auth.all_branches_required", await TestApi.ProblemCodeAsync(scopedUpdate));
        Assert.Equal(HttpStatusCode.Forbidden, (await reception.PutAsJsonAsync("/api/v1/commercial-settings", body)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await reception.GetAsync("/api/v1/commercial-settings")).StatusCode);
        Assert.Equal("RequireFullPayment", (await a.Owner.GetFromJsonAsync<SettingsDto>("/api/v1/commercial-settings"))!.AccessPolicy);
    }

    [Fact]
    public async Task La_tarifa_general_inicial_de_IVA_es_15_y_los_gimnasios_la_consultan_pero_no_la_administran()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        using var platform = await TestApi.PlatformClientAsync(api);

        var current = (await tenant.Owner.GetFromJsonAsync<TaxRateDto[]>("/api/v1/tax-rates/current"))!;
        Assert.Equal(15m, current.Single(r => r.Kind == "General").Percent);
        Assert.Equal(0m, current.Single(r => r.Kind == "Zero").Percent);
        Assert.Equal(0m, current.Single(r => r.Kind == "Exempt").Percent);
        Assert.Equal(0m, current.Single(r => r.Kind == "NotSubject").Percent);
        Assert.All(current, r => Assert.True(r.IsCurrent));

        var all = await platform.GetFromJsonAsync<TaxRateDto[]>("/api/v1/platform/tax-rates");
        Assert.Contains(all!, r => r.Kind == "General" && r.Percent == 15m && r.ValidUntil is null);

        // Un gimnasio no puede ver ni cambiar la administración global de tarifas.
        Assert.Equal(HttpStatusCode.Forbidden, (await tenant.Owner.GetAsync("/api/v1/platform/tax-rates")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tenant.Owner.PostAsJsonAsync("/api/v1/platform/tax-rates",
            new { percent = 5, validFrom = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)) })).StatusCode);
    }

    [Fact]
    public async Task Un_decreto_se_registra_hacia_adelante_cierra_la_tarifa_anterior_y_no_se_superpone()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        using var platform = await TestApi.PlatformClientAsync(api);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var effective = today.AddDays(3650);

        // Rige hacia adelante: no se puede reescribir lo vigente.
        var past = await platform.PostAsJsonAsync("/api/v1/platform/tax-rates", new { percent = 12, validFrom = today.AddDays(-30) });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, past.StatusCode);
        Assert.Equal("taxes.rate_in_past", await TestApi.ProblemCodeAsync(past));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await platform.PostAsJsonAsync("/api/v1/platform/tax-rates", new { percent = 101, validFrom = effective })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await platform.PostAsJsonAsync("/api/v1/platform/tax-rates", new { percent = -1, validFrom = effective })).StatusCode);

        var created = await platform.PostAsJsonAsync("/api/v1/platform/tax-rates", new { percent = 13, validFrom = effective });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var rate = (await created.Content.ReadFromJsonAsync<TaxRateDto>())!;
        Assert.Equal(13m, rate.Percent);
        Assert.False(rate.IsCurrent);

        // La anterior se cierra el día previo y sigue vigente hasta entonces; hoy todavía rige el 15 %.
        var all = await platform.GetFromJsonAsync<TaxRateDto[]>("/api/v1/platform/tax-rates");
        var general = all!.Where(r => r.Kind == "General").OrderBy(r => r.ValidFrom).ToArray();
        Assert.Equal(effective.AddDays(-1), general[^2].ValidUntil);
        Assert.Null(general[^1].ValidUntil);
        Assert.Equal(15m, (await tenant.Owner.GetFromJsonAsync<TaxRateDto[]>("/api/v1/tax-rates/current"))!.Single(r => r.Kind == "General").Percent);

        // Otro decreto no puede empezar antes ni el mismo día que el último.
        foreach (var date in new[] { effective, effective.AddDays(-5), today.AddDays(30) })
        {
            var overlap = await platform.PostAsJsonAsync("/api/v1/platform/tax-rates", new { percent = 14, validFrom = date });
            Assert.Equal(HttpStatusCode.Conflict, overlap.StatusCode);
            Assert.Equal("taxes.rate_overlap", await TestApi.ProblemCodeAsync(overlap));
        }

        // El equipo de plataforma no tiene audit.read, así que se verifica el registro directamente en la base.
        await using var connection = new Npgsql.NpgsqlConnection(api.AdminConnection);
        await connection.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand("SELECT count(*) FROM audit.audit_logs WHERE action = 'TaxRate.Created'", connection);
        Assert.True((long)(await command.ExecuteScalarAsync())! >= 1);
    }
}
