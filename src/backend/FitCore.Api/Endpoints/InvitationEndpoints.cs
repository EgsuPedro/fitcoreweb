using FitCore.Api.Infrastructure;
using FitCore.Application.Core;
using FitCore.Platform.Application.Invitations;
using FitCore.Platform.Domain;

namespace FitCore.Api.Endpoints;

/// <summary>
/// Invitaciones de personal del gimnasio del token. Invitar exige poder crear usuarios; el rol y las sucursales se validan con las mismas reglas que al crear un
/// usuario a mano (no se puede dar más de lo que se tiene). Aceptar y ver el enlace son rutas públicas (<c>/public/invitations</c>).
/// </summary>
internal static class InvitationEndpoints
{
    public static IEndpointRouteBuilder MapInvitationEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/invitations").WithTags("Invitaciones");

        group.MapGet("/", async (IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.QueryAsync(new ListInvitationsQuery(), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.UsersRead)
            .Produces<IReadOnlyList<InvitationDto>>()
            .WithName("ListInvitations");

        group.MapPost("/", async (CreateInvitationRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new CreateInvitationCommand(request), ct)).ToHttp(r => Results.Created($"/api/v1/invitations/{r.Id}", r)))
            .RequirePermission(Permissions.UsersCreate)
            .WithValidation<CreateInvitationRequest>()
            .Produces<InvitationDto>(StatusCodes.Status201Created)
            .WithName("CreateInvitation");

        group.MapPost("/{id:guid}/revoke", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new RevokeInvitationCommand(id), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.UsersCreate)
            .Produces<InvitationDto>()
            .WithName("RevokeInvitation");

        group.MapPost("/{id:guid}/resend", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new ResendInvitationCommand(id), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.UsersCreate)
            .Produces<InvitationDto>()
            .WithName("ResendInvitation");

        return routes;
    }
}
