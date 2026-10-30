namespace FitCore.Application.Core;

/// <summary>Clases de origen que otros módulos saben entregar a la facturación electrónica.</summary>
public static class InvoiceSourceTypes
{
    /// <summary>Una obligación de pago de Comercial (membresía u otro concepto) y los pagos confirmados que la cubren.</summary>
    public const string Obligation = "obligation";
}

public enum InvoiceSourceStatus
{
    /// <summary>Todavía debe dinero: no se factura hasta cobrarla.</summary>
    Open = 1,
    Paid = 2,
    Voided = 3
}

/// <summary>Tipo de documento tal como lo registró el módulo de origen. Facturación lo traduce a los tipos de identificación del SRI.</summary>
public enum SourceIdKind
{
    Cedula = 1,
    Ruc = 2,
    Passport = 3,
    Other = 4
}

/// <summary>Quien recibe el comprobante, tal como figura hoy en el módulo de origen. Facturación guarda una copia (snapshot) y no vuelve a consultarlo.</summary>
public sealed record InvoiceSourceBuyer(
    Guid? PersonId,
    string FirstName,
    string LastName,
    SourceIdKind IdKind,
    string IdNumber,
    string? Email,
    string? Phone,
    string? Address);

/// <summary>
/// Una línea con los valores congelados al generar la obligación. Los importes <b>incluyen</b> el impuesto (así vende el gimnasio); el desglose en base e impuesto
/// es el que el origen calculó con la tarifa vigente de su fecha. Facturación puede recalcular la base según la regla de redondeo del SRI.
/// </summary>
public sealed record InvoiceSourceLine(
    string Description,
    decimal Quantity,
    decimal GrossAmount,
    decimal DiscountAmount,
    decimal PayableAmount,
    decimal Subtotal,
    decimal TaxAmount,
    string TaxKind,
    decimal TaxPercent);

/// <summary>Pago confirmado que cubre el origen. <c>Method</c> es Cash, BankTransfer, Card u Other.</summary>
public sealed record InvoiceSourcePayment(string Method, decimal Amount, DateTimeOffset PaidAt, string? Reference);

public sealed record InvoiceSource(
    string SourceType,
    Guid SourceId,
    string Reference,
    Guid BranchId,
    DateOnly IssueDate,
    string Currency,
    InvoiceSourceStatus Status,
    InvoiceSourceBuyer Buyer,
    IReadOnlyList<InvoiceSourceLine> Lines,
    IReadOnlyList<InvoiceSourcePayment> Payments)
{
    public decimal PayableTotal => Lines.Sum(l => l.PayableAmount);

    public decimal PaidTotal => Payments.Sum(p => p.Amount);
}

/// <summary>
/// Puerta de entrada de la facturación electrónica a lo que se vendió. Facturación no conoce Comercial: Comercial implementa este contrato y devuelve solo lo que se
/// factura (comprador, líneas, impuestos congelados y pagos). Respeta el alcance de sucursales de quien consulta: lo que no puede ver, no existe (<c>null</c>).
/// </summary>
public interface IInvoiceSourceProvider
{
    Task<InvoiceSource?> LoadAsync(string sourceType, Guid sourceId, CancellationToken cancellationToken);
}
