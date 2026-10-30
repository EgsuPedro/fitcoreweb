using FitCore.SharedKernel;

namespace FitCore.Platform.Application.Registration;

public static class RegistrationStatus
{
    /// <summary>Esperando que la persona confirme su correo. Todavía no existe gimnasio ni usuario.</summary>
    public const string Pending = "Pending";

    /// <summary>Correo confirmado y gimnasio creado; falta abrir su suscripción (paso corto que se completa en la misma petición).</summary>
    public const string Provisioning = "Provisioning";

    public const string Verified = "Verified";
    public const string Expired = "Expired";

    /// <summary>Reemplazado por un registro más nuevo del mismo correo.</summary>
    public const string Superseded = "Superseded";

    /// <summary>No se creó el gimnasio por una regla (por ejemplo, la prueba gratuita ya se usó).</summary>
    public const string Rejected = "Rejected";

    /// <summary>El gimnasio se creó pero no se pudo abrir su suscripción: la plataforma debe revisarlo.</summary>
    public const string Failed = "Failed";
}

/// <summary>
/// Solicitud de registro de un gimnasio, anterior a su existencia. La contraseña se guarda hasheada y solo hasta confirmar el correo (después se borra);
/// el token de confirmación vive en <c>identity.account_tokens</c> y aquí solo se anota el sujeto. Sin RLS: aún no hay gimnasio al que pertenecer.
/// </summary>
public sealed class PendingRegistration
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string EmailNormalized { get; set; } = string.Empty;
    public string OwnerFullName { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string GymName { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string PlanCode { get; set; } = string.Empty;
    public string Locale { get; set; } = "es";
    public string TimeZone { get; set; } = Domain.Tenant.DefaultTimeZone;
    public string TermsVersion { get; set; } = string.Empty;
    public string PrivacyVersion { get; set; } = string.Empty;
    public string Status { get; set; } = RegistrationStatus.Pending;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset LastSentAt { get; set; }
    public int SendCount { get; set; } = 1;
    public DateTimeOffset? CompletedAt { get; set; }
    public Guid? TenantId { get; set; }
}

/// <summary>Constancia de que un correo (normalizado) ya aprovechó una prueba gratuita. Una por propietario.</summary>
public sealed class TrialClaim
{
    public string EmailNormalized { get; set; } = string.Empty;
    public Guid TenantId { get; set; }
    public string PlanCode { get; set; } = string.Empty;
    public DateTimeOffset ClaimedAt { get; set; }
}

/// <summary>Aceptación de un documento legal (términos, privacidad) con su versión y fecha. Aislada por gimnasio.</summary>
public sealed class LegalAcceptance : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; init; }
    public Guid UserId { get; set; }
    public string Document { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public DateTimeOffset AcceptedAt { get; set; }
}

public static class LegalDocuments
{
    public const string Terms = "terms";
    public const string Privacy = "privacy";
}

/// <summary>
/// Versiones vigentes de los textos legales (sección <c>Legal</c>). Quien se registra debe aceptar exactamente estas versiones; al publicar un texto nuevo
/// se cambia la versión y las solicitudes con la anterior se rechazan. El borrador inicial está marcado «pendiente de revisión legal».
/// </summary>
public sealed class LegalOptions
{
    public const string SectionName = "Legal";

    public string TermsVersion { get; set; } = "2026-10-30-borrador";
    public string PrivacyVersion { get; set; } = "2026-10-30-borrador";
}

public static class EmailNormalizer
{
    /// <summary>
    /// Forma comparable de un correo: minúsculas, sin la etiqueta «+algo» y, en Gmail, sin puntos. Sirve para limitar una prueba gratuita por propietario; no se usa
    /// para enviar. No detiene a quien tenga varios correos distintos: eso se mitiga con límites de frecuencia y revisión de la plataforma.
    /// </summary>
    public static string Normalize(string email)
    {
        var value = (email ?? string.Empty).Trim().ToLowerInvariant();
        var at = value.LastIndexOf('@');
        if (at <= 0)
        {
            return value;
        }

        var local = value[..at];
        var domain = value[(at + 1)..];
        var plus = local.IndexOf('+');
        if (plus > 0)
        {
            local = local[..plus];
        }

        if (domain is "gmail.com" or "googlemail.com")
        {
            local = local.Replace(".", string.Empty);
            domain = "gmail.com";
        }

        return local + "@" + domain;
    }
}
