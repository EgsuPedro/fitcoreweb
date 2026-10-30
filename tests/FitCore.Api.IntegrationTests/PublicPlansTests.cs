using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FitCore.Api.IntegrationTests.Infrastructure;

namespace FitCore.Api.IntegrationTests;

/// <summary>Planes públicos del sitio comercial: publicación explícita, qué se expone, qué nunca se expone y límites de frecuencia.</summary>
[Collection(ApiCollection1.Name)]
public class PublicPlansTests(ApiFixture api)
{
    private static readonly string[] SomeFeatures = ["members", "branches", "classes"];

    private async Task<(Guid Id, string Code)> PlanAsync(decimal price = 25m, int trialDays = 14, bool isActive = true)
    {
        using var platform = await TestApi.PlatformClientAsync(api);
        var code = $"pub-{Guid.NewGuid():N}"[..20];
        var response = await platform.PostAsJsonAsync("/api/v1/platform/plans", new
        {
            code, name = $"Plan {code}", description = "Para gimnasios que empiezan", rules = "REGLA INTERNA SECRETA", isActive,
            currency = "USD", billingPeriod = "Monthly", price, trialDays, graceDays = 7, features = SomeFeatures,
            limits = new object[]
            {
                new { code = "branches", value = 2L }, new { code = "internal_users", value = 5L },
                new { code = "members", value = (long?)null }, new { code = "storage_mb", value = 500L }
            }
        });
        var body = await ReadAsync(response, HttpStatusCode.Created);
        return (body.GetProperty("id").GetGuid(), code);
    }

    private async Task<HttpResponseMessage> VisibilityAsync(Guid id, bool isPublic)
    {
        using var platform = await TestApi.PlatformClientAsync(api);
        return await platform.PatchAsJsonAsync($"/api/v1/platform/plans/{id}/visibility", new { isPublic });
    }

    private async Task<HttpResponseMessage> StatusAsync(Guid id, bool isActive)
    {
        using var platform = await TestApi.PlatformClientAsync(api);
        return await platform.PatchAsJsonAsync($"/api/v1/platform/plans/{id}/status", new { isActive });
    }

    private async Task<(Guid Id, string Code)> PublishedPlanAsync(decimal price = 25m, int trialDays = 14)
    {
        var plan = await PlanAsync(price, trialDays);
        await ReadAsync(await VisibilityAsync(plan.Id, true));
        return plan;
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"Respondió {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private async Task<JsonElement> ListAsync()
    {
        using var anon = api.CreateClient();
        return await ReadAsync(await anon.GetAsync("/api/v1/public/plans"));
    }

    private static bool Contains(JsonElement list, string code) => list.EnumerateArray().Any(p => p.GetProperty("code").GetString() == code);

    [Fact]
    public async Task A_new_active_plan_is_not_public_until_the_platform_publishes_it()
    {
        var plan = await PlanAsync();

        Assert.False(Contains(await ListAsync(), plan.Code));
        using var anon = api.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/api/v1/public/plans/{plan.Code}")).StatusCode);
    }

    [Fact]
    public async Task A_published_plan_is_visible_without_logging_in_with_its_real_terms()
    {
        var plan = await PublishedPlanAsync(price: 25m, trialDays: 14);

        var list = await ListAsync();
        var item = list.EnumerateArray().Single(p => p.GetProperty("code").GetString() == plan.Code);

        Assert.Equal(25m, item.GetProperty("price").GetDecimal());
        Assert.Equal("USD", item.GetProperty("currency").GetString());
        Assert.Equal("Monthly", item.GetProperty("billingPeriod").GetString());
        Assert.Equal(14, item.GetProperty("trialDays").GetInt32());
        Assert.True(item.GetProperty("selfService").GetBoolean());
        Assert.Equal(SomeFeatures.Order(), item.GetProperty("features").EnumerateArray().Select(f => f.GetProperty("code").GetString()!).Order());
        var branches = item.GetProperty("limits").EnumerateArray().Single(l => l.GetProperty("code").GetString() == "branches");
        Assert.Equal(2, branches.GetProperty("value").GetInt64());
        var members = item.GetProperty("limits").EnumerateArray().Single(l => l.GetProperty("code").GetString() == "members");
        Assert.Equal(JsonValueKind.Null, members.GetProperty("value").ValueKind); // sin límite
        using var anon = api.CreateClient();
        var single = await ReadAsync(await anon.GetAsync($"/api/v1/public/plans/{plan.Code}"));
        Assert.Equal(plan.Code, single.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Public_responses_never_carry_internal_data()
    {
        var plan = await PublishedPlanAsync();
        using var anon = api.CreateClient();

        var text = await (await anon.GetAsync($"/api/v1/public/plans/{plan.Code}")).Content.ReadAsStringAsync();
        var listText = await (await anon.GetAsync("/api/v1/public/plans")).Content.ReadAsStringAsync();

        foreach (var body in new[] { text, listText })
        {
            Assert.DoesNotContain("REGLA INTERNA SECRETA", body);
            Assert.DoesNotContain("\"id\"", body);
            Assert.DoesNotContain("graceDays", body);
            Assert.DoesNotContain("\"rules\"", body);
            Assert.DoesNotContain("versions", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("createdBy", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("isActive", body);
        }
    }

    [Fact]
    public async Task Only_plans_with_a_trial_or_a_price_of_zero_allow_self_service()
    {
        var withTrial = await PublishedPlanAsync(price: 40m, trialDays: 7);
        var paidNoTrial = await PublishedPlanAsync(price: 40m, trialDays: 0);
        var free = await PublishedPlanAsync(price: 0m, trialDays: 0);

        var list = await ListAsync();
        bool SelfService(string code) => list.EnumerateArray().Single(p => p.GetProperty("code").GetString() == code).GetProperty("selfService").GetBoolean();

        Assert.True(SelfService(withTrial.Code));
        Assert.False(SelfService(paidNoTrial.Code));
        Assert.True(SelfService(free.Code));
    }

    [Fact]
    public async Task Hiding_a_plan_removes_it_from_the_site()
    {
        var plan = await PublishedPlanAsync();
        Assert.True(Contains(await ListAsync(), plan.Code));

        await ReadAsync(await VisibilityAsync(plan.Id, false));

        Assert.False(Contains(await ListAsync(), plan.Code));
    }

    [Fact]
    public async Task Deactivating_a_public_plan_hides_it_and_reactivating_shows_it_again()
    {
        var plan = await PublishedPlanAsync();

        await ReadAsync(await StatusAsync(plan.Id, false));
        Assert.False(Contains(await ListAsync(), plan.Code));
        using var anon = api.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/api/v1/public/plans/{plan.Code}")).StatusCode);

        await ReadAsync(await StatusAsync(plan.Id, true));
        Assert.True(Contains(await ListAsync(), plan.Code));
    }

    [Fact]
    public async Task An_inactive_plan_cannot_be_published()
    {
        var plan = await PlanAsync(isActive: false);

        var response = await VisibilityAsync(plan.Id, true);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("plans.not_publishable", (await ReadAsync(response, HttpStatusCode.Conflict)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task The_legacy_plan_can_never_be_published_or_shown()
    {
        using var platform = await TestApi.PlatformClientAsync(api);
        var legacy = (await ReadAsync(await platform.GetAsync("/api/v1/platform/plans?search=legacy"))).GetProperty("items")
            .EnumerateArray().Single(p => p.GetProperty("code").GetString() == "legacy");

        var response = await VisibilityAsync(legacy.GetProperty("id").GetGuid(), true);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.False(Contains(await ListAsync(), "legacy"));
        using var anon = api.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync("/api/v1/public/plans/legacy")).StatusCode);
    }

    [Fact]
    public async Task A_price_change_shows_up_on_the_site_with_the_new_version_only()
    {
        var plan = await PublishedPlanAsync(price: 25m);
        using var platform = await TestApi.PlatformClientAsync(api);

        await ReadAsync(await platform.PutAsJsonAsync($"/api/v1/platform/plans/{plan.Id}", new
        {
            name = $"Plan {plan.Code}", description = "Nuevo", rules = (string?)null, currency = "USD", billingPeriod = "Monthly", price = 30m,
            trialDays = 14, graceDays = 7, features = SomeFeatures,
            limits = new object[]
            {
                new { code = "branches", value = 2L }, new { code = "internal_users", value = 5L },
                new { code = "members", value = (long?)null }, new { code = "storage_mb", value = 500L }
            }
        }));

        var item = (await ListAsync()).EnumerateArray().Single(p => p.GetProperty("code").GetString() == plan.Code);
        Assert.Equal(30m, item.GetProperty("price").GetDecimal());
        Assert.Equal("Nuevo", item.GetProperty("description").GetString());
    }

    [Fact]
    public async Task Unknown_hidden_and_inactive_plans_answer_exactly_the_same()
    {
        var hidden = await PlanAsync();
        var inactive = await PublishedPlanAsync();
        await StatusAsync(inactive.Id, false);
        using var anon = api.CreateClient();

        var bodies = new List<string>();
        foreach (var code in new[] { "no-existe-este-plan", hidden.Code, inactive.Code })
        {
            var response = await anon.GetAsync($"/api/v1/public/plans/{code}");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            bodies.Add(await response.Content.ReadAsStringAsync());
        }

        Assert.Single(bodies.Select(b => JsonDocument.Parse(b).RootElement.GetProperty("code").GetString()).Distinct());
    }

    [Fact]
    public async Task Only_the_platform_can_change_visibility()
    {
        var plan = await PlanAsync();
        var slug = $"gym-{Guid.NewGuid():N}"[..20];
        var tenant = await TestApi.NewTenantAsync(api, slug);

        var asOwner = await tenant.Owner.PatchAsJsonAsync($"/api/v1/platform/plans/{plan.Id}/visibility", new { isPublic = true });
        using var anon = api.CreateClient();
        var asAnonymous = await anon.PatchAsJsonAsync($"/api/v1/platform/plans/{plan.Id}/visibility", new { isPublic = true });

        Assert.Equal(HttpStatusCode.Forbidden, asOwner.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, asAnonymous.StatusCode);
        Assert.False(Contains(await ListAsync(), plan.Code));
    }

    [Fact]
    public async Task The_catalog_responses_can_be_cached_for_a_minute()
    {
        var plan = await PublishedPlanAsync();
        using var anon = api.CreateClient();

        var list = await anon.GetAsync("/api/v1/public/plans");
        var single = await anon.GetAsync($"/api/v1/public/plans/{plan.Code}");

        Assert.Equal("public, max-age=60", list.Headers.CacheControl?.ToString());
        Assert.Equal("public, max-age=60", single.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Public_reads_are_rate_limited_per_ip()
    {
        using var limited = api.CreateClientWith(("RateLimiting:PublicReadPermitPerMinute", "3"));

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            statuses.Add((await limited.GetAsync("/api/v1/public/plans")).StatusCode);
        }

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests], statuses);
    }

    [Fact]
    public async Task Changing_visibility_leaves_an_audit_trail()
    {
        var plan = await PlanAsync();
        await ReadAsync(await VisibilityAsync(plan.Id, true));

        await using var conn = new Npgsql.NpgsqlConnection(api.AdminConnection);
        await conn.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(
            "SELECT count(*) FROM audit.audit_logs WHERE entity = 'Plan' AND entity_id = @id AND after::text LIKE '%IsPublic%'", conn);
        cmd.Parameters.AddWithValue("id", plan.Id.ToString());
        Assert.True((long)(await cmd.ExecuteScalarAsync())! >= 1);
    }
}
