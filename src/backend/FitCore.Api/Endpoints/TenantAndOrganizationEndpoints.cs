using FitCore.Api.Infrastructure;
using FitCore.Application.Core;
using FitCore.Platform.Application.Organizations;
using FitCore.Platform.Application.Tenants;
using FitCore.Platform.Domain;

namespace FitCore.Api.Endpoints;

internal static class TenantAndOrganizationEndpoints
{
    public static IEndpointRouteBuilder MapTenantAndOrganizationEndpoints(this IEndpointRouteBuilder routes)
    {
        var platform = routes.MapGroup("/platform/tenants").WithTags("Platform");

        platform.MapGet("/", async (IDispatcher dispatcher, CancellationToken ct, int page = 1, int pageSize = 25) =>
                (await dispatcher.QueryAsync(new ListTenantsQuery(new PageRequest(page, pageSize)), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.PlatformTenantsManage)
            .Produces<PagedResult<TenantDto>>()
            .WithName("ListTenants");

        platform.MapPost("/", async (CreateTenantRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new CreateTenantCommand(request), ct))
                    .ToHttp(t => Results.Created($"/api/v1/platform/tenants/{t.Id}", t)))
            .RequirePermission(Permissions.PlatformTenantsManage)
            .WithValidation<CreateTenantRequest>()
            .Produces<TenantDto>(StatusCodes.Status201Created)
            .WithName("CreateTenant");

        platform.MapPost("/{id:guid}/suspend", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new SetTenantStatusCommand(id, Active: false), ct)).ToHttp(t => Results.Ok(t)))
            .RequirePermission(Permissions.PlatformTenantsManage)
            .Produces<TenantDto>()
            .WithName("SuspendTenant");

        platform.MapPost("/{id:guid}/activate", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new SetTenantStatusCommand(id, Active: true), ct)).ToHttp(t => Results.Ok(t)))
            .RequirePermission(Permissions.PlatformTenantsManage)
            .Produces<TenantDto>()
            .WithName("ActivateTenant");

        var organization = routes.MapGroup("/organization").WithTags("Organization");

        organization.MapGet("/", async (IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.QueryAsync(new GetOrganizationQuery(), ct)).ToHttp(t => Results.Ok(t)))
            .RequirePermission(Permissions.OrganizationRead)
            .Produces<TenantDto>()
            .WithName("GetOrganization");

        organization.MapPut("/", async (UpdateOrganizationRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new UpdateOrganizationCommand(request), ct)).ToHttp(t => Results.Ok(t)))
            .RequirePermission(Permissions.OrganizationUpdate)
            .WithValidation<UpdateOrganizationRequest>()
            .Produces<TenantDto>()
            .WithName("UpdateOrganization");

        return routes;
    }
}
