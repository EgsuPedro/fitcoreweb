using FitCore.Application.Core;
using FitCore.Classes.Domain;
using FitCore.Classes.Persistence;
using FitCore.Platform.Domain;
using FitCore.Platform.Persistence;
using FitCore.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FitCore.Classes.Application.Notifications;

/// <param name="RemindersCreated">Recordatorios de clase nuevos.</param>
/// <param name="Delivered">Avisos entregados por un canal externo en esta pasada.</param>
/// <param name="Retrying">Avisos que fallaron de forma transitoria y se reintentarán.</param>
/// <param name="Failed">Avisos que ya no se reintentan (fallo definitivo o intentos agotados).</param>
public sealed record NotificationRunSummary(int Tenants, int RemindersCreated, int Delivered, int Retrying, int Failed)
{
    public static readonly NotificationRunSummary Empty = new(0, 0, 0, 0, 0);

    public NotificationRunSummary Add(NotificationRunSummary other) =>
        new(Tenants + other.Tenants, RemindersCreated + other.RemindersCreated, Delivered + other.Delivered, Retrying + other.Retrying, Failed + other.Failed);
}

/// <summary>
/// Trabajo periódico de las notificaciones (lo ejecuta el Worker): genera los recordatorios de las clases próximas y entrega por su canal
/// los avisos pendientes, con reintentos y espera creciente ante fallos transitorios. Los avisos nacidos de eventos no pasan por aquí: los
/// crea la outbox. Cada gimnasio se procesa por separado y un fallo en uno no frena a los demás.
/// </summary>
public sealed class NotificationRunner(
    IServiceScopeFactory scopes,
    IClock clock,
    IEnumerable<INotificationChannel> channels,
    IConfiguration configuration,
    ILogger<NotificationRunner> logger)
{
    private const int BatchSize = 200;
    private const int DeliveryBatchSize = 50;

    /// <summary>Cuánto antes de empezar la clase se recuerda (<c>Classes:ReminderLeadMinutes</c>, 180 por defecto).</summary>
    private TimeSpan ReminderLead => TimeSpan.FromMinutes(Math.Clamp(configuration.GetValue("Classes:ReminderLeadMinutes", 180), 15, 24 * 60));

    public async Task<NotificationRunSummary> RunAsync(CancellationToken cancellationToken)
    {
        List<Guid> tenants;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var platform = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            tenants = await platform.Tenants.AsNoTracking()
                .Where(t => t.Status == TenantStatus.Active && t.Slug != Tenant.PlatformSlug)
                .Select(t => t.Id)
                .ToListAsync(cancellationToken);
        }

        var total = NotificationRunSummary.Empty;
        foreach (var tenantId in tenants)
        {
            try
            {
                var reminders = await CreateRemindersAsync(tenantId, cancellationToken);
                total = total.Add(reminders).Add(await DeliverPendingAsync(tenantId, [.. channels], cancellationToken));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Falló el procesamiento de notificaciones del gimnasio {TenantId}", tenantId);
            }
        }

        return total with { Tenants = tenants.Count };
    }

    /// <summary>Recordatorio para cada reserva confirmada de una sesión que empieza dentro del plazo. Uno por reserva, aunque el trabajo se repita.</summary>
    public async Task<NotificationRunSummary> CreateRemindersAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<ClassesDbContext>();
        var now = clock.UtcNow;
        var until = now + ReminderLead;

        var due = await (from r in db.Reservations.AsNoTracking()
                         join s in db.ClassSessions.AsNoTracking() on r.SessionId equals s.Id
                         where r.Status == ReservationStatus.Confirmed && s.Status == SessionStatus.Scheduled && s.StartsAt > now && s.StartsAt <= until
                               && !db.Notifications.Any(n => n.DedupeKey == "reminder:" + r.Id.ToString())
                         orderby s.StartsAt
                         select new { ReservationId = r.Id, r.MemberId, r.SessionId })
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        var facts = new Dictionary<Guid, SessionFacts?>();
        var created = 0;
        foreach (var item in due)
        {
            if (!facts.TryGetValue(item.SessionId, out var session))
            {
                session = await SessionFacts.LoadAsync(db, item.SessionId, cancellationToken);
                facts[item.SessionId] = session;
            }

            var userId = await db.MemberRefs.AsNoTracking().Where(m => m.Id == item.MemberId).Select(m => m.UserId).FirstOrDefaultAsync(cancellationToken);
            if (session is null || userId is null)
            {
                continue;
            }

            var made = await NotificationWriter.TryCreateAsync(
                db,
                new NewNotification(
                    tenantId,
                    userId.Value,
                    session.Session.BranchId,
                    NotificationType.ClassReminder,
                    "Tu clase empieza pronto",
                    $"{session.ClassName} empieza a las {NotificationText.Time(session.Session.StartsAt, session.Zone)} en {session.BranchName}.",
                    new { sessionId = session.Session.Id, reservationId = item.ReservationId },
                    $"reminder:{item.ReservationId}"),
                now,
                cancellationToken);
            if (made)
            {
                created++;
            }
        }

        return NotificationRunSummary.Empty with { RemindersCreated = created };
    }

    /// <summary>
    /// Entrega por su canal los avisos pendientes cuyo momento llegó. Un fallo transitorio se reintenta con espera creciente hasta agotar los
    /// intentos; uno definitivo, o un canal que no está configurado, deja el aviso como fallido. El error que se guarda es solo un código.
    /// </summary>
    public async Task<NotificationRunSummary> DeliverPendingAsync(Guid tenantId, IReadOnlyCollection<INotificationChannel> available, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<ClassesDbContext>();
        var now = clock.UtcNow;

        // SKIP LOCKED permite varias instancias del Worker sin entregar dos veces el mismo aviso.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var pending = await db.Notifications
            .FromSqlInterpolated($"SELECT *, xmin FROM classes.inbox_notifications WHERE status = 'Pending' AND next_attempt_at <= {now} ORDER BY next_attempt_at LIMIT {DeliveryBatchSize} FOR UPDATE SKIP LOCKED")
            .ToListAsync(cancellationToken);

        int delivered = 0, retrying = 0, failed = 0;
        foreach (var notification in pending)
        {
            var channel = available.FirstOrDefault(c => c.IsConfigured && c.Name == notification.Channel);
            if (channel is null)
            {
                notification.RecordFailure("channel_unavailable", transient: false, now);
            }
            else
            {
                ChannelResult result;
                try
                {
                    result = await channel.SendAsync(
                        new NotificationMessage(notification.Id, notification.RecipientUserId, notification.Type.ToString(), notification.Title, notification.Body),
                        cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Del proveedor solo se guarda un código: el texto de la excepción puede traer datos del destinatario o del proveedor.
                    logger.LogWarning(ex, "El canal {Channel} lanzó una excepción al entregar el aviso {NotificationId}", notification.Channel, notification.Id);
                    result = ChannelResult.TransientFailure("channel_error");
                }

                if (result.Succeeded)
                {
                    notification.MarkDelivered(now);
                }
                else
                {
                    notification.RecordFailure(result.ErrorCode ?? "unknown", result.Transient, now);
                }
            }

            switch (notification.Status)
            {
                case NotificationStatus.Delivered:
                    delivered++;
                    break;
                case NotificationStatus.Failed:
                    failed++;
                    break;
                default:
                    retrying++;
                    break;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return NotificationRunSummary.Empty with { Delivered = delivered, Retrying = retrying, Failed = failed };
    }
}
