using FitCore.Analytics.Persistence;
using FitCore.Application.Core;
using FitCore.Platform.Domain;
using FitCore.Platform.Security;
using FitCore.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace FitCore.Analytics.Application.Reports.Financial;

internal sealed class GetOutstandingHandler(AnalyticsDbContext db, IAccessContext access, IClock clock)
    : IQueryHandler<OutstandingQuery, OutstandingReportDto>
{
    public async Task<Result<OutstandingReportDto>> HandleAsync(OutstandingQuery query, CancellationToken cancellationToken)
    {
        var begin = await ReportStart.BeginAsync(db, access, Permissions.ReportsFinancialRead, query.Filter, clock, cancellationToken);
        if (begin.IsFailure)
        {
            return begin.Error;
        }

        var (_, scope, periods) = begin.Value;
        var today = scope.Today;
        var open = Metrics.OpenBalances(db, scope.BranchIds, query.PlanId);
        if (query.OverdueOnly)
        {
            open = open.Where(b => b.DueDate < today);
        }

        // Totales y antigüedad en una sola pasada en PostgreSQL. Al día = aún no vence; luego tramos de días vencidos.
        var d30 = today.AddDays(-30);
        var d60 = today.AddDays(-60);
        var d90 = today.AddDays(-90);
        var totals = await open.GroupBy(_ => 1).Select(g => new
        {
            Total = g.Sum(b => b.Remaining),
            Count = g.Count(),
            Overdue = g.Sum(b => b.DueDate < today ? b.Remaining : 0m),
            OverdueCount = g.Count(b => b.DueDate < today),
            CurrentAmount = g.Sum(b => b.DueDate >= today ? b.Remaining : 0m),
            CurrentCount = g.Count(b => b.DueDate >= today),
            A1Amount = g.Sum(b => b.DueDate < today && b.DueDate >= d30 ? b.Remaining : 0m),
            A1Count = g.Count(b => b.DueDate < today && b.DueDate >= d30),
            A2Amount = g.Sum(b => b.DueDate < d30 && b.DueDate >= d60 ? b.Remaining : 0m),
            A2Count = g.Count(b => b.DueDate < d30 && b.DueDate >= d60),
            A3Amount = g.Sum(b => b.DueDate < d60 && b.DueDate >= d90 ? b.Remaining : 0m),
            A3Count = g.Count(b => b.DueDate < d60 && b.DueDate >= d90),
            A4Amount = g.Sum(b => b.DueDate < d90 ? b.Remaining : 0m),
            A4Count = g.Count(b => b.DueDate < d90)
        }).FirstOrDefaultAsync(cancellationToken);

        var aging = totals is null
            ? new List<AgingRowDto> { new("Al día", 0, 0m), new("1-30 días", 0, 0m), new("31-60 días", 0, 0m), new("61-90 días", 0, 0m), new("Más de 90 días", 0, 0m) }
            : [new("Al día", totals.CurrentCount, totals.CurrentAmount), new("1-30 días", totals.A1Count, totals.A1Amount), new("31-60 días", totals.A2Count, totals.A2Amount), new("61-90 días", totals.A3Count, totals.A3Amount), new("Más de 90 días", totals.A4Count, totals.A4Amount)];

        var branchNames = scope.AuthorizedBranches.ToDictionary(b => b.Id, b => b.Name);
        var byBranchRaw = await open.GroupBy(b => b.BranchId).Select(g => new
        {
            BranchId = g.Key,
            Outstanding = g.Sum(b => b.Remaining),
            Overdue = g.Sum(b => b.DueDate < today ? b.Remaining : 0m),
            Open = g.Count(),
            OverdueCount = g.Count(b => b.DueDate < today)
        }).ToListAsync(cancellationToken);
        var byBranch = byBranchRaw.OrderByDescending(b => b.Outstanding)
            .Select(b => new BalanceByBranchDto(b.BranchId, branchNames.GetValueOrDefault(b.BranchId, string.Empty), b.Outstanding, b.Overdue, b.Open, b.OverdueCount))
            .ToList();

        PagedResult<MemberBalanceDto>? members = null;
        PagedResult<ObligationBalanceDto>? obligations = null;
        if (query.View == BalanceView.Members)
        {
            members = await MemberRowsAsync(open, today, query.Page, cancellationToken);
        }
        else
        {
            obligations = await ObligationRowsAsync(open, branchNames, today, query.Page, cancellationToken);
        }

        return new OutstandingReportDto(
            ReportHeader.Of(scope, periods, clock.UtcNow, "Hoy (foto actual); el vencimiento usa la fecha de vencimiento de cada obligación"),
            totals?.Total ?? 0m,
            totals?.Overdue ?? 0m,
            totals?.Count ?? 0,
            totals?.OverdueCount ?? 0,
            aging,
            byBranch,
            members,
            obligations);
    }

    private async Task<PagedResult<MemberBalanceDto>> MemberRowsAsync(IQueryable<OpenBalance> open, DateOnly today, PageRequest page, CancellationToken ct)
    {
        var grouped = open.GroupBy(b => b.MemberId).Select(g => new
        {
            MemberId = g.Key,
            Outstanding = g.Sum(b => b.Remaining),
            Overdue = g.Sum(b => b.DueDate < today ? b.Remaining : 0m),
            Oldest = g.Min(b => b.DueDate),
            Open = g.Count()
        });
        var total = await grouped.CountAsync(ct);
        var pageRows = await grouped.OrderByDescending(g => g.Overdue).ThenByDescending(g => g.Outstanding).ThenBy(g => g.MemberId)
            .Skip(page.Skip).Take(page.SafePageSize).ToListAsync(ct);
        var ids = pageRows.Select(r => r.MemberId).ToList();
        var names = await db.Members.Where(m => ids.Contains(m.Id)).Select(m => new { m.Id, m.Code, m.FirstName, m.LastName }).ToDictionaryAsync(m => m.Id, ct);
        var items = pageRows.Select(r =>
        {
            var member = names[r.MemberId];
            return new MemberBalanceDto(r.MemberId, member.Code, $"{member.FirstName} {member.LastName}", r.Outstanding, r.Overdue, r.Oldest, r.Open);
        }).ToList();
        return new PagedResult<MemberBalanceDto>(items, page.SafePage, page.SafePageSize, total);
    }

    private async Task<PagedResult<ObligationBalanceDto>> ObligationRowsAsync(IQueryable<OpenBalance> open, Dictionary<Guid, string> branchNames, DateOnly today, PageRequest page, CancellationToken ct)
    {
        var rows = from b in open
                   join o in db.Obligations on b.ObligationId equals o.Id
                   join mem in db.Members on b.MemberId equals mem.Id
                   orderby b.DueDate, o.Reference
                   select new
                   {
                       b.ObligationId,
                       o.Reference,
                       o.Concept,
                       mem.Code,
                       mem.FirstName,
                       mem.LastName,
                       b.BranchId,
                       PlanName = db.Memberships.Where(m => m.Id == b.MembershipId).Select(m => m.PlanName).FirstOrDefault(),
                       o.IssueDate,
                       b.DueDate,
                       o.PayableAmount,
                       b.Remaining
                   };
        var total = await rows.CountAsync(ct);
        var pageRows = await rows.Skip(page.Skip).Take(page.SafePageSize).ToListAsync(ct);
        var items = pageRows.Select(r =>
        {
            var days = Math.Max(0, today.DayNumber - r.DueDate.DayNumber);
            return new ObligationBalanceDto(
                r.ObligationId,
                r.Reference,
                r.Concept,
                r.Code,
                $"{r.FirstName} {r.LastName}",
                r.BranchId,
                branchNames.GetValueOrDefault(r.BranchId, string.Empty),
                r.PlanName,
                r.IssueDate,
                r.DueDate,
                r.PayableAmount,
                r.PayableAmount - r.Remaining,
                r.Remaining,
                days,
                days > 0 ? "Overdue" : r.Remaining < r.PayableAmount ? "PartiallyPaid" : "Pending");
        }).ToList();
        return new PagedResult<ObligationBalanceDto>(items, page.SafePage, page.SafePageSize, total);
    }
}
