using FitCore.Api.Infrastructure;
using FitCore.Application.Core;
using FitCore.Platform.Domain;
using FitCore.Subscriptions.Application.Organizations;

namespace FitCore.Api.Endpoints;

/// <summary>
/// Panel global de FitCore 360: organizaciones clientes, notas, actividad administrativa y métricas agregadas. Solo el equipo de plataforma, con el mínimo
/// privilegio: ve el contrato, el cobro y conteos de consumo de cada gimnasio, nunca los datos personales ni clínicos ni financieros de sus alumnos.
/// (Las rutas de <c>/platform/tenants</c> siguen siendo el alta y la suspensión administrativa.)
/// </summary>
internal static class OrganizationsEndpoints
{
    public static IEndpointRouteBuilder MapOrganizationsEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/platform/organizations").WithTags("Platform · Organizaciones");

        group.MapGet("/", async (
                IDispatcher dispatcher, CancellationToken ct, string? search = null, string? tenantStatus = null, string? subscriptionStatus = null,
                string? planCode = null, int? dueWithinDays = null, int page = 1, int pageSize = 25) =>
                (await dispatcher.QueryAsync(new ListOrganizationsQuery(new PageRequest(page, pageSize), search, tenantStatus, subscriptionStatus, planCode, dueWithinDays), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.PlatformOrganizationsRead)
            .Produces<PagedResult<OrganizationSummaryDto>>()
            .WithName("ListPlatformOrganizations");

        group.MapGet("/{id:guid}", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.QueryAsync(new GetOrganizationQuery(id), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.PlatformOrganizationsRead)
            .Produces<OrganizationDto>()
            .WithName("GetPlatformOrganization");

        group.MapPost("/{id:guid}/usage/refresh", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new RefreshUsageCommand(id), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.PlatformOrganizationsManage)
            .Produces<OrganizationUsageDto>()
            .WithName("RefreshOrganizationUsage");

        group.MapGet("/{id:guid}/notes", async (Guid id, IDispatcher dispatcher, CancellationToken ct, int page = 1, int pageSize = 25) =>
                (await dispatcher.QueryAsync(new ListNotesQuery(id, new PageRequest(page, pageSize)), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.PlatformOrganizationsRead)
            .Produces<PagedResult<NoteDto>>()
            .WithName("ListOrganizationNotes");

        group.MapPost("/{id:guid}/notes", async (Guid id, AddNoteRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new AddNoteCommand(id, request), ct)).ToHttp(r => Results.Created($"/api/v1/platform/organizations/{id}/notes", r)))
            .RequirePermission(Permissions.PlatformOrganizationsManage)
            .WithValidation<AddNoteRequest>()
            .Produces<NoteDto>(StatusCodes.Status201Created)
            .WithName("AddOrganizationNote");

        group.MapGet("/{id:guid}/activity", async (Guid id, IDispatcher dispatcher, CancellationToken ct, int page = 1, int pageSize = 25) =>
                (await dispatcher.QueryAsync(new GetActivityQuery(id, new PageRequest(page, pageSize)), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.PlatformOrganizationsRead)
            .Produces<PagedResult<ActivityEntryDto>>()
            .WithName("GetOrganizationActivity");

        routes.MapGet("/platform/metrics", async (IDispatcher dispatcher, CancellationToken ct, DateOnly? from = null, DateOnly? to = null) =>
                (await dispatcher.QueryAsync(new GetMetricsQuery(from, to), ct)).ToHttp(r => Results.Ok(r)))
            .WithTags("Platform · Organizaciones")
            .RequirePermission(Permissions.PlatformMetricsRead)
            .Produces<MetricsDto>()
            .WithName("GetPlatformMetrics");

        return routes;
    }
}
