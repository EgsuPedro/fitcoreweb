using FitCore.SharedKernel;

namespace FitCore.Invoicing.Domain;

/// <summary>Régimen tributario del emisor. «Negocio popular» (RIMPE) no emite factura electrónica sino nota de venta, por eso no puede emitir aquí.</summary>
public enum TaxRegime
{
    General = 1,
    RimpeEmprendedor = 2,
    RimpeNegocioPopular = 3
}

/// <summary>Ambiente del SRI al que se transmiten los comprobantes. En pruebas no tienen validez tributaria.</summary>
public enum SriEnvironment
{
    Test = 1,
    Production = 2
}

/// <summary>Tipo de emisión. Hoy solo existe la normal (el SRI retiró la emisión por contingencia con claves propias para el esquema offline).</summary>
public enum EmissionType
{
    Normal = 1
}

/// <summary>
/// Datos tributarios de un gimnasio como emisor de comprobantes electrónicos (uno por gimnasio). Mientras no estén completos y válidos
/// (ver <see cref="IssuerReadiness"/>) no se puede emitir.
/// </summary>
public sealed class IssuerProfile : ITenantOwned, IAuditable
{
    public Guid TenantId { get; init; }
    public string Ruc { get; set; } = string.Empty;
    public string LegalName { get; set; } = string.Empty;
    public string? TradeName { get; set; }
    public string HeadOfficeAddress { get; set; } = string.Empty;
    public TaxRegime TaxRegime { get; set; } = TaxRegime.General;
    public bool AccountingRequired { get; set; }

    /// <summary>Número de resolución si es contribuyente especial.</summary>
    public string? SpecialTaxpayerNumber { get; set; }

    /// <summary>Número de resolución si es agente de retención.</summary>
    public string? WithholdingAgentNumber { get; set; }

    public SriEnvironment Environment { get; set; } = SriEnvironment.Test;
    public EmissionType EmissionType { get; set; } = EmissionType.Normal;

    /// <summary>Correo desde el que se envían los comprobantes y al que contesta el cliente.</summary>
    public string? NotificationEmail { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }
}

/// <summary>
/// Establecimiento y punto de emisión (códigos de 3 dígitos del SRI). Un secuencial siempre pertenece a un par (establecimiento, punto):
/// el número de factura es «EEE-PPP-NNNNNNNNN». No se borran: se desactivan.
/// </summary>
public sealed class EmissionPoint : ITenantOwned, IAuditable
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }

    /// <summary>Sucursal del sistema a la que corresponde (opcional: un establecimiento puede no coincidir con una sucursal).</summary>
    public Guid? BranchId { get; set; }

    public string EstablishmentCode { get; set; } = string.Empty;
    public string PointCode { get; set; } = string.Empty;

    /// <summary>Dirección del establecimiento (la que va en el comprobante).</summary>
    public string EstablishmentAddress { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }

    public static bool IsValidCode(string? code) =>
        code is { Length: 3 } && code.All(char.IsAsciiDigit) && code != "000";
}

/// <summary>
/// Certificado de firma electrónica (.p12) del emisor. El archivo y su contraseña se guardan <b>cifrados</b> (AES-256-GCM con clave por secreto protegida por la clave
/// maestra del servidor); jamás en claro. Solo uno está activo por gimnasio; al reemplazarlo o retirarlo se borran los secretos del anterior y queda la constancia.
/// </summary>
public sealed class SigningCertificate : ITenantOwned, IAuditable
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public string Subject { get; set; } = string.Empty;
    public string Issuer { get; set; } = string.Empty;
    public string SerialNumber { get; set; } = string.Empty;

    /// <summary>Huella SHA-256 (hex) del certificado.</summary>
    public string Thumbprint { get; set; } = string.Empty;

    /// <summary>RUC que se pudo leer en el sujeto del certificado (no todos lo traen ahí); sirve para avisar si parece de otro emisor.</summary>
    public string? SubjectRuc { get; set; }

    public DateTimeOffset NotBefore { get; set; }
    public DateTimeOffset NotAfter { get; set; }

    /// <summary>Archivo .p12 cifrado. Vacío cuando el certificado se retiró.</summary>
    public byte[] EncryptedP12 { get; set; } = [];

    /// <summary>Contraseña del .p12 cifrada. Vacía cuando el certificado se retiró.</summary>
    public byte[] EncryptedPassword { get; set; } = [];

    /// <summary>Versión de la clave maestra con la que se cifró (para poder rotarla).</summary>
    public int KeyVersion { get; set; }

    public bool IsActive { get; set; }
    public DateTimeOffset UploadedAt { get; set; }
    public Guid? UploadedBy { get; set; }
    public DateTimeOffset? RetiredAt { get; set; }
}

/// <summary>Una comprobación para saber si el gimnasio puede emitir. <c>Blocking</c>: si falla, no se puede emitir; si no, es solo un aviso.</summary>
public sealed record ReadinessCheck(string Code, bool Ok, bool Blocking);

/// <summary>Reglas puras de «¿está el emisor listo para emitir?». Se evalúan con lo guardado; el servidor las vuelve a aplicar al emitir.</summary>
public static class IssuerReadiness
{
    public static readonly TimeSpan ExpiryWarning = TimeSpan.FromDays(30);

    public static IReadOnlyList<ReadinessCheck> Evaluate(
        IssuerProfile? issuer,
        IReadOnlyCollection<EmissionPoint> points,
        SigningCertificate? certificate,
        bool masterKeyConfigured,
        DateTimeOffset now,
        bool? certificateMatchesRuc)
    {
        var checks = new List<ReadinessCheck>
        {
            new("issuer_profile", issuer is not null && !string.IsNullOrWhiteSpace(issuer.LegalName) && !string.IsNullOrWhiteSpace(issuer.HeadOfficeAddress), true),
            new("ruc_valid", issuer is not null && EcuadorIds.IsValidRuc(issuer.Ruc), true),
            new("regime_allows_electronic_invoice", issuer is null || issuer.TaxRegime != TaxRegime.RimpeNegocioPopular, true),
            new("emission_point", points.Any(p => p.IsActive && EmissionPoint.IsValidCode(p.EstablishmentCode) && EmissionPoint.IsValidCode(p.PointCode)
                                                  && !string.IsNullOrWhiteSpace(p.EstablishmentAddress)), true),
            new("master_key", masterKeyConfigured, true),
            new("certificate", certificate is { IsActive: true, EncryptedP12.Length: > 0 }, true),
            new("certificate_valid", certificate is { IsActive: true } c && c.NotBefore <= now && now < c.NotAfter, true),
            new("notification_email", issuer is not null && !string.IsNullOrWhiteSpace(issuer.NotificationEmail), false),
            new("certificate_expiring", certificate is not { IsActive: true } c2 || c2.NotAfter - now > ExpiryWarning, false),
            new("certificate_matches_ruc", certificateMatchesRuc is not false, false),
            new("production_environment", issuer is null || issuer.Environment == SriEnvironment.Production, false)
        };
        return checks;
    }

    /// <summary>Se puede emitir solo si pasan todas las comprobaciones bloqueantes.</summary>
    public static bool CanIssue(IEnumerable<ReadinessCheck> checks) => checks.Where(c => c.Blocking).All(c => c.Ok);
}
