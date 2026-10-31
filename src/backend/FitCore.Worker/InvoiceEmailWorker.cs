using FitCore.Invoicing.Application.Invoices;

namespace FitCore.Worker;

/// <summary>
/// Envío por correo de los comprobantes autorizados (XML autorizado y RIDE adjuntos): cada pocos segundos reclama los envíos vencidos de la cola de cada gimnasio y los entrega, con reintentos
/// y espera creciente. Sin SMTP configurado no hace nada y no gasta intentos.
/// </summary>
internal sealed class InvoiceEmailWorker(InvoiceEmailRunner runner, ILogger<InvoiceEmailWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                var sent = await runner.RunAsync(stoppingToken);
                if (sent > 0)
                {
                    logger.LogInformation("Correos de comprobantes: {Count} procesados", sent);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Falló el envío de correos de comprobantes");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
