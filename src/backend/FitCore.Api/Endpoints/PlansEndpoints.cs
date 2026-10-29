using FitCore.Api.Infrastructure;
using FitCore.Application.Core;
using FitCore.Platform.Domain;
using FitCore.Subscriptions.Application.Plans;

namespace FitCore.Api.Endpoints;

/// <summary>
/// Planes comerciales de FIT360 (lo que la plataforma le vende a un gimnasio). Solo el equipo de plataforma: los permisos
/// <c>platform.plans.*</c> no se pueden asignar fuera del gimnasio «platform». No tienen relación con <c>/membership-plans</c>.
/// </summary>
internal static class PlansEndpoints
{
    public static IEndpointRouteBuilder MapPlansEndpoints(this IEndpointRouteBuilder routes)
    {
        var plans = routes.MapGroup("/platform/plans").WithTags("Platform · Planes comerciales");

        plans.MapGet("/", async (IDispatcher dispatcher, CancellationToken ct, string? search = null, bool? isActive = null, int page = 1, int pageSize = 25) =>
                (await dispatcher.QueryAsync(new ListPlansQuery(new PageRequest(page, pageSize), search, isActive), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.PlatformPlansRead)
            .Produces<PagedResult<PlanSummaryDto>>()
            .WithName("ListCommercialPlans");

        plans.MapGet("/catalog", async (IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.QueryAsync(new GetPlanCatalogQuery(), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.PlatformPlansRead)
            .Produces<PlanCatalogDto>()
            .WithName("GetCommercialPlanCatalog");

        plans.MapGet("/{id:guid}", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.QueryAsync(new GetPlanQuery(id), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.PlatformPlansRead)
            .Produces<PlanDto>()
            .WithName("GetCommercialPlan");

        plans.MapPost("/", async (CreatePlanRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new CreatePlanCommand(request), ct)).ToHttp(r => Results.Created($"/api/v1/platform/plans/{r.Id}", r)))
            .RequirePermission(Permissions.PlatformPlansManage)
            .WithValidation<CreatePlanRequest>()
            .Produces<PlanDto>(StatusCodes.Status201Created)
            .WithName("CreateCommercialPlan");

        plans.MapPut("/{id:guid}", async (Guid id, UpdatePlanRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new UpdatePlanCommand(id, request), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.PlatformPlansManage)
            .WithValidation<UpdatePlanRequest>()
            .Produces<PlanDto>()
            .WithName("UpdateCommercialPlan");

        plans.MapPatch("/{id:guid}/status", async (Guid id, SetPlanStatusRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new SetPlanStatusCommand(id, request.IsActive), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.PlatformPlansManage)
            .Produces<PlanDto>()
            .WithName("SetCommercialPlanStatus");

        return routes;
    }
}
