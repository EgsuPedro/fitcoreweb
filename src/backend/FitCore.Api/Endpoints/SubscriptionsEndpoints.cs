using FitCore.Api.Infrastructure;
using FitCore.Application.Core;
using FitCore.Platform.Domain;
using FitCore.Subscriptions.Application.Subscriptions;

namespace FitCore.Api.Endpoints;

/// <summary>
/// Suscripciones comerciales de los gimnasios (administración de la plataforma). Es el contrato de uso de FitCore 360, no las membresías
/// de los alumnos. <c>/platform/tenants/{id}/suspend</c> sigue siendo la suspensión administrativa (cierra el acceso); la suspensión
/// <i>comercial</i> es <c>/subscription/suspend</c> y no cierra la sesión.
/// </summary>
internal static class SubscriptionsEndpoints
{
    public static IEndpointRouteBuilder MapSubscriptionsEndpoints(this IEndpointRouteBuilder routes)
    {
        var tenant = routes.MapGroup("/platform/tenants/{id:guid}/subscription").WithTags("Platform · Suscripciones");

        tenant.MapGet("/", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.QueryAsync(new GetTenantSubscriptionQuery(id), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.PlatformSubscriptionsRead)
            .Produces<SubscriptionDto>()
            .WithName("GetTenantSubscription");

        tenant.MapGet("/history", async (Guid id, IDispatcher dispatcher, CancellationToken ct, int page = 1, int pageSize = 25) =>
                (await dispatcher.QueryAsync(new GetSubscriptionHistoryQuery(id, new PageRequest(page, pageSize)), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.PlatformSubscriptionsRead)
            .Produces<PagedResult<SubscriptionHistoryItemDto>>()
            .WithName("GetTenantSubscriptionHistory");

        tenant.MapPost("/", async (Guid id, CreateSubscriptionRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new CreateSubscriptionCommand(id, request), ct)).ToHttp(r => Results.Created($"/api/v1/platform/tenants/{id}/subscription", r)))
            .RequirePermission(Permissions.PlatformSubscriptionsManage)
            .WithValidation<CreateSubscriptionRequest>()
            .Produces<SubscriptionDto>(StatusCodes.Status201Created)
            .WithName("CreateTenantSubscription");

        tenant.MapPost("/activate", async (Guid id, ActivateSubscriptionRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new ActivateSubscriptionCommand(id, request), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.PlatformSubscriptionsManage)
            .WithValidation<ActivateSubscriptionRequest>()
            .Produces<SubscriptionDto>()
            .WithName("ActivateTenantSubscription");

        tenant.MapPost("/suspend", async (Guid id, SuspendSubscriptionRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new SuspendSubscriptionCommand(id, request), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.PlatformSubscriptionsManage)
            .WithValidation<SuspendSubscriptionRequest>()
            .Produces<SubscriptionDto>()
            .WithName("SuspendTenantSubscription");

        tenant.MapPost("/reactivate", async (Guid id, ReactivateSubscriptionRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new ReactivateSubscriptionCommand(id, request), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.PlatformSubscriptionsManage)
            .WithValidation<ReactivateSubscriptionRequest>()
            .Produces<SubscriptionDto>()
            .WithName("ReactivateTenantSubscription");

        routes.MapGet("/platform/subscriptions", async (
                IDispatcher dispatcher, CancellationToken ct, string? status = null, string? planCode = null, string? search = null, int? dueWithinDays = null, int page = 1, int pageSize = 25) =>
                (await dispatcher.QueryAsync(new ListSubscriptionsQuery(new PageRequest(page, pageSize), status, planCode, search, dueWithinDays), ct)).ToHttp(r => Results.Ok(r)))
            .WithTags("Platform · Suscripciones")
            .RequirePermission(Permissions.PlatformSubscriptionsRead)
            .Produces<PagedResult<SubscriptionSummaryDto>>()
            .WithName("ListSubscriptions");

        return routes;
    }
}
