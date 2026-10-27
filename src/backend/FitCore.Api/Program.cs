using System.Globalization;
using FitCore.Api.Endpoints;
using FitCore.Api.Infrastructure;
using FitCore.Application.Core;
using FitCore.Infrastructure.Core;
using FitCore.SharedKernel;
using Microsoft.AspNetCore.Localization;
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

builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddDispatcher();

builder.Services.AddProblemDetails(options =>
    options.CustomizeProblemDetails = ctx =>
        ctx.ProblemDetails.Extensions["traceId"] = System.Diagnostics.Activity.Current?.Id ?? ctx.HttpContext.TraceIdentifier);
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

builder.Services.AddOpenApi("v1");

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

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseSerilogRequestLogging();
app.UseRequestLocalization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi("/openapi/{documentName}.json");
}

app.MapHealthChecks("/health/live", new() { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new() { Predicate = check => check.Tags.Contains("ready") });

var v1 = app.MapGroup("/api/v1");
v1.MapSystemEndpoints();

await app.RunAsync();

// Necesario para WebApplicationFactory en las pruebas de integración.
public partial class Program;
