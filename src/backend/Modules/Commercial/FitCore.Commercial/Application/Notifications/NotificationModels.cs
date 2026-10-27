using FitCore.Application.Core;
using FitCore.Commercial.Application.Members;
using FitCore.Commercial.Domain;
using FitCore.SharedKernel;
using FluentValidation;
using Microsoft.Extensions.Logging;

namespace FitCore.Commercial.Application.Notifications;

// ---- Puerto de envío -----------------------------------------------------------------------------------------------

/// <summary>Mensaje de WhatsApp listo para enviar. <c>To</c> va en formato internacional (+593…).</summary>
public sealed record WhatsAppMessage(string To, string Text);

public sealed record WhatsAppSendResult(bool Success, string? MessageId, string? Error)
{
    public static WhatsAppSendResult Ok(string messageId) => new(true, messageId, null);

    public static WhatsAppSendResult Fail(string error) => new(false, null, error);
}

/// <summary>
/// Puerto hacia el proveedor de WhatsApp. Hoy solo existe el emisor simulado; cuando haya credenciales de Meta Cloud API se
/// agrega otra implementación sin tocar el resto del sistema.
/// </summary>
public interface IWhatsAppSender
{
    /// <summary>Nombre del proveedor que se registra en cada notificación (<c>simulated</c>, <c>meta</c>…).</summary>
    string ProviderName { get; }

    Task<WhatsAppSendResult> SendAsync(WhatsAppMessage message, CancellationToken cancellationToken);
}

public sealed class WhatsAppOptions
{
    public const string SectionName = "WhatsApp";

    /// <summary>Solo <c>Simulated</c> está soportado todavía: cualquier otro valor impide arrancar para que nadie crea que se envía.</summary>
    public string Provider { get; set; } = "Simulated";
}

/// <summary>Emisor de pruebas: no sale nada a internet. Registra en el log el envío (con el teléfono enmascarado) y lo da por entregado.</summary>
public sealed class SimulatedWhatsAppSender(ILogger<SimulatedWhatsAppSender> logger) : IWhatsAppSender
{
    public string ProviderName => "simulated";

    public Task<WhatsAppSendResult> SendAsync(WhatsAppMessage message, CancellationToken cancellationToken)
    {
        logger.LogInformation("[WhatsApp simulado] mensaje de {Length} caracteres a {To}", message.Text.Length, NotificationMasks.Phone(message.To));
        return Task.FromResult(WhatsAppSendResult.Ok($"sim-{Guid.CreateVersion7():N}"));
    }
}

internal static class NotificationMasks
{
    /// <summary>+593991234567 → +59399****4567: quien consulta el historial no necesita ver el número completo.</summary>
    public static string Phone(string phone) => phone.Length <= 8 ? phone : string.Concat(phone.AsSpan(0, 6), new string('*', phone.Length - 10), phone.AsSpan(phone.Length - 4));
}

// ---- Textos --------------------------------------------------------------------------------------------------------

internal static class NotificationMessages
{
    private static string Date(DateOnly date) => date.ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture);

    public static string StaffExpiringSoon(Member member, string planName, DateOnly endDate, int daysLeft, string branchName) =>
        $"Aviso FitCore: la membresía de {member.FullName} ({member.Code}), plan {planName}, "
        + (daysLeft == 0 ? "vence hoy" : $"vence el {Date(endDate)} (en {daysLeft} día{(daysLeft == 1 ? string.Empty : "s")})")
        + $" en {branchName} y aún no tiene renovación.";

    public static string MemberDueDay(Member member, string planName, DateOnly endDate, int daysPast, string gymName) =>
        daysPast == 0
            ? $"Hola {member.FirstName}, tu membresía {planName} en {gymName} vence hoy. ¡Gracias por entrenar con nosotros! "
              + "Cuando quieras renovarla, acércate a recepción y con gusto te ayudamos."
            : $"Hola {member.FirstName}, tu membresía {planName} en {gymName} venció el {Date(endDate)}. ¡Gracias por entrenar con nosotros! "
              + "Cuando quieras renovarla, acércate a recepción y con gusto te ayudamos.";

    public static string MemberAfterDue(Member member, string planName, DateOnly endDate, string gymName) =>
        $"Hola {member.FirstName}, notamos que tu membresía {planName} en {gymName} venció el {Date(endDate)}. "
        + "Te esperamos de vuelta: si deseas renovarla, pasa por recepción o escríbenos. ¡Aquí estamos para ayudarte!";
}

// ---- Consulta de notificaciones ------------------------------------------------------------------------------------

public sealed record NotificationDto(
    Guid Id,
    Guid BranchId,
    Guid MembershipId,
    Guid MemberId,
    string MemberName,
    string MemberCode,
    string Type,
    string RecipientKind,
    string Recipient,
    string MessageText,
    string Status,
    int Attempts,
    string? LastError,
    string? Provider,
    DateOnly MembershipEndDate,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SentAt);

public sealed record ListNotificationsQuery(
    PageRequest Page,
    NotificationStatus? Status,
    NotificationType? Type,
    Guid? BranchId,
    Guid? MemberId) : IQuery<PagedResult<NotificationDto>>;

// ---- Configuración -------------------------------------------------------------------------------------------------

public sealed record NotificationSettingsDto(bool Enabled, IReadOnlyList<string> StaffPhones, string Provider, DateTimeOffset? UpdatedAt);

public sealed record UpdateNotificationSettingsRequest(bool Enabled, IReadOnlyList<string>? StaffPhones);

public sealed record GetNotificationSettingsQuery : IQuery<NotificationSettingsDto>;

public sealed record UpdateNotificationSettingsCommand(UpdateNotificationSettingsRequest Request) : ICommand<NotificationSettingsDto>;

public sealed class UpdateNotificationSettingsRequestValidator : AbstractValidator<UpdateNotificationSettingsRequest>
{
    public UpdateNotificationSettingsRequestValidator()
    {
        RuleFor(x => x.StaffPhones).Must(phones => phones is null || phones.Count <= 5).WithMessage("Máximo 5 teléfonos del personal.");
        RuleForEach(x => x.StaffPhones).Must(phone => PhoneNumbers.Normalize(phone) is not null)
            .WithMessage("Hay un teléfono inválido: use el formato 0991234567 o +593991234567.");
    }
}

// ---- Consentimiento del alumno -------------------------------------------------------------------------------------

public sealed record SetWhatsAppConsentRequest(bool Granted);

public sealed record SetWhatsAppConsentCommand(Guid MemberId, SetWhatsAppConsentRequest Request) : ICommand<MemberDto>;

public static class NotificationErrors
{
    public static readonly Error PhoneRequired = Error.Validation(
        "members.whatsapp_phone_required",
        "Para activar WhatsApp el alumno necesita un teléfono válido (por ejemplo 0991234567).");
}
