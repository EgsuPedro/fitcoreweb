using FitCore.Api.Infrastructure;
using FitCore.Application.Core;
using FitCore.Platform.Application.Contact;
using FitCore.Platform.Domain;
using FitCore.SharedKernel;

namespace FitCore.Api.Endpoints;

/// <summary>Solicitudes de contacto y demostración recibidas por el sitio público. Solo las ve y atiende el equipo de plataforma.</summary>
internal static class ContactRequestEndpoints
{
    public static IEndpointRouteBuilder MapContactRequestEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/platform/contact-requests").WithTags("Platform · Solicitudes comerciales");

        group.MapGet("/", async (IDispatcher dispatcher, CancellationToken ct, string? status = null, string? kind = null, int page = 1, int pageSize = 25) =>
                (await dispatcher.QueryAsync(new ListContactRequestsQuery(new PageRequest(page, pageSize), status, kind), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.PlatformLeadsRead)
            .Produces<PagedResult<ContactRequestDto>>()
            .WithName("ListContactRequests");

        group.MapPatch("/{id:guid}", async (Guid id, SetContactAttendedRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new SetContactAttendedCommand(id, request.Attended), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.PlatformLeadsManage)
            .Produces<ContactRequestDto>()
            .WithName("SetContactRequestAttended");

        return routes;
    }
}
