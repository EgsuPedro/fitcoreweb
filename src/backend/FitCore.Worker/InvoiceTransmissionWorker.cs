using FitCore.Invoicing.Application.Invoices;
using FitCore.Invoicing.Sri;
using Microsoft.Extensions.Options;

namespace FitCore.Worker;

/// <summary>
/// Transmisión de comprobantes electrónicos al SRI: cada pocos segundos reclama los comprobantes vencidos de la cola de cada gimnasio y los avanza (firma, envío, consulta de autorización) con
/// reintentos y espera creciente. El intervalo se configura con <c>Invoicing:Sri:IntervalSeconds</c> (15 por defecto, entre 5 y 300).
/// </summary>
internal sealed class InvoiceTransmissionWorker(InvoiceTransmissionRunner runner, IOptions<SriOptions> options, ILogger<InvoiceTransmissionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Clamp(options.Value.IntervalSeconds, 5, 300)));
        do
        {
            try
            {
                var summary = await runner.RunAsync(stoppingToken);
                if (summary.Processed > 0 || summary.Failed > 0)
                {
                    logger.LogInformation("Transmisión SRI: {Processed} avanzados, {Failed} por reintentar en {Tenants} gimnasios", summary.Processed, summary.Failed, summary.Tenants);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Falló la transmisión de comprobantes al SRI");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
