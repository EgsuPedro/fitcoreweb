using FitCore.Application.Core;
using FitCore.Commercial.Application.Members;
using FitCore.Commercial.Application.Settings;
using FitCore.Commercial.Domain;
using FitCore.Commercial.Persistence;
using FitCore.Platform.Application;
using FitCore.Platform.Security;
using FitCore.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FitCore.Commercial.Application.Notifications;

internal static class NotificationScope
{
    public static IQueryable<Notification> VisibleTo(this IQueryable<Notification> notifications, UserAccess actor) =>
        notifications.InScope(actor, n => n.BranchId);
}

internal sealed class ListNotificationsHandler(CommercialDbContext db, IAccessContext access)
    : IQueryHandler<ListNotificationsQuery, PagedResult<NotificationDto>>
{
    public async Task<Result<PagedResult<NotificationDto>>> HandleAsync(ListNotificationsQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        if (query.BranchId is { } requested && !actor.Value.CanAccessBranch(requested))
        {
            return PlatformErrors.BranchNotAllowed;
        }

        var notifications = db.Notifications.AsNoTracking().VisibleTo(actor.Value);
        if (query.Status is { } status)
        {
            notifications = notifications.Where(n => n.Status == status);
        }

        if (query.Type is { } type)
        {
            notifications = notifications.Where(n => n.Type == type);
        }

        if (query.BranchId is { } branchId)
        {
            notifications = notifications.Where(n => n.BranchId == branchId);
        }

        if (query.MemberId is { } memberId)
        {
            notifications = notifications.Where(n => n.MemberId == memberId);
        }

        var paged = await notifications.OrderByDescending(n => n.CreatedAt).ThenBy(n => n.Id).ToPagedAsync(query.Page, cancellationToken);
        var memberIds = paged.Items.Select(n => n.MemberId).Distinct().ToList();
        var members = await db.Members.AsNoTracking().Where(m => memberIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, cancellationToken);

        var items = paged.Items.Select(n =>
        {
            members.TryGetValue(n.MemberId, out var member);
            return new NotificationDto(
                n.Id,
                n.BranchId,
                n.MembershipId,
                n.MemberId,
                member?.FullName ?? string.Empty,
                member?.Code ?? string.Empty,
                n.Type.ToString(),
                n.RecipientKind.ToString(),
                NotificationMasks.Phone(n.Recipient),
                n.MessageText,
                n.Status.ToString(),
                n.Attempts,
                n.LastError,
                n.Provider,
                n.MembershipEndDate,
                n.CreatedAt,
                n.SentAt);
        });
        return new PagedResult<NotificationDto>([.. items], paged.Page, paged.PageSize, paged.TotalCount);
    }
}

internal sealed class GetNotificationSettingsHandler(CommercialDbContext db, IAccessContext access, IOptions<WhatsAppOptions> options)
    : IQueryHandler<GetNotificationSettingsQuery, NotificationSettingsDto>
{
    public async Task<Result<NotificationSettingsDto>> HandleAsync(GetNotificationSettingsQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var settings = await SettingsStore.GetAsync(db, actor.Value.TenantId, cancellationToken);
        return new NotificationSettingsDto(settings.NotificationsEnabled, settings.StaffPhones, options.Value.Provider, settings.UpdatedAt);
    }
}

internal sealed class UpdateNotificationSettingsHandler(
    CommercialDbContext db,
    IAccessContext access,
    IClock clock,
    ICurrentUser currentUser,
    IOptions<WhatsAppOptions> options)
    : ICommandHandler<UpdateNotificationSettingsCommand, NotificationSettingsDto>
{
    public async Task<Result<NotificationSettingsDto>> HandleAsync(UpdateNotificationSettingsCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        // Como las demás políticas, es de todo el gimnasio: solo quien accede a todas las sucursales puede cambiarla.
        if (!actor.Value.AllBranches)
        {
            return PlatformErrors.AllBranchesRequired;
        }

        var settings = await db.Settings.FirstOrDefaultAsync(cancellationToken);
        if (settings is null)
        {
            settings = CommercialSettings.CreateDefault(actor.Value.TenantId);
            db.Settings.Add(settings);
        }

        var phones = (command.Request.StaffPhones ?? [])
            .Select(p => PhoneNumbers.Normalize(p)!)
            .Distinct()
            .ToList();
        settings.UpdateNotifications(command.Request.Enabled, phones, currentUser.UserId, clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        return new NotificationSettingsDto(settings.NotificationsEnabled, settings.StaffPhones, options.Value.Provider, settings.UpdatedAt);
    }
}

internal sealed class SetWhatsAppConsentHandler(CommercialDbContext db, IAccessContext access, IClock clock, ICurrentUser currentUser)
    : ICommandHandler<SetWhatsAppConsentCommand, MemberDto>
{
    public async Task<Result<MemberDto>> HandleAsync(SetWhatsAppConsentCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var member = await db.Members.VisibleTo(actor.Value).FirstOrDefaultAsync(m => m.Id == command.MemberId, cancellationToken);
        if (member is null)
        {
            return MemberErrors.NotFound;
        }

        if (command.Request.Granted && PhoneNumbers.Normalize(member.Phone) is null)
        {
            return NotificationErrors.PhoneRequired;
        }

        member.SetWhatsAppConsent(command.Request.Granted, currentUser.UserId, clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        return MemberDto.From(member);
    }
}
