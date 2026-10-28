using FitCore.Classes.Application.Reservations;

namespace FitCore.Worker;

/// <summary>
/// Vence las invitaciones de la lista de espera que nadie respondió a tiempo y ofrece el cupo al siguiente de la fila. El intervalo se
/// configura con <c>Classes:WaitlistIntervalSeconds</c> (30 por defecto, entre 5 y 3600): los plazos de las invitaciones son de minutos.
/// </summary>
internal sealed class WaitlistWorker(WaitlistRunner runner, IConfiguration configuration, ILogger<WaitlistWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seconds = Math.Clamp(configuration.GetValue("Classes:WaitlistIntervalSeconds", 30), 5, 3600);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds));
        do
        {
            try
            {
                var summary = await runner.RunAsync(stoppingToken);
                if (summary.OffersExpired > 0 || summary.SeatsOffered > 0 || summary.WaitlistsClosed > 0)
                {
                    logger.LogInformation(
                        "Lista de espera: {Expired} invitaciones vencidas, {Offered} cupos ofrecidos, {Closed} reservas cerradas en {Tenants} gimnasios",
                        summary.OffersExpired,
                        summary.SeatsOffered,
                        summary.WaitlistsClosed,
                        summary.Tenants);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Falló el mantenimiento de la lista de espera");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
