using FitCore.Application.Core;
using FitCore.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FitCore.Platform.Persistence;

/// <summary>Publica los eventos pendientes de la outbox a los <see cref="IOutboxEventHandler"/> registrados.</summary>
public sealed class OutboxProcessor(IServiceScopeFactory scopes, IClock clock, ILogger<OutboxProcessor> logger) : BackgroundService
{
    private const int BatchSize = 20;
    private const int MaxAttempts = 5;
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await ProcessBatchAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Falló el procesamiento de la outbox");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task<int> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var handlers = scope.ServiceProvider.GetServices<IOutboxEventHandler>().ToList();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // SKIP LOCKED permite varias instancias del Worker sin procesar dos veces el mismo mensaje.
        var messages = await db.OutboxMessages
            .FromSqlRaw(
                "SELECT * FROM messaging.outbox_messages WHERE processed_at IS NULL ORDER BY occurred_at LIMIT {0} FOR UPDATE SKIP LOCKED",
                BatchSize)
            .ToListAsync(cancellationToken);

        foreach (var message in messages)
        {
            try
            {
                if (message.TenantId is { } tenantId)
                {
                    await db.UseTenantAsync(tenantId, cancellationToken);
                }

                var matching = handlers.Where(h => h.EventType == message.Type).ToList();
                foreach (var handler in matching)
                {
                    await handler.HandleAsync(message.TenantId, message.Payload, cancellationToken);
                }

                message.ProcessedAt = clock.UtcNow;
                if (matching.Count == 0)
                {
                    logger.LogDebug("Evento {Type} sin handlers registrados", message.Type);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                message.Attempts++;
                message.Error = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
                if (message.Attempts >= MaxAttempts)
                {
                    message.ProcessedAt = clock.UtcNow;
                    logger.LogError(ex, "Evento {Type} descartado tras {Attempts} intentos", message.Type, message.Attempts);
                }
            }
            finally
            {
                await db.ClearTenantAsync(cancellationToken);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return messages.Count;
    }
}
