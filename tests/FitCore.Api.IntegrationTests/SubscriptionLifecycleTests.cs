using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FitCore.Api.IntegrationTests.Infrastructure;
using Npgsql;

namespace FitCore.Api.IntegrationTests;

/// <summary>Renovaciones, cambios de plan, cancelaciones, prórrogas y proceso automático de vencimientos de las suscripciones comerciales.</summary>
[Collection(ApiCollection3.Name)]
public class SubscriptionLifecycleTests(ApiFixture api)
{
    private static readonly string[] AllFeatures =
        ["members", "branches", "cash_payments", "attendance", "training", "nutrition", "classes", "advanced_reports", "exports", "advanced_permissions", "extra_storage"];

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-5));

    private static string D(int daysFromToday) => Today.AddDays(daysFromToday).ToString("yyyy-MM-dd");

    private async Task<(Guid Id, string Code)> PlanAsync(decimal price, int? graceDays = null, int? members = null, string period = "Monthly", string[]? features = null, bool active = true)
    {
        using var platform = await TestApi.PlatformClientAsync(api);
        var code = $"lc-{Guid.NewGuid():N}"[..20];
        var response = await platform.PostAsJsonAsync("/api/v1/platform/plans", new
        {
            code, name = $"Plan {code}", isActive = active, currency = "USD", billingPeriod = period, price, trialDays = 10, graceDays,
            features = features ?? AllFeatures,
            limits = new object[]
            {
                new { code = "branches", value = (long?)null }, new { code = "internal_users", value = (long?)null },
                new { code = "members", value = (long?)members }, new { code = "storage_mb", value = (long?)null }
            }
        });
        var body = await Json(response, HttpStatusCode.Created);
        return (body.GetProperty("id").GetGuid(), code);
    }

    private async Task<TestTenant> TenantAsync(string planCode, bool trial = false)
    {
        var slug = $"gym-{Guid.NewGuid():N}"[..20];
        using var platform = await TestApi.PlatformClientAsync(api);
        var created = await Json(await platform.PostAsJsonAsync("/api/v1/platform/tenants", new
        {
            slug, name = $"Gimnasio {slug}", timeZone = "America/Guayaquil", locale = "es-EC", ownerEmail = $"owner@{slug}.test",
            ownerFullName = "Dueño", ownerPassword = TestApi.OwnerPassword, planCode, startTrial = trial
        }), HttpStatusCode.Created);
        var owner = TestApi.Authenticated(api, await TestApi.LoginAsync(api, slug, $"owner@{slug}.test", TestApi.OwnerPassword));
        return new TestTenant(created.GetProperty("id").GetGuid(), slug, $"owner@{slug}.test", TestApi.OwnerPassword, owner);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"Respondió {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private async Task<(HttpStatusCode Status, string? Code)> PostAsync(Guid tenantId, string action, object body)
    {
        using var platform = await TestApi.PlatformClientAsync(api);
        var response = await platform.PostAsJsonAsync($"/api/v1/platform/tenants/{tenantId}/subscription/{action}", body);
        return (response.StatusCode, response.IsSuccessStatusCode ? null : await TestApi.ProblemCodeAsync(response));
    }

    private async Task<JsonElement> OkAsync(Guid tenantId, string action, object body)
    {
        using var platform = await TestApi.PlatformClientAsync(api);
        return await Json(await platform.PostAsJsonAsync($"/api/v1/platform/tenants/{tenantId}/subscription/{action}", body));
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

    private Task<string> StatusAsync(Guid tenantId) =>
        ScalarAsync<string>($"SELECT status FROM subscriptions.subscriptions WHERE tenant_id = '{tenantId}' AND status <> 'Cancelled'");

    private Task<string> FieldAsync(Guid tenantId, string column) =>
        ScalarAsync<string>($"SELECT coalesce({column}::text, 'null') FROM subscriptions.subscriptions WHERE tenant_id = '{tenantId}' ORDER BY created_at DESC LIMIT 1");

    private Task SetPeriodAsync(Guid tenantId, int endOffsetDays, int lengthDays = 30) =>
        ExecAsync($"UPDATE subscriptions.subscriptions SET current_period_start = '{D(endOffsetDays - lengthDays)}', current_period_end = '{D(endOffsetDays)}', next_due_on = '{D(endOffsetDays)}' WHERE tenant_id = '{tenantId}' AND status <> 'Cancelled'");

    private async Task<JsonElement> RunLifecycleAsync()
    {
        using var platform = await TestApi.PlatformClientAsync(api);
        return await Json(await platform.PostAsync("/api/v1/platform/subscriptions/run-lifecycle", null));
    }

    // ---- Renovación --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Renovar_dentro_de_la_ventana_empieza_donde_termino_el_periodo_y_queda_en_el_historial()
    {
        var (_, code) = await PlanAsync(40m);
        var tenant = await TenantAsync(code);
        await SetPeriodAsync(tenant.Id, endOffsetDays: 5);

        var renewed = await OkAsync(tenant.Id, "renew", new { note = "Pagó por transferencia" });

        Assert.Equal("Active", renewed.GetProperty("status").GetString());
        Assert.Equal(D(5), renewed.GetProperty("currentPeriodStart").GetString());
        Assert.Equal(Today.AddDays(5).AddMonths(1).ToString("yyyy-MM-dd"), renewed.GetProperty("currentPeriodEnd").GetString());
        Assert.Equal(renewed.GetProperty("currentPeriodEnd").GetString(), renewed.GetProperty("nextDueOn").GetString());
        Assert.Equal(1L, await ScalarAsync<long>($"SELECT count(*) FROM subscriptions.subscription_events WHERE tenant_id = '{tenant.Id}' AND type = 'Renewed' AND actor_id IS NOT NULL"));
        Assert.Equal("Full", await ScalarAsync<string>($"SELECT access_mode FROM subscriptions.tenant_entitlements WHERE tenant_id = '{tenant.Id}'"));
    }

    [Fact]
    public async Task Renovar_con_demasiada_anticipacion_o_con_una_cancelacion_programada_se_rechaza_sin_cambiar_nada()
    {
        var (_, code) = await PlanAsync(40m);
        var tenant = await TenantAsync(code);
        await SetPeriodAsync(tenant.Id, endOffsetDays: 90);

        Assert.Equal((HttpStatusCode.Conflict, "subscriptions.renewal_too_early"), await PostAsync(tenant.Id, "renew", new { note = (string?)null }));
        Assert.Equal(D(90), await FieldAsync(tenant.Id, "current_period_end"));

        await SetPeriodAsync(tenant.Id, endOffsetDays: 5);
        await OkAsync(tenant.Id, "cancel", new { reason = "Se va", atPeriodEnd = true });
        Assert.Equal((HttpStatusCode.Conflict, "subscriptions.cancellation_scheduled"), await PostAsync(tenant.Id, "renew", new { note = (string?)null }));
        Assert.Equal(0L, await ScalarAsync<long>($"SELECT count(*) FROM subscriptions.subscription_events WHERE tenant_id = '{tenant.Id}' AND type = 'Renewed'"));
    }

    [Fact]
    public async Task Renovar_una_suscripcion_vencida_empieza_hoy_y_devuelve_el_acceso()
    {
        var (_, code) = await PlanAsync(40m);
        var tenant = await TenantAsync(code);
        await ExecAsync($"UPDATE subscriptions.subscriptions SET status = 'Expired', current_period_start = '{D(-120)}', current_period_end = '{D(-90)}', next_due_on = '{D(-90)}' WHERE tenant_id = '{tenant.Id}'");
        Assert.Equal("Restricted", await ScalarAsync<string>($"SELECT access_mode FROM subscriptions.tenant_entitlements WHERE tenant_id = '{tenant.Id}'"));

        var renewed = await OkAsync(tenant.Id, "renew", new { note = "Regularizó" });

        Assert.Equal("Active", renewed.GetProperty("status").GetString());
        Assert.Equal(D(0), renewed.GetProperty("currentPeriodStart").GetString());
        Assert.Equal("Full", await ScalarAsync<string>($"SELECT access_mode FROM subscriptions.tenant_entitlements WHERE tenant_id = '{tenant.Id}'"));
        Assert.Equal(HttpStatusCode.Created, (await tenant.Owner.PostAsJsonAsync("/api/v1/branches", new { code = "UIO", name = "Quito", timeZone = "America/Guayaquil", sriEstablishmentCode = "001", address = (string?)null })).StatusCode);
    }

    // ---- Cambio de plan ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Una_mejora_de_plan_entra_de_inmediato_con_las_funciones_y_limites_del_plan_nuevo()
    {
        var (_, basic) = await PlanAsync(20m, members: 3, features: ["members", "attendance"]);
        var (proId, _) = await PlanAsync(60m, members: 500, features: AllFeatures);
        var tenant = await TenantAsync(basic);
        var before = await FieldAsync(tenant.Id, "current_period_end");

        var changed = await OkAsync(tenant.Id, "change-plan", new { planId = proId, note = "Crece" });

        Assert.Equal(60m, changed.GetProperty("price").GetDecimal());
        Assert.Equal(before, changed.GetProperty("currentPeriodEnd").GetString());
        Assert.Equal(JsonValueKind.Null, changed.GetProperty("pendingPlanId").ValueKind);
        Assert.Equal("Upgrade", await ScalarAsync<string>($"SELECT kind FROM subscriptions.subscription_plan_changes WHERE tenant_id = '{tenant.Id}'"));
        Assert.Equal("Applied", await ScalarAsync<string>($"SELECT status FROM subscriptions.subscription_plan_changes WHERE tenant_id = '{tenant.Id}'"));

        // Los derechos vigentes cambiaron en la misma operación: ya tiene clases y el tope de 500 alumnos.
        Assert.Equal(11, await ScalarAsync<int>($"SELECT cardinality(features) FROM subscriptions.tenant_entitlements WHERE tenant_id = '{tenant.Id}'"));
        Assert.Equal("500", await ScalarAsync<string>($"SELECT limits ->> 'members' FROM subscriptions.tenant_entitlements WHERE tenant_id = '{tenant.Id}'"));
        Assert.Equal(HttpStatusCode.OK, (await tenant.Owner.GetAsync("/api/v1/group-classes")).StatusCode);
    }

    [Fact]
    public async Task Una_baja_de_plan_se_programa_y_entra_en_vigor_al_renovar()
    {
        var (_, pro) = await PlanAsync(60m, members: 500);
        var (basicId, _) = await PlanAsync(20m, members: 10, features: ["members", "attendance"]);
        var tenant = await TenantAsync(pro);

        var scheduled = await OkAsync(tenant.Id, "change-plan", new { planId = basicId, note = "Reduce costos" });

        Assert.Equal(60m, scheduled.GetProperty("price").GetDecimal()); // hasta el fin del período sigue con lo que pagó
        Assert.Equal(basicId, scheduled.GetProperty("pendingPlanId").GetGuid());
        Assert.Equal("Downgrade", await ScalarAsync<string>($"SELECT kind FROM subscriptions.subscription_plan_changes WHERE tenant_id = '{tenant.Id}'"));
        Assert.Equal("Scheduled", await ScalarAsync<string>($"SELECT status FROM subscriptions.subscription_plan_changes WHERE tenant_id = '{tenant.Id}'"));
        Assert.Equal("500", await ScalarAsync<string>($"SELECT limits ->> 'members' FROM subscriptions.tenant_entitlements WHERE tenant_id = '{tenant.Id}'"));
        Assert.Equal(HttpStatusCode.OK, (await tenant.Owner.GetAsync("/api/v1/group-classes")).StatusCode);

        await SetPeriodAsync(tenant.Id, endOffsetDays: 3);
        var renewed = await OkAsync(tenant.Id, "renew", new { note = "Pagó el plan nuevo" });

        Assert.Equal(20m, renewed.GetProperty("price").GetDecimal());
        Assert.Equal(JsonValueKind.Null, renewed.GetProperty("pendingPlanId").ValueKind);
        Assert.Equal("Applied", await ScalarAsync<string>($"SELECT status FROM subscriptions.subscription_plan_changes WHERE tenant_id = '{tenant.Id}'"));
        Assert.Equal("10", await ScalarAsync<string>($"SELECT limits ->> 'members' FROM subscriptions.tenant_entitlements WHERE tenant_id = '{tenant.Id}'"));
        Assert.Equal(2, await ScalarAsync<int>($"SELECT cardinality(features) FROM subscriptions.tenant_entitlements WHERE tenant_id = '{tenant.Id}'"));
        Assert.Equal(HttpStatusCode.Forbidden, (await tenant.Owner.GetAsync("/api/v1/group-classes")).StatusCode);
    }

    [Fact]
    public async Task Un_nuevo_cambio_programado_reemplaza_al_anterior_y_una_mejora_lo_cancela()
    {
        var (_, pro) = await PlanAsync(60m);
        var (midId, _) = await PlanAsync(30m);
        var (minId, _) = await PlanAsync(10m);
        var (topId, _) = await PlanAsync(99m);
        var tenant = await TenantAsync(pro);

        await OkAsync(tenant.Id, "change-plan", new { planId = midId });
        var second = await OkAsync(tenant.Id, "change-plan", new { planId = minId });
        Assert.Equal(minId, second.GetProperty("pendingPlanId").GetGuid());
        Assert.Equal(1L, await ScalarAsync<long>($"SELECT count(*) FROM subscriptions.subscription_plan_changes WHERE tenant_id = '{tenant.Id}' AND status = 'Superseded'"));

        var upgraded = await OkAsync(tenant.Id, "change-plan", new { planId = topId });
        Assert.Equal(99m, upgraded.GetProperty("price").GetDecimal());
        Assert.Equal(JsonValueKind.Null, upgraded.GetProperty("pendingPlanId").ValueKind);
        Assert.Equal(0L, await ScalarAsync<long>($"SELECT count(*) FROM subscriptions.subscription_plan_changes WHERE tenant_id = '{tenant.Id}' AND status = 'Scheduled'"));
    }

    [Fact]
    public async Task Los_cambios_de_plan_invalidos_se_rechazan()
    {
        var (sameId, same) = await PlanAsync(60m);
        var (inactiveId, _) = await PlanAsync(10m, active: false);
        var tenant = await TenantAsync(same);

        Assert.Equal((HttpStatusCode.Conflict, "subscriptions.same_plan"), await PostAsync(tenant.Id, "change-plan", new { planId = sameId }));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "subscriptions.plan_inactive"), await PostAsync(tenant.Id, "change-plan", new { planId = inactiveId }));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "subscriptions.plan_not_found"), await PostAsync(tenant.Id, "change-plan", new { planId = Guid.NewGuid() }));

        using var platform = await TestApi.PlatformClientAsync(api);
        await platform.PostAsJsonAsync($"/api/v1/platform/tenants/{tenant.Id}/subscription/suspend", new { reason = "x" });
        var (otherId, _) = await PlanAsync(90m);
        Assert.Equal((HttpStatusCode.Conflict, "subscriptions.invalid_state"), await PostAsync(tenant.Id, "change-plan", new { planId = otherId }));
        Assert.Equal(0L, await ScalarAsync<long>($"SELECT count(*) FROM subscriptions.subscription_plan_changes WHERE tenant_id = '{tenant.Id}'"));
    }

    // ---- Cancelación -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Cancelar_de_inmediato_deja_la_suscripcion_cancelada_el_acceso_restringido_y_los_datos_intactos()
    {
        var (_, code) = await PlanAsync(30m);
        var tenant = await TenantAsync(code);
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "validation.failed"), await PostAsync(tenant.Id, "cancel", new { reason = " ", atPeriodEnd = false }));

        var cancelled = await OkAsync(tenant.Id, "cancel", new { reason = "Cierra el negocio", atPeriodEnd = false });

        Assert.Equal("Cancelled", cancelled.GetProperty("status").GetString());
        Assert.Equal("Cierra el negocio", cancelled.GetProperty("cancelReason").GetString());
        Assert.Equal("Restricted", await ScalarAsync<string>($"SELECT access_mode FROM subscriptions.tenant_entitlements WHERE tenant_id = '{tenant.Id}'"));
        Assert.Equal(HttpStatusCode.OK, (await tenant.Owner.GetAsync("/api/v1/members")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await tenant.Owner.GetAsync("/api/v1/reports/exports")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tenant.Owner.PostAsJsonAsync("/api/v1/branches", new { code = "UIO", name = "Quito", timeZone = "America/Guayaquil", sriEstablishmentCode = "001", address = (string?)null })).StatusCode);
        Assert.Equal("Active", await ScalarAsync<string>($"SELECT status FROM organizations.tenants WHERE id = '{tenant.Id}'"));
        // Una cancelada ya no es «la suscripción vigente»: no hay nada más que cancelar.
        Assert.Equal((HttpStatusCode.NotFound, "subscriptions.not_found"), await PostAsync(tenant.Id, "cancel", new { reason = "otra vez", atPeriodEnd = false }));
    }

    [Fact]
    public async Task Cancelar_al_final_del_periodo_mantiene_el_acceso_hasta_esa_fecha_y_el_proceso_la_ejecuta()
    {
        var (_, code) = await PlanAsync(30m);
        var tenant = await TenantAsync(code);

        var scheduled = await OkAsync(tenant.Id, "cancel", new { reason = "Se cambia de proveedor", atPeriodEnd = true });
        Assert.Equal("Active", scheduled.GetProperty("status").GetString());
        Assert.True(scheduled.GetProperty("cancelAtPeriodEnd").GetBoolean());
        Assert.Equal("Se cambia de proveedor", scheduled.GetProperty("scheduledCancelReason").GetString());
        Assert.Equal((HttpStatusCode.Conflict, "subscriptions.cancellation_already_scheduled"), await PostAsync(tenant.Id, "cancel", new { reason = "otra", atPeriodEnd = true }));

        // Todavía no llegó la fecha: sigue activa y operando.
        await RunLifecycleAsync();
        Assert.Equal("Active", await StatusAsync(tenant.Id));
        Assert.Equal(HttpStatusCode.Created, (await tenant.Owner.PostAsJsonAsync("/api/v1/branches", new { code = "UIO", name = "Quito", timeZone = "America/Guayaquil", sriEstablishmentCode = "001", address = (string?)null })).StatusCode);

        // Llega el fin del período: el proceso la cancela con el motivo dado.
        await SetPeriodAsync(tenant.Id, endOffsetDays: 0);
        await RunLifecycleAsync();
        Assert.Equal("Cancelled", await ScalarAsync<string>($"SELECT status FROM subscriptions.subscriptions WHERE tenant_id = '{tenant.Id}'"));
        Assert.Equal("Se cambia de proveedor", await FieldAsync(tenant.Id, "cancel_reason"));
        Assert.Equal(1L, await ScalarAsync<long>($"SELECT count(*) FROM subscriptions.subscription_events WHERE tenant_id = '{tenant.Id}' AND type = 'Cancelled' AND actor_id IS NULL"));
    }

    [Fact]
    public async Task Retirar_la_cancelacion_programada_la_anula_y_permite_renovar()
    {
        var (_, code) = await PlanAsync(30m);
        var tenant = await TenantAsync(code);
        await OkAsync(tenant.Id, "cancel", new { reason = "Duda", atPeriodEnd = true });

        Assert.Equal((HttpStatusCode.Conflict, "subscriptions.no_cancellation_scheduled"), await PostAsync(await TenantAsync(code).ContinueWith(t => t.Result.Id), "resume-cancellation", new { note = (string?)null }));
        var resumed = await OkAsync(tenant.Id, "resume-cancellation", new { note = "Se quedó" });

        Assert.False(resumed.GetProperty("cancelAtPeriodEnd").GetBoolean());
        Assert.Equal(JsonValueKind.Null, resumed.GetProperty("scheduledCancelReason").ValueKind);
        await SetPeriodAsync(tenant.Id, endOffsetDays: 0);
        await RunLifecycleAsync();
        Assert.Equal("PastDue", await StatusAsync(tenant.Id)); // ya no se cancela: vencido sin pago pasa a mora
    }

    // ---- Prórroga ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task La_prorroga_alarga_el_periodo_o_la_gracia_con_motivo_y_queda_auditada()
    {
        var (_, code) = await PlanAsync(30m, graceDays: 5);
        var tenant = await TenantAsync(code);
        var end = Today.AddMonths(1);

        var extended = await OkAsync(tenant.Id, "extend", new { days = 10, reason = "Cortesía por demora del banco" });
        Assert.Equal(end.AddDays(10).ToString("yyyy-MM-dd"), extended.GetProperty("currentPeriodEnd").GetString());
        Assert.Equal(end.AddDays(10).ToString("yyyy-MM-dd"), extended.GetProperty("nextDueOn").GetString());
        Assert.Equal(1L, await ScalarAsync<long>($"SELECT count(*) FROM subscriptions.subscription_events WHERE tenant_id = '{tenant.Id}' AND type = 'Extended' AND actor_id IS NOT NULL AND detail LIKE '10 días · Cortesía%'"));

        // En mora, la prórroga alarga la gracia.
        await ExecAsync($"UPDATE subscriptions.subscriptions SET status = 'PastDue', grace_ends_on = '{D(2)}' WHERE tenant_id = '{tenant.Id}'");
        var late = await OkAsync(tenant.Id, "extend", new { days = 7, reason = "Espera la transferencia" });
        Assert.Equal(D(9), late.GetProperty("graceEndsOn").GetString());

        var subscriptionId = await ScalarAsync<Guid>($"SELECT id FROM subscriptions.subscriptions WHERE tenant_id = '{tenant.Id}'");
        Assert.True(await ScalarAsync<long>($"SELECT count(*) FROM audit.audit_logs WHERE entity = 'Subscription' AND entity_id = '{subscriptionId}' AND action = 'Subscription.Updated'") >= 2);
    }

    [Fact]
    public async Task La_prorroga_valida_dias_motivo_y_estado()
    {
        var (_, code) = await PlanAsync(30m);
        var tenant = await TenantAsync(code);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostAsync(tenant.Id, "extend", new { days = 0, reason = "x" })).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostAsync(tenant.Id, "extend", new { days = 91, reason = "x" })).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostAsync(tenant.Id, "extend", new { days = 5, reason = " " })).Status);

        await ExecAsync($"UPDATE subscriptions.subscriptions SET status = 'Expired' WHERE tenant_id = '{tenant.Id}'");
        Assert.Equal((HttpStatusCode.Conflict, "subscriptions.invalid_state"), await PostAsync(tenant.Id, "extend", new { days = 5, reason = "x" }));
    }

    // ---- Proceso de vencimientos -------------------------------------------------------------------------------------

    [Fact]
    public async Task Una_prueba_que_termina_sin_pago_vence_y_el_proceso_es_idempotente()
    {
        var (_, code) = await PlanAsync(30m);
        var tenant = await TenantAsync(code, trial: true);
        Assert.Equal("Trialing", await StatusAsync(tenant.Id));

        // Dentro de la prueba no pasa nada.
        await RunLifecycleAsync();
        Assert.Equal("Trialing", await StatusAsync(tenant.Id));

        await ExecAsync($"UPDATE subscriptions.subscriptions SET current_period_start = '{D(-11)}', current_period_end = '{D(0)}', trial_ends_on = '{D(0)}', next_due_on = '{D(0)}' WHERE tenant_id = '{tenant.Id}'");
        var first = await RunLifecycleAsync();
        Assert.True(first.GetProperty("trialsExpired").GetInt32() >= 1);
        Assert.Equal("Expired", await StatusAsync(tenant.Id));
        Assert.Equal("Restricted", await ScalarAsync<string>($"SELECT access_mode FROM subscriptions.tenant_entitlements WHERE tenant_id = '{tenant.Id}'"));

        await RunLifecycleAsync();
        Assert.Equal("Expired", await StatusAsync(tenant.Id));
        Assert.Equal(1L, await ScalarAsync<long>($"SELECT count(*) FROM subscriptions.subscription_events WHERE tenant_id = '{tenant.Id}' AND type = 'Expired'"));
    }

    [Fact]
    public async Task Un_plan_de_pago_vencido_pasa_a_mora_con_la_gracia_del_plan_y_luego_vence()
    {
        var (_, code) = await PlanAsync(30m, graceDays: 5);
        var tenant = await TenantAsync(code);
        await SetPeriodAsync(tenant.Id, endOffsetDays: 0);

        var run = await RunLifecycleAsync();
        Assert.True(run.GetProperty("markedPastDue").GetInt32() >= 1);
        Assert.Equal("PastDue", await StatusAsync(tenant.Id));
        Assert.Equal(D(5), await FieldAsync(tenant.Id, "grace_ends_on"));
        Assert.Equal("Grace", await ScalarAsync<string>($"SELECT access_mode FROM subscriptions.tenant_entitlements WHERE tenant_id = '{tenant.Id}'"));
        Assert.Equal(HttpStatusCode.Created, (await tenant.Owner.PostAsJsonAsync("/api/v1/branches", new { code = "UIO", name = "Quito", timeZone = "America/Guayaquil", sriEstablishmentCode = "001", address = (string?)null })).StatusCode);

        // Dentro de la gracia el proceso no la vence; cuando termina, sí.
        await RunLifecycleAsync();
        Assert.Equal("PastDue", await StatusAsync(tenant.Id));
        await ExecAsync($"UPDATE subscriptions.subscriptions SET grace_ends_on = '{D(0)}' WHERE tenant_id = '{tenant.Id}'");
        var expired = await RunLifecycleAsync();
        Assert.True(expired.GetProperty("graceExpired").GetInt32() >= 1);
        Assert.Equal("Expired", await StatusAsync(tenant.Id));
        Assert.Equal("Restricted", await ScalarAsync<string>($"SELECT access_mode FROM subscriptions.tenant_entitlements WHERE tenant_id = '{tenant.Id}'"));
        Assert.Equal(HttpStatusCode.Forbidden, (await tenant.Owner.PostAsJsonAsync("/api/v1/branches", new { code = "GYE", name = "Guayaquil", timeZone = "America/Guayaquil", sriEstablishmentCode = "002", address = (string?)null })).StatusCode);
    }

    [Fact]
    public async Task Un_plan_gratuito_se_renueva_solo_sin_pasar_por_mora_y_aplica_la_baja_programada()
    {
        var (_, free) = await PlanAsync(0m);
        var tenant = await TenantAsync(free);
        await SetPeriodAsync(tenant.Id, endOffsetDays: 0);

        var run = await RunLifecycleAsync();

        Assert.True(run.GetProperty("freeRenewed").GetInt32() >= 1);
        Assert.Equal("Active", await StatusAsync(tenant.Id));
        Assert.Equal(D(0), await FieldAsync(tenant.Id, "current_period_start"));
        Assert.Equal(Today.AddMonths(1).ToString("yyyy-MM-dd"), await FieldAsync(tenant.Id, "current_period_end"));
        Assert.Equal(1L, await ScalarAsync<long>($"SELECT count(*) FROM subscriptions.subscription_events WHERE tenant_id = '{tenant.Id}' AND type = 'Renewed' AND actor_id IS NULL"));

        // Repetir el proceso no vuelve a renovar.
        await RunLifecycleAsync();
        Assert.Equal(1L, await ScalarAsync<long>($"SELECT count(*) FROM subscriptions.subscription_events WHERE tenant_id = '{tenant.Id}' AND type = 'Renewed'"));
    }

    [Fact]
    public async Task El_proceso_no_toca_suspendidas_canceladas_ni_las_que_todavia_no_vencen()
    {
        var (_, code) = await PlanAsync(30m);
        var suspended = await TenantAsync(code);
        var future = await TenantAsync(code);
        using var platform = await TestApi.PlatformClientAsync(api);
        await platform.PostAsJsonAsync($"/api/v1/platform/tenants/{suspended.Id}/subscription/suspend", new { reason = "Revisión" });
        await SetPeriodAsync(suspended.Id, endOffsetDays: -10);
        await SetPeriodAsync(future.Id, endOffsetDays: 3);

        await RunLifecycleAsync();

        Assert.Equal("Suspended", await StatusAsync(suspended.Id));
        Assert.Equal("Active", await StatusAsync(future.Id));
    }

    [Fact]
    public async Task Varias_pasadas_simultaneas_del_proceso_hacen_cada_transicion_una_sola_vez()
    {
        var (_, code) = await PlanAsync(30m);
        var tenant = await TenantAsync(code);
        await SetPeriodAsync(tenant.Id, endOffsetDays: 0);

        var runs = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => RunLifecycleAsync()));

        Assert.All(runs, r => Assert.Equal(0, r.GetProperty("failed").GetInt32()));
        Assert.Equal("PastDue", await StatusAsync(tenant.Id));
        Assert.Equal(1L, await ScalarAsync<long>($"SELECT count(*) FROM subscriptions.subscription_events WHERE tenant_id = '{tenant.Id}' AND type = 'PastDue'"));
    }

    [Fact]
    public async Task Renovaciones_simultaneas_de_la_misma_suscripcion_solo_se_aplican_una_vez()
    {
        var (_, code) = await PlanAsync(30m);
        var tenant = await TenantAsync(code);
        await SetPeriodAsync(tenant.Id, endOffsetDays: 3);

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => PostAsync(tenant.Id, "renew", new { note = "doble clic" })));

        Assert.All(results, r => Assert.True(r.Status is HttpStatusCode.OK or HttpStatusCode.Conflict, $"Respondió {(int)r.Status}"));
        Assert.Equal(1, results.Count(r => r.Status == HttpStatusCode.OK));
        Assert.Equal(1L, await ScalarAsync<long>($"SELECT count(*) FROM subscriptions.subscription_events WHERE tenant_id = '{tenant.Id}' AND type = 'Renewed'"));
        Assert.Equal(Today.AddDays(3).AddMonths(1).ToString("yyyy-MM-dd"), await FieldAsync(tenant.Id, "current_period_end"));
    }

    [Fact]
    public async Task Solo_el_equipo_de_plataforma_renueva_cambia_cancela_prorroga_o_ejecuta_el_proceso()
    {
        var (planId, code) = await PlanAsync(30m);
        var tenant = await TenantAsync(code);
        var other = await TenantAsync(code);

        foreach (var (action, body) in new (string, object)[]
        {
            ("renew", new { note = "x" }), ("change-plan", new { planId }), ("cancel", new { reason = "x", atPeriodEnd = false }),
            ("resume-cancellation", new { note = "x" }), ("extend", new { days = 5, reason = "x" })
        })
        {
            var own = await tenant.Owner.PostAsJsonAsync($"/api/v1/platform/tenants/{tenant.Id}/subscription/{action}", body);
            var foreign = await tenant.Owner.PostAsJsonAsync($"/api/v1/platform/tenants/{other.Id}/subscription/{action}", body);
            Assert.Equal(HttpStatusCode.Forbidden, own.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await tenant.Owner.PostAsync("/api/v1/platform/subscriptions/run-lifecycle", null)).StatusCode);
        using var anonymous = api.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/v1/platform/subscriptions/run-lifecycle", null)).StatusCode);
        Assert.Equal("Active", await StatusAsync(tenant.Id));
        Assert.Equal("Active", await StatusAsync(other.Id));
        Assert.Equal(0L, await ScalarAsync<long>($"SELECT count(*) FROM subscriptions.subscription_events WHERE tenant_id IN ('{tenant.Id}', '{other.Id}') AND type <> 'Created'"));
    }

    [Fact]
    public async Task La_base_rechaza_cambios_de_plan_reescritos_o_borrados()
    {
        var (_, pro) = await PlanAsync(60m);
        var (basicId, _) = await PlanAsync(20m);
        var tenant = await TenantAsync(pro);
        await OkAsync(tenant.Id, "change-plan", new { planId = basicId });

        async Task<string?> Err(string sql)
        {
            await using var connection = new NpgsqlConnection(api.AdminConnection);
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

        Assert.Contains("no se puede modificar", await Err($"UPDATE subscriptions.subscription_plan_changes SET kind = 'Upgrade' WHERE tenant_id = '{tenant.Id}'") ?? "NO ERROR");
        Assert.Contains("histórico", await Err($"DELETE FROM subscriptions.subscription_plan_changes WHERE tenant_id = '{tenant.Id}'") ?? "NO ERROR");
        Assert.Contains("ck_subscriptions_scheduled_cancel", await Err($"UPDATE subscriptions.subscriptions SET cancel_at_period_end = true WHERE tenant_id = '{tenant.Id}'") ?? "NO ERROR");
        Assert.Contains("ck_subscriptions_pending_plan", await Err($"UPDATE subscriptions.subscriptions SET pending_plan_id = NULL WHERE tenant_id = '{tenant.Id}'") ?? "NO ERROR");
    }
}
