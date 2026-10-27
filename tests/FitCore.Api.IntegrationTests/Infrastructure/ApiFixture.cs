using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Testcontainers.PostgreSql;

namespace FitCore.Api.IntegrationTests.Infrastructure;

/// <summary>
/// PostgreSQL real en contenedor + API en memoria. La API arranca como en producción: aplica las migraciones
/// con el usuario administrador y se conecta con el rol <c>fitcore_app</c> (sin superusuario, sujeto a RLS).
/// </summary>
public sealed class ApiFixture : IAsyncLifetime
{
    public const string AppRole = "fitcore_app";
    public const string AppPassword = "AppRolePw_test1";
    public const string PlatformEmail = "admin@fitcore.test";
    public const string PlatformPassword = "PlatformPass1234";
    public const string SigningKey = "test-signing-key-test-signing-key-0123456789";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17")
        .WithDatabase("fitcore")
        .WithUsername("fitcore")
        .WithPassword("AdminPw_test1")
        .Build();

    private WebApplicationFactory<Program>? _factory;

    public string AdminConnection => _container.GetConnectionString();

    public string AppConnection => new NpgsqlConnectionStringBuilder(AdminConnection)
    {
        Username = AppRole,
        Password = AppPassword,
        Pooling = false
    }.ConnectionString;

    public IServiceProvider Services => _factory!.Services;

    public HttpClient CreateClient() => _factory!.CreateClient();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Default", AppConnection);
            builder.UseSetting("ConnectionStrings:Migrations", AdminConnection);
            builder.UseSetting("Database:AutoMigrate", "true");
            builder.UseSetting("Database:AppRolePassword", AppPassword);
            builder.UseSetting("Jwt:SigningKey", SigningKey);
            builder.UseSetting("Platform:AdminEmail", PlatformEmail);
            builder.UseSetting("Platform:AdminPassword", PlatformPassword);
            builder.UseSetting("RateLimiting:LoginPermitPerMinute", "100000");
            builder.UseSetting("RateLimiting:SessionPermitPerMinute", "100000");
        });

        // Fuerza el arranque (migraciones, catálogo de permisos y administrador de plataforma).
        using var client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _factory?.Dispose();
        await _container.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFixture>
{
    public const string Name = "api";
}
