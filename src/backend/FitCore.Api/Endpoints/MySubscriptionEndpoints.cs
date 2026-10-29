using FitCore.Api.Infrastructure;
using FitCore.Application.Core;
using FitCore.Platform.Domain;
using FitCore.Subscriptions.Application.Billing;
using FitCore.Subscriptions.Application.Entitlements;
using FitCore.Subscriptions.Application.Portal;

namespace FitCore.Api.Endpoints;

/// <summary>
/// Portal «Mi suscripción»: lo que el gimnasio ve de su propia suscripción comercial y lo que puede pedir. Todo sale del gimnasio del token: no hay ningún
/// identificador de gimnasio en la ruta. El gimnasio nunca modifica su contrato: pide, y la plataforma aprueba o rechaza (<c>/platform/subscription-requests</c>).
/// </summary>
internal static class MySubscriptionEndpoints
{
    public static IEndpointRouteBuilder MapMySubscriptionEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/my/subscription").WithTags("Mi suscripción");

        group.MapGet("/", async (IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.QueryAsync(new GetMySubscriptionQuery(), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.SubscriptionRead)
            .Produces<MySubscriptionDto>()
            .WithName("GetMySubscription");

        group.MapGet("/usage", async (IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.QueryAsync(new GetMyUsageQuery(), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.SubscriptionRead)
            .Produces<UsageDto>()
            .WithName("GetMySubscriptionUsage");

        group.MapGet("/plans", async (IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.QueryAsync(new ListMyPlansQuery(), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.SubscriptionRead)
            .Produces<IReadOnlyList<AvailablePlanDto>>()
            .WithName("ListMySubscriptionPlans");

        group.MapGet("/invoices", async (IDispatcher dispatcher, CancellationToken ct, int page = 1, int pageSize = 25) =>
                (await dispatcher.QueryAsync(new ListMyInvoicesQuery(new PageRequest(page, pageSize)), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.SubscriptionRead)
            .Produces<PagedResult<InvoiceSummaryDto>>()
            .WithName("ListMySubscriptionInvoices");

        group.MapGet("/notices", async (IDispatcher dispatcher, CancellationToken ct, bool unreadOnly = false, int page = 1, int pageSize = 25) =>
                (await dispatcher.QueryAsync(new ListMyNoticesQuery(new PageRequest(page, pageSize), unreadOnly), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.SubscriptionRead)
            .Produces<PagedResult<NoticeDto>>()
            .WithName("ListMySubscriptionNotices");

        group.MapPost("/notices/read", async (IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new MarkNoticeReadCommand(null), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.SubscriptionRead)
            .Produces<int>()
            .WithName("MarkAllMySubscriptionNoticesRead");

        group.MapPost("/notices/{id:guid}/read", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new MarkNoticeReadCommand(id), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.SubscriptionRead)
            .Produces<int>()
            .WithName("MarkMySubscriptionNoticeRead");

        group.MapGet("/requests", async (IDispatcher dispatcher, CancellationToken ct, int page = 1, int pageSize = 25) =>
                (await dispatcher.QueryAsync(new ListMyRequestsQuery(new PageRequest(page, pageSize)), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.SubscriptionRead)
            .Produces<PagedResult<SubscriptionRequestDto>>()
            .WithName("ListMySubscriptionRequests");

        group.MapPost("/requests", async (NewSubscriptionRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new CreateRequestCommand(request), ct)).ToHttp(r => Results.Created("/api/v1/my/subscription/requests", r)))
            .RequirePermission(Permissions.SubscriptionManage)
            .WithValidation<NewSubscriptionRequest>()
            .Produces<SubscriptionRequestDto>(StatusCodes.Status201Created)
            .WithName("CreateMySubscriptionRequest");

        group.MapPost("/requests/{id:guid}/withdraw", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new WithdrawRequestCommand(id), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.SubscriptionManage)
            .Produces<SubscriptionRequestDto>()
            .WithName("WithdrawMySubscriptionRequest");

        // Lado de la plataforma: resolver las solicitudes de los gimnasios.
        var platform = routes.MapGroup("/platform/subscription-requests").WithTags("Platform · Solicitudes de suscripción");

        platform.MapGet("/", async (IDispatcher dispatcher, CancellationToken ct, string? status = null, Guid? tenantId = null, int page = 1, int pageSize = 25) =>
                (await dispatcher.QueryAsync(new ListPlatformRequestsQuery(new PageRequest(page, pageSize), status, tenantId), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.PlatformSubscriptionsRead)
            .Produces<PagedResult<PlatformRequestDto>>()
            .WithName("ListPlatformSubscriptionRequests");

        platform.MapPost("/{id:guid}/approve", async (Guid id, ResolveSubscriptionRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new ApproveRequestCommand(id, request), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.PlatformSubscriptionsManage)
            .WithValidation<ResolveSubscriptionRequest>()
            .Produces<PlatformRequestDto>()
            .WithName("ApprovePlatformSubscriptionRequest");

        platform.MapPost("/{id:guid}/reject", async (Guid id, ResolveSubscriptionRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new RejectRequestCommand(id, request), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.PlatformSubscriptionsManage)
            .WithValidation<ResolveSubscriptionRequest>()
            .Produces<PlatformRequestDto>()
            .WithName("RejectPlatformSubscriptionRequest");

        return routes;
    }
}
