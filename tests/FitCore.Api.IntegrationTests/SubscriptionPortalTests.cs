using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FitCore.Api.IntegrationTests.Infrastructure;
using Npgsql;

namespace FitCore.Api.IntegrationTests;

/// <summary>Portal «Mi suscripción»: lo que el gimnasio ve y pide, la resolución por la plataforma, los avisos comerciales y el aislamiento entre gimnasios.</summary>
[Collection(ApiCollection3.Name)]
public class SubscriptionPortalTests(ApiFixture api)
{
    private static readonly string[] AllFeatures =
        ["members", "branches", "cash_payments", "attendance", "training", "nutrition", "classes", "advanced_reports", "exports", "advanced_permissions", "extra_storage"];

    private async Task<(Guid Id, string Code)> PlanAsync(decimal price, string period = "Monthly", int trialDays = 10)
    {
        using var platform = await TestApi.PlatformClientAsync(api);
        var code = $"po-{Guid.NewGuid():N}"[..20];
        var body = await Json(await platform.PostAsJsonAsync("/api/v1/platform/plans", new
        {
            code, name = $"Plan {code}", isActive = true, currency = "USD", billingPeriod = period, price, trialDays, features = AllFeatures,
            limits = new object[]
            {
                new { code = "branches", value = 3L }, new { code = "internal_users", value = (long?)null },
                new { code = "members", value = 100L }, new { code = "storage_mb", value = (long?)null }
            }
        }), HttpStatusCode.Created);
        return (body.GetProperty("id").GetGuid(), code);
    }

    private async Task<TestTenant> TenantAsync(string planCode, bool trial = false)
    {
        var slug = $"gym-{Guid.NewGuid():N}"[..20];
        using var platform = await TestApi.PlatformClientAsync(api);
        var created = await Json(await platform.PostAsJsonAsync("/api/v1/platform/tenants", new
        {
            slug, name = $"Gimnasio Portal {slug}", timeZone = "America/Guayaquil", locale = "es-EC", ownerEmail = $"owner@{slug}.test",
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

    private static async Task<(HttpStatusCode Status, string? Code, JsonElement? Body)> ReadAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        var json = string.IsNullOrEmpty(text) ? (JsonElement?)null : JsonDocument.Parse(text).RootElement.Clone();
        var code = !response.IsSuccessStatusCode && json is { } j && j.TryGetProperty("code", out var c) ? c.GetString() : null;
        return (response.StatusCode, code, json);
    }

    private async Task<(HttpStatusCode Status, string? Code, JsonElement? Body)> PlatformAsync(HttpMethod method, string url, object? body = null)
    {
        using var platform = await TestApi.PlatformClientAsync(api);
        return await ReadAsync(await platform.SendAsync(new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) }));
    }

    private static async Task<JsonElement> MineAsync(TestTenant tenant) => await Json(await tenant.Owner.GetAsync("/api/v1/my/subscription"));

    private static async Task<(HttpStatusCode Status, string? Code, JsonElement? Body)> AskAsync(TestTenant tenant, string kind, Guid? planId = null, string? message = null) =>
        await ReadAsync(await tenant.Owner.PostAsJsonAsync("/api/v1/my/subscription/requests", new { kind, planId, message }));

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(api.AdminConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private async Task<NpgsqlConnection> AsTenantAsync(Guid tenantId)
    {
        var connection = new NpgsqlConnection(api.AppConnection);
        await connection.OpenAsync();
        await using var set = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, false)", connection);
        set.Parameters.AddWithValue("t", tenantId.ToString());
        await set.ExecuteScalarAsync();
        return connection;
    }

    private static IEnumerable<string> AlertCodes(JsonElement mine) => mine.GetProperty("alerts").EnumerateArray().Select(a => a.GetProperty("code").GetString()!);

    // ---- Lo que el gimnasio ve -------------------------------------------------------------------------------------

    [Fact]
    public async Task El_propietario_ve_su_plan_su_periodo_y_su_consumo_y_la_plataforma_no_tiene_portal()
    {
        var (_, code) = await PlanAsync(30m);
        var tenant = await TenantAsync(code);

        var mine = await MineAsync(tenant);
        Assert.Equal(code, mine.GetProperty("planCode").GetString());
        Assert.Equal("Active", mine.GetProperty("status").GetString());
        Assert.Equal("Full", mine.GetProperty("accessMode").GetString());
        Assert.Equal(30m, mine.GetProperty("price").GetDecimal());
        Assert.False(mine.GetProperty("isFree").GetBoolean());
        Assert.Equal(0, mine.GetProperty("pendingRequests").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, mine.GetProperty("openInvoice").ValueKind);

        // La plataforma administra gimnasios, no tiene una suscripción propia: no hay portal para ella.
        using var platform = await TestApi.PlatformClientAsync(api);
        Assert.Equal(HttpStatusCode.Forbidden, (await platform.GetAsync("/api/v1/my/subscription")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await platform.GetAsync("/api/v1/my/subscription/notices")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().GetAsync("/api/v1/my/subscription")).StatusCode);
    }

    [Fact]
    public async Task El_catalogo_de_planes_solo_ofrece_los_vendibles_y_dice_si_es_mejora_o_baja()
    {
        var (_, cheap) = await PlanAsync(10m);
        var (_, dear) = await PlanAsync(50m);
        var tenant = await TenantAsync(cheap);

        var plans = await Json(await tenant.Owner.GetAsync("/api/v1/my/subscription/plans"));
        var byCode = plans.EnumerateArray().ToDictionary(p => p.GetProperty("plan").GetProperty("code").GetString()!);
        Assert.False(byCode.ContainsKey("legacy"));
        Assert.True(byCode[cheap].GetProperty("isCurrent").GetBoolean());
        Assert.Equal("Upgrade", byCode[dear].GetProperty("changeKind").GetString());
    }

    // ---- Solicitudes -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Pedir_un_cambio_de_plan_queda_pendiente_la_plataforma_lo_aprueba_y_el_plan_cambia()
    {
        var (_, cheap) = await PlanAsync(10m);
        var (dearId, dear) = await PlanAsync(50m);
        var tenant = await TenantAsync(cheap);

        var created = await AskAsync(tenant, "ChangePlan", dearId, "Necesitamos más sucursales");
        Assert.Equal(HttpStatusCode.Created, created.Status);
        var id = created.Body!.Value.GetProperty("id").GetGuid();
        Assert.Equal("Pending", created.Body.Value.GetProperty("status").GetString());

        // El contrato NO cambia por pedirlo.
        Assert.Equal(cheap, (await MineAsync(tenant)).GetProperty("planCode").GetString());
        Assert.Equal(1, (await MineAsync(tenant)).GetProperty("pendingRequests").GetArrayLength());

        // Una segunda igual espera a la primera; pedir el plan actual tampoco tiene sentido.
        Assert.Equal("subscription_requests.already_pending", (await AskAsync(tenant, "ChangePlan", dearId)).Code);

        var pending = (await PlatformAsync(HttpMethod.Get, $"/api/v1/platform/subscription-requests?status=Pending&tenantId={tenant.Id}")).Body!.Value;
        Assert.Equal([id], pending.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));

        var approved = await PlatformAsync(HttpMethod.Post, $"/api/v1/platform/subscription-requests/{id}/approve", new { note = "Aprobado" });
        Assert.Equal(HttpStatusCode.OK, approved.Status);
        Assert.Equal("Approved", approved.Body!.Value.GetProperty("status").GetString());

        // Una mejora entra de inmediato, sin prorrateo.
        var mine = await MineAsync(tenant);
        Assert.Equal(dear, mine.GetProperty("planCode").GetString());
        Assert.Equal(0, mine.GetProperty("pendingRequests").GetArrayLength());
        var history = await Json(await tenant.Owner.GetAsync("/api/v1/my/subscription/requests"));
        Assert.Equal("Approved", history.GetProperty("items")[0].GetProperty("status").GetString());

        // Aprobarla otra vez no hace nada más.
        Assert.Equal("subscription_requests.already_resolved", (await PlatformAsync(HttpMethod.Post, $"/api/v1/platform/subscription-requests/{id}/approve", new { note = (string?)null })).Code);
    }

    [Fact]
    public async Task Una_baja_pedida_se_programa_al_fin_del_periodo_y_un_plan_igual_o_heredado_se_rechaza()
    {
        var (cheapId, cheap) = await PlanAsync(10m);
        var (dearId, dear) = await PlanAsync(50m);
        var tenant = await TenantAsync(dear);

        Assert.Equal("subscription_requests.same_plan", (await AskAsync(tenant, "ChangePlan", dearId)).Code);
        var legacy = await ScalarAsync<Guid>("SELECT id FROM subscriptions.plans WHERE code = 'legacy'");
        Assert.Equal("subscription_requests.plan_not_available", (await AskAsync(tenant, "ChangePlan", legacy)).Code);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await AskAsync(tenant, "ChangePlan", null)).Status);

        var created = await AskAsync(tenant, "ChangePlan", cheapId);
        var id = created.Body!.Value.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await PlatformAsync(HttpMethod.Post, $"/api/v1/platform/subscription-requests/{id}/approve", new { note = (string?)null })).Status);

        var mine = await MineAsync(tenant);
        Assert.Equal(dear, mine.GetProperty("planCode").GetString()); // sigue en su plan hasta renovar
        Assert.Equal(cheap, mine.GetProperty("pendingPlanName").GetString()?.Replace("Plan ", ""));
    }

    [Fact]
    public async Task Rechazar_exige_una_razon_que_el_gimnasio_puede_leer()
    {
        var (dearId, _) = await PlanAsync(50m);
        var (_, cheap) = await PlanAsync(10m);
        var tenant = await TenantAsync(cheap);

        var id = (await AskAsync(tenant, "ChangePlan", dearId)).Body!.Value.GetProperty("id").GetGuid();
        Assert.Equal("subscription_requests.note_required", (await PlatformAsync(HttpMethod.Post, $"/api/v1/platform/subscription-requests/{id}/reject", new { note = "  " })).Code);

        var rejected = await PlatformAsync(HttpMethod.Post, $"/api/v1/platform/subscription-requests/{id}/reject", new { note = "Primero regularice la factura pendiente" });
        Assert.Equal("Rejected", rejected.Body!.Value.GetProperty("status").GetString());

        var history = await Json(await tenant.Owner.GetAsync("/api/v1/my/subscription/requests"));
        var item = history.GetProperty("items")[0];
        Assert.Equal("Rejected", item.GetProperty("status").GetString());
        Assert.Equal("Primero regularice la factura pendiente", item.GetProperty("resolutionNote").GetString());

        // Resuelta, se puede volver a pedir.
        Assert.Equal(HttpStatusCode.Created, (await AskAsync(tenant, "ChangePlan", dearId)).Status);
    }

    [Fact]
    public async Task La_cancelacion_pide_motivo_y_al_aprobarla_termina_al_final_del_periodo_sin_cerrar_el_gimnasio()
    {
        var (_, code) = await PlanAsync(30m);
        var tenant = await TenantAsync(code);

        Assert.Equal("subscription_requests.message_required", (await AskAsync(tenant, "Cancellation")).Code);
        var id = (await AskAsync(tenant, "Cancellation", message: "Cerramos el local")).Body!.Value.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await PlatformAsync(HttpMethod.Post, $"/api/v1/platform/subscription-requests/{id}/approve", new { note = (string?)null })).Status);

        var mine = await MineAsync(tenant);
        Assert.True(mine.GetProperty("cancelAtPeriodEnd").GetBoolean());
        Assert.Equal("Active", mine.GetProperty("status").GetString());
        Assert.Equal("Full", mine.GetProperty("accessMode").GetString());
        Assert.Contains("cancellation_scheduled", AlertCodes(mine));

        // Ya cancelándose, no tiene sentido pedirlo de nuevo; sí pedir que se retire.
        Assert.Equal("subscription_requests.invalid_state", (await AskAsync(tenant, "Cancellation", message: "otra vez")).Code);
        var reactivation = (await AskAsync(tenant, "Reactivation", message: "Nos arrepentimos")).Body!.Value.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await PlatformAsync(HttpMethod.Post, $"/api/v1/platform/subscription-requests/{reactivation}/approve", new { note = (string?)null })).Status);
        Assert.False((await MineAsync(tenant)).GetProperty("cancelAtPeriodEnd").GetBoolean());
    }

    [Fact]
    public async Task Un_gimnasio_suspendido_puede_pedir_la_reactivacion_y_ver_su_situacion()
    {
        var (_, code) = await PlanAsync(30m);
        var tenant = await TenantAsync(code);

        // Con la suscripción en regla no hay nada que reactivar.
        Assert.Equal("subscription_requests.invalid_state", (await AskAsync(tenant, "Reactivation")).Code);

        Assert.Equal(HttpStatusCode.OK, (await PlatformAsync(HttpMethod.Post, $"/api/v1/platform/tenants/{tenant.Id}/subscription/suspend", new { reason = "Falta de pago confirmada" })).Status);
        var suspended = await MineAsync(tenant);
        Assert.Equal("Suspended", suspended.GetProperty("status").GetString());
        Assert.Equal("Restricted", suspended.GetProperty("accessMode").GetString());
        Assert.Contains("suspended", AlertCodes(suspended));
        Assert.Contains("restricted", AlertCodes(suspended));

        // El motivo interno de la suspensión no se expone al gimnasio.
        Assert.DoesNotContain("Falta de pago confirmada", suspended.GetRawText(), StringComparison.Ordinal);

        // Restringido pero con sesión: puede pedir.
        var asked = await AskAsync(tenant, "Reactivation", message: "Ya pagamos por transferencia");
        Assert.Equal(HttpStatusCode.Created, asked.Status);
        var id = asked.Body!.Value.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await PlatformAsync(HttpMethod.Post, $"/api/v1/platform/subscription-requests/{id}/approve", new { note = (string?)null })).Status);
        Assert.Equal("Active", (await MineAsync(tenant)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Aprobar_una_reactivacion_que_no_aplica_deja_la_solicitud_pendiente_y_el_gimnasio_puede_retirarla()
    {
        var (_, code) = await PlanAsync(30m);
        var tenant = await TenantAsync(code);

        // Se pide estando suspendida y la plataforma la reactiva por su cuenta: la solicitud ya no tiene qué ejecutar.
        await PlatformAsync(HttpMethod.Post, $"/api/v1/platform/tenants/{tenant.Id}/subscription/suspend", new { reason = "x" });
        var id = (await AskAsync(tenant, "Reactivation")).Body!.Value.GetProperty("id").GetGuid();
        await PlatformAsync(HttpMethod.Post, $"/api/v1/platform/tenants/{tenant.Id}/subscription/reactivate", new { note = (string?)null });

        var approve = await PlatformAsync(HttpMethod.Post, $"/api/v1/platform/subscription-requests/{id}/approve", new { note = (string?)null });
        Assert.Equal(HttpStatusCode.Conflict, approve.Status);
        Assert.Equal("subscription_requests.reactivation_not_applicable", approve.Code);
        Assert.Equal("Pending", await ScalarAsync<string>($"SELECT status FROM subscriptions.subscription_requests WHERE id = '{id}'"));

        var withdrawn = await Json(await tenant.Owner.PostAsync($"/api/v1/my/subscription/requests/{id}/withdraw", null));
        Assert.Equal("Withdrawn", withdrawn.GetProperty("status").GetString());
        Assert.Equal("subscription_requests.already_resolved", (await ReadAsync(await tenant.Owner.PostAsync($"/api/v1/my/subscription/requests/{id}/withdraw", null))).Code);
    }

    [Fact]
    public async Task Un_gimnasio_no_ve_ni_toca_las_solicitudes_avisos_y_facturas_de_otro()
    {
        var (dearId, _) = await PlanAsync(50m);
        var (_, cheap) = await PlanAsync(10m);
        var a = await TenantAsync(cheap);
        var b = await TenantAsync(cheap);

        var id = (await AskAsync(a, "ChangePlan", dearId)).Body!.Value.GetProperty("id").GetGuid();
        await PlatformAsync(HttpMethod.Post, $"/api/v1/platform/tenants/{a.Id}/subscription/invoices");
        await PlatformAsync(HttpMethod.Post, "/api/v1/platform/subscriptions/run-lifecycle");

        Assert.Equal(0, (await Json(await b.Owner.GetAsync("/api/v1/my/subscription/requests"))).GetProperty("totalCount").GetInt32());
        Assert.Equal(0, (await Json(await b.Owner.GetAsync("/api/v1/my/subscription/invoices"))).GetProperty("totalCount").GetInt32());
        Assert.Equal(0, (await Json(await b.Owner.GetAsync("/api/v1/my/subscription/notices"))).GetProperty("totalCount").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await b.Owner.PostAsync($"/api/v1/my/subscription/requests/{id}/withdraw", null)).StatusCode);

        // Ni el propietario ni la API pueden resolver solicitudes: es de la plataforma.
        Assert.Equal(HttpStatusCode.Forbidden, (await a.Owner.PostAsJsonAsync($"/api/v1/platform/subscription-requests/{id}/approve", new { note = (string?)null })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await a.Owner.GetAsync("/api/v1/platform/subscription-requests")).StatusCode);

        // La base lo respalda: desde el contexto de otro gimnasio no existe la fila, y el propio no puede aprobarla.
        await using (var other = await AsTenantAsync(b.Id))
        {
            await using var count = new NpgsqlCommand("SELECT count(*) FROM subscriptions.subscription_requests", other);
            Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
        }

        await using var own = await AsTenantAsync(a.Id);
        await using var approve = new NpgsqlCommand($"UPDATE subscriptions.subscription_requests SET status = 'Approved', resolved_at = now() WHERE id = '{id}'", own);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => approve.ExecuteNonQueryAsync());
        Assert.Equal("FC110", ex.SqlState);
    }

    [Fact]
    public async Task Los_mensajes_no_admiten_datos_de_tarjeta_ni_exceden_el_maximo()
    {
        var (dearId, _) = await PlanAsync(50m);
        var (_, cheap) = await PlanAsync(10m);
        var tenant = await TenantAsync(cheap);

        Assert.Equal("subscription_requests.message_sensitive", (await AskAsync(tenant, "ChangePlan", dearId, "Mi tarjeta es 4111 1111 1111 1111")).Code);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await AskAsync(tenant, "ChangePlan", dearId, new string('x', 501))).Status);
        Assert.Equal(0, (await MineAsync(tenant)).GetProperty("pendingRequests").GetArrayLength());
    }

    // ---- Facturas ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task El_gimnasio_ve_sus_facturas_y_la_abierta_en_el_resumen_sin_detalle_interno()
    {
        var (_, code) = await PlanAsync(30m);
        var tenant = await TenantAsync(code);

        var issued = await PlatformAsync(HttpMethod.Post, $"/api/v1/platform/tenants/{tenant.Id}/subscription/invoices");
        Assert.Equal(HttpStatusCode.Created, issued.Status);
        var number = issued.Body!.Value.GetProperty("number").GetString();

        var invoices = await Json(await tenant.Owner.GetAsync("/api/v1/my/subscription/invoices"));
        Assert.Equal(1, invoices.GetProperty("totalCount").GetInt32());
        Assert.Equal(number, invoices.GetProperty("items")[0].GetProperty("number").GetString());

        var open = (await MineAsync(tenant)).GetProperty("openInvoice");
        Assert.Equal(number, open.GetProperty("number").GetString());
        Assert.Equal(30m, open.GetProperty("amount").GetDecimal());

        // El gimnasio no ve el detalle (intentos, referencias, notas) ni puede pagar o anular: eso lo registra la plataforma.
        var id = open.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await tenant.Owner.GetAsync($"/api/v1/platform/billing/invoices/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tenant.Owner.PostAsync($"/api/v1/platform/billing/invoices/{id}/void", null)).StatusCode);
    }

    // ---- Avisos ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Los_avisos_nacen_de_los_hechos_una_sola_vez_y_se_marcan_leidos()
    {
        var (_, code) = await PlanAsync(30m);
        var tenant = await TenantAsync(code);
        var other = await TenantAsync(code);

        await PlatformAsync(HttpMethod.Post, $"/api/v1/platform/tenants/{tenant.Id}/subscription/invoices");
        await PlatformAsync(HttpMethod.Post, $"/api/v1/platform/tenants/{tenant.Id}/subscription/suspend", new { reason = "Motivo interno reservado" });

        var first = (await PlatformAsync(HttpMethod.Post, "/api/v1/platform/subscriptions/run-lifecycle")).Body!.Value;
        Assert.True(first.GetProperty("noticesCreated").GetInt32() >= 2);

        // Repetir el proceso no duplica nada.
        await PlatformAsync(HttpMethod.Post, "/api/v1/platform/subscriptions/run-lifecycle");
        var notices = await Json(await tenant.Owner.GetAsync("/api/v1/my/subscription/notices"));
        var types = notices.GetProperty("items").EnumerateArray().Select(n => n.GetProperty("type").GetString()).ToList();
        Assert.Equal(types.Count, types.Distinct().Count());
        Assert.Contains("suspended", types);
        Assert.Contains("invoice_issued", types);
        Assert.Equal(types.Count, await ScalarAsync<long>($"SELECT count(*) FROM subscriptions.subscription_notices WHERE tenant_id = '{tenant.Id}'"));

        // Sin motivos internos ni notas: solo datos neutros.
        Assert.DoesNotContain("Motivo interno reservado", notices.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(types.Count, (await MineAsync(tenant)).GetProperty("unreadNotices").GetInt32());

        var one = notices.GetProperty("items")[0].GetProperty("id").GetGuid();
        Assert.Equal(1, await (await tenant.Owner.PostAsync($"/api/v1/my/subscription/notices/{one}/read", null)).Content.ReadFromJsonAsync<int>());
        Assert.Equal(types.Count - 1, (await MineAsync(tenant)).GetProperty("unreadNotices").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await tenant.Owner.PostAsync($"/api/v1/my/subscription/notices/{Guid.NewGuid()}/read", null)).StatusCode);

        await tenant.Owner.PostAsync("/api/v1/my/subscription/notices/read", null);
        Assert.Equal(0, (await MineAsync(tenant)).GetProperty("unreadNotices").GetInt32());
        Assert.Equal(0, (await Json(await tenant.Owner.GetAsync("/api/v1/my/subscription/notices?unreadOnly=true"))).GetProperty("totalCount").GetInt32());

        // El otro gimnasio no recibió nada de esto, y marcar como leído no toca los avisos ajenos.
        Assert.Equal(0, (await Json(await other.Owner.GetAsync("/api/v1/my/subscription/notices"))).GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task Una_prueba_por_vencer_avisa_en_el_banner_y_genera_un_aviso_de_calendario_una_sola_vez()
    {
        var (_, code) = await PlanAsync(30m, trialDays: 2);
        var tenant = await TenantAsync(code, trial: true);

        var mine = await MineAsync(tenant);
        Assert.Equal("Trialing", mine.GetProperty("status").GetString());
        Assert.Contains("trial_ending", AlertCodes(mine));

        await PlatformAsync(HttpMethod.Post, "/api/v1/platform/subscriptions/run-lifecycle");
        await PlatformAsync(HttpMethod.Post, "/api/v1/platform/subscriptions/run-lifecycle");
        Assert.Equal(1L, await ScalarAsync<long>($"SELECT count(*) FROM subscriptions.subscription_notices WHERE tenant_id = '{tenant.Id}' AND type = 'trial_ending'"));
    }

    [Fact]
    public async Task Los_avisos_son_inmutables_el_gimnasio_no_los_crea_ni_los_edita_ni_los_borra()
    {
        var (_, code) = await PlanAsync(30m);
        var tenant = await TenantAsync(code);
        await PlatformAsync(HttpMethod.Post, $"/api/v1/platform/tenants/{tenant.Id}/subscription/suspend", new { reason = "x" });
        await PlatformAsync(HttpMethod.Post, "/api/v1/platform/subscriptions/run-lifecycle");
        var id = await ScalarAsync<Guid>($"SELECT id FROM subscriptions.subscription_notices WHERE tenant_id = '{tenant.Id}' LIMIT 1");

        await using var connection = await AsTenantAsync(tenant.Id);
        await using (var insert = new NpgsqlCommand(
            $"INSERT INTO subscriptions.subscription_notices (id, tenant_id, type, severity, dedupe_key, created_at) VALUES (gen_random_uuid(), '{tenant.Id}', 'falso', 'Info', 'k', now())", connection))
        {
            await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
        }

        await using (var edit = new NpgsqlCommand($"UPDATE subscriptions.subscription_notices SET type = 'otro' WHERE id = '{id}'", connection))
        {
            Assert.Equal("FC110", (await Assert.ThrowsAsync<PostgresException>(() => edit.ExecuteNonQueryAsync())).SqlState);
        }

        await using (var delete = new NpgsqlCommand($"DELETE FROM subscriptions.subscription_notices WHERE id = '{id}'", connection))
        {
            // La aplicación ni siquiera tiene permiso de borrar (42501).
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync())).SqlState);
        }

        // Marcar leído sí está permitido, y una sola vez.
        await using var read = new NpgsqlCommand($"UPDATE subscriptions.subscription_notices SET read_at = now() WHERE id = '{id}'", connection);
        Assert.Equal(1, await read.ExecuteNonQueryAsync());
    }
}
