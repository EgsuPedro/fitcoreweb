using FitCore.Api.Infrastructure;
using FitCore.Application.Core;
using FitCore.Commercial.Application.Members;
using FitCore.Commercial.Domain;
using FitCore.Platform.Domain;

namespace FitCore.Api.Endpoints;

internal static class CommercialEndpoints
{
    public static IEndpointRouteBuilder MapCommercialEndpoints(this IEndpointRouteBuilder routes)
    {
        MapMembers(routes.MapGroup("/members").WithTags("Members"));
        return routes;
    }

    private static void MapMembers(RouteGroupBuilder group)
    {
        group.MapGet("/", async (IDispatcher dispatcher, CancellationToken ct, string? search = null, MemberStatus? status = null, Guid? branchId = null, int page = 1, int pageSize = 25) =>
                (await dispatcher.QueryAsync(new ListMembersQuery(new PageRequest(page, pageSize), search, status, branchId), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.MembersRead)
            .Produces<PagedResult<MemberDto>>()
            .WithName("ListMembers");

        group.MapGet("/{id:guid}", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.QueryAsync(new GetMemberQuery(id), ct)).ToHttp(m => Results.Ok(m)))
            .RequirePermission(Permissions.MembersRead)
            .Produces<MemberDto>()
            .WithName("GetMember");

        group.MapPost("/", async (CreateMemberRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new CreateMemberCommand(request), ct)).ToHttp(m => Results.Created($"/api/v1/members/{m.Id}", m)))
            .RequirePermission(Permissions.MembersCreate)
            .WithValidation<CreateMemberRequest>()
            .Produces<MemberDto>(StatusCodes.Status201Created)
            .WithName("CreateMember");

        group.MapPut("/{id:guid}", async (Guid id, UpdateMemberRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new UpdateMemberCommand(id, request), ct)).ToHttp(m => Results.Ok(m)))
            .RequirePermission(Permissions.MembersUpdate)
            .WithValidation<UpdateMemberRequest>()
            .Produces<MemberDto>()
            .WithName("UpdateMember");

        group.MapPatch("/{id:guid}/status", async (Guid id, SetMemberStatusRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new SetMemberStatusCommand(id, request), ct)).ToHttp(m => Results.Ok(m)))
            .RequirePermission(Permissions.MembersStatus)
            .Produces<MemberDto>()
            .WithName("SetMemberStatus");
    }
}
