using FitCore.Api.Infrastructure;
using FitCore.Application.Core;
using FitCore.Platform.Domain;
using FitCore.Wellness.Application.Exercises;
using FitCore.Wellness.Application.Trainers;
using FitCore.Wellness.Application.Training;
using FitCore.Wellness.Domain;

namespace FitCore.Api.Endpoints;

internal static class WellnessEndpoints
{
    public static IEndpointRouteBuilder MapWellnessEndpoints(this IEndpointRouteBuilder routes)
    {
        MapTrainers(routes.MapGroup("/trainers").WithTags("Trainers"));
        MapMemberTrainerData(routes.MapGroup("/members").WithTags("Trainers"));
        MapExercises(routes.MapGroup("/exercises").WithTags("Exercises"));
        MapTrainingPlans(routes.MapGroup("/training-plans").WithTags("Training plans"));
        MapTraining(routes);
        MapPlatformExercises(routes.MapGroup("/platform/exercises").WithTags("Exercises (platform)"));
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

    private static void MapTrainingPlans(RouteGroupBuilder group)
    {
        group.MapGet("/", async (IDispatcher dispatcher, CancellationToken ct, string? search = null, PlanStatus? status = null, Difficulty? level = null, int page = 1, int pageSize = 25) =>
                (await dispatcher.QueryAsync(new ListPlansQuery(new PageRequest(page, pageSize), search, status, level), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.TrainingPlansRead)
            .Produces<PagedResult<PlanSummaryDto>>()
            .WithName("ListTrainingPlans");

        group.MapGet("/{id:guid}", async (Guid id, IDispatcher dispatcher, CancellationToken ct, int? version = null) =>
                (await dispatcher.QueryAsync(new GetPlanQuery(id, version), ct)).ToHttp(p => Results.Ok(p)))
            .RequirePermission(Permissions.TrainingPlansRead)
            .Produces<PlanDetailDto>()
            .WithName("GetTrainingPlan");

        group.MapGet("/{id:guid}/versions", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.QueryAsync(new ListPlanVersionsQuery(id), ct)).ToHttp(v => Results.Ok(v)))
            .RequirePermission(Permissions.TrainingPlansRead)
            .Produces<IReadOnlyList<PlanVersionDto>>()
            .WithName("ListTrainingPlanVersions");

        group.MapPost("/", async (TrainingPlanRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new CreatePlanCommand(request), ct)).ToHttp(p => Results.Created($"/api/v1/training-plans/{p.Id}", p)))
            .RequirePermission(Permissions.TrainingPlansManage)
            .WithValidation<TrainingPlanRequest>()
            .Produces<PlanDetailDto>(StatusCodes.Status201Created)
            .WithName("CreateTrainingPlan");

        // Modificar una rutina crea una versión nueva: lo ya asignado y entrenado conserva la suya.
        group.MapPut("/{id:guid}", async (Guid id, TrainingPlanRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new UpdatePlanCommand(id, request), ct)).ToHttp(p => Results.Ok(p)))
            .RequirePermission(Permissions.TrainingPlansManage)
            .WithValidation<TrainingPlanRequest>()
            .Produces<PlanDetailDto>()
            .WithName("UpdateTrainingPlan");

        group.MapPost("/{id:guid}/duplicate", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new DuplicatePlanCommand(id), ct)).ToHttp(p => Results.Created($"/api/v1/training-plans/{p.Id}", p)))
            .RequirePermission(Permissions.TrainingPlansManage)
            .Produces<PlanDetailDto>(StatusCodes.Status201Created)
            .WithName("DuplicateTrainingPlan");

        group.MapPatch("/{id:guid}/status", async (Guid id, SetPlanStatusRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new SetPlanStatusCommand(id, request), ct)).ToHttp(p => Results.Ok(p)))
            .RequirePermission(Permissions.TrainingPlansManage)
            .Produces<PlanSummaryDto>()
            .WithName("SetTrainingPlanStatus");

        group.MapPost("/{id:guid}/assignments", async (Guid id, AssignPlanRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new AssignPlanCommand(id, request), ct)).ToHttp(a => Results.Created($"/api/v1/members/{a.MemberId}/training-plan", a)))
            .RequirePermission(Permissions.TrainingMembersManage)
            .WithValidation<AssignPlanRequest>()
            .Produces<TrainingAssignmentDto>(StatusCodes.Status201Created)
            .WithName("AssignTrainingPlan");
    }

    // Rutina, sesiones y progreso de un alumno. Quién accede a qué alumno lo resuelve el caso de uso desde la sesión del usuario.
    private static void MapTraining(IEndpointRouteBuilder routes)
    {
        routes.MapPost("/training-assignments/{id:guid}/end", async (Guid id, EndAssignmentRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new EndAssignmentCommand(id, request), ct)).ToHttp(a => Results.Ok(a)))
            .RequirePermission(Permissions.TrainingMembersManage)
            .WithValidation<EndAssignmentRequest>()
            .Produces<TrainingAssignmentDto>()
            .WithTags("Training plans")
            .WithName("EndTrainingAssignment");

        routes.MapGet("/members/{id:guid}/training-plan", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.QueryAsync(new GetMemberPlanQuery(id), ct)).ToHttp(p => Results.Ok(p)))
            .RequirePermission(Permissions.TrainingMemberRead)
            .Produces<MemberTrainingPlanDto>()
            .WithTags("Workout sessions")
            .WithName("GetMemberTrainingPlan");

        routes.MapGet("/members/{id:guid}/training-assignments", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.QueryAsync(new ListMemberTrainingAssignmentsQuery(id), ct)).ToHttp(a => Results.Ok(a)))
            .RequirePermission(Permissions.TrainingMemberRead)
            .Produces<IReadOnlyList<TrainingAssignmentDto>>()
            .WithTags("Workout sessions")
            .WithName("ListMemberTrainingAssignments");

        routes.MapPost("/workout-sessions", async (CreateWorkoutSessionRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new CreateWorkoutSessionCommand(request), ct)).ToHttp(s => Results.Created($"/api/v1/workout-sessions/{s.Id}", s)))
            .RequirePermission(Permissions.TrainingSessionsLog)
            .WithValidation<CreateWorkoutSessionRequest>()
            .Produces<WorkoutSessionDto>(StatusCodes.Status201Created)
            .WithTags("Workout sessions")
            .WithName("CreateWorkoutSession");

        routes.MapGet("/workout-sessions/{id:guid}", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.QueryAsync(new GetWorkoutSessionQuery(id), ct)).ToHttp(s => Results.Ok(s)))
            .RequirePermission(Permissions.TrainingMemberRead)
            .Produces<WorkoutSessionDto>()
            .WithTags("Workout sessions")
            .WithName("GetWorkoutSession");

        routes.MapPost("/workout-sessions/{id:guid}/trainer-comment", async (Guid id, TrainerCommentRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new AddTrainerCommentCommand(id, request), ct)).ToHttp(s => Results.Ok(s)))
            .RequirePermission(Permissions.TrainingMembersManage)
            .WithValidation<TrainerCommentRequest>()
            .Produces<WorkoutSessionDto>()
            .WithTags("Workout sessions")
            .WithName("AddWorkoutSessionTrainerComment");

        routes.MapGet("/members/{id:guid}/workout-sessions", async (Guid id, IDispatcher dispatcher, CancellationToken ct, DateOnly? from = null, DateOnly? to = null, int page = 1, int pageSize = 25) =>
                (await dispatcher.QueryAsync(new ListMemberSessionsQuery(id, from, to, new PageRequest(page, pageSize)), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.TrainingMemberRead)
            .Produces<PagedResult<WorkoutSessionDto>>()
            .WithTags("Workout sessions")
            .WithName("ListMemberWorkoutSessions");

        routes.MapGet("/members/{id:guid}/workout-sessions/compare", async (Guid id, Guid sessionA, Guid sessionB, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.QueryAsync(new CompareSessionsQuery(id, sessionA, sessionB), ct)).ToHttp(c => Results.Ok(c)))
            .RequirePermission(Permissions.TrainingMemberRead)
            .Produces<SessionComparisonDto>()
            .WithTags("Workout sessions")
            .WithName("CompareWorkoutSessions");

        routes.MapGet("/members/{id:guid}/exercise-progress", async (Guid id, Guid exerciseId, IDispatcher dispatcher, CancellationToken ct, DateOnly? from = null, DateOnly? to = null) =>
                (await dispatcher.QueryAsync(new ExerciseProgressQuery(id, exerciseId, from, to), ct)).ToHttp(p => Results.Ok(p)))
            .RequirePermission(Permissions.TrainingMemberRead)
            .Produces<ExerciseProgressDto>()
            .WithTags("Workout sessions")
            .WithName("GetExerciseProgress");
    }

    private static void MapExercises(RouteGroupBuilder group)
    {
        group.MapGet("/", async (IDispatcher dispatcher, CancellationToken ct, string? search = null, MuscleGroup? muscle = null, Equipment? equipment = null, Difficulty? difficulty = null, ExerciseType? type = null, string? scope = null, bool? isActive = null, int page = 1, int pageSize = 25) =>
                (await dispatcher.QueryAsync(new ListExercisesQuery(new PageRequest(page, pageSize), search, muscle, equipment, difficulty, type, scope, isActive), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.ExercisesRead)
            .Produces<PagedResult<ExerciseDto>>()
            .WithName("ListExercises");

        group.MapGet("/{id:guid}", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.QueryAsync(new GetExerciseQuery(id), ct)).ToHttp(e => Results.Ok(e)))
            .RequirePermission(Permissions.ExercisesRead)
            .Produces<ExerciseDto>()
            .WithName("GetExercise");

        group.MapPost("/", async (ExerciseRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new CreateExerciseCommand(request, Global: false), ct)).ToHttp(e => Results.Created($"/api/v1/exercises/{e.Id}", e)))
            .RequirePermission(Permissions.ExercisesManage)
            .WithValidation<ExerciseRequest>()
            .Produces<ExerciseDto>(StatusCodes.Status201Created)
            .WithName("CreateExercise");

        group.MapPut("/{id:guid}", async (Guid id, ExerciseRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new UpdateExerciseCommand(id, request, Global: false), ct)).ToHttp(e => Results.Ok(e)))
            .RequirePermission(Permissions.ExercisesManage)
            .WithValidation<ExerciseRequest>()
            .Produces<ExerciseDto>()
            .WithName("UpdateExercise");

        group.MapPatch("/{id:guid}/status", async (Guid id, SetExerciseStatusRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new SetExerciseStatusCommand(id, request, Global: false), ct)).ToHttp(e => Results.Ok(e)))
            .RequirePermission(Permissions.ExercisesManage)
            .Produces<ExerciseDto>()
            .WithName("SetExerciseStatus");

        // Copia un ejercicio (global o propio) al gimnasio para adaptarlo.
        group.MapPost("/{id:guid}/duplicate", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new DuplicateExerciseCommand(id), ct)).ToHttp(e => Results.Created($"/api/v1/exercises/{e.Id}", e)))
            .RequirePermission(Permissions.ExercisesManage)
            .Produces<ExerciseDto>(StatusCodes.Status201Created)
            .WithName("DuplicateExercise");
    }

    // Catálogo base: solo el equipo de plataforma lo crea y edita; los gimnasios lo leen y lo duplican.
    private static void MapPlatformExercises(RouteGroupBuilder group)
    {
        group.MapGet("/", async (IDispatcher dispatcher, CancellationToken ct, string? search = null, MuscleGroup? muscle = null, Equipment? equipment = null, Difficulty? difficulty = null, ExerciseType? type = null, bool? isActive = null, int page = 1, int pageSize = 25) =>
                (await dispatcher.QueryAsync(new ListExercisesQuery(new PageRequest(page, pageSize), search, muscle, equipment, difficulty, type, "global", isActive), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.PlatformExercisesManage)
            .Produces<PagedResult<ExerciseDto>>()
            .WithName("ListGlobalExercises");

        group.MapPost("/", async (ExerciseRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new CreateExerciseCommand(request, Global: true), ct)).ToHttp(e => Results.Created($"/api/v1/platform/exercises/{e.Id}", e)))
            .RequirePermission(Permissions.PlatformExercisesManage)
            .WithValidation<ExerciseRequest>()
            .Produces<ExerciseDto>(StatusCodes.Status201Created)
            .WithName("CreateGlobalExercise");

        group.MapPut("/{id:guid}", async (Guid id, ExerciseRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new UpdateExerciseCommand(id, request, Global: true), ct)).ToHttp(e => Results.Ok(e)))
            .RequirePermission(Permissions.PlatformExercisesManage)
            .WithValidation<ExerciseRequest>()
            .Produces<ExerciseDto>()
            .WithName("UpdateGlobalExercise");

        group.MapPatch("/{id:guid}/status", async (Guid id, SetExerciseStatusRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new SetExerciseStatusCommand(id, request, Global: true), ct)).ToHttp(e => Results.Ok(e)))
            .RequirePermission(Permissions.PlatformExercisesManage)
            .Produces<ExerciseDto>()
            .WithName("SetGlobalExerciseStatus");
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
