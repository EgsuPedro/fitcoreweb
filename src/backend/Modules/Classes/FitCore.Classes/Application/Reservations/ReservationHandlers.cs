using FitCore.Application.Core;
using FitCore.Classes.Application.Sessions;
using FitCore.Classes.Domain;
using FitCore.Classes.Persistence;
using FitCore.Platform.Security;
using FitCore.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace FitCore.Classes.Application.Reservations;

internal static class MyMember
{
    /// <summary>El alumno de la sesión del usuario (siempre desde su sesión, nunca desde un identificador del cliente).</summary>
    public static async Task<Result<MemberRef>> ResolveAsync(ClassesDbContext db, UserAccess actor, CancellationToken cancellationToken)
    {
        var member = await db.MemberRefs.AsNoTracking().FirstOrDefaultAsync(m => m.UserId == actor.UserId, cancellationToken);
        return member is null ? ReservationErrors.MemberNotFound : member;
    }
}

internal sealed class ListReservationsHandler(ClassesDbContext db, IAccessContext access)
    : IQueryHandler<ListReservationsQuery, PagedResult<ReservationDto>>
{
    public async Task<Result<PagedResult<ReservationDto>>> HandleAsync(ListReservationsQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        if (query.BranchId is { } requested && !actor.Value.CanAccessBranch(requested))
        {
            return FitCore.Platform.Application.PlatformErrors.BranchNotAllowed;
        }

        var reservations = db.Reservations.AsNoTracking().VisibleTo(actor.Value);
        if (query.BranchId is { } branchId)
        {
            reservations = reservations.Where(r => r.BranchId == branchId);
        }

        if (query.SessionId is { } sessionId)
        {
            reservations = reservations.Where(r => r.SessionId == sessionId);
        }

        if (query.MemberId is { } memberId)
        {
            reservations = reservations.Where(r => r.MemberId == memberId);
        }

        if (query.Status is { } status)
        {
            reservations = reservations.Where(r => r.Status == status);
        }

        if (query.From is { } from)
        {
            var fromUtc = from.ToUniversalTime();
            reservations = reservations.Where(r => db.ClassSessions.Any(s => s.Id == r.SessionId && s.StartsAt >= fromUtc));
        }

        if (query.To is { } to)
        {
            var toUtc = to.ToUniversalTime();
            reservations = reservations.Where(r => db.ClassSessions.Any(s => s.Id == r.SessionId && s.StartsAt < toUtc));
        }

        // Dentro de una sesión, por orden de llegada (lo que luego fija el orden de la lista de espera).
        var paged = await reservations.OrderBy(r => r.CreatedAt).ThenBy(r => r.Id).ToPagedAsync(query.Page, cancellationToken);
        return new PagedResult<ReservationDto>([.. await ReservationMapper.ToDtosAsync(db, paged.Items, cancellationToken)], paged.Page, paged.PageSize, paged.TotalCount);
    }
}

internal sealed class GetReservationHandler(ClassesDbContext db, IAccessContext access) : IQueryHandler<GetReservationQuery, ReservationDto>
{
    public async Task<Result<ReservationDto>> HandleAsync(GetReservationQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var reservation = await db.Reservations.AsNoTracking().VisibleTo(actor.Value).FirstOrDefaultAsync(r => r.Id == query.Id, cancellationToken);
        return reservation is null ? ReservationErrors.NotFound : (await ReservationMapper.ToDtosAsync(db, [reservation], cancellationToken))[0];
    }
}

internal sealed class CreateReservationHandler(ClassesDbContext db, IAccessContext access, IClock clock)
    : ICommandHandler<CreateReservationCommand, ReservationDto>
{
    public async Task<Result<ReservationDto>> HandleAsync(CreateReservationCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        // El personal solo reserva en sesiones de las sucursales que administra; el tenant sale siempre de su sesión.
        var session = await db.ClassSessions.AsNoTracking().VisibleTo(actor.Value).FirstOrDefaultAsync(s => s.Id == command.Request.SessionId, cancellationToken);
        if (session is null)
        {
            return SessionErrors.NotFound;
        }

        var member = await db.MemberRefs.AsNoTracking().FirstOrDefaultAsync(m => m.Id == command.Request.MemberId, cancellationToken);
        if (member is null)
        {
            return ReservationErrors.MemberNotFound;
        }

        return await Booking.BookAsync(db, clock, actor.Value, member, session, ReservationSource.Staff, command.IdempotencyKey, command.Request, cancellationToken);
    }
}

internal sealed class CancelReservationHandler(ClassesDbContext db, IAccessContext access, IClock clock)
    : ICommandHandler<CancelReservationCommand, ReservationDto>
{
    public async Task<Result<ReservationDto>> HandleAsync(CancelReservationCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var reservation = await db.Reservations.VisibleTo(actor.Value).FirstOrDefaultAsync(r => r.Id == command.Id, cancellationToken);
        if (reservation is null)
        {
            return ReservationErrors.NotFound;
        }

        return await Cancelling.CancelAsync(db, clock, actor.Value.UserId, reservation, isSelf: false, command.Request.Reason, cancellationToken);
    }
}

internal sealed class ListMyReservationsHandler(ClassesDbContext db, IAccessContext access, IClock clock)
    : IQueryHandler<ListMyReservationsQuery, PagedResult<ReservationDto>>
{
    public async Task<Result<PagedResult<ReservationDto>>> HandleAsync(ListMyReservationsQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var member = await MyMember.ResolveAsync(db, actor.Value, cancellationToken);
        if (member.IsFailure)
        {
            return member.Error;
        }

        var reservations = db.Reservations.AsNoTracking().Where(r => r.MemberId == member.Value.Id);
        if (query.Status is { } status)
        {
            reservations = reservations.Where(r => r.Status == status);
        }

        if (query.UpcomingOnly)
        {
            var now = clock.UtcNow;
            reservations = reservations.Where(r => db.ClassSessions.Any(s => s.Id == r.SessionId && s.EndsAt > now));
        }

        var paged = await reservations.OrderBy(r => db.ClassSessions.Where(s => s.Id == r.SessionId).Select(s => s.StartsAt).FirstOrDefault()).ThenBy(r => r.Id)
            .ToPagedAsync(query.Page, cancellationToken);
        return new PagedResult<ReservationDto>([.. await ReservationMapper.ToDtosAsync(db, paged.Items, cancellationToken)], paged.Page, paged.PageSize, paged.TotalCount);
    }
}

internal sealed class CreateMyReservationHandler(ClassesDbContext db, IAccessContext access, IClock clock)
    : ICommandHandler<CreateMyReservationCommand, ReservationDto>
{
    public async Task<Result<ReservationDto>> HandleAsync(CreateMyReservationCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var member = await MyMember.ResolveAsync(db, actor.Value, cancellationToken);
        if (member.IsFailure)
        {
            return member.Error;
        }

        // El alumno ve las sesiones de todo su gimnasio (puede asistir a otra sucursal si su plan lo permite: lo decide la elegibilidad).
        var session = await db.ClassSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == command.Request.SessionId, cancellationToken);
        if (session is null)
        {
            return SessionErrors.NotFound;
        }

        return await Booking.BookAsync(db, clock, actor.Value, member.Value, session, ReservationSource.Member, command.IdempotencyKey, command.Request, cancellationToken);
    }
}

internal sealed class CancelMyReservationHandler(ClassesDbContext db, IAccessContext access, IClock clock)
    : ICommandHandler<CancelMyReservationCommand, ReservationDto>
{
    public async Task<Result<ReservationDto>> HandleAsync(CancelMyReservationCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var member = await MyMember.ResolveAsync(db, actor.Value, cancellationToken);
        if (member.IsFailure)
        {
            return member.Error;
        }

        // La reserva de otro alumno responde como si no existiera.
        var reservation = await db.Reservations.FirstOrDefaultAsync(r => r.Id == command.Id && r.MemberId == member.Value.Id, cancellationToken);
        if (reservation is null)
        {
            return ReservationErrors.NotFound;
        }

        return await Cancelling.CancelAsync(db, clock, actor.Value.UserId, reservation, isSelf: true, command.Request.Reason, cancellationToken);
    }
}
