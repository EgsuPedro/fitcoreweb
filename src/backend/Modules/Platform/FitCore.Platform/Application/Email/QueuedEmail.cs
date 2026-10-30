namespace FitCore.Platform.Application.Email;

public static class EmailStatus
{
    public const string Pending = "Pending";
    public const string Sending = "Sending";
    public const string Sent = "Sent";
    public const string Failed = "Failed";
}

/// <summary>
/// Correo en la cola de envío (<c>messaging.emails</c>). Los cuerpos contienen enlaces de un solo uso: se borran en cuanto el correo
/// se envía o falla definitivamente. Tabla interna del Worker (sin RLS: se procesa entre gimnasios y la API nunca la expone).
/// </summary>
public sealed class QueuedEmail
{
    public Guid Id { get; set; }
    public string ToAddress { get; set; } = string.Empty;
    public string Template { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string? TextBody { get; set; }
    public string? HtmlBody { get; set; }
    public string Status { get; set; } = EmailStatus.Pending;
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
}
