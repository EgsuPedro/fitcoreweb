using FitCore.Analytics.Application;
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

        return routes;
    }
}
