using Microsoft.Extensions.DependencyInjection;
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
    public const string WebhookSecret = "whsec-test-secret-0123456789abcdef";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17")
        .WithDatabase("fitcore")
        .WithUsername("fitcore")
        .WithPassword("AdminPw_test1")
        .Build();

    private WebApplicationFactory<Program>? _factory;

    /// <summary>Carpeta del almacén privado de archivos de esta API (una por colección de pruebas); se borra al terminar.</summary>
    public string FilesRoot { get; } = Path.Combine(Path.GetTempPath(), "fitcore-tests-files-" + Guid.NewGuid().ToString("N"));

    public string AdminConnection => _container.GetConnectionString();

    public string AppConnection => new NpgsqlConnectionStringBuilder(AdminConnection)
    {
        Username = AppRole,
        Password = AppPassword,
        // Con reutilización: la suite abre miles de conexiones cortas y, sin ella, Windows agota los puertos efímeros (error 10048).
        // El grupo reinicia la sesión al devolver cada conexión (DISCARD ALL), así que app.tenant_id no se filtra entre pruebas.
        Pooling = true,
        MaxPoolSize = 30
    }.ConnectionString;

    public IServiceProvider Services => _factory!.Services;

    public HttpClient CreateClient() => _factory!.CreateClient();

    /// <summary>Un cliente de una segunda instancia de la API (misma base) con otra configuración: sirve para probar límites configurables.</summary>
    public HttpClient CreateClientWith(params (string Key, string Value)[] settings) =>
        _factory!.WithWebHostBuilder(builder =>
        {
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }
        }).CreateClient();

    /// <summary>Un cliente de otra instancia de la API (misma base) con servicios sustituidos: sirve para inyectar fallos que no se pueden provocar con datos.</summary>
    public HttpClient CreateClientWithServices(Action<IServiceCollection> configure) =>
        _factory!.WithWebHostBuilder(builder => builder.ConfigureServices(configure)).CreateClient();

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
            builder.UseSetting("RateLimiting:WebhookPermitPerMinute", "100000");
            builder.UseSetting("Subscriptions:Payments:ProviderCode", "testpay");
            builder.UseSetting("Subscriptions:Payments:WebhookSecret", WebhookSecret);
            builder.UseSetting("RateLimiting:SessionPermitPerMinute", "100000");
            builder.UseSetting("RateLimiting:PublicReadPermitPerMinute", "100000");
            builder.UseSetting("RateLimiting:RegisterPermitPerMinute", "100000");
            builder.UseSetting("RateLimiting:RecoveryPermitPerMinute", "100000");
            builder.UseSetting("Email:Smtp:Host", "smtp.test.invalid");
            builder.UseSetting("Email:Smtp:From", "no-reply@fit360.test");
            builder.UseSetting("PublicSite:BaseUrl", "https://fit360.test");
            builder.UseSetting("FileStorage:RootPath", FilesRoot);
        });

        // Fuerza el arranque (migraciones, catálogo de permisos y administrador de plataforma).
        using var client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _factory?.Dispose();
        await _container.DisposeAsync();
        if (Directory.Exists(FilesRoot))
        {
            Directory.Delete(FilesRoot, recursive: true);
        }
    }
}

// Cada colección levanta su propio PostgreSQL + API y las colecciones corren en paralelo (xUnit): la suite pasa de ~10 min a
// una fracción. Las pruebas crean sus propios gimnasios (slugs únicos), así que no comparten estado; los grupos reparten la carga.
[CollectionDefinition(Name)]
public sealed class ApiCollection1 : ICollectionFixture<ApiFixture>
{
    public const string Name = "api-1";
}

[CollectionDefinition(Name)]
public sealed class ApiCollection2 : ICollectionFixture<ApiFixture>
{
    public const string Name = "api-2";
}

[CollectionDefinition(Name)]
public sealed class ApiCollection3 : ICollectionFixture<ApiFixture>
{
    public const string Name = "api-3";
}

[CollectionDefinition(Name)]
public sealed class ApiCollection4 : ICollectionFixture<ApiFixture>
{
    public const string Name = "api-4";
}
