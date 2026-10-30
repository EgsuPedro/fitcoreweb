using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FitCore.Api.IntegrationTests.Infrastructure;
using FitCore.Application.Core;
using FitCore.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FitCore.Api.IntegrationTests;

/// <summary>Registro público de gimnasios: confirmación por correo, creación transaccional, prueba única, seguridad y respuestas sin enumeración.</summary>
[Collection(ApiCollection2.Name)]
public partial class RegistrationTests(ApiFixture api)
{
    private const string Terms = "2026-10-30-borrador";
    private const string StrongPassword = "ClaveSegura2026";
    private static readonly string[] SomeFeatures = ["members", "branches", "classes"];

    // ---- utilidades ------------------------------------------------------------------------------------------------------------

    private async Task<string> PlanAsync(decimal price = 25m, int trialDays = 14, bool publish = true, bool active = true)
    {
        using var platform = await TestApi.PlatformClientAsync(api);
        var code = $"reg-{Guid.NewGuid():N}"[..20];
        var response = await platform.PostAsJsonAsync("/api/v1/platform/plans", new
        {
            code, name = $"Plan {code}", isActive = active, currency = "USD", billingPeriod = "Monthly", price, trialDays, features = SomeFeatures,
            limits = new object[]
            {
                new { code = "branches", value = 2L }, new { code = "internal_users", value = 5L },
                new { code = "members", value = (long?)null }, new { code = "storage_mb", value = 500L }
            }
        });
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        if (publish)
        {
            var id = body.GetProperty("id").GetGuid();
            var published = await platform.PatchAsJsonAsync($"/api/v1/platform/plans/{id}/visibility", new { isPublic = true });
            Assert.Equal(HttpStatusCode.OK, published.StatusCode);
        }

        return code;
    }

    private static Dictionary<string, object?> Body(string email, string slug, string plan, Dictionary<string, object?>? extra = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["ownerFullName"] = "Ana Pérez", ["email"] = email, ["password"] = StrongPassword, ["gymName"] = "Gimnasio Prueba", ["slug"] = slug,
            ["planCode"] = plan, ["termsVersion"] = Terms, ["privacyVersion"] = Terms, ["locale"] = "es"
        };
        foreach (var (key, value) in extra ?? [])
        {
            body[key] = value;
        }

        return body;
    }

    private static string NewSlug() => $"reg-{Guid.NewGuid():N}"[..20];

    private static string NewEmail() => $"dueno-{Guid.NewGuid():N}@registro.test";

    private Task<HttpResponseMessage> RegisterAsync(HttpClient client, string email, string slug, string plan, Dictionary<string, object?>? extra = null) =>
        client.PostAsJsonAsync("/api/v1/public/registrations", Body(email, slug, plan, extra));

    private async Task<string?> TokenFromEmailAsync(string email)
    {
        await using var conn = new NpgsqlConnection(api.AdminConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT text_body FROM messaging.emails WHERE lower(to_address) = lower(@to) AND template = 'verify_email' ORDER BY created_at DESC LIMIT 1", conn);
        cmd.Parameters.AddWithValue("to", email);
        var body = await cmd.ExecuteScalarAsync() as string;
        var match = body is null ? null : TokenPattern().Match(body);
        return match is { Success: true } ? Uri.UnescapeDataString(match.Groups[1].Value) : null;
    }

    [GeneratedRegex(@"/verificar-correo\?token=([A-Za-z0-9_%\-\.]+)")]
    private static partial Regex TokenPattern();

    private async Task<long> CountAsync(string sql, params (string Name, object Value)[] args)
    {
        await using var conn = new NpgsqlConnection(api.AdminConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in args)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        return (long)(await cmd.ExecuteScalarAsync())!;
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

    private static async Task<(HttpStatusCode Status, string? Code, JsonElement Body)> ReadAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        var json = string.IsNullOrEmpty(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
        var code = json.ValueKind == JsonValueKind.Object && json.TryGetProperty("code", out var c) ? c.GetString() : null;
        return (response.StatusCode, code, json);
    }

    private Task<HttpResponseMessage> VerifyAsync(HttpClient client, string token) =>
        client.PostAsJsonAsync("/api/v1/public/registrations/verify-email", new { token });

    private async Task<(string Email, string Slug, string Plan, string Token)> RegisteredAsync(decimal price = 25m, int trialDays = 14)
    {
        using var client = api.CreateClient();
        var plan = await PlanAsync(price, trialDays);
        var email = NewEmail();
        var slug = NewSlug();
        Assert.Equal(HttpStatusCode.Accepted, (await RegisterAsync(client, email, slug, plan)).StatusCode);
        var token = await TokenFromEmailAsync(email);
        Assert.NotNull(token);
        return (email, slug, plan, token!);
    }

    // ---- registro ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Registering_stores_a_pending_request_and_queues_the_email_but_creates_no_gym_yet()
    {
        using var client = api.CreateClient();
        var plan = await PlanAsync();
        var email = NewEmail();
        var slug = NewSlug();

        var response = await RegisterAsync(client, email, slug, plan);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM organizations.pending_registrations WHERE slug = @s AND status = 'Pending'", ("s", slug)));
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM organizations.tenants WHERE slug = @s", ("s", slug)));
        Assert.NotNull(await TokenFromEmailAsync(email));
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { tenantSlug = slug, email, password = StrongPassword });
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    [Fact]
    public async Task The_password_is_never_stored_in_plain_text_and_the_token_only_as_a_hash()
    {
        var (email, slug, _, token) = await RegisteredAsync();

        var hash = await ScalarAsync("SELECT password_hash FROM organizations.pending_registrations WHERE slug = @s", ("s", slug));

        Assert.NotNull(hash);
        Assert.DoesNotContain(StrongPassword, hash);
        Assert.StartsWith("AQAAAA", hash); // formato del hasher de Identity
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM identity.account_tokens WHERE token_hash = @t", ("t", token)));
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM organizations.pending_registrations p WHERE p::text LIKE '%' || @p || '%'", ("p", StrongPassword)));
        Assert.NotNull(email);
    }

    [Fact]
    public async Task The_response_is_identical_whether_or_not_the_email_already_has_an_account()
    {
        using var client = api.CreateClient();
        var plan = await PlanAsync();
        var existing = await TestApi.NewTenantAsync(api, NewSlug());
        var fresh = await RegisterAsync(client, NewEmail(), NewSlug(), plan);
        var withExistingOwner = await RegisterAsync(client, existing.OwnerEmail, NewSlug(), plan);
        var again = await RegisterAsync(client, existing.OwnerEmail, NewSlug(), plan);

        var bodies = new[] { fresh, withExistingOwner, again };
        foreach (var r in bodies)
        {
            Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        }

        Assert.Single(await Task.WhenAll(bodies.Select(async r => await r.Content.ReadAsStringAsync())).ContinueWith(t => t.Result.Distinct().ToList()));
    }

    [Fact]
    public async Task The_price_sent_by_the_browser_is_rejected()
    {
        using var client = api.CreateClient();
        var plan = await PlanAsync(price: 99m, trialDays: 14);

        foreach (var field in new[] { "price", "amount" })
        {
            var response = await RegisterAsync(client, NewEmail(), NewSlug(), plan, new() { [field] = 0m });
            Assert.False(response.IsSuccessStatusCode, $"Aceptó «{field}»");
        }

        var currency = await RegisterAsync(client, NewEmail(), NewSlug(), plan, new() { ["currency"] = "USD" });
        Assert.False(currency.IsSuccessStatusCode);
    }

    [Fact]
    public async Task Plans_that_are_hidden_inactive_unknown_or_paid_without_trial_cannot_be_chosen()
    {
        using var client = api.CreateClient();
        var hidden = await PlanAsync(publish: false);
        var paidNoTrial = await PlanAsync(price: 40m, trialDays: 0);

        var unknown = await ReadAsync(await RegisterAsync(client, NewEmail(), NewSlug(), "no-existe"));
        var hiddenResult = await ReadAsync(await RegisterAsync(client, NewEmail(), NewSlug(), hidden));
        var legacy = await ReadAsync(await RegisterAsync(client, NewEmail(), NewSlug(), "legacy"));
        var sales = await ReadAsync(await RegisterAsync(client, NewEmail(), NewSlug(), paidNoTrial));

        Assert.Equal("registration.plan_unavailable", unknown.Code);
        Assert.Equal("registration.plan_unavailable", hiddenResult.Code);
        Assert.Equal("registration.plan_unavailable", legacy.Code);
        Assert.Equal("registration.plan_requires_sales", sales.Code);
    }

    [Theory]
    [InlineData("corta1A", "users.password_invalid")]
    [InlineData("sinnumeroslargaAAA", "users.password_invalid")]
    [InlineData("sinmayusculas12345", "users.password_invalid")]
    public async Task A_weak_password_is_rejected(string password, string expectedCode)
    {
        using var client = api.CreateClient();
        var plan = await PlanAsync();

        var result = await ReadAsync(await RegisterAsync(client, NewEmail(), NewSlug(), plan, new() { ["password"] = password }));

        Assert.Equal(expectedCode, result.Code);
    }

    [Theory]
    [InlineData("no-es-correo", "slug-valido-1")]
    [InlineData("a@b.test", "Slug Con Espacios")]
    [InlineData("a@b.test", "platform")]
    [InlineData("a@b.test", "ab")]
    [InlineData("a@b.test", "-empieza-mal")]
    public async Task Invalid_email_or_identifier_is_rejected_and_nothing_is_stored(string email, string slug)
    {
        using var client = api.CreateClient();
        var plan = await PlanAsync();
        var before = await CountAsync("SELECT count(*) FROM organizations.pending_registrations");

        var response = await RegisterAsync(client, email, slug, plan);

        Assert.False(response.IsSuccessStatusCode);
        Assert.Equal(before, await CountAsync("SELECT count(*) FROM organizations.pending_registrations"));
    }

    [Fact]
    public async Task An_identifier_already_used_by_a_gym_or_by_another_pending_registration_is_rejected()
    {
        using var client = api.CreateClient();
        var plan = await PlanAsync();
        var existing = await TestApi.NewTenantAsync(api, NewSlug());
        var pendingSlug = NewSlug();
        await RegisterAsync(client, NewEmail(), pendingSlug, plan);

        var byTenant = await ReadAsync(await RegisterAsync(client, NewEmail(), existing.Slug, plan));
        var byPending = await ReadAsync(await RegisterAsync(client, NewEmail(), pendingSlug, plan));

        Assert.Equal("registration.slug_taken", byTenant.Code);
        Assert.Equal("registration.slug_taken", byPending.Code);
    }

    [Fact]
    public async Task Outdated_legal_versions_are_rejected()
    {
        using var client = api.CreateClient();
        var plan = await PlanAsync();

        var terms = await ReadAsync(await RegisterAsync(client, NewEmail(), NewSlug(), plan, new() { ["termsVersion"] = "vieja" }));
        var privacy = await ReadAsync(await RegisterAsync(client, NewEmail(), NewSlug(), plan, new() { ["privacyVersion"] = "vieja" }));

        Assert.Equal("registration.legal_version_mismatch", terms.Code);
        Assert.Equal("registration.legal_version_mismatch", privacy.Code);
    }

    [Fact]
    public async Task Without_a_mail_server_registration_answers_503_and_stores_nothing()
    {
        using var noMail = api.CreateClientWith(("Email:Smtp:Host", ""));
        var plan = await PlanAsync();
        var slug = NewSlug();
        var email = NewEmail();

        var response = await RegisterAsync(noMail, email, slug, plan);
        var resend = await noMail.PostAsJsonAsync("/api/v1/public/registrations/resend-verification", new { email });

        var result = await ReadAsync(response);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, result.Status);
        Assert.Equal("registration.email_unavailable", result.Code);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resend.StatusCode);
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM organizations.pending_registrations WHERE slug = @s", ("s", slug)));
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM messaging.emails WHERE lower(to_address) = lower(@e)", ("e", email)));
    }

    [Fact]
    public async Task A_new_registration_with_the_same_email_replaces_the_previous_one()
    {
        using var client = api.CreateClient();
        var plan = await PlanAsync();
        var email = NewEmail();
        var slug = NewSlug();
        await RegisterAsync(client, email, slug, plan);
        var first = await TokenFromEmailAsync(email);
        await ExecAsync("UPDATE messaging.emails SET created_at = created_at - interval '1 second' WHERE lower(to_address) = lower(@e)", ("e", email));

        await RegisterAsync(client, email, slug, plan);
        var second = await TokenFromEmailAsync(email);

        Assert.NotEqual(first, second);
        Assert.Equal("registration.token_invalid", (await ReadAsync(await VerifyAsync(client, first!))).Code);
        Assert.Equal(HttpStatusCode.OK, (await VerifyAsync(client, second!)).StatusCode);
    }

    [Fact]
    public async Task Too_many_registrations_for_one_email_in_an_hour_stop_silently()
    {
        using var client = api.CreateClient();
        var plan = await PlanAsync();
        var email = NewEmail();

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.Accepted, (await RegisterAsync(client, email, NewSlug(), plan)).StatusCode);
        }

        Assert.Equal(3, await CountAsync("SELECT count(*) FROM organizations.pending_registrations WHERE email_normalized = @e", ("e", email)));
        Assert.Equal(3, await CountAsync("SELECT count(*) FROM messaging.emails WHERE lower(to_address) = lower(@e)", ("e", email)));
    }

    [Fact]
    public async Task Registration_endpoints_are_rate_limited_per_ip()
    {
        using var limited = api.CreateClientWith(("RateLimiting:RegisterPermitPerMinute", "2"));
        var plan = await PlanAsync();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            statuses.Add((await RegisterAsync(limited, NewEmail(), NewSlug(), plan)).StatusCode);
        }

        Assert.Equal([HttpStatusCode.Accepted, HttpStatusCode.Accepted, HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests], statuses);
    }

    // ---- confirmación -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Confirming_the_email_creates_the_gym_the_owner_and_the_trial_subscription()
    {
        var (email, slug, plan, token) = await RegisteredAsync(price: 25m, trialDays: 14);
        using var client = api.CreateClient();

        var response = await VerifyAsync(client, token);

        var result = await ReadAsync(response);
        Assert.Equal(HttpStatusCode.OK, result.Status);
        Assert.Equal(slug, result.Body.GetProperty("tenantSlug").GetString());
        Assert.Equal("PendingSetup", result.Body.GetProperty("setupStatus").GetString());
        Assert.Equal(14, result.Body.GetProperty("trialDays").GetInt32());

        var tenantId = Guid.Parse((await ScalarAsync("SELECT id::text FROM organizations.tenants WHERE slug = @s", ("s", slug)))!);
        Assert.Equal("PendingSetup", await ScalarAsync("SELECT setup_status FROM organizations.tenants WHERE id = @t", ("t", tenantId)));
        Assert.Equal("Active", await ScalarAsync("SELECT status FROM organizations.tenants WHERE id = @t", ("t", tenantId)));
        Assert.Equal("Trialing", await ScalarAsync("SELECT status FROM subscriptions.subscriptions WHERE tenant_id = @t", ("t", tenantId)));
        Assert.Equal(plan, await ScalarAsync("SELECT plan_code FROM subscriptions.subscriptions WHERE tenant_id = @t", ("t", tenantId)));
        Assert.Equal("Verified", await ScalarAsync("SELECT status FROM organizations.pending_registrations WHERE slug = @s", ("s", slug)));
        Assert.Equal(string.Empty, await ScalarAsync("SELECT password_hash FROM organizations.pending_registrations WHERE slug = @s", ("s", slug)));

        // El propietario entra con la clave que eligió y su único rol es el de propietario del gimnasio (nada de plataforma).
        var jwt = await TestApi.LoginAsync(api, slug, email, StrongPassword);
        Assert.False(string.IsNullOrEmpty(jwt));
        var roles = await ScalarAsync(
            "SELECT string_agg(r.name, ',') FROM identity.user_roles ur JOIN identity.app_roles r ON r.id = ur.role_id JOIN identity.app_users u ON u.id = ur.user_id WHERE u.tenant_id = @t AND lower(u.email) = lower(@e)",
            ("t", tenantId), ("e", email));
        Assert.Equal("Propietario", roles);
        Assert.Equal(0, await CountAsync(
            "SELECT count(*) FROM identity.role_permissions rp JOIN identity.user_roles ur ON ur.role_id = rp.role_id JOIN identity.app_users u ON u.id = ur.user_id WHERE u.tenant_id = @t AND rp.permission_code LIKE 'platform.%'",
            ("t", tenantId)));
    }

    [Fact]
    public async Task Legal_acceptance_is_recorded_with_its_version_and_date()
    {
        var (_, slug, _, token) = await RegisteredAsync();
        using var client = api.CreateClient();
        await VerifyAsync(client, token);

        var tenantId = Guid.Parse((await ScalarAsync("SELECT id::text FROM organizations.tenants WHERE slug = @s", ("s", slug)))!);

        Assert.Equal(2, await CountAsync("SELECT count(*) FROM organizations.legal_acceptances WHERE tenant_id = @t AND version = @v AND accepted_at <= now()", ("t", tenantId), ("v", Terms)));
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM organizations.legal_acceptances WHERE tenant_id = @t AND document = 'terms'", ("t", tenantId)));
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM organizations.legal_acceptances WHERE tenant_id = @t AND document = 'privacy'", ("t", tenantId)));
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM audit.audit_logs WHERE tenant_id = @t AND action = 'tenant.self_registered'", ("t", tenantId)));
    }

    [Fact]
    public async Task The_same_link_cannot_create_two_gyms()
    {
        var (_, slug, _, token) = await RegisteredAsync();
        using var client = api.CreateClient();

        var first = await VerifyAsync(client, token);
        var second = await ReadAsync(await VerifyAsync(client, token));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("registration.token_invalid", second.Code);
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM organizations.tenants WHERE slug = @s", ("s", slug)));
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM subscriptions.subscriptions s JOIN organizations.tenants t ON t.id = s.tenant_id WHERE t.slug = @s", ("s", slug)));
    }

    [Fact]
    public async Task Simultaneous_confirmations_create_exactly_one_gym()
    {
        var (_, slug, _, token) = await RegisteredAsync();
        using var client = api.CreateClient();

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => VerifyAsync(client, token)));

        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM organizations.tenants WHERE slug = @s", ("s", slug)));
    }

    [Theory]
    [InlineData("token-que-no-existe")]
    [InlineData("a")]
    public async Task An_unknown_token_is_rejected_with_the_generic_error(string token)
    {
        using var client = api.CreateClient();

        var result = await ReadAsync(await VerifyAsync(client, token));

        Assert.Equal("registration.token_invalid", result.Code);
    }

    [Fact]
    public async Task An_expired_link_or_an_expired_request_cannot_create_a_gym()
    {
        var (_, slugA, _, tokenA) = await RegisteredAsync();
        var (_, slugB, _, tokenB) = await RegisteredAsync();
        await ExecAsync("UPDATE identity.account_tokens SET expires_at = now() - interval '1 minute' WHERE token_hash = @h", ("h", FitCore.Platform.Application.AccountTokens.AccountTokenService.Hash(tokenA)));
        await ExecAsync("UPDATE organizations.pending_registrations SET expires_at = now() - interval '1 minute' WHERE slug = @s", ("s", slugB));
        using var client = api.CreateClient();

        var linkExpired = await ReadAsync(await VerifyAsync(client, tokenA));
        var requestExpired = await ReadAsync(await VerifyAsync(client, tokenB));

        Assert.Equal("registration.token_invalid", linkExpired.Code);
        Assert.Equal("registration.expired", requestExpired.Code);
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM organizations.tenants WHERE slug IN (@a, @b)", ("a", slugA), ("b", slugB)));
    }

    [Fact]
    public async Task A_second_trial_for_the_same_owner_is_refused_with_a_clear_message_and_creates_nothing()
    {
        using var client = api.CreateClient();
        var plan = await PlanAsync(price: 25m, trialDays: 14);
        var owner = $"repetido-{Guid.NewGuid():N}";
        var firstEmail = $"{owner}@registro.test";
        var firstSlug = NewSlug();
        await RegisterAsync(client, firstEmail, firstSlug, plan);
        Assert.Equal(HttpStatusCode.OK, (await VerifyAsync(client, (await TokenFromEmailAsync(firstEmail))!)).StatusCode);

        // El mismo propietario con una «+etiqueta» y otro identificador.
        var secondEmail = $"{owner}+otro@Registro.test";
        var secondSlug = NewSlug();
        await RegisterAsync(client, secondEmail, secondSlug, plan);
        var response = await ReadAsync(await VerifyAsync(client, (await TokenFromEmailAsync(secondEmail))!));

        Assert.Equal(HttpStatusCode.Conflict, response.Status);
        Assert.Equal("registration.trial_already_used", response.Code);
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM organizations.tenants WHERE slug = @s", ("s", secondSlug)));
        Assert.Equal("Rejected", await ScalarAsync("SELECT status FROM organizations.pending_registrations WHERE slug = @s", ("s", secondSlug)));
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM organizations.trial_claims WHERE email_normalized = @e", ("e", $"{owner}@registro.test")));
    }

    [Fact]
    public async Task A_free_plan_without_trial_needs_no_trial_claim_and_starts_active()
    {
        using var client = api.CreateClient();
        var plan = await PlanAsync(price: 0m, trialDays: 0);
        var email = NewEmail();
        var slug = NewSlug();
        await RegisterAsync(client, email, slug, plan);

        var response = await VerifyAsync(client, (await TokenFromEmailAsync(email))!);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tenantId = Guid.Parse((await ScalarAsync("SELECT id::text FROM organizations.tenants WHERE slug = @s", ("s", slug)))!);
        Assert.Equal("Active", await ScalarAsync("SELECT status FROM subscriptions.subscriptions WHERE tenant_id = @t", ("t", tenantId)));
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM organizations.trial_claims WHERE tenant_id = @t", ("t", tenantId)));
    }

    [Fact]
    public async Task If_the_identifier_is_taken_before_confirming_nothing_is_created()
    {
        var (_, slug, _, token) = await RegisteredAsync();
        await TestApi.NewTenantAsync(api, slug); // otro gimnasio se quedó con el identificador mientras tanto
        using var client = api.CreateClient();

        var result = await ReadAsync(await VerifyAsync(client, token));

        Assert.Equal("registration.slug_taken", result.Code);
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM organizations.tenants WHERE slug = @s", ("s", slug)));
        Assert.Equal("Rejected", await ScalarAsync("SELECT status FROM organizations.pending_registrations WHERE slug = @s AND status <> 'Superseded' ORDER BY created_at DESC LIMIT 1", ("s", slug)));
    }

    [Fact]
    public async Task A_plan_withdrawn_before_confirming_leaves_the_link_usable_and_creates_nothing()
    {
        var (_, slug, plan, token) = await RegisteredAsync();
        using var platform = await TestApi.PlatformClientAsync(api);
        var id = (await ReadAsync(await platform.GetAsync($"/api/v1/platform/plans?search={plan}"))).Body.GetProperty("items")[0].GetProperty("id").GetGuid();
        await platform.PatchAsJsonAsync($"/api/v1/platform/plans/{id}/visibility", new { isPublic = false });
        using var client = api.CreateClient();

        var withdrawn = await ReadAsync(await VerifyAsync(client, token));
        Assert.Equal("registration.plan_unavailable", withdrawn.Code);
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM organizations.tenants WHERE slug = @s", ("s", slug)));

        await platform.PatchAsJsonAsync($"/api/v1/platform/plans/{id}/visibility", new { isPublic = true });
        Assert.Equal(HttpStatusCode.OK, (await VerifyAsync(client, token)).StatusCode); // el enlace no se gastó
    }

    // ---- reenvío ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Resending_issues_a_new_link_and_invalidates_the_old_one()
    {
        var (email, _, _, oldToken) = await RegisteredAsync();
        using var client = api.CreateClient();
        await ExecAsync("UPDATE organizations.pending_registrations SET last_sent_at = now() - interval '2 minutes' WHERE lower(email) = lower(@e)", ("e", email));
        await ExecAsync("UPDATE messaging.emails SET created_at = created_at - interval '1 minute' WHERE lower(to_address) = lower(@e)", ("e", email));

        var response = await client.PostAsJsonAsync("/api/v1/public/registrations/resend-verification", new { email });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var newToken = await TokenFromEmailAsync(email);
        Assert.NotEqual(oldToken, newToken);
        Assert.Equal("registration.token_invalid", (await ReadAsync(await VerifyAsync(client, oldToken))).Code);
        Assert.Equal(HttpStatusCode.OK, (await VerifyAsync(client, newToken!)).StatusCode);
    }

    [Fact]
    public async Task Resending_too_soon_or_for_an_unknown_email_answers_the_same_and_sends_nothing()
    {
        var (email, _, _, _) = await RegisteredAsync();
        var stranger = NewEmail();
        using var client = api.CreateClient();

        var tooSoon = await client.PostAsJsonAsync("/api/v1/public/registrations/resend-verification", new { email });
        var unknown = await client.PostAsJsonAsync("/api/v1/public/registrations/resend-verification", new { email = stranger });

        Assert.Equal(HttpStatusCode.Accepted, tooSoon.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, unknown.StatusCode);
        Assert.Equal(await tooSoon.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM messaging.emails WHERE lower(to_address) = lower(@e)", ("e", email)));
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM messaging.emails WHERE lower(to_address) = lower(@e)", ("e", stranger)));
    }

    // ---- fallos y compensación ---------------------------------------------------------------------------------------------------

    private sealed class FlakyValidationHook : ITenantProvisioningHook
    {
        private static int _calls;

        public Task<Result> ValidateAsync(TenantPlanSelection selection, CancellationToken cancellationToken) =>
            Task.FromResult(Interlocked.Increment(ref _calls) == 1 ? Result.Failure(Error.Failure("test.transient", "falla transitoria")) : Result.Success());

        public Task<Result> OnProvisionedAsync(Guid tenantId, TenantPlanSelection selection, CancellationToken cancellationToken) => Task.FromResult(Result.Success());
    }

    private sealed class FailingSubscriptionHook : ITenantProvisioningHook
    {
        public Task<Result> ValidateAsync(TenantPlanSelection selection, CancellationToken cancellationToken) => Task.FromResult(Result.Success());

        public Task<Result> OnProvisionedAsync(Guid tenantId, TenantPlanSelection selection, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Failure(Error.Failure("test.hook_failed", "no se pudo abrir la suscripción")));
    }

    [Fact]
    public async Task A_failure_before_anything_is_created_leaves_no_trace_and_the_link_still_works()
    {
        var (_, slug, _, token) = await RegisteredAsync();
        using var flaky = api.CreateClientWithServices(s => s.AddScoped<ITenantProvisioningHook, FlakyValidationHook>());

        var first = await ReadAsync(await VerifyAsync(flaky, token));
        Assert.Equal("test.transient", first.Code);
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM organizations.tenants WHERE slug = @s", ("s", slug)));
        Assert.Equal("Pending", await ScalarAsync("SELECT status FROM organizations.pending_registrations WHERE slug = @s", ("s", slug)));

        var second = await VerifyAsync(flaky, token); // el token no se gastó
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM organizations.tenants WHERE slug = @s", ("s", slug)));
    }

    [Fact]
    public async Task If_the_subscription_cannot_be_opened_the_gym_is_suspended_and_the_request_marked_failed()
    {
        var (email, slug, _, token) = await RegisteredAsync();
        using var broken = api.CreateClientWithServices(s => s.AddScoped<ITenantProvisioningHook, FailingSubscriptionHook>());

        var result = await ReadAsync(await VerifyAsync(broken, token));

        Assert.Equal(HttpStatusCode.InternalServerError, result.Status);
        Assert.Equal("registration.provisioning_failed", result.Code);
        Assert.Equal("Suspended", await ScalarAsync("SELECT status FROM organizations.tenants WHERE slug = @s", ("s", slug)));
        Assert.Equal("Failed", await ScalarAsync("SELECT status FROM organizations.pending_registrations WHERE slug = @s", ("s", slug)));
        using var client = api.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { tenantSlug = slug, email, password = StrongPassword });
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode); // un gimnasio suspendido no deja entrar
    }

    // ---- mantenimiento ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Maintenance_expires_old_requests_wipes_their_password_and_deletes_very_old_finished_ones()
    {
        var (_, slugOld, _, _) = await RegisteredAsync();
        var (_, slugFinished, _, tokenFinished) = await RegisteredAsync();
        using var client = api.CreateClient();
        await VerifyAsync(client, tokenFinished);
        await ExecAsync("UPDATE organizations.pending_registrations SET expires_at = now() - interval '1 hour' WHERE slug = @s", ("s", slugOld));
        await ExecAsync("UPDATE organizations.pending_registrations SET created_at = now() - interval '40 days' WHERE slug = @s", ("s", slugFinished));

        await using var scope = api.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FitCore.Platform.Persistence.PlatformDbContext>();
        await FitCore.Platform.Application.Registration.RegistrationMaintenance.RunAsync(db, new FitCore.Infrastructure.Core.SystemClock(), default);

        Assert.Equal("Expired", await ScalarAsync("SELECT status FROM organizations.pending_registrations WHERE slug = @s", ("s", slugOld)));
        Assert.Equal(string.Empty, await ScalarAsync("SELECT password_hash FROM organizations.pending_registrations WHERE slug = @s", ("s", slugOld)));
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM organizations.pending_registrations WHERE slug = @s", ("s", slugFinished)));
    }

    [Fact]
    public void Email_normalization_ignores_case_plus_tags_and_gmail_dots()
    {
        var n = FitCore.Platform.Application.Registration.EmailNormalizer.Normalize;

        Assert.Equal("ana@correo.com", n("  Ana+gym@Correo.com "));
        Assert.Equal("juanperez@gmail.com", n("juan.perez+x@googlemail.com"));
        Assert.Equal("a.b@empresa.com", n("A.B@empresa.com")); // los puntos solo se ignoran en Gmail
    }
}
