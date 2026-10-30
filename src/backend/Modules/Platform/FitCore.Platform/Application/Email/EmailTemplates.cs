using System.Net;

namespace FitCore.Platform.Application.Email;

public static class EmailTemplateCodes
{
    public const string VerifyEmail = "verify_email";
    public const string PasswordReset = "password_reset";
    public const string Invitation = "invitation";
    public const string PasswordChanged = "password_changed";
    public const string DemoRequestNotification = "demo_request_notification";
}

public sealed record EmailContent(string Subject, string TextBody, string HtmlBody);

/// <summary>
/// Plantillas de correo (español por defecto, inglés). Todo valor que llega de una persona (nombre del gimnasio, mensaje de contacto)
/// se codifica para HTML; los enlaces solo se aceptan si son http(s). El texto plano nunca lleva HTML.
/// </summary>
public static class EmailTemplates
{
    public static EmailContent Render(string template, string? locale, IReadOnlyDictionary<string, string> values)
    {
        var en = string.Equals(locale, "en", StringComparison.OrdinalIgnoreCase);
        return template switch
        {
            EmailTemplateCodes.VerifyEmail => Action(
                en ? "Confirm your email — FIT360" : "Confirme su correo — FIT360",
                en ? $"Hello {V(values, "name")}," : $"Hola {V(values, "name")},",
                en ? "To finish creating your gym account, confirm your email address. The link works once and expires in 24 hours."
                   : "Para terminar de crear la cuenta de su gimnasio, confirme su correo. El enlace sirve una sola vez y vence en 24 horas.",
                en ? "Confirm email" : "Confirmar correo",
                Link(values),
                en ? "If you did not request this, you can ignore this message." : "Si usted no lo solicitó, puede ignorar este mensaje."),
            EmailTemplateCodes.PasswordReset => Action(
                en ? "Reset your password — FIT360" : "Restablezca su contraseña — FIT360",
                en ? $"Hello {V(values, "name")}," : $"Hola {V(values, "name")},",
                en ? "We received a request to reset your password. The link works once and expires in 1 hour."
                   : "Recibimos una solicitud para restablecer su contraseña. El enlace sirve una sola vez y vence en 1 hora.",
                en ? "Choose a new password" : "Elegir una contraseña nueva",
                Link(values),
                en ? "If you did not request this, ignore this message: your password does not change." : "Si usted no lo solicitó, ignore este mensaje: su contraseña no cambia."),
            EmailTemplateCodes.Invitation => Action(
                en ? $"You were invited to {V(values, "gym")} — FIT360" : $"Lo invitaron a {V(values, "gym")} — FIT360",
                en ? "Hello," : "Hola,",
                en ? $"{V(values, "inviter")} invited you to join {V(values, "gym")} on FIT360. The link works once and expires in 7 days."
                   : $"{V(values, "inviter")} lo invitó a unirse a {V(values, "gym")} en FIT360. El enlace sirve una sola vez y vence en 7 días.",
                en ? "Accept invitation" : "Aceptar invitación",
                Link(values),
                en ? "If you were not expecting this invitation, you can ignore it." : "Si no esperaba esta invitación, puede ignorarla."),
            EmailTemplateCodes.PasswordChanged => Notice(
                en ? "Your password was changed — FIT360" : "Se cambió su contraseña — FIT360",
                en ? $"Hello {V(values, "name")}," : $"Hola {V(values, "name")},",
                en ? "The password of your FIT360 account was just changed and all open sessions were closed."
                   : "La contraseña de su cuenta de FIT360 acaba de cambiarse y se cerraron todas las sesiones abiertas.",
                en ? "If it was not you, ask your administrator to reset your password right away." : "Si no fue usted, pida a su administrador que restablezca su contraseña de inmediato."),
            EmailTemplateCodes.DemoRequestNotification => Notification(values, en),
            _ => throw new ArgumentOutOfRangeException(nameof(template), template, "Plantilla de correo desconocida.")
        };
    }

    private static EmailContent Notification(IReadOnlyDictionary<string, string> v, bool en)
    {
        var subject = en ? "New contact request — FIT360" : "Nueva solicitud de contacto — FIT360";
        var lines = new (string Label, string Value)[]
        {
            (en ? "Type" : "Tipo", V(v, "kind")),
            (en ? "Name" : "Nombre", V(v, "name")),
            (en ? "Email" : "Correo", V(v, "email")),
            (en ? "Phone" : "Teléfono", V(v, "phone")),
            (en ? "Gym" : "Gimnasio", V(v, "gym")),
            (en ? "Message" : "Mensaje", V(v, "message"))
        };
        var text = string.Join("\n", lines.Select(l => $"{l.Label}: {l.Value}"));
        var rows = string.Concat(lines.Select(l => $"<tr><td><strong>{H(l.Label)}</strong></td><td>{H(l.Value)}</td></tr>"));
        return new EmailContent(subject, text, Wrap($"<table cellpadding=\"6\">{rows}</table>"));
    }

    private static EmailContent Notice(string subject, string greeting, string body, string footer)
    {
        var text = $"{greeting}\n\n{body}\n\n{footer}\n\nFIT360";
        return new EmailContent(subject, text, Wrap($"<p>{H(greeting)}</p><p>{H(body)}</p><p style=\"font-size:13px;color:#555\">{H(footer)}</p>"));
    }

    private static EmailContent Action(string subject, string greeting, string body, string button, string link, string footer)
    {
        var text = $"{greeting}\n\n{body}\n\n{button}: {link}\n\n{footer}\n\nFIT360";
        var html = Wrap(
            $"<p>{H(greeting)}</p><p>{H(body)}</p>" +
            $"<p><a href=\"{H(link)}\" style=\"background:#0f766e;color:#ffffff;padding:10px 18px;border-radius:6px;text-decoration:none;display:inline-block\">{H(button)}</a></p>" +
            $"<p style=\"font-size:13px;color:#555\">{H(link)}</p><p style=\"font-size:13px;color:#555\">{H(footer)}</p>");
        return new EmailContent(subject, text, html);
    }

    private static string Wrap(string inner) =>
        "<!doctype html><html><body style=\"font-family:Arial,Helvetica,sans-serif;color:#1f2937;line-height:1.5\">" + inner + "<p>FIT360</p></body></html>";

    private static string Link(IReadOnlyDictionary<string, string> v)
    {
        var link = V(v, "link");
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException("El enlace del correo debe ser http o https.", nameof(v));
        }

        return uri.AbsoluteUri;
    }

    // Los saltos de línea se vuelven espacios: un valor no puede partir el asunto ni las líneas de la notificación.
    private static string V(IReadOnlyDictionary<string, string> v, string key) =>
        v.TryGetValue(key, out var value) ? value.Replace('\r', ' ').Replace('\n', ' ') : string.Empty;

    private static string H(string value) => WebUtility.HtmlEncode(value);
}
