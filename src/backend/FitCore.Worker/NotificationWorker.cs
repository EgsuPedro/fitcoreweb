using FitCore.Classes.Application.Notifications;

namespace FitCore.Worker;

/// <summary>
/// Genera los recordatorios de las clases próximas y entrega los avisos pendientes por su canal, con reintentos. Los avisos nacidos de
/// eventos (reserva confirmada, cupo ofrecido, clase cancelada…) los crea la outbox, que también procesa este Worker. El intervalo se
/// configura con <c>Classes:NotificationIntervalSeconds</c> (60 por defecto, entre 10 y 3600).
/// </summary>
internal sealed class NotificationWorker(NotificationRunner runner, IConfiguration configuration, ILogger<NotificationWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seconds = Math.Clamp(configuration.GetValue("Classes:NotificationIntervalSeconds", 60), 10, 3600);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds));
        do
        {
            try
            {
                var summary = await runner.RunAsync(stoppingToken);
                if (summary.RemindersCreated > 0 || summary.Delivered > 0 || summary.Retrying > 0 || summary.Failed > 0)
                {
                    logger.LogInformation(
                        "Notificaciones: {Reminders} recordatorios, {Delivered} entregadas, {Retrying} por reintentar, {Failed} fallidas en {Tenants} gimnasios",
                        summary.RemindersCreated,
                        summary.Delivered,
                        summary.Retrying,
                        summary.Failed,
                        summary.Tenants);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Falló el procesamiento de notificaciones");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
