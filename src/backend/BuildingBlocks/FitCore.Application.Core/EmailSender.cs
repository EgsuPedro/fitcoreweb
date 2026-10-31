using FitCore.SharedKernel;

namespace FitCore.Application.Core;

/// <summary>Archivo adjunto de un correo. El tipo de contenido debe ser <c>tipo/subtipo</c> (por ejemplo <c>application/pdf</c>).</summary>
public sealed record EmailAttachment(string FileName, string ContentType, byte[] Content);

/// <summary>Correo ya redactado. El destinatario es uno solo: cada persona recibe su propio mensaje (sin copias ocultas).</summary>
public sealed record EmailMessage(string To, string Subject, string TextBody, string? HtmlBody = null, IReadOnlyList<EmailAttachment>? Attachments = null);

/// <summary>
/// Puerto de envío de correo. La implementación real habla SMTP; no existe una versión «de mentira»: si no hay SMTP configurado,
/// <see cref="IsConfigured"/> es falso y quien lo necesita debe decirlo con claridad en lugar de aparentar que envió.
/// </summary>
public interface IEmailSender
{
    /// <summary>Hay un servidor SMTP configurado.</summary>
    bool IsConfigured { get; }

    /// <summary>Entrega el mensaje al servidor. Un fallo (red, rechazo del servidor) vuelve como error, no como excepción.</summary>
    Task<Result> SendAsync(EmailMessage message, CancellationToken cancellationToken);
}
