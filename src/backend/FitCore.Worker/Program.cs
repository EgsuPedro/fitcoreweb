using System.Globalization;
using FitCore.Application.Core;
using FitCore.Classes;
using FitCore.Commercial;
using FitCore.Infrastructure.Core;
using FitCore.Platform;
using FitCore.Platform.Persistence;
using FitCore.SharedKernel;
using FitCore.Worker;
using Serilog;
using Serilog.Formatting.Compact;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSerilog((services, configuration) =>
{
    configuration
        .ReadFrom.Configuration(builder.Configuration)
        .Enrich.FromLogContext()
        .Enrich.WithProperty("Application", "FitCore.Worker");

    if (builder.Environment.IsDevelopment())
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
builder.Services.AddPlatformPersistence(builder.Configuration);
builder.Services.AddCommercialPersistence(builder.Configuration);
builder.Services.AddWhatsAppNotifications(builder.Configuration);
builder.Services.AddClassesPersistence(builder.Configuration);
builder.Services.AddWaitlistMaintenance();

// Jobs: la outbox publica los eventos de dominio. Los siguientes (cuotas recurrentes, vencimientos,
// recordatorios) se registran aquí por módulo cuando existan.
builder.Services.AddHostedService<OutboxProcessor>();
builder.Services.AddHostedService<ExpiryNotificationWorker>();
builder.Services.AddHostedService<WaitlistWorker>();
builder.Services.AddHostedService<HeartbeatService>();

await builder.Build().RunAsync();
