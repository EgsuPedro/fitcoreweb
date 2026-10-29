using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FitCore.Api.IntegrationTests.Infrastructure;
using Npgsql;
using static FitCore.Api.IntegrationTests.Infrastructure.CommercialTestApi;

namespace FitCore.Api.IntegrationTests;

/// <summary>Capacidades comerciales: límites de sucursales, usuarios y alumnos, funcionalidades por plan y modo restringido.</summary>
[Collection(ApiCollection2.Name)]
public class SubscriptionLimitTests(ApiFixture api)
{
    private static readonly string[] AllFeatures =
        ["members", "branches", "cash_payments", "attendance", "training", "nutrition", "classes", "advanced_reports", "exports", "advanced_permissions", "extra_storage"];

    private static object[] Limits(long? branches = null, long? users = null, long? members = null) =>
    [
        new { code = "branches", value = branches }, new { code = "internal_users", value = users },
        new { code = "members", value = members }, new { code = "storage_mb", value = (long?)null }
    ];

    private async Task<string> PlanCodeAsync(object[] limits, string[]? features = null, decimal price = 25m)
    {
        using var platform = await TestApi.PlatformClientAsync(api);
        var code = $"lim-{Guid.NewGuid():N}"[..20];
        var response = await platform.PostAsJsonAsync("/api/v1/platform/plans", new
        {
            code, name = "Plan limitado", isActive = true, currency = "USD", billingPeriod = "Monthly", price, trialDays = 0,
            features = features ?? AllFeatures, limits
        });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return code;
    }

    private async Task<TestTenant> TenantWithPlanAsync(string planCode)
    {
        var slug = $"gym-{Guid.NewGuid():N}"[..20];
        using var platform = await TestApi.PlatformClientAsync(api);
        var response = await platform.PostAsJsonAsync("/api/v1/platform/tenants", new
        {
            slug, name = $"Gimnasio {slug}", timeZone = "America/Guayaquil", locale = "es-EC", ownerEmail = $"owner@{slug}.test",
            ownerFullName = "Dueño", ownerPassword = TestApi.OwnerPassword, planCode, startTrial = false
        });
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, text);
        var id = JsonDocument.Parse(text).RootElement.GetProperty("id").GetGuid();
        var owner = TestApi.Authenticated(api, await TestApi.LoginAsync(api, slug, $"owner@{slug}.test", TestApi.OwnerPassword));
        return new TestTenant(id, slug, $"owner@{slug}.test", TestApi.OwnerPassword, owner);
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

    private static Task<HttpResponseMessage> PostBranchAsync(HttpClient owner, string code) =>
        owner.PostAsJsonAsync("/api/v1/branches", new { code, name = $"Sucursal {code}", timeZone = "America/Guayaquil", sriEstablishmentCode = "001", address = (string?)null });

    private static async Task<HttpResponseMessage> PostMemberAsync(HttpClient owner, Guid branchId) =>
        await owner.PostAsJsonAsync("/api/v1/members", new
        {
            branchId, firstName = "Alumno", lastName = "Limite", idType = "Cedula", idNumber = MemberTests.NewCedula(), birthDate = "1995-04-12",
            email = $"m.{Guid.NewGuid():N}"[..20] + "@alumnos.test", phone = "0991234567"
        });

    private static async Task<(HttpStatusCode Status, string? Code)> ProblemAsync(HttpResponseMessage response) =>
        (response.StatusCode, response.IsSuccessStatusCode ? null : await TestApi.ProblemCodeAsync(response));

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Respondió {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private async Task SetStatusAsync(Guid tenantId, string status)
    {
        using var platform = await TestApi.PlatformClientAsync(api);
        if (status == "Suspended")
        {
            Assert.Equal(HttpStatusCode.OK, (await platform.PostAsJsonAsync($"/api/v1/platform/tenants/{tenantId}/subscription/suspend", new { reason = "Prueba" })).StatusCode);
        }
        else
        {
            await ExecAsync($"UPDATE subscriptions.subscriptions SET status = '{status}' WHERE tenant_id = '{tenantId}' AND status <> 'Cancelled'");
        }
    }

    [Fact]
    public async Task El_limite_de_sucursales_bloquea_la_siguiente_y_se_libera_al_desactivar_una()
    {
        var tenant = await TenantWithPlanAsync(await PlanCodeAsync(Limits(branches: 2)));

        Assert.Equal(HttpStatusCode.Created, (await PostBranchAsync(tenant.Owner, "UIO")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await PostBranchAsync(tenant.Owner, "GYE")).StatusCode);

        var third = await PostBranchAsync(tenant.Owner, "CUE");
        Assert.Equal((HttpStatusCode.Conflict, "subscription.limit_reached"), await ProblemAsync(third));
        Assert.Contains("permite 2 sucursales", (await third.Content.ReadAsStringAsync()).Replace("\\u00F3", "ó"));
        Assert.Equal(2L, await ScalarAsync<long>($"SELECT count(*) FROM organizations.branches WHERE tenant_id = '{tenant.Id}'"));

        // Desactivar una libera cupo; reactivarla de nuevo sobre el tope también se rechaza (lo garantiza la base).
        var branches = await tenant.Owner.GetFromJsonAsync<PagedDto<JsonElement>>("/api/v1/branches");
        var first = branches!.Items.First(b => b.GetProperty("code").GetString() == "UIO");
        var firstId = first.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await tenant.Owner.PutAsJsonAsync($"/api/v1/branches/{firstId}", new { name = "Quito", timeZone = "America/Guayaquil", sriEstablishmentCode = "001", address = (string?)null, isActive = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await PostBranchAsync(tenant.Owner, "CUE")).StatusCode);

        var reactivate = await tenant.Owner.PutAsJsonAsync($"/api/v1/branches/{firstId}", new { name = "Quito", timeZone = "America/Guayaquil", sriEstablishmentCode = "001", address = (string?)null, isActive = true });
        Assert.Equal((HttpStatusCode.Conflict, "subscription.limit_reached"), await ProblemAsync(reactivate));
        Assert.Equal(2L, await ScalarAsync<long>($"SELECT count(*) FROM organizations.branches WHERE tenant_id = '{tenant.Id}' AND is_active"));
    }

    [Fact]
    public async Task Sin_la_funcion_de_sucursales_solo_se_permite_la_primera()
    {
        var tenant = await TenantWithPlanAsync(await PlanCodeAsync(Limits(branches: 5), AllFeatures.Where(f => f != "branches").ToArray()));

        Assert.Equal(HttpStatusCode.Created, (await PostBranchAsync(tenant.Owner, "UIO")).StatusCode);
        var second = await PostBranchAsync(tenant.Owner, "GYE");

        Assert.Equal((HttpStatusCode.Forbidden, "subscription.feature_unavailable"), await ProblemAsync(second));
        Assert.Equal(1L, await ScalarAsync<long>($"SELECT count(*) FROM organizations.branches WHERE tenant_id = '{tenant.Id}'"));
    }

    [Fact]
    public async Task El_limite_de_usuarios_cuenta_al_personal_pero_no_a_los_alumnos_con_acceso_al_portal()
    {
        // Plan de 2 usuarios internos: el propietario ya ocupa uno.
        var tenant = await TenantWithPlanAsync(await PlanCodeAsync(Limits(users: 2)));
        var branch = await TestApi.CreateBranchAsync(tenant.Owner, "UIO");
        await TestApi.CreateUserAsync(tenant.Owner, "uno@staff.test", "Entrenador", branch);

        var blocked = await tenant.Owner.PostAsJsonAsync("/api/v1/users", new
        {
            email = "dos@staff.test", fullName = "Segundo", password = "Clave-Segura-1234",
            roleIds = new[] { await TestApi.RoleIdAsync(tenant.Owner, "Entrenador") }, branchIds = new[] { branch }
        });
        Assert.Equal((HttpStatusCode.Conflict, "subscription.limit_reached"), await ProblemAsync(blocked));

        // Dar acceso al portal a un alumno no consume cupo de personal.
        var member = await JsonAsync(await PostMemberAsync(tenant.Owner, branch));
        var access = await tenant.Owner.PostAsync($"/api/v1/members/{member.GetProperty("id").GetGuid()}/access", null);
        Assert.Equal(HttpStatusCode.Created, access.StatusCode);

        Assert.Equal(2L, await ScalarAsync<long>($"SELECT count(*) FROM identity.app_users WHERE tenant_id = '{tenant.Id}' AND account_kind = 'Staff'"));
        Assert.Equal(1L, await ScalarAsync<long>($"SELECT count(*) FROM identity.app_users WHERE tenant_id = '{tenant.Id}' AND account_kind = 'Member'"));

        var usage = await JsonAsync(await tenant.Owner.GetAsync("/api/v1/my/subscription/usage"));
        var users = usage.GetProperty("limits").EnumerateArray().Single(l => l.GetProperty("code").GetString() == "internal_users");
        Assert.Equal(2, users.GetProperty("used").GetInt32());
        Assert.Equal(0, users.GetProperty("remaining").GetInt32());
        Assert.True(users.GetProperty("reached").GetBoolean());
    }

    [Fact]
    public async Task El_limite_de_alumnos_bloquea_el_alta_y_deja_intactos_a_los_ya_registrados()
    {
        var tenant = await TenantWithPlanAsync(await PlanCodeAsync(Limits(members: 2)));
        var branch = await TestApi.CreateBranchAsync(tenant.Owner, "UIO");

        Assert.Equal(HttpStatusCode.Created, (await PostMemberAsync(tenant.Owner, branch)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await PostMemberAsync(tenant.Owner, branch)).StatusCode);
        var third = await PostMemberAsync(tenant.Owner, branch);

        Assert.Equal((HttpStatusCode.Conflict, "subscription.limit_reached"), await ProblemAsync(third));
        Assert.Equal(2L, await ScalarAsync<long>($"SELECT count(*) FROM members.members WHERE tenant_id = '{tenant.Id}'"));
        var list = await tenant.Owner.GetFromJsonAsync<PagedDto<JsonElement>>("/api/v1/members");
        Assert.Equal(2, list!.TotalCount);
    }

    [Fact]
    public async Task Altas_simultaneas_nunca_superan_el_limite_en_alumnos_sucursales_ni_usuarios()
    {
        var tenant = await TenantWithPlanAsync(await PlanCodeAsync(Limits(branches: 3, users: 4, members: 5)));
        var branch = await TestApi.CreateBranchAsync(tenant.Owner, "UIO");
        var roleId = await TestApi.RoleIdAsync(tenant.Owner, "Entrenador");

        var members = await Task.WhenAll(Enumerable.Range(0, 14).Select(_ => PostMemberAsync(tenant.Owner, branch)));
        var branches = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => PostBranchAsync(tenant.Owner, $"B{i}")));
        var users = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => tenant.Owner.PostAsJsonAsync("/api/v1/users", new
        {
            email = $"u{i}@staff.test", fullName = $"Usuario {i}", password = "Clave-Segura-1234", roleIds = new[] { roleId }, branchIds = new[] { branch }
        })));

        foreach (var batch in new[] { members, branches, users })
        {
            Assert.All(batch, r => Assert.True(r.StatusCode is HttpStatusCode.Created or HttpStatusCode.Conflict, $"Respondió {(int)r.StatusCode}"));
        }

        Assert.Equal(5, members.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(2, branches.Count(r => r.StatusCode == HttpStatusCode.Created)); // ya existe UIO: caben 2 más
        Assert.Equal(3, users.Count(r => r.StatusCode == HttpStatusCode.Created)); // el propietario ocupa uno

        Assert.Equal(5L, await ScalarAsync<long>($"SELECT count(*) FROM members.members WHERE tenant_id = '{tenant.Id}'"));
        Assert.Equal(3L, await ScalarAsync<long>($"SELECT count(*) FROM organizations.branches WHERE tenant_id = '{tenant.Id}' AND is_active"));
        Assert.Equal(4L, await ScalarAsync<long>($"SELECT count(*) FROM identity.app_users WHERE tenant_id = '{tenant.Id}' AND is_active AND account_kind = 'Staff'"));
    }

    [Fact]
    public async Task La_base_rechaza_el_exceso_aunque_se_salte_la_aplicacion()
    {
        var tenant = await TenantWithPlanAsync(await PlanCodeAsync(Limits(branches: 1)));
        var branch = await TestApi.CreateBranchAsync(tenant.Owner, "UIO");

        await using var connection = new NpgsqlConnection(api.AppConnection);
        await connection.OpenAsync();
        await using (var set = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, false)", connection))
        {
            set.Parameters.AddWithValue("t", tenant.Id.ToString());
            await set.ExecuteScalarAsync();
        }

        await using var insert = new NpgsqlCommand($"""
            INSERT INTO organizations.branches (id, tenant_id, code, name, time_zone, is_active, created_at)
            VALUES (gen_random_uuid(), '{tenant.Id}', 'SQL', 'Directa', 'America/Guayaquil', true, now())
            """, connection);
        var error = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());

        Assert.Equal("FC101", error.SqlState);
        Assert.Equal(1L, await ScalarAsync<long>($"SELECT count(*) FROM organizations.branches WHERE tenant_id = '{tenant.Id}'"));
        Assert.NotEqual(Guid.Empty, branch);
    }

    [Fact]
    public async Task Con_la_suscripcion_restringida_se_consulta_y_se_cobra_pero_no_se_crean_recursos_ni_funciones_premium()
    {
        var tenant = await TenantWithPlanAsync(await PlanCodeAsync(Limits()));
        var branch = await TestApi.CreateBranchAsync(tenant.Owner, "UIO");
        var member = await JsonAsync(await PostMemberAsync(tenant.Owner, branch));
        var memberId = member.GetProperty("id").GetGuid();

        await SetStatusAsync(tenant.Id, "Suspended");
        Assert.Equal("Restricted", await ScalarAsync<string>($"SELECT access_mode FROM subscriptions.tenant_entitlements WHERE tenant_id = '{tenant.Id}'"));

        // No se crea nada nuevo…
        Assert.Equal((HttpStatusCode.Forbidden, "subscription.restricted"), await ProblemAsync(await PostBranchAsync(tenant.Owner, "GYE")));
        Assert.Equal((HttpStatusCode.Forbidden, "subscription.restricted"), await ProblemAsync(await PostMemberAsync(tenant.Owner, branch)));
        Assert.Equal((HttpStatusCode.Forbidden, "subscription.restricted"), await ProblemAsync(await tenant.Owner.PostAsJsonAsync("/api/v1/users", new
        {
            email = "nuevo@staff.test", fullName = "Nuevo", password = "Clave-Segura-1234", roleIds = new[] { await TestApi.RoleIdAsync(tenant.Owner, "Entrenador") }, branchIds = new[] { branch }
        })));
        // …ni se modifican las funciones premium; su consulta sigue disponible.
        Assert.Equal((HttpStatusCode.Forbidden, "subscription.restricted"), await ProblemAsync(await tenant.Owner.PostAsJsonAsync("/api/v1/group-classes", new { name = "Yoga" })));
        Assert.Equal(HttpStatusCode.OK, (await tenant.Owner.GetAsync("/api/v1/group-classes")).StatusCode);

        // Se consulta lo propio y se exporta.
        Assert.Equal(HttpStatusCode.OK, (await tenant.Owner.GetAsync("/api/v1/members")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await tenant.Owner.GetAsync("/api/v1/branches")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await tenant.Owner.GetAsync("/api/v1/reports/exports")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await tenant.Owner.GetAsync("/api/v1/my/subscription/usage")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await tenant.Owner.GetAsync("/api/v1/analytics/dashboard?preset=Last30Days")).StatusCode);

        // Las operaciones financieras de los alumnos no se bloquean por una restricción comercial.
        var obligation = await tenant.Owner.PostAsJsonAsync("/api/v1/payment-obligations", new { memberId, concept = "Producto", amount = 20m, taxKind = "General" });
        Assert.Equal(HttpStatusCode.Created, obligation.StatusCode);
        var payment = await PostPaymentAsync(tenant.Owner, PaymentBody(memberId, 20m, "Card", "VCH-RESTRINGIDO"));
        Assert.True(payment.StatusCode == HttpStatusCode.Created, await payment.Content.ReadAsStringAsync());

        // La sesión sigue abierta y regularizar la suscripción devuelve todo.
        using var platform = await TestApi.PlatformClientAsync(api);
        await platform.PostAsJsonAsync($"/api/v1/platform/tenants/{tenant.Id}/subscription/reactivate", new { note = "Regularizó" });
        Assert.Equal("Full", await ScalarAsync<string>($"SELECT access_mode FROM subscriptions.tenant_entitlements WHERE tenant_id = '{tenant.Id}'"));
        Assert.Equal(HttpStatusCode.Created, (await PostBranchAsync(tenant.Owner, "GYE")).StatusCode);
    }

    [Fact]
    public async Task En_mora_dentro_de_la_gracia_todo_sigue_funcionando()
    {
        var tenant = await TenantWithPlanAsync(await PlanCodeAsync(Limits()));
        var branch = await TestApi.CreateBranchAsync(tenant.Owner, "UIO");
        await SetStatusAsync(tenant.Id, "PastDue");

        Assert.Equal("Grace", await ScalarAsync<string>($"SELECT access_mode FROM subscriptions.tenant_entitlements WHERE tenant_id = '{tenant.Id}'"));
        Assert.Equal(HttpStatusCode.Created, (await PostMemberAsync(tenant.Owner, branch)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await PostBranchAsync(tenant.Owner, "GYE")).StatusCode);

        // Vencida (fin de la gracia): se restringe.
        await SetStatusAsync(tenant.Id, "Expired");
        Assert.Equal("Restricted", await ScalarAsync<string>($"SELECT access_mode FROM subscriptions.tenant_entitlements WHERE tenant_id = '{tenant.Id}'"));
        Assert.Equal((HttpStatusCode.Forbidden, "subscription.restricted"), await ProblemAsync(await PostMemberAsync(tenant.Owner, branch)));
    }

    [Fact]
    public async Task Cada_funcionalidad_se_exige_en_el_servidor_segun_el_plan()
    {
        var withoutPremium = AllFeatures.Where(f => f is not ("classes" or "training" or "nutrition" or "advanced_reports" or "exports" or "advanced_permissions" or "extra_storage")).ToArray();
        var tenant = await TenantWithPlanAsync(await PlanCodeAsync(Limits(), withoutPremium));

        foreach (var path in new[]
        {
            "/api/v1/group-classes", "/api/v1/class-sessions", "/api/v1/exercises", "/api/v1/training-plans",
            "/api/v1/analytics/dashboard?preset=Today", "/api/v1/reports/financial/collections?preset=Today", "/api/v1/reports/exports"
        })
        {
            Assert.Equal((HttpStatusCode.Forbidden, "subscription.feature_unavailable"), await ProblemAsync(await tenant.Owner.GetAsync(path)));
        }

        // Los roles de plantilla se pueden consultar; crear roles propios exige «Gestión avanzada de permisos».
        Assert.Equal(HttpStatusCode.OK, (await tenant.Owner.GetAsync("/api/v1/roles")).StatusCode);
        var role = await tenant.Owner.PostAsJsonAsync("/api/v1/roles", new { name = "Propio", description = "x", grantsAllBranches = false, permissions = new[] { "members.read" } });
        Assert.Equal((HttpStatusCode.Forbidden, "subscription.feature_unavailable"), await ProblemAsync(role));

        // Lo básico del plan sigue disponible.
        Assert.Equal(HttpStatusCode.OK, (await tenant.Owner.GetAsync("/api/v1/members")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await tenant.Owner.GetAsync("/api/v1/attendance")).StatusCode);

        // Con la función incluida, la ruta responde.
        var full = await TenantWithPlanAsync(await PlanCodeAsync(Limits()));
        Assert.Equal(HttpStatusCode.OK, (await full.Owner.GetAsync("/api/v1/group-classes")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await full.Owner.GetAsync("/api/v1/exercises")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await full.Owner.GetAsync("/api/v1/reports/exports")).StatusCode);
    }

    [Fact]
    public async Task Pasar_a_un_plan_menor_no_borra_nada_pero_bloquea_las_altas_hasta_bajar_del_limite()
    {
        var tenant = await TenantWithPlanAsync(await PlanCodeAsync(Limits()));
        var branch = await TestApi.CreateBranchAsync(tenant.Owner, "UIO");
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.Created, (await PostMemberAsync(tenant.Owner, branch)).StatusCode);
        }

        // Cambio de plan hacia uno de 1 alumno (el flujo de cambio llega en el bloque 5; aquí se simula cancelando y contratando).
        await ExecAsync($"UPDATE subscriptions.subscriptions SET status = 'Cancelled', cancelled_at = now(), cancel_reason = 'Cambio de plan' WHERE tenant_id = '{tenant.Id}'");
        using var platform = await TestApi.PlatformClientAsync(api);
        var small = await PlanCodeAsync(Limits(members: 1));
        var planId = (await JsonAsync(await platform.GetAsync($"/api/v1/platform/plans?search={small}"))).GetProperty("items")[0].GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Created, (await platform.PostAsJsonAsync($"/api/v1/platform/tenants/{tenant.Id}/subscription", new { planId, startTrial = false })).StatusCode);

        Assert.Equal(3L, await ScalarAsync<long>($"SELECT count(*) FROM members.members WHERE tenant_id = '{tenant.Id}'"));
        Assert.Equal(3, (await tenant.Owner.GetFromJsonAsync<PagedDto<JsonElement>>("/api/v1/members"))!.TotalCount);
        Assert.Equal((HttpStatusCode.Conflict, "subscription.limit_reached"), await ProblemAsync(await PostMemberAsync(tenant.Owner, branch)));

        var usage = await JsonAsync(await tenant.Owner.GetAsync("/api/v1/my/subscription/usage"));
        var members = usage.GetProperty("limits").EnumerateArray().Single(l => l.GetProperty("code").GetString() == "members");
        Assert.Equal(3, members.GetProperty("used").GetInt32());
        Assert.Equal(1, members.GetProperty("limit").GetInt32());
        Assert.Equal(0, members.GetProperty("remaining").GetInt32());
        Assert.True(members.GetProperty("reached").GetBoolean());
    }

    [Fact]
    public async Task Los_derechos_vigentes_los_deriva_la_base_de_la_suscripcion_y_siempre_estan_al_dia()
    {
        var code = await PlanCodeAsync(Limits(branches: 4, members: 50), ["members", "classes"]);
        var tenant = await TenantWithPlanAsync(code);

        Assert.Equal("Full", await ScalarAsync<string>($"SELECT access_mode FROM subscriptions.tenant_entitlements WHERE tenant_id = '{tenant.Id}'"));
        Assert.Equal(["classes", "members"], await ScalarAsync<string[]>($"SELECT features FROM subscriptions.tenant_entitlements WHERE tenant_id = '{tenant.Id}'"));
        Assert.Equal("4", await ScalarAsync<string>($"SELECT limits ->> 'branches' FROM subscriptions.tenant_entitlements WHERE tenant_id = '{tenant.Id}'"));
        Assert.True(await ScalarAsync<bool>($"SELECT limits -> 'internal_users' = 'null'::jsonb FROM subscriptions.tenant_entitlements WHERE tenant_id = '{tenant.Id}'"));

        // Una escritura directa también la actualiza: no hay forma de dejarla desfasada.
        await ExecAsync($"UPDATE subscriptions.subscriptions SET status = 'PastDue' WHERE tenant_id = '{tenant.Id}'");
        Assert.Equal("Grace", await ScalarAsync<string>($"SELECT access_mode FROM subscriptions.tenant_entitlements WHERE tenant_id = '{tenant.Id}'"));
        await ExecAsync($"UPDATE subscriptions.subscriptions SET status = 'Expired' WHERE tenant_id = '{tenant.Id}'");
        Assert.Equal("Restricted", await ScalarAsync<string>($"SELECT access_mode FROM subscriptions.tenant_entitlements WHERE tenant_id = '{tenant.Id}'"));
        Assert.Equal("Expired", await ScalarAsync<string>($"SELECT status FROM subscriptions.tenant_entitlements WHERE tenant_id = '{tenant.Id}'"));
    }

    [Fact]
    public async Task Un_gimnasio_solo_ve_el_consumo_y_los_derechos_propios_y_no_puede_escribirlos()
    {
        var a = await TenantWithPlanAsync(await PlanCodeAsync(Limits(members: 7)));
        var b = await TenantWithPlanAsync(await PlanCodeAsync(Limits(members: 9)));

        var usageA = await JsonAsync(await a.Owner.GetAsync("/api/v1/my/subscription/usage"));
        var usageB = await JsonAsync(await b.Owner.GetAsync("/api/v1/my/subscription/usage"));
        Assert.Equal(7, usageA.GetProperty("limits").EnumerateArray().Single(l => l.GetProperty("code").GetString() == "members").GetProperty("limit").GetInt32());
        Assert.Equal(9, usageB.GetProperty("limits").EnumerateArray().Single(l => l.GetProperty("code").GetString() == "members").GetProperty("limit").GetInt32());

        await using var connection = new NpgsqlConnection(api.AppConnection);
        await connection.OpenAsync();
        await using (var set = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, false)", connection))
        {
            set.Parameters.AddWithValue("t", a.Id.ToString());
            await set.ExecuteScalarAsync();
        }

        await using var count = new NpgsqlCommand("SELECT count(*) FROM subscriptions.tenant_entitlements", connection);
        Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!);
        await using var tamper = new NpgsqlCommand($"UPDATE subscriptions.tenant_entitlements SET limits = '{{}}'::jsonb WHERE tenant_id = '{a.Id}'", connection);
        Assert.Equal(0, await tamper.ExecuteNonQueryAsync());
        Assert.Equal("7", await ScalarAsync<string>($"SELECT limits ->> 'members' FROM subscriptions.tenant_entitlements WHERE tenant_id = '{a.Id}'"));
    }

    [Fact]
    public async Task Sin_derechos_registrados_el_servidor_falla_cerrado_y_el_consumo_exige_permiso()
    {
        var tenant = await TenantWithPlanAsync(await PlanCodeAsync(Limits()));
        var branch = await TestApi.CreateBranchAsync(tenant.Owner, "UIO");
        var staff = await TestApi.CreateUserAsync(tenant.Owner, "entrena@staff.test", "Entrenador", branch);

        // Un usuario sin el permiso de suscripción no ve el consumo (el rol Entrenador no lo tiene).
        using var trainer = TestApi.Authenticated(api, await TestApi.LoginAsync(api, tenant.Slug, "entrena@staff.test", TestApi.UserPassword));
        Assert.Equal(HttpStatusCode.Forbidden, (await trainer.GetAsync("/api/v1/my/subscription/usage")).StatusCode);
        using var anonymous = api.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/my/subscription/usage")).StatusCode);
        Assert.NotEqual(Guid.Empty, staff.Id);

        await ExecAsync($"DELETE FROM subscriptions.tenant_entitlements WHERE tenant_id = '{tenant.Id}'");
        Assert.Equal((HttpStatusCode.Forbidden, "subscription.not_found"), await ProblemAsync(await PostMemberAsync(tenant.Owner, branch)));
        Assert.Equal((HttpStatusCode.Forbidden, "subscription.not_found"), await ProblemAsync(await tenant.Owner.GetAsync("/api/v1/group-classes")));
    }
}
