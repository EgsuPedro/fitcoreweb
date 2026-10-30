using FitCore.Api.Infrastructure;
using FitCore.Application.Core;
using FitCore.Subscriptions.Application.Public;

namespace FitCore.Api.Endpoints;

/// <summary>
/// Rutas públicas del sitio comercial: sin sesión, con límite de frecuencia por IP y sin datos privados de ningún gimnasio.
/// Los planes se leen del catálogo real; nunca hay precios escritos a mano en el sitio.
/// </summary>
internal static class PublicEndpoints
{
    public static IEndpointRouteBuilder MapPublicEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/public").WithTags("Sitio público");

        group.MapGet("/plans", async (IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
            {
                var result = await dispatcher.QueryAsync(new ListPublicPlansQuery(), ct);
                if (result.IsSuccess)
                {
                    CachePublic(http);
                }

                return result.ToHttp(r => Results.Ok(r));
            })
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.PublicRead)
            .Produces<IReadOnlyList<PublicPlanDto>>()
            .WithName("ListPublicPlans");

        group.MapGet("/plans/{code}", async (string code, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
            {
                var result = await dispatcher.QueryAsync(new GetPublicPlanQuery(code), ct);
                if (result.IsSuccess)
                {
                    CachePublic(http);
                }

                return result.ToHttp(r => Results.Ok(r));
            })
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.PublicRead)
            .Produces<PublicPlanDto>()
            .WithName("GetPublicPlan");

        return routes;
    }

    // Un minuto de caché compartida: un cambio de precio se ve en menos de un minuto y el sitio resiste picos de visitas.
    private static void CachePublic(HttpContext http) =>
        http.Response.Headers.CacheControl = "public, max-age=60";
}
