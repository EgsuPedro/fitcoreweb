using System.Globalization;
using FitCore.Application.Core;
using FitCore.Classes;
using FitCore.Commercial;
using FitCore.Infrastructure.Core;
using FitCore.Invoicing;
using FitCore.Platform;
using FitCore.Platform.Persistence;
using FitCore.SharedKernel;
using FitCore.Subscriptions;
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
builder.Services.AddEmailQueue(builder.Configuration);
builder.Services.AddSmtpEmail(builder.Configuration);
// El RIDE lleva el logo del gimnasio: el Worker lee la misma carpeta de archivos privados que la API (mismo volumen).
builder.Services.AddScoped<ITenantBranding, FitCore.Platform.Application.Branding.TenantBrandingProvider>();
builder.Services.AddLocalFileStore(builder.Configuration["FileStorage:RootPath"] is { Length: > 0 } filesRoot
    ? filesRoot
    : Path.Combine(AppContext.BaseDirectory, "data", "files"));
builder.Services.AddCommercialPersistence(builder.Configuration);
builder.Services.AddWhatsAppNotifications(builder.Configuration);
builder.Services.AddClassesPersistence(builder.Configuration);
builder.Services.AddWaitlistMaintenance();
builder.Services.AddClassNotifications();
builder.Services.AddSubscriptionsPersistence(builder.Configuration);
builder.Services.AddInvoicingPersistence(builder.Configuration);

// Jobs: la outbox publica los eventos de dominio. Los siguientes (cuotas recurrentes, vencimientos,
// recordatorios) se registran aquí por módulo cuando existan.
builder.Services.AddHostedService<OutboxProcessor>();
builder.Services.AddHostedService<FitCore.Platform.Application.Email.EmailQueueProcessor>();
builder.Services.AddHostedService<ExpiryNotificationWorker>();
builder.Services.AddHostedService<WaitlistWorker>();
builder.Services.AddHostedService<NotificationWorker>();
builder.Services.AddHostedService<SubscriptionWorker>();
builder.Services.AddHostedService<InvoiceTransmissionWorker>();
builder.Services.AddHostedService<InvoiceEmailWorker>();
builder.Services.AddHostedService<HeartbeatService>();

await builder.Build().RunAsync();
