using FitCore.Application.Core;
using FitCore.SharedKernel;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace FitCore.Infrastructure.Core;

/// <summary>Configuración SMTP genérica (sección <c>Email:Smtp</c>). Sin <see cref="Host"/> el correo se considera no configurado.</summary>
public sealed class SmtpOptions
{
    public const string SectionName = "Email:Smtp";

    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;

    /// <summary><c>Auto</c> (según el puerto), <c>StartTls</c>, <c>SslOnConnect</c> o <c>None</c> (solo para el buzón local de desarrollo).</summary>
    public string Security { get; set; } = "Auto";

    public string User { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string From { get; set; } = string.Empty;
    public string FromName { get; set; } = "FIT360";
    public int TimeoutSeconds { get; set; } = 20;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(From);
}

/// <summary>
/// Envío real por SMTP con MailKit. No registra el contenido de los mensajes ni la clave: solo destinatario enmascarado y resultado.
/// Rechaza saltos de línea en destinatario y asunto (inyección de encabezados) y marca el mensaje como automático.
/// </summary>
public sealed class SmtpEmailSender(IOptions<SmtpOptions> options, ILogger<SmtpEmailSender> logger) : IEmailSender
{
    private readonly SmtpOptions _options = options.Value;

    public bool IsConfigured => _options.IsConfigured;

    public async Task<Result> SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return Error.Failure("email.not_configured", "El envío de correo no está configurado.");
        }

        if (HasLineBreak(message.To) || HasLineBreak(message.Subject))
        {
            return Error.Validation("email.invalid_header", "El destinatario o el asunto contienen saltos de línea.");
        }

        if (!MailboxAddress.TryParse(message.To, out var to))
        {
            return Error.Validation("email.invalid_recipient", "El destinatario no es una dirección de correo válida.");
        }

        if (message.Attachments?.Any(a => string.IsNullOrWhiteSpace(a.FileName) || HasLineBreak(a.FileName) || !ContentType.TryParse(a.ContentType, out _)) == true)
        {
            return Error.Validation("email.invalid_attachment", "Un adjunto tiene un nombre o un tipo de contenido no válido.");
        }

        MimeMessage mime;
        try
        {
            mime = Build(message, to);
        }
        catch (ParseException)
        {
            return Error.Validation("email.invalid_sender", "El remitente configurado no es válido.");
        }

        using var client = new SmtpClient { Timeout = Math.Max(1, _options.TimeoutSeconds) * 1000 };
        try
        {
            await client.ConnectAsync(_options.Host, _options.Port, ResolveSecurity(), cancellationToken);
            if (!string.IsNullOrEmpty(_options.User))
            {
                await client.AuthenticateAsync(_options.User, _options.Password, cancellationToken);
            }

            await client.SendAsync(mime, cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);
            return Result.Success();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // El mensaje del servidor puede traer la dirección; el detalle técnico queda solo en el registro del servidor.
            logger.LogWarning(ex, "No se pudo enviar un correo a {Recipient}", Mask(message.To));
            return Error.Failure("email.send_failed", "El servidor de correo no aceptó el mensaje.");
        }
    }

    private MimeMessage Build(EmailMessage message, MailboxAddress to)
    {
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(_options.FromName, _options.From));
        mime.To.Add(to);
        mime.Subject = message.Subject;
        mime.Headers.Add("Auto-Submitted", "auto-generated");
        var body = new BodyBuilder { TextBody = message.TextBody, HtmlBody = message.HtmlBody };
        foreach (var attachment in message.Attachments ?? [])
        {
            body.Attachments.Add(attachment.FileName, attachment.Content, ContentType.Parse(attachment.ContentType));
        }

        mime.Body = body.ToMessageBody();
        return mime;
    }

    private SecureSocketOptions ResolveSecurity() => _options.Security.ToLowerInvariant() switch
    {
        "starttls" => SecureSocketOptions.StartTls,
        "ssl" or "sslonconnect" => SecureSocketOptions.SslOnConnect,
        "none" => SecureSocketOptions.None,
        _ => _options.Port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable
    };

    private static bool HasLineBreak(string value) => value.Contains('\r') || value.Contains('\n');

    /// <summary>«juan.perez@correo.com» → «j***@correo.com» para los registros.</summary>
    public static string Mask(string email)
    {
        var at = email.IndexOf('@');
        return at <= 0 ? "***" : email[0] + "***" + email[at..];
    }
}

public static class EmailServiceCollectionExtensions
{
    /// <summary>Registra el emisor SMTP (lo usan la API y el Worker). Lee la sección <c>Email:Smtp</c>.</summary>
    public static IServiceCollection AddSmtpEmail(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SmtpOptions>(configuration.GetSection(SmtpOptions.SectionName));
        services.AddSingleton<IEmailSender, SmtpEmailSender>();
        return services;
    }
}
