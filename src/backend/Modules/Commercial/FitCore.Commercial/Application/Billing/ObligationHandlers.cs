using FitCore.Application.Core;
using FitCore.Commercial.Application.Settings;
using FitCore.Commercial.Domain;
using FitCore.Commercial.Persistence;
using FitCore.Platform.Application;
using FitCore.Platform.Security;
using FitCore.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace FitCore.Commercial.Application.Billing;

public sealed record ObligationDto(
    Guid Id,
    string Reference,
    Guid BranchId,
    Guid MemberId,
    string MemberCode,
    string MemberName,
    Guid? MembershipId,
    string Concept,
    DateOnly IssueDate,
    DateOnly DueDate,
    decimal OriginalAmount,
    decimal DiscountAmount,
    decimal PayableAmount,
    decimal Subtotal,
    decimal TaxAmount,
    string TaxKind,
    decimal TaxPercent,
    string Currency,
    decimal PaidAmount,
    decimal ReservedAmount,
    decimal Balance,
    string Status,
    string? VoidReason,
    DateTimeOffset? VoidedAt,
    DateTimeOffset CreatedAt,
    Guid? CreatedBy)
{
    /// <summary>El saldo es lo exigible menos lo cobrado (pagos confirmados); lo reservado son transferencias aún sin verificar.</summary>
    public static ObligationDto From(PaymentObligation o, string memberCode, string memberName, DateOnly today, decimal paid = 0m, decimal reserved = 0m) =>
        new(
            o.Id,
            o.Reference,
            o.BranchId,
            o.MemberId,
            memberCode,
            memberName,
            o.MembershipId,
            o.Concept,
            o.IssueDate,
            o.DueDate,
            o.OriginalAmount,
            o.DiscountAmount,
            o.PayableAmount,
            o.Subtotal,
            o.TaxAmount,
            o.TaxKind.ToString(),
            o.TaxPercent,
            PaymentObligation.Currency,
            paid,
            reserved,
            o.Status == ObligationStatus.Voided ? 0m : o.PayableAmount - paid,
            o.EffectiveStatus(today).ToString(),
            o.VoidReason,
            o.VoidedAt,
            o.CreatedAt,
            o.CreatedBy);
}

public sealed record ListObligationsQuery(PageRequest Page, Guid? MemberId, Guid? MembershipId, Guid? BranchId, ObligationStatus? Status, string? Search)
    : IQuery<PagedResult<ObligationDto>>;

public sealed record GetObligationQuery(Guid Id) : IQuery<ObligationDto>;

internal static class ObligationErrors
{
    public static readonly Error NotFound = Error.NotFound("obligations.not_found", "Obligación no encontrada.");
}

internal static class ObligationQueries
{
    public static IQueryable<PaymentObligation> VisibleTo(this IQueryable<PaymentObligation> obligations, UserAccess actor) =>
        obligations.InScope(actor, o => o.BranchId);

    /// <summary>Filtro por estado efectivo: "vencida" es pendiente o parcial con la fecha de vencimiento superada.</summary>
    public static IQueryable<PaymentObligation> WithEffectiveStatus(this IQueryable<PaymentObligation> obligations, ObligationStatus status, DateOnly today) =>
        status switch
        {
            ObligationStatus.Overdue => obligations.Where(o => (o.Status == ObligationStatus.Pending || o.Status == ObligationStatus.PartiallyPaid) && o.DueDate < today),
            ObligationStatus.Pending or ObligationStatus.PartiallyPaid => obligations.Where(o => o.Status == status && o.DueDate >= today),
            _ => obligations.Where(o => o.Status == status)
        };
}

internal sealed class ListObligationsHandler(CommercialDbContext db, IAccessContext access, IClock clock)
    : IQueryHandler<ListObligationsQuery, PagedResult<ObligationDto>>
{
    public async Task<Result<PagedResult<ObligationDto>>> HandleAsync(ListObligationsQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        if (query.BranchId is { } requested && !actor.Value.CanAccessBranch(requested))
        {
            return PlatformErrors.BranchNotAllowed;
        }

        var today = LocalDates.Today(clock);
        var obligations = db.Obligations.AsNoTracking().VisibleTo(actor.Value);
        if (query.MemberId is { } memberId)
        {
            obligations = obligations.Where(o => o.MemberId == memberId);
        }

        if (query.MembershipId is { } membershipId)
        {
            obligations = obligations.Where(o => o.MembershipId == membershipId);
        }

        if (query.BranchId is { } branchId)
        {
            obligations = obligations.Where(o => o.BranchId == branchId);
        }

        if (query.Status is { } status)
        {
            obligations = obligations.WithEffectiveStatus(status, today);
        }

        var rows = from o in obligations
                   join m in db.Members on o.MemberId equals m.Id
                   select new { Obligation = o, m.Code, m.FirstName, m.LastName };

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var pattern = LikePattern.Contains(query.Search);
            rows = rows.Where(r =>
                EF.Functions.ILike(r.Obligation.Reference, pattern)
                || EF.Functions.ILike(r.Code, pattern)
                || EF.Functions.ILike(r.FirstName + " " + r.LastName, pattern)
                || EF.Functions.ILike(r.LastName, pattern));
        }

        var paged = await rows.OrderByDescending(r => r.Obligation.IssueDate).ThenByDescending(r => r.Obligation.Reference).ToPagedAsync(query.Page, cancellationToken);
        var balances = await Ledger.BalancesAsync(db, [.. paged.Items.Select(r => r.Obligation.Id)], cancellationToken);
        return new PagedResult<ObligationDto>(
            [.. paged.Items.Select(r => ObligationDto.From(r.Obligation, r.Code, $"{r.FirstName} {r.LastName}", today, balances.GetValueOrDefault(r.Obligation.Id).Paid, balances.GetValueOrDefault(r.Obligation.Id).Reserved))],
            paged.Page,
            paged.PageSize,
            paged.TotalCount);
    }
}

internal sealed class GetObligationHandler(CommercialDbContext db, IAccessContext access, IClock clock) : IQueryHandler<GetObligationQuery, ObligationDto>
{
    public async Task<Result<ObligationDto>> HandleAsync(GetObligationQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var row = await (from o in db.Obligations.AsNoTracking().VisibleTo(actor.Value)
                         join m in db.Members on o.MemberId equals m.Id
                         where o.Id == query.Id
                         select new { Obligation = o, m.Code, m.FirstName, m.LastName }).FirstOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return ObligationErrors.NotFound;
        }

        var balance = (await Ledger.BalancesAsync(db, [row.Obligation.Id], cancellationToken)).GetValueOrDefault(row.Obligation.Id);
        return ObligationDto.From(row.Obligation, row.Code, $"{row.FirstName} {row.LastName}", LocalDates.Today(clock), balance.Paid, balance.Reserved);
    }
}
