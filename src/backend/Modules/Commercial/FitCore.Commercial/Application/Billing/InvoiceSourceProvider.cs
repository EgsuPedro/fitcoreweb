using FitCore.Application.Core;
using FitCore.Commercial.Domain;
using FitCore.Commercial.Persistence;
using FitCore.Platform.Security;
using Microsoft.EntityFrameworkCore;

namespace FitCore.Commercial.Application.Billing;

/// <summary>
/// Entrega a la facturación electrónica lo que se cobró (contrato de BuildingBlocks): una obligación con su alumno y los pagos confirmados que la cubren. Solo lee y
/// respeta el alcance de sucursales de quien consulta, igual que el resto de pantallas de cobros.
/// </summary>
internal sealed class CommercialInvoiceSourceProvider(CommercialDbContext db, IAccessContext access) : IInvoiceSourceProvider
{
    public async Task<InvoiceSource?> LoadAsync(string sourceType, Guid sourceId, CancellationToken cancellationToken)
    {
        if (sourceType != InvoiceSourceTypes.Obligation)
        {
            return null;
        }

        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return null;
        }

        var row = await (from o in db.Obligations.AsNoTracking().VisibleTo(actor.Value)
                         join m in db.Members.AsNoTracking() on o.MemberId equals m.Id
                         where o.Id == sourceId
                         select new { Obligation = o, Member = m }).FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }

        var payments = await (from a in db.PaymentAllocations.AsNoTracking()
                              join p in db.Payments.AsNoTracking() on a.PaymentId equals p.Id
                              where a.ObligationId == sourceId && p.Status == PaymentStatus.Confirmed
                              orderby p.PaidAt
                              select new { p.Method, a.Amount, p.PaidAt, p.Reference }).ToListAsync(cancellationToken);

        var o2 = row.Obligation;
        var member = row.Member;
        var paid = payments.Sum(p => p.Amount);
        var status = o2.Status == ObligationStatus.Voided ? InvoiceSourceStatus.Voided
            : paid >= o2.PayableAmount ? InvoiceSourceStatus.Paid
            : InvoiceSourceStatus.Open;

        var buyer = new InvoiceSourceBuyer(
            member.Id,
            member.FirstName,
            member.LastName,
            member.IdType switch
            {
                IdentificationType.Cedula => SourceIdKind.Cedula,
                IdentificationType.Ruc => SourceIdKind.Ruc,
                IdentificationType.Passport => SourceIdKind.Passport,
                _ => SourceIdKind.Other
            },
            member.IdNumber,
            member.Email,
            member.Phone,
            member.Address);

        var line = new InvoiceSourceLine(
            o2.Concept, 1m, o2.OriginalAmount, o2.DiscountAmount, o2.PayableAmount, o2.Subtotal, o2.TaxAmount, o2.TaxKind.ToString(), o2.TaxPercent);

        return new InvoiceSource(
            InvoiceSourceTypes.Obligation,
            o2.Id,
            o2.Reference,
            o2.BranchId,
            o2.IssueDate,
            PaymentObligation.Currency,
            status,
            buyer,
            [line],
            payments.Select(p => new InvoiceSourcePayment(p.Method.ToString(), p.Amount, p.PaidAt, p.Reference)).ToList());
    }
}
