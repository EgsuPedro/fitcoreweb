using System.Net;
using System.Net.Http.Json;
using FitCore.Api.IntegrationTests.Infrastructure;

namespace FitCore.Api.IntegrationTests;

[Collection(ApiCollection1.Name)]
public class AuthTests(ApiFixture api)
{
    private static object Login(TestTenant tenant, string? password = null) =>
        new { tenantSlug = tenant.Slug, email = tenant.OwnerEmail, password = password ?? tenant.OwnerPassword };

    [Fact]
    public async Task Login_correcto_devuelve_access_y_refresh_y_me_refleja_al_usuario()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        using var client = api.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/login", Login(tenant));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tokens = (await response.Content.ReadFromJsonAsync<TokenDto>())!;
        Assert.False(string.IsNullOrWhiteSpace(tokens.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(tokens.RefreshToken));
        Assert.Equal(900, tokens.ExpiresIn);

        using var authed = TestApi.Authenticated(api, tokens.AccessToken);
        var me = await authed.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/v1/auth/me");
        Assert.Equal(tenant.Slug, me.GetProperty("tenantSlug").GetString());
        Assert.True(me.GetProperty("allBranches").GetBoolean());
        Assert.Contains("branches.create", me.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));
    }

    [Fact]
    public async Task Credenciales_invalidas_no_revelan_si_existe_el_usuario_ni_el_gimnasio()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        using var client = api.CreateClient();

        var badPassword = await client.PostAsJsonAsync("/api/v1/auth/login", Login(tenant, "Incorrecta12345"));
        var unknownUser = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { tenantSlug = tenant.Slug, email = "nadie@nada.test", password = "Incorrecta12345" });
        var unknownTenant = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { tenantSlug = "no-existe-gym", email = tenant.OwnerEmail, password = tenant.OwnerPassword });

        foreach (var response in new[] { badPassword, unknownUser, unknownTenant })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("auth.invalid_credentials", await TestApi.ProblemCodeAsync(response));
        }
    }

    [Fact]
    public async Task Cinco_intentos_fallidos_bloquean_la_cuenta_incluso_con_la_clave_correcta()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        using var client = api.CreateClient();

        for (var i = 0; i < 5; i++)
        {
            await client.PostAsJsonAsync("/api/v1/auth/login", Login(tenant, "Incorrecta12345"));
        }

        var blocked = await client.PostAsJsonAsync("/api/v1/auth/login", Login(tenant));

        Assert.Equal(HttpStatusCode.Unauthorized, blocked.StatusCode);
        Assert.Equal("auth.locked_out", await TestApi.ProblemCodeAsync(blocked));
    }

    [Fact]
    public async Task Refresh_rota_el_token_y_reusar_el_anterior_revoca_toda_la_familia()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        using var client = api.CreateClient();
        var first = (await (await client.PostAsJsonAsync("/api/v1/auth/login", Login(tenant))).Content.ReadFromJsonAsync<TokenDto>())!;

        var rotated = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = first.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        var second = (await rotated.Content.ReadFromJsonAsync<TokenDto>())!;
        Assert.NotEqual(first.RefreshToken, second.RefreshToken);

        // Reutilizar el token ya rotado indica robo: se rechaza...
        var reuse = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = first.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);
        Assert.Equal("auth.refresh_reused", await TestApi.ProblemCodeAsync(reuse));

        // ...y también deja de servir el token nuevo de la misma familia.
        var descendant = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = second.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, descendant.StatusCode);
    }

    [Fact]
    public async Task Logout_revoca_la_sesion()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        using var client = api.CreateClient();
        var tokens = (await (await client.PostAsJsonAsync("/api/v1/auth/login", Login(tenant))).Content.ReadFromJsonAsync<TokenDto>())!;

        var logout = await client.PostAsJsonAsync("/api/v1/auth/logout", new { refreshToken = tokens.RefreshToken });
        var refresh = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = tokens.RefreshToken });

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    [Fact]
    public async Task Refresh_con_token_manipulado_o_de_otro_tenant_se_rechaza()
    {
        var a = await TestApi.NewTenantAsync(api);
        var b = await TestApi.NewTenantAsync(api);
        using var client = api.CreateClient();
        var tokens = (await (await client.PostAsJsonAsync("/api/v1/auth/login", Login(a))).Content.ReadFromJsonAsync<TokenDto>())!;

        // Cambiar el prefijo de tenant: el secreto no existe en el otro gimnasio (RLS lo oculta).
        var refreshToken = tokens.RefreshToken!;
        var secret = refreshToken[(refreshToken.IndexOf('.', StringComparison.Ordinal) + 1)..];
        var forged = $"{b.Id:N}.{secret}";

        foreach (var token in new[] { forged, "basura", $"{a.Id:N}.", "" })
        {
            var response = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = token });
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task Modo_cookie_envia_el_refresh_en_cookie_HttpOnly_y_no_en_el_cuerpo()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        using var client = api.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { tenantSlug = tenant.Slug, email = tenant.OwnerEmail, password = tenant.OwnerPassword, useCookie = true });

        var tokens = (await response.Content.ReadFromJsonAsync<TokenDto>())!;
        Assert.Null(tokens.RefreshToken);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith("fc_refresh=", StringComparison.Ordinal));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/api/v1/auth", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Gimnasio_suspendido_no_puede_iniciar_sesion_y_su_token_deja_de_servir()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        using var platform = await TestApi.PlatformClientAsync(api);

        Assert.Equal(HttpStatusCode.OK, (await tenant.Owner.GetAsync("/api/v1/branches")).StatusCode);

        var suspend = await platform.PostAsync($"/api/v1/platform/tenants/{tenant.Id}/suspend", null);
        Assert.Equal(HttpStatusCode.OK, suspend.StatusCode);

        using var client = api.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", Login(tenant));
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await tenant.Owner.GetAsync("/api/v1/branches")).StatusCode);

        await platform.PostAsync($"/api/v1/platform/tenants/{tenant.Id}/activate", null);
        Assert.Equal(HttpStatusCode.OK, (await tenant.Owner.GetAsync("/api/v1/branches")).StatusCode);
    }

    [Fact]
    public async Task Desactivar_un_usuario_revoca_sus_sesiones_y_bloquea_su_token()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var branch = await TestApi.CreateBranchAsync(tenant.Owner);
        var user = await TestApi.CreateUserAsync(tenant.Owner, "recepcion@gym.test", "Recepcion", branch);

        using var client = api.CreateClient();
        var tokens = (await (await client.PostAsJsonAsync("/api/v1/auth/login",
            new { tenantSlug = tenant.Slug, email = user.Email, password = TestApi.UserPassword })).Content.ReadFromJsonAsync<TokenDto>())!;
        using var userClient = TestApi.Authenticated(api, tokens.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await userClient.GetAsync("/api/v1/branches")).StatusCode);

        var deactivate = await tenant.Owner.PutAsJsonAsync($"/api/v1/users/{user.Id}/status", new { isActive = false });
        Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await userClient.GetAsync("/api/v1/branches")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = tokens.RefreshToken })).StatusCode);
    }

    [Fact]
    public async Task Cambiar_la_clave_revoca_todas_las_sesiones()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        using var client = api.CreateClient();
        var tokens = (await (await client.PostAsJsonAsync("/api/v1/auth/login", Login(tenant))).Content.ReadFromJsonAsync<TokenDto>())!;

        var change = await tenant.Owner.PostAsJsonAsync("/api/v1/auth/change-password",
            new { currentPassword = tenant.OwnerPassword, newPassword = "NuevaClave12345" });

        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = tokens.RefreshToken })).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsJsonAsync("/api/v1/auth/login",
                new { tenantSlug = tenant.Slug, email = tenant.OwnerEmail, password = "NuevaClave12345" })).StatusCode);
    }

    [Fact]
    public async Task Refresh_concurrente_con_el_mismo_token_solo_tiene_un_ganador()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        using var client = api.CreateClient();
        var tokens = (await (await client.PostAsJsonAsync("/api/v1/auth/login", Login(tenant))).Content.ReadFromJsonAsync<TokenDto>())!;

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = tokens.RefreshToken })));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.OK), r => Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode));
    }
}
