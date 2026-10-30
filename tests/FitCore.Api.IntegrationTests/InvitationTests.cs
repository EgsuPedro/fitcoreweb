using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FitCore.Api.IntegrationTests.Infrastructure;
using FitCore.Platform.Application.AccountTokens;
using Npgsql;

namespace FitCore.Api.IntegrationTests;

/// <summary>Invitaciones de personal: enlace de un solo uso, rol y sucursales decididos por quien invita, límites del plan, aislamiento y respuestas seguras.</summary>
[Collection(ApiCollection3.Name)]
public partial class InvitationTests(ApiFixture api)
{
    private const string StrongPassword = "ClaveInvitada2026";

    private static string NewSlug() => $"inv-{Guid.NewGuid():N}"[..20];

    private static string NewEmail() => $"persona-{Guid.NewGuid():N}@invitados.test";

    [GeneratedRegex(@"/aceptar-invitacion\?token=([A-Za-z0-9_%\-\.]+)")]
    private static partial Regex TokenPattern();

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

    private async Task<long> CountAsync(string sql, params (string Name, object Value)[] args) => long.Parse((await ScalarAsync(sql, args))!);

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

    private async Task<string?> TokenFromEmailAsync(string email)
    {
        var body = await ScalarAsync(
            "SELECT text_body FROM messaging.emails WHERE lower(to_address) = lower(@e) AND template = 'invitation' ORDER BY created_at DESC LIMIT 1", ("e", email));
        var match = body is null ? null : TokenPattern().Match(body);
        return match is { Success: true } ? Uri.UnescapeDataString(match.Groups[1].Value) : null;
    }

    private static async Task<(HttpStatusCode Status, string? Code, JsonElement Body)> ReadAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        var json = string.IsNullOrEmpty(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
        var code = json.ValueKind == JsonValueKind.Object && json.TryGetProperty("code", out var c) ? c.GetString() : null;
        return (response.StatusCode, code, json);
    }

    private Task<HttpResponseMessage> InviteAsync(HttpClient client, string email, Guid roleId, params Guid[] branchIds) =>
        client.PostAsJsonAsync("/api/v1/invitations", new { email, roleId, branchIds });

    private Task<HttpResponseMessage> AcceptAsync(HttpClient client, string token, string password = StrongPassword, string fullName = "Persona Invitada") =>
        client.PostAsJsonAsync("/api/v1/public/invitations/accept", new { token, fullName, password });

    /// <summary>Un gimnasio con una sucursal y una invitación pendiente al rol «Entrenador».</summary>
    private async Task<(TestTenant Tenant, Guid BranchId, Guid InvitationId, string Email, string Token)> InvitedAsync()
    {
        var tenant = await TestApi.NewTenantAsync(api, NewSlug());
        var branch = await TestApi.CreateBranchAsync(tenant.Owner, "UNO");
        var role = await TestApi.RoleIdAsync(tenant.Owner, "Entrenador");
        var email = NewEmail();
        var created = await ReadAsync(await InviteAsync(tenant.Owner, email, role, branch));
        Assert.Equal(HttpStatusCode.Created, created.Status);
        var token = await TokenFromEmailAsync(email);
        Assert.NotNull(token);
        return (tenant, branch, created.Body.GetProperty("id").GetGuid(), email, token!);
    }

    // ---- invitar ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Inviting_stores_a_pending_invitation_and_queues_an_email_with_a_seven_day_single_use_link()
    {
        var (tenant, branch, id, email, token) = await InvitedAsync();

        var list = (await ReadAsync(await tenant.Owner.GetAsync("/api/v1/invitations"))).Body;
        var item = list.EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == id);

        Assert.Equal("Pending", item.GetProperty("status").GetString());
        Assert.Equal("Entrenador", item.GetProperty("roleName").GetString());
        Assert.Equal(branch, item.GetProperty("branchIds")[0].GetGuid());
        var days = double.Parse((await ScalarAsync("SELECT extract(epoch FROM (expires_at - created_at)) / 86400 FROM identity.invitations WHERE id = @i", ("i", id)))!);
        Assert.InRange(days, 6.99, 7.01);
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM identity.account_tokens WHERE token_hash = @t", ("t", token)));
        var body = await ScalarAsync("SELECT text_body FROM messaging.emails WHERE lower(to_address) = lower(@e) AND template = 'invitation'", ("e", email));
        Assert.Contains($"Gimnasio {tenant.Slug}", body);
    }

    [Fact]
    public async Task Only_people_who_can_create_users_can_invite()
    {
        var (tenant, branch, _, _, _) = await InvitedAsync();
        await TestApi.CreateUserAsync(tenant.Owner, $"coach@{tenant.Slug}.test", "Entrenador", branch);
        var coach = TestApi.Authenticated(api, await TestApi.LoginAsync(api, tenant.Slug, $"coach@{tenant.Slug}.test", TestApi.UserPassword));
        var role = await TestApi.RoleIdAsync(tenant.Owner, "Entrenador");
        using var anon = api.CreateClient();

        Assert.Equal(HttpStatusCode.Forbidden, (await InviteAsync(coach, NewEmail(), role, branch)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await InviteAsync(anon, NewEmail(), role, branch)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/v1/invitations")).StatusCode);
    }

    [Fact]
    public async Task Nobody_can_invite_someone_to_a_role_with_more_privileges_than_their_own()
    {
        var tenant = await TestApi.NewTenantAsync(api, NewSlug());
        var branch = await TestApi.CreateBranchAsync(tenant.Owner, "UNO");
        await TestApi.CreateUserAsync(tenant.Owner, $"gerente@{tenant.Slug}.test", "AdministradorSucursal", branch);
        var manager = TestApi.Authenticated(api, await TestApi.LoginAsync(api, tenant.Slug, $"gerente@{tenant.Slug}.test", TestApi.UserPassword));
        var ownerRole = await TestApi.RoleIdAsync(tenant.Owner, "Propietario");

        var result = await ReadAsync(await InviteAsync(manager, NewEmail(), ownerRole, branch));

        Assert.Equal(HttpStatusCode.Forbidden, result.Status);
        Assert.Equal("users.role_not_assignable", result.Code);
    }

    [Fact]
    public async Task A_role_from_another_gym_or_an_unknown_branch_is_rejected()
    {
        var a = await TestApi.NewTenantAsync(api, NewSlug());
        var b = await TestApi.NewTenantAsync(api, NewSlug());
        var foreignRole = await TestApi.RoleIdAsync(b.Owner, "Entrenador");
        var ownRole = await TestApi.RoleIdAsync(a.Owner, "Entrenador");

        var foreign = await ReadAsync(await InviteAsync(a.Owner, NewEmail(), foreignRole));
        var badBranch = await ReadAsync(await InviteAsync(a.Owner, NewEmail(), ownRole, Guid.NewGuid()));

        Assert.Equal("users.role_not_found", foreign.Code);
        Assert.Equal("users.branch_not_found", badBranch.Code);
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM identity.invitations WHERE tenant_id = @t", ("t", a.Id)));
    }

    [Fact]
    public async Task A_second_pending_invitation_for_the_same_email_or_an_existing_user_is_refused()
    {
        var (tenant, _, _, email, _) = await InvitedAsync();
        var role = await TestApi.RoleIdAsync(tenant.Owner, "Entrenador");

        var duplicate = await ReadAsync(await InviteAsync(tenant.Owner, email.ToUpperInvariant(), role));
        var existingUser = await ReadAsync(await InviteAsync(tenant.Owner, tenant.OwnerEmail, role));

        Assert.Equal("invitations.already_pending", duplicate.Code);
        Assert.Equal("users.email_taken", existingUser.Code);
    }

    [Fact]
    public async Task Without_a_mail_server_nothing_is_invited()
    {
        var tenant = await TestApi.NewTenantAsync(api, NewSlug());
        var role = await TestApi.RoleIdAsync(tenant.Owner, "Entrenador");
        using var noMail = api.CreateClientWith(("Email:Smtp:Host", ""));
        noMail.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", await TestApi.LoginAsync(api, tenant.Slug, tenant.OwnerEmail, tenant.OwnerPassword));
        var owner = noMail;

        var result = await ReadAsync(await InviteAsync(owner, NewEmail(), role));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, result.Status);
        Assert.Equal("invitations.email_unavailable", result.Code);
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM identity.invitations WHERE tenant_id = @t", ("t", tenant.Id)));
    }

    [Fact]
    public async Task There_is_a_daily_cap_on_invitations_per_gym()
    {
        var tenant = await TestApi.NewTenantAsync(api, NewSlug());
        var role = await TestApi.RoleIdAsync(tenant.Owner, "Entrenador");

        for (var i = 0; i < 20; i++)
        {
            Assert.Equal(HttpStatusCode.Created, (await InviteAsync(tenant.Owner, NewEmail(), role)).StatusCode);
        }

        var result = await ReadAsync(await InviteAsync(tenant.Owner, NewEmail(), role));
        Assert.Equal("invitations.daily_limit", result.Code);
    }

    // ---- ver y aceptar ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_link_can_be_previewed_without_spending_it()
    {
        var (tenant, _, _, email, token) = await InvitedAsync();
        using var anon = api.CreateClient();

        var first = await ReadAsync(await anon.PostAsJsonAsync("/api/v1/public/invitations/preview", new { token }));
        var second = await ReadAsync(await anon.PostAsJsonAsync("/api/v1/public/invitations/preview", new { token }));

        Assert.Equal(HttpStatusCode.OK, first.Status);
        Assert.Equal($"Gimnasio {tenant.Slug}", first.Body.GetProperty("gymName").GetString());
        Assert.Equal(email, first.Body.GetProperty("email").GetString());
        Assert.Equal("Entrenador", first.Body.GetProperty("roleName").GetString());
        Assert.Equal(HttpStatusCode.OK, second.Status);
    }

    [Fact]
    public async Task Accepting_creates_the_member_of_staff_with_the_role_and_branches_chosen_by_the_inviter()
    {
        var (tenant, branch, id, email, token) = await InvitedAsync();
        using var anon = api.CreateClient();

        var accepted = await ReadAsync(await AcceptAsync(anon, token));

        Assert.Equal(HttpStatusCode.OK, accepted.Status);
        Assert.Equal(tenant.Slug, accepted.Body.GetProperty("tenantSlug").GetString());
        var jwt = await TestApi.LoginAsync(api, tenant.Slug, email, StrongPassword);
        Assert.False(string.IsNullOrEmpty(jwt));
        var userId = await ScalarAsync("SELECT id::text FROM identity.app_users WHERE tenant_id = @t AND lower(email) = lower(@e)", ("t", tenant.Id), ("e", email));
        Assert.Equal("Entrenador", await ScalarAsync("SELECT r.name FROM identity.user_roles ur JOIN identity.app_roles r ON r.id = ur.role_id WHERE ur.user_id = @u::uuid", ("u", userId!)));
        Assert.Equal(branch.ToString(), await ScalarAsync("SELECT branch_id::text FROM identity.user_branches WHERE user_id = @u::uuid", ("u", userId!)));
        Assert.Equal("Accepted", await ScalarAsync("SELECT status FROM identity.invitations WHERE id = @i", ("i", id)));
        Assert.Equal(userId, await ScalarAsync("SELECT accepted_user_id::text FROM identity.invitations WHERE id = @i", ("i", id)));
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM audit.audit_logs WHERE tenant_id = @t AND action = 'Invitation.Accepted'", ("t", tenant.Id)));
    }

    [Fact]
    public async Task The_link_works_only_once_even_with_simultaneous_attempts()
    {
        var (tenant, _, _, email, token) = await InvitedAsync();
        using var anon = api.CreateClient();

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => AcceptAsync(anon, token)));
        var late = await ReadAsync(await AcceptAsync(anon, token));

        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal("invitations.token_invalid", late.Code);
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM identity.app_users WHERE tenant_id = @t AND lower(email) = lower(@e)", ("t", tenant.Id), ("e", email)));
    }

    [Theory]
    [InlineData("corta1A")]
    [InlineData("sinnumeroslargaAAA")]
    public async Task A_weak_password_is_refused_and_the_link_is_not_spent(string weak)
    {
        var (tenant, _, _, email, token) = await InvitedAsync();
        using var anon = api.CreateClient();

        var refused = await ReadAsync(await AcceptAsync(anon, token, weak));
        var retry = await AcceptAsync(anon, token);

        Assert.Equal("users.password_invalid", refused.Code);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.False(string.IsNullOrEmpty(await TestApi.LoginAsync(api, tenant.Slug, email, StrongPassword)));
    }

    [Fact]
    public async Task Unknown_expired_revoked_and_foreign_links_are_all_refused_the_same_way()
    {
        var expired = await InvitedAsync();
        await ExecAsync("UPDATE identity.invitations SET expires_at = now() - interval '1 minute' WHERE id = @i", ("i", expired.InvitationId));
        var revoked = await InvitedAsync();
        Assert.Equal(HttpStatusCode.OK, (await revoked.Tenant.Owner.PostAsync($"/api/v1/invitations/{revoked.InvitationId}/revoke", null)).StatusCode);
        var tokenExpired = await InvitedAsync();
        await ExecAsync("UPDATE identity.account_tokens SET expires_at = now() - interval '1 minute' WHERE token_hash = @h", ("h", AccountTokenService.Hash(tokenExpired.Token)));
        using var anon = api.CreateClient();

        var codes = new List<string?>();
        foreach (var token in new[] { "no-existe", expired.Token, revoked.Token, tokenExpired.Token })
        {
            codes.Add((await ReadAsync(await AcceptAsync(anon, token))).Code);
            codes.Add((await ReadAsync(await anon.PostAsJsonAsync("/api/v1/public/invitations/preview", new { token }))).Code);
        }

        Assert.All(codes, code => Assert.Equal("invitations.token_invalid", code));
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM identity.app_users WHERE lower(email) IN (lower(@a), lower(@b), lower(@c))", ("a", expired.Email), ("b", revoked.Email), ("c", tokenExpired.Email)));
    }

    [Fact]
    public async Task A_link_only_adds_the_person_to_the_gym_that_invited_them()
    {
        var (tenant, _, _, email, token) = await InvitedAsync();
        var other = await TestApi.NewTenantAsync(api, NewSlug());
        using var anon = api.CreateClient();

        await AcceptAsync(anon, token);

        Assert.Equal(1, await CountAsync("SELECT count(*) FROM identity.app_users WHERE tenant_id = @t AND lower(email) = lower(@e)", ("t", tenant.Id), ("e", email)));
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM identity.app_users WHERE tenant_id = @t AND lower(email) = lower(@e)", ("t", other.Id), ("e", email)));
        var cross = await anon.PostAsJsonAsync("/api/v1/auth/login", new { tenantSlug = other.Slug, email, password = StrongPassword });
        Assert.Equal(HttpStatusCode.Unauthorized, cross.StatusCode);
    }

    [Fact]
    public async Task An_invitation_from_someone_who_has_since_been_deactivated_is_not_honored()
    {
        var tenant = await TestApi.NewTenantAsync(api, NewSlug());
        var branch = await TestApi.CreateBranchAsync(tenant.Owner, "UNO");
        await TestApi.CreateUserAsync(tenant.Owner, $"gerente@{tenant.Slug}.test", "Propietario");
        var manager = TestApi.Authenticated(api, await TestApi.LoginAsync(api, tenant.Slug, $"gerente@{tenant.Slug}.test", TestApi.UserPassword));
        var email = NewEmail();
        var role = await TestApi.RoleIdAsync(tenant.Owner, "Entrenador");
        Assert.Equal(HttpStatusCode.Created, (await InviteAsync(manager, email, role, branch)).StatusCode);
        var token = (await TokenFromEmailAsync(email))!;
        await ExecAsync("UPDATE identity.app_users SET is_active = false WHERE tenant_id = @t AND lower(email) = lower(@e)", ("t", tenant.Id), ("e", $"gerente@{tenant.Slug}.test"));
        using var anon = api.CreateClient();

        var result = await ReadAsync(await AcceptAsync(anon, token));

        Assert.Equal("invitations.token_invalid", result.Code);
    }

    [Fact]
    public async Task A_suspended_gym_cannot_take_in_new_staff()
    {
        var (tenant, _, _, _, token) = await InvitedAsync();
        using var platform = await TestApi.PlatformClientAsync(api);
        await platform.PostAsync($"/api/v1/platform/tenants/{tenant.Id}/suspend", null);
        using var anon = api.CreateClient();

        var result = await ReadAsync(await AcceptAsync(anon, token));

        Assert.Equal("invitations.token_invalid", result.Code);
    }

    // ---- gestionar --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Revoking_a_pending_invitation_kills_its_link_and_cannot_be_repeated()
    {
        var (tenant, _, id, _, token) = await InvitedAsync();

        var revoked = await ReadAsync(await tenant.Owner.PostAsync($"/api/v1/invitations/{id}/revoke", null));
        var again = await ReadAsync(await tenant.Owner.PostAsync($"/api/v1/invitations/{id}/revoke", null));
        using var anon = api.CreateClient();

        Assert.Equal("Revoked", revoked.Body.GetProperty("status").GetString());
        Assert.Equal("invitations.not_pending", again.Code);
        Assert.Equal("invitations.token_invalid", (await ReadAsync(await AcceptAsync(anon, token))).Code);
    }

    [Fact]
    public async Task Resending_needs_a_pause_and_then_replaces_the_link()
    {
        var (tenant, _, id, email, oldToken) = await InvitedAsync();

        var tooSoon = await ReadAsync(await tenant.Owner.PostAsync($"/api/v1/invitations/{id}/resend", null));
        await ExecAsync("UPDATE identity.invitations SET last_sent_at = now() - interval '2 minutes' WHERE id = @i", ("i", id));
        await ExecAsync("UPDATE messaging.emails SET created_at = created_at - interval '1 minute' WHERE lower(to_address) = lower(@e)", ("e", email));
        var resent = await ReadAsync(await tenant.Owner.PostAsync($"/api/v1/invitations/{id}/resend", null));
        var newToken = await TokenFromEmailAsync(email);
        using var anon = api.CreateClient();

        Assert.Equal("invitations.resend_too_soon", tooSoon.Code);
        Assert.Equal(HttpStatusCode.OK, resent.Status);
        Assert.NotEqual(oldToken, newToken);
        Assert.Equal("invitations.token_invalid", (await ReadAsync(await AcceptAsync(anon, oldToken))).Code);
        Assert.Equal(HttpStatusCode.OK, (await AcceptAsync(anon, newToken!)).StatusCode);
    }

    [Fact]
    public async Task An_expired_invitation_shows_as_expired_and_can_be_replaced_by_a_new_one()
    {
        var (tenant, _, id, email, _) = await InvitedAsync();
        await ExecAsync("UPDATE identity.invitations SET expires_at = now() - interval '1 minute' WHERE id = @i", ("i", id));
        var role = await TestApi.RoleIdAsync(tenant.Owner, "Entrenador");

        var list = (await ReadAsync(await tenant.Owner.GetAsync("/api/v1/invitations"))).Body;
        var again = await InviteAsync(tenant.Owner, email, role);

        Assert.Equal("Expired", list.EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == id).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
    }

    [Fact]
    public async Task Each_gym_only_sees_and_manages_its_own_invitations()
    {
        var a = await InvitedAsync();
        var b = await TestApi.NewTenantAsync(api, NewSlug());

        var seenByB = (await ReadAsync(await b.Owner.GetAsync("/api/v1/invitations"))).Body;
        var revokeForeign = await b.Owner.PostAsync($"/api/v1/invitations/{a.InvitationId}/revoke", null);
        var resendForeign = await b.Owner.PostAsync($"/api/v1/invitations/{a.InvitationId}/resend", null);

        Assert.Equal(0, seenByB.GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, revokeForeign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, resendForeign.StatusCode);
        Assert.Equal("Pending", await ScalarAsync("SELECT status FROM identity.invitations WHERE id = @i", ("i", a.InvitationId)));
    }

    // ---- límite de usuarios del plan --------------------------------------------------------------------------------------------

    private async Task<TestTenant> TenantWithUserLimitAsync(long limit)
    {
        using var platform = await TestApi.PlatformClientAsync(api);
        var code = $"inv-{Guid.NewGuid():N}"[..18];
        var plan = await platform.PostAsJsonAsync("/api/v1/platform/plans", new
        {
            code, name = "Plan chico", isActive = true, currency = "USD", billingPeriod = "Monthly", price = 10m, trialDays = 0, features = new[] { "members", "branches" },
            limits = new object[]
            {
                new { code = "branches", value = 5L }, new { code = "internal_users", value = limit },
                new { code = "members", value = (long?)null }, new { code = "storage_mb", value = 500L }
            }
        });
        Assert.Equal(HttpStatusCode.Created, plan.StatusCode);
        var slug = NewSlug();
        var created = await platform.PostAsJsonAsync("/api/v1/platform/tenants", new
        {
            slug, name = $"Gimnasio {slug}", timeZone = "America/Guayaquil", locale = "es-EC", ownerEmail = $"owner@{slug}.test",
            ownerFullName = "Dueño", ownerPassword = TestApi.OwnerPassword, planCode = code, startTrial = false
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        var owner = TestApi.Authenticated(api, await TestApi.LoginAsync(api, slug, $"owner@{slug}.test", TestApi.OwnerPassword));
        return new TestTenant(id, slug, $"owner@{slug}.test", TestApi.OwnerPassword, owner);
    }

    [Fact]
    public async Task Pending_invitations_reserve_a_seat_so_the_plan_user_limit_cannot_be_exceeded()
    {
        var tenant = await TenantWithUserLimitAsync(2); // el propietario ya ocupa una plaza
        var role = await TestApi.RoleIdAsync(tenant.Owner, "Entrenador");

        var first = await InviteAsync(tenant.Owner, NewEmail(), role);
        var second = await ReadAsync(await InviteAsync(tenant.Owner, NewEmail(), role));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.Status);
        Assert.Equal("subscription.limit_reached", second.Code);
    }

    [Fact]
    public async Task Accepting_rechecks_the_limit_and_does_not_spend_the_link_when_there_is_no_seat()
    {
        var tenant = await TenantWithUserLimitAsync(2);
        var role = await TestApi.RoleIdAsync(tenant.Owner, "Entrenador");
        var email = NewEmail();
        Assert.Equal(HttpStatusCode.Created, (await InviteAsync(tenant.Owner, email, role)).StatusCode);
        var token = (await TokenFromEmailAsync(email))!;
        await TestApi.CreateUserAsync(tenant.Owner, $"otro@{tenant.Slug}.test", "Entrenador"); // alguien ocupa la plaza reservada
        using var anon = api.CreateClient();

        var result = await ReadAsync(await AcceptAsync(anon, token));

        Assert.Equal("subscription.limit_reached", result.Code);
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM identity.app_users WHERE tenant_id = @t AND lower(email) = lower(@e)", ("t", tenant.Id), ("e", email)));
        Assert.Equal("Pending", await ScalarAsync("SELECT status FROM identity.invitations WHERE tenant_id = @t AND lower(email) = lower(@e)", ("t", tenant.Id), ("e", email)));
    }

    // ---- aislamiento en la base --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_database_itself_isolates_invitations_by_gym()
    {
        var a = await InvitedAsync();
        var b = await InvitedAsync();

        await using var conn = new NpgsqlConnection(api.AppConnection);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using (var set = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, true)", conn, tx))
        {
            set.Parameters.AddWithValue("t", a.Tenant.Id.ToString());
            await set.ExecuteNonQueryAsync();
        }

        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM identity.invitations WHERE id = @other", conn, tx);
        cmd.Parameters.AddWithValue("other", b.InvitationId);
        Assert.Equal(0L, await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Invitations_leave_an_audit_trail_without_the_token_or_the_password()
    {
        var (tenant, _, id, _, token) = await InvitedAsync();
        await tenant.Owner.PostAsync($"/api/v1/invitations/{id}/revoke", null);

        var actions = await ScalarAsync(
            "SELECT string_agg(DISTINCT action, ',' ORDER BY action) FROM audit.audit_logs WHERE tenant_id = @t AND action LIKE 'Invitation.%'", ("t", tenant.Id));
        var leaked = await CountAsync("SELECT count(*) FROM audit.audit_logs a WHERE a.tenant_id = @t AND (a::text LIKE '%' || @k || '%' OR a::text LIKE '%' || @p || '%')",
            ("t", tenant.Id), ("k", token), ("p", StrongPassword));

        Assert.Equal("Invitation.Created,Invitation.Revoked", actions);
        Assert.Equal(0, leaked);
    }
}
