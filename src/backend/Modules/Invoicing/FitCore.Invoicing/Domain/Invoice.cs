using FitCore.SharedKernel;
using System.Globalization;

namespace FitCore.Invoicing.Domain;

public enum InvoiceDocumentType
{
    Invoice = 1
}

/// <summary>
/// Estados del comprobante (ADR 0014). Hoy un comprobante nace «pendiente de firma»; los demás los alcanzan los bloques de firma, transmisión y autorización.
/// «Anulada» es solo el registro de una anulación hecha por el trámite oficial del SRI.
/// </summary>
public enum InvoiceStatus
{
    PendingSignature = 1,
    SignedDocument = 2,
    PendingSubmission = 3,
    Received = 4,
    Processing = 5,
    Authorized = 6,
    NotAuthorized = 7,
    Rejected = 8,
    Voided = 9,

    /// <summary>Reemplazado por una persona antes de autorizarse (devuelto, no autorizado o sin enviar). Libera la venta para volver a facturarla con otro número.</summary>
    Discarded = 10
}

/// <summary>
/// Comprobante electrónico (factura). Nace con su número y su clave de acceso ya asignados y con todo congelado: emisor, comprador, líneas, impuestos y totales. Una vez creado, nada de eso
/// cambia (lo garantiza también un disparador de la base); solo avanza su estado. Nunca se borra.
/// </summary>
public sealed class Invoice : ITenantOwned, IAuditable
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public InvoiceDocumentType DocumentType { get; init; } = InvoiceDocumentType.Invoice;
    public InvoiceStatus Status { get; set; } = InvoiceStatus.PendingSignature;

    // Origen (único por gimnasio, tipo de origen y tipo de comprobante: una venta no se factura dos veces).
    public string SourceType { get; init; } = string.Empty;
    public Guid SourceId { get; init; }
    public string SourceReference { get; init; } = string.Empty;
    public Guid BranchId { get; init; }

    // Numeración.
    public Guid EmissionPointId { get; init; }
    public string EstablishmentCode { get; init; } = string.Empty;
    public string PointCode { get; init; } = string.Empty;
    public int Sequential { get; init; }
    public SriEnvironment Environment { get; init; }
    public DateOnly IssueDate { get; init; }
    public string AccessKey { get; init; } = string.Empty;
    public string NumericCode { get; init; } = string.Empty;

    // Emisor (copia).
    public string IssuerRuc { get; init; } = string.Empty;
    public string IssuerLegalName { get; init; } = string.Empty;
    public string? IssuerTradeName { get; init; }
    public string IssuerHeadOfficeAddress { get; init; } = string.Empty;
    public string IssuerEstablishmentAddress { get; init; } = string.Empty;
    public TaxRegime TaxRegime { get; init; }
    public bool AccountingRequired { get; init; }
    public string? SpecialTaxpayerNumber { get; init; }
    public string? WithholdingAgentNumber { get; init; }

    // Comprador (copia).
    public SriBuyerIdType BuyerIdType { get; init; }
    public string BuyerIdNumber { get; init; } = string.Empty;
    public string BuyerName { get; init; } = string.Empty;
    public string? BuyerAddress { get; init; }
    public string? BuyerEmail { get; init; }
    public string? BuyerPhone { get; init; }
    public Guid? BuyerPersonId { get; init; }

    // Totales.
    public string Currency { get; init; } = "USD";
    public decimal SubtotalWithoutTaxes { get; init; }
    public decimal TotalDiscount { get; init; }
    public decimal TotalTax { get; init; }
    public decimal Total { get; init; }

    /// <summary>Lo cobrado menos el total del comprobante (casi siempre 0; ver <see cref="InvoiceCalculator.SplitGross"/>).</summary>
    public decimal RoundingDifference { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
    public Guid? CreatedBy { get; init; }
    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>Número de autorización que entrega el SRI (en el esquema offline coincide con la clave de acceso). Se fija una sola vez, al autorizarse.</summary>
    public string? AuthorizationNumber { get; set; }

    public DateTimeOffset? AuthorizedAt { get; set; }

    public List<InvoiceLine> Lines { get; init; } = [];
    public List<InvoiceTax> Taxes { get; init; } = [];
    public List<InvoicePayment> Payments { get; init; } = [];

    /// <summary>Número visible «EEE-PPP-NNNNNNNNN».</summary>
    public string Number => $"{EstablishmentCode}-{PointCode}-{Sequential.ToString("D9", CultureInfo.InvariantCulture)}";
}

public sealed class InvoiceLine
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid InvoiceId { get; init; }
    public int LineNumber { get; init; }
    public string MainCode { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public decimal Quantity { get; init; }
    public decimal UnitPrice { get; init; }
    public decimal Discount { get; init; }

    /// <summary>Precio total sin impuestos (base imponible de la línea).</summary>
    public decimal Subtotal { get; init; }

    public string TaxKind { get; init; } = string.Empty;
    public string TaxCode { get; init; } = string.Empty;
    public string TaxRateCode { get; init; } = string.Empty;
    public decimal TaxPercent { get; init; }
    public decimal TaxAmount { get; init; }
}

/// <summary>Total por impuesto y tarifa (<c>totalConImpuestos</c> del XML).</summary>
public sealed class InvoiceTax
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid InvoiceId { get; init; }
    public string TaxCode { get; init; } = string.Empty;
    public string RateCode { get; init; } = string.Empty;
    public decimal Percent { get; init; }
    public decimal TaxableBase { get; init; }
    public decimal Amount { get; init; }
}

public sealed class InvoicePayment
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid InvoiceId { get; init; }
    public string MethodCode { get; init; } = string.Empty;
    public string Method { get; init; } = string.Empty;
    public decimal Amount { get; init; }
}

public enum InvoiceDocumentKind
{
    /// <summary>XML del comprobante sin firmar, tal como lo armó el sistema.</summary>
    Xml = 1,

    /// <summary>XML con la firma XAdES-BES: el que se envía al SRI y el que el cliente conserva.</summary>
    SignedXml = 2,

    /// <summary>Respuesta de autorización del SRI (<c>&lt;autorizacion&gt;</c> con el comprobante firmado dentro): el documento tributario válido que se conserva y se entrega al cliente.</summary>
    AuthorizedXml = 3
}

/// <summary>Un documento generado del comprobante (XML y XML firmado). Inmutable: contiene datos personales y es la constancia de lo que se firmó; se entrega solo con permiso y de forma auditada.</summary>
public sealed class InvoiceDocument
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid InvoiceId { get; init; }
    public InvoiceDocumentKind Kind { get; init; }
    public string Content { get; init; } = string.Empty;

    /// <summary>SHA-256 (hex) del contenido en UTF-8: permite comprobar que el documento guardado es el que se firmó y se envió.</summary>
    public string Sha256 { get; init; } = string.Empty;

    public int SizeBytes { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>Historial inmutable del comprobante: quién hizo qué y cuándo.</summary>
public sealed class InvoiceEvent
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid InvoiceId { get; init; }
    public string Type { get; init; } = string.Empty;
    public InvoiceStatus? FromStatus { get; init; }
    public InvoiceStatus ToStatus { get; init; }
    public string? Detail { get; init; }
    public Guid? ActorId { get; init; }
    public DateTimeOffset At { get; init; }
}

/// <summary>
/// Trabajo de transmisión de un comprobante al SRI (una fila mientras está en curso; se borra al terminar). Es una cola con concesión: el Worker «reclama» las filas vencidas con
/// <c>locked_until</c> y varias instancias no procesan la misma. <c>NextAttemptAt</c> nulo = estacionado (requiere que una persona lo reactive).
/// </summary>
public sealed class InvoiceTransmission
{
    public Guid InvoiceId { get; init; }
    public Guid TenantId { get; init; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public bool Parked { get; set; }

    /// <summary>Intentos fallidos seguidos (no se cuentan las consultas de autorización pendientes).</summary>
    public int Attempts { get; set; }

    /// <summary>Consultas de autorización ya hechas sin respuesta definitiva; fija la espera de la siguiente.</summary>
    public int Polls { get; set; }

    public DateTimeOffset FirstAttemptAt { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }

    /// <summary>Último fallo como código estable (<c>sri_timeout</c>, <c>sri_http_503</c>…), nunca texto del SRI ni datos del comprobante.</summary>
    public string? LastError { get; set; }

    public DateTimeOffset? LockedUntil { get; set; }
    public Guid? RequestedBy { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public enum InvoiceEmailStatus
{
    Pending = 1,
    Sending = 2,
    Sent = 3,
    Failed = 4
}

/// <summary>
/// Envío por correo del XML autorizado y el RIDE de un comprobante. Es una cola con concesión como la del SRI: el Worker reclama las filas vencidas y reintenta con espera creciente. No guarda los adjuntos:
/// se generan al enviar a partir de los documentos inmutables del comprobante. Una fila por solicitud (el reenvío manual crea otra), así queda constancia de cada envío.
/// </summary>
public sealed class InvoiceEmail
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid InvoiceId { get; init; }
    public string ToAddress { get; init; } = string.Empty;
    public InvoiceEmailStatus Status { get; set; } = InvoiceEmailStatus.Pending;
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }

    /// <summary>Último fallo como código estable (<c>email.send_failed</c>…), nunca texto del servidor de correo.</summary>
    public string? LastError { get; set; }

    public DateTimeOffset? SentAt { get; set; }

    /// <summary>Nulo cuando lo encoló el sistema al autorizarse el comprobante.</summary>
    public Guid? RequestedBy { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

public enum SriOperation
{
    Reception = 1,
    Authorization = 2
}

/// <summary>Constancia inmutable de cada intercambio con el SRI: qué se pidió, qué contestó (estado y mensajes) y cuánto tardó. No guarda el XML.</summary>
public sealed class InvoiceSriAttempt
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid InvoiceId { get; init; }
    public SriOperation Operation { get; init; }
    public SriEnvironment Environment { get; init; }

    /// <summary>Resultado: Received, Returned, Authorized, NotAuthorized, Processing, NotFound, Unknown o Unavailable.</summary>
    public string Outcome { get; init; } = string.Empty;

    public string? SriState { get; init; }

    /// <summary>Código de fallo transitorio cuando el SRI no respondió.</summary>
    public string? ErrorCode { get; init; }

    public int DurationMs { get; init; }

    /// <summary>Mensajes del SRI como JSON (<c>[{id,text,detail,type}]</c>).</summary>
    public string Messages { get; init; } = "[]";

    public DateTimeOffset At { get; init; }
}

/// <summary>
/// Último secuencial asignado por gimnasio, punto de emisión, tipo de comprobante y ambiente. Se incrementa con una sola sentencia atómica dentro de la transacción del comprobante:
/// quien llega segundo espera, y si la transacción se deshace el número no se consume.
/// </summary>
public sealed class EmissionSequence : IAuditable
{
    public Guid TenantId { get; init; }
    public Guid EmissionPointId { get; init; }
    public InvoiceDocumentType DocumentType { get; init; }
    public SriEnvironment Environment { get; init; }
    public int LastValue { get; set; }
}
