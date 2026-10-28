using FitCore.Application.Core;
using FitCore.Classes.Application.Reservations;
using FitCore.Classes.Application.Sessions;
using FitCore.Classes.Domain;
using FitCore.Classes.Persistence;
using FitCore.Platform.Domain;
using FitCore.Platform.Security;
using FitCore.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace FitCore.Classes.Application.Attendance;

internal static class AttendanceAccess
{
    /// <summary>
    /// ¿Puede el usuario registrar asistencia de esta sesión? El personal de recepción y quien programa el horario, en las sucursales a las
    /// que tienen acceso; un entrenador, solo en las sesiones donde él es el instructor.
    /// </summary>
    public static async Task<bool> CanOperateAsync(ClassesDbContext db, UserAccess actor, ClassSession session, CancellationToken cancellationToken) =>
        actor.Has(Permissions.ClassReservationsManage)
        || actor.Has(Permissions.ClassSessionsManage)
        || await db.TrainerRefs.AsNoTracking().AnyAsync(t => t.Id == session.InstructorId && t.UserId == actor.UserId, cancellationToken);
}

internal static class RosterBuilder
{
    public static async Task<RosterDto> BuildAsync(ClassesDbContext db, ClassSession session, CancellationToken cancellationToken)
    {
        var reservations = await db.Reservations.AsNoTracking()
            .Where(r => r.SessionId == session.Id
                        && (r.Status == ReservationStatus.Confirmed || r.Status == ReservationStatus.Offered || r.Status == ReservationStatus.Waitlisted))
            .ToListAsync(cancellationToken);
        var memberIds = reservations.Select(r => r.MemberId).Distinct().ToList();
        var members = await db.MemberRefs.AsNoTracking().Where(m => memberIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, cancellationToken);
        var attendance = await db.ClassAttendances.AsNoTracking().Where(a => a.SessionId == session.Id).ToDictionaryAsync(a => a.ReservationId, cancellationToken);
        var positions = await Waitlist.PositionsAsync(db, [session.Id], cancellationToken);
        var className = (await SessionScheduling.ClassNamesAsync(db, [session.ClassId], cancellationToken)).GetValueOrDefault(session.ClassId).Name ?? string.Empty;

        RosterEntryDto Entry(ClassReservation r)
        {
            members.TryGetValue(r.MemberId, out var member);
            attendance.TryGetValue(r.Id, out var taken);
            return new RosterEntryDto(
                r.Id,
                r.MemberId,
                member?.FullName ?? string.Empty,
                member?.Code ?? string.Empty,
                r.Status.ToString(),
                positions.TryGetValue(r.Id, out var position) ? position : null,
                taken?.Outcome.ToString(),
                taken?.Notes,
                taken is null ? null : taken.UpdatedAt ?? taken.CreatedAt,
                taken?.GymEntryId is not null,
                r.CreatedAt,
                r.OfferExpiresAt);
        }

        var participants = reservations.Where(r => r.Status == ReservationStatus.Confirmed).Select(Entry).OrderBy(e => e.MemberName).ThenBy(e => e.ReservationId).ToList();
        var attended = participants.Count(p => p.Outcome == nameof(AttendanceOutcome.Attended));
        var noShow = participants.Count(p => p.Outcome == nameof(AttendanceOutcome.NoShow));
        return new RosterDto(
            session.Id,
            session.ClassId,
            className,
            session.StartsAt,
            session.EndsAt,
            session.Status.ToString(),
            session.Capacity,
            participants.Count,
            attended,
            noShow,
            participants.Count - attended - noShow,
            participants,
            [.. reservations.Where(r => r.Status == ReservationStatus.Offered).OrderBy(r => r.OfferedAt).Select(Entry)],
            [.. reservations.Where(r => r.Status == ReservationStatus.Waitlisted).OrderBy(r => r.CreatedAt).ThenBy(r => r.Id).Select(Entry)]);
    }
}

internal sealed class GetRosterHandler(ClassesDbContext db, IAccessContext access) : IQueryHandler<GetRosterQuery, RosterDto>
{
    public async Task<Result<RosterDto>> HandleAsync(GetRosterQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var session = await db.ClassSessions.AsNoTracking().VisibleTo(actor.Value).FirstOrDefaultAsync(s => s.Id == query.SessionId, cancellationToken);
        return session is null ? SessionErrors.NotFound : await RosterBuilder.BuildAsync(db, session, cancellationToken);
    }
}

internal sealed class RecordAttendanceHandler(ClassesDbContext db, IAccessContext access, IClock clock)
    : ICommandHandler<RecordAttendanceCommand, RosterDto>
{
    public async Task<Result<RosterDto>> HandleAsync(RecordAttendanceCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var visible = await db.ClassSessions.AsNoTracking().VisibleTo(actor.Value).FirstOrDefaultAsync(s => s.Id == command.SessionId, cancellationToken);
        if (visible is null || !await AttendanceAccess.CanOperateAsync(db, actor.Value, visible, cancellationToken))
        {
            return SessionErrors.NotFound;
        }

        var entries = command.Request.Entries;
        var reservationIds = entries.Select(e => e.ReservationId).ToList();
        if (reservationIds.Distinct().Count() != reservationIds.Count)
        {
            return ClassAttendanceErrors.DuplicateEntries;
        }

        // Con la sesión bloqueada: reservar, cancelar y registrar asistencia no se cruzan.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await SessionScheduling.LockSessionsAsync(db, [visible.Id], cancellationToken);
        var session = await db.ClassSessions.AsNoTracking().FirstAsync(s => s.Id == visible.Id, cancellationToken);
        var now = clock.UtcNow;
        if (session.Status == SessionStatus.Cancelled)
        {
            return ClassAttendanceErrors.SessionCancelled;
        }

        if (session.StartsAt > now)
        {
            return ClassAttendanceErrors.SessionNotStarted;
        }

        var reservations = await db.Reservations.AsNoTracking().Where(r => r.SessionId == session.Id && reservationIds.Contains(r.Id)).ToListAsync(cancellationToken);
        if (reservations.Count != reservationIds.Count)
        {
            return ClassAttendanceErrors.ReservationNotInSession;
        }

        if (reservations.Any(r => r.Status != ReservationStatus.Confirmed))
        {
            return ClassAttendanceErrors.ReservationNotAttendable;
        }

        var existing = await db.ClassAttendances.Where(a => reservationIds.Contains(a.ReservationId)).ToDictionaryAsync(a => a.ReservationId, cancellationToken);
        var gymEntries = await GymEntriesAsync(db, session, [.. reservations.Select(r => r.MemberId).Distinct()], cancellationToken);
        foreach (var entry in entries)
        {
            var reservation = reservations.First(r => r.Id == entry.ReservationId);
            Guid? gymEntry = gymEntries.TryGetValue(reservation.MemberId, out var entryId) ? entryId : null;
            if (existing.TryGetValue(entry.ReservationId, out var attendance))
            {
                attendance.Correct(entry.Outcome, entry.Notes, gymEntry, actor.Value.UserId, now);
            }
            else
            {
                db.ClassAttendances.Add(ClassAttendance.Record(
                    actor.Value.TenantId, session.BranchId, session.Id, reservation.Id, reservation.MemberId, entry.Outcome, entry.Notes, gymEntry, actor.Value.UserId, now));
            }
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException pg && AttendanceDatabaseError(pg.SqlState) is not null)
        {
            return AttendanceDatabaseError(((Npgsql.PostgresException)ex.InnerException).SqlState)!;
        }

        return await RosterBuilder.BuildAsync(db, session, cancellationToken);
    }

    /// <summary>Lo que la base rechaza cuando la aplicación no llegó a verlo (la reserva cambió o la sesión no empezó).</summary>
    private static Error? AttendanceDatabaseError(string sqlState) => sqlState switch
    {
        "FC006" => ClassAttendanceErrors.ReservationNotAttendable,
        "FC008" => ClassAttendanceErrors.SessionNotStarted,
        Npgsql.PostgresErrorCodes.UniqueViolation => ReservationErrors.Changed,
        _ => null
    };

    /// <summary>Por alumno, la entrada al gimnasio (misma sucursal) que coincide con la sesión: la primera que cruza su horario.</summary>
    public static async Task<Dictionary<Guid, Guid>> GymEntriesAsync(ClassesDbContext db, ClassSession session, IReadOnlyCollection<Guid> memberIds, CancellationToken cancellationToken)
    {
        var entries = await db.GymEntryRefs.AsNoTracking()
            .Where(e => memberIds.Contains(e.MemberId) && e.BranchId == session.BranchId && e.CheckInAt <= session.EndsAt && (e.CheckOutAt == null || e.CheckOutAt >= session.StartsAt))
            .Select(e => new { e.Id, e.MemberId, e.CheckInAt })
            .ToListAsync(cancellationToken);
        return entries.GroupBy(e => e.MemberId).ToDictionary(g => g.Key, g => g.OrderBy(e => e.CheckInAt).First().Id);
    }
}

internal sealed class CompleteSessionHandler(ClassesDbContext db, IAccessContext access, IClock clock)
    : ICommandHandler<CompleteSessionCommand, RosterDto>
{
    public async Task<Result<RosterDto>> HandleAsync(CompleteSessionCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var visible = await db.ClassSessions.AsNoTracking().VisibleTo(actor.Value).FirstOrDefaultAsync(s => s.Id == command.SessionId, cancellationToken);
        if (visible is null || !await AttendanceAccess.CanOperateAsync(db, actor.Value, visible, cancellationToken))
        {
            return SessionErrors.NotFound;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await SessionScheduling.LockSessionsAsync(db, [visible.Id], cancellationToken);
        var session = await db.ClassSessions.FirstAsync(s => s.Id == visible.Id, cancellationToken);

        // Completar dos veces devuelve lo mismo.
        if (session.Status == SessionStatus.Completed)
        {
            return await RosterBuilder.BuildAsync(db, session, cancellationToken);
        }

        var now = clock.UtcNow;
        if (session.Status == SessionStatus.Cancelled)
        {
            return ClassAttendanceErrors.SessionCancelled;
        }

        if (session.StartsAt > now)
        {
            return ClassAttendanceErrors.SessionNotStarted;
        }

        // La fila de espera y las invitaciones pendientes ya no tienen a qué entrar.
        var open = await db.Reservations
            .Where(r => r.SessionId == session.Id && (r.Status == ReservationStatus.Waitlisted || r.Status == ReservationStatus.Offered))
            .ToListAsync(cancellationToken);
        foreach (var reservation in open)
        {
            reservation.Expire(actor.Value.UserId, now);
        }

        if (command.Request.MarkPendingAsNoShow)
        {
            var recorded = await db.ClassAttendances.AsNoTracking().Where(a => a.SessionId == session.Id).Select(a => a.ReservationId).ToListAsync(cancellationToken);
            var pending = await db.Reservations.AsNoTracking()
                .Where(r => r.SessionId == session.Id && r.Status == ReservationStatus.Confirmed && !recorded.Contains(r.Id))
                .ToListAsync(cancellationToken);
            foreach (var reservation in pending)
            {
                db.ClassAttendances.Add(ClassAttendance.Record(
                    actor.Value.TenantId, session.BranchId, session.Id, reservation.Id, reservation.MemberId, AttendanceOutcome.NoShow, null, null, actor.Value.UserId, now));
            }
        }

        session.Complete(actor.Value.UserId, now);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return SessionErrors.Changed;
        }

        return await RosterBuilder.BuildAsync(db, session, cancellationToken);
    }
}

internal static class AttendanceMapper
{
    public static async Task<IReadOnlyList<ClassAttendanceDto>> ToDtosAsync(ClassesDbContext db, IReadOnlyList<ClassAttendance> attendances, CancellationToken cancellationToken)
    {
        var sessionIds = attendances.Select(a => a.SessionId).Distinct().ToList();
        var memberIds = attendances.Select(a => a.MemberId).Distinct().ToList();
        var userIds = attendances.Select(a => a.UpdatedBy ?? a.CreatedBy).Where(id => id is not null).Select(id => id!.Value).Distinct().ToList();
        var sessions = await db.ClassSessions.AsNoTracking().Where(s => sessionIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, cancellationToken);
        var members = await db.MemberRefs.AsNoTracking().Where(m => memberIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, cancellationToken);
        var users = await db.UserRefs.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, cancellationToken);
        var classes = await SessionScheduling.ClassNamesAsync(db, [.. sessions.Values.Select(s => s.ClassId).Distinct()], cancellationToken);
        var instructors = await SessionScheduling.InstructorNamesAsync(db, [.. sessions.Values.Select(s => s.InstructorId).Distinct()], cancellationToken);
        var branches = await SessionScheduling.BranchNamesAsync(db, [.. sessions.Values.Select(s => s.BranchId).Distinct()], cancellationToken);

        return
        [
            .. attendances.Select(a =>
            {
                sessions.TryGetValue(a.SessionId, out var session);
                members.TryGetValue(a.MemberId, out var member);
                var className = session is not null && classes.TryGetValue(session.ClassId, out var c) ? c : (Name: string.Empty, Activity: string.Empty);
                var recorder = (a.UpdatedBy ?? a.CreatedBy) is { } id && users.TryGetValue(id, out var name) ? name : string.Empty;
                return new ClassAttendanceDto(
                    a.Id,
                    a.ReservationId,
                    a.SessionId,
                    session?.ClassId ?? Guid.Empty,
                    className.Name,
                    className.Activity,
                    a.BranchId,
                    branches.GetValueOrDefault(a.BranchId, string.Empty),
                    session is null ? string.Empty : instructors.GetValueOrDefault(session.InstructorId, string.Empty),
                    session?.StartsAt ?? default,
                    session?.EndsAt ?? default,
                    a.MemberId,
                    member?.FullName ?? string.Empty,
                    member?.Code ?? string.Empty,
                    a.Outcome.ToString(),
                    a.Notes,
                    a.GymEntryId is not null,
                    a.CreatedAt,
                    recorder,
                    a.UpdatedAt);
            })
        ];
    }
}

internal sealed class ListClassAttendanceHandler(ClassesDbContext db, IAccessContext access)
    : IQueryHandler<ListClassAttendanceQuery, PagedResult<ClassAttendanceDto>>
{
    public async Task<Result<PagedResult<ClassAttendanceDto>>> HandleAsync(ListClassAttendanceQuery query, CancellationToken cancellationToken)
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

        var items = db.ClassAttendances.AsNoTracking().AsQueryable();
        if (!actor.Value.AllBranches)
        {
            var branchIds = actor.Value.BranchIds.ToList();
            items = items.Where(a => branchIds.Contains(a.BranchId));
        }

        if (query.BranchId is { } branchId)
        {
            items = items.Where(a => a.BranchId == branchId);
        }

        if (query.SessionId is { } sessionId)
        {
            items = items.Where(a => a.SessionId == sessionId);
        }

        if (query.MemberId is { } memberId)
        {
            items = items.Where(a => a.MemberId == memberId);
        }

        if (query.Outcome is { } outcome)
        {
            items = items.Where(a => a.Outcome == outcome);
        }

        if (query.From is { } from)
        {
            var fromUtc = from.ToUniversalTime();
            items = items.Where(a => db.ClassSessions.Any(s => s.Id == a.SessionId && s.StartsAt >= fromUtc));
        }

        if (query.To is { } to)
        {
            var toUtc = to.ToUniversalTime();
            items = items.Where(a => db.ClassSessions.Any(s => s.Id == a.SessionId && s.StartsAt < toUtc));
        }

        var paged = await items
            .OrderByDescending(a => db.ClassSessions.Where(s => s.Id == a.SessionId).Select(s => s.StartsAt).FirstOrDefault())
            .ThenBy(a => a.Id)
            .ToPagedAsync(query.Page, cancellationToken);
        return new PagedResult<ClassAttendanceDto>([.. await AttendanceMapper.ToDtosAsync(db, paged.Items, cancellationToken)], paged.Page, paged.PageSize, paged.TotalCount);
    }
}

internal sealed class ListMyClassAttendanceHandler(ClassesDbContext db, IAccessContext access)
    : IQueryHandler<ListMyClassAttendanceQuery, PagedResult<ClassAttendanceDto>>
{
    public async Task<Result<PagedResult<ClassAttendanceDto>>> HandleAsync(ListMyClassAttendanceQuery query, CancellationToken cancellationToken)
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

        var items = db.ClassAttendances.AsNoTracking().Where(a => a.MemberId == member.Value.Id);
        if (query.Outcome is { } outcome)
        {
            items = items.Where(a => a.Outcome == outcome);
        }

        var paged = await items
            .OrderByDescending(a => db.ClassSessions.Where(s => s.Id == a.SessionId).Select(s => s.StartsAt).FirstOrDefault())
            .ThenBy(a => a.Id)
            .ToPagedAsync(query.Page, cancellationToken);
        return new PagedResult<ClassAttendanceDto>([.. await AttendanceMapper.ToDtosAsync(db, paged.Items, cancellationToken)], paged.Page, paged.PageSize, paged.TotalCount);
    }
}
