using FitCore.Api.Infrastructure;
using FitCore.Application.Core;
using FitCore.Classes.Application.GroupClasses;
using FitCore.Classes.Application.Sessions;
using FitCore.Classes.Domain;
using FitCore.Platform.Domain;

namespace FitCore.Api.Endpoints;

internal static class ClassesEndpoints
{
    public static IEndpointRouteBuilder MapClassesEndpoints(this IEndpointRouteBuilder routes)
    {
        MapGroupClasses(routes.MapGroup("/group-classes").WithTags("Group classes"));
        MapSessions(routes.MapGroup("/class-sessions").WithTags("Class sessions"));
        return routes;
    }

    private static void MapSessions(RouteGroupBuilder group)
    {
        group.MapGet("/", async (IDispatcher dispatcher, CancellationToken ct, DateTimeOffset? from = null, DateTimeOffset? to = null, Guid? classId = null, Guid? branchId = null, Guid? instructorId = null, Guid? seriesId = null, SessionStatus? status = null, int page = 1, int pageSize = 25) =>
                (await dispatcher.QueryAsync(new ListSessionsQuery(new PageRequest(page, pageSize), from, to, classId, branchId, instructorId, seriesId, status), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.ClassSessionsRead)
            .Produces<PagedResult<ClassSessionDto>>()
            .WithName("ListClassSessions");

        group.MapGet("/{id:guid}", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.QueryAsync(new GetSessionQuery(id), ct)).ToHttp(s => Results.Ok(s)))
            .RequirePermission(Permissions.ClassSessionsRead)
            .Produces<ClassSessionDto>()
            .WithName("GetClassSession");

        group.MapPost("/", async (CreateSessionRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new CreateSessionCommand(request), ct)).ToHttp(s => Results.Created($"/api/v1/class-sessions/{s.Id}", s)))
            .RequirePermission(Permissions.ClassSessionsManage)
            .WithValidation<CreateSessionRequest>()
            .Produces<ClassSessionDto>(StatusCodes.Status201Created)
            .WithName("CreateClassSession");

        group.MapPatch("/{id:guid}", async (Guid id, UpdateSessionRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new UpdateSessionCommand(id, request), ct)).ToHttp(s => Results.Ok(s)))
            .RequirePermission(Permissions.ClassSessionsManage)
            .WithValidation<UpdateSessionRequest>()
            .Produces<ClassSessionDto>()
            .WithName("UpdateClassSession");

        group.MapPost("/{id:guid}/cancel", async (Guid id, CancelRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new CancelSessionCommand(id, request), ct)).ToHttp(s => Results.Ok(s)))
            .RequirePermission(Permissions.ClassSessionsManage)
            .WithValidation<CancelRequest>()
            .Produces<ClassSessionDto>()
            .WithName("CancelClassSession");

        var series = group.MapGroup("/recurrences");

        series.MapGet("/", async (IDispatcher dispatcher, CancellationToken ct, Guid? classId = null, Guid? branchId = null, SeriesStatus? status = null, int page = 1, int pageSize = 25) =>
                (await dispatcher.QueryAsync(new ListSeriesQuery(new PageRequest(page, pageSize), classId, branchId, status), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.ClassSessionsRead)
            .Produces<PagedResult<ClassSeriesDto>>()
            .WithName("ListClassSeries");

        series.MapGet("/{id:guid}", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.QueryAsync(new GetSeriesQuery(id), ct)).ToHttp(s => Results.Ok(s)))
            .RequirePermission(Permissions.ClassSessionsRead)
            .Produces<ClassSeriesDto>()
            .WithName("GetClassSeries");

        series.MapPost("/", async (CreateSeriesRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new CreateSeriesCommand(request), ct)).ToHttp(r => Results.Created($"/api/v1/class-sessions/recurrences/{r.Series.Id}", r)))
            .RequirePermission(Permissions.ClassSessionsManage)
            .WithValidation<CreateSeriesRequest>()
            .Produces<SeriesRunDto>(StatusCodes.Status201Created)
            .WithName("CreateClassSeries");

        series.MapPost("/{id:guid}/generate", async (Guid id, GenerateSeriesRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new GenerateSeriesCommand(id, request), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.ClassSessionsManage)
            .Produces<SeriesRunDto>()
            .WithName("GenerateClassSeries");

        series.MapPost("/{id:guid}/cancel", async (Guid id, CancelRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new CancelSeriesCommand(id, request), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.ClassSessionsManage)
            .WithValidation<CancelRequest>()
            .Produces<CancelSeriesResultDto>()
            .WithName("CancelClassSeries");
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
