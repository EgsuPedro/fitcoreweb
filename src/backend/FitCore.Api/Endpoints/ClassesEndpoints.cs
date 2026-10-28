using FitCore.Api.Infrastructure;
using FitCore.Application.Core;
using FitCore.Classes.Application.Attendance;
using FitCore.Classes.Application.GroupClasses;
using FitCore.Classes.Application.Notifications;
using FitCore.Classes.Application.Reservations;
using FitCore.Classes.Application.Sessions;
using FitCore.Classes.Domain;
using FitCore.Platform.Domain;
using Microsoft.AspNetCore.Mvc;

namespace FitCore.Api.Endpoints;

internal static class ClassesEndpoints
{
    public static IEndpointRouteBuilder MapClassesEndpoints(this IEndpointRouteBuilder routes)
    {
        MapGroupClasses(routes.MapGroup("/group-classes").WithTags("Group classes"));
        MapSessions(routes.MapGroup("/class-sessions").WithTags("Class sessions"));
        MapReservations(routes.MapGroup("/class-reservations").WithTags("Class reservations"));
        MapMyReservations(routes.MapGroup("/me/class-reservations").WithTags("Member portal"));
        routes.MapGroup("/me/class-sessions").WithTags("Member portal")
            .MapGet("/", async (IDispatcher dispatcher, CancellationToken ct, DateTimeOffset? from = null, DateTimeOffset? to = null, Guid? classId = null, int page = 1, int pageSize = 25) =>
                    (await dispatcher.QueryAsync(new ListMyClassSessionsQuery(new PageRequest(page, pageSize), from, to, classId), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.MeRead)
            .Produces<PagedResult<MyClassSessionDto>>()
            .WithName("ListMyClassSessions");
        MapAttendance(routes);
        MapNotifications(routes.MapGroup("/me").WithTags("Notifications"));
        return routes;
    }

    // La bandeja es de cada usuario (alumno, instructor o personal) y siempre es la del usuario de la sesión; el permiso solo permite usarla.
    private static void MapNotifications(RouteGroupBuilder group)
    {
        group.MapGet("/notifications", async (IDispatcher dispatcher, CancellationToken ct, bool unreadOnly = false, NotificationType? type = null, int page = 1, int pageSize = 25) =>
                (await dispatcher.QueryAsync(new ListMyNotificationsQuery(new PageRequest(page, pageSize), unreadOnly, type), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.InboxUse)
            .Produces<PagedResult<InboxNotificationDto>>()
            .WithName("ListMyNotifications");

        group.MapGet("/notifications/summary", async (IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.QueryAsync(new GetNotificationSummaryQuery(), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.InboxUse)
            .Produces<NotificationSummaryDto>()
            .WithName("GetMyNotificationSummary");

        group.MapPatch("/notifications/{id:guid}/read", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new MarkNotificationReadCommand(id), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.InboxUse)
            .Produces<InboxNotificationDto>()
            .WithName("MarkMyNotificationRead");

        group.MapPost("/notifications/read-all", async (IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new MarkAllNotificationsReadCommand(), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.InboxUse)
            .Produces<MarkAllReadResultDto>()
            .WithName("MarkAllMyNotificationsRead");

        group.MapGet("/notification-preferences", async (IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.QueryAsync(new GetNotificationPreferencesQuery(), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.InboxUse)
            .Produces<IReadOnlyList<NotificationPreferenceDto>>()
            .WithName("GetMyNotificationPreferences");

        group.MapPut("/notification-preferences", async (UpdatePreferencesRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new UpdateNotificationPreferencesCommand(request), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.InboxUse)
            .WithValidation<UpdatePreferencesRequest>()
            .Produces<IReadOnlyList<NotificationPreferenceDto>>()
            .WithName("UpdateMyNotificationPreferences");
    }

    private static void MapAttendance(IEndpointRouteBuilder routes)
    {
        var sessions = routes.MapGroup("/class-sessions").WithTags("Class attendance");

        // Quién tiene cupo, quién tiene una invitación y quién espera, con la asistencia ya registrada.
        sessions.MapGet("/{id:guid}/roster", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.QueryAsync(new GetRosterQuery(id), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.ClassAttendanceRead)
            .Produces<RosterDto>()
            .WithName("GetClassRoster");

        // Registrar es idempotente: una asistencia por reserva; repetir lo mismo no duplica y enviar otro resultado lo corrige (queda auditado).
        sessions.MapPut("/{id:guid}/attendance", async (Guid id, RecordAttendanceRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new RecordAttendanceCommand(id, request), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.ClassAttendanceManage)
            .WithValidation<RecordAttendanceRequest>()
            .Produces<RosterDto>()
            .WithName("RecordClassAttendance");

        sessions.MapPost("/{id:guid}/complete", async (Guid id, CompleteSessionRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new CompleteSessionCommand(id, request), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.ClassAttendanceManage)
            .Produces<RosterDto>()
            .WithName("CompleteClassSession");

        routes.MapGroup("/class-attendance").WithTags("Class attendance")
            .MapGet("/", async (IDispatcher dispatcher, CancellationToken ct, Guid? sessionId = null, Guid? memberId = null, Guid? branchId = null, AttendanceOutcome? outcome = null, DateTimeOffset? from = null, DateTimeOffset? to = null, int page = 1, int pageSize = 25) =>
                (await dispatcher.QueryAsync(new ListClassAttendanceQuery(new PageRequest(page, pageSize), sessionId, memberId, branchId, outcome, from, to), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.ClassAttendanceRead)
            .Produces<PagedResult<ClassAttendanceDto>>()
            .WithName("ListClassAttendance");

        routes.MapGroup("/me/class-attendance").WithTags("Member portal")
            .MapGet("/", async (IDispatcher dispatcher, CancellationToken ct, AttendanceOutcome? outcome = null, int page = 1, int pageSize = 25) =>
                (await dispatcher.QueryAsync(new ListMyClassAttendanceQuery(new PageRequest(page, pageSize), outcome), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.MeRead)
            .Produces<PagedResult<ClassAttendanceDto>>()
            .WithName("ListMyClassAttendance");
    }

    private static void MapReservations(RouteGroupBuilder group)
    {
        group.MapGet("/", async (IDispatcher dispatcher, CancellationToken ct, Guid? sessionId = null, Guid? memberId = null, Guid? branchId = null, ReservationStatus? status = null, DateTimeOffset? from = null, DateTimeOffset? to = null, int page = 1, int pageSize = 25) =>
                (await dispatcher.QueryAsync(new ListReservationsQuery(new PageRequest(page, pageSize), sessionId, memberId, branchId, status, from, to), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.ClassReservationsRead)
            .Produces<PagedResult<ReservationDto>>()
            .WithName("ListClassReservations");

        group.MapGet("/{id:guid}", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.QueryAsync(new GetReservationQuery(id), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.ClassReservationsRead)
            .Produces<ReservationDto>()
            .WithName("GetClassReservation");

        // Reservar exige Idempotency-Key: reintentar la misma solicitud (corte de red, doble clic) no reserva dos veces.
        group.MapPost("/", async (CreateReservationRequest request, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new CreateReservationCommand(request, idempotencyKey), ct)).ToHttp(r => Results.Created($"/api/v1/class-reservations/{r.Id}", r)))
            .RequirePermission(Permissions.ClassReservationsManage)
            .WithValidation<CreateReservationRequest>()
            .Produces<ReservationDto>(StatusCodes.Status201Created)
            .WithName("CreateClassReservation");

        group.MapPost("/{id:guid}/cancel", async (Guid id, CancelReservationRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new CancelReservationCommand(id, request), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.ClassReservationsManage)
            .WithValidation<CancelReservationRequest>()
            .Produces<ReservationDto>()
            .WithName("CancelClassReservation");

        // Responder una invitación de la lista de espera: aceptar confirma el cupo reservado; rechazar lo pasa al siguiente.
        group.MapPost("/{id:guid}/accept", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new AcceptReservationCommand(id), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.ClassReservationsManage)
            .Produces<ReservationDto>()
            .WithName("AcceptClassReservationOffer");

        group.MapPost("/{id:guid}/decline", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new DeclineReservationCommand(id), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.ClassReservationsManage)
            .Produces<ReservationDto>()
            .WithName("DeclineClassReservationOffer");
    }

    private static void MapMyReservations(RouteGroupBuilder group)
    {
        group.MapGet("/", async (IDispatcher dispatcher, CancellationToken ct, ReservationStatus? status = null, bool upcomingOnly = false, int page = 1, int pageSize = 25) =>
                (await dispatcher.QueryAsync(new ListMyReservationsQuery(new PageRequest(page, pageSize), status, upcomingOnly), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.MeRead)
            .Produces<PagedResult<ReservationDto>>()
            .WithName("ListMyClassReservations");

        group.MapPost("/", async (CreateMyReservationRequest request, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new CreateMyReservationCommand(request, idempotencyKey), ct)).ToHttp(r => Results.Created($"/api/v1/me/class-reservations/{r.Id}", r)))
            .RequirePermission(Permissions.MeRead)
            .WithValidation<CreateMyReservationRequest>()
            .Produces<ReservationDto>(StatusCodes.Status201Created)
            .WithName("CreateMyClassReservation");

        group.MapPost("/{id:guid}/cancel", async (Guid id, CancelReservationRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new CancelMyReservationCommand(id, request), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.MeRead)
            .WithValidation<CancelReservationRequest>()
            .Produces<ReservationDto>()
            .WithName("CancelMyClassReservation");

        group.MapPost("/{id:guid}/accept", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new AcceptMyReservationCommand(id), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.MeRead)
            .Produces<ReservationDto>()
            .WithName("AcceptMyClassReservationOffer");

        group.MapPost("/{id:guid}/decline", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new DeclineMyReservationCommand(id), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.MeRead)
            .Produces<ReservationDto>()
            .WithName("DeclineMyClassReservationOffer");
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
