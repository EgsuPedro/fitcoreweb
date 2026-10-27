using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace FitCore.Api.IntegrationTests.Infrastructure;

public sealed record TokenDto(string AccessToken, string TokenType, int ExpiresIn, string? RefreshToken, DateTimeOffset RefreshTokenExpiresAt);

public sealed record TenantDto(Guid Id, string Slug, string Name, string Status);

public sealed record BranchDto(Guid Id, string Code, string Name, bool IsActive);

public sealed record RoleDto(Guid Id, string Name, bool GrantsAllBranches, string[] Permissions);

public sealed record UserDto(Guid Id, string Email, string FullName, bool IsActive, Guid[] BranchIds);

public sealed record PagedDto<T>(T[] Items, int Page, int PageSize, int TotalCount);

public sealed record AuditDto(long Id, string Action, string Entity, string? EntityId);

/// <summary>Un gimnasio de prueba con su propietario autenticado.</summary>
public sealed record TestTenant(Guid Id, string Slug, string OwnerEmail, string OwnerPassword, HttpClient Owner);

public static class TestApi
{
    public const string OwnerPassword = "OwnerPass1234";
    public const string UserPassword = "UserPass12345";

    public static async Task<string> LoginAsync(ApiFixture api, string slug, string email, string password)
    {
        using var client = api.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { tenantSlug = slug, email, password });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TokenDto>())!.AccessToken;
    }

    public static HttpClient Authenticated(ApiFixture api, string accessToken)
    {
        var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    public static async Task<HttpClient> PlatformClientAsync(ApiFixture api) =>
        Authenticated(api, await LoginAsync(api, "platform", ApiFixture.PlatformEmail, ApiFixture.PlatformPassword));

    public static async Task<TestTenant> NewTenantAsync(ApiFixture api, string? slug = null)
    {
        slug ??= $"gym-{Guid.NewGuid():N}"[..20];
        var ownerEmail = $"owner@{slug}.test";

        using var platform = await PlatformClientAsync(api);
        var response = await platform.PostAsJsonAsync("/api/v1/platform/tenants", new
        {
            slug,
            name = $"Gimnasio {slug}",
            timeZone = "America/Guayaquil",
            locale = "es-EC",
            ownerEmail,
            ownerFullName = "Dueño de Prueba",
            ownerPassword = OwnerPassword
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var tenant = (await response.Content.ReadFromJsonAsync<TenantDto>())!;

        var owner = Authenticated(api, await LoginAsync(api, slug, ownerEmail, OwnerPassword));
        return new TestTenant(tenant.Id, slug, ownerEmail, OwnerPassword, owner);
    }

    public static async Task<Guid> CreateBranchAsync(HttpClient owner, string code = "MAIN")
    {
        var response = await owner.PostAsJsonAsync("/api/v1/branches", new
        {
            code,
            name = $"Sucursal {code}",
            timeZone = "America/Guayaquil",
            sriEstablishmentCode = "001",
            address = "Av. Principal"
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<BranchDto>())!.Id;
    }

    public static async Task<Guid> RoleIdAsync(HttpClient client, string roleName)
    {
        var roles = await client.GetFromJsonAsync<RoleDto[]>("/api/v1/roles");
        return roles!.Single(r => r.Name == roleName).Id;
    }

    public static async Task<UserDto> CreateUserAsync(HttpClient actor, string email, string roleName, params Guid[] branchIds)
    {
        var roleId = await RoleIdAsync(actor, roleName);
        var response = await actor.PostAsJsonAsync("/api/v1/users", new
        {
            email,
            fullName = $"Usuario {email}",
            password = UserPassword,
            roleIds = new[] { roleId },
            branchIds
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<UserDto>())!;
    }

    public static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }
}
