using System.Text;
using FitCore.SharedKernel;

namespace FitCore.Commercial.Domain;

public enum NotificationType
{
    /// <summary>Aviso al personal: la membresía de un alumno termina en 3 días o menos y no tiene renovación.</summary>
    StaffExpiringSoon = 1,

    /// <summary>Mensaje cordial al alumno el día en que vence su membresía (si no la renovó).</summary>
    MemberDueDay = 2,

    /// <summary>Mensaje cordial al alumno 3 días después del vencimiento (si sigue sin renovar).</summary>
    MemberAfterDue = 3
}

public enum NotificationStatus
{
    Pending = 1,
    Sent = 2,

    /// <summary>El proveedor rechazó el envío; se reintenta hasta agotar los intentos.</summary>
    Failed = 3,

    /// <summary>Una instancia del Worker lo reclamó y lo está enviando. Si se queda así demasiado tiempo (el proceso murió), se reclama de nuevo.</summary>
    Sending = 4
}

public enum NotificationRecipientKind
{
    Staff = 1,
    Member = 2
}

/// <summary>
/// Mensaje de WhatsApp que el sistema debe enviar (o ya envió) por el vencimiento de una membresía. La combinación
/// (membresía, tipo, destinatario) es única: ese aviso se genera una sola vez aunque el proceso corra muchas veces.
/// </summary>
public sealed class Notification : Entity<Guid>, ITenantOwned
{
    public const int MaxAttempts = 3;

    private Notification(Guid id)
        : base(id)
    {
    }

    public Guid TenantId { get; private set; }
    public Guid BranchId { get; private set; }
    public Guid MembershipId { get; private set; }
    public Guid MemberId { get; private set; }
    public NotificationType Type { get; private set; }
    public NotificationRecipientKind RecipientKind { get; private set; }

    /// <summary>Teléfono destino en formato internacional (+593…).</summary>
    public string Recipient { get; private set; } = string.Empty;

    public string MessageText { get; private set; } = string.Empty;
    public NotificationStatus Status { get; private set; } = NotificationStatus.Pending;
    public int Attempts { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>Proveedor que envió el mensaje (<c>simulated</c> mientras no haya credenciales de Meta).</summary>
    public string? Provider { get; private set; }

    public string? ProviderMessageId { get; private set; }

    /// <summary>Fecha de fin de la membresía que originó el aviso.</summary>
    public DateOnly MembershipEndDate { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? SentAt { get; private set; }

    /// <summary>Cuándo lo reclamó la última instancia que lo envía (para recuperar reclamos abandonados).</summary>
    public DateTimeOffset? ClaimedAt { get; private set; }

    public static Notification Create(
        Guid tenantId,
        Guid branchId,
        Guid membershipId,
        Guid memberId,
        NotificationType type,
        NotificationRecipientKind kind,
        string recipient,
        string text,
        DateOnly membershipEndDate,
        DateTimeOffset now) =>
        new(Guid.CreateVersion7())
        {
            TenantId = tenantId,
            BranchId = branchId,
            MembershipId = membershipId,
            MemberId = memberId,
            Type = type,
            RecipientKind = kind,
            Recipient = recipient,
            MessageText = text,
            MembershipEndDate = membershipEndDate,
            CreatedAt = now
        };

    public bool CanRetry => Status != NotificationStatus.Sent && Attempts < MaxAttempts;

    public void MarkSent(string provider, string? providerMessageId, DateTimeOffset now)
    {
        Status = NotificationStatus.Sent;
        Provider = provider;
        ProviderMessageId = providerMessageId;
        SentAt = now;
        LastError = null;
    }

    public void MarkFailed(string provider, string error)
    {
        Status = NotificationStatus.Failed;
        Provider = provider;
        LastError = error.Length > 500 ? error[..500] : error;
    }
}

/// <summary>Normaliza teléfonos al formato internacional que exige WhatsApp (solo dígitos con prefijo +).</summary>
public static class PhoneNumbers
{
    private const string EcuadorPrefix = "593";

    /// <summary>
    /// <c>0991234567</c> → <c>+593991234567</c>; <c>+593 99 123 4567</c> → <c>+593991234567</c>. Devuelve null si no parece un
    /// teléfono (menos de 8 o más de 15 dígitos).
    /// </summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim();
        var digits = new StringBuilder();
        foreach (var c in trimmed)
        {
            if (char.IsAsciiDigit(c))
            {
                digits.Append(c);
            }
            else if (c is not (' ' or '-' or '(' or ')' or '+' or '.'))
            {
                return null;
            }
        }

        var text = digits.ToString();
        var doubleZero = trimmed.StartsWith("00", StringComparison.Ordinal);
        var international = trimmed.StartsWith('+') || doubleZero;
        if (doubleZero)
        {
            text = text[2..];
        }

        if (!international)
        {
            if (text.StartsWith('0'))
            {
                text = EcuadorPrefix + text[1..];
            }
            else if (!text.StartsWith(EcuadorPrefix, StringComparison.Ordinal))
            {
                return null;
            }
        }

        return text.Length is >= 8 and <= 15 ? "+" + text : null;
    }
}
