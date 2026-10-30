using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FitCore.Api.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FitCore.Api.IntegrationTests;

/// <summary>Formularios de contacto y demostración del sitio público: validación, antiabuso, privacidad y la pantalla de atención de la plataforma.</summary>
[Collection(ApiCollection1.Name)]
public class ContactTests(ApiFixture api)
{
    private const string PrivacyVersion = "2026-10-30-borrador";

    private static string Unique() => Guid.NewGuid().ToString("N")[..10];

    private static Dictionary<string, object?> Body(string? tag = null, Dictionary<string, object?>? extra = null)
    {
        var id = tag ?? Unique();
        var body = new Dictionary<string, object?>
        {
            ["name"] = $"Persona {id}", ["email"] = $"contacto-{id}@visitantes.test", ["phone"] = "+593 99 123 4567", ["gymName"] = $"Gimnasio {id}",
            ["message"] = "Quisiera conocer cómo funciona el sistema para mi gimnasio.", ["acceptPrivacy"] = true, ["privacyVersion"] = PrivacyVersion
        };
        foreach (var (key, value) in extra ?? [])
        {
            body[key] = value;
        }

        return body;
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

    private async Task<long> CountAsync(string sql, params (string Name, object Value)[] args) => long.Parse((await ScalarAsync(sql, args))!);

    private static async Task<(HttpStatusCode Status, string? Code, JsonElement Body)> ReadAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        var json = string.IsNullOrEmpty(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
        var code = json.ValueKind == JsonValueKind.Object && json.TryGetProperty("code", out var c) ? c.GetString() : null;
        return (response.StatusCode, code, json);
    }

    // ---- recibir -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_contact_form_is_stored_and_the_sales_team_is_notified_by_email()
    {
        using var client = api.CreateClient();
        var id = Unique();

        var response = await client.PostAsJsonAsync("/api/v1/public/contact", Body(id));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("Contact", await ScalarAsync("SELECT kind FROM organizations.contact_requests WHERE email_normalized = @e", ("e", $"contacto-{id}@visitantes.test")));
        Assert.Equal("New", await ScalarAsync("SELECT status FROM organizations.contact_requests WHERE email_normalized = @e", ("e", $"contacto-{id}@visitantes.test")));
        Assert.Equal(PrivacyVersion, await ScalarAsync("SELECT privacy_version FROM organizations.contact_requests WHERE email_normalized = @e", ("e", $"contacto-{id}@visitantes.test")));
        var mail = await ScalarAsync("SELECT text_body FROM messaging.emails WHERE lower(to_address) = 'ventas@fit360.test' AND text_body LIKE '%' || @e || '%'", ("e", $"contacto-{id}@visitantes.test"));
        Assert.Contains($"Persona {id}", mail);
        Assert.Contains("Contacto", mail);
    }

    [Fact]
    public async Task A_demo_request_is_stored_with_its_own_kind()
    {
        using var client = api.CreateClient();
        var id = Unique();

        var response = await client.PostAsJsonAsync("/api/v1/public/demo-requests", Body(id));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("Demo", await ScalarAsync("SELECT kind FROM organizations.contact_requests WHERE email_normalized = @e", ("e", $"contacto-{id}@visitantes.test")));
        var mail = await ScalarAsync("SELECT text_body FROM messaging.emails WHERE lower(to_address) = 'ventas@fit360.test' AND text_body LIKE '%' || @e || '%'", ("e", $"contacto-{id}@visitantes.test"));
        Assert.Contains("Demostración", mail);
    }

    [Fact]
    public async Task Phone_and_gym_are_optional_and_nothing_about_the_visitor_device_is_stored()
    {
        using var client = api.CreateClient();
        var id = Unique();
        var body = Body(id, new() { ["phone"] = null, ["gymName"] = null });

        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync("/api/v1/public/contact", body)).StatusCode);

        Assert.True(string.IsNullOrEmpty(await ScalarAsync("SELECT phone FROM organizations.contact_requests WHERE email_normalized = @e", ("e", $"contacto-{id}@visitantes.test"))));
        var columns = await ScalarAsync("SELECT string_agg(column_name, ',') FROM information_schema.columns WHERE table_schema = 'organizations' AND table_name = 'contact_requests'");
        Assert.DoesNotContain("ip", columns!.Split(',').Select(c => c.ToLowerInvariant()));
        Assert.DoesNotContain("user_agent", columns);
    }

    [Theory]
    [InlineData("name", "")]
    [InlineData("name", "x")]
    [InlineData("email", "no-es-correo")]
    [InlineData("message", "corto")]
    [InlineData("phone", "abc")]
    [InlineData("acceptPrivacy", false)]
    public async Task Invalid_forms_are_rejected_and_nothing_is_stored(string field, object value)
    {
        using var client = api.CreateClient();
        var id = Unique();
        var body = Body(id);
        body[field] = value is "x" ? new string('x', 121) : value;
        var before = await CountAsync("SELECT count(*) FROM organizations.contact_requests");

        var response = await client.PostAsJsonAsync("/api/v1/public/contact", body);

        Assert.False(response.IsSuccessStatusCode);
        Assert.Equal(before, await CountAsync("SELECT count(*) FROM organizations.contact_requests"));
    }

    [Fact]
    public async Task A_message_over_one_thousand_characters_is_rejected()
    {
        using var client = api.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/public/contact", Body(extra: new() { ["message"] = new string('a', 1001) }));

        Assert.False(response.IsSuccessStatusCode);
    }

    [Fact]
    public async Task An_outdated_privacy_policy_version_is_rejected()
    {
        using var client = api.CreateClient();

        var result = await ReadAsync(await client.PostAsJsonAsync("/api/v1/public/contact", Body(extra: new() { ["privacyVersion"] = "vieja" })));

        Assert.Equal("registration.legal_version_mismatch", result.Code);
    }

    [Fact]
    public async Task The_trap_field_makes_a_robot_look_successful_but_stores_nothing()
    {
        using var client = api.CreateClient();
        var id = Unique();

        var response = await client.PostAsJsonAsync("/api/v1/public/contact", Body(id, new() { ["website"] = "http://spam.test" }));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM organizations.contact_requests WHERE email_normalized = @e", ("e", $"contacto-{id}@visitantes.test")));
    }

    [Fact]
    public async Task Too_many_messages_from_one_email_in_an_hour_stop_silently()
    {
        using var client = api.CreateClient();
        var id = Unique();
        var bodies = new List<string>();

        for (var i = 0; i < 5; i++)
        {
            var response = await client.PostAsJsonAsync("/api/v1/public/contact", Body(id));
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            bodies.Add(await response.Content.ReadAsStringAsync());
        }

        Assert.Single(bodies.Distinct());
        Assert.Equal(3, await CountAsync("SELECT count(*) FROM organizations.contact_requests WHERE email_normalized = @e", ("e", $"contacto-{id}@visitantes.test")));
    }

    [Fact]
    public async Task The_forms_are_rate_limited_per_ip()
    {
        using var limited = api.CreateClientWith(("RateLimiting:RegisterPermitPerMinute", "2"));

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            statuses.Add((await limited.PostAsJsonAsync("/api/v1/public/contact", Body())).StatusCode);
        }

        Assert.Equal([HttpStatusCode.Accepted, HttpStatusCode.Accepted, HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests], statuses);
    }

    [Fact]
    public async Task Without_a_notification_address_or_mail_server_the_request_is_still_kept_for_the_platform_screen()
    {
        using var noNotify = api.CreateClientWith(("Contact:NotifyEmail", ""));
        using var noMail = api.CreateClientWith(("Email:Smtp:Host", ""));
        var a = Unique();
        var b = Unique();

        Assert.Equal(HttpStatusCode.Accepted, (await noNotify.PostAsJsonAsync("/api/v1/public/contact", Body(a))).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await noMail.PostAsJsonAsync("/api/v1/public/contact", Body(b))).StatusCode);

        Assert.Equal(1, await CountAsync("SELECT count(*) FROM organizations.contact_requests WHERE email_normalized = @e", ("e", $"contacto-{a}@visitantes.test")));
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM organizations.contact_requests WHERE email_normalized = @e", ("e", $"contacto-{b}@visitantes.test")));
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM messaging.emails WHERE text_body LIKE '%' || @e || '%'", ("e", $"contacto-{a}@visitantes.test")));
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM messaging.emails WHERE text_body LIKE '%' || @e || '%'", ("e", $"contacto-{b}@visitantes.test")));
    }

    [Fact]
    public async Task What_the_visitor_types_cannot_inject_html_into_the_team_notification()
    {
        using var client = api.CreateClient();
        var id = Unique();
        var body = Body(id, new() { ["name"] = $"<script>alert('{id}')</script>", ["message"] = "<img src=x onerror=alert(1)> y un mensaje largo de prueba" });

        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync("/api/v1/public/contact", body)).StatusCode);

        var html = await ScalarAsync("SELECT html_body FROM messaging.emails WHERE lower(to_address) = 'ventas@fit360.test' AND text_body LIKE '%' || @e || '%'", ("e", $"contacto-{id}@visitantes.test"));
        Assert.NotNull(html);
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("<img", html);
    }

    // ---- atender (plataforma) ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_platform_team_lists_filters_and_marks_requests_as_attended()
    {
        using var client = api.CreateClient();
        var demoId = Unique();
        var contactId = Unique();
        await client.PostAsJsonAsync("/api/v1/public/demo-requests", Body(demoId));
        await client.PostAsJsonAsync("/api/v1/public/contact", Body(contactId));
        using var platform = await TestApi.PlatformClientAsync(api);

        var demos = await ReadAsync(await platform.GetAsync("/api/v1/platform/contact-requests?kind=Demo&pageSize=100"));
        var item = demos.Body.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("email").GetString() == $"contacto-{demoId}@visitantes.test");
        var requestId = item.GetProperty("id").GetGuid();
        var attended = await ReadAsync(await platform.PatchAsJsonAsync($"/api/v1/platform/contact-requests/{requestId}", new { attended = true }));
        var onlyNew = await ReadAsync(await platform.GetAsync("/api/v1/platform/contact-requests?status=New&pageSize=100"));
        var reopened = await ReadAsync(await platform.PatchAsJsonAsync($"/api/v1/platform/contact-requests/{requestId}", new { attended = false }));

        Assert.Equal("Demo", item.GetProperty("kind").GetString());
        Assert.Equal("Attended", attended.Body.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, attended.Body.GetProperty("attendedAt").ValueKind);
        Assert.DoesNotContain(onlyNew.Body.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetGuid() == requestId);
        Assert.Contains(onlyNew.Body.GetProperty("items").EnumerateArray(), i => i.GetProperty("email").GetString() == $"contacto-{contactId}@visitantes.test");
        Assert.Equal("New", reopened.Body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Only_the_platform_team_can_see_or_attend_requests()
    {
        var tenant = await TestApi.NewTenantAsync(api, $"con-{Unique()}"[..14]);
        using var anon = api.CreateClient();

        Assert.Equal(HttpStatusCode.Forbidden, (await tenant.Owner.GetAsync("/api/v1/platform/contact-requests")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tenant.Owner.PatchAsJsonAsync($"/api/v1/platform/contact-requests/{Guid.NewGuid()}", new { attended = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/v1/platform/contact-requests")).StatusCode);
    }

    [Fact]
    public async Task Marking_an_unknown_request_answers_not_found()
    {
        using var platform = await TestApi.PlatformClientAsync(api);

        var result = await ReadAsync(await platform.PatchAsJsonAsync($"/api/v1/platform/contact-requests/{Guid.NewGuid()}", new { attended = true }));

        Assert.Equal(HttpStatusCode.NotFound, result.Status);
        Assert.Equal("contact.not_found", result.Code);
    }

    // ---- retención ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Requests_older_than_one_hundred_eighty_days_are_purged()
    {
        using var client = api.CreateClient();
        var oldId = Unique();
        var recentId = Unique();
        await client.PostAsJsonAsync("/api/v1/public/contact", Body(oldId));
        await client.PostAsJsonAsync("/api/v1/public/contact", Body(recentId));
        await using (var conn = new NpgsqlConnection(api.AdminConnection))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("UPDATE organizations.contact_requests SET created_at = now() - interval '181 days' WHERE email_normalized = @e", conn);
            cmd.Parameters.AddWithValue("e", $"contacto-{oldId}@visitantes.test");
            await cmd.ExecuteNonQueryAsync();
        }

        await using var scope = api.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FitCore.Platform.Persistence.PlatformDbContext>();
        await FitCore.Platform.Application.Contact.ContactMaintenance.PurgeAsync(db, new FitCore.Infrastructure.Core.SystemClock(), default);

        Assert.Equal(0, await CountAsync("SELECT count(*) FROM organizations.contact_requests WHERE email_normalized = @e", ("e", $"contacto-{oldId}@visitantes.test")));
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM organizations.contact_requests WHERE email_normalized = @e", ("e", $"contacto-{recentId}@visitantes.test")));
    }
}
