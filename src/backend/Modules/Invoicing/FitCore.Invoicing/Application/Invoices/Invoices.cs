using System.Globalization;
using System.Text.RegularExpressions;
using FitCore.Application.Core;
using FitCore.Invoicing.Application.Settings;
using FitCore.Invoicing.Application.Sources;
using FitCore.Invoicing.Domain;
using FitCore.Invoicing.Persistence;
using FitCore.Platform.Domain;
using FitCore.Platform.Security;
using FitCore.SharedKernel;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FitCore.Invoicing.Application.Invoices;

// ---- Modelos ----------------------------------------------------------------------------------------------------------------------

public sealed record InvoiceLineDto(
    int LineNumber, string MainCode, string Description, decimal Quantity, decimal UnitPrice, decimal Discount, decimal Subtotal, string TaxKind, string TaxRateCode, decimal TaxPercent, decimal TaxAmount);

public sealed record InvoiceTaxDto(string TaxCode, string RateCode, decimal Percent, decimal TaxableBase, decimal Amount);

public sealed record InvoicePaymentDto(string MethodCode, string Method, decimal Amount);

/// <summary>Resultado del cálculo: lo mismo se muestra en la vista previa y se guarda en el comprobante.</summary>
public sealed record InvoiceCalculationDto(
    IReadOnlyList<InvoiceLineDto> Lines,
    IReadOnlyList<InvoiceTaxDto> Taxes,
    IReadOnlyList<InvoicePaymentDto> Payments,
    decimal SubtotalWithoutTaxes,
    decimal TotalDiscount,
    decimal TotalTax,
    decimal Total,
    decimal RoundingDifference)
{
    public static InvoiceCalculationDto From(InvoiceCalculation c) => new(
        [.. c.Lines.Select(l => new InvoiceLineDto(l.LineNumber, l.MainCode, l.Description, l.Quantity, l.UnitPrice, l.Discount, l.Subtotal, l.TaxKind, l.RateCode, l.TaxPercent, l.TaxAmount))],
        [.. c.Taxes.Select(t => new InvoiceTaxDto(t.TaxCode, t.RateCode, t.Percent, t.TaxableBase, t.Amount))],
        [.. c.Payments.Select(p => new InvoicePaymentDto(p.MethodCode, p.Method, p.Amount))],
        c.SubtotalWithoutTaxes, c.TotalDiscount, c.TotalTax, c.Total, c.RoundingDifference);
}

public sealed record InvoiceEventDto(string Type, string? FromStatus, string ToStatus, string? Detail, Guid? ActorId, DateTimeOffset At);

public sealed record InvoiceIssuerDto(string Ruc, string LegalName, string? TradeName, string HeadOfficeAddress, string EstablishmentAddress, string TaxRegime, bool AccountingRequired, string? SpecialTaxpayerNumber, string? WithholdingAgentNumber);

public sealed record InvoiceDto(
    Guid Id,
    string DocumentType,
    string Number,
    string AccessKey,
    string Status,
    string Environment,
    DateOnly IssueDate,
    string SourceType,
    Guid SourceId,
    string SourceReference,
    Guid BranchId,
    Guid EmissionPointId,
    InvoiceIssuerDto Issuer,
    InvoiceBuyerDto Buyer,
    InvoiceCalculationDto Calculation,
    string Currency,
    DateTimeOffset CreatedAt,
    IReadOnlyList<InvoiceEventDto> Events)
{
    internal static InvoiceDto From(Invoice i, IReadOnlyList<InvoiceEvent> events) => new(
        i.Id,
        i.DocumentType.ToString(),
        i.Number,
        i.AccessKey,
        i.Status.ToString(),
        i.Environment.ToString(),
        i.IssueDate,
        i.SourceType,
        i.SourceId,
        i.SourceReference,
        i.BranchId,
        i.EmissionPointId,
        new InvoiceIssuerDto(i.IssuerRuc, i.IssuerLegalName, i.IssuerTradeName, i.IssuerHeadOfficeAddress, i.IssuerEstablishmentAddress, i.TaxRegime.ToString(), i.AccountingRequired, i.SpecialTaxpayerNumber, i.WithholdingAgentNumber),
        new InvoiceBuyerDto(i.BuyerIdType.Code(), i.BuyerIdType.ToString(), i.BuyerIdNumber, i.BuyerName, i.BuyerAddress, i.BuyerEmail, i.BuyerPhone, i.BuyerIdType == SriBuyerIdType.FinalConsumer),
        new InvoiceCalculationDto(
            [.. i.Lines.OrderBy(l => l.LineNumber).Select(l => new InvoiceLineDto(l.LineNumber, l.MainCode, l.Description, l.Quantity, l.UnitPrice, l.Discount, l.Subtotal, l.TaxKind, l.TaxRateCode, l.TaxPercent, l.TaxAmount))],
            [.. i.Taxes.OrderBy(t => t.RateCode, StringComparer.Ordinal).Select(t => new InvoiceTaxDto(t.TaxCode, t.RateCode, t.Percent, t.TaxableBase, t.Amount))],
            [.. i.Payments.Select(p => new InvoicePaymentDto(p.MethodCode, p.Method, p.Amount))],
            i.SubtotalWithoutTaxes, i.TotalDiscount, i.TotalTax, i.Total, i.RoundingDifference),
        i.Currency,
        i.CreatedAt,
        [.. events.OrderBy(e => e.At).Select(e => new InvoiceEventDto(e.Type, e.FromStatus?.ToString(), e.ToStatus.ToString(), e.Detail, e.ActorId, e.At))]);
}

public sealed record InvoiceListItemDto(
    Guid Id, string Number, string DocumentType, DateOnly IssueDate, string Status, string Environment, string BuyerName, string BuyerIdNumber, decimal Total, string SourceReference, Guid BranchId);

/// <summary>Pide facturar una venta. <c>BuyerIdType</c> permite elegir el tipo de identificación (por ejemplo, consumidor final); <c>EmissionPointId</c>, un punto de emisión distinto del que corresponde por defecto.</summary>
public sealed record IssueInvoiceRequest(string SourceType, Guid SourceId, SriBuyerIdType? BuyerIdType = null, Guid? EmissionPointId = null);

/// <summary><c>Created</c> es falso cuando la venta ya estaba facturada: emitir dos veces la misma venta devuelve el mismo comprobante.</summary>
public sealed record IssueInvoiceResult(InvoiceDto Invoice, bool Created);

public sealed record IssueInvoiceCommand(IssueInvoiceRequest Request) : ICommand<IssueInvoiceResult>;

public sealed record GetInvoiceQuery(Guid Id) : IQuery<InvoiceDto>;

public sealed record ListInvoicesQuery(int Page, int PageSize, InvoiceStatus? Status, string? Search) : IQuery<PagedResult<InvoiceListItemDto>>;

public static class InvoiceErrors
{
    public static readonly Error NotFound = Error.NotFound("invoicing.invoice_not_found", "Comprobante no encontrado.");
    public static readonly Error CannotIssue = Error.Validation("invoicing.cannot_issue", "La venta no se puede facturar todavía.");
}

public sealed class IssueInvoiceRequestValidator : AbstractValidator<IssueInvoiceRequest>
{
    public IssueInvoiceRequestValidator()
    {
        RuleFor(x => x.SourceType).NotEmpty().MaximumLength(30);
        RuleFor(x => x.SourceId).NotEmpty();
        RuleFor(x => x.BuyerIdType).IsInEnum().When(x => x.BuyerIdType is not null);
    }
}

// ---- Acceso -----------------------------------------------------------------------------------------------------------------------

internal static class InvoiceScope
{
    /// <summary>Restringe los comprobantes a las sucursales del usuario (todas, o solo las asignadas).</summary>
    public static IQueryable<Invoice> VisibleTo(this IQueryable<Invoice> invoices, UserAccess actor)
    {
        if (actor.AllBranches)
        {
            return invoices;
        }

        var ids = actor.BranchIds.ToList();
        return invoices.Where(i => ids.Contains(i.BranchId));
    }
}

// ---- Casos de uso -----------------------------------------------------------------------------------------------------------------

internal sealed class IssueInvoiceHandler(IAccessContext access, InvoicePlanner planner, InvoicingDbContext db, IClock clock)
    : ICommandHandler<IssueInvoiceCommand, IssueInvoiceResult>
{
    public async Task<Result<IssueInvoiceResult>> HandleAsync(IssueInvoiceCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var r = command.Request;
        var plan = await planner.PlanAsync(r.SourceType, r.SourceId, r.BuyerIdType, r.EmissionPointId, cancellationToken);
        if (plan.IsFailure)
        {
            return plan.Error;
        }

        var p = plan.Value;
        if (p.ExistingInvoiceId is { } existingId)
        {
            return new IssueInvoiceResult(await LoadAsync(existingId, cancellationToken), Created: false);
        }

        if (!p.CanIssue || p.Calculation is null || p.Buyer.Buyer is null || p.Point is null || p.Issuer is null)
        {
            var blocking = string.Join(", ", p.Issues.Where(i => i.Blocking).Select(i => i.Code));
            return Error.Validation(InvoiceErrors.CannotIssue.Code, $"{InvoiceErrors.CannotIssue.Description} Pendiente: {blocking}.");
        }

        var now = clock.UtcNow;
        var issueDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(Tenant.DefaultTimeZone)).DateTime);
        var tenantId = actor.Value.TenantId;
        var point = p.Point;
        var issuer = p.Issuer;
        var buyer = p.Buyer.Buyer;
        var calculation = p.Calculation;

        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            // El secuencial se asigna con una sola sentencia atómica dentro de la misma transacción que el comprobante: quien llega segundo espera el candado de la fila, los números
            // salen sin huecos y, si algo falla, el número no se consume.
            var environment = issuer.Environment.ToString();
            var sequential = (await db.Database.SqlQuery<int>($"""
                INSERT INTO invoicing.emission_sequences (tenant_id, emission_point_id, document_type, environment, last_value)
                VALUES ({tenantId}, {point.Id}, {nameof(InvoiceDocumentType.Invoice)}, {environment}, 1)
                ON CONFLICT (tenant_id, emission_point_id, document_type, environment)
                DO UPDATE SET last_value = invoicing.emission_sequences.last_value + 1
                RETURNING last_value AS "Value"
                """).ToListAsync(cancellationToken)).Single();

            var numericCode = AccessKey.NewNumericCode();
            var invoice = new Invoice
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                SourceType = p.Source.SourceType,
                SourceId = p.Source.SourceId,
                SourceReference = p.Source.Reference,
                BranchId = p.Source.BranchId,
                EmissionPointId = point.Id,
                EstablishmentCode = point.EstablishmentCode,
                PointCode = point.PointCode,
                Sequential = sequential,
                Environment = issuer.Environment,
                IssueDate = issueDate,
                NumericCode = numericCode,
                AccessKey = AccessKey.Build(issueDate, SriCatalogs.InvoiceDocumentCode, issuer.Ruc, issuer.Environment, point.EstablishmentCode, point.PointCode, sequential, numericCode),
                IssuerRuc = issuer.Ruc,
                IssuerLegalName = issuer.LegalName,
                IssuerTradeName = issuer.TradeName,
                IssuerHeadOfficeAddress = issuer.HeadOfficeAddress,
                IssuerEstablishmentAddress = point.EstablishmentAddress,
                TaxRegime = issuer.TaxRegime,
                AccountingRequired = issuer.AccountingRequired,
                SpecialTaxpayerNumber = issuer.SpecialTaxpayerNumber,
                WithholdingAgentNumber = issuer.WithholdingAgentNumber,
                BuyerIdType = buyer.IdType,
                BuyerIdNumber = buyer.IdNumber,
                BuyerName = buyer.LegalName,
                BuyerAddress = buyer.Address,
                BuyerEmail = buyer.Email,
                BuyerPhone = buyer.Phone,
                BuyerPersonId = buyer.PersonId,
                Currency = p.Source.Currency,
                SubtotalWithoutTaxes = calculation.SubtotalWithoutTaxes,
                TotalDiscount = calculation.TotalDiscount,
                TotalTax = calculation.TotalTax,
                Total = calculation.Total,
                RoundingDifference = calculation.RoundingDifference,
                CreatedAt = now,
                CreatedBy = actor.Value.UserId
            };

            foreach (var l in calculation.Lines)
            {
                invoice.Lines.Add(new InvoiceLine
                {
                    Id = Guid.CreateVersion7(), TenantId = tenantId, InvoiceId = invoice.Id, LineNumber = l.LineNumber, MainCode = l.MainCode, Description = l.Description,
                    Quantity = l.Quantity, UnitPrice = l.UnitPrice, Discount = l.Discount, Subtotal = l.Subtotal, TaxKind = l.TaxKind, TaxCode = SriCatalogs.IvaTaxCode,
                    TaxRateCode = l.RateCode, TaxPercent = l.TaxPercent, TaxAmount = l.TaxAmount
                });
            }

            foreach (var t in calculation.Taxes)
            {
                invoice.Taxes.Add(new InvoiceTax
                {
                    Id = Guid.CreateVersion7(), TenantId = tenantId, InvoiceId = invoice.Id, TaxCode = t.TaxCode, RateCode = t.RateCode, Percent = t.Percent, TaxableBase = t.TaxableBase, Amount = t.Amount
                });
            }

            foreach (var pay in calculation.Payments)
            {
                invoice.Payments.Add(new InvoicePayment { Id = Guid.CreateVersion7(), TenantId = tenantId, InvoiceId = invoice.Id, MethodCode = pay.MethodCode, Method = pay.Method, Amount = pay.Amount });
            }

            db.Invoices.Add(invoice);
            db.InvoiceEvents.Add(new InvoiceEvent
            {
                Id = Guid.CreateVersion7(), TenantId = tenantId, InvoiceId = invoice.Id, Type = "Created", FromStatus = null, ToStatus = invoice.Status, ActorId = actor.Value.UserId, At = now,
                Detail = $"Origen {invoice.SourceType} {invoice.SourceReference}"
            });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new IssueInvoiceResult(await LoadAsync(invoice.Id, cancellationToken), Created: true);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Otra solicitud facturó esta venta al mismo tiempo: su comprobante es el válido y el número que reservamos se deshizo con la transacción.
            db.ChangeTracker.Clear();
            var existing = await db.Invoices.AsNoTracking()
                .Where(i => i.SourceType == r.SourceType && i.SourceId == r.SourceId && i.DocumentType == InvoiceDocumentType.Invoice)
                .Select(i => (Guid?)i.Id)
                .FirstOrDefaultAsync(cancellationToken);
            if (existing is null)
            {
                throw;
            }

            return new IssueInvoiceResult(await LoadAsync(existing.Value, cancellationToken), Created: false);
        }
    }

    private async Task<InvoiceDto> LoadAsync(Guid id, CancellationToken cancellationToken)
    {
        var invoice = await db.Invoices.AsNoTracking().Include(i => i.Lines).Include(i => i.Taxes).Include(i => i.Payments).AsSplitQuery().SingleAsync(i => i.Id == id, cancellationToken);
        var events = await db.InvoiceEvents.AsNoTracking().Where(e => e.InvoiceId == id).ToListAsync(cancellationToken);
        return InvoiceDto.From(invoice, events);
    }
}

internal sealed class GetInvoiceHandler(IAccessContext access, InvoicingDbContext db) : IQueryHandler<GetInvoiceQuery, InvoiceDto>
{
    public async Task<Result<InvoiceDto>> HandleAsync(GetInvoiceQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var invoice = await db.Invoices.AsNoTracking().VisibleTo(actor.Value)
            .Include(i => i.Lines).Include(i => i.Taxes).Include(i => i.Payments).AsSplitQuery()
            .FirstOrDefaultAsync(i => i.Id == query.Id, cancellationToken);
        if (invoice is null)
        {
            return InvoiceErrors.NotFound;
        }

        var events = await db.InvoiceEvents.AsNoTracking().Where(e => e.InvoiceId == invoice.Id).ToListAsync(cancellationToken);
        return InvoiceDto.From(invoice, events);
    }
}

internal sealed partial class ListInvoicesHandler(IAccessContext access, InvoicingDbContext db) : IQueryHandler<ListInvoicesQuery, PagedResult<InvoiceListItemDto>>
{
    [GeneratedRegex(@"^(\d{3})-(\d{3})-(\d{1,9})$")]
    private static partial Regex NumberPattern();

    public async Task<Result<PagedResult<InvoiceListItemDto>>> HandleAsync(ListInvoicesQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var page = Math.Max(1, query.Page);
        var size = Math.Clamp(query.PageSize, 1, 100);
        var invoices = db.Invoices.AsNoTracking().VisibleTo(actor.Value);
        if (query.Status is { } status)
        {
            invoices = invoices.Where(i => i.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            if (NumberPattern().Match(term) is { Success: true } number)
            {
                // «001-001-000000123»: se busca por establecimiento, punto y secuencial.
                var establishment = number.Groups[1].Value;
                var point = number.Groups[2].Value;
                var sequential = int.Parse(number.Groups[3].Value, CultureInfo.InvariantCulture);
                invoices = invoices.Where(i => i.EstablishmentCode == establishment && i.PointCode == point && i.Sequential == sequential);
            }
            else
            {
                var like = $"%{term.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal)}%";
                invoices = invoices.Where(i => EF.Functions.ILike(i.BuyerName, like) || i.BuyerIdNumber.StartsWith(term) || i.AccessKey.StartsWith(term) || i.SourceReference == term);
            }
        }

        var total = await invoices.CountAsync(cancellationToken);
        var rows = await invoices.OrderByDescending(i => i.IssueDate).ThenByDescending(i => i.Sequential).Skip((page - 1) * size).Take(size).ToListAsync(cancellationToken);
        var items = rows.Select(i => new InvoiceListItemDto(
            i.Id, i.Number, i.DocumentType.ToString(), i.IssueDate, i.Status.ToString(), i.Environment.ToString(), i.BuyerName, i.BuyerIdNumber, i.Total, i.SourceReference, i.BranchId)).ToList();
        return new PagedResult<InvoiceListItemDto>(items, page, size, total);
    }
}
