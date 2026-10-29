using FitCore.Subscriptions.Application.Subscriptions;
using Microsoft.Extensions.Options;

namespace FitCore.Worker;

/// <summary>
/// Revisa los vencimientos de las suscripciones comerciales: pruebas terminadas, cancelaciones programadas, renovación automática de planes gratuitos,
/// paso a mora y fin de la gracia. El intervalo es <c>Subscriptions:LifecycleIntervalMinutes</c> (15 por defecto, entre 1 y 1440). No cobra nada.
/// </summary>
internal sealed class SubscriptionWorker(SubscriptionLifecycleRunner runner, IOptions<SubscriptionOptions> options, ILogger<SubscriptionWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var minutes = Math.Clamp(options.Value.LifecycleIntervalMinutes, 1, 1440);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(minutes));
        do
        {
            try
            {
                var summary = await runner.RunAsync(stoppingToken);
                if (summary.Examined > 0)
                {
                    logger.LogInformation(
                        "Suscripciones: {Examined} revisadas, {Trials} pruebas vencidas, {Free} renovaciones gratuitas, {Cancelled} canceladas, {PastDue} en mora, {Grace} con la gracia terminada, {Failed} con error, {Invoices} facturas emitidas",
                        summary.Examined, summary.TrialsExpired, summary.FreeRenewed, summary.Cancelled, summary.MarkedPastDue, summary.GraceExpired, summary.Failed, summary.InvoicesIssued);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Falló el proceso de vencimientos de suscripciones");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
