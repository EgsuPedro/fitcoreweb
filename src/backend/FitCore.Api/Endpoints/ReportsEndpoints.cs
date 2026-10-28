using FitCore.Analytics.Application.Reports;
using FitCore.Analytics.Application.Reports.Financial;
using FitCore.Analytics.Domain;
using FitCore.Api.Infrastructure;
using FitCore.Application.Core;
using FitCore.Platform.Domain;

namespace FitCore.Api.Endpoints;

/// <summary>Reportes de la fase de analítica. Todos validan el permiso de su área en el servidor y se acotan a las sucursales del usuario.</summary>
internal static class ReportsEndpoints
{
    public static IEndpointRouteBuilder MapReportsEndpoints(this IEndpointRouteBuilder routes)
    {
        MapFinancial(routes.MapGroup("/reports/financial").WithTags("Reports: financial"));
        return routes;
    }

    private static void MapFinancial(RouteGroupBuilder group)
    {
        group.MapGet("/collections", async (IDispatcher dispatcher, CancellationToken ct, PeriodPreset preset = PeriodPreset.Last30Days, DateOnly? from = null, DateOnly? to = null,
                    Guid[]? branchId = null, PaymentStatusFilter status = PaymentStatusFilter.Confirmed, string? method = null, Guid? planId = null, int page = 1, int pageSize = 25) =>
                (await dispatcher.QueryAsync(new CollectionsQuery(new ReportFilter(preset, from, to, branchId ?? []), status, method, planId, new PageRequest(page, pageSize)), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.ReportsFinancialRead)
            .Produces<CollectionsReportDto>()
            .WithName("GetCollectionsReport");

        group.MapGet("/payment-methods", async (IDispatcher dispatcher, CancellationToken ct, PeriodPreset preset = PeriodPreset.Last30Days, DateOnly? from = null, DateOnly? to = null, Guid[]? branchId = null) =>
                (await dispatcher.QueryAsync(new PaymentMethodsQuery(new ReportFilter(preset, from, to, branchId ?? [])), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.ReportsFinancialRead)
            .Produces<PaymentMethodsReportDto>()
            .WithName("GetPaymentMethodsReport");

        group.MapGet("/outstanding-balances", async (IDispatcher dispatcher, CancellationToken ct, Guid[]? branchId = null, BalanceView view = BalanceView.Members, bool overdueOnly = false, Guid? planId = null, int page = 1, int pageSize = 25) =>
                (await dispatcher.QueryAsync(new OutstandingQuery(new ReportFilter(PeriodPreset.Today, null, null, branchId ?? []), view, overdueOnly, planId, new PageRequest(page, pageSize)), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.ReportsFinancialRead)
            .Produces<OutstandingReportDto>()
            .WithName("GetOutstandingBalancesReport");

        group.MapGet("/cash-registers", async (IDispatcher dispatcher, CancellationToken ct, PeriodPreset preset = PeriodPreset.Last30Days, DateOnly? from = null, DateOnly? to = null,
                    Guid[]? branchId = null, bool onlyWithDifference = false, int page = 1, int pageSize = 25) =>
                (await dispatcher.QueryAsync(new CashReportQuery(new ReportFilter(preset, from, to, branchId ?? []), onlyWithDifference, new PageRequest(page, pageSize)), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.ReportsFinancialRead)
            .Produces<CashReportDto>()
            .WithName("GetCashRegistersReport");

        group.MapGet("/reversals", async (IDispatcher dispatcher, CancellationToken ct, PeriodPreset preset = PeriodPreset.Last30Days, DateOnly? from = null, DateOnly? to = null,
                    Guid[]? branchId = null, int page = 1, int pageSize = 25) =>
                (await dispatcher.QueryAsync(new ReversalsQuery(new ReportFilter(preset, from, to, branchId ?? []), new PageRequest(page, pageSize)), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.ReportsFinancialRead)
            .Produces<ReversalsReportDto>()
            .WithName("GetReversalsReport");
    }
}
