using FitCore.Application.Core;
using FitCore.Classes.Domain;
using FitCore.Classes.Persistence;
using FitCore.Platform.Security;
using FitCore.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace FitCore.Classes.Application.Notifications;

internal sealed class ListMyNotificationsHandler(ClassesDbContext db, IAccessContext access)
    : IQueryHandler<ListMyNotificationsQuery, PagedResult<NotificationDto>>
{
    public async Task<Result<PagedResult<NotificationDto>>> HandleAsync(ListMyNotificationsQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        // Siempre la bandeja del usuario de la sesión: ni el tenant ni el destinatario viajan en la solicitud.
        var items = db.Notifications.AsNoTracking().Where(n => n.RecipientUserId == actor.Value.UserId && n.Status == NotificationStatus.Delivered && n.Channel == NotificationChannels.InApp);
        if (query.UnreadOnly)
        {
            items = items.Where(n => n.ReadAt == null);
        }

        if (query.Type is { } type)
        {
            items = items.Where(n => n.Type == type);
        }

        var paged = await items.OrderByDescending(n => n.CreatedAt).ThenByDescending(n => n.Id).ToPagedAsync(query.Page, cancellationToken);
        return new PagedResult<NotificationDto>([.. paged.Items.Select(NotificationDto.From)], paged.Page, paged.PageSize, paged.TotalCount);
    }
}

internal sealed class GetNotificationSummaryHandler(ClassesDbContext db, IAccessContext access)
    : IQueryHandler<GetNotificationSummaryQuery, NotificationSummaryDto>
{
    public async Task<Result<NotificationSummaryDto>> HandleAsync(GetNotificationSummaryQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var unread = await db.Notifications.AsNoTracking()
            .CountAsync(n => n.RecipientUserId == actor.Value.UserId && n.Status == NotificationStatus.Delivered && n.Channel == NotificationChannels.InApp && n.ReadAt == null, cancellationToken);
        return new NotificationSummaryDto(unread);
    }
}

internal sealed class MarkNotificationReadHandler(ClassesDbContext db, IAccessContext access, IClock clock)
    : ICommandHandler<MarkNotificationReadCommand, NotificationDto>
{
    public async Task<Result<NotificationDto>> HandleAsync(MarkNotificationReadCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        // El aviso de otro usuario (o de otro gimnasio) responde como si no existiera.
        var own = db.Notifications.Where(n => n.Id == command.Id && n.RecipientUserId == actor.Value.UserId && n.Status == NotificationStatus.Delivered && n.Channel == NotificationChannels.InApp);
        if (!await own.AnyAsync(cancellationToken))
        {
            return NotificationErrors.NotFound;
        }

        // Marcar leído es idempotente y no pisa la hora de la primera lectura, aunque lleguen dos solicitudes a la vez.
        var now = clock.UtcNow;
        await own.Where(n => n.ReadAt == null).ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, now), cancellationToken);
        return NotificationDto.From(await own.AsNoTracking().FirstAsync(cancellationToken));
    }
}

internal sealed class MarkAllNotificationsReadHandler(ClassesDbContext db, IAccessContext access, IClock clock)
    : ICommandHandler<MarkAllNotificationsReadCommand, MarkAllReadResultDto>
{
    public async Task<Result<MarkAllReadResultDto>> HandleAsync(MarkAllNotificationsReadCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var now = clock.UtcNow;
        var marked = await db.Notifications
            .Where(n => n.RecipientUserId == actor.Value.UserId && n.Status == NotificationStatus.Delivered && n.Channel == NotificationChannels.InApp && n.ReadAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, now), cancellationToken);
        return new MarkAllReadResultDto(marked);
    }
}

internal static class PreferenceReader
{
    public static async Task<IReadOnlyList<NotificationPreferenceDto>> ReadAsync(ClassesDbContext db, Guid userId, CancellationToken cancellationToken)
    {
        var off = (await db.NotificationPreferences.AsNoTracking().Where(p => p.UserId == userId && !p.Enabled).Select(p => p.Type).ToListAsync(cancellationToken)).ToHashSet();
        return
        [
            .. NotificationCatalog.All.Select(info => new NotificationPreferenceDto(
                info.Type.ToString(), info.Label, info.Description, info.Critical || !off.Contains(info.Type), info.Critical))
        ];
    }
}

internal sealed class GetNotificationPreferencesHandler(ClassesDbContext db, IAccessContext access)
    : IQueryHandler<GetNotificationPreferencesQuery, IReadOnlyList<NotificationPreferenceDto>>
{
    public async Task<Result<IReadOnlyList<NotificationPreferenceDto>>> HandleAsync(GetNotificationPreferencesQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        return Result.Success(await PreferenceReader.ReadAsync(db, actor.Value.UserId, cancellationToken));
    }
}

internal sealed class UpdateNotificationPreferencesHandler(ClassesDbContext db, IAccessContext access, IClock clock)
    : ICommandHandler<UpdateNotificationPreferencesCommand, IReadOnlyList<NotificationPreferenceDto>>
{
    public async Task<Result<IReadOnlyList<NotificationPreferenceDto>>> HandleAsync(UpdateNotificationPreferencesCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var changes = command.Request.Preferences;
        if (changes.Select(c => c.Type).Distinct().Count() != changes.Count)
        {
            return NotificationErrors.DuplicateTypes;
        }

        // Los avisos críticos no se apagan: informan de un cambio que afecta al usuario (un cupo con plazo, una clase cancelada o movida).
        if (changes.Any(c => !c.Enabled && NotificationCatalog.IsCritical(c.Type)))
        {
            return NotificationErrors.Required;
        }

        var now = clock.UtcNow;
        var tenantId = actor.Value.TenantId;
        var userId = actor.Value.UserId;
        foreach (var change in changes)
        {
            var type = change.Type.ToString();
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO classes.notification_preferences (tenant_id, user_id, type, enabled, updated_at)
                VALUES ({tenantId}, {userId}, {type}, {change.Enabled}, {now})
                ON CONFLICT (tenant_id, user_id, type) DO UPDATE SET enabled = EXCLUDED.enabled, updated_at = EXCLUDED.updated_at
                """,
                cancellationToken);
        }

        return Result.Success(await PreferenceReader.ReadAsync(db, userId, cancellationToken));
    }
}
