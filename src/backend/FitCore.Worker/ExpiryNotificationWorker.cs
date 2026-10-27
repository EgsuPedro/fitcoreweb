using FitCore.Commercial.Application.Notifications;

namespace FitCore.Worker;

/// <summary>
/// Genera y envía periódicamente los avisos por vencimiento de membresía (personal y alumnos). El intervalo se configura con
/// <c>Notifications:IntervalMinutes</c> (30 por defecto). Cada gimnasio decide si los activa; por defecto están apagados.
/// </summary>
internal sealed class ExpiryNotificationWorker(ExpiryNotificationRunner runner, IConfiguration configuration, ILogger<ExpiryNotificationWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var minutes = Math.Clamp(configuration.GetValue("Notifications:IntervalMinutes", 30), 1, 24 * 60);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(minutes));
        do
        {
            try
            {
                var summary = await runner.RunAsync(stoppingToken);
                if (summary.Created > 0 || summary.Sent > 0 || summary.Failed > 0)
                {
                    logger.LogInformation(
                        "Avisos de vencimiento: {Created} generados, {Sent} enviados, {Failed} con error en {Tenants} gimnasios",
                        summary.Created,
                        summary.Sent,
                        summary.Failed,
                        summary.Tenants);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Falló la ejecución de los avisos de vencimiento");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
