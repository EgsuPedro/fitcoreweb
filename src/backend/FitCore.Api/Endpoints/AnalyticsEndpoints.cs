using FitCore.Analytics.Application;
using FitCore.Analytics.Application.Dashboard;
using FitCore.Analytics.Domain;
using FitCore.Api.Infrastructure;
using FitCore.Application.Core;
using FitCore.Platform.Domain;

namespace FitCore.Api.Endpoints;

internal static class AnalyticsEndpoints
{
    public static IEndpointRouteBuilder MapAnalyticsEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/analytics").WithTags("Analytics");

        // Contexto común de los reportes: período resuelto en la hora del gimnasio, comparable, sucursales y áreas permitidas.
        group.MapGet("/context", async (IDispatcher dispatcher, CancellationToken ct, PeriodPreset preset = PeriodPreset.Last30Days, DateOnly? from = null, DateOnly? to = null, Guid[]? branchId = null) =>
                (await dispatcher.QueryAsync(new GetAnalyticsContextQuery(preset, from, to, branchId ?? []), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.AnalyticsDashboardRead)
            .Produces<AnalyticsContextDto>()
            .WithName("GetAnalyticsContext");

        group.MapGet("/dashboard", async (IDispatcher dispatcher, CancellationToken ct, PeriodPreset preset = PeriodPreset.Last30Days, DateOnly? from = null, DateOnly? to = null, Guid[]? branchId = null, int expiringDays = 15) =>
                (await dispatcher.QueryAsync(new GetDashboardQuery(preset, from, to, branchId ?? [], expiringDays), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.AnalyticsDashboardRead)
            .Produces<DashboardDto>()
            .WithName("GetAnalyticsDashboard");

        // Evolución del período y de su comparable, por tramos, para los gráficos (el cálculo es del servidor).
        group.MapGet("/dashboard/comparison", async (IDispatcher dispatcher, CancellationToken ct, PeriodPreset preset = PeriodPreset.Last30Days, DateOnly? from = null, DateOnly? to = null, Guid[]? branchId = null) =>
                (await dispatcher.QueryAsync(new GetDashboardSeriesQuery(preset, from, to, branchId ?? []), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.AnalyticsDashboardRead)
            .Produces<DashboardSeriesDto>()
            .WithName("GetAnalyticsDashboardComparison");

        return routes;
    }
}
