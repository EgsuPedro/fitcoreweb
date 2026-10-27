using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace FitCore.Api.IntegrationTests;

public class SystemEndpointsTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client = factory.CreateClient();

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
    public async Task Ruta_inexistente_devuelve_problem_details()
    {
        var response = await _client.GetAsync("/api/v1/no-existe");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }
}

