using System.Text.Json;
using FitCore.Application.Core;
using FitCore.Classes.Application.Sessions;
using FitCore.Classes.Domain;
using FitCore.Classes.Persistence;
using FitCore.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FitCore.Classes.Application.Notifications;

/// <summary>Lo que cuenta una sesión para redactar un aviso: su clase, su sucursal y la zona horaria con que se muestra la hora.</summary>
internal sealed record SessionFacts(ClassSession Session, string ClassName, string BranchName, TimeZoneInfo Zone)
{
    public string When() => NotificationText.When(Session.StartsAt, Zone);

    public static async Task<SessionFacts?> LoadAsync(ClassesDbContext db, Guid sessionId, CancellationToken cancellationToken)
    {
        var session = await db.ClassSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
        if (session is null)
        {
            return null;
        }

        var className = (await SessionScheduling.ClassNamesAsync(db, [session.ClassId], cancellationToken)).GetValueOrDefault(session.ClassId).Name ?? string.Empty;
        var branchName = (await SessionScheduling.BranchNamesAsync(db, [session.BranchId], cancellationToken)).GetValueOrDefault(session.BranchId, string.Empty);
        return new SessionFacts(session, className, branchName, await SessionScheduling.ZoneAsync(db, session.BranchId, cancellationToken));
    }
}

/// <summary>
/// Base de los manejadores de eventos de clases. El evento llega de la outbox (se guardó en la misma transacción que el cambio) al menos una
/// vez, así que cada manejador debe ser idempotente: el aviso se identifica por una clave del hecho y la base descarta el duplicado. Cada
/// manejador además comprueba que el hecho siga vigente (no se avisa de una invitación que ya se aceptó, ni de una reserva ya cancelada).
/// </summary>
internal abstract class ClassEventHandler<TEvent>(IServiceScopeFactory scopes, IClock clock) : IOutboxEventHandler
    where TEvent : class
{
    public string EventType => typeof(TEvent).FullName!;

    public async Task HandleAsync(Guid? tenantId, string payload, CancellationToken cancellationToken)
    {
        if (tenantId is not { } tenant)
        {
            return;
        }

        var domainEvent = JsonSerializer.Deserialize<TEvent>(payload) ?? throw new InvalidOperationException($"Evento {EventType} vacío.");
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenant);
        var db = scope.ServiceProvider.GetRequiredService<ClassesDbContext>();
        await HandleAsync(db, tenant, domainEvent, clock.UtcNow, cancellationToken);
    }

    protected abstract Task HandleAsync(ClassesDbContext db, Guid tenantId, TEvent domainEvent, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>El usuario con que el alumno entra al sistema; sin él no tiene bandeja y no hay a quién avisar.</summary>
    protected static async Task<Guid?> MemberUserAsync(ClassesDbContext db, Guid memberId, CancellationToken cancellationToken) =>
        await db.MemberRefs.AsNoTracking().Where(m => m.Id == memberId).Select(m => m.UserId).FirstOrDefaultAsync(cancellationToken);

    protected static async Task<Guid?> InstructorUserAsync(ClassesDbContext db, Guid trainerId, CancellationToken cancellationToken) =>
        await db.TrainerRefs.AsNoTracking().Where(t => t.Id == trainerId).Select(t => (Guid?)t.UserId).FirstOrDefaultAsync(cancellationToken);

    protected static Task NotifyAsync(
        ClassesDbContext db,
        Guid tenantId,
        Guid userId,
        SessionFacts facts,
        NotificationType type,
        string title,
        string body,
        string dedupeKey,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        Guid? reservationId = null) =>
        NotificationWriter.TryCreateAsync(
            db,
            new NewNotification(tenantId, userId, facts.Session.BranchId, type, title, body, new { sessionId = facts.Session.Id, reservationId }, dedupeKey),
            now,
            cancellationToken);
}

internal sealed class ReservationConfirmedHandler(IServiceScopeFactory scopes, IClock clock) : ClassEventHandler<ReservationConfirmed>(scopes, clock)
{
    protected override async Task HandleAsync(ClassesDbContext db, Guid tenantId, ReservationConfirmed e, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var reservation = await db.Reservations.AsNoTracking().FirstOrDefaultAsync(r => r.Id == e.ReservationId, cancellationToken);
        if (reservation is not { Status: ReservationStatus.Confirmed } || await MemberUserAsync(db, e.MemberId, cancellationToken) is not { } userId)
        {
            return;
        }

        if (await SessionFacts.LoadAsync(db, e.SessionId, cancellationToken) is not { Session.IsScheduled: true } facts)
        {
            return;
        }

        await NotifyAsync(
            db, tenantId, userId, facts, NotificationType.ReservationConfirmed,
            "Reserva confirmada",
            $"Tienes cupo en {facts.ClassName}: {facts.When()}, {facts.BranchName}.",
            $"reservation-confirmed:{e.ReservationId}", now, cancellationToken, e.ReservationId);
    }
}

internal sealed class WaitlistJoinedHandler(IServiceScopeFactory scopes, IClock clock) : ClassEventHandler<WaitlistJoined>(scopes, clock)
{
    protected override async Task HandleAsync(ClassesDbContext db, Guid tenantId, WaitlistJoined e, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Si ya se le ofreció el cupo, este aviso llegaría tarde y confundiría: la invitación ya lo dice todo.
        var reservation = await db.Reservations.AsNoTracking().FirstOrDefaultAsync(r => r.Id == e.ReservationId, cancellationToken);
        if (reservation is not { Status: ReservationStatus.Waitlisted } || await MemberUserAsync(db, e.MemberId, cancellationToken) is not { } userId)
        {
            return;
        }

        if (await SessionFacts.LoadAsync(db, e.SessionId, cancellationToken) is not { Session.IsScheduled: true } facts)
        {
            return;
        }

        await NotifyAsync(
            db, tenantId, userId, facts, NotificationType.WaitlistJoined,
            "Estás en la lista de espera",
            $"{facts.ClassName} ({facts.When()}) está llena. Si se libera un cupo, te avisaremos y tendrás un plazo para aceptarlo.",
            $"waitlist-joined:{e.ReservationId}", now, cancellationToken, e.ReservationId);
    }
}

internal sealed class WaitlistOfferMadeHandler(IServiceScopeFactory scopes, IClock clock) : ClassEventHandler<WaitlistOfferMade>(scopes, clock)
{
    protected override async Task HandleAsync(ClassesDbContext db, Guid tenantId, WaitlistOfferMade e, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Solo vale mientras la invitación siga abierta: si ya la aceptó, la rechazó o venció, avisar sería un error.
        var reservation = await db.Reservations.AsNoTracking().FirstOrDefaultAsync(r => r.Id == e.ReservationId, cancellationToken);
        if (reservation is not { Status: ReservationStatus.Offered } || reservation.OfferExpiresAt <= now
            || await MemberUserAsync(db, e.MemberId, cancellationToken) is not { } userId)
        {
            return;
        }

        if (await SessionFacts.LoadAsync(db, e.SessionId, cancellationToken) is not { Session.IsScheduled: true } facts)
        {
            return;
        }

        await NotifyAsync(
            db, tenantId, userId, facts, NotificationType.WaitlistOffer,
            "Se liberó un cupo para ti",
            $"Hay un cupo en {facts.ClassName} ({facts.When()}). Acéptalo antes de las {NotificationText.Time(e.ExpiresAt, facts.Zone)} o pasará a la siguiente persona.",
            $"waitlist-offer:{e.ReservationId}", now, cancellationToken, e.ReservationId);
    }
}

internal sealed class WaitlistOfferExpiredHandler(IServiceScopeFactory scopes, IClock clock) : ClassEventHandler<WaitlistOfferExpired>(scopes, clock)
{
    protected override async Task HandleAsync(ClassesDbContext db, Guid tenantId, WaitlistOfferExpired e, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var reservation = await db.Reservations.AsNoTracking().FirstOrDefaultAsync(r => r.Id == e.ReservationId, cancellationToken);
        if (reservation is not { Status: ReservationStatus.Expired } || await MemberUserAsync(db, e.MemberId, cancellationToken) is not { } userId)
        {
            return;
        }

        if (await SessionFacts.LoadAsync(db, e.SessionId, cancellationToken) is not { } facts)
        {
            return;
        }

        await NotifyAsync(
            db, tenantId, userId, facts, NotificationType.WaitlistOfferExpired,
            "La invitación venció",
            $"El plazo para aceptar el cupo en {facts.ClassName} ({facts.When()}) terminó y pasó a la siguiente persona.",
            $"waitlist-offer-expired:{e.ReservationId}", now, cancellationToken, e.ReservationId);
    }
}

internal sealed class ReservationCancelledHandler(IServiceScopeFactory scopes, IClock clock) : ClassEventHandler<ReservationCancelled>(scopes, clock)
{
    protected override async Task HandleAsync(ClassesDbContext db, Guid tenantId, ReservationCancelled e, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (await MemberUserAsync(db, e.MemberId, cancellationToken) is not { } userId || await SessionFacts.LoadAsync(db, e.SessionId, cancellationToken) is not { } facts)
        {
            return;
        }

        if (e.BySession)
        {
            var reason = string.IsNullOrWhiteSpace(e.SessionReason) ? string.Empty : $" Motivo: {e.SessionReason.Trim()}";
            await NotifyAsync(
                db, tenantId, userId, facts, NotificationType.SessionCancelled,
                "Clase cancelada",
                $"{facts.ClassName} del {facts.When()} se canceló, y con ella tu reserva.{reason}",
                $"reservation-cancelled:{e.ReservationId}", now, cancellationToken, e.ReservationId);
            return;
        }

        // Lo que el propio alumno cancela (o rechaza) no se le avisa; lo que cancela el gimnasio, sí.
        if (e.CancelledBy is { } by && by != userId)
        {
            await NotifyAsync(
                db, tenantId, userId, facts, NotificationType.ReservationCancelledByGym,
                "Reserva cancelada",
                $"El gimnasio canceló tu reserva de {facts.ClassName} ({facts.When()}).",
                $"reservation-cancelled:{e.ReservationId}", now, cancellationToken, e.ReservationId);
        }
    }
}

internal sealed class SessionRescheduledHandler(IServiceScopeFactory scopes, IClock clock) : ClassEventHandler<SessionRescheduled>(scopes, clock)
{
    protected override async Task HandleAsync(ClassesDbContext db, Guid tenantId, SessionRescheduled e, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (await SessionFacts.LoadAsync(db, e.SessionId, cancellationToken) is not { Session.IsScheduled: true } facts)
        {
            return;
        }

        var key = $"session-rescheduled:{e.EventId}";
        var timeChanged = e.OldStartsAt != e.NewStartsAt;

        // Los inscritos (con cupo, con invitación o en espera) y los instructores involucrados.
        var memberIds = await db.Reservations.AsNoTracking()
            .Where(r => r.SessionId == e.SessionId
                        && (r.Status == ReservationStatus.Confirmed || r.Status == ReservationStatus.Offered || r.Status == ReservationStatus.Waitlisted))
            .Select(r => r.MemberId)
            .ToListAsync(cancellationToken);
        var memberUsers = await db.MemberRefs.AsNoTracking()
            .Where(m => memberIds.Contains(m.Id) && m.UserId != null)
            .Select(m => m.UserId!.Value)
            .ToListAsync(cancellationToken);
        var change = timeChanged
            ? $"antes {NotificationText.When(e.OldStartsAt, facts.Zone)}, ahora {facts.When()}"
            : $"sigue siendo {facts.When()}, con otro instructor";
        foreach (var userId in memberUsers)
        {
            await NotifyAsync(
                db, tenantId, userId, facts, NotificationType.SessionRescheduled,
                "Cambio de horario",
                $"{facts.ClassName} ({facts.BranchName}) cambió: {change}.",
                key, now, cancellationToken);
        }

        var newInstructor = await InstructorUserAsync(db, e.NewInstructorId, cancellationToken);
        if (newInstructor is { } current)
        {
            var body = e.OldInstructorId == e.NewInstructorId
                ? $"Cambió el horario de {facts.ClassName}: {change}."
                : $"Se te asignó {facts.ClassName}: {facts.When()}, {facts.BranchName}.";
            await NotifyAsync(db, tenantId, current, facts, NotificationType.SessionRescheduled, "Cambio en tu sesión", body, key, now, cancellationToken);
        }

        if (e.OldInstructorId != e.NewInstructorId && await InstructorUserAsync(db, e.OldInstructorId, cancellationToken) is { } previous)
        {
            await NotifyAsync(
                db, tenantId, previous, facts, NotificationType.SessionRescheduled,
                "Cambio en tu sesión",
                $"Ya no impartes {facts.ClassName} del {NotificationText.When(e.OldStartsAt, facts.Zone)}.",
                key, now, cancellationToken);
        }
    }
}

internal sealed class SessionCancelledHandler(IServiceScopeFactory scopes, IClock clock) : ClassEventHandler<SessionCancelled>(scopes, clock)
{
    protected override async Task HandleAsync(ClassesDbContext db, Guid tenantId, SessionCancelled e, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // A los inscritos los avisa su reserva cancelada; aquí, al instructor.
        if (await SessionFacts.LoadAsync(db, e.SessionId, cancellationToken) is not { } facts
            || await InstructorUserAsync(db, facts.Session.InstructorId, cancellationToken) is not { } userId)
        {
            return;
        }

        await NotifyAsync(
            db, tenantId, userId, facts, NotificationType.SessionCancelled,
            "Sesión cancelada",
            $"Se canceló {facts.ClassName} del {facts.When()} ({facts.BranchName}). Motivo: {e.Reason}",
            $"session-cancelled:{e.SessionId}", now, cancellationToken);
    }
}
