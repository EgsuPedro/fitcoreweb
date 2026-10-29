using FitCore.Api.Infrastructure;
using FitCore.Application.Core;
using FitCore.Platform.Domain;
using FitCore.Subscriptions.Application.Entitlements;

namespace FitCore.Api.Endpoints;

/// <summary>Lo que el gimnasio ve de su propia suscripción comercial. Todo sale del gimnasio del token: no hay ningún identificador en la ruta.</summary>
internal static class MySubscriptionEndpoints
{
    public static IEndpointRouteBuilder MapMySubscriptionEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/my/subscription").WithTags("Mi suscripción");

        group.MapGet("/usage", async (IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.QueryAsync(new GetMyUsageQuery(), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.SubscriptionRead)
            .Produces<UsageDto>()
            .WithName("GetMySubscriptionUsage");

        return routes;
    }
}
