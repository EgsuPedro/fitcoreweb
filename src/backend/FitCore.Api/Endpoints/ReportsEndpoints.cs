using FitCore.Analytics.Application.Reports;
using FitCore.Analytics.Application.Reports.Branches;
using FitCore.Analytics.Application.Reports.Financial;
using FitCore.Analytics.Application.Reports.Members;
using FitCore.Analytics.Application.Reports.Operations;
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
        MapMembers(routes.MapGroup("/reports/members").WithTags("Reports: members"));
        MapOperations(routes.MapGroup("/reports").WithTags("Reports: operations"));
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

    private static void MapMembers(RouteGroupBuilder group)
    {
        group.MapGet("/summary", async (IDispatcher dispatcher, CancellationToken ct, PeriodPreset preset = PeriodPreset.Last30Days, DateOnly? from = null, DateOnly? to = null, Guid[]? branchId = null, Guid? planId = null) =>
                (await dispatcher.QueryAsync(new MembersSummaryQuery(new ReportFilter(preset, from, to, branchId ?? []), planId), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.ReportsMembersRead)
            .Produces<MembersSummaryDto>()
            .WithName("GetMembersSummaryReport");

        group.MapGet("/acquisitions", async (IDispatcher dispatcher, CancellationToken ct, PeriodPreset preset = PeriodPreset.Last30Days, DateOnly? from = null, DateOnly? to = null,
                    Guid[]? branchId = null, Guid? planId = null, MembershipKind kind = MembershipKind.Any, int page = 1, int pageSize = 25) =>
                (await dispatcher.QueryAsync(new AcquisitionsQuery(new ReportFilter(preset, from, to, branchId ?? []), planId, kind, new PageRequest(page, pageSize)), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.ReportsMembersRead)
            .Produces<AcquisitionsReportDto>()
            .WithName("GetMemberAcquisitionsReport");

        group.MapGet("/retention", async (IDispatcher dispatcher, CancellationToken ct, PeriodPreset preset = PeriodPreset.Last30Days, DateOnly? from = null, DateOnly? to = null,
                    Guid[]? branchId = null, Guid? planId = null, int page = 1, int pageSize = 25) =>
                (await dispatcher.QueryAsync(new RetentionQuery(new ReportFilter(preset, from, to, branchId ?? []), planId, new PageRequest(page, pageSize)), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.ReportsMembersRead)
            .Produces<RetentionReportDto>()
            .WithName("GetMemberRetentionReport");

        group.MapGet("/renewals", async (IDispatcher dispatcher, CancellationToken ct, PeriodPreset preset = PeriodPreset.Last30Days, DateOnly? from = null, DateOnly? to = null,
                    Guid[]? branchId = null, Guid? planId = null, int page = 1, int pageSize = 25) =>
                (await dispatcher.QueryAsync(new RenewalsQuery(new ReportFilter(preset, from, to, branchId ?? []), planId, new PageRequest(page, pageSize)), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.ReportsMembersRead)
            .Produces<RenewalsReportDto>()
            .WithName("GetMemberRenewalsReport");

        group.MapGet("/expirations", async (IDispatcher dispatcher, CancellationToken ct, Guid[]? branchId = null, int days = 30, Guid? planId = null, bool hideRenewed = false, int page = 1, int pageSize = 25) =>
                (await dispatcher.QueryAsync(new ExpirationsQuery(new ReportFilter(PeriodPreset.Today, null, null, branchId ?? []), days, planId, hideRenewed, new PageRequest(page, pageSize)), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.ReportsMembersRead)
            .Produces<ExpirationsReportDto>()
            .WithName("GetMemberExpirationsReport");
    }

    private static void MapOperations(RouteGroupBuilder group)
    {
        group.MapGet("/attendance", async (IDispatcher dispatcher, CancellationToken ct, PeriodPreset preset = PeriodPreset.Last30Days, DateOnly? from = null, DateOnly? to = null, Guid[]? branchId = null,
                    AttendanceGrouping grouping = AttendanceGrouping.Auto, AttendanceView view = AttendanceView.Summary, int page = 1, int pageSize = 25) =>
                (await dispatcher.QueryAsync(new AttendanceQuery(new ReportFilter(preset, from, to, branchId ?? []), grouping, view, new PageRequest(page, pageSize)), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.ReportsOperationsRead)
            .Produces<AttendanceReportDto>()
            .WithName("GetGymAttendanceReport");

        // Reportes de clases: el permiso de operación ve todo; el de instructor (entrenador) solo sus sesiones. Lo decide el caso de uso.
        group.MapGet("/classes/occupancy", async (IDispatcher dispatcher, CancellationToken ct, PeriodPreset preset = PeriodPreset.Last30Days, DateOnly? from = null, DateOnly? to = null, Guid[]? branchId = null,
                    string? activity = null, Guid? classId = null, int page = 1, int pageSize = 25) =>
                (await dispatcher.QueryAsync(new ClassOccupancyQuery(new ReportFilter(preset, from, to, branchId ?? []), activity, classId, new PageRequest(page, pageSize)), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.AnalyticsDashboardRead)
            .Produces<ClassOccupancyReportDto>()
            .WithName("GetClassOccupancyReport");

        group.MapGet("/classes/cancellations", async (IDispatcher dispatcher, CancellationToken ct, PeriodPreset preset = PeriodPreset.Last30Days, DateOnly? from = null, DateOnly? to = null, Guid[]? branchId = null,
                    int page = 1, int pageSize = 25) =>
                (await dispatcher.QueryAsync(new ClassCancellationsQuery(new ReportFilter(preset, from, to, branchId ?? []), new PageRequest(page, pageSize)), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.AnalyticsDashboardRead)
            .Produces<ClassCancellationsReportDto>()
            .WithName("GetClassCancellationsReport");

        // Comparación de sucursales: exige el permiso de comparar; cada bloque de indicadores solo viaja si el usuario tiene el permiso de su área.
        group.MapGet("/branches/comparison", async (IDispatcher dispatcher, CancellationToken ct, PeriodPreset preset = PeriodPreset.Last30Days, DateOnly? from = null, DateOnly? to = null, Guid[]? branchId = null) =>
                (await dispatcher.QueryAsync(new BranchComparisonQuery(new ReportFilter(preset, from, to, branchId ?? [])), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.ReportsCompare)
            .Produces<BranchComparisonReportDto>()
            .WithName("GetBranchComparisonReport");
    }
}
