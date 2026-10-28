using FitCore.Analytics.Domain;
using FitCore.Analytics.Persistence;
using FitCore.Application.Core;
using FitCore.Platform.Domain;
using FitCore.Platform.Security;
using FitCore.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace FitCore.Analytics.Application.Reports.Financial;

internal static class FinancialErrors
{
    public static readonly Error MethodInvalid = Error.Validation("analytics.method_invalid", "El medio de pago no es válido (Cash, BankTransfer, Card u Other).");
}

internal static class PaymentMethods
{
    public static readonly string[] All = ["Cash", "BankTransfer", "Card", "Other"];
}

/// <summary>Una línea de cobro: la parte de un pago asignada a una obligación (y, si es de una membresía, su plan).</summary>
internal sealed class CollectionLine
{
    public Guid BranchId { get; init; }
    public Guid PaymentId { get; init; }
    public decimal Amount { get; init; }
    public bool IsPartial { get; init; }
    public Guid? PlanId { get; init; }
    public string? PlanName { get; init; }
}

internal sealed class GetCollectionsHandler(AnalyticsDbContext db, IAccessContext access, IClock clock)
    : IQueryHandler<CollectionsQuery, CollectionsReportDto>
{
    public async Task<Result<CollectionsReportDto>> HandleAsync(CollectionsQuery query, CancellationToken cancellationToken)
    {
        if (query.Method is not null && !PaymentMethods.All.Contains(query.Method))
        {
            return FinancialErrors.MethodInvalid;
        }

        var begin = await ReportStart.BeginAsync(db, access, Permissions.ReportsFinancialRead, query.Filter, clock, cancellationToken);
        if (begin.IsFailure)
        {
            return begin.Error;
        }

        var (_, scope, periods) = begin.Value;
        var branches = scope.BranchIds;

        // Resumen: siempre sobre lo Confirmado (vigente). Se compara con el período anterior con los mismos filtros.
        var now = await TotalsAsync(Lines(branches, periods.Current, "Confirmed", query.Method, query.PlanId), cancellationToken);
        var before = await TotalsAsync(Lines(branches, periods.Previous, "Confirmed", query.Method, query.PlanId), cancellationToken);
        var pendingNow = await TotalsAsync(Lines(branches, periods.Current, "PendingVerification", query.Method, query.PlanId), cancellationToken);
        var pendingBefore = await TotalsAsync(Lines(branches, periods.Previous, "PendingVerification", query.Method, query.PlanId), cancellationToken);
        var partialNow = await TotalsAsync(Lines(branches, periods.Current, "Confirmed", query.Method, query.PlanId).Where(l => l.IsPartial), cancellationToken);
        var partialBefore = await TotalsAsync(Lines(branches, periods.Previous, "Confirmed", query.Method, query.PlanId).Where(l => l.IsPartial), cancellationToken);

        static decimal Average((decimal Amount, int Count) t) => t.Count == 0 ? 0m : Math.Round(t.Amount / t.Count, 2, MidpointRounding.AwayFromZero);

        var summary = new List<KpiDto>
        {
            Kpi.Money("collected", "Total cobrado", "Pagos confirmados en el período (parte asignada si se filtra por plan). Sin utilidad: no descuenta costos ni gastos.", "Fecha de pago", now.Amount, before.Amount),
            Kpi.Count("payments_count", "Pagos recibidos", "Cantidad de pagos confirmados en el período.", "Fecha de pago", now.Count, before.Count),
            Kpi.Money("average_payment", "Pago promedio", "Total cobrado ÷ pagos recibidos.", "Fecha de pago", Average(now), Average(before)),
            Kpi.Money("partial_amount", "Pagos parciales (abonos)", "Parte cobrada en asignaciones que cubren solo una parte del exigible de la obligación.", "Fecha de pago", partialNow.Amount, partialBefore.Amount),
            Kpi.Count("partial_count", "Pagos que abonan", "Pagos con al menos una asignación que no salda el exigible de su obligación.", "Fecha de pago", partialNow.Count, partialBefore.Count),
            Kpi.Money("pending_verification", "Por verificar", "Pagos registrados sin confirmar (transferencias). No cuentan como cobrado.", "Fecha de pago", pendingNow.Amount, pendingBefore.Amount)
        };

        var lines = Lines(branches, periods.Current, "Confirmed", query.Method, query.PlanId);
        var branchNames = scope.AuthorizedBranches.ToDictionary(b => b.Id, b => b.Name);
        var byBranchRaw = await lines.GroupBy(l => l.BranchId)
            .Select(g => new { BranchId = g.Key, Amount = g.Sum(l => l.Amount), Count = g.Select(l => l.PaymentId).Distinct().Count() })
            .ToListAsync(cancellationToken);
        var byBranch = byBranchRaw.OrderByDescending(b => b.Amount)
            .Select(b => new CollectionsByBranchDto(b.BranchId, branchNames.GetValueOrDefault(b.BranchId, string.Empty), b.Amount, b.Count, Share(b.Amount, now.Amount)))
            .ToList();

        var byPlanRaw = await lines.GroupBy(l => new { l.PlanId, l.PlanName })
            .Select(g => new { g.Key.PlanId, g.Key.PlanName, Amount = g.Sum(l => l.Amount) })
            .ToListAsync(cancellationToken);
        var byPlan = byPlanRaw.OrderByDescending(p => p.Amount)
            .Select(p => new CollectionsByPlanDto(p.PlanId, p.PlanName ?? "Cobros sin plan", p.Amount, Share(p.Amount, now.Amount)))
            .ToList();

        var payments = await PaymentRowsAsync(query, scope, periods.Current, cancellationToken);
        return new CollectionsReportDto(ReportHeader.Of(scope, periods, clock.UtcNow, "Fecha de pago"), summary, byBranch, byPlan, payments);
    }

    private static decimal? Share(decimal part, decimal total) => total > 0m ? Math.Round(part * 100m / total, 1, MidpointRounding.AwayFromZero) : null;

    private static async Task<(decimal Amount, int Count)> TotalsAsync(IQueryable<CollectionLine> lines, CancellationToken ct)
    {
        var row = await lines.GroupBy(_ => 1)
            .Select(g => new { Amount = g.Sum(l => l.Amount), Count = g.Select(l => l.PaymentId).Distinct().Count() })
            .FirstOrDefaultAsync(ct);
        return row is null ? (0m, 0) : (row.Amount, row.Count);
    }

    /// <summary>
    /// Las líneas de cobro del período (parte de cada pago asignada a cada obligación). Como el importe de un pago es la suma de sus
    /// asignaciones, sumar líneas da lo mismo que sumar pagos, y además permite repartir por plan sin contar nada dos veces.
    /// </summary>
    private IQueryable<CollectionLine> Lines(IReadOnlyList<Guid> branches, ReportPeriod period, string status, string? method, Guid? planId)
    {
        var start = period.StartUtc;
        var end = period.EndUtcExclusive;
        var lines = from p in db.Payments
                    where branches.Contains(p.BranchId) && p.Status == status && p.PaidAt >= start && p.PaidAt < end && (method == null || p.Method == method)
                    join a in db.Allocations on p.Id equals a.PaymentId
                    join o in db.Obligations on a.ObligationId equals o.Id
                    join m in db.Memberships on o.MembershipId equals m.Id into memberships
                    from m in memberships.DefaultIfEmpty()
                    select new CollectionLine
                    {
                        BranchId = p.BranchId,
                        PaymentId = p.Id,
                        Amount = a.Amount,
                        IsPartial = a.Amount < o.PayableAmount,
                        PlanId = m == null ? null : m.PlanId,
                        PlanName = m == null ? null : m.PlanName
                    };
        return planId is { } plan ? lines.Where(l => l.PlanId == plan) : lines;
    }

    private async Task<PagedResult<CollectionRowDto>> PaymentRowsAsync(CollectionsQuery query, AnalyticsScope scope, ReportPeriod period, CancellationToken ct)
    {
        var start = period.StartUtc;
        var end = period.EndUtcExclusive;
        var branches = scope.BranchIds;
        var planId = query.PlanId;
        var method = query.Method;
        var status = query.Status switch
        {
            PaymentStatusFilter.Confirmed => "Confirmed",
            PaymentStatusFilter.PendingVerification => "PendingVerification",
            PaymentStatusFilter.Reversed => "Reversed",
            _ => null
        };

        var rows = from p in db.Payments
                   where branches.Contains(p.BranchId) && p.PaidAt >= start && p.PaidAt < end
                         && (status == null || p.Status == status) && (method == null || p.Method == method)
                         && (planId == null || db.Allocations.Any(a => a.PaymentId == p.Id
                             && db.Obligations.Any(o => o.Id == a.ObligationId
                                 && db.Memberships.Any(m => m.Id == o.MembershipId && m.PlanId == planId))))
                   join mem in db.Members on p.MemberId equals mem.Id
                   join br in db.BranchRefs on p.BranchId equals br.Id
                   orderby p.PaidAt descending, p.ReceiptNumber descending
                   select new CollectionRowDto(
                       p.Id,
                       p.ReceiptNumber,
                       p.PaidAt,
                       p.BranchId,
                       br.Name,
                       mem.Code,
                       mem.FirstName + " " + mem.LastName,
                       p.Method,
                       p.MethodDetail,
                       p.Amount,
                       p.Status,
                       db.Allocations.Count(a => a.PaymentId == p.Id),
                       db.Allocations.Any(a => a.PaymentId == p.Id && db.Obligations.Any(o => o.Id == a.ObligationId && a.Amount < o.PayableAmount)));
        return await rows.ToPagedAsync(query.Page, ct);
    }
}

internal sealed class GetPaymentMethodsHandler(AnalyticsDbContext db, IAccessContext access, IClock clock)
    : IQueryHandler<PaymentMethodsQuery, PaymentMethodsReportDto>
{
    public async Task<Result<PaymentMethodsReportDto>> HandleAsync(PaymentMethodsQuery query, CancellationToken cancellationToken)
    {
        var begin = await ReportStart.BeginAsync(db, access, Permissions.ReportsFinancialRead, query.Filter, clock, cancellationToken);
        if (begin.IsFailure)
        {
            return begin.Error;
        }

        var (_, scope, periods) = begin.Value;
        var now = await Metrics.PaymentMethodsAsync(db, scope.BranchIds, periods.Current, cancellationToken);
        var before = await Metrics.PaymentMethodsAsync(db, scope.BranchIds, periods.Previous, cancellationToken);
        var total = now.Sum(m => m.Amount);
        var previousTotal = before.Sum(m => m.Amount);

        // Siempre los cuatro medios, también los que no tuvieron cobros: la ausencia se ve como 0, no se oculta.
        var rows = PaymentMethods.All.Select(method =>
        {
            var current = now.FirstOrDefault(m => m.Method == method);
            var previous = before.FirstOrDefault(m => m.Method == method)?.Amount ?? 0m;
            var amount = current?.Amount ?? 0m;
            return new MethodRowDto(method, amount, current?.Count ?? 0, total > 0m ? Math.Round(amount * 100m / total, 1, MidpointRounding.AwayFromZero) : null, previous, Variation.Of(amount, previous));
        }).ToList();

        var start = periods.Current.StartUtc;
        var end = periods.Current.EndUtcExclusive;
        var branches = scope.BranchIds;
        var details = await db.Payments
            .Where(p => branches.Contains(p.BranchId) && p.Status == "Confirmed" && p.Method == "Other" && p.PaidAt >= start && p.PaidAt < end && p.MethodDetail != null)
            .Select(p => p.MethodDetail!)
            .Distinct()
            .OrderBy(d => d)
            .Take(20)
            .ToListAsync(cancellationToken);

        return new PaymentMethodsReportDto(ReportHeader.Of(scope, periods, clock.UtcNow, "Fecha de pago"), total, previousTotal, rows, details);
    }
}
