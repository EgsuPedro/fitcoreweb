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

namespace FitCore.Invoicing.Application.Invoices;

// ---- Modelos ----------------------------------------------------------------------------------------------------------------------

/// <summary>Importe a acreditar de una línea de la factura, con IVA incluido. Sin importe: todo lo que quede de esa línea.</summary>
public sealed record CreditNoteLineRequest(Guid LineId, decimal? Amount = null);

/// <summary>Quien recibe la nota de crédito cuando la factura se hizo a consumidor final: el SRI exige identificar al receptor (no hay nota de crédito a consumidor final).</summary>
public sealed record CreditNoteBuyerRequest(SriBuyerIdType IdType, string IdNumber, string LegalName, string? Email = null, string? Address = null, string? Phone = null);

/// <summary>
/// Pide una nota de crédito sobre una factura autorizada. Sin <c>Lines</c> se acredita todo lo que quede de la factura. <c>Submit</c> la pone en la cola del SRI en el mismo acto.
/// </summary>
public sealed record IssueCreditNoteRequest(Guid InvoiceId, string Reason, IReadOnlyList<CreditNoteLineRequest>? Lines = null, CreditNoteBuyerRequest? Buyer = null, bool Submit = false);

public sealed record IssueCreditNoteCommand(IssueCreditNoteRequest Request) : ICommand<InvoiceDto>;

public sealed record CreditableLineDto(Guid LineId, int LineNumber, string Description, decimal TaxPercent, decimal OriginalAmount, decimal CreditedAmount, decimal RemainingAmount);

/// <summary>Lo que se puede acreditar de una factura: cada línea con lo ya acreditado y lo que queda (con IVA incluido), y lo que impide emitir.</summary>
public sealed record CreditNotePreviewDto(
    Guid InvoiceId,
    string Number,
    string Status,
    string BuyerName,
    bool BuyerIsFinalConsumer,
    decimal Total,
    decimal CreditedTotal,
    decimal RemainingTotal,
    IReadOnlyList<CreditableLineDto> Lines,
    IReadOnlyList<InvoiceIssueDto> Issues,
    bool CanIssue);

public sealed record GetCreditNotePreviewQuery(Guid InvoiceId) : IQuery<CreditNotePreviewDto>;

public static class CreditNoteIssueErrors
{
    public static readonly Error RequiresAuthorized = Error.Conflict("invoicing.credit_note_requires_authorized", "Solo se puede emitir una nota de crédito sobre una factura autorizada por el SRI.");
    public static readonly Error BuyerRequired = Error.Validation("invoicing.credit_note_buyer_required", "La factura se hizo a consumidor final: indique a quién se le emite la nota de crédito (el SRI exige identificar al receptor).");
    public static readonly Error BuyerNotAllowed = Error.Validation("invoicing.credit_note_buyer_not_allowed", "La nota de crédito va al mismo comprador de la factura: solo se indica comprador cuando la factura fue a consumidor final.");
    public static readonly Error EmissionPointUnavailable = Error.Validation("invoicing.credit_note_point_unavailable", "El punto de emisión de la factura ya no está activo: actívelo para emitir la nota de crédito.");
}

public sealed class IssueCreditNoteRequestValidator : AbstractValidator<IssueCreditNoteRequest>
{
    public IssueCreditNoteRequestValidator()
    {
        RuleFor(x => x.InvoiceId).NotEmpty();
        RuleFor(x => x.Reason).NotEmpty().MinimumLength(5).MaximumLength(300).WithMessage("Indique el motivo (entre 5 y 300 caracteres).");
        RuleForEach(x => x.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.LineId).NotEmpty();
            line.RuleFor(l => l.Amount).GreaterThan(0m).When(l => l.Amount is not null).WithMessage("El importe a acreditar debe ser mayor que cero.");
        });
        When(x => x.Buyer is not null, () =>
        {
            RuleFor(x => x.Buyer!.LegalName).NotEmpty().MaximumLength(BuyerRules.MaxNameLength);
            RuleFor(x => x.Buyer!.IdNumber).NotEmpty().MaximumLength(20);
            RuleFor(x => x.Buyer!.Email).MaximumLength(254);
            RuleFor(x => x.Buyer!.Address).MaximumLength(BuyerRules.MaxAddressLength);
            RuleFor(x => x.Buyer!.Phone).MaximumLength(40);
        });
    }
}

// ---- Apoyo ------------------------------------------------------------------------------------------------------------------------

internal static class CreditNoteSupport
{
    /// <summary>
    /// Lo ya acreditado de cada línea de la factura por notas de crédito vigentes. Las descartadas y las no autorizadas liberan su importe; las devueltas, en trámite o autorizadas lo reservan
    /// (una devuelta puede reenviarse y no debe poder acreditarse dos veces).
    /// </summary>
    public static async Task<IReadOnlyDictionary<Guid, CreditedAmount>> CreditedByLineAsync(InvoicingDbContext db, Guid invoiceId, CancellationToken cancellationToken)
    {
        var rows = await db.InvoiceLines.AsNoTracking()
            .Where(l => l.ModifiedLineId != null)
            .Join(
                db.Invoices.AsNoTracking().Where(n => n.ModifiedInvoiceId == invoiceId && n.Status != InvoiceStatus.Discarded && n.Status != InvoiceStatus.NotAuthorized),
                l => l.InvoiceId, n => n.Id, (l, n) => l)
            .GroupBy(l => l.ModifiedLineId!.Value)
            .Select(g => new { LineId = g.Key, Base = g.Sum(l => l.Subtotal), Tax = g.Sum(l => l.TaxAmount) })
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(r => r.LineId, r => new CreditedAmount(r.Base, r.Tax));
    }
}

// ---- Casos de uso -----------------------------------------------------------------------------------------------------------------

internal sealed class GetCreditNotePreviewHandler(IAccessContext access, InvoicingDbContext db) : IQueryHandler<GetCreditNotePreviewQuery, CreditNotePreviewDto>
{
    public async Task<Result<CreditNotePreviewDto>> HandleAsync(GetCreditNotePreviewQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var invoice = await db.Invoices.AsNoTracking().Include(i => i.Lines).VisibleTo(actor.Value)
            .FirstOrDefaultAsync(i => i.Id == query.InvoiceId && i.DocumentType == InvoiceDocumentType.Invoice, cancellationToken);
        if (invoice is null)
        {
            return InvoiceErrors.NotFound;
        }

        var creditable = CreditNoteCalculator.Creditable(invoice, await CreditNoteSupport.CreditedByLineAsync(db, invoice.Id, cancellationToken));
        var issues = new List<InvoiceIssueDto>();
        if (invoice.Status != InvoiceStatus.Authorized)
        {
            issues.Add(new InvoiceIssueDto("invoice_not_authorized", true));
        }

        if (creditable.All(c => c.RemainingBase <= 0m))
        {
            issues.Add(new InvoiceIssueDto("invoice_fully_credited", true));
        }

        if (!await db.EmissionPoints.AsNoTracking().AnyAsync(p => p.Id == invoice.EmissionPointId && p.IsActive, cancellationToken))
        {
            issues.Add(new InvoiceIssueDto("emission_point_invalid", true));
        }

        if (invoice.BuyerIdType == SriBuyerIdType.FinalConsumer)
        {
            issues.Add(new InvoiceIssueDto("credit_note_buyer_required", false));
        }

        var credited = creditable.Sum(c => c.Credited.Gross);
        return new CreditNotePreviewDto(
            invoice.Id, invoice.Number, invoice.Status.ToString(), invoice.BuyerName, invoice.BuyerIdType == SriBuyerIdType.FinalConsumer, invoice.Total, credited, invoice.Total - credited,
            [.. creditable.Select(c => new CreditableLineDto(c.Line.Id, c.Line.LineNumber, c.Line.Description, c.Line.TaxPercent, c.OriginalGross, c.Credited.Gross, c.RemainingGross))],
            issues, issues.All(i => !i.Blocking));
    }
}

internal sealed class IssueCreditNoteHandler(IAccessContext access, InvoicingDbContext db, IClock clock) : ICommandHandler<IssueCreditNoteCommand, InvoiceDto>
{
    public async Task<Result<InvoiceDto>> HandleAsync(IssueCreditNoteCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var r = command.Request;
        if (!await db.Invoices.AsNoTracking().VisibleTo(actor.Value).AnyAsync(i => i.Id == r.InvoiceId && i.DocumentType == InvoiceDocumentType.Invoice, cancellationToken))
        {
            return InvoiceErrors.NotFound;
        }

        var now = clock.UtcNow;
        var issueDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(Tenant.DefaultTimeZone)).DateTime);
        Guid noteId;
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            // El candado de fila de la factura: dos notas de crédito al mismo tiempo sobre la misma factura se atienden una tras otra, y la segunda ya ve lo que acreditó la primera.
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM invoicing.invoices WHERE id = {r.InvoiceId} FOR UPDATE", cancellationToken);
            var original = await db.Invoices.Include(i => i.Lines).AsSplitQuery().SingleAsync(i => i.Id == r.InvoiceId, cancellationToken);
            if (original.Status != InvoiceStatus.Authorized)
            {
                return CreditNoteIssueErrors.RequiresAuthorized;
            }

            var buyer = ResolveBuyer(original, r.Buyer);
            if (buyer.IsFailure)
            {
                return buyer.Error;
            }

            var point = await db.EmissionPoints.AsNoTracking().FirstOrDefaultAsync(p => p.Id == original.EmissionPointId && p.IsActive, cancellationToken);
            if (point is null)
            {
                return CreditNoteIssueErrors.EmissionPointUnavailable;
            }

            var creditable = CreditNoteCalculator.Creditable(original, await CreditNoteSupport.CreditedByLineAsync(db, original.Id, cancellationToken));
            var calculation = CreditNoteCalculator.Calculate(creditable, r.Lines?.Select(l => new CreditRequestLine(l.LineId, l.Amount)).ToList());
            if (calculation.IsFailure)
            {
                return calculation.Error;
            }

            var c = calculation.Value;
            var tenantId = actor.Value.TenantId;
            var environment = original.Environment.ToString();
            var sequential = (await db.Database.SqlQuery<int>($"""
                INSERT INTO invoicing.emission_sequences (tenant_id, emission_point_id, document_type, environment, last_value)
                VALUES ({tenantId}, {point.Id}, {nameof(InvoiceDocumentType.CreditNote)}, {environment}, 1)
                ON CONFLICT (tenant_id, emission_point_id, document_type, environment)
                DO UPDATE SET last_value = invoicing.emission_sequences.last_value + 1
                RETURNING last_value AS "Value"
                """).ToListAsync(cancellationToken)).Single();

            var numericCode = AccessKey.NewNumericCode();
            var b = buyer.Value;
            var note = new Invoice
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                DocumentType = InvoiceDocumentType.CreditNote,
                SourceType = "invoice",
                SourceId = original.Id,
                SourceReference = original.Number,
                BranchId = original.BranchId,
                EmissionPointId = point.Id,
                EstablishmentCode = point.EstablishmentCode,
                PointCode = point.PointCode,
                Sequential = sequential,
                Environment = original.Environment,
                IssueDate = issueDate,
                NumericCode = numericCode,
                AccessKey = AccessKey.Build(issueDate, SriCatalogs.CreditNoteDocumentCode, original.IssuerRuc, original.Environment, point.EstablishmentCode, point.PointCode, sequential, numericCode),
                IssuerRuc = original.IssuerRuc,
                IssuerLegalName = original.IssuerLegalName,
                IssuerTradeName = original.IssuerTradeName,
                IssuerHeadOfficeAddress = original.IssuerHeadOfficeAddress,
                IssuerEstablishmentAddress = point.EstablishmentAddress,
                TaxRegime = original.TaxRegime,
                AccountingRequired = original.AccountingRequired,
                SpecialTaxpayerNumber = original.SpecialTaxpayerNumber,
                WithholdingAgentNumber = original.WithholdingAgentNumber,
                BuyerIdType = b.IdType,
                BuyerIdNumber = b.IdNumber,
                BuyerName = b.LegalName,
                BuyerAddress = b.Address,
                BuyerEmail = b.Email,
                BuyerPhone = b.Phone,
                BuyerPersonId = b.PersonId,
                ModifiedInvoiceId = original.Id,
                ModifiedDocumentNumber = original.Number,
                ModifiedIssueDate = original.IssueDate,
                ModificationReason = r.Reason.Trim(),
                Currency = original.Currency,
                SubtotalWithoutTaxes = c.SubtotalWithoutTaxes,
                TotalDiscount = 0m,
                TotalTax = c.TotalTax,
                Total = c.Total,
                RoundingDifference = 0m,
                CreatedAt = now,
                CreatedBy = actor.Value.UserId
            };

            foreach (var l in c.Lines)
            {
                note.Lines.Add(new InvoiceLine
                {
                    Id = Guid.CreateVersion7(), TenantId = tenantId, InvoiceId = note.Id, LineNumber = l.LineNumber, MainCode = l.MainCode, Description = l.Description, Quantity = 1m,
                    UnitPrice = l.Base, Discount = 0m, Subtotal = l.Base, TaxKind = l.TaxKind, TaxCode = l.TaxCode, TaxRateCode = l.RateCode, TaxPercent = l.TaxPercent, TaxAmount = l.TaxAmount,
                    ModifiedLineId = l.ModifiedLineId
                });
            }

            foreach (var t in c.Taxes)
            {
                note.Taxes.Add(new InvoiceTax
                {
                    Id = Guid.CreateVersion7(), TenantId = tenantId, InvoiceId = note.Id, TaxCode = t.TaxCode, RateCode = t.RateCode, Percent = t.Percent, TaxableBase = t.TaxableBase, Amount = t.Amount
                });
            }

            db.Invoices.Add(note);
            db.InvoiceEvents.Add(new InvoiceEvent
            {
                Id = Guid.CreateVersion7(), TenantId = tenantId, InvoiceId = note.Id, Type = "Created", FromStatus = null, ToStatus = note.Status, ActorId = actor.Value.UserId, At = now,
                Detail = $"Nota de crédito de la factura {original.Number}"
            });
            TransmissionQueue.AddEvent(db, original, "CreditNoteIssued", $"Nota de crédito {note.Number}", actor.Value.UserId, now);
            await db.SaveChangesAsync(cancellationToken);
            if (r.Submit)
            {
                await TransmissionQueue.EnqueueAsync(db, note, actor.Value.UserId, now, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            noteId = note.Id;
        }

        db.ChangeTracker.Clear();
        return await InvoiceReader.ReadAsync(db, noteId, cancellationToken);
    }

    /// <summary>El mismo comprador de la factura; si fue consumidor final, el que se indique (con las mismas reglas de identificación de la factura).</summary>
    private static Result<InvoiceBuyerSnapshot> ResolveBuyer(Invoice original, CreditNoteBuyerRequest? requested)
    {
        if (original.BuyerIdType != SriBuyerIdType.FinalConsumer)
        {
            return requested is null
                ? new InvoiceBuyerSnapshot(original.BuyerIdType, original.BuyerIdNumber, original.BuyerName, original.BuyerAddress, original.BuyerEmail, original.BuyerPhone, original.BuyerPersonId)
                : CreditNoteIssueErrors.BuyerNotAllowed;
        }

        if (requested is null || requested.IdType == SriBuyerIdType.FinalConsumer)
        {
            return CreditNoteIssueErrors.BuyerRequired;
        }

        var source = new InvoiceSourceBuyer(null, requested.LegalName, string.Empty, SourceIdKind.Other, requested.IdNumber, requested.Email, requested.Phone, requested.Address);
        var resolved = BuyerRules.Resolve(source, requested.IdType, original.Total, decimal.MaxValue);
        var blocking = resolved.Issues.Where(i => i.Blocking).Select(i => i.Code).ToList();
        return blocking.Count > 0 || resolved.Buyer is null
            ? Error.Validation("invoicing.credit_note_buyer_invalid", $"Los datos del receptor no son válidos: {string.Join(", ", blocking)}.")
            : resolved.Buyer;
    }
}
