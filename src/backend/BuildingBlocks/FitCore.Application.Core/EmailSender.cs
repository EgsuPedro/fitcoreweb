using FitCore.SharedKernel;

namespace FitCore.Application.Core;

/// <summary>Correo ya redactado. El destinatario es uno solo: cada persona recibe su propio mensaje (sin copias ocultas).</summary>
public sealed record EmailMessage(string To, string Subject, string TextBody, string? HtmlBody = null);

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
