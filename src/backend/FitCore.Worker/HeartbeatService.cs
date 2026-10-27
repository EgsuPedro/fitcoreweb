using FitCore.SharedKernel;

namespace FitCore.Worker;

/// <summary>Señal de vida del Worker. Se reemplaza por los jobs reales a partir de la Fase 2.</summary>
internal sealed class HeartbeatService(ILogger<HeartbeatService> logger, IClock clock) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            logger.LogInformation("Worker activo a las {UtcNow:O}", clock.UtcNow);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
