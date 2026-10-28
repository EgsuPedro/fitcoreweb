using System.Text.Json;
using FitCore.Application.Core;
using FitCore.Classes.Domain;
using FluentValidation;

namespace FitCore.Classes.Application.Notifications;

/// <param name="Data">Referencias para abrir lo que corresponde (<c>sessionId</c>, <c>reservationId</c>).</param>
public sealed record NotificationDto(Guid Id, string Type, string Title, string Body, JsonElement Data, bool Critical, DateTimeOffset CreatedAt, bool IsRead, DateTimeOffset? ReadAt)
{
    public static NotificationDto From(InboxNotification n) =>
        new(n.Id, n.Type.ToString(), n.Title, n.Body, ParseData(n.Data), NotificationCatalog.IsCritical(n.Type), n.CreatedAt, n.IsRead, n.ReadAt);

    private static JsonElement ParseData(string data)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(data) ? "{}" : data);
        return document.RootElement.Clone();
    }
}

public sealed record NotificationSummaryDto(int Unread);

public sealed record MarkAllReadResultDto(int Marked);

/// <param name="Required">Los avisos críticos (un cupo con plazo, una clase cancelada o movida) no se pueden apagar.</param>
public sealed record NotificationPreferenceDto(string Type, string Label, string Description, bool Enabled, bool Required);

public sealed record PreferenceChange(NotificationType Type, bool Enabled);

public sealed record UpdatePreferencesRequest(IReadOnlyList<PreferenceChange> Preferences);

public sealed record ListMyNotificationsQuery(PageRequest Page, bool UnreadOnly, NotificationType? Type) : IQuery<PagedResult<NotificationDto>>;

public sealed record GetNotificationSummaryQuery : IQuery<NotificationSummaryDto>;

public sealed record MarkNotificationReadCommand(Guid Id) : ICommand<NotificationDto>;

public sealed record MarkAllNotificationsReadCommand : ICommand<MarkAllReadResultDto>;

public sealed record GetNotificationPreferencesQuery : IQuery<IReadOnlyList<NotificationPreferenceDto>>;

public sealed record UpdateNotificationPreferencesCommand(UpdatePreferencesRequest Request) : ICommand<IReadOnlyList<NotificationPreferenceDto>>;

public sealed class UpdatePreferencesRequestValidator : AbstractValidator<UpdatePreferencesRequest>
{
    public UpdatePreferencesRequestValidator()
    {
        RuleFor(x => x.Preferences).NotNull().NotEmpty().Must(p => p.Count <= NotificationCatalog.All.Count)
            .WithMessage("Hay una preferencia por tipo de aviso.");
        RuleForEach(x => x.Preferences).ChildRules(p => p.RuleFor(c => c.Type).IsInEnum());
    }
}

public static class NotificationErrors
{
    public static readonly FitCore.SharedKernel.Error NotFound = FitCore.SharedKernel.Error.NotFound("classes.notification_not_found", "Aviso no encontrado.");
    public static readonly FitCore.SharedKernel.Error Required = FitCore.SharedKernel.Error.Validation("classes.notification_required", "Ese aviso es obligatorio: informa de un cambio que te afecta y no se puede desactivar.");
    public static readonly FitCore.SharedKernel.Error DuplicateTypes = FitCore.SharedKernel.Error.Validation("classes.notification_duplicate_types", "Un tipo de aviso aparece más de una vez.");
}
