using FitCore.Application.Core;
using FitCore.Invoicing.Application.Security;
using FitCore.Invoicing.Application.Settings;
using FitCore.Invoicing.Domain;
using FitCore.Invoicing.Persistence;
using FitCore.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FitCore.Invoicing.Application.Sources;

/// <summary>Todo lo que se sabe de una venta antes de emitir: el origen, el comprador resuelto, el cálculo, el punto de emisión elegido y lo que impide o desaconseja emitir.</summary>
internal sealed record PlanOutcome(
    InvoiceSource Source,
    BuyerResolution Buyer,
    InvoiceCalculation? Calculation,
    IReadOnlyList<InvoiceIssueDto> Issues,
    bool IssuerReady,
    IssuerProfile? Issuer,
    EmissionPoint? Point,
    Guid? ExistingInvoiceId,
    decimal FinalConsumerMaxAmount)
{
    public bool CanIssue => Issues.All(i => !i.Blocking);
}

/// <summary>
/// Reúne y valida lo necesario para facturar una venta. La vista previa y la emisión usan exactamente este mismo plan: lo que se ve antes de emitir es lo que se emite, y ninguna regla
/// vive solo en un lado.
/// </summary>
internal sealed class InvoicePlanner(IInvoiceSourceProvider provider, SettingsReader settings, InvoicingDbContext db, IOptions<InvoicingOptions> options)
{
    public async Task<Result<PlanOutcome>> PlanAsync(string sourceType, Guid sourceId, SriBuyerIdType? chosenBuyerType, Guid? emissionPointId, CancellationToken cancellationToken)
    {
        if (sourceType != InvoiceSourceTypes.Obligation)
        {
            return SourceErrors.UnsupportedType;
        }

        var source = await provider.LoadAsync(sourceType, sourceId, cancellationToken);
        if (source is null)
        {
            return SourceErrors.NotFound;
        }

        var issues = new List<InvoiceIssueDto>();
        var limit = options.Value.FinalConsumerMaxAmount;
        var total = source.PayableTotal;

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

        var buyer = BuyerRules.Resolve(source.Buyer, chosenBuyerType, total, limit);
        issues.AddRange(buyer.Issues.Select(i => new InvoiceIssueDto(i.Code, i.Blocking)));

        var issuer = await db.Issuers.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        InvoiceCalculation? calculation = null;
        if (total > 0m)
        {
            var calculated = InvoiceCalculator.Calculate(source, issuer?.CardPaymentMethod ?? CardPaymentMethod.Credit);
            if (calculated.IsSuccess)
            {
                calculation = calculated.Value;
            }
            else
            {
                issues.Add(new InvoiceIssueDto(calculated.Error.Code.Replace("invoicing.", string.Empty, StringComparison.Ordinal), true));
            }
        }

        var point = await ChoosePointAsync(source.BranchId, emissionPointId, cancellationToken);
        if (point is null)
        {
            issues.Add(new InvoiceIssueDto(emissionPointId is null ? "emission_point_missing" : "emission_point_invalid", true));
        }

        var existing = await db.Invoices.AsNoTracking()
            .Where(i => i.SourceType == sourceType && i.SourceId == sourceId && i.DocumentType == InvoiceDocumentType.Invoice && i.Status != InvoiceStatus.Discarded)
            .Select(i => (Guid?)i.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            issues.Add(new InvoiceIssueDto("already_invoiced", true));
        }

        var configuration = await settings.ReadAsync(cancellationToken);
        if (!configuration.CanIssue)
        {
            issues.Add(new InvoiceIssueDto("issuer_not_ready", true));
        }

        return new PlanOutcome(source, buyer, calculation, issues, configuration.CanIssue, issuer, point, existing, limit);
    }

    /// <summary>
    /// Punto de emisión: el pedido (activo y de la sucursal de la venta o sin sucursal) o, sin pedido, el activo de la sucursal de la venta y, si no hay, el activo sin sucursal
    /// (el de menor código). Nunca se elige uno de otra sucursal.
    /// </summary>
    private async Task<EmissionPoint?> ChoosePointAsync(Guid branchId, Guid? requested, CancellationToken cancellationToken)
    {
        var points = db.EmissionPoints.AsNoTracking().Where(p => p.IsActive && (p.BranchId == branchId || p.BranchId == null));
        if (requested is not null)
        {
            return await points.FirstOrDefaultAsync(p => p.Id == requested, cancellationToken);
        }

        return await points
            .OrderByDescending(p => p.BranchId == branchId)
            .ThenBy(p => p.EstablishmentCode)
            .ThenBy(p => p.PointCode)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
