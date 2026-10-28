using FitCore.Analytics.Persistence;
using FitCore.Application.Core;
using FitCore.Platform.Domain;
using FitCore.Platform.Security;
using FitCore.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace FitCore.Analytics.Application.Reports.Financial;

internal sealed class GetCashReportHandler(AnalyticsDbContext db, IAccessContext access, IClock clock)
    : IQueryHandler<CashReportQuery, CashReportDto>
{
    public async Task<Result<CashReportDto>> HandleAsync(CashReportQuery query, CancellationToken cancellationToken)
    {
        var begin = await ReportStart.BeginAsync(db, access, Permissions.ReportsFinancialRead, query.Filter, clock, cancellationToken);
        if (begin.IsFailure)
        {
            return begin.Error;
        }

        var (_, scope, periods) = begin.Value;
        var branches = scope.BranchIds;
        var start = periods.Current.StartUtc;
        var end = periods.Current.EndUtcExclusive;

        var sessions = db.CashSessions.Where(s => branches.Contains(s.BranchId) && s.OpenedAt >= start && s.OpenedAt < end);
        var totals = await sessions.GroupBy(_ => 1).Select(g => new
        {
            Opened = g.Count(),
            StillOpen = g.Count(s => s.Status == "Open"),
            Closed = g.Count(s => s.Status == "Closed"),
            Shortage = g.Sum(s => s.Difference < 0m ? -s.Difference!.Value : 0m),
            Surplus = g.Sum(s => s.Difference > 0m ? s.Difference!.Value : 0m),
            WithDifference = g.Count(s => s.Difference != null && s.Difference != 0m)
        }).FirstOrDefaultAsync(cancellationToken);

        var movements = await db.CashMovements
            .Where(m => branches.Contains(m.BranchId) && m.CreatedAt >= start && m.CreatedAt < end)
            .GroupBy(m => new { m.Type, m.Direction })
            .Select(g => new CashMovementTotalDto(g.Key.Type, g.Key.Direction, g.Sum(m => m.Amount), g.Count()))
            .ToListAsync(cancellationToken);

        var listed = query.OnlyWithDifference ? sessions.Where(s => s.Difference != null && s.Difference != 0m) : sessions;
        var rows = from s in listed
                   join r in db.CashRegisters on s.RegisterId equals r.Id
                   join br in db.BranchRefs on s.BranchId equals br.Id
                   join opener in db.Users on s.OpenedBy equals opener.Id
                   join closer in db.Users on s.ClosedBy equals closer.Id into closers
                   from closer in closers.DefaultIfEmpty()
                   orderby s.OpenedAt descending, s.Id
                   select new CashSessionRowDto(
                       s.Id, s.BranchId, br.Name, r.Name, opener.FullName, s.OpenedAt, closer == null ? null : closer.FullName, s.ClosedAt, s.Status,
                       s.OpeningAmount, s.ExpectedAtClose, s.CountedAmount, s.Difference);
        var paged = await rows.ToPagedAsync(query.Page, cancellationToken);

        return new CashReportDto(
            ReportHeader.Of(scope, periods, clock.UtcNow, "Apertura del turno (aperturas y cierres) y fecha del movimiento (movimientos)"),
            totals?.Opened ?? 0,
            totals?.StillOpen ?? 0,
            totals?.Closed ?? 0,
            totals?.Shortage ?? 0m,
            totals?.Surplus ?? 0m,
            totals?.WithDifference ?? 0,
            movements.OrderBy(m => m.Type).ToList(),
            paged);
    }
}

internal sealed class GetReversalsHandler(AnalyticsDbContext db, IAccessContext access, IClock clock)
    : IQueryHandler<ReversalsQuery, ReversalsReportDto>
{
    public async Task<Result<ReversalsReportDto>> HandleAsync(ReversalsQuery query, CancellationToken cancellationToken)
    {
        var begin = await ReportStart.BeginAsync(db, access, Permissions.ReportsFinancialRead, query.Filter, clock, cancellationToken);
        if (begin.IsFailure)
        {
            return begin.Error;
        }

        var (_, scope, periods) = begin.Value;
        var branches = scope.BranchIds;
        var start = periods.Current.StartUtc;
        var end = periods.Current.EndUtcExclusive;

        var reversed = db.Payments.Where(p => branches.Contains(p.BranchId) && p.Status == "Reversed" && p.ReversedAt >= start && p.ReversedAt < end);
        var reversedTotals = await reversed.GroupBy(_ => 1).Select(g => new { Amount = g.Sum(p => p.Amount), Count = g.Count() }).FirstOrDefaultAsync(cancellationToken);
        var voided = db.Obligations.Where(o => branches.Contains(o.BranchId) && o.Status == "Voided" && o.VoidedAt >= start && o.VoidedAt < end);
        var voidedTotals = await voided.GroupBy(_ => 1).Select(g => new { Amount = g.Sum(o => o.PayableAmount), Count = g.Count() }).FirstOrDefaultAsync(cancellationToken);

        var reversalRows = from p in reversed
                           join mem in db.Members on p.MemberId equals mem.Id
                           join br in db.BranchRefs on p.BranchId equals br.Id
                           join actor in db.Users on p.ReversedBy equals actor.Id into users
                           from actor in users.DefaultIfEmpty()
                           orderby p.ReversedAt descending, p.ReceiptNumber descending
                           select new ReversalRowDto(
                               p.Id, p.ReceiptNumber, p.PaidAt, p.ReversedAt!.Value, br.Name, mem.Code, mem.FirstName + " " + mem.LastName, p.Method, p.Amount,
                               p.ReversalReason, actor == null ? null : actor.FullName);

        var voidedRows = from o in voided
                         join mem in db.Members on o.MemberId equals mem.Id
                         join br in db.BranchRefs on o.BranchId equals br.Id
                         join actor in db.Users on o.VoidedBy equals actor.Id into users
                         from actor in users.DefaultIfEmpty()
                         orderby o.VoidedAt descending, o.Reference descending
                         select new VoidedObligationRowDto(
                             o.Id, o.Reference, o.Concept, o.VoidedAt!.Value, br.Name, mem.Code, mem.FirstName + " " + mem.LastName, o.PayableAmount,
                             o.VoidReason, actor == null ? null : actor.FullName);

        return new ReversalsReportDto(
            ReportHeader.Of(scope, periods, clock.UtcNow, "Fecha de reversión del pago y fecha de anulación de la obligación"),
            reversedTotals?.Amount ?? 0m,
            reversedTotals?.Count ?? 0,
            voidedTotals?.Amount ?? 0m,
            voidedTotals?.Count ?? 0,
            await reversalRows.ToPagedAsync(query.Page, cancellationToken),
            await voidedRows.ToPagedAsync(query.Page, cancellationToken));
    }
}
