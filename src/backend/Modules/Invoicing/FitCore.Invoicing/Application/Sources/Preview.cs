using FitCore.Application.Core;
using FitCore.Invoicing.Application.Security;
using FitCore.Invoicing.Application.Settings;
using FitCore.Invoicing.Domain;
using FitCore.Platform.Application;
using FitCore.Platform.Security;
using FitCore.SharedKernel;
using Microsoft.Extensions.Options;

namespace FitCore.Invoicing.Application.Sources;

// ---- Modelos ----------------------------------------------------------------------------------------------------------------------

public sealed record InvoiceBuyerDto(string IdTypeCode, string IdType, string IdNumber, string LegalName, string? Address, string? Email, string? Phone, bool IsFinalConsumer);

public sealed record InvoicePreviewLineDto(
    string Description, decimal Quantity, decimal GrossAmount, decimal DiscountAmount, decimal PayableAmount, decimal Subtotal, decimal TaxAmount, string TaxKind, decimal TaxPercent);

public sealed record InvoicePreviewPaymentDto(string Method, decimal Amount, DateTimeOffset PaidAt, string? Reference);

public sealed record InvoiceIssueDto(string Code, bool Blocking);

/// <summary>
/// Lo que se facturaría con un origen (hoy, una obligación cobrada): comprador resuelto con los tipos del SRI, líneas con impuestos congelados, pagos y todo lo que
/// impediría o desaconsejaría emitir. No guarda nada ni toca al SRI.
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
    IReadOnlyList<InvoiceIssueDto> Issues,
    bool CanIssue);

public sealed record PreviewInvoiceSourceQuery(string SourceType, Guid SourceId, SriBuyerIdType? BuyerIdType) : IQuery<InvoicePreviewDto>;

public static class SourceErrors
{
    public static readonly Error NotFound = Error.NotFound("invoicing.source_not_found", "No se encontró lo que se quiere facturar.");
    public static readonly Error UnsupportedType = Error.Validation("invoicing.source_type_unsupported", "Ese tipo de origen no se puede facturar.");
}

// ---- Casos de uso -----------------------------------------------------------------------------------------------------------------

internal sealed class PreviewInvoiceSourceHandler(
    IAccessContext access, IInvoiceSourceProvider provider, SettingsReader settings, IOptions<InvoicingOptions> options)
    : IQueryHandler<PreviewInvoiceSourceQuery, InvoicePreviewDto>
{
    public async Task<Result<InvoicePreviewDto>> HandleAsync(PreviewInvoiceSourceQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        if (query.SourceType != InvoiceSourceTypes.Obligation)
        {
            return SourceErrors.UnsupportedType;
        }

        var source = await provider.LoadAsync(query.SourceType, query.SourceId, cancellationToken);
        if (source is null)
        {
            return SourceErrors.NotFound;
        }

        var limit = options.Value.FinalConsumerMaxAmount;
        var total = source.PayableTotal;
        var resolution = BuyerRules.Resolve(source.Buyer, query.BuyerIdType, total, limit);

        var issues = new List<InvoiceIssueDto>();
        switch (source.Status)
        {
            case InvoiceSourceStatus.Voided:
                issues.Add(new InvoiceIssueDto("source_voided", true));
                break;
            case InvoiceSourceStatus.Open:
                issues.Add(new InvoiceIssueDto("source_not_paid", true));
                break;
        }

        if (source.Currency != "USD")
        {
            issues.Add(new InvoiceIssueDto("currency_unsupported", true));
        }

        if (total <= 0m)
        {
            issues.Add(new InvoiceIssueDto("amount_not_positive", true));
        }

        issues.AddRange(resolution.Issues.Select(i => new InvoiceIssueDto(i.Code, i.Blocking)));

        var configuration = await settings.ReadAsync(cancellationToken);
        if (!configuration.CanIssue)
        {
            issues.Add(new InvoiceIssueDto("issuer_not_ready", true));
        }

        var buyer = resolution.Buyer is { } b
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
            source.Payments.Select(p => new InvoicePreviewPaymentDto(p.Method, p.Amount, p.PaidAt, p.Reference)).ToList(),
            total,
            source.PaidTotal,
            limit,
            configuration.CanIssue,
            issues,
            issues.All(i => !i.Blocking));
    }
}
