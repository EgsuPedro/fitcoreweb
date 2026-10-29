using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FitCore.Api.IntegrationTests.Infrastructure;
using Npgsql;

namespace FitCore.Api.IntegrationTests;

/// <summary>Suscripciones comerciales de los gimnasios: alta, estados, historial, aislamiento y garantías de la base de datos.</summary>
[Collection(ApiCollection1.Name)]
public class SubscriptionTests(ApiFixture api)
{
    private static readonly object[] AllLimits =
    [
        new { code = "branches", value = 1 }, new { code = "internal_users", value = 5 },
        new { code = "members", value = 100 }, new { code = "storage_mb", value = (long?)null }
    ];

    private async Task<HttpClient> PlatformAsync() => await TestApi.PlatformClientAsync(api);

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"Respondió {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<Guid> CreatePlanAsync(HttpClient platform, decimal price = 30m, int trialDays = 14, string period = "Monthly", int? graceDays = null, bool active = true)
    {
        var created = await ReadAsync(await platform.PostAsJsonAsync("/api/v1/platform/plans", new
        {
            code = $"sub-{Guid.NewGuid():N}"[..20], name = "Plan de suscripción", isActive = active, currency = "USD", billingPeriod = period, price, trialDays, graceDays,
            features = new[] { "members", "classes" }, limits = AllLimits
        }), HttpStatusCode.Created);
        return created.GetProperty("id").GetGuid();
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(api.AdminConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private async Task ExecAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(api.AdminConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Ejecuta SQL con el rol de la aplicación y el contexto de gimnasio indicado (como lo haría la API).</summary>
    private async Task<(int Rows, string? Error)> AsAppAsync(Guid? tenantId, string sql)
    {
        await using var connection = new NpgsqlConnection(api.AppConnection);
        await connection.OpenAsync();
        await using (var set = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, false)", connection))
        {
            set.Parameters.AddWithValue("t", tenantId?.ToString() ?? string.Empty);
            await set.ExecuteScalarAsync();
        }

        await using var command = new NpgsqlCommand(sql, connection);
        try
        {
            return (await command.ExecuteNonQueryAsync(), null);
        }
        catch (PostgresException ex)
        {
            return (-1, ex.MessageText);
        }
    }

    private async Task<long> CountAsAppAsync(Guid? tenantId, string table)
    {
        await using var connection = new NpgsqlConnection(api.AppConnection);
        await connection.OpenAsync();
        await using (var set = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, false)", connection))
        {
            set.Parameters.AddWithValue("t", tenantId?.ToString() ?? string.Empty);
            await set.ExecuteScalarAsync();
        }

        await using var command = new NpgsqlCommand($"SELECT count(*) FROM {table}", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<Guid> PlatformTenantIdAsync() => await ScalarAsync<Guid>("SELECT id FROM organizations.tenants WHERE slug = 'platform'");

    private static async Task<TestTenant> NewTenantWithPlanAsync(ApiFixture api, string? planCode = null, bool trial = false)
    {
        var slug = $"gym-{Guid.NewGuid():N}"[..20];
        using var platform = await TestApi.PlatformClientAsync(api);
        var response = await platform.PostAsJsonAsync("/api/v1/platform/tenants", new
        {
            slug, name = $"Gimnasio {slug}", timeZone = "America/Guayaquil", locale = "es-EC", ownerEmail = $"owner@{slug}.test",
            ownerFullName = "Dueño", ownerPassword = TestApi.OwnerPassword, planCode, startTrial = trial
        });
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"Respondió {(int)response.StatusCode}: {text}");
        var tenant = JsonDocument.Parse(text).RootElement;
        var owner = TestApi.Authenticated(api, await TestApi.LoginAsync(api, slug, $"owner@{slug}.test", TestApi.OwnerPassword));
        return new TestTenant(tenant.GetProperty("id").GetGuid(), slug, $"owner@{slug}.test", TestApi.OwnerPassword, owner);
    }

    private static async Task<(Guid PlanId, string Code)> PlanWithCodeAsync(HttpClient platform, decimal price = 30m, int trialDays = 14, int? graceDays = null)
    {
        var id = await CreatePlanAsync(platform, price, trialDays, graceDays: graceDays);
        var plan = await ReadAsync(await platform.GetAsync($"/api/v1/platform/plans/{id}"));
        return (id, plan.GetProperty("code").GetString()!);
    }

    private static string Today => DateTime.UtcNow.AddHours(-5).ToString("yyyy-MM-dd");

    [Fact]
    public async Task Un_gimnasio_nuevo_sin_plan_recibe_una_suscripcion_activa_en_el_plan_heredado()
    {
        var tenant = await NewTenantWithPlanAsync(api);
        using var platform = await PlatformAsync();

        var subscription = await ReadAsync(await platform.GetAsync($"/api/v1/platform/tenants/{tenant.Id}/subscription"));

        Assert.Equal("Active", subscription.GetProperty("status").GetString());
        Assert.Equal("legacy", subscription.GetProperty("planCode").GetString());
        Assert.Equal(0m, subscription.GetProperty("price").GetDecimal());
        Assert.Equal(tenant.Slug, subscription.GetProperty("tenantSlug").GetString());
        Assert.Equal(Today, subscription.GetProperty("currentPeriodStart").GetString());
        Assert.Equal("Created", subscription.GetProperty("recentEvents")[0].GetProperty("type").GetString());

        // Lo que quedó en PostgreSQL.
        Assert.Equal(1L, await ScalarAsync<long>($"SELECT count(*) FROM subscriptions.subscriptions WHERE tenant_id = '{tenant.Id}' AND status = 'Active' AND plan_code = 'legacy'"));
        Assert.Equal(1L, await ScalarAsync<long>($"SELECT count(*) FROM subscriptions.subscription_events WHERE tenant_id = '{tenant.Id}' AND type = 'Created'"));
        Assert.Equal(11L, await ScalarAsync<long>("SELECT count(*) FROM subscriptions.plan_features f JOIN subscriptions.plan_versions v ON v.id = f.plan_version_id JOIN subscriptions.plans p ON p.id = v.plan_id WHERE p.code = 'legacy'"));
        Assert.Equal(4L, await ScalarAsync<long>("SELECT count(*) FROM subscriptions.plan_limits l JOIN subscriptions.plan_versions v ON v.id = l.plan_version_id JOIN subscriptions.plans p ON p.id = v.plan_id WHERE p.code = 'legacy' AND l.value IS NULL"));
        Assert.False(await ScalarAsync<bool>("SELECT is_active FROM subscriptions.plans WHERE code = 'legacy'"));
    }

    [Fact]
    public async Task El_alta_de_un_gimnasio_con_plan_y_prueba_crea_la_suscripcion_en_prueba()
    {
        using var platform = await PlatformAsync();
        var (_, code) = await PlanWithCodeAsync(platform, price: 59m, trialDays: 14);

        var tenant = await NewTenantWithPlanAsync(api, code, trial: true);

        var subscription = await ReadAsync(await platform.GetAsync($"/api/v1/platform/tenants/{tenant.Id}/subscription"));
        Assert.Equal("Trialing", subscription.GetProperty("status").GetString());
        Assert.Equal(code, subscription.GetProperty("planCode").GetString());
        Assert.Equal(59m, subscription.GetProperty("price").GetDecimal());
        var expectedEnd = DateTime.UtcNow.AddHours(-5).AddDays(14).ToString("yyyy-MM-dd");
        Assert.Equal(expectedEnd, subscription.GetProperty("trialEndsOn").GetString());
        Assert.Equal(expectedEnd, subscription.GetProperty("nextDueOn").GetString());
        var types = subscription.GetProperty("recentEvents").EnumerateArray().Select(e => e.GetProperty("type").GetString()!).ToArray();
        Assert.Contains("TrialStarted", types);
    }

    [Fact]
    public async Task Un_plan_inexistente_inactivo_o_sin_prueba_rechaza_el_alta_y_no_deja_un_gimnasio_a_medias()
    {
        using var platform = await PlatformAsync();
        var inactive = await ReadAsync(await platform.GetAsync($"/api/v1/platform/plans/{await CreatePlanAsync(platform, active: false)}"));
        var noTrial = await ReadAsync(await platform.GetAsync($"/api/v1/platform/plans/{await CreatePlanAsync(platform, trialDays: 0)}"));

        foreach (var (planCode, trial, expectedCode) in new (string, bool, string)[]
        {
            ("no-existe-plan", false, "subscriptions.plan_not_found"),
            (inactive.GetProperty("code").GetString()!, false, "subscriptions.plan_inactive"),
            (noTrial.GetProperty("code").GetString()!, true, "subscriptions.plan_without_trial")
        })
        {
            var slug = $"gym-{Guid.NewGuid():N}"[..20];
            var response = await platform.PostAsJsonAsync("/api/v1/platform/tenants", new
            {
                slug, name = "X", timeZone = "America/Guayaquil", locale = "es-EC", ownerEmail = $"o@{slug}.test", ownerFullName = "O",
                ownerPassword = TestApi.OwnerPassword, planCode, startTrial = trial
            });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal(expectedCode, await TestApi.ProblemCodeAsync(response));
            Assert.Equal(0L, await ScalarAsync<long>($"SELECT count(*) FROM organizations.tenants WHERE slug = '{slug}'"));
        }
    }

    [Fact]
    public async Task Asignar_un_plan_a_un_gimnasio_con_suscripcion_vigente_se_rechaza_y_tras_cancelar_se_permite_una_nueva()
    {
        var tenant = await NewTenantWithPlanAsync(api);
        using var platform = await PlatformAsync();
        var planId = await CreatePlanAsync(platform, price: 80m, trialDays: 0);

        var duplicate = await platform.PostAsJsonAsync($"/api/v1/platform/tenants/{tenant.Id}/subscription", new { planId, startTrial = false, note = "x" });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("subscriptions.already_exists", await TestApi.ProblemCodeAsync(duplicate));

        // Una cancelada es historial: se puede contratar de nuevo y las dos quedan guardadas.
        await ExecAsync($"UPDATE subscriptions.subscriptions SET status = 'Cancelled', cancelled_at = now(), cancel_reason = 'Prueba' WHERE tenant_id = '{tenant.Id}'");
        var again = await ReadAsync(await platform.PostAsJsonAsync($"/api/v1/platform/tenants/{tenant.Id}/subscription", new { planId, startTrial = false, note = "Nueva contratación" }), HttpStatusCode.Created);

        Assert.Equal("Active", again.GetProperty("status").GetString());
        Assert.Equal(80m, again.GetProperty("price").GetDecimal());
        Assert.Equal(2L, await ScalarAsync<long>($"SELECT count(*) FROM subscriptions.subscriptions WHERE tenant_id = '{tenant.Id}'"));
        Assert.Equal(1L, await ScalarAsync<long>($"SELECT count(*) FROM subscriptions.subscriptions WHERE tenant_id = '{tenant.Id}' AND status <> 'Cancelled'"));
    }

    [Fact]
    public async Task Las_asignaciones_invalidas_se_rechazan_con_un_codigo_claro()
    {
        var tenant = await NewTenantWithPlanAsync(api);
        using var platform = await PlatformAsync();
        await ExecAsync($"UPDATE subscriptions.subscriptions SET status = 'Cancelled', cancelled_at = now(), cancel_reason = 'x' WHERE tenant_id = '{tenant.Id}'");
        var noTrialPlan = await CreatePlanAsync(platform, trialDays: 0);
        var inactivePlan = await CreatePlanAsync(platform, active: false);

        async Task<string?> Code(Guid tenantId, object body, HttpStatusCode expected)
        {
            var response = await platform.PostAsJsonAsync($"/api/v1/platform/tenants/{tenantId}/subscription", body);
            Assert.Equal(expected, response.StatusCode);
            return await TestApi.ProblemCodeAsync(response);
        }

        Assert.Equal("subscriptions.plan_not_found", await Code(tenant.Id, new { planId = Guid.NewGuid(), startTrial = false }, HttpStatusCode.UnprocessableEntity));
        Assert.Equal("subscriptions.plan_inactive", await Code(tenant.Id, new { planId = inactivePlan, startTrial = false }, HttpStatusCode.UnprocessableEntity));
        Assert.Equal("subscriptions.plan_without_trial", await Code(tenant.Id, new { planId = noTrialPlan, startTrial = true }, HttpStatusCode.UnprocessableEntity));
        Assert.Equal("subscriptions.tenant_not_found", await Code(Guid.NewGuid(), new { planId = noTrialPlan, startTrial = false }, HttpStatusCode.NotFound));
        Assert.Equal("subscriptions.platform_protected", await Code(await PlatformTenantIdAsync(), new { planId = noTrialPlan, startTrial = false }, HttpStatusCode.Conflict));
        Assert.Equal(0L, await ScalarAsync<long>($"SELECT count(*) FROM subscriptions.subscriptions WHERE tenant_id = '{tenant.Id}' AND status <> 'Cancelled'"));
    }

    [Fact]
    public async Task Activar_una_prueba_inicia_el_primer_periodo_de_pago_y_solo_se_puede_una_vez()
    {
        using var platform = await PlatformAsync();
        var (_, code) = await PlanWithCodeAsync(platform, price: 45m, trialDays: 7);
        var tenant = await NewTenantWithPlanAsync(api, code, trial: true);

        var activated = await ReadAsync(await platform.PostAsJsonAsync($"/api/v1/platform/tenants/{tenant.Id}/subscription/activate", new { note = "Pagó por transferencia" }));

        Assert.Equal("Active", activated.GetProperty("status").GetString());
        Assert.Equal(Today, activated.GetProperty("currentPeriodStart").GetString());
        Assert.Equal(DateTime.UtcNow.AddHours(-5).AddMonths(1).ToString("yyyy-MM-dd"), activated.GetProperty("currentPeriodEnd").GetString());
        Assert.Equal(JsonValueKind.Null, activated.GetProperty("trialEndsOn").ValueKind);
        Assert.Equal(1L, await ScalarAsync<long>($"SELECT count(*) FROM subscriptions.subscription_events WHERE tenant_id = '{tenant.Id}' AND type = 'Activated' AND detail = 'Pagó por transferencia'"));

        var again = await platform.PostAsJsonAsync($"/api/v1/platform/tenants/{tenant.Id}/subscription/activate", new { note = "otra vez" });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("subscriptions.invalid_transition", await TestApi.ProblemCodeAsync(again));
        Assert.Equal(1L, await ScalarAsync<long>($"SELECT count(*) FROM subscriptions.subscription_events WHERE tenant_id = '{tenant.Id}' AND type = 'Activated'"));
    }

    [Fact]
    public async Task Suspender_y_reactivar_comercialmente_no_cierra_el_acceso_del_gimnasio()
    {
        var tenant = await NewTenantWithPlanAsync(api);
        using var platform = await PlatformAsync();

        var noReason = await platform.PostAsJsonAsync($"/api/v1/platform/tenants/{tenant.Id}/subscription/suspend", new { reason = " " });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noReason.StatusCode);

        var suspended = await ReadAsync(await platform.PostAsJsonAsync($"/api/v1/platform/tenants/{tenant.Id}/subscription/suspend", new { reason = "Pago rechazado por el banco" }));
        Assert.Equal("Suspended", suspended.GetProperty("status").GetString());
        Assert.Equal("Pago rechazado por el banco", suspended.GetProperty("suspensionReason").GetString());
        Assert.Equal("Suspended", await ScalarAsync<string>($"SELECT status FROM subscriptions.subscriptions WHERE tenant_id = '{tenant.Id}'"));
        Assert.Equal("Active", await ScalarAsync<string>($"SELECT status_before_suspension FROM subscriptions.subscriptions WHERE tenant_id = '{tenant.Id}'"));

        // La suspensión comercial NO es la administrativa: el gimnasio sigue activo y su propietario sigue dentro y operando.
        Assert.Equal("Active", await ScalarAsync<string>($"SELECT status FROM organizations.tenants WHERE id = '{tenant.Id}'"));
        Assert.Equal(HttpStatusCode.OK, (await tenant.Owner.GetAsync("/api/v1/organization")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await tenant.Owner.PostAsJsonAsync("/api/v1/branches", new { code = "UIO", name = "Quito", timeZone = "America/Guayaquil", sriEstablishmentCode = "001", address = (string?)null })).StatusCode);
        var login = await api.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { tenantSlug = tenant.Slug, email = tenant.OwnerEmail, password = tenant.OwnerPassword, useCookie = false });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var twice = await platform.PostAsJsonAsync($"/api/v1/platform/tenants/{tenant.Id}/subscription/suspend", new { reason = "otra vez" });
        Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);

        var reactivated = await ReadAsync(await platform.PostAsJsonAsync($"/api/v1/platform/tenants/{tenant.Id}/subscription/reactivate", new { note = "Regularizó" }));
        Assert.Equal("Active", reactivated.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, reactivated.GetProperty("suspensionReason").ValueKind);
        Assert.Equal(0L, await ScalarAsync<long>($"SELECT count(*) FROM subscriptions.subscriptions WHERE tenant_id = '{tenant.Id}' AND (suspended_at IS NOT NULL OR suspension_reason IS NOT NULL)"));

        var events = (await ReadAsync(await platform.GetAsync($"/api/v1/platform/tenants/{tenant.Id}/subscription/history"))).GetProperty("items")
            .EnumerateArray().Select(e => e.GetProperty("type").GetString()!).ToArray();
        Assert.Equal(["Reactivated", "Suspended", "Created"], events);
    }

    [Fact]
    public async Task Reactivar_con_el_periodo_vencido_deja_la_suscripcion_en_mora_con_gracia()
    {
        using var platform = await PlatformAsync();
        var (_, code) = await PlanWithCodeAsync(platform, price: 30m, trialDays: 0, graceDays: 10);
        var tenant = await NewTenantWithPlanAsync(api, code);
        await platform.PostAsJsonAsync($"/api/v1/platform/tenants/{tenant.Id}/subscription/suspend", new { reason = "Revisión" });
        var expiredEnd = DateTime.UtcNow.AddHours(-5).AddDays(-20).ToString("yyyy-MM-dd");
        await ExecAsync($"UPDATE subscriptions.subscriptions SET current_period_start = '{expiredEnd}'::date - 30, current_period_end = '{expiredEnd}', next_due_on = '{expiredEnd}' WHERE tenant_id = '{tenant.Id}'");

        var reactivated = await ReadAsync(await platform.PostAsJsonAsync($"/api/v1/platform/tenants/{tenant.Id}/subscription/reactivate", new { note = (string?)null }));

        Assert.Equal("PastDue", reactivated.GetProperty("status").GetString());
        // La gracia es la del plan (10 días), no la de plataforma (7).
        Assert.Equal(DateTime.UtcNow.AddHours(-5).AddDays(10).ToString("yyyy-MM-dd"), reactivated.GetProperty("graceEndsOn").GetString());
    }

    [Fact]
    public async Task Un_cambio_de_precio_del_plan_no_altera_la_suscripcion_ya_contratada()
    {
        using var platform = await PlatformAsync();
        var planId = await CreatePlanAsync(platform, price: 30m, trialDays: 0);
        var plan = await ReadAsync(await platform.GetAsync($"/api/v1/platform/plans/{planId}"));
        var tenant = await NewTenantWithPlanAsync(api, plan.GetProperty("code").GetString());

        await ReadAsync(await platform.PutAsJsonAsync($"/api/v1/platform/plans/{planId}", new
        {
            name = "Plan de suscripción", currency = "USD", billingPeriod = "Monthly", price = 99m, trialDays = 0, features = new[] { "members", "classes" }, limits = AllLimits
        }));

        var subscription = await ReadAsync(await platform.GetAsync($"/api/v1/platform/tenants/{tenant.Id}/subscription"));
        Assert.Equal(30m, subscription.GetProperty("price").GetDecimal());
        Assert.Equal(1, subscription.GetProperty("planVersion").GetInt32());
        Assert.Equal(30m, await ScalarAsync<decimal>($"SELECT price FROM subscriptions.subscriptions WHERE tenant_id = '{tenant.Id}'"));
        Assert.Equal(99m, await ScalarAsync<decimal>($"SELECT price FROM subscriptions.plan_versions WHERE plan_id = '{planId}' AND number = 2"));
    }

    [Fact]
    public async Task El_listado_filtra_por_estado_plan_busqueda_y_proximos_vencimientos()
    {
        using var platform = await PlatformAsync();
        var (_, code) = await PlanWithCodeAsync(platform, price: 25m, trialDays: 5);
        var trialing = await NewTenantWithPlanAsync(api, code, trial: true);
        var active = await NewTenantWithPlanAsync(api, code);

        var byPlan = await ReadAsync(await platform.GetAsync($"/api/v1/platform/subscriptions?planCode={code}&pageSize=100"));
        var ids = byPlan.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("tenantId").GetGuid()).ToArray();
        Assert.Contains(trialing.Id, ids);
        Assert.Contains(active.Id, ids);

        var byStatus = await ReadAsync(await platform.GetAsync($"/api/v1/platform/subscriptions?planCode={code}&status=Trialing"));
        Assert.Equal([trialing.Id], byStatus.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("tenantId").GetGuid()).ToArray());

        var bySearch = await ReadAsync(await platform.GetAsync($"/api/v1/platform/subscriptions?search={active.Slug}"));
        Assert.Equal([active.Id], bySearch.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("tenantId").GetGuid()).ToArray());

        // La prueba vence en 5 días; la suscripción activa, en un mes.
        var soon = await ReadAsync(await platform.GetAsync($"/api/v1/platform/subscriptions?planCode={code}&dueWithinDays=7"));
        Assert.Equal([trialing.Id], soon.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("tenantId").GetGuid()).ToArray());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await platform.GetAsync("/api/v1/platform/subscriptions?status=Inventado")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await platform.GetAsync("/api/v1/platform/subscriptions?dueWithinDays=999")).StatusCode);
    }

    [Fact]
    public async Task Solo_el_equipo_de_plataforma_administra_suscripciones_y_un_gimnasio_no_toca_las_ajenas()
    {
        var a = await NewTenantWithPlanAsync(api);
        var b = await NewTenantWithPlanAsync(api);
        using var platform = await PlatformAsync();
        var planId = await CreatePlanAsync(platform);

        foreach (var path in new[] { $"/api/v1/platform/tenants/{b.Id}/subscription", $"/api/v1/platform/tenants/{b.Id}/subscription/history", "/api/v1/platform/subscriptions" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await a.Owner.GetAsync(path)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await a.Owner.PostAsJsonAsync($"/api/v1/platform/tenants/{a.Id}/subscription", new { planId, startTrial = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await a.Owner.PostAsJsonAsync($"/api/v1/platform/tenants/{a.Id}/subscription/suspend", new { reason = "yo mismo" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await a.Owner.PostAsJsonAsync($"/api/v1/platform/tenants/{a.Id}/subscription/activate", new { note = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await a.Owner.PostAsJsonAsync($"/api/v1/platform/tenants/{a.Id}/subscription/reactivate", new { note = "x" })).StatusCode);
        using var anonymous = api.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/v1/platform/tenants/{a.Id}/subscription")).StatusCode);

        Assert.Equal("Active", await ScalarAsync<string>($"SELECT status FROM subscriptions.subscriptions WHERE tenant_id = '{a.Id}'"));
    }

    [Fact]
    public async Task La_base_aisla_las_suscripciones_por_gimnasio_y_solo_deja_escribir_a_la_plataforma()
    {
        var a = await NewTenantWithPlanAsync(api);
        var b = await NewTenantWithPlanAsync(api);
        var platformId = await PlatformTenantIdAsync();

        // Un gimnasio ve solo la suya; la plataforma ve todas; sin contexto no se ve ninguna.
        Assert.Equal(1L, await CountAsAppAsync(a.Id, "subscriptions.subscriptions"));
        Assert.Equal(1L, await CountAsAppAsync(a.Id, "subscriptions.subscription_events"));
        Assert.True(await CountAsAppAsync(platformId, "subscriptions.subscriptions") >= 2);
        Assert.Equal(0L, await CountAsAppAsync(null, "subscriptions.subscriptions"));

        // Un gimnasio no puede modificar ni crear suscripciones, ni siquiera la propia (lo haría una inyección SQL en la aplicación).
        var update = await AsAppAsync(a.Id, $"UPDATE subscriptions.subscriptions SET price = 0 WHERE tenant_id = '{a.Id}'");
        Assert.Equal(0, update.Rows);
        var steal = await AsAppAsync(a.Id, $"UPDATE subscriptions.subscriptions SET status = 'Suspended' WHERE tenant_id = '{b.Id}'");
        Assert.Equal(0, steal.Rows);
        var insert = await AsAppAsync(a.Id, $"""
            INSERT INTO subscriptions.subscription_events (id, subscription_id, tenant_id, type, occurred_at)
            SELECT gen_random_uuid(), id, tenant_id, 'Created', now() FROM subscriptions.subscriptions WHERE tenant_id = '{a.Id}'
            """);
        Assert.Contains("row-level security", insert.Error ?? "NO ERROR");
        Assert.Equal("Active", await ScalarAsync<string>($"SELECT status FROM subscriptions.subscriptions WHERE tenant_id = '{b.Id}'"));

        // El rol de la aplicación tampoco puede borrar nada.
        var delete = await AsAppAsync(platformId, $"DELETE FROM subscriptions.subscriptions WHERE tenant_id = '{a.Id}'");
        Assert.Contains("permission denied", delete.Error ?? "NO ERROR");
    }

    [Fact]
    public async Task La_base_rechaza_transiciones_invalidas_borrados_y_reescrituras_aunque_se_intente_por_sql()
    {
        var tenant = await NewTenantWithPlanAsync(api);
        var admin = api.AdminConnection;

        async Task<string?> Err(string sql)
        {
            await using var connection = new NpgsqlConnection(admin);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            try
            {
                await command.ExecuteNonQueryAsync();
                return null;
            }
            catch (PostgresException ex)
            {
                return ex.MessageText;
            }
        }

        Assert.Contains("Transición de suscripción no permitida", await Err($"UPDATE subscriptions.subscriptions SET status = 'Trialing' WHERE tenant_id = '{tenant.Id}'") ?? "NO ERROR");
        Assert.Contains("no se pueden modificar", await Err($"UPDATE subscriptions.subscriptions SET tenant_id = '{Guid.NewGuid()}' WHERE tenant_id = '{tenant.Id}'") ?? "NO ERROR");
        Assert.Contains("histórico", await Err($"DELETE FROM subscriptions.subscriptions WHERE tenant_id = '{tenant.Id}'") ?? "NO ERROR");
        Assert.Contains("histórico", await Err($"UPDATE subscriptions.subscription_events SET detail = 'otra cosa' WHERE tenant_id = '{tenant.Id}'") ?? "NO ERROR");
        Assert.Contains("histórico", await Err($"DELETE FROM subscriptions.subscription_events WHERE tenant_id = '{tenant.Id}'") ?? "NO ERROR");
        Assert.Contains("ck_subscriptions_suspended", await Err($"UPDATE subscriptions.subscriptions SET status = 'Suspended' WHERE tenant_id = '{tenant.Id}'") ?? "NO ERROR");
        Assert.Contains("ck_subscriptions_dates", await Err($"UPDATE subscriptions.subscriptions SET current_period_end = current_period_start WHERE tenant_id = '{tenant.Id}'") ?? "NO ERROR");
        Assert.Contains("ux_subscriptions_current", await Err($"""
            INSERT INTO subscriptions.subscriptions (id, tenant_id, plan_id, plan_version_id, plan_version_number, plan_code, plan_name, price, currency, period, status, started_at, current_period_start, current_period_end, next_due_on, cancel_at_period_end, created_at)
            SELECT gen_random_uuid(), tenant_id, plan_id, plan_version_id, plan_version_number, plan_code, plan_name, price, currency, period, status, started_at, current_period_start, current_period_end, next_due_on, false, now()
            FROM subscriptions.subscriptions WHERE tenant_id = '{tenant.Id}'
            """) ?? "NO ERROR");
        Assert.Equal("Active", await ScalarAsync<string>($"SELECT status FROM subscriptions.subscriptions WHERE tenant_id = '{tenant.Id}'"));
    }

    [Fact]
    public async Task Cambios_simultaneos_nunca_corrompen_la_suscripcion()
    {
        using var platform = await PlatformAsync();
        var planId = await CreatePlanAsync(platform, trialDays: 0);

        // Dos altas simultáneas para el mismo gimnasio sin suscripción vigente: solo una se crea.
        var tenant = await NewTenantWithPlanAsync(api);
        await ExecAsync($"UPDATE subscriptions.subscriptions SET status = 'Cancelled', cancelled_at = now(), cancel_reason = 'x' WHERE tenant_id = '{tenant.Id}'");
        var creates = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            using var client = await PlatformAsync();
            return await client.PostAsJsonAsync($"/api/v1/platform/tenants/{tenant.Id}/subscription", new { planId, startTrial = false });
        }));
        Assert.Equal(1, creates.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.All(creates.Where(r => r.StatusCode != HttpStatusCode.Created), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        Assert.Equal(1L, await ScalarAsync<long>($"SELECT count(*) FROM subscriptions.subscriptions WHERE tenant_id = '{tenant.Id}' AND status <> 'Cancelled'"));

        // Seis suspensiones simultáneas: una sola tiene éxito y deja un único hecho en el historial.
        var suspends = await Task.WhenAll(Enumerable.Range(0, 6).Select(async i =>
        {
            using var client = await PlatformAsync();
            return await client.PostAsJsonAsync($"/api/v1/platform/tenants/{tenant.Id}/subscription/suspend", new { reason = $"Motivo {i}" });
        }));
        Assert.All(suspends, r => Assert.True(r.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict, $"Respondió {(int)r.StatusCode}"));
        Assert.Equal(1, suspends.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1L, await ScalarAsync<long>($"SELECT count(*) FROM subscriptions.subscription_events WHERE tenant_id = '{tenant.Id}' AND type = 'Suspended'"));
        Assert.Equal("Suspended", await ScalarAsync<string>($"SELECT status FROM subscriptions.subscriptions WHERE tenant_id = '{tenant.Id}' AND status <> 'Cancelled'"));
    }

    [Fact]
    public async Task Los_cambios_de_suscripcion_quedan_auditados_y_las_suscripciones_no_tocan_las_membresias_de_alumnos()
    {
        var tenant = await NewTenantWithPlanAsync(api);
        using var platform = await PlatformAsync();
        var members = await ScalarAsync<long>("SELECT count(*) FROM members.memberships");
        var payments = await ScalarAsync<long>("SELECT count(*) FROM billing.payments");

        await platform.PostAsJsonAsync($"/api/v1/platform/tenants/{tenant.Id}/subscription/suspend", new { reason = "Auditoría" });
        await platform.PostAsJsonAsync($"/api/v1/platform/tenants/{tenant.Id}/subscription/reactivate", new { note = (string?)null });

        var subscriptionId = await ScalarAsync<Guid>($"SELECT id FROM subscriptions.subscriptions WHERE tenant_id = '{tenant.Id}'");
        var actions = await ScalarAsync<string[]>($"SELECT array_agg(action ORDER BY id) FROM audit.audit_logs WHERE entity = 'Subscription' AND entity_id = '{subscriptionId}'");
        Assert.Contains("Subscription.Created", actions);
        Assert.Equal(2, actions.Count(a => a == "Subscription.Updated"));
        Assert.Equal(members, await ScalarAsync<long>("SELECT count(*) FROM members.memberships"));
        Assert.Equal(payments, await ScalarAsync<long>("SELECT count(*) FROM billing.payments"));
    }
}
