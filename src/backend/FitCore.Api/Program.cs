using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using FitCore.Api.Endpoints;
using FitCore.Api.Infrastructure;
using FitCore.Application.Core;
using FitCore.Commercial;
using FitCore.Analytics;
using FitCore.Invoicing;
using FitCore.Subscriptions;
using FitCore.Classes;
using FitCore.Wellness;
using FitCore.Infrastructure.Core;
using FitCore.Platform;
using FitCore.Platform.Persistence;
using FitCore.Platform.Security;
using FitCore.SharedKernel;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Serilog;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, configuration) =>
{
    configuration
        .ReadFrom.Configuration(context.Configuration)
        .Enrich.FromLogContext()
        .Enrich.WithProperty("Application", "FitCore.Api");

    if (context.HostingEnvironment.IsDevelopment())
    {
        configuration.WriteTo.Console(formatProvider: CultureInfo.InvariantCulture);
    }
    else
    {
        configuration.WriteTo.Console(new RenderedCompactJsonFormatter());
    }
});

// Detrás de un proxy (nginx) la IP real del cliente llega en X-Forwarded-For. Solo se confía en las redes declaradas en
// Network:TrustedProxies: si la API se expone directamente, esa cabecera la puede falsificar cualquiera.
var trustedProxies = builder.Configuration.GetSection("Network:TrustedProxies").Get<string[]>() ?? [];
if (trustedProxies.Length > 0)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = 1;
        options.KnownNetworks.Clear();
        options.KnownProxies.Clear();
        foreach (var cidr in trustedProxies)
        {
            var parts = cidr.Split('/');
            options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(
                IPAddress.Parse(parts[0]),
                int.Parse(parts[1], CultureInfo.InvariantCulture)));
        }
    });
}

builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddDispatcher();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();
builder.Services.AddScoped<IRequestInfo, HttpRequestInfo>();
// Almacén de archivos privados (fotos de progreso). La carpeta no se publica: los archivos solo salen por la API tras autorizar.
builder.Services.AddSmtpEmail(builder.Configuration);
builder.Services.AddLocalFileStore(builder.Configuration["FileStorage:RootPath"] is { Length: > 0 } filesRoot
    ? filesRoot
    : Path.Combine(AppContext.BaseDirectory, "data", "files"));
builder.Services.AddPlatformModule(builder.Configuration);
builder.Services.AddCommercialModule(builder.Configuration);
builder.Services.AddWellnessModule(builder.Configuration);
builder.Services.AddClassesModule(builder.Configuration);
builder.Services.AddAnalyticsModule(builder.Configuration);
builder.Services.AddSubscriptionsModule(builder.Configuration);
builder.Services.AddInvoicingModule(builder.Configuration);

builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddProblemDetails(options =>
    options.CustomizeProblemDetails = ctx =>
        ctx.ProblemDetails.Extensions["traceId"] = System.Diagnostics.Activity.Current?.Id ?? ctx.HttpContext.TraceIdentifier);
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

builder.Services.AddOpenApi("v1", options =>
    options.AddDocumentTransformer((document, _, _) =>
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT"
        };
        return Task.CompletedTask;
    }));

// Autenticación JWT: la clave viene de configuración (variables de entorno / gestor de secretos), nunca del código.
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<JwtOptions>>((bearer, jwt) =>
    {
        bearer.MapInboundClaims = false;
        bearer.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = jwt.Value.Issuer,
            ValidAudience = jwt.Value.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Value.SigningKey)),
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = ClaimNames.Name
        };
    });

// Todo endpoint exige sesión activa salvo que declare AllowAnonymous; los de negocio declaran su permiso.
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().AddRequirements(new ActiveUserRequirement()).Build())
    .SetDefaultPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().AddRequirements(new ActiveUserRequirement()).Build());
builder.Services.AddScoped<IAuthorizationHandler, PermissionHandler>();
builder.Services.AddScoped<IAuthorizationHandler, ActiveUserHandler>();
builder.Services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, SessionAwareAuthorizationResultHandler>();

// Límites por IP. Se separan porque el refresh se ejecuta en cada recarga de página y varias personas de un mismo
// gimnasio comparten IP (wifi de recepción); la defensa contra adivinar contraseñas es el bloqueo de cuenta de Identity.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    AddPerIpPolicy(options, RateLimitPolicies.Login, builder.Configuration.GetValue("RateLimiting:LoginPermitPerMinute", 20));
    AddPerIpPolicy(options, RateLimitPolicies.Webhooks, builder.Configuration.GetValue("RateLimiting:WebhookPermitPerMinute", 300));
    AddPerIpPolicy(options, RateLimitPolicies.Register, builder.Configuration.GetValue("RateLimiting:RegisterPermitPerMinute", 10));
    AddPerIpPolicy(options, RateLimitPolicies.Recovery, builder.Configuration.GetValue("RateLimiting:RecoveryPermitPerMinute", 10));
    AddPerIpPolicy(options, RateLimitPolicies.PublicRead, builder.Configuration.GetValue("RateLimiting:PublicReadPermitPerMinute", 120));
    AddPerIpPolicy(options, RateLimitPolicies.Session, builder.Configuration.GetValue("RateLimiting:SessionPermitPerMinute", 120));
});

// Idiomas: español (Ecuador) por defecto; el resto se agrega a esta lista.
var supportedCultures = new[] { new CultureInfo("es-EC"), new CultureInfo("es"), new CultureInfo("en") };
builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    options.DefaultRequestCulture = new RequestCulture("es-EC");
    options.SupportedCultures = supportedCultures;
    options.SupportedUICultures = supportedCultures;
});

var health = builder.Services.AddHealthChecks();
var connectionString = builder.Configuration.GetConnectionString("Default");
if (!string.IsNullOrWhiteSpace(connectionString))
{
    health.AddNpgSql(connectionString, name: "postgres", tags: ["ready"]);
}

var app = builder.Build();

if (trustedProxies.Length > 0)
{
    app.UseForwardedHeaders();
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseSerilogRequestLogging();
app.UseRequestLocalization();

if (app.Environment.IsDevelopment())
{
    // Interfaz Swagger solo en Development. Va antes de la autorización porque sirve archivos estáticos sin endpoint
    // (la política global exigiría sesión); el documento OpenAPI que consume se mapea más abajo. Autorizar con "Bearer <token>".
    app.UseSwaggerUI(options =>
    {
        options.RoutePrefix = "swagger";
        options.SwaggerEndpoint("/openapi/v1.json", "FIT360 v1");
        options.DocumentTitle = "FIT360 - API";
    });
}

app.UseRouting();
app.UseAuthentication();
app.UseMiddleware<TenantContextMiddleware>();
app.UseRateLimiter();
app.UseAuthorization();
app.UseMiddleware<BranchHeaderMiddleware>();
app.UseMiddleware<FeatureGateMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi("/openapi/{documentName}.json").AllowAnonymous();
}

app.MapHealthChecks("/health/live", new() { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new() { Predicate = check => check.Tags.Contains("ready") }).AllowAnonymous();

var v1 = app.MapGroup("/api/v1");
v1.MapSystemEndpoints();
v1.MapAuthEndpoints();
v1.MapTenantAndOrganizationEndpoints();
v1.MapAdministrationEndpoints();
v1.MapCommercialEndpoints();
v1.MapWellnessEndpoints();
v1.MapClassesEndpoints();
v1.MapAnalyticsEndpoints();
v1.MapReportsEndpoints();
v1.MapPlansEndpoints();
v1.MapPublicEndpoints();
v1.MapSubscriptionsEndpoints();
v1.MapBillingEndpoints();
v1.MapWebhookEndpoints();
v1.MapBrandingEndpoints();
v1.MapOnboardingEndpoints();
v1.MapInvitationEndpoints();
v1.MapContactRequestEndpoints();
v1.MapInvoicingSettingsEndpoints();
v1.MapOrganizationsEndpoints();
v1.MapMySubscriptionEndpoints();

await DatabaseInitializer.InitializeAsync(app.Services, app.Configuration);

// Despliegue: «--migrate-only» aplica las migraciones y los catálogos y termina, sin abrir el puerto. Es el paso controlado que corre antes de
// arrancar la API de producción (con las credenciales del propietario de la base); la API en ejecución no necesita esas credenciales.
if (args.Contains("--migrate-only", StringComparer.Ordinal))
{
    return;
}

await app.RunAsync();

static void AddPerIpPolicy(Microsoft.AspNetCore.RateLimiting.RateLimiterOptions options, string name, int permitPerMinute) =>
    options.AddPolicy(name, http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = permitPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));

// Necesario para WebApplicationFactory en las pruebas de integración.
public partial class Program;
