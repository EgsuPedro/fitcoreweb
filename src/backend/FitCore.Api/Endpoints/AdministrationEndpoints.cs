using FitCore.Api.Infrastructure;
using FitCore.Application.Core;
using FitCore.Platform.Application.Branches;
using FitCore.Platform.Application.Roles;
using FitCore.Platform.Application.Users;
using FitCore.Platform.Domain;

namespace FitCore.Api.Endpoints;

internal static class AdministrationEndpoints
{
    public static IEndpointRouteBuilder MapAdministrationEndpoints(this IEndpointRouteBuilder routes)
    {
        MapBranches(routes.MapGroup("/branches").WithTags("Branches"));
        MapUsers(routes.MapGroup("/users").WithTags("Users"));
        MapRolesAndAudit(routes);
        return routes;
    }

    private static void MapBranches(RouteGroupBuilder group)
    {
        group.MapGet("/", async (IDispatcher dispatcher, CancellationToken ct, string? search = null, bool? isActive = null, int page = 1, int pageSize = 25) =>
                (await dispatcher.QueryAsync(new ListBranchesQuery(new PageRequest(page, pageSize), search, isActive), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.BranchesRead)
            .WithName("ListBranches");

        group.MapGet("/{id:guid}", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.QueryAsync(new GetBranchQuery(id), ct)).ToHttp(b => Results.Ok(b)))
            .RequirePermission(Permissions.BranchesRead)
            .WithName("GetBranch");

        group.MapPost("/", async (CreateBranchRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new CreateBranchCommand(request), ct)).ToHttp(b => Results.Created($"/api/v1/branches/{b.Id}", b)))
            .RequirePermission(Permissions.BranchesCreate)
            .WithValidation<CreateBranchRequest>()
            .WithName("CreateBranch");

        group.MapPut("/{id:guid}", async (Guid id, UpdateBranchRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new UpdateBranchCommand(id, request), ct)).ToHttp(b => Results.Ok(b)))
            .RequirePermission(Permissions.BranchesUpdate)
            .WithValidation<UpdateBranchRequest>()
            .WithName("UpdateBranch");
    }

    private static void MapUsers(RouteGroupBuilder group)
    {
        group.MapGet("/", async (IDispatcher dispatcher, CancellationToken ct, string? search = null, Guid? branchId = null, bool? isActive = null, int page = 1, int pageSize = 25) =>
                (await dispatcher.QueryAsync(new ListUsersQuery(new PageRequest(page, pageSize), search, branchId, isActive), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.UsersRead)
            .WithName("ListUsers");

        group.MapGet("/{id:guid}", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.QueryAsync(new GetUserQuery(id), ct)).ToHttp(u => Results.Ok(u)))
            .RequirePermission(Permissions.UsersRead)
            .WithName("GetUser");

        group.MapPost("/", async (CreateUserRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new CreateUserCommand(request), ct)).ToHttp(u => Results.Created($"/api/v1/users/{u.Id}", u)))
            .RequirePermission(Permissions.UsersCreate)
            .WithValidation<CreateUserRequest>()
            .WithName("CreateUser");

        group.MapPut("/{id:guid}/access", async (Guid id, SetUserAccessRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new SetUserAccessCommand(id, request), ct)).ToHttp(u => Results.Ok(u)))
            .RequirePermission(Permissions.UsersUpdate)
            .WithName("SetUserAccess");

        group.MapPut("/{id:guid}/status", async (Guid id, SetUserStatusRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new SetUserStatusCommand(id, request), ct)).ToHttp(u => Results.Ok(u)))
            .RequirePermission(Permissions.UsersUpdate)
            .WithName("SetUserStatus");
    }

    private static void MapRolesAndAudit(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/roles", async (IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.QueryAsync(new ListRolesQuery(), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.RolesRead)
            .WithTags("Roles")
            .WithName("ListRoles");

        routes.MapGet("/permissions", async (IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.QueryAsync(new ListPermissionsQuery(), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.RolesRead)
            .WithTags("Roles")
            .WithName("ListPermissions");

        routes.MapGet("/audit-logs", async (
                IDispatcher dispatcher,
                CancellationToken ct,
                string? entity = null,
                string? entityId = null,
                DateTimeOffset? from = null,
                DateTimeOffset? to = null,
                int page = 1,
                int pageSize = 25) =>
                (await dispatcher.QueryAsync(new ListAuditLogsQuery(new PageRequest(page, pageSize), entity, entityId, from, to), ct))
                    .ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.AuditRead)
            .WithTags("Audit")
            .WithName("ListAuditLogs");
    }
}
