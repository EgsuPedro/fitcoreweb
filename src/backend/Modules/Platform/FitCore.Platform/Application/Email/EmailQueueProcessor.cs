using FitCore.Application.Core;
using FitCore.Infrastructure.Core;
using FitCore.Platform.Persistence;
using FitCore.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FitCore.Platform.Application.Email;

/// <summary>
/// Envía la cola de correo con reintentos espaciados (1 min, 5 min, 15 min, 1 h, 6 h) y un máximo de 5 intentos. Cada correo se
/// «reclama» con una sola sentencia (<c>FOR UPDATE SKIP LOCKED</c>): varias instancias del Worker no envían dos veces el mismo, y si un
/// Worker muere a mitad de envío el candado vence y otro lo retoma. Sin SMTP configurado no gasta intentos: los deja esperando.
/// </summary>
public sealed class EmailQueueProcessor(IServiceScopeFactory scopes, IClock clock, ILogger<EmailQueueProcessor> logger) : BackgroundService
{
    public const int MaxAttempts = 5;
    private const int BatchSize = 10;
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LockTime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SentRetention = TimeSpan.FromDays(30);
    private static readonly TimeSpan[] Backoff =
        [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromHours(1), TimeSpan.FromHours(6)];

    private DateTimeOffset _lastPurge = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await ProcessBatchAsync(stoppingToken);
                if (clock.UtcNow - _lastPurge > TimeSpan.FromHours(6))
                {
                    await PurgeAsync(stoppingToken);
                    _lastPurge = clock.UtcNow;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Falló el procesamiento de la cola de correo");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Procesa un lote y devuelve cuántos correos se intentaron.</summary>
    public async Task<int> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<IEmailSender>();
        if (!sender.IsConfigured)
        {
            logger.LogWarning("Hay correos en cola pero el SMTP no está configurado (Email:Smtp)");
            return 0;
        }

        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var now = clock.UtcNow;

        // Un envío que quedó a medias (el Worker murió) y ya agotó sus intentos no se reintenta: se marca como fallido.
        await db.Database.ExecuteSqlAsync($"""
            UPDATE messaging.emails
            SET status = 'Failed', text_body = NULL, html_body = NULL, locked_until = NULL,
                last_error = 'Se agotaron los intentos (el envío quedó interrumpido).'
            WHERE status = 'Sending' AND locked_until < {now} AND attempts >= {MaxAttempts}
            """, cancellationToken);

        var ids = await db.Database.SqlQuery<Guid>($"""
            UPDATE messaging.emails SET status = 'Sending', attempts = attempts + 1, locked_until = {now + LockTime}
            WHERE id IN (
                SELECT id FROM messaging.emails
                WHERE (status = 'Pending' AND next_attempt_at <= {now}) OR (status = 'Sending' AND locked_until < {now})
                ORDER BY next_attempt_at
                LIMIT {BatchSize}
                FOR UPDATE SKIP LOCKED)
            RETURNING id AS "Value"
            """).ToListAsync(cancellationToken);

        foreach (var id in ids)
        {
            var email = await db.QueuedEmails.FirstAsync(e => e.Id == id, cancellationToken);
            var result = await sender.SendAsync(
                new EmailMessage(email.ToAddress, email.Subject, email.TextBody ?? string.Empty, email.HtmlBody), cancellationToken);
            Complete(email, result);
            await db.SaveChangesAsync(cancellationToken);
        }

        return ids.Count;
    }

    private void Complete(QueuedEmail email, Result result)
    {
        var now = clock.UtcNow;
        email.LockedUntil = null;
        if (result.IsSuccess)
        {
            email.Status = EmailStatus.Sent;
            email.SentAt = now;
            email.LastError = null;
            Purge(email);
            return;
        }

        email.LastError = result.Error.Code;
        if (email.Attempts >= MaxAttempts || result.Error.Type == ErrorType.Validation)
        {
            // Un destinatario o remitente inválido no mejora al reintentar.
            email.Status = EmailStatus.Failed;
            Purge(email);
            logger.LogError("Correo {Id} ({Template}) a {Recipient} falló definitivamente: {Error}",
                email.Id, email.Template, SmtpEmailSender.Mask(email.ToAddress), result.Error.Code);
            return;
        }

        email.Status = EmailStatus.Pending;
        email.NextAttemptAt = now + Backoff[Math.Min(email.Attempts - 1, Backoff.Length - 1)];
    }

    // Los cuerpos llevan enlaces de un solo uso: no se conservan una vez terminado el envío.
    private static void Purge(QueuedEmail email)
    {
        email.TextBody = null;
        email.HtmlBody = null;
    }

    private async Task PurgeAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var limit = clock.UtcNow - SentRetention;
        var deleted = await db.QueuedEmails
            .Where(e => (e.Status == EmailStatus.Sent || e.Status == EmailStatus.Failed) && e.CreatedAt < limit)
            .ExecuteDeleteAsync(cancellationToken);
        var tokens = await scope.ServiceProvider.GetRequiredService<AccountTokens.AccountTokenService>()
            .PurgeAsync(TimeSpan.FromDays(7), cancellationToken);
        if (deleted + tokens > 0)
        {
            logger.LogInformation("Limpieza: {Emails} correos y {Tokens} tokens antiguos eliminados", deleted, tokens);
        }
    }
}
