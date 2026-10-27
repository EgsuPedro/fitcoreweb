using FitCore.Api.Infrastructure;
using FitCore.Application.Core;
using FitCore.Platform.Domain;
using FitCore.Wellness.Application.Trainers;
using FitCore.Wellness.Domain;

namespace FitCore.Api.Endpoints;

internal static class WellnessEndpoints
{
    public static IEndpointRouteBuilder MapWellnessEndpoints(this IEndpointRouteBuilder routes)
    {
        MapTrainers(routes.MapGroup("/trainers").WithTags("Trainers"));
        MapMemberTrainerData(routes.MapGroup("/members").WithTags("Trainers"));
        return routes;
    }

    private static void MapTrainers(RouteGroupBuilder group)
    {
        group.MapGet("/", async (IDispatcher dispatcher, CancellationToken ct, string? search = null, TrainerStatus? status = null, Guid? branchId = null, int page = 1, int pageSize = 25) =>
                (await dispatcher.QueryAsync(new ListTrainersQuery(new PageRequest(page, pageSize), search, status, branchId), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.TrainersRead)
            .Produces<PagedResult<TrainerDto>>()
            .WithName("ListTrainers");

        // Perfil del propio entrenador (404 si el usuario no tiene perfil).
        group.MapGet("/me", async (IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.QueryAsync(new GetMyTrainerQuery(), ct)).ToHttp(t => Results.Ok(t)))
            .RequirePermission(Permissions.TrainersRead)
            .Produces<TrainerDto>()
            .WithName("GetMyTrainer");

        group.MapGet("/{id:guid}", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.QueryAsync(new GetTrainerQuery(id), ct)).ToHttp(t => Results.Ok(t)))
            .RequirePermission(Permissions.TrainersRead)
            .Produces<TrainerDto>()
            .WithName("GetTrainer");

        group.MapPost("/", async (CreateTrainerRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new CreateTrainerCommand(request), ct)).ToHttp(t => Results.Created($"/api/v1/trainers/{t.Id}", t)))
            .RequirePermission(Permissions.TrainersManage)
            .WithValidation<CreateTrainerRequest>()
            .Produces<TrainerDto>(StatusCodes.Status201Created)
            .WithName("CreateTrainer");

        group.MapPut("/{id:guid}", async (Guid id, UpdateTrainerRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new UpdateTrainerCommand(id, request), ct)).ToHttp(t => Results.Ok(t)))
            .RequirePermission(Permissions.TrainersManage)
            .WithValidation<UpdateTrainerRequest>()
            .Produces<TrainerDto>()
            .WithName("UpdateTrainer");

        group.MapPatch("/{id:guid}/status", async (Guid id, SetTrainerStatusRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new SetTrainerStatusCommand(id, request), ct)).ToHttp(t => Results.Ok(t)))
            .RequirePermission(Permissions.TrainersManage)
            .Produces<TrainerDto>()
            .WithName("SetTrainerStatus");

        group.MapGet("/{id:guid}/availability", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.QueryAsync(new GetAvailabilityQuery(id), ct)).ToHttp(a => Results.Ok(a)))
            .RequirePermission(Permissions.TrainersRead)
            .Produces<IReadOnlyList<AvailabilityDto>>()
            .WithName("GetTrainerAvailability");

        // Cada entrenador cambia la suya; la de otro exige trainers.manage (lo comprueba el caso de uso).
        group.MapPut("/{id:guid}/availability", async (Guid id, SetAvailabilityRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new SetAvailabilityCommand(id, request), ct)).ToHttp(a => Results.Ok(a)))
            .RequirePermission(Permissions.TrainersRead)
            .WithValidation<SetAvailabilityRequest>()
            .Produces<IReadOnlyList<AvailabilityDto>>()
            .WithName("SetTrainerAvailability");

        // Un entrenador ve solo sus alumnos; los de otro exigen trainers.manage (lo comprueba el caso de uso).
        group.MapGet("/{id:guid}/members", async (Guid id, IDispatcher dispatcher, CancellationToken ct, bool activeOnly = true, int page = 1, int pageSize = 25) =>
                (await dispatcher.QueryAsync(new ListTrainerMembersQuery(id, activeOnly, new PageRequest(page, pageSize)), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.TrainersRead)
            .Produces<PagedResult<AssignmentDto>>()
            .WithName("ListTrainerMembers");

        group.MapPost("/{id:guid}/members", async (Guid id, AssignMemberRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new AssignMemberCommand(id, request), ct)).ToHttp(a => Results.Created($"/api/v1/trainers/{id}/members", a)))
            .RequirePermission(Permissions.TrainersManage)
            .WithValidation<AssignMemberRequest>()
            .Produces<AssignmentDto>(StatusCodes.Status201Created)
            .WithName("AssignMemberToTrainer");

        group.MapPost("/{id:guid}/members/{memberId:guid}/unassign", async (Guid id, Guid memberId, UnassignMemberRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new UnassignMemberCommand(id, memberId, request), ct)).ToHttp(a => Results.Ok(a)))
            .RequirePermission(Permissions.TrainersManage)
            .WithValidation<UnassignMemberRequest>()
            .Produces<AssignmentDto>()
            .WithName("UnassignMemberFromTrainer");
    }

    private static void MapMemberTrainerData(RouteGroupBuilder group)
    {
        // Historial de entrenadores del alumno (quién lo entrenó y por qué cambió).
        group.MapGet("/{id:guid}/trainer-assignments", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.QueryAsync(new ListMemberAssignmentsQuery(id), ct)).ToHttp(a => Results.Ok(a)))
            .RequirePermission(Permissions.TrainersManage)
            .Produces<IReadOnlyList<AssignmentDto>>()
            .WithName("ListMemberTrainerAssignments");

        // Observaciones profesionales: acceso restringido al entrenador asignado (el permiso es sensible y no lo tiene el administrador).
        group.MapPost("/{id:guid}/trainer-notes", async (Guid id, CreateTrainerNoteRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new CreateTrainerNoteCommand(id, request), ct)).ToHttp(n => Results.Created($"/api/v1/members/{id}/trainer-notes", n)))
            .RequirePermission(Permissions.TrainersNotes)
            .WithValidation<CreateTrainerNoteRequest>()
            .Produces<TrainerNoteDto>(StatusCodes.Status201Created)
            .WithName("CreateTrainerNote");

        group.MapGet("/{id:guid}/trainer-notes", async (Guid id, IDispatcher dispatcher, CancellationToken ct, int page = 1, int pageSize = 25) =>
                (await dispatcher.QueryAsync(new ListTrainerNotesQuery(id, new PageRequest(page, pageSize)), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.TrainersNotes)
            .Produces<PagedResult<TrainerNoteDto>>()
            .WithName("ListTrainerNotes");
    }
}
