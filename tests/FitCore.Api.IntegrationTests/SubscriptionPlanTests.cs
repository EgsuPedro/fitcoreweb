using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FitCore.Api.IntegrationTests.Infrastructure;
using Npgsql;

namespace FitCore.Api.IntegrationTests;

/// <summary>Catálogo de planes comerciales de FitCore 360 (módulo Subscriptions): versiones, límites, funciones, auditoría y aislamiento.</summary>
[Collection(ApiCollection3.Name)]
public class SubscriptionPlanTests(ApiFixture api)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static string NewCode(string prefix = "plan") => $"{prefix}-{Guid.NewGuid():N}"[..20];

    private static readonly object[] AllLimits =
    [
        new { code = "branches", value = 1 },
        new { code = "internal_users", value = 5 },
        new { code = "members", value = 100 },
        new { code = "storage_mb", value = (long?)null }
    ];

    private static object Body(
        string? code = null, decimal price = 29.90m, string period = "Monthly", int trialDays = 14, int? graceDays = null,
        string[]? features = null, object[]? limits = null, string name = "Plan de prueba", string? description = "Descripción", bool isActive = true) => new
    {
        code = code ?? NewCode(),
        name,
        description,
        rules = "Uso razonable",
        isActive,
        currency = "USD",
        billingPeriod = period,
        price,
        trialDays,
        graceDays,
        features = features ?? ["members", "attendance"],
        limits = limits ?? AllLimits,
        changeNote = "Alta"
    };

    private static object UpdateBody(
        decimal price = 29.90m, string period = "Monthly", int trialDays = 14, int? graceDays = null,
        string[]? features = null, object[]? limits = null, string name = "Plan de prueba", string? description = "Descripción") => new
    {
        name,
        description,
        rules = "Uso razonable",
        currency = "USD",
        billingPeriod = period,
        price,
        trialDays,
        graceDays,
        features = features ?? ["members", "attendance"],
        limits = limits ?? AllLimits,
        changeNote = "Ajuste"
    };

    private async Task<HttpClient> PlatformAsync() => await TestApi.PlatformClientAsync(api);

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"Respondió {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private async Task<(Guid Id, string Code)> CreatePlanAsync(HttpClient platform, object? body = null)
    {
        body ??= Body();
        var created = await ReadAsync(await platform.PostAsJsonAsync("/api/v1/platform/plans", body), HttpStatusCode.Created);
        return (created.GetProperty("id").GetGuid(), created.GetProperty("code").GetString()!);
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(api.AdminConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private async Task<string?> SqlErrorAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
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

    [Fact]
    public async Task Crear_un_plan_lo_persiste_con_su_primera_version_y_precio_decimal_exacto()
    {
        using var platform = await PlatformAsync();
        var (id, code) = await CreatePlanAsync(platform, Body(price: 49.90m, period: "Quarterly", trialDays: 14, graceDays: 5, features: ["members", "classes", "exports"]));

        var plan = await ReadAsync(await platform.GetAsync($"/api/v1/platform/plans/{id}"));
        Assert.Equal(code, plan.GetProperty("code").GetString());
        Assert.True(plan.GetProperty("isActive").GetBoolean());
        var current = plan.GetProperty("current");
        Assert.Equal(1, current.GetProperty("number").GetInt32());
        Assert.Equal(49.90m, current.GetProperty("price").GetDecimal());
        Assert.Equal("Quarterly", current.GetProperty("billingPeriod").GetString());
        Assert.Equal(5, current.GetProperty("graceDays").GetInt32());
        Assert.Equal(["classes", "exports", "members"], current.GetProperty("features").EnumerateArray().Select(f => f.GetString()!).ToArray());
        var storage = current.GetProperty("limits").EnumerateArray().Single(l => l.GetProperty("code").GetString() == "storage_mb");
        Assert.Equal(JsonValueKind.Null, storage.GetProperty("value").ValueKind);

        // Lo que guarda PostgreSQL: numeric exacto, una versión, funciones y los 4 límites (el ilimitado es NULL).
        Assert.Equal(49.90m, await ScalarAsync<decimal>($"SELECT price FROM subscriptions.plan_versions WHERE plan_id = '{id}'"));
        Assert.Equal(1L, await ScalarAsync<long>($"SELECT count(*) FROM subscriptions.plan_versions WHERE plan_id = '{id}'"));
        Assert.Equal(3L, await ScalarAsync<long>($"SELECT count(*) FROM subscriptions.plan_features f JOIN subscriptions.plan_versions v ON v.id = f.plan_version_id WHERE v.plan_id = '{id}'"));
        Assert.Equal(4L, await ScalarAsync<long>($"SELECT count(*) FROM subscriptions.plan_limits l JOIN subscriptions.plan_versions v ON v.id = l.plan_version_id WHERE v.plan_id = '{id}'"));
        Assert.Equal(1L, await ScalarAsync<long>($"SELECT count(*) FROM subscriptions.plan_limits l JOIN subscriptions.plan_versions v ON v.id = l.plan_version_id WHERE v.plan_id = '{id}' AND l.value IS NULL"));
    }

    [Fact]
    public async Task Un_plan_gratuito_con_precio_cero_es_valido()
    {
        using var platform = await PlatformAsync();
        var (id, _) = await CreatePlanAsync(platform, Body(price: 0m, trialDays: 0));

        var plan = await ReadAsync(await platform.GetAsync($"/api/v1/platform/plans/{id}"));
        Assert.Equal(0m, plan.GetProperty("current").GetProperty("price").GetDecimal());
    }

    [Fact]
    public async Task Los_datos_invalidos_se_rechazan_con_un_codigo_claro()
    {
        using var platform = await PlatformAsync();

        var cases = new (object Body, string Field)[]
        {
            (Body(code: "X"), "code"),
            (Body(code: "Con Espacios"), "code"),
            (Body(price: -1m), "price"),
            (Body(price: 10.999m), "price"),
            (Body(price: 1_000_000.01m), "price"),
            (Body(trialDays: -1), "trialDays"),
            (Body(trialDays: 366), "trialDays"),
            (Body(graceDays: 91), "graceDays"),
            (Body(features: ["members", "no_existe"]), "features"),
            (Body(features: ["members", "members"]), "features"),
            (Body(limits: [new { code = "members", value = 10 }]), "limits"),
            (Body(limits: [new { code = "branches", value = 1 }, new { code = "internal_users", value = 1 }, new { code = "members", value = -5 }, new { code = "storage_mb", value = 1 }]), "limits"),
            (Body(limits: [.. AllLimits, new { code = "inventado", value = 1 }]), "limits"),
            (Body(period: "Weekly"), "request.invalid_body")
        };

        foreach (var (body, code) in cases)
        {
            var response = await platform.PostAsJsonAsync("/api/v1/platform/plans", body);
            var text = await response.Content.ReadAsStringAsync();
            if (code == "request.invalid_body")
            {
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                Assert.Contains(code, text);
                continue;
            }

            // Cada regla incumplida señala el campo que corrige quien llama.
            Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{code}: respondió {(int)response.StatusCode}: {text}");
            Assert.True(JsonDocument.Parse(text).RootElement.GetProperty("errors").TryGetProperty(code, out _), $"Faltó el error del campo {code}: {text}");
        }

        var currency = await platform.PostAsJsonAsync("/api/v1/platform/plans", new { code = NewCode(), name = "X", isActive = true, currency = "EUR", billingPeriod = "Monthly", price = 1m, trialDays = 0, features = Array.Empty<string>(), limits = AllLimits });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, currency.StatusCode);
        Assert.Contains("La moneda no está admitida.", await currency.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task El_codigo_es_unico_sin_distinguir_mayusculas()
    {
        using var platform = await PlatformAsync();
        var code = NewCode("unico");
        await CreatePlanAsync(platform, Body(code: code));

        var again = await platform.PostAsJsonAsync("/api/v1/platform/plans", Body(code: code.ToUpperInvariant()));

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("plans.code_taken", await TestApi.ProblemCodeAsync(again));
        Assert.Equal(1L, await ScalarAsync<long>($"SELECT count(*) FROM subscriptions.plans WHERE code = '{code}'"));
    }

    [Fact]
    public async Task Editar_solo_datos_descriptivos_no_crea_version_pero_un_cambio_comercial_si()
    {
        using var platform = await PlatformAsync();
        var (id, _) = await CreatePlanAsync(platform, Body(price: 30m, features: ["members", "attendance"]));

        // Nombre, descripción y el orden de las funciones no son condiciones comerciales.
        var cosmetic = await ReadAsync(await platform.PutAsJsonAsync($"/api/v1/platform/plans/{id}", UpdateBody(price: 30m, name: "Nombre nuevo", description: "Otra", features: ["attendance", "members"])));
        Assert.Equal(1, cosmetic.GetProperty("current").GetProperty("number").GetInt32());
        Assert.Equal("Nombre nuevo", cosmetic.GetProperty("name").GetString());
        Assert.Equal(1L, await ScalarAsync<long>($"SELECT count(*) FROM subscriptions.plan_versions WHERE plan_id = '{id}'"));

        // El precio sí lo es: versión 2, y la 1 sigue exactamente como se contrató.
        var repriced = await ReadAsync(await platform.PutAsJsonAsync($"/api/v1/platform/plans/{id}", UpdateBody(price: 45m, name: "Nombre nuevo", features: ["members", "attendance"])));
        Assert.Equal(2, repriced.GetProperty("current").GetProperty("number").GetInt32());
        Assert.Equal(45m, repriced.GetProperty("current").GetProperty("price").GetDecimal());
        var versions = repriced.GetProperty("versions").EnumerateArray().ToArray();
        Assert.Equal([2, 1], versions.Select(v => v.GetProperty("number").GetInt32()).ToArray());
        Assert.Equal(30m, versions[1].GetProperty("price").GetDecimal());

        Assert.Equal(30m, await ScalarAsync<decimal>($"SELECT price FROM subscriptions.plan_versions WHERE plan_id = '{id}' AND number = 1"));
        Assert.Equal(45m, await ScalarAsync<decimal>($"SELECT price FROM subscriptions.plan_versions WHERE plan_id = '{id}' AND number = 2"));
    }

    [Fact]
    public async Task Cambiar_limites_o_funciones_crea_otra_version_y_conserva_las_anteriores()
    {
        using var platform = await PlatformAsync();
        var (id, _) = await CreatePlanAsync(platform);

        var moreMembers = await ReadAsync(await platform.PutAsJsonAsync($"/api/v1/platform/plans/{id}", UpdateBody(limits:
            [new { code = "branches", value = 1 }, new { code = "internal_users", value = 5 }, new { code = "members", value = 500 }, new { code = "storage_mb", value = (long?)null }])));
        Assert.Equal(2, moreMembers.GetProperty("current").GetProperty("number").GetInt32());

        var moreFeatures = await ReadAsync(await platform.PutAsJsonAsync($"/api/v1/platform/plans/{id}", UpdateBody(features: ["members", "attendance", "classes"], limits:
            [new { code = "branches", value = 1 }, new { code = "internal_users", value = 5 }, new { code = "members", value = 500 }, new { code = "storage_mb", value = (long?)null }])));
        Assert.Equal(3, moreFeatures.GetProperty("current").GetProperty("number").GetInt32());

        var versions = moreFeatures.GetProperty("versions").EnumerateArray().ToArray();
        long? MembersLimit(JsonElement v) => v.GetProperty("limits").EnumerateArray().Single(l => l.GetProperty("code").GetString() == "members").GetProperty("value").GetInt64();
        Assert.Equal([500L, 500L, 100L], versions.Select(v => MembersLimit(v)).ToArray());
        Assert.Equal(2, versions[2].GetProperty("features").GetArrayLength());
        Assert.Equal(3, versions[0].GetProperty("features").GetArrayLength());
    }

    [Fact]
    public async Task Desactivar_un_plan_no_borra_su_historial_y_se_puede_reactivar()
    {
        using var platform = await PlatformAsync();
        var (id, code) = await CreatePlanAsync(platform);
        await platform.PutAsJsonAsync($"/api/v1/platform/plans/{id}", UpdateBody(price: 99m));

        var off = await ReadAsync(await platform.PatchAsJsonAsync($"/api/v1/platform/plans/{id}/status", new { isActive = false }));
        Assert.False(off.GetProperty("isActive").GetBoolean());

        // Sigue existiendo con todas sus versiones; solo deja de ser asignable.
        Assert.Equal(2L, await ScalarAsync<long>($"SELECT count(*) FROM subscriptions.plan_versions WHERE plan_id = '{id}'"));
        Assert.False(await ScalarAsync<bool>($"SELECT is_active FROM subscriptions.plans WHERE id = '{id}'"));

        var inactive = await ReadAsync(await platform.GetAsync($"/api/v1/platform/plans?isActive=false&search={code}"));
        Assert.Contains(inactive.GetProperty("items").EnumerateArray(), p => p.GetProperty("id").GetGuid() == id);
        var active = await ReadAsync(await platform.GetAsync($"/api/v1/platform/plans?isActive=true&search={code}"));
        Assert.Empty(active.GetProperty("items").EnumerateArray());

        var on = await ReadAsync(await platform.PatchAsJsonAsync($"/api/v1/platform/plans/{id}/status", new { isActive = true }));
        Assert.True(on.GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task La_base_impide_borrar_o_reescribir_planes_y_versiones_aunque_se_intente_por_sql()
    {
        using var platform = await PlatformAsync();
        var (id, _) = await CreatePlanAsync(platform);
        var admin = api.AdminConnection;

        Assert.Contains("histórico", await SqlErrorAsync(admin, $"DELETE FROM subscriptions.plans WHERE id = '{id}'") ?? "NO ERROR");
        Assert.Contains("histórico", await SqlErrorAsync(admin, $"DELETE FROM subscriptions.plan_versions WHERE plan_id = '{id}'") ?? "NO ERROR");
        Assert.Contains("histórico", await SqlErrorAsync(admin, $"UPDATE subscriptions.plan_versions SET price = 1 WHERE plan_id = '{id}'") ?? "NO ERROR");
        Assert.Contains("histórico", await SqlErrorAsync(admin, "DELETE FROM subscriptions.plan_limits") ?? "NO ERROR");
        Assert.Contains("no se pueden modificar", await SqlErrorAsync(admin, $"UPDATE subscriptions.plans SET code = 'otro-codigo' WHERE id = '{id}'") ?? "NO ERROR");

        // Las versiones son consecutivas: no se puede saltar el número.
        var gap = await SqlErrorAsync(admin, $"""
            INSERT INTO subscriptions.plan_versions (id, plan_id, number, currency, period, price, trial_days, created_at)
            VALUES (gen_random_uuid(), '{id}', 5, 'USD', 'Monthly', 1, 0, now())
            """);
        Assert.Contains("siguiente", gap ?? "NO ERROR");

        // El rol de la aplicación tampoco tiene permiso de DELETE.
        var denied = await SqlErrorAsync(api.AppConnection, $"DELETE FROM subscriptions.plans WHERE id = '{id}'");
        Assert.Contains("permission denied", denied ?? "NO ERROR");

        Assert.Equal(1L, await ScalarAsync<long>($"SELECT count(*) FROM subscriptions.plans WHERE id = '{id}'"));
        Assert.Equal(1L, await ScalarAsync<long>($"SELECT count(*) FROM subscriptions.plan_versions WHERE plan_id = '{id}'"));
    }

    [Fact]
    public async Task Los_cambios_de_planes_quedan_auditados()
    {
        using var platform = await PlatformAsync();
        var (id, _) = await CreatePlanAsync(platform, Body(price: 20m));
        await platform.PutAsJsonAsync($"/api/v1/platform/plans/{id}", UpdateBody(price: 25m));
        await platform.PatchAsJsonAsync($"/api/v1/platform/plans/{id}/status", new { isActive = false });

        var actions = await ScalarAsync<string[]>($"SELECT array_agg(action ORDER BY id) FROM audit.audit_logs WHERE entity IN ('Plan', 'PlanVersion') AND (entity_id = '{id}' OR after::text LIKE '%{id}%')");
        Assert.Contains("Plan.Created", actions);
        Assert.Equal(2, actions.Count(a => a == "PlanVersion.Created"));
        Assert.Contains("Plan.Updated", actions);

        // El precio anterior y el nuevo constan en la auditoría.
        var after = await ScalarAsync<string>($"SELECT string_agg(after::text, ' ') FROM audit.audit_logs WHERE entity = 'PlanVersion' AND after::text LIKE '%{id}%'");
        Assert.Contains("20", after);
        Assert.Contains("25", after);
    }

    [Fact]
    public async Task Solo_el_equipo_de_plataforma_administra_los_planes_y_ningun_gimnasio_puede_concederse_ese_permiso()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        using var platform = await PlatformAsync();
        var (id, _) = await CreatePlanAsync(platform);

        foreach (var path in new[] { "/api/v1/platform/plans", "/api/v1/platform/plans/catalog", $"/api/v1/platform/plans/{id}" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await tenant.Owner.GetAsync(path)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await tenant.Owner.PostAsJsonAsync("/api/v1/platform/plans", Body())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tenant.Owner.PutAsJsonAsync($"/api/v1/platform/plans/{id}", UpdateBody(price: 1m))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tenant.Owner.PatchAsJsonAsync($"/api/v1/platform/plans/{id}/status", new { isActive = false })).StatusCode);

        using var anonymous = api.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/platform/plans")).StatusCode);

        // Un propietario no puede fabricarse un rol con permisos de plataforma.
        var role = await tenant.Owner.PostAsJsonAsync("/api/v1/roles", new { name = "Plan hacker", description = "x", grantsAllBranches = false, permissions = new[] { "platform.plans.manage", "platform.plans.read" } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, role.StatusCode);
        Assert.Equal("roles.permission_not_found", await TestApi.ProblemCodeAsync(role));

        // Nada cambió.
        Assert.True(await ScalarAsync<bool>($"SELECT is_active FROM subscriptions.plans WHERE id = '{id}'"));
    }

    [Fact]
    public async Task El_equipo_de_plataforma_recibe_los_permisos_de_planes()
    {
        using var platform = await PlatformAsync();
        var me = await ReadAsync(await platform.GetAsync("/api/v1/auth/me"));
        var permissions = me.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ToArray();
        Assert.Contains("platform.plans.read", permissions);
        Assert.Contains("platform.plans.manage", permissions);
    }

    [Fact]
    public async Task Ediciones_simultaneas_nunca_dejan_versiones_repetidas_ni_huecos()
    {
        using var platform = await PlatformAsync();
        var (id, _) = await CreatePlanAsync(platform, Body(price: 10m));

        var responses = await Task.WhenAll(Enumerable.Range(1, 8).Select(async i =>
        {
            using var client = await PlatformAsync();
            return await client.PutAsJsonAsync($"/api/v1/platform/plans/{id}", UpdateBody(price: 10m + i));
        }));

        // Las ediciones se atienden una tras otra: todas tienen éxito y cada una crea la versión siguiente.
        Assert.All(responses, r => Assert.True(r.StatusCode == HttpStatusCode.OK, $"Respondió {(int)r.StatusCode}"));

        var numbers = await ScalarAsync<int[]>($"SELECT array_agg(number ORDER BY number) FROM subscriptions.plan_versions WHERE plan_id = '{id}'");
        Assert.Equal(Enumerable.Range(1, 9), numbers);
        var prices = await ScalarAsync<decimal[]>($"SELECT array_agg(price ORDER BY number) FROM subscriptions.plan_versions WHERE plan_id = '{id}'");
        Assert.Equal(Enumerable.Range(1, 8).Select(i => 10m + i).Order(), prices.Skip(1).Order());
    }

    [Fact]
    public async Task El_catalogo_lista_las_funciones_y_los_limites_validos()
    {
        using var platform = await PlatformAsync();
        var catalog = await ReadAsync(await platform.GetAsync("/api/v1/platform/plans/catalog"));

        var features = catalog.GetProperty("features").EnumerateArray().Select(f => f.GetProperty("code").GetString()!).ToArray();
        var limits = catalog.GetProperty("limits").EnumerateArray().Select(f => f.GetProperty("code").GetString()!).ToArray();
        Assert.Contains("classes", features);
        Assert.Contains("advanced_reports", features);
        Assert.Contains("exports", features);
        Assert.Equal(["branches", "internal_users", "members", "storage_mb"], limits.OrderBy(c => c, StringComparer.Ordinal).ToArray());
        Assert.Equal(["Monthly", "Quarterly", "Yearly"], catalog.GetProperty("billingPeriods").EnumerateArray().Select(p => p.GetString()!).ToArray());
    }

    [Fact]
    public async Task Los_planes_comerciales_no_tocan_los_planes_de_membresia_de_los_gimnasios()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        using var platform = await PlatformAsync();
        var before = await ScalarAsync<long>("SELECT count(*) FROM members.membership_plans");

        await CreatePlanAsync(platform, Body(price: 77m));

        Assert.Equal(before, await ScalarAsync<long>("SELECT count(*) FROM members.membership_plans"));
        var gymPlans = await tenant.Owner.GetFromJsonAsync<PagedDto<JsonElement>>("/api/v1/membership-plans", Json);
        Assert.Empty(gymPlans!.Items);
    }
}
