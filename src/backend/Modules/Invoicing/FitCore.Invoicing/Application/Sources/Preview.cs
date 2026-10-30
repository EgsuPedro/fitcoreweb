using FitCore.Application.Core;
using FitCore.Invoicing.Application.Invoices;
using FitCore.Invoicing.Application.Settings;
using FitCore.Invoicing.Domain;
using FitCore.Platform.Security;
using FitCore.SharedKernel;

namespace FitCore.Invoicing.Application.Sources;

// ---- Modelos ----------------------------------------------------------------------------------------------------------------------

public sealed record InvoiceBuyerDto(string IdTypeCode, string IdType, string IdNumber, string LegalName, string? Address, string? Email, string? Phone, bool IsFinalConsumer);

public sealed record InvoicePreviewLineDto(
    string Description, decimal Quantity, decimal GrossAmount, decimal DiscountAmount, decimal PayableAmount, decimal Subtotal, decimal TaxAmount, string TaxKind, decimal TaxPercent);

public sealed record InvoicePreviewPaymentDto(string Method, decimal Amount, DateTimeOffset PaidAt, string? Reference);

public sealed record InvoiceIssueDto(string Code, bool Blocking);

/// <summary>
/// Lo que se facturaría con un origen (hoy, una obligación cobrada): comprador resuelto con los tipos del SRI, cálculo del comprobante con las reglas del SRI, punto de emisión que se usaría
/// y todo lo que impediría o desaconsejaría emitir. No guarda nada ni toca al SRI.
/// </summary>
public sealed record InvoicePreviewDto(
    string SourceType,
    Guid SourceId,
    string Reference,
    DateOnly IssueDate,
    string Currency,
    string SourceStatus,
    InvoiceBuyerDto? Buyer,
    IReadOnlyList<InvoicePreviewLineDto> Lines,
    IReadOnlyList<InvoicePreviewPaymentDto> Payments,
    decimal PayableTotal,
    decimal PaidTotal,
    decimal FinalConsumerMaxAmount,
    bool IssuerReady,
    InvoiceCalculationDto? Calculation,
    string? EmissionPoint,
    Guid? ExistingInvoiceId,
    IReadOnlyList<InvoiceIssueDto> Issues,
    bool CanIssue);

public sealed record PreviewInvoiceSourceQuery(string SourceType, Guid SourceId, SriBuyerIdType? BuyerIdType, Guid? EmissionPointId = null) : IQuery<InvoicePreviewDto>;

public static class SourceErrors
{
    public static readonly Error NotFound = Error.NotFound("invoicing.source_not_found", "No se encontró lo que se quiere facturar.");
    public static readonly Error UnsupportedType = Error.Validation("invoicing.source_type_unsupported", "Ese tipo de origen no se puede facturar.");
}

// ---- Casos de uso -----------------------------------------------------------------------------------------------------------------

internal sealed class PreviewInvoiceSourceHandler(IAccessContext access, InvoicePlanner planner) : IQueryHandler<PreviewInvoiceSourceQuery, InvoicePreviewDto>
{
    public async Task<Result<InvoicePreviewDto>> HandleAsync(PreviewInvoiceSourceQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var plan = await planner.PlanAsync(query.SourceType, query.SourceId, query.BuyerIdType, query.EmissionPointId, cancellationToken);
        if (plan.IsFailure)
        {
            return plan.Error;
        }

        var p = plan.Value;
        var source = p.Source;
        var buyer = p.Buyer.Buyer is { } b
            ? new InvoiceBuyerDto(b.IdTypeCode, b.IdType.ToString(), b.IdNumber, b.LegalName, b.Address, b.Email, b.Phone, b.IsFinalConsumer)
            : null;

        return new InvoicePreviewDto(
            source.SourceType,
            source.SourceId,
            source.Reference,
            source.IssueDate,
            source.Currency,
            source.Status.ToString(),
            buyer,
            source.Lines.Select(l => new InvoicePreviewLineDto(l.Description, l.Quantity, l.GrossAmount, l.DiscountAmount, l.PayableAmount, l.Subtotal, l.TaxAmount, l.TaxKind, l.TaxPercent)).ToList(),
            source.Payments.Select(x => new InvoicePreviewPaymentDto(x.Method, x.Amount, x.PaidAt, x.Reference)).ToList(),
            source.PayableTotal,
            source.PaidTotal,
            p.FinalConsumerMaxAmount,
            p.IssuerReady,
            p.Calculation is null ? null : InvoiceCalculationDto.From(p.Calculation),
            p.Point is null ? null : $"{p.Point.EstablishmentCode}-{p.Point.PointCode}",
            p.ExistingInvoiceId,
            p.Issues,
            p.CanIssue);
    }
}
