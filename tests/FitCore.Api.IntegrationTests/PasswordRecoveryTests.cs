using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FitCore.Api.IntegrationTests.Infrastructure;
using FitCore.Platform.Application.AccountTokens;
using Npgsql;

namespace FitCore.Api.IntegrationTests;

/// <summary>Recuperación de contraseña por correo: respuestas sin enumeración, enlaces de un solo uso, límites y cierre de sesiones.</summary>
[Collection(ApiCollection3.Name)]
public partial class PasswordRecoveryTests(ApiFixture api)
{
    private const string NewPassword = "OtraClaveSegura2027";

    private static string NewSlug() => $"rec-{Guid.NewGuid():N}"[..20];

    private Task<HttpResponseMessage> ForgotAsync(HttpClient client, string slug, string email) =>
        client.PostAsJsonAsync("/api/v1/auth/forgot-password", new { tenantSlug = slug, email });

    private Task<HttpResponseMessage> ResetAsync(HttpClient client, string token, string password) =>
        client.PostAsJsonAsync("/api/v1/auth/reset-password", new { token, newPassword = password });

    private Task<HttpResponseMessage> LoginRawAsync(HttpClient client, string slug, string email, string password) =>
        client.PostAsJsonAsync("/api/v1/auth/login", new { tenantSlug = slug, email, password });

    [GeneratedRegex(@"/restablecer-clave\?token=([A-Za-z0-9_%\-\.]+)")]
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
            "SELECT text_body FROM messaging.emails WHERE lower(to_address) = lower(@e) AND template = 'password_reset' ORDER BY created_at DESC LIMIT 1", ("e", email));
        var match = body is null ? null : TokenPattern().Match(body);
        return match is { Success: true } ? Uri.UnescapeDataString(match.Groups[1].Value) : null;
    }

    /// <summary>Quita el intervalo mínimo entre solicitudes para poder pedir otro enlace sin esperar un minuto.</summary>
    private Task SkipWaitAsync(string email) => ExecAsync(
        "UPDATE identity.account_tokens SET created_at = created_at - interval '5 minutes' WHERE purpose = 'password_reset' AND subject_id IN (SELECT id FROM identity.app_users WHERE lower(email) = lower(@e))",
        ("e", email));

    private async Task<(TestTenant Tenant, string Token)> RequestedAsync()
    {
        var tenant = await TestApi.NewTenantAsync(api, NewSlug());
        using var client = api.CreateClient();
        Assert.Equal(HttpStatusCode.Accepted, (await ForgotAsync(client, tenant.Slug, tenant.OwnerEmail)).StatusCode);
        var token = await TokenFromEmailAsync(tenant.OwnerEmail);
        Assert.NotNull(token);
        return (tenant, token!);
    }

    private static async Task<(HttpStatusCode Status, string? Code)> ReadAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrEmpty(text))
        {
            return (response.StatusCode, null);
        }

        var json = JsonDocument.Parse(text).RootElement;
        return (response.StatusCode, json.ValueKind == JsonValueKind.Object && json.TryGetProperty("code", out var c) ? c.GetString() : null);
    }

    // ---- pedir el enlace -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Asking_for_a_link_queues_an_email_with_a_one_hour_single_use_link()
    {
        var (tenant, token) = await RequestedAsync();

        Assert.Equal(1, await CountAsync("SELECT count(*) FROM messaging.emails WHERE lower(to_address) = lower(@e) AND template = 'password_reset'", ("e", tenant.OwnerEmail)));
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM identity.account_tokens WHERE token_hash = @t", ("t", token)));
        var minutes = double.Parse((await ScalarAsync(
            "SELECT extract(epoch FROM (expires_at - created_at)) / 60 FROM identity.account_tokens WHERE token_hash = @h", ("h", AccountTokenService.Hash(token))))!);
        Assert.InRange(minutes, 59, 61);
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM audit.audit_logs WHERE tenant_id = @t AND action = 'Auth.PasswordResetRequested'", ("t", tenant.Id)));
    }

    [Fact]
    public async Task The_answer_is_identical_for_real_unknown_and_foreign_accounts()
    {
        var tenant = await TestApi.NewTenantAsync(api, NewSlug());
        var other = await TestApi.NewTenantAsync(api, NewSlug());
        using var client = api.CreateClient();

        var real = await ForgotAsync(client, tenant.Slug, tenant.OwnerEmail);
        var unknownUser = await ForgotAsync(client, tenant.Slug, $"nadie-{Guid.NewGuid():N}@x.test");
        var unknownGym = await ForgotAsync(client, "gimnasio-que-no-existe", tenant.OwnerEmail);
        var wrongGym = await ForgotAsync(client, other.Slug, tenant.OwnerEmail); // el correo existe, pero en otro gimnasio

        var bodies = new List<string>();
        foreach (var r in new[] { real, unknownUser, unknownGym, wrongGym })
        {
            Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
            bodies.Add(await r.Content.ReadAsStringAsync());
        }

        Assert.Single(bodies.Distinct());
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM messaging.emails WHERE template = 'password_reset' AND lower(to_address) = lower(@e)", ("e", tenant.OwnerEmail)));
    }

    [Fact]
    public async Task Suspended_gyms_inactive_users_and_the_platform_admin_get_no_email()
    {
        var suspended = await TestApi.NewTenantAsync(api, NewSlug());
        using var platform = await TestApi.PlatformClientAsync(api);
        Assert.True((await platform.PostAsync($"/api/v1/platform/tenants/{suspended.Id}/suspend", null)).IsSuccessStatusCode);
        var inactive = await TestApi.NewTenantAsync(api, NewSlug());
        await ExecAsync("UPDATE identity.app_users SET is_active = false WHERE tenant_id = @t", ("t", inactive.Id));
        using var client = api.CreateClient();

        var results = new[]
        {
            await ForgotAsync(client, suspended.Slug, suspended.OwnerEmail),
            await ForgotAsync(client, inactive.Slug, inactive.OwnerEmail),
            await ForgotAsync(client, "platform", ApiFixture.PlatformEmail)
        };

        Assert.All(results, r => Assert.Equal(HttpStatusCode.Accepted, r.StatusCode));
        Assert.Equal(0, await CountAsync(
            "SELECT count(*) FROM messaging.emails WHERE template = 'password_reset' AND lower(to_address) IN (lower(@a), lower(@b), lower(@c))",
            ("a", suspended.OwnerEmail), ("b", inactive.OwnerEmail), ("c", ApiFixture.PlatformEmail)));
    }

    [Fact]
    public async Task Asking_again_too_soon_or_too_often_sends_nothing_more()
    {
        var tenant = await TestApi.NewTenantAsync(api, NewSlug());
        using var client = api.CreateClient();

        await ForgotAsync(client, tenant.Slug, tenant.OwnerEmail);
        await ForgotAsync(client, tenant.Slug, tenant.OwnerEmail); // dentro del minuto
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM messaging.emails WHERE template = 'password_reset' AND lower(to_address) = lower(@e)", ("e", tenant.OwnerEmail)));

        for (var i = 0; i < 4; i++)
        {
            await SkipWaitAsync(tenant.OwnerEmail);
            await ForgotAsync(client, tenant.Slug, tenant.OwnerEmail);
        }

        Assert.Equal(3, await CountAsync("SELECT count(*) FROM messaging.emails WHERE template = 'password_reset' AND lower(to_address) = lower(@e)", ("e", tenant.OwnerEmail)));
    }

    [Fact]
    public async Task A_new_link_invalidates_the_previous_one()
    {
        var (tenant, first) = await RequestedAsync();
        await SkipWaitAsync(tenant.OwnerEmail);
        await ExecAsync("UPDATE messaging.emails SET created_at = created_at - interval '1 minute' WHERE lower(to_address) = lower(@e)", ("e", tenant.OwnerEmail));
        using var client = api.CreateClient();
        await ForgotAsync(client, tenant.Slug, tenant.OwnerEmail);
        var second = await TokenFromEmailAsync(tenant.OwnerEmail);

        Assert.NotEqual(first, second);
        Assert.Equal("auth.reset_token_invalid", (await ReadAsync(await ResetAsync(client, first, NewPassword))).Code);
        Assert.Equal(HttpStatusCode.NoContent, (await ResetAsync(client, second!, NewPassword)).StatusCode);
    }

    [Fact]
    public async Task Without_a_mail_server_it_says_so_and_stores_nothing()
    {
        var tenant = await TestApi.NewTenantAsync(api, NewSlug());
        using var noMail = api.CreateClientWith(("Email:Smtp:Host", ""));

        var result = await ReadAsync(await ForgotAsync(noMail, tenant.Slug, tenant.OwnerEmail));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, result.Status);
        Assert.Equal("auth.recovery_email_unavailable", result.Code);
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM messaging.emails WHERE lower(to_address) = lower(@e)", ("e", tenant.OwnerEmail)));
    }

    // ---- usar el enlace --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_link_sets_a_new_password_the_old_one_stops_working_and_the_owner_is_told()
    {
        var (tenant, token) = await RequestedAsync();
        using var client = api.CreateClient();

        var reset = await ResetAsync(client, token, NewPassword);

        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LoginRawAsync(client, tenant.Slug, tenant.OwnerEmail, NewPassword)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginRawAsync(client, tenant.Slug, tenant.OwnerEmail, tenant.OwnerPassword)).StatusCode);
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM messaging.emails WHERE template = 'password_changed' AND lower(to_address) = lower(@e)", ("e", tenant.OwnerEmail)));
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM audit.audit_logs WHERE tenant_id = @t AND action = 'Auth.PasswordResetCompleted'", ("t", tenant.Id)));
    }

    [Fact]
    public async Task The_link_works_only_once()
    {
        var (_, token) = await RequestedAsync();
        using var client = api.CreateClient();

        var first = await ResetAsync(client, token, NewPassword);
        var second = await ReadAsync(await ResetAsync(client, token, "TerceraClaveSegura2028"));

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal("auth.reset_token_invalid", second.Code);
    }

    [Fact]
    public async Task Simultaneous_uses_of_the_link_let_exactly_one_win()
    {
        var (_, token) = await RequestedAsync();
        using var client = api.CreateClient();

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => ResetAsync(client, token, NewPassword)));

        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.NoContent));
    }

    [Fact]
    public async Task Unknown_or_expired_links_are_rejected_with_the_generic_error()
    {
        var (tenant, token) = await RequestedAsync();
        await ExecAsync("UPDATE identity.account_tokens SET expires_at = now() - interval '1 minute' WHERE token_hash = @h", ("h", AccountTokenService.Hash(token)));
        using var client = api.CreateClient();

        var expired = await ReadAsync(await ResetAsync(client, token, NewPassword));
        var unknown = await ReadAsync(await ResetAsync(client, "token-que-no-existe", NewPassword));

        Assert.Equal("auth.reset_token_invalid", expired.Code);
        Assert.Equal("auth.reset_token_invalid", unknown.Code);
        Assert.Equal(HttpStatusCode.OK, (await LoginRawAsync(client, tenant.Slug, tenant.OwnerEmail, tenant.OwnerPassword)).StatusCode); // la clave no cambió
    }

    [Theory]
    [InlineData("corta1A")]
    [InlineData("sinnumeroslargaAAA")]
    public async Task A_weak_new_password_is_refused_and_the_link_is_not_spent(string weak)
    {
        var (tenant, token) = await RequestedAsync();
        using var client = api.CreateClient();

        var refused = await ReadAsync(await ResetAsync(client, token, weak));
        var retry = await ResetAsync(client, token, NewPassword);

        Assert.Equal("users.password_invalid", refused.Code);
        Assert.Equal(HttpStatusCode.NoContent, retry.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LoginRawAsync(client, tenant.Slug, tenant.OwnerEmail, NewPassword)).StatusCode);
    }

    [Fact]
    public async Task Resetting_closes_every_open_session()
    {
        var (tenant, token) = await RequestedAsync();
        using var client = api.CreateClient();
        var login = await LoginRawAsync(client, tenant.Slug, tenant.OwnerEmail, tenant.OwnerPassword);
        var refresh = JsonDocument.Parse(await login.Content.ReadAsStringAsync()).RootElement.GetProperty("refreshToken").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = refresh })).StatusCode);

        await ResetAsync(client, token, NewPassword);

        var after = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = refresh });
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Fact]
    public async Task Resetting_lifts_an_account_lockout_and_clears_the_forced_change_flag()
    {
        var (tenant, token) = await RequestedAsync();
        using var client = api.CreateClient();
        for (var i = 0; i < 6; i++)
        {
            await LoginRawAsync(client, tenant.Slug, tenant.OwnerEmail, "incorrecta-" + i);
        }

        Assert.NotEqual(HttpStatusCode.OK, (await LoginRawAsync(client, tenant.Slug, tenant.OwnerEmail, tenant.OwnerPassword)).StatusCode); // bloqueada
        await ExecAsync("UPDATE identity.app_users SET must_change_password = true WHERE tenant_id = @t", ("t", tenant.Id));

        await ResetAsync(client, token, NewPassword);

        Assert.Equal(HttpStatusCode.OK, (await LoginRawAsync(client, tenant.Slug, tenant.OwnerEmail, NewPassword)).StatusCode);
        Assert.Equal("false", await ScalarAsync("SELECT must_change_password::text FROM identity.app_users WHERE tenant_id = @t", ("t", tenant.Id)));
    }

    [Fact]
    public async Task A_link_only_changes_the_account_it_was_issued_for_even_if_the_email_exists_in_another_gym()
    {
        var (tenant, token) = await RequestedAsync();
        var otherSlug = NewSlug();
        using var platform = await TestApi.PlatformClientAsync(api);
        var created = await platform.PostAsJsonAsync("/api/v1/platform/tenants", new
        {
            slug = otherSlug, name = "Otro gimnasio", timeZone = "America/Guayaquil", locale = "es-EC", ownerEmail = tenant.OwnerEmail,
            ownerFullName = "Otro Dueño", ownerPassword = TestApi.OwnerPassword
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var client = api.CreateClient();

        await ResetAsync(client, token, NewPassword);

        Assert.Equal(HttpStatusCode.OK, (await LoginRawAsync(client, otherSlug, tenant.OwnerEmail, TestApi.OwnerPassword)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginRawAsync(client, otherSlug, tenant.OwnerEmail, NewPassword)).StatusCode);
    }

    [Fact]
    public async Task A_link_for_an_account_deactivated_in_the_meantime_is_refused()
    {
        var (tenant, token) = await RequestedAsync();
        await ExecAsync("UPDATE identity.app_users SET is_active = false WHERE tenant_id = @t", ("t", tenant.Id));
        using var client = api.CreateClient();

        var result = await ReadAsync(await ResetAsync(client, token, NewPassword));

        Assert.Equal("auth.reset_token_invalid", result.Code);
    }

    [Fact]
    public async Task A_link_from_another_purpose_cannot_reset_a_password()
    {
        var tenant = await TestApi.NewTenantAsync(api, NewSlug());
        await using var scope = api.Services.CreateAsyncScope();
        var tokens = scope.ServiceProvider.GetRequiredService<AccountTokenService>();
        var verifyToken = await tokens.IssueAsync(AccountTokenPurposes.VerifyEmail, Guid.NewGuid(), tenant.Id, TimeSpan.FromHours(1), default);
        using var client = api.CreateClient();

        var result = await ReadAsync(await ResetAsync(client, verifyToken, NewPassword));

        Assert.Equal("auth.reset_token_invalid", result.Code);
    }

    [Fact]
    public async Task Recovery_endpoints_are_rate_limited_per_ip()
    {
        using var limited = api.CreateClientWith(("RateLimiting:RecoveryPermitPerMinute", "2"));

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            statuses.Add((await ForgotAsync(limited, "cualquiera", $"x{i}@x.test")).StatusCode);
        }

        Assert.Equal([HttpStatusCode.Accepted, HttpStatusCode.Accepted, HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests], statuses);
    }

    [Fact]
    public async Task Neither_the_password_nor_the_link_token_appear_in_the_audit_trail()
    {
        var (tenant, token) = await RequestedAsync();
        using var client = api.CreateClient();
        await ResetAsync(client, token, NewPassword);

        var leaked = await CountAsync(
            "SELECT count(*) FROM audit.audit_logs a WHERE a.tenant_id = @t AND (a::text LIKE '%' || @p || '%' OR a::text LIKE '%' || @k || '%')",
            ("t", tenant.Id), ("p", NewPassword), ("k", token));

        Assert.Equal(0, leaked);
    }
}
