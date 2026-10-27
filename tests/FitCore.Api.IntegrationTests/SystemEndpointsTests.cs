using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace FitCore.Api.IntegrationTests;

/// <summary>Pruebas sin base de datos: solo el arranque de la API y sus endpoints públicos.</summary>
public class SystemEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public SystemEndpointsTests(WebApplicationFactory<Program> factory) =>
        _client = factory
            .WithWebHostBuilder(builder => builder.UseSetting("Jwt:SigningKey", "test-signing-key-test-signing-key-0123456789"))
            .CreateClient();

    [Fact]
    public async Task Health_live_responde_200()
    {
        var response = await _client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task System_info_devuelve_nombre_y_fecha_utc()
    {
        var response = await _client.GetAsync("/api/v1/system/info");

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.NotNull(body);
        Assert.Equal("FitCore 360", body["name"].ToString());
        Assert.True(body.ContainsKey("utcNow"));
    }

    [Fact]
    public async Task Ruta_inexistente_sin_sesion_responde_401_y_no_revela_que_rutas_existen()
    {
        // La política global exige sesión incluso para rutas que no existen: no se puede enumerar la API sin autenticarse.
        var response = await _client.GetAsync("/api/v1/no-existe");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Endpoint_protegido_sin_token_devuelve_401_problem_details()
    {
        var response = await _client.GetAsync("/api/v1/branches");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Login_limita_los_intentos_por_IP_pero_refresh_tiene_un_limite_aparte()
    {
        // Sin base de datos: las primeras llamadas llegan al handler y fallan (500); lo que importa es el 429 del limitador.
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Jwt:SigningKey", "test-signing-key-test-signing-key-0123456789");
            builder.UseSetting("RateLimiting:LoginPermitPerMinute", "3");
            builder.UseSetting("RateLimiting:SessionPermitPerMinute", "50");
        });
        using var client = factory.CreateClient();
        var login = new { tenantSlug = "x", email = "a@b.test", password = "x" };

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            statuses.Add((await client.PostAsJsonAsync("/api/v1/auth/login", login)).StatusCode);
        }

        Assert.Equal(2, statuses.Count(s => s == HttpStatusCode.TooManyRequests));
        Assert.All(statuses.Take(3), s => Assert.NotEqual(HttpStatusCode.TooManyRequests, s));

        // El refresh (cada recarga de página) no comparte el contador del login.
        var refresh = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = "basura" });
        Assert.NotEqual(HttpStatusCode.TooManyRequests, refresh.StatusCode);
    }

    [Fact]
    public void Arranque_falla_si_la_clave_jwt_es_corta()
    {
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseSetting("Jwt:SigningKey", "corta"));

        Assert.ThrowsAny<Exception>(() => factory.CreateClient());
    }
}
