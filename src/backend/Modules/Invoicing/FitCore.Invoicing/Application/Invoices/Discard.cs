using FitCore.Application.Core;
using FitCore.Invoicing.Application.Settings;
using FitCore.Invoicing.Domain;
using FitCore.Invoicing.Persistence;
using FitCore.Platform.Security;
using FitCore.SharedKernel;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace FitCore.Invoicing.Application.Invoices;

// ---- Modelos ----------------------------------------------------------------------------------------------------------------------

public sealed record DiscardInvoiceRequest(string Reason);

/// <summary>
/// Descarta un comprobante que no llegó a autorizarse (devuelto, no autorizado, o sin enviar todavía) para poder volver a facturar la venta con un número nuevo. No borra nada: el comprobante
/// queda como «descartado», con su motivo y quién lo hizo, y su secuencial no se reutiliza.
/// </summary>
public sealed record DiscardInvoiceCommand(Guid Id, string Reason) : ICommand<InvoiceDto>;

/// <summary>Cifras del panel de facturación. Todo respeta las sucursales de quien consulta.</summary>
public sealed record InvoicingSummaryDto(
    IReadOnlyDictionary<string, int> CountsByStatus,
    int InQueue,
    int Parked,
    int NeedsAttention,
    int EmailsFailed,
    int MonthAuthorizedCount,
    decimal MonthAuthorizedTotal);

public sealed record GetInvoicingSummaryQuery : IQuery<InvoicingSummaryDto>;

public static class DiscardErrors
{
    public static readonly Error NotDiscardable = Error.Conflict("invoicing.invoice_not_discardable", "Solo se puede descartar un comprobante devuelto, no autorizado o que todavía no se envió al SRI. Uno en trámite o autorizado no se descarta.");
}

public sealed class DiscardInvoiceRequestValidator : AbstractValidator<DiscardInvoiceRequest>
{
    public DiscardInvoiceRequestValidator() =>
        RuleFor(x => x.Reason).NotEmpty().MinimumLength(10).MaximumLength(300).WithMessage("Indique el motivo (entre 10 y 300 caracteres).");
}

// ---- Casos de uso -----------------------------------------------------------------------------------------------------------------

internal sealed class DiscardInvoiceHandler(IAccessContext access, InvoicingDbContext db, IClock clock) : ICommandHandler<DiscardInvoiceCommand, InvoiceDto>
{
    public async Task<Result<InvoiceDto>> HandleAsync(DiscardInvoiceCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        if (!await db.Invoices.AsNoTracking().VisibleTo(actor.Value).AnyAsync(i => i.Id == command.Id, cancellationToken))
        {
            return InvoiceErrors.NotFound;
        }

        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            // El mismo candado de fila que usan la firma, el envío y el Worker: no se descarta algo que el Worker está enviando en este instante.
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM invoicing.invoices WHERE id = {command.Id} FOR UPDATE", cancellationToken);
            var invoice = await db.Invoices.SingleAsync(i => i.Id == command.Id, cancellationToken);

            // Antes de enviarse (sin firmar o firmado) todavía no hay constancia alguna con el SRI; después, solo si el SRI lo devolvió o no lo autorizó.
            var discardable = invoice.Status is InvoiceStatus.Rejected or InvoiceStatus.NotAuthorized
                || (invoice.Status is InvoiceStatus.PendingSignature or InvoiceStatus.SignedDocument && !await db.SriAttempts.AnyAsync(a => a.InvoiceId == invoice.Id, cancellationToken));
            if (!discardable)
            {
                return DiscardErrors.NotDiscardable;
            }

            var queued = await db.Transmissions.FirstOrDefaultAsync(t => t.InvoiceId == invoice.Id, cancellationToken);
            if (queued is not null)
            {
                db.Transmissions.Remove(queued);
            }

            TransmissionQueue.Transition(db, invoice, InvoiceStatus.Discarded, "Discarded", command.Reason.Trim(), actor.Value.UserId, clock.UtcNow);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        db.ChangeTracker.Clear();
        return await InvoiceReader.ReadAsync(db, command.Id, cancellationToken);
    }
}

internal sealed class GetInvoicingSummaryHandler(IAccessContext access, InvoicingDbContext db, IClock clock) : IQueryHandler<GetInvoicingSummaryQuery, InvoicingSummaryDto>
{
    public async Task<Result<InvoicingSummaryDto>> HandleAsync(GetInvoicingSummaryQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var invoices = db.Invoices.AsNoTracking().VisibleTo(actor.Value);
        var counts = await invoices.GroupBy(i => i.Status).Select(g => new { Status = g.Key, Count = g.Count() }).ToListAsync(cancellationToken);
        var byStatus = Enum.GetValues<InvoiceStatus>().ToDictionary(s => s.ToString(), s => counts.FirstOrDefault(c => c.Status == s)?.Count ?? 0);

        var queue = db.Transmissions.AsNoTracking().Where(t => invoices.Any(i => i.Id == t.InvoiceId));
        var inQueue = await queue.CountAsync(t => !t.Parked, cancellationToken);
        var parked = await queue.CountAsync(t => t.Parked, cancellationToken);
        var emailsFailed = await db.InvoiceEmails.AsNoTracking().Where(e => e.Status == InvoiceEmailStatus.Failed && invoices.Any(i => i.Id == e.InvoiceId)).CountAsync(cancellationToken);

        var now = clock.UtcNow;
        var monthStart = new DateOnly(now.Year, now.Month, 1);
        var month = await invoices.Where(i => i.Status == InvoiceStatus.Authorized && i.IssueDate >= monthStart)
            .GroupBy(_ => 1).Select(g => new { Count = g.Count(), Total = g.Sum(i => i.Total) }).FirstOrDefaultAsync(cancellationToken);

        return new InvoicingSummaryDto(
            byStatus, inQueue, parked, byStatus[nameof(InvoiceStatus.Rejected)] + byStatus[nameof(InvoiceStatus.NotAuthorized)] + parked,
            emailsFailed, month?.Count ?? 0, month?.Total ?? 0m);
    }
}
