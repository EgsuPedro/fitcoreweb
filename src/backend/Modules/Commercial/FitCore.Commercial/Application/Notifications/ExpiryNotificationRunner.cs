using FitCore.Application.Core;
using FitCore.Commercial.Application.Settings;
using FitCore.Commercial.Domain;
using FitCore.Commercial.Persistence;
using FitCore.Platform.Domain;
using FitCore.Platform.Persistence;
using FitCore.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace FitCore.Commercial.Application.Notifications;

public sealed record NotificationRunSummary(int Tenants, int Created, int Sent, int Failed)
{
    public static readonly NotificationRunSummary Empty = new(0, 0, 0, 0);

    public NotificationRunSummary Add(NotificationRunSummary other) =>
        new(Tenants + other.Tenants, Created + other.Created, Sent + other.Sent, Failed + other.Failed);
}

/// <summary>
/// Genera y envía los avisos por vencimiento de membresía. Lo ejecuta el Worker periódicamente; es seguro repetirlo:
/// cada aviso es único por membresía, tipo y destinatario, y el envío se reclama antes de salir (varias instancias no duplican).
/// <list type="bullet">
/// <item>Personal (teléfonos configurados): cuando faltan 3 días o menos para el fin y no hay renovación.</item>
/// <item>Alumno (solo con consentimiento y teléfono válido): el día del vencimiento y 3 días después, si no renovó.</item>
/// </list>
/// Las ventanas toleran que el proceso haya estado detenido unos días: el aviso se envía una vez al volver.
/// </summary>
public sealed class ExpiryNotificationRunner(IServiceScopeFactory scopes, IClock clock, ILogger<ExpiryNotificationRunner> logger)
{
    private const int StaffDaysBefore = 3;
    private const int MemberDueDayCatchUpDays = 2;
    private const int MemberAfterDueDays = 3;
    private const int MemberAfterDueCatchUpDays = 10;
    private const int BatchSize = 200;
    private static readonly TimeSpan ClaimTimeout = TimeSpan.FromMinutes(10);

    public async Task<NotificationRunSummary> RunAsync(CancellationToken cancellationToken)
    {
        List<(Guid Id, string Name)> tenants;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var platform = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            tenants = (await platform.Tenants.AsNoTracking()
                    .Where(t => t.Status == TenantStatus.Active && t.Slug != Tenant.PlatformSlug)
                    .Select(t => new { t.Id, t.Name })
                    .ToListAsync(cancellationToken))
                .Select(t => (t.Id, t.Name))
                .ToList();
        }

        var total = NotificationRunSummary.Empty;
        foreach (var (id, name) in tenants)
        {
            try
            {
                total = total.Add(await RunTenantAsync(id, name, cancellationToken));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Un gimnasio con problemas no debe frenar a los demás.
                logger.LogError(ex, "Falló el envío de avisos de vencimiento del gimnasio {TenantId}", id);
            }
        }

        return total with { Tenants = tenants.Count };
    }

    private async Task<NotificationRunSummary> RunTenantAsync(Guid tenantId, string gymName, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<CommercialDbContext>();
        var sender = scope.ServiceProvider.GetRequiredService<IWhatsAppSender>();

        var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        if (settings is not { NotificationsEnabled: true })
        {
            return NotificationRunSummary.Empty;
        }

        var created = await CreateDueNotificationsAsync(db, tenantId, gymName, settings, cancellationToken);
        var (sent, failed) = await SendPendingAsync(db, sender, cancellationToken);
        return new NotificationRunSummary(0, created, sent, failed);
    }

    private async Task<int> CreateDueNotificationsAsync(
        CommercialDbContext db,
        Guid tenantId,
        string gymName,
        CommercialSettings settings,
        CancellationToken cancellationToken)
    {
        var branches = await db.BranchRefs.AsNoTracking().ToDictionaryAsync(b => b.Id, cancellationToken);

        // Ventana amplia por fecha UTC (la zona horaria de cada sucursal se aplica después, por membresía).
        var utcToday = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        var from = utcToday.AddDays(-(MemberAfterDueCatchUpDays + 1));
        var to = utcToday.AddDays(StaffDaysBefore + 1);
        var candidates = await db.Memberships.AsNoTracking()
            .Where(m => m.Status == MembershipStatus.Active && m.EndDate >= from && m.EndDate <= to)
            .OrderBy(m => m.EndDate)
            .Take(BatchSize * 5)
            .ToListAsync(cancellationToken);
        if (candidates.Count == 0)
        {
            return 0;
        }

        var membershipIds = candidates.Select(m => m.Id).ToList();
        var memberIds = candidates.Select(m => m.MemberId).Distinct().ToList();
        var members = await db.Members.AsNoTracking().Where(m => memberIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, cancellationToken);

        // Renovada = existe otra membresía no cancelada del alumno que la continúa (o que empieza después de su fin).
        var others = await db.Memberships.AsNoTracking()
            .Where(m => memberIds.Contains(m.MemberId) && m.Status != MembershipStatus.Cancelled)
            .Select(m => new { m.Id, m.MemberId, m.StartDate, m.RenewedFromId })
            .ToListAsync(cancellationToken);

        var existing = (await db.Notifications.AsNoTracking()
                .Where(n => membershipIds.Contains(n.MembershipId))
                .Select(n => new { n.MembershipId, n.Type, n.Recipient })
                .ToListAsync(cancellationToken))
            .Select(n => (n.MembershipId, n.Type, n.Recipient))
            .ToHashSet();

        var now = clock.UtcNow;
        var created = 0;
        foreach (var membership in candidates)
        {
            if (!members.TryGetValue(membership.MemberId, out var member) || !branches.TryGetValue(membership.BranchId, out var branch))
            {
                continue;
            }

            var renewed = others.Any(o => o.MemberId == membership.MemberId && o.Id != membership.Id
                && (o.RenewedFromId == membership.Id || o.StartDate > membership.EndDate));
            if (renewed)
            {
                continue;
            }

            var daysLeft = membership.EndDate.DayNumber - LocalDates.Today(clock, branch.TimeZone).DayNumber;

            void Add(NotificationType type, NotificationRecipientKind kind, string phone, string text)
            {
                if (existing.Add((membership.Id, type, phone)))
                {
                    db.Notifications.Add(Notification.Create(tenantId, membership.BranchId, membership.Id, member.Id, type, kind, phone, text, membership.EndDate, now));
                    created++;
                }
            }

            if (daysLeft is >= 0 and <= StaffDaysBefore)
            {
                foreach (var phone in settings.StaffPhones)
                {
                    Add(NotificationType.StaffExpiringSoon, NotificationRecipientKind.Staff, phone,
                        NotificationMessages.StaffExpiringSoon(member, membership.PlanName, membership.EndDate, daysLeft, branch.Name));
                }
            }

            // Al alumno solo se le escribe con su consentimiento vigente y un teléfono válido.
            var memberPhone = member is { WhatsAppConsent: true, Status: MemberStatus.Active } ? PhoneNumbers.Normalize(member.Phone) : null;
            if (memberPhone is null)
            {
                continue;
            }

            if (daysLeft <= 0 && daysLeft >= -MemberDueDayCatchUpDays)
            {
                Add(NotificationType.MemberDueDay, NotificationRecipientKind.Member, memberPhone,
                    NotificationMessages.MemberDueDay(member, membership.PlanName, membership.EndDate, -daysLeft, gymName));
            }
            else if (daysLeft <= -MemberAfterDueDays && daysLeft >= -MemberAfterDueCatchUpDays)
            {
                Add(NotificationType.MemberAfterDue, NotificationRecipientKind.Member, memberPhone,
                    NotificationMessages.MemberAfterDue(member, membership.PlanName, membership.EndDate, gymName));
            }
        }

        if (created == 0)
        {
            return 0;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return created;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Otra instancia del Worker generó el mismo aviso a la vez: el índice único garantizó que solo existe uno.
            db.ChangeTracker.Clear();
            logger.LogDebug("Avisos ya generados por otra instancia; se omite el lote");
            return 0;
        }
    }

    private async Task<(int Sent, int Failed)> SendPendingAsync(CommercialDbContext db, IWhatsAppSender sender, CancellationToken cancellationToken)
    {
        var staleClaim = clock.UtcNow - ClaimTimeout;
        var pending = await db.Notifications
            .Where(n => (n.Status == NotificationStatus.Pending || n.Status == NotificationStatus.Failed
                    || (n.Status == NotificationStatus.Sending && n.ClaimedAt < staleClaim))
                && n.Attempts < Notification.MaxAttempts)
            .OrderBy(n => n.CreatedAt)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        int sent = 0, failed = 0;
        foreach (var notification in pending)
        {
            // Reclamo atómico: el UPDATE solo prospera si nadie lo tomó desde que se leyó (mismo estado e intentos). La instancia
            // que lo gana pasa el aviso a "Sending"; las demás ven 0 filas y lo omiten, así que nunca se envía dos veces a la vez.
            var expected = notification.Attempts;
            var expectedStatus = notification.Status;
            var claimedAt = clock.UtcNow;
            var claimed = await db.Notifications
                .Where(n => n.Id == notification.Id && n.Attempts == expected && n.Status == expectedStatus)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(n => n.Attempts, expected + 1)
                        .SetProperty(n => n.Status, NotificationStatus.Sending)
                        .SetProperty(n => n.ClaimedAt, claimedAt),
                    cancellationToken);
            if (claimed == 0)
            {
                continue;
            }

            // El UPDATE cambió la fila por fuera del seguimiento de EF: se recarga para que Sent/Failed se escriban de verdad.
            await db.Entry(notification).ReloadAsync(cancellationToken);

            WhatsAppSendResult result;
            try
            {
                result = await sender.SendAsync(new WhatsAppMessage(notification.Recipient, notification.MessageText), cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result = WhatsAppSendResult.Fail(ex.Message);
            }

            if (result.Success)
            {
                notification.MarkSent(sender.ProviderName, result.MessageId, clock.UtcNow);
                sent++;
            }
            else
            {
                notification.MarkFailed(sender.ProviderName, result.Error ?? "Error desconocido del proveedor.");
                failed++;
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        return (sent, failed);
    }
}
