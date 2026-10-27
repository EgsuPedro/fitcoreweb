using FitCore.Application.Core;
using FitCore.Commercial.Application.Settings;
using FitCore.Commercial.Domain;
using FitCore.Commercial.Persistence;
using FitCore.Platform.Application;
using FitCore.Platform.Security;
using FitCore.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace FitCore.Commercial.Application.Billing;

public sealed record AgingBucketDto(string Label, int Count, decimal Amount);

public sealed record ReceivablesBranchDto(Guid BranchId, string BranchCode, string BranchName, decimal Outstanding, decimal Overdue, int OpenCount, int OverdueCount);

public sealed record DebtorDto(
    Guid MemberId,
    string MemberCode,
    string MemberName,
    string? Phone,
    decimal Outstanding,
    decimal Overdue,
    DateOnly OldestDueDate,
    int OpenObligations);

/// <summary>Cartera: lo que los alumnos deben hoy. No incluye obligaciones anuladas ni saldadas.</summary>
public sealed record ReceivablesSummaryDto(
    string Currency,
    DateOnly AsOf,
    decimal Outstanding,
    decimal Overdue,
    int OpenCount,
    int OverdueCount,
    IReadOnlyList<AgingBucketDto> Aging,
    IReadOnlyList<ReceivablesBranchDto> ByBranch,
    IReadOnlyList<DebtorDto> TopDebtors);

public sealed record GetReceivablesSummaryQuery(Guid? BranchId, int Top) : IQuery<ReceivablesSummaryDto>;

internal sealed class GetReceivablesSummaryHandler(CommercialDbContext db, IAccessContext access, IClock clock)
    : IQueryHandler<GetReceivablesSummaryQuery, ReceivablesSummaryDto>
{
    public async Task<Result<ReceivablesSummaryDto>> HandleAsync(GetReceivablesSummaryQuery query, CancellationToken cancellationToken)
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
        var open = db.Obligations.AsNoTracking().VisibleTo(actor.Value)
            .Where(o => o.Status == ObligationStatus.Pending || o.Status == ObligationStatus.PartiallyPaid);
        if (query.BranchId is { } branchId)
        {
            open = open.Where(o => o.BranchId == branchId);
        }

        var obligations = await open.ToListAsync(cancellationToken);
        var balances = await Ledger.BalancesAsync(db, [.. obligations.Select(o => o.Id)], cancellationToken);

        // Cada fila: cuánto falta (exigible − cobrado) y cuántos días lleva vencida (0 si no ha vencido).
        var rows = obligations
            .Select(o => new
            {
                Obligation = o,
                Owed = o.PayableAmount - balances.GetValueOrDefault(o.Id).Paid,
                DaysOverdue = Math.Max(0, today.DayNumber - o.DueDate.DayNumber)
            })
            .Where(r => r.Owed > 0m)
            .ToList();

        AgingBucketDto Bucket(string label, Func<int, bool> match)
        {
            var matching = rows.Where(r => match(r.DaysOverdue)).ToList();
            return new AgingBucketDto(label, matching.Count, matching.Sum(r => r.Owed));
        }

        var branchIds = rows.Select(r => r.Obligation.BranchId).Distinct().ToList();
        var branches = await db.BranchRefs.AsNoTracking().Where(b => branchIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, cancellationToken);
        var memberIds = rows.Select(r => r.Obligation.MemberId).Distinct().ToList();
        var members = await db.Members.AsNoTracking().Where(m => memberIds.Contains(m.Id))
            .Select(m => new { m.Id, m.Code, m.FirstName, m.LastName, m.Phone }).ToDictionaryAsync(m => m.Id, cancellationToken);

        var byBranch = rows.GroupBy(r => r.Obligation.BranchId).Select(g =>
        {
            var branch = branches[g.Key];
            return new ReceivablesBranchDto(
                g.Key,
                branch.Code,
                branch.Name,
                g.Sum(r => r.Owed),
                g.Where(r => r.DaysOverdue > 0).Sum(r => r.Owed),
                g.Count(),
                g.Count(r => r.DaysOverdue > 0));
        }).OrderByDescending(b => b.Outstanding).ToList();

        var debtors = rows.GroupBy(r => r.Obligation.MemberId).Select(g =>
        {
            var member = members[g.Key];
            return new DebtorDto(
                g.Key,
                member.Code,
                $"{member.FirstName} {member.LastName}",
                member.Phone,
                g.Sum(r => r.Owed),
                g.Where(r => r.DaysOverdue > 0).Sum(r => r.Owed),
                g.Min(r => r.Obligation.DueDate),
                g.Count());
        }).OrderByDescending(d => d.Overdue).ThenByDescending(d => d.Outstanding).Take(Math.Clamp(query.Top, 1, 100)).ToList();

        return new ReceivablesSummaryDto(
            PaymentObligation.Currency,
            today,
            rows.Sum(r => r.Owed),
            rows.Where(r => r.DaysOverdue > 0).Sum(r => r.Owed),
            rows.Count,
            rows.Count(r => r.DaysOverdue > 0),
            [
                Bucket("Al día", d => d == 0),
                Bucket("1-30 días", d => d is >= 1 and <= 30),
                Bucket("31-60 días", d => d is >= 31 and <= 60),
                Bucket("61-90 días", d => d is >= 61 and <= 90),
                Bucket("Más de 90 días", d => d > 90)
            ],
            byBranch,
            debtors);
    }
}
