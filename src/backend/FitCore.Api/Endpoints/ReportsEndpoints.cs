using FitCore.Analytics.Application.Exports;
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
        MapExports(routes.MapGroup("/reports/exports").WithTags("Reports: exports"));
        return routes;
    }

    /// <summary>Páginas de reportes: hasta 100 filas por solicitud (las exportaciones pagan el costo una sola vez, en el servidor).</summary>
    private static PageRequest Page(int page, int pageSize) => new(page, Math.Clamp(pageSize, 1, PageRequest.MaxPageSize));

    private static void MapFinancial(RouteGroupBuilder group)
    {
        group.MapGet("/collections", async (IDispatcher dispatcher, CancellationToken ct, PeriodPreset preset = PeriodPreset.Last30Days, DateOnly? from = null, DateOnly? to = null,
                    Guid[]? branchId = null, PaymentStatusFilter status = PaymentStatusFilter.Confirmed, string? method = null, Guid? planId = null, int page = 1, int pageSize = 25) =>
                (await dispatcher.QueryAsync(new CollectionsQuery(new ReportFilter(preset, from, to, branchId ?? []), status, method, planId, Page(page, pageSize)), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.ReportsFinancialRead)
            .Produces<CollectionsReportDto>()
            .WithName("GetCollectionsReport");

        group.MapGet("/payment-methods", async (IDispatcher dispatcher, CancellationToken ct, PeriodPreset preset = PeriodPreset.Last30Days, DateOnly? from = null, DateOnly? to = null, Guid[]? branchId = null) =>
                (await dispatcher.QueryAsync(new PaymentMethodsQuery(new ReportFilter(preset, from, to, branchId ?? [])), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.ReportsFinancialRead)
            .Produces<PaymentMethodsReportDto>()
            .WithName("GetPaymentMethodsReport");

        group.MapGet("/outstanding-balances", async (IDispatcher dispatcher, CancellationToken ct, Guid[]? branchId = null, BalanceView view = BalanceView.Members, bool overdueOnly = false, Guid? planId = null, int page = 1, int pageSize = 25) =>
                (await dispatcher.QueryAsync(new OutstandingQuery(new ReportFilter(PeriodPreset.Today, null, null, branchId ?? []), view, overdueOnly, planId, Page(page, pageSize)), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.ReportsFinancialRead)
            .Produces<OutstandingReportDto>()
            .WithName("GetOutstandingBalancesReport");

        group.MapGet("/cash-registers", async (IDispatcher dispatcher, CancellationToken ct, PeriodPreset preset = PeriodPreset.Last30Days, DateOnly? from = null, DateOnly? to = null,
                    Guid[]? branchId = null, bool onlyWithDifference = false, int page = 1, int pageSize = 25) =>
                (await dispatcher.QueryAsync(new CashReportQuery(new ReportFilter(preset, from, to, branchId ?? []), onlyWithDifference, Page(page, pageSize)), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.ReportsFinancialRead)
            .Produces<CashReportDto>()
            .WithName("GetCashRegistersReport");

        group.MapGet("/reversals", async (IDispatcher dispatcher, CancellationToken ct, PeriodPreset preset = PeriodPreset.Last30Days, DateOnly? from = null, DateOnly? to = null,
                    Guid[]? branchId = null, int page = 1, int pageSize = 25) =>
                (await dispatcher.QueryAsync(new ReversalsQuery(new ReportFilter(preset, from, to, branchId ?? []), Page(page, pageSize)), ct)).ToHttp(r => Results.Ok(r)))
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
                (await dispatcher.QueryAsync(new AcquisitionsQuery(new ReportFilter(preset, from, to, branchId ?? []), planId, kind, Page(page, pageSize)), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.ReportsMembersRead)
            .Produces<AcquisitionsReportDto>()
            .WithName("GetMemberAcquisitionsReport");

        group.MapGet("/retention", async (IDispatcher dispatcher, CancellationToken ct, PeriodPreset preset = PeriodPreset.Last30Days, DateOnly? from = null, DateOnly? to = null,
                    Guid[]? branchId = null, Guid? planId = null, int page = 1, int pageSize = 25) =>
                (await dispatcher.QueryAsync(new RetentionQuery(new ReportFilter(preset, from, to, branchId ?? []), planId, Page(page, pageSize)), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.ReportsMembersRead)
            .Produces<RetentionReportDto>()
            .WithName("GetMemberRetentionReport");

        group.MapGet("/renewals", async (IDispatcher dispatcher, CancellationToken ct, PeriodPreset preset = PeriodPreset.Last30Days, DateOnly? from = null, DateOnly? to = null,
                    Guid[]? branchId = null, Guid? planId = null, int page = 1, int pageSize = 25) =>
                (await dispatcher.QueryAsync(new RenewalsQuery(new ReportFilter(preset, from, to, branchId ?? []), planId, Page(page, pageSize)), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.ReportsMembersRead)
            .Produces<RenewalsReportDto>()
            .WithName("GetMemberRenewalsReport");

        group.MapGet("/expirations", async (IDispatcher dispatcher, CancellationToken ct, Guid[]? branchId = null, int days = 30, Guid? planId = null, bool hideRenewed = false, int page = 1, int pageSize = 25) =>
                (await dispatcher.QueryAsync(new ExpirationsQuery(new ReportFilter(PeriodPreset.Today, null, null, branchId ?? []), days, planId, hideRenewed, Page(page, pageSize)), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.ReportsMembersRead)
            .Produces<ExpirationsReportDto>()
            .WithName("GetMemberExpirationsReport");
    }

    private static void MapOperations(RouteGroupBuilder group)
    {
        group.MapGet("/attendance", async (IDispatcher dispatcher, CancellationToken ct, PeriodPreset preset = PeriodPreset.Last30Days, DateOnly? from = null, DateOnly? to = null, Guid[]? branchId = null,
                    AttendanceGrouping grouping = AttendanceGrouping.Auto, AttendanceView view = AttendanceView.Summary, int page = 1, int pageSize = 25) =>
                (await dispatcher.QueryAsync(new AttendanceQuery(new ReportFilter(preset, from, to, branchId ?? []), grouping, view, Page(page, pageSize)), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.ReportsOperationsRead)
            .Produces<AttendanceReportDto>()
            .WithName("GetGymAttendanceReport");

        // Reportes de clases: el permiso de operación ve todo; el de instructor (entrenador) solo sus sesiones. Lo decide el caso de uso.
        group.MapGet("/classes/occupancy", async (IDispatcher dispatcher, CancellationToken ct, PeriodPreset preset = PeriodPreset.Last30Days, DateOnly? from = null, DateOnly? to = null, Guid[]? branchId = null,
                    string? activity = null, Guid? classId = null, int page = 1, int pageSize = 25) =>
                (await dispatcher.QueryAsync(new ClassOccupancyQuery(new ReportFilter(preset, from, to, branchId ?? []), activity, classId, Page(page, pageSize)), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.AnalyticsDashboardRead)
            .Produces<ClassOccupancyReportDto>()
            .WithName("GetClassOccupancyReport");

        group.MapGet("/classes/cancellations", async (IDispatcher dispatcher, CancellationToken ct, PeriodPreset preset = PeriodPreset.Last30Days, DateOnly? from = null, DateOnly? to = null, Guid[]? branchId = null,
                    int page = 1, int pageSize = 25) =>
                (await dispatcher.QueryAsync(new ClassCancellationsQuery(new ReportFilter(preset, from, to, branchId ?? []), Page(page, pageSize)), ct)).ToHttp(r => Results.Ok(r)))
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

    /// <summary>
    /// Exportaciones a CSV: se generan al momento (tope de 20.000 filas), se guardan en el almacén privado y vencen a las 24 horas. Pedir y descargar exigen
    /// sesión, el permiso de exportar y el del reporte; solo quien la pidió puede verla y bajarla. No hay enlaces públicos.
    /// </summary>
    private static void MapExports(RouteGroupBuilder group)
    {
        group.MapPost("/", async (ExportRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new CreateExportCommand(request), ct)).ToHttp(r => Results.Created($"/api/v1/reports/exports/{r.Id}", r)))
            .RequirePermission(Permissions.ReportsExport)
            .Produces<ExportDto>(StatusCodes.Status201Created)
            .WithName("CreateReportExport");

        group.MapGet("/", async (IDispatcher dispatcher, CancellationToken ct, int page = 1, int pageSize = 25) =>
                (await dispatcher.QueryAsync(new ListExportsQuery(Page(page, pageSize)), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.ReportsExport)
            .Produces<PagedResult<ExportDto>>()
            .WithName("ListReportExports");

        group.MapGet("/{id:guid}", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.QueryAsync(new GetExportQuery(id), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.ReportsExport)
            .Produces<ExportDto>()
            .WithName("GetReportExport");

        group.MapGet("/{id:guid}/download", async (Guid id, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                (await dispatcher.QueryAsync(new DownloadExportQuery(id), ct)).ToHttp(file =>
                {
                    http.Response.Headers.CacheControl = "private, no-store";
                    http.Response.Headers.XContentTypeOptions = "nosniff";
                    return Results.File(file.Content, file.ContentType, file.FileName);
                }))
            .RequirePermission(Permissions.ReportsExport)
            .Produces(StatusCodes.Status200OK, contentType: "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")
            .WithName("DownloadReportExport");
    }
}
