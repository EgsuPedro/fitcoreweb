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
    public async Task Swagger_UI_y_documento_OpenAPI_estan_disponibles_en_Development()
    {
        var ui = await _client.GetAsync("/swagger/index.html");
        var document = await _client.GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, ui.StatusCode);
        Assert.Equal(HttpStatusCode.OK, document.StatusCode);
        Assert.Contains("/api/v1/auth/login", await document.Content.ReadAsStringAsync(), StringComparison.Ordinal);
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

    [Theory]
    [InlineData("{\"tenantSlug\": ")]
    [InlineData("no es json")]
    public async Task Un_cuerpo_ilegible_responde_400_y_no_500(string body)
    {
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");

        var response = await _client.PostAsync("/api/v1/auth/login", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Un_cuerpo_con_bytes_UTF8_invalidos_responde_400_y_no_500()
    {
        using var content = new ByteArrayContent([0x7B, 0x22, 0x74, 0x65, 0x6E, 0x61, 0x6E, 0x74, 0x53, 0x6C, 0x75, 0x67, 0x22, 0x3A, 0x22, 0xE1, 0x22, 0x7D]);
        content.Headers.ContentType = new("application/json");

        var response = await _client.PostAsync("/api/v1/auth/login", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
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
