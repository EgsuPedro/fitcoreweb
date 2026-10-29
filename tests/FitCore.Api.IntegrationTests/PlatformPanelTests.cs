using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FitCore.Api.IntegrationTests.Infrastructure;
using Npgsql;

namespace FitCore.Api.IntegrationTests;

/// <summary>Panel global de FIT360: organizaciones, consumo agregado, notas, actividad administrativa y métricas, con mínimo privilegio.</summary>
[Collection(ApiCollection2.Name)]
public class PlatformPanelTests(ApiFixture api)
{
    private static readonly string[] AllFeatures =
        ["members", "branches", "cash_payments", "attendance", "training", "nutrition", "classes", "advanced_reports", "exports", "advanced_permissions", "extra_storage"];

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-5));

    private async Task<(Guid Id, string Code)> PlanAsync(decimal price, string period = "Monthly", int trialDays = 10, string[]? features = null)
    {
        using var platform = await TestApi.PlatformClientAsync(api);
        var code = $"pp-{Guid.NewGuid():N}"[..20];
        var body = await Json(await platform.PostAsJsonAsync("/api/v1/platform/plans", new
        {
            code, name = $"Plan {code}", isActive = true, currency = "USD", billingPeriod = period, price, trialDays, features = features ?? AllFeatures,
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
            slug, name = $"Gimnasio Panel {slug}", timeZone = "America/Guayaquil", locale = "es-EC", ownerEmail = $"owner@{slug}.test",
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

    private async Task<(HttpStatusCode Status, string? Code, JsonElement? Body)> SendAsync(HttpMethod method, string url, object? body = null)
    {
        using var platform = await TestApi.PlatformClientAsync(api);
        var response = await platform.SendAsync(new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) });
        var text = await response.Content.ReadAsStringAsync();
        var json = string.IsNullOrEmpty(text) ? (JsonElement?)null : JsonDocument.Parse(text).RootElement.Clone();
        var code = !response.IsSuccessStatusCode && json is { } j && j.TryGetProperty("code", out var c) ? c.GetString() : null;
        return (response.StatusCode, code, json);
    }

    private async Task<JsonElement> GetOkAsync(string url) => (await SendAsync(HttpMethod.Get, url)).Body!.Value;

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

    private static IEnumerable<Guid> Ids(JsonElement page) => page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid());

    // ---- Organizaciones ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task El_listado_de_organizaciones_filtra_y_nunca_incluye_el_gimnasio_de_plataforma()
    {
        var (_, code) = await PlanAsync(30m);
        var trial = await TenantAsync(code, trial: true);
        var active = await TenantAsync(code);
        var suspended = await TenantAsync(code);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, $"/api/v1/platform/tenants/{suspended.Id}/suspend")).Status);

        var byPlan = await GetOkAsync($"/api/v1/platform/organizations?planCode={code}&pageSize=100");
        Assert.Equal(3, byPlan.GetProperty("totalCount").GetInt32());
        var row = byPlan.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == trial.Id);
        Assert.Equal("Trialing", row.GetProperty("subscription").GetProperty("status").GetString());
        Assert.Equal(code, row.GetProperty("subscription").GetProperty("planCode").GetString());
        Assert.Equal("Active", row.GetProperty("tenantStatus").GetString());

        Assert.Equal([trial.Id], Ids(await GetOkAsync($"/api/v1/platform/organizations?planCode={code}&subscriptionStatus=Trialing")));
        Assert.Equal([suspended.Id], Ids(await GetOkAsync($"/api/v1/platform/organizations?planCode={code}&tenantStatus=Suspended")));
        Assert.Equal([active.Id], Ids(await GetOkAsync($"/api/v1/platform/organizations?search={active.Slug}")));
        Assert.Equal([trial.Id], Ids(await GetOkAsync($"/api/v1/platform/organizations?planCode={code}&dueWithinDays=12")));

        var all = await GetOkAsync("/api/v1/platform/organizations?pageSize=100&search=platform");
        Assert.DoesNotContain(all.GetProperty("items").EnumerateArray(), i => i.GetProperty("slug").GetString() == "platform");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await SendAsync(HttpMethod.Get, "/api/v1/platform/organizations?tenantStatus=Otro")).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await SendAsync(HttpMethod.Get, "/api/v1/platform/organizations?subscriptionStatus=Inventado")).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await SendAsync(HttpMethod.Get, "/api/v1/platform/organizations?dueWithinDays=9999")).Status);
    }

    [Fact]
    public async Task El_detalle_trae_contrato_derechos_cobros_y_nunca_datos_de_alumnos()
    {
        var (_, code) = await PlanAsync(45m, features: ["members", "attendance"]);
        var tenant = await TenantAsync(code, trial: true);
        var branch = await TestApi.CreateBranchAsync(tenant.Owner, "UIO");
        var member = await tenant.Owner.PostAsJsonAsync("/api/v1/members", new
        {
            branchId = branch, firstName = "Secreta", lastName = "Confidencial", idType = "Cedula", idNumber = MemberTests.NewCedula(), birthDate = "1990-01-01",
            email = "secreta.confidencial@alumnos.test", phone = "0991111111"
        });
        Assert.Equal(HttpStatusCode.Created, member.StatusCode);
        await SendAsync(HttpMethod.Post, $"/api/v1/platform/tenants/{tenant.Id}/subscription/invoices");

        var detail = await GetOkAsync($"/api/v1/platform/organizations/{tenant.Id}");

        Assert.Equal(tenant.Slug, detail.GetProperty("slug").GetString());
        Assert.Equal("Trialing", detail.GetProperty("subscription").GetProperty("status").GetString());
        Assert.Equal("Full", detail.GetProperty("entitlement").GetProperty("accessMode").GetString());
        Assert.Equal(["attendance", "members"], detail.GetProperty("entitlement").GetProperty("features").EnumerateArray().Select(f => f.GetString()!).Order().ToArray());
        Assert.Equal(3, detail.GetProperty("entitlement").GetProperty("limits").EnumerateArray().Single(l => l.GetProperty("code").GetString() == "branches").GetProperty("limit").GetInt32());
        Assert.Equal(1, detail.GetProperty("openInvoices").GetInt32());
        Assert.Equal(45m, detail.GetProperty("openInvoicesAmount").GetDecimal());
        Assert.Equal(1, detail.GetProperty("recentInvoices").GetArrayLength());

        // Mínimo privilegio: nada de lo que pertenece a los alumnos viaja en la respuesta.
        var raw = detail.GetRawText();
        Assert.DoesNotContain("Secreta", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("secreta.confidencial", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("0991111111", raw, StringComparison.Ordinal);

        Assert.Equal((HttpStatusCode.NotFound, "organizations.not_found"), (await SendAsync(HttpMethod.Get, $"/api/v1/platform/organizations/{Guid.NewGuid()}")).Let(r => (r.Status, r.Code)));
        var platformId = await ScalarAsync<Guid>("SELECT id FROM organizations.tenants WHERE slug = 'platform'");
        Assert.Equal((HttpStatusCode.Conflict, "organizations.platform_protected"), (await SendAsync(HttpMethod.Get, $"/api/v1/platform/organizations/{platformId}")).Let(r => (r.Status, r.Code)));
    }

    [Fact]
    public async Task El_consumo_se_mide_con_conteos_en_el_contexto_del_gimnasio_y_la_plataforma_no_puede_leer_sus_tablas()
    {
        var (_, code) = await PlanAsync(30m);
        var tenant = await TenantAsync(code);
        var branch = await TestApi.CreateBranchAsync(tenant.Owner, "UIO");
        await TestApi.CreateUserAsync(tenant.Owner, "entrena@staff.test", "Entrenador", branch);
        for (var i = 0; i < 2; i++)
        {
            var created = await tenant.Owner.PostAsJsonAsync("/api/v1/members", new
            {
                branchId = branch, firstName = $"Alumno{i}", lastName = "Medido", idType = "Cedula", idNumber = MemberTests.NewCedula(), birthDate = "1995-04-12",
                email = $"m{i}.{Guid.NewGuid():N}"[..18] + "@alumnos.test", phone = "0991234567"
            });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        // Antes de medir no hay foto.
        Assert.Equal(JsonValueKind.Null, (await GetOkAsync($"/api/v1/platform/organizations/{tenant.Id}")).GetProperty("usage").ValueKind);

        var measured = (await SendAsync(HttpMethod.Post, $"/api/v1/platform/organizations/{tenant.Id}/usage/refresh")).Body!.Value;
        Assert.Equal(1, measured.GetProperty("branches").GetInt32());
        Assert.Equal(2, measured.GetProperty("internalUsers").GetInt32()); // propietario + entrenador
        Assert.Equal(2, measured.GetProperty("members").GetInt32());

        await TestApi.CreateUserAsync(tenant.Owner, "otro@staff.test", "Entrenador", branch);
        await SendAsync(HttpMethod.Post, $"/api/v1/platform/organizations/{tenant.Id}/usage/refresh");
        Assert.Equal(1L, await ScalarAsync<long>($"SELECT count(*) FROM subscriptions.tenant_usage_snapshots WHERE tenant_id = '{tenant.Id}'"));
        Assert.Equal(3, await ScalarAsync<int>($"SELECT internal_users FROM subscriptions.tenant_usage_snapshots WHERE tenant_id = '{tenant.Id}'"));

        var listed = (await GetOkAsync($"/api/v1/platform/organizations?search={tenant.Slug}")).GetProperty("items")[0].GetProperty("usage");
        Assert.Equal(3, listed.GetProperty("internalUsers").GetInt32());
        Assert.Equal(2, listed.GetProperty("members").GetInt32());

        // La base lo respalda: con el contexto de plataforma NO se ven las tablas operativas del gimnasio (solo se contó entrando con el contexto del propio gimnasio).
        var platformId = await ScalarAsync<Guid>("SELECT id FROM organizations.tenants WHERE slug = 'platform'");
        await using var connection = new NpgsqlConnection(api.AppConnection);
        await connection.OpenAsync();
        await using (var set = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, false)", connection))
        {
            set.Parameters.AddWithValue("t", platformId.ToString());
            await set.ExecuteScalarAsync();
        }

        foreach (var table in new[] { "members.members", "identity.app_users WHERE tenant_id = '" + tenant.Id + "'", "organizations.branches WHERE tenant_id = '" + tenant.Id + "'" })
        {
            await using var count = new NpgsqlCommand($"SELECT count(*) FROM {table}", connection);
            Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
        }
    }

    [Fact]
    public async Task El_proceso_de_vencimientos_renueva_las_fotos_de_consumo_viejas()
    {
        var (_, code) = await PlanAsync(30m);
        var tenant = await TenantAsync(code);
        await SendAsync(HttpMethod.Post, $"/api/v1/platform/organizations/{tenant.Id}/usage/refresh");
        await ExecAsync($"UPDATE subscriptions.tenant_usage_snapshots SET measured_at = now() - interval '3 hours', branches = 99 WHERE tenant_id = '{tenant.Id}'");

        var run = (await SendAsync(HttpMethod.Post, "/api/v1/platform/subscriptions/run-lifecycle")).Body!.Value;

        Assert.True(run.GetProperty("usageRefreshed").GetInt32() >= 1);
        Assert.Equal(0, await ScalarAsync<int>($"SELECT branches FROM subscriptions.tenant_usage_snapshots WHERE tenant_id = '{tenant.Id}'"));
        Assert.True(await ScalarAsync<bool>($"SELECT measured_at > now() - interval '5 minutes' FROM subscriptions.tenant_usage_snapshots WHERE tenant_id = '{tenant.Id}'"));
    }

    // ---- Notas -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Las_notas_son_internas_solo_se_agregan_se_validan_y_el_gimnasio_no_las_ve()
    {
        var (_, code) = await PlanAsync(30m);
        var tenant = await TenantAsync(code);
        var path = $"/api/v1/platform/organizations/{tenant.Id}/notes";

        var created = await SendAsync(HttpMethod.Post, path, new { text = "  Pidió factura a nombre de la matriz.  " });
        Assert.Equal(HttpStatusCode.Created, created.Status);
        Assert.Equal("Pidió factura a nombre de la matriz.", created.Body!.Value.GetProperty("text").GetString());
        Assert.False(string.IsNullOrEmpty(created.Body!.Value.GetProperty("authorName").GetString()));
        await SendAsync(HttpMethod.Post, path, new { text = "Segunda nota" });

        var notes = (await GetOkAsync(path)).GetProperty("items").EnumerateArray().Select(n => n.GetProperty("text").GetString()!).ToArray();
        Assert.Equal(["Segunda nota", "Pidió factura a nombre de la matriz."], notes);
        Assert.Equal(2, (await GetOkAsync($"/api/v1/platform/organizations/{tenant.Id}")).GetProperty("notesCount").GetInt32());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await SendAsync(HttpMethod.Post, path, new { text = " " })).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await SendAsync(HttpMethod.Post, path, new { text = new string('x', 1001) })).Status);
        var sensitive = await SendAsync(HttpMethod.Post, path, new { text = "Pagó con la tarjeta 4111 1111 1111 1111" });
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "organizations.note_sensitive"), (sensitive.Status, sensitive.Code));
        Assert.Equal(2L, await ScalarAsync<long>($"SELECT count(*) FROM subscriptions.platform_notes WHERE tenant_id = '{tenant.Id}'"));

        // El propio gimnasio no las lee, ni por la API ni por la base; y no se editan ni se borran.
        Assert.Equal(HttpStatusCode.Forbidden, (await tenant.Owner.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tenant.Owner.PostAsJsonAsync(path, new { text = "yo mismo" })).StatusCode);
        await using var connection = new NpgsqlConnection(api.AppConnection);
        await connection.OpenAsync();
        await using (var set = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, false)", connection))
        {
            set.Parameters.AddWithValue("t", tenant.Id.ToString());
            await set.ExecuteScalarAsync();
        }

        await using var count = new NpgsqlCommand("SELECT count(*) FROM subscriptions.platform_notes", connection);
        Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);

        await using var admin = new NpgsqlConnection(api.AdminConnection);
        await admin.OpenAsync();
        await using var update = new NpgsqlCommand($"UPDATE subscriptions.platform_notes SET text = 'otra' WHERE tenant_id = '{tenant.Id}'", admin);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync());
        Assert.Contains("histórico", ex.MessageText);
    }

    // ---- Actividad y auditoría ---------------------------------------------------------------------------------------

    [Fact]
    public async Task La_actividad_muestra_las_acciones_administrativas_con_quien_las_hizo()
    {
        var (_, code) = await PlanAsync(30m);
        var tenant = await TenantAsync(code, trial: true);
        await SendAsync(HttpMethod.Post, $"/api/v1/platform/tenants/{tenant.Id}/subscription/extend", new { days = 5, reason = "Cortesía" });
        await SendAsync(HttpMethod.Post, $"/api/v1/platform/tenants/{tenant.Id}/subscription/invoices");
        await SendAsync(HttpMethod.Post, $"/api/v1/platform/organizations/{tenant.Id}/notes", new { text = "Cliente piloto" });
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, $"/api/v1/platform/tenants/{tenant.Id}/suspend")).Status);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, $"/api/v1/platform/tenants/{tenant.Id}/activate")).Status);

        var activity = await GetOkAsync($"/api/v1/platform/organizations/{tenant.Id}/activity?pageSize=100");
        var entries = activity.GetProperty("items").EnumerateArray().ToArray();
        var actions = entries.Select(e => e.GetProperty("action").GetString()).ToArray();

        Assert.Contains("Subscription.Created", actions);
        Assert.Contains("Subscription.Updated", actions);
        Assert.Contains("SubscriptionInvoice.Created", actions);
        Assert.Contains("PlatformNote.Created", actions);
        Assert.Contains("Tenant.Suspended", actions);
        Assert.Contains("Tenant.Activated", actions);
        Assert.All(entries.Where(e => e.GetProperty("action").GetString() is "Tenant.Suspended" or "SubscriptionInvoice.Created" or "PlatformNote.Created"),
            e => Assert.False(string.IsNullOrEmpty(e.GetProperty("actorName").GetString())));

        // En la base: quién (usuario del equipo de plataforma) y desde dónde quedó registrado.
        Assert.Equal(1L, await ScalarAsync<long>($"SELECT count(*) FROM audit.audit_logs WHERE action = 'Tenant.Suspended' AND entity_id = '{tenant.Id}' AND user_id IS NOT NULL"));

        // La actividad de una organización no mezcla la de otra.
        var other = await TenantAsync(code);
        var otherActions = (await GetOkAsync($"/api/v1/platform/organizations/{other.Id}/activity?pageSize=100")).GetProperty("items").EnumerateArray()
            .Select(e => e.GetProperty("action").GetString()).ToArray();
        Assert.DoesNotContain("Tenant.Suspended", otherActions);
        Assert.DoesNotContain("PlatformNote.Created", otherActions);
    }

    // ---- Métricas ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Las_metricas_cuadran_con_su_formula_documentada_y_distinguen_contratado_de_cobrado()
    {
        var (_, monthly) = await PlanAsync(30m);
        var (_, yearly) = await PlanAsync(120m, period: "Yearly");
        var (_, free) = await PlanAsync(0m);
        var a = await TenantAsync(monthly);
        var b = await TenantAsync(monthly);
        var c = await TenantAsync(yearly);
        var d = await TenantAsync(free);
        var trial = await TenantAsync(monthly, trial: true);

        var metrics = await GetOkAsync("/api/v1/platform/metrics");
        var byPlan = metrics.GetProperty("byPlan").EnumerateArray().ToDictionary(p => p.GetProperty("planCode").GetString()!);

        // MRR por plan: 30/mes ×2 activas = 60; 120/año ÷ 12 = 10; gratuito 0; la prueba no cuenta aunque sea del plan de 30.
        Assert.Equal(3, byPlan[monthly].GetProperty("subscriptions").GetInt32());
        Assert.Equal(60m, byPlan[monthly].GetProperty("mrr").GetDecimal());
        Assert.Equal(1, byPlan[yearly].GetProperty("subscriptions").GetInt32());
        Assert.Equal(10m, byPlan[yearly].GetProperty("mrr").GetDecimal());
        Assert.Equal(0m, byPlan[free].GetProperty("mrr").GetDecimal());

        var mrr = metrics.GetProperty("mrr").GetDecimal();
        Assert.Equal(mrr, metrics.GetProperty("byPlan").EnumerateArray().Sum(p => p.GetProperty("mrr").GetDecimal()));
        Assert.Equal(mrr * 12, metrics.GetProperty("arr").GetDecimal());
        Assert.Contains("No incluye pruebas", metrics.GetProperty("mrrFormula").GetString());
        Assert.Equal(metrics.GetProperty("collectedAmount").GetDecimal() - metrics.GetProperty("refundedAmount").GetDecimal(), metrics.GetProperty("netCollectedAmount").GetDecimal());

        var statuses = metrics.GetProperty("subscriptionsByStatus");
        Assert.True(statuses.GetProperty("Trialing").GetInt32() >= 1);
        Assert.True(statuses.GetProperty("Active").GetInt32() >= 4);
        Assert.Equal(statuses.GetProperty("Trialing").GetInt32(), metrics.GetProperty("trialsInProgress").GetInt32());
        var total = statuses.EnumerateObject().Sum(s => s.Value.GetInt32());
        Assert.Equal(metrics.GetProperty("organizations").GetInt32() - metrics.GetProperty("organizationsWithoutSubscription").GetInt32(), total);
        Assert.All(new[] { a.Id, b.Id, c.Id, d.Id, trial.Id }, id => Assert.NotEqual(Guid.Empty, id));
    }

    [Fact]
    public async Task Cobros_cancelaciones_y_conversion_de_pruebas_se_reflejan_en_las_metricas_del_periodo()
    {
        var (_, code) = await PlanAsync(40m);
        var before = await GetOkAsync("/api/v1/platform/metrics");

        // Una prueba que convierte (factura → pago → activa) y otra que se pierde (se cancela en prueba).
        var converting = await TenantAsync(code, trial: true);
        var invoice = (await SendAsync(HttpMethod.Post, $"/api/v1/platform/tenants/{converting.Id}/subscription/invoices")).Body!.Value;
        using (var platform = await TestApi.PlatformClientAsync(api))
        {
            using var pay = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/platform/billing/invoices/{invoice.GetProperty("id").GetString()}/pay")
            {
                Content = JsonContent.Create(new { amount = 40m, currency = "USD", method = "BankTransfer", reference = $"TRF-{Guid.NewGuid():N}"[..16] })
            };
            pay.Headers.Add("Idempotency-Key", $"k-{Guid.NewGuid():N}");
            Assert.Equal(HttpStatusCode.OK, (await platform.SendAsync(pay)).StatusCode);
        }

        var lost = await TenantAsync(code, trial: true);
        await SendAsync(HttpMethod.Post, $"/api/v1/platform/tenants/{lost.Id}/subscription/cancel", new { reason = "No le sirvió", atPeriodEnd = false });

        var after = await GetOkAsync("/api/v1/platform/metrics");

        Assert.True(after.GetProperty("collectedAmount").GetDecimal() >= before.GetProperty("collectedAmount").GetDecimal() + 40m);
        Assert.True(after.GetProperty("activationsPaid").GetInt32() >= before.GetProperty("activationsPaid").GetInt32() + 1);
        Assert.True(after.GetProperty("cancellations").GetInt32() >= before.GetProperty("cancellations").GetInt32() + 1);
        Assert.True(after.GetProperty("trialsConverted").GetInt32() >= before.GetProperty("trialsConverted").GetInt32() + 1);
        Assert.True(after.GetProperty("trialsLost").GetInt32() >= before.GetProperty("trialsLost").GetInt32() + 1);
        var rate = after.GetProperty("trialConversionPercent").GetDecimal();
        Assert.InRange(rate, 0m, 100m);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await SendAsync(HttpMethod.Get, "/api/v1/platform/metrics?from=2026-12-01&to=2026-01-01")).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await SendAsync(HttpMethod.Get, "/api/v1/platform/metrics?from=2020-01-01&to=2026-01-01")).Status);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, $"/api/v1/platform/metrics?from={Today.AddDays(-400):yyyy-MM-dd}&to={Today:yyyy-MM-dd}")).Status);
    }

    [Fact]
    public async Task Las_metricas_son_agregadas_y_no_exponen_datos_de_gimnasios_ni_de_alumnos()
    {
        var (_, code) = await PlanAsync(30m);
        var tenant = await TenantAsync(code);

        var raw = (await GetOkAsync("/api/v1/platform/metrics")).GetRawText();

        Assert.DoesNotContain(tenant.Slug, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(tenant.OwnerEmail, raw, StringComparison.Ordinal);
    }

    // ---- Seguridad ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Solo_el_equipo_de_plataforma_ve_el_panel_y_un_gimnasio_no_ve_a_otro()
    {
        var (_, code) = await PlanAsync(30m);
        var a = await TenantAsync(code);
        var b = await TenantAsync(code);

        foreach (var path in new[]
        {
            "/api/v1/platform/organizations", $"/api/v1/platform/organizations/{b.Id}", $"/api/v1/platform/organizations/{b.Id}/notes",
            $"/api/v1/platform/organizations/{b.Id}/activity", "/api/v1/platform/metrics"
        })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await a.Owner.GetAsync(path)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await a.Owner.PostAsync($"/api/v1/platform/organizations/{b.Id}/usage/refresh", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await a.Owner.PostAsJsonAsync($"/api/v1/platform/organizations/{b.Id}/notes", new { text = "x" })).StatusCode);
        using var anonymous = api.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/platform/organizations")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/platform/metrics")).StatusCode);
        Assert.Equal(0L, await ScalarAsync<long>($"SELECT count(*) FROM subscriptions.platform_notes WHERE tenant_id = '{b.Id}'"));

        // Los permisos nuevos solo existen para el equipo de plataforma y no se pueden dar a un rol de gimnasio.
        var role = await a.Owner.PostAsJsonAsync("/api/v1/roles", new
        {
            name = "Espía", description = "x", grantsAllBranches = false,
            permissions = new[] { "platform.organizations.read", "platform.metrics.read" }
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, role.StatusCode);
        Assert.Equal("roles.permission_not_found", await TestApi.ProblemCodeAsync(role));
    }
}

