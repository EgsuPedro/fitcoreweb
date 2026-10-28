using FitCore.Api.Infrastructure;
using FitCore.Application.Core;
using FitCore.Classes.Application.GroupClasses;
using FitCore.Classes.Domain;
using FitCore.Platform.Domain;

namespace FitCore.Api.Endpoints;

internal static class ClassesEndpoints
{
    public static IEndpointRouteBuilder MapClassesEndpoints(this IEndpointRouteBuilder routes)
    {
        MapGroupClasses(routes.MapGroup("/group-classes").WithTags("Group classes"));
        return routes;
    }

    private static void MapGroupClasses(RouteGroupBuilder group)
    {
        group.MapGet("/", async (IDispatcher dispatcher, CancellationToken ct, string? search = null, ActivityType? activity = null, ClassLevel? level = null, Guid? branchId = null, Guid? instructorId = null, bool? isActive = null, int page = 1, int pageSize = 25) =>
                (await dispatcher.QueryAsync(new ListGroupClassesQuery(new PageRequest(page, pageSize), search, activity, level, branchId, instructorId, isActive), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.GroupClassesRead)
            .Produces<PagedResult<GroupClassDto>>()
            .WithName("ListGroupClasses");

        group.MapGet("/{id:guid}", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.QueryAsync(new GetGroupClassQuery(id), ct)).ToHttp(c => Results.Ok(c)))
            .RequirePermission(Permissions.GroupClassesRead)
            .Produces<GroupClassDto>()
            .WithName("GetGroupClass");

        group.MapPost("/", async (GroupClassRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new CreateGroupClassCommand(request), ct)).ToHttp(c => Results.Created($"/api/v1/group-classes/{c.Id}", c)))
            .RequirePermission(Permissions.GroupClassesManage)
            .WithValidation<GroupClassRequest>()
            .Produces<GroupClassDto>(StatusCodes.Status201Created)
            .WithName("CreateGroupClass");

        group.MapPut("/{id:guid}", async (Guid id, GroupClassRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new UpdateGroupClassCommand(id, request), ct)).ToHttp(c => Results.Ok(c)))
            .RequirePermission(Permissions.GroupClassesManage)
            .WithValidation<GroupClassRequest>()
            .Produces<GroupClassDto>()
            .WithName("UpdateGroupClass");

        group.MapPatch("/{id:guid}/status", async (Guid id, SetGroupClassStatusRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new SetGroupClassStatusCommand(id, request), ct)).ToHttp(c => Results.Ok(c)))
            .RequirePermission(Permissions.GroupClassesManage)
            .Produces<GroupClassDto>()
            .WithName("SetGroupClassStatus");
    }
}
