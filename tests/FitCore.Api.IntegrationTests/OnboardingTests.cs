using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FitCore.Api.IntegrationTests.Infrastructure;
using Npgsql;

namespace FitCore.Api.IntegrationTests;

/// <summary>Asistente de configuración inicial: avance persistente, pasos idempotentes, validaciones, permisos y aislamiento entre gimnasios.</summary>
[Collection(ApiCollection1.Name)]
public class OnboardingTests(ApiFixture api)
{
    private static string NewSlug() => $"onb-{Guid.NewGuid():N}"[..20];

    private async Task ExecAsync(string sql, params (string Name, object Value)[] args)
    {
        await using var conn = new NpgsqlConnection(api.AdminConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in args)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<string?> ScalarAsync(string sql, params (string Name, object Value)[] args)
    {
        await using var conn = new NpgsqlConnection(api.AdminConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in args)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        return (await cmd.ExecuteScalarAsync())?.ToString();
    }

    /// <summary>Un gimnasio recién registrado: pendiente de configuración y sin sucursales.</summary>
    private async Task<TestTenant> PendingTenantAsync()
    {
        var tenant = await TestApi.NewTenantAsync(api, NewSlug());
        await ExecAsync("UPDATE organizations.tenants SET setup_status = 'PendingSetup' WHERE id = @t", ("t", tenant.Id));
        return tenant;
    }

    private static object Profile(string name = "Gimnasio Real", string email = "info@gimnasio.test", string? phone = "+593 99 123 4567", string country = "ec") => new
    {
        name, contactEmail = email, contactPhone = phone, country, timeZone = "America/Guayaquil", locale = "es-EC"
    };

    private static object Branch(string name = "Sede Centro", object[]? hours = null) => new
    {
        name, address = "Av. Amazonas N24-03 y Colón", phone = "02 222 3333", timeZone = "America/Guayaquil",
        hours = hours ?? [new { day = 1, closed = false, open = "06:00", close = "21:00" }, new { day = 0, closed = true }]
    };

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"Respondió {(int)response.StatusCode}: {text}");
        return string.IsNullOrEmpty(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static string Status(JsonElement dto, string step) =>
        dto.GetProperty("steps").EnumerateArray().Single(s => s.GetProperty("code").GetString() == step).GetProperty("status").GetString()!;

    // ---- estado inicial ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_gym_from_before_the_wizard_has_it_closed_and_nothing_is_demanded()
    {
        var tenant = await TestApi.NewTenantAsync(api, NewSlug());

        var state = await JsonAsync(await tenant.Owner.GetAsync("/api/v1/onboarding"));

        Assert.Equal("Ready", state.GetProperty("setupStatus").GetString());
        Assert.True(state.GetProperty("completed").GetBoolean());
        Assert.Equal("finish", state.GetProperty("currentStep").GetString());
    }

    [Fact]
    public async Task A_pending_gym_starts_at_the_first_step_with_nothing_done()
    {
        var tenant = await PendingTenantAsync();

        var state = await JsonAsync(await tenant.Owner.GetAsync("/api/v1/onboarding"));

        Assert.Equal("PendingSetup", state.GetProperty("setupStatus").GetString());
        Assert.False(state.GetProperty("completed").GetBoolean());
        Assert.Equal("profile", state.GetProperty("currentStep").GetString());
        Assert.False(state.GetProperty("canComplete").GetBoolean());
        Assert.All(new[] { "profile", "branch", "settings", "team" }, step => Assert.Equal("Pending", Status(state, step)));
        Assert.Equal(JsonValueKind.Null, state.GetProperty("branch").ValueKind);
        Assert.Equal("USD", state.GetProperty("profile").GetProperty("currency").GetString());
    }

    [Fact]
    public async Task The_progress_is_kept_on_the_server_and_survives_a_new_login()
    {
        var tenant = await PendingTenantAsync();
        await JsonAsync(await tenant.Owner.PutAsJsonAsync("/api/v1/onboarding/profile", Profile()));

        var newSession = TestApi.Authenticated(api, await TestApi.LoginAsync(api, tenant.Slug, tenant.OwnerEmail, tenant.OwnerPassword));
        var state = await JsonAsync(await newSession.GetAsync("/api/v1/onboarding"));

        Assert.Equal("Done", Status(state, "profile"));
        Assert.Equal("branch", state.GetProperty("currentStep").GetString());
        Assert.Equal("Gimnasio Real", state.GetProperty("profile").GetProperty("name").GetString());
    }

    // ---- paso 1: datos del gimnasio ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Saving_the_profile_stores_the_contact_data_and_is_idempotent()
    {
        var tenant = await PendingTenantAsync();

        var first = await JsonAsync(await tenant.Owner.PutAsJsonAsync("/api/v1/onboarding/profile", Profile()));
        var doneAt = first.GetProperty("steps").EnumerateArray().Single(s => s.GetProperty("code").GetString() == "profile").GetProperty("at").GetDateTimeOffset();
        var second = await JsonAsync(await tenant.Owner.PutAsJsonAsync("/api/v1/onboarding/profile", Profile(name: "Gimnasio Renombrado", country: "CO")));

        var profile = second.GetProperty("profile");
        Assert.Equal("Gimnasio Renombrado", profile.GetProperty("name").GetString());
        Assert.Equal("CO", profile.GetProperty("country").GetString());
        Assert.Equal("info@gimnasio.test", profile.GetProperty("contactEmail").GetString());
        Assert.Equal(doneAt.ToUnixTimeMilliseconds(), second.GetProperty("steps").EnumerateArray().Single(s => s.GetProperty("code").GetString() == "profile").GetProperty("at").GetDateTimeOffset().ToUnixTimeMilliseconds());
        Assert.Equal("1", await ScalarAsync("SELECT count(*)::text FROM organizations.tenant_onboarding WHERE tenant_id = @t", ("t", tenant.Id)));
        Assert.Equal("Gimnasio Renombrado", await ScalarAsync("SELECT name FROM organizations.tenants WHERE id = @t", ("t", tenant.Id)));
    }

    [Theory]
    [InlineData("name", "")]
    [InlineData("contactEmail", "no-es-correo")]
    [InlineData("contactEmail", "")]
    [InlineData("contactPhone", "abc")]
    [InlineData("country", "Ecuador")]
    [InlineData("timeZone", "Marte/Olimpo")]
    [InlineData("locale", "fr")]
    public async Task Invalid_profile_data_is_rejected_and_nothing_changes(string field, string value)
    {
        var tenant = await PendingTenantAsync();
        var body = new Dictionary<string, object?>
        {
            ["name"] = "Gimnasio", ["contactEmail"] = "info@gimnasio.test", ["contactPhone"] = "0999999999", ["country"] = "EC",
            ["timeZone"] = "America/Guayaquil", ["locale"] = "es-EC"
        };
        body[field] = value;

        var response = await tenant.Owner.PutAsJsonAsync("/api/v1/onboarding/profile", body);

        Assert.False(response.IsSuccessStatusCode);
        var state = await JsonAsync(await tenant.Owner.GetAsync("/api/v1/onboarding"));
        Assert.Equal("Pending", Status(state, "profile"));
    }

    // ---- paso 2: sucursal principal ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Saving_the_branch_creates_the_main_branch_once_and_then_updates_it()
    {
        var tenant = await PendingTenantAsync();

        var first = await JsonAsync(await tenant.Owner.PutAsJsonAsync("/api/v1/onboarding/branch", Branch()));
        var second = await JsonAsync(await tenant.Owner.PutAsJsonAsync("/api/v1/onboarding/branch", Branch(name: "Sede Norte")));

        Assert.Equal("Done", Status(first, "branch"));
        Assert.Equal(first.GetProperty("branch").GetProperty("id").GetGuid(), second.GetProperty("branch").GetProperty("id").GetGuid());
        Assert.Equal("Sede Norte", second.GetProperty("branch").GetProperty("name").GetString());
        Assert.Equal("1", await ScalarAsync("SELECT count(*)::text FROM organizations.branches WHERE tenant_id = @t", ("t", tenant.Id)));
        Assert.Equal("PRINCIPAL", await ScalarAsync("SELECT code FROM organizations.branches WHERE tenant_id = @t", ("t", tenant.Id)));
        var hours = second.GetProperty("branch").GetProperty("hours");
        Assert.Equal(2, hours.GetArrayLength());
        Assert.Equal("06:00", hours.EnumerateArray().Single(h => h.GetProperty("day").GetInt32() == 1).GetProperty("open").GetString());
        Assert.True(hours.EnumerateArray().Single(h => h.GetProperty("day").GetInt32() == 0).GetProperty("closed").GetBoolean());
    }

    [Fact]
    public async Task If_the_gym_already_has_a_branch_the_step_updates_the_first_one_instead_of_creating_another()
    {
        var tenant = await PendingTenantAsync();
        var existing = await TestApi.CreateBranchAsync(tenant.Owner, "MATRIZ");

        var state = await JsonAsync(await tenant.Owner.PutAsJsonAsync("/api/v1/onboarding/branch", Branch(name: "Matriz renovada")));

        Assert.Equal(existing, state.GetProperty("branch").GetProperty("id").GetGuid());
        Assert.Equal("1", await ScalarAsync("SELECT count(*)::text FROM organizations.branches WHERE tenant_id = @t", ("t", tenant.Id)));
        Assert.Equal("001", await ScalarAsync("SELECT sri_establishment_code FROM organizations.branches WHERE tenant_id = @t", ("t", tenant.Id))); // lo que no es del asistente se conserva
    }

    public static TheoryData<string, object[]> BadHours => new()
    {
        { "apertura posterior al cierre", [new { day = 1, closed = false, open = "22:00", close = "06:00" }] },
        { "apertura igual al cierre", [new { day = 1, closed = false, open = "08:00", close = "08:00" }] },
        { "día repetido", [new { day = 2, closed = true }, new { day = 2, closed = true }] },
        { "día fuera de rango", [new { day = 7, closed = true }] },
        { "hora mal escrita", [new { day = 1, closed = false, open = "6am", close = "9pm" }] },
        { "hora inexistente", [new { day = 1, closed = false, open = "06:00", close = "25:00" }] },
        { "abierto sin horas", [new { day = 1, closed = false }] }
    };

    [Theory]
    [MemberData(nameof(BadHours))]
    public async Task An_invalid_schedule_is_rejected(string reason, object[] hours)
    {
        var tenant = await PendingTenantAsync();

        var response = await tenant.Owner.PutAsJsonAsync("/api/v1/onboarding/branch", Branch(hours: hours));

        Assert.False(response.IsSuccessStatusCode, reason);
        Assert.Equal("0", await ScalarAsync("SELECT count(*)::text FROM organizations.branches WHERE tenant_id = @t", ("t", tenant.Id)));
    }

    // ---- pasos opcionales y cierre ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Optional_steps_can_be_completed_or_skipped_and_required_ones_cannot()
    {
        var tenant = await PendingTenantAsync();

        var settings = await JsonAsync(await tenant.Owner.PostAsync("/api/v1/onboarding/steps/settings/complete", null));
        var team = await JsonAsync(await tenant.Owner.PostAsync("/api/v1/onboarding/steps/team/skip", null));
        var profile = await tenant.Owner.PostAsync("/api/v1/onboarding/steps/profile/skip", null);
        var unknown = await tenant.Owner.PostAsync("/api/v1/onboarding/steps/inventada/complete", null);

        Assert.Equal("Done", Status(settings, "settings"));
        Assert.Equal("Skipped", Status(team, "team"));
        Assert.False(profile.IsSuccessStatusCode);
        Assert.False(unknown.IsSuccessStatusCode);
    }

    [Fact]
    public async Task Finishing_requires_the_two_mandatory_steps()
    {
        var tenant = await PendingTenantAsync();

        var early = await tenant.Owner.PostAsync("/api/v1/onboarding/complete", null);
        await tenant.Owner.PutAsJsonAsync("/api/v1/onboarding/profile", Profile());
        var halfway = await tenant.Owner.PostAsync("/api/v1/onboarding/complete", null);

        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, halfway.StatusCode);
        Assert.Equal("PendingSetup", await ScalarAsync("SELECT setup_status FROM organizations.tenants WHERE id = @t", ("t", tenant.Id)));
    }

    [Fact]
    public async Task Finishing_marks_the_gym_ready_skips_what_was_left_and_is_repeatable()
    {
        var tenant = await PendingTenantAsync();
        await tenant.Owner.PutAsJsonAsync("/api/v1/onboarding/profile", Profile());
        await tenant.Owner.PutAsJsonAsync("/api/v1/onboarding/branch", Branch());

        var done = await JsonAsync(await tenant.Owner.PostAsync("/api/v1/onboarding/complete", null));
        var again = await JsonAsync(await tenant.Owner.PostAsync("/api/v1/onboarding/complete", null));

        Assert.True(done.GetProperty("completed").GetBoolean());
        Assert.Equal("Ready", done.GetProperty("setupStatus").GetString());
        Assert.Equal("Skipped", Status(done, "settings"));
        Assert.Equal("Skipped", Status(done, "team"));
        Assert.True(again.GetProperty("completed").GetBoolean());
        Assert.Equal("Ready", await ScalarAsync("SELECT setup_status FROM organizations.tenants WHERE id = @t", ("t", tenant.Id)));
        Assert.Equal("1", await ScalarAsync("SELECT count(*)::text FROM audit.audit_logs WHERE tenant_id = @t AND action = 'Onboarding.Completed'", ("t", tenant.Id)));
    }

    [Fact]
    public async Task The_current_session_reports_the_setup_status_before_and_after_finishing()
    {
        var tenant = await PendingTenantAsync();
        Assert.Equal("PendingSetup", (await JsonAsync(await tenant.Owner.GetAsync("/api/v1/auth/me"))).GetProperty("setupStatus").GetString());

        await tenant.Owner.PutAsJsonAsync("/api/v1/onboarding/profile", Profile());
        await tenant.Owner.PutAsJsonAsync("/api/v1/onboarding/branch", Branch());
        await tenant.Owner.PostAsync("/api/v1/onboarding/complete", null);

        Assert.Equal("Ready", (await JsonAsync(await tenant.Owner.GetAsync("/api/v1/auth/me"))).GetProperty("setupStatus").GetString());
    }

    [Fact]
    public async Task A_pending_gym_can_keep_using_the_system_while_it_finishes_the_setup()
    {
        var tenant = await PendingTenantAsync();

        var branches = await tenant.Owner.GetAsync("/api/v1/branches");
        var settings = await tenant.Owner.GetAsync("/api/v1/commercial-settings");

        Assert.Equal(HttpStatusCode.OK, branches.StatusCode);
        Assert.Equal(HttpStatusCode.OK, settings.StatusCode);
    }

    // ---- permisos y aislamiento --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Staff_without_permission_to_edit_the_gym_can_look_but_not_change()
    {
        var tenant = await PendingTenantAsync();
        var branchId = await TestApi.CreateBranchAsync(tenant.Owner, "UNO");
        await TestApi.CreateUserAsync(tenant.Owner, $"coach@{tenant.Slug}.test", "Entrenador", branchId);
        var coach = TestApi.Authenticated(api, await TestApi.LoginAsync(api, tenant.Slug, $"coach@{tenant.Slug}.test", TestApi.UserPassword));

        var read = await coach.GetAsync("/api/v1/onboarding");
        var write = await coach.PutAsJsonAsync("/api/v1/onboarding/profile", Profile());
        var finish = await coach.PostAsync("/api/v1/onboarding/complete", null);

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, finish.StatusCode);
    }

    [Fact]
    public async Task Anonymous_requests_are_refused()
    {
        using var anon = api.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/v1/onboarding")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PutAsJsonAsync("/api/v1/onboarding/profile", Profile())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsync("/api/v1/onboarding/complete", null)).StatusCode);
    }

    [Fact]
    public async Task Each_gym_only_sees_and_changes_its_own_setup()
    {
        var a = await PendingTenantAsync();
        var b = await PendingTenantAsync();
        await a.Owner.PutAsJsonAsync("/api/v1/onboarding/profile", Profile(name: "Gimnasio A"));
        await a.Owner.PutAsJsonAsync("/api/v1/onboarding/branch", Branch(name: "Sede A"));

        var seenByB = await JsonAsync(await b.Owner.GetAsync("/api/v1/onboarding"));
        await b.Owner.PutAsJsonAsync("/api/v1/onboarding/profile", Profile(name: "Gimnasio B"));

        Assert.Equal("Pending", Status(seenByB, "profile"));
        Assert.Equal(JsonValueKind.Null, seenByB.GetProperty("branch").ValueKind);
        Assert.Equal("Gimnasio A", await ScalarAsync("SELECT name FROM organizations.tenants WHERE id = @t", ("t", a.Id)));
        Assert.Equal("Gimnasio B", await ScalarAsync("SELECT name FROM organizations.tenants WHERE id = @t", ("t", b.Id)));
        Assert.Equal("1", await ScalarAsync("SELECT count(*)::text FROM organizations.tenant_onboarding WHERE tenant_id = @t AND branch_completed_at IS NULL", ("t", b.Id)));
    }

    [Fact]
    public async Task The_database_itself_isolates_the_progress_rows_by_gym()
    {
        var a = await PendingTenantAsync();
        var b = await PendingTenantAsync();
        await a.Owner.GetAsync("/api/v1/onboarding");
        await b.Owner.GetAsync("/api/v1/onboarding");

        await using var conn = new NpgsqlConnection(api.AppConnection);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using (var set = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, true)", conn, tx))
        {
            set.Parameters.AddWithValue("t", a.Id.ToString());
            await set.ExecuteNonQueryAsync();
        }

        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM organizations.tenant_onboarding WHERE tenant_id = @other", conn, tx);
        cmd.Parameters.AddWithValue("other", b.Id);
        Assert.Equal(0L, await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Every_step_leaves_an_audit_trail()
    {
        var tenant = await PendingTenantAsync();
        await tenant.Owner.PutAsJsonAsync("/api/v1/onboarding/profile", Profile());
        await tenant.Owner.PutAsJsonAsync("/api/v1/onboarding/branch", Branch());
        await tenant.Owner.PostAsync("/api/v1/onboarding/steps/team/skip", null);

        var actions = await ScalarAsync(
            "SELECT string_agg(DISTINCT action, ',' ORDER BY action) FROM audit.audit_logs WHERE tenant_id = @t AND action LIKE 'Onboarding.%'", ("t", tenant.Id));

        Assert.Equal("Onboarding.BranchSaved,Onboarding.ProfileSaved,Onboarding.StepSkipped", actions);
    }
}
