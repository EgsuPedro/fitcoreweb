using FitCore.Application.Core;
using FitCore.Classes.Application.GroupClasses;
using FitCore.Classes.Domain;
using FitCore.Classes.Persistence;
using FitCore.Platform.Application;
using FitCore.Platform.Security;
using FitCore.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace FitCore.Classes.Application.Sessions;

internal sealed class ListSessionsHandler(ClassesDbContext db, IAccessContext access)
    : IQueryHandler<ListSessionsQuery, PagedResult<ClassSessionDto>>
{
    public async Task<Result<PagedResult<ClassSessionDto>>> HandleAsync(ListSessionsQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        if (query.BranchId is { } requested && !actor.Value.CanAccessBranch(requested))
        {
            return PlatformErrors.BranchNotAllowed;
        }

        var sessions = db.ClassSessions.AsNoTracking().VisibleTo(actor.Value);
        if (query.BranchId is { } branchId)
        {
            sessions = sessions.Where(s => s.BranchId == branchId);
        }

        if (query.From is { } from)
        {
            var fromUtc = from.ToUniversalTime();
            sessions = sessions.Where(s => s.StartsAt >= fromUtc);
        }

        if (query.To is { } to)
        {
            var toUtc = to.ToUniversalTime();
            sessions = sessions.Where(s => s.StartsAt < toUtc);
        }

        if (query.ClassId is { } classId)
        {
            sessions = sessions.Where(s => s.ClassId == classId);
        }

        if (query.InstructorId is { } instructorId)
        {
            sessions = sessions.Where(s => s.InstructorId == instructorId);
        }

        if (query.SeriesId is { } seriesId)
        {
            sessions = sessions.Where(s => s.SeriesId == seriesId);
        }

        if (query.Status is { } status)
        {
            sessions = sessions.Where(s => s.Status == status);
        }

        var paged = await sessions.OrderBy(s => s.StartsAt).ThenBy(s => s.Id).ToPagedAsync(query.Page, cancellationToken);
        return new PagedResult<ClassSessionDto>([.. await SessionScheduling.ToDtosAsync(db, paged.Items, cancellationToken)], paged.Page, paged.PageSize, paged.TotalCount);
    }
}

internal sealed class GetSessionHandler(ClassesDbContext db, IAccessContext access) : IQueryHandler<GetSessionQuery, ClassSessionDto>
{
    public async Task<Result<ClassSessionDto>> HandleAsync(GetSessionQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var session = await db.ClassSessions.AsNoTracking().VisibleTo(actor.Value).FirstOrDefaultAsync(s => s.Id == query.Id, cancellationToken);
        return session is null ? SessionErrors.NotFound : await SessionScheduling.ToDtoAsync(db, session, cancellationToken);
    }
}

internal sealed class CreateSessionHandler(ClassesDbContext db, IAccessContext access, IClock clock, ICurrentUser currentUser)
    : ICommandHandler<CreateSessionCommand, ClassSessionDto>
{
    public async Task<Result<ClassSessionDto>> HandleAsync(CreateSessionCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var request = command.Request;
        var groupClass = await db.GroupClasses.AsNoTracking().VisibleTo(actor.Value).FirstOrDefaultAsync(c => c.Id == request.ClassId, cancellationToken);
        if (groupClass is null)
        {
            return GroupClassErrors.NotFound;
        }

        if (!groupClass.IsActive)
        {
            return SessionErrors.ClassInactive;
        }

        var now = clock.UtcNow;
        var startsAt = request.StartsAt.ToUniversalTime();
        if (startsAt <= now)
        {
            return SessionErrors.InPast;
        }

        var instructorId = request.InstructorId ?? groupClass.InstructorId;
        if (await GroupClassQueries.CheckInstructorAsync(db, instructorId, groupClass.BranchId, cancellationToken) is { } instructorError)
        {
            return instructorError;
        }

        var endsAt = startsAt.AddMinutes(groupClass.DurationMinutes);
        if (await SessionScheduling.OverlapsAsync(db, instructorId, startsAt, endsAt, Guid.Empty, cancellationToken))
        {
            return SessionErrors.InstructorOverlap;
        }

        // Fuera de la disponibilidad declarada solo se programa como excepción explícita, con su motivo (queda en la sesión y en la auditoría).
        string? overrideReason = null;
        var zone = await SessionScheduling.ZoneAsync(db, groupClass.BranchId, cancellationToken);
        var slots = await SessionScheduling.SlotsAsync(db, instructorId, cancellationToken);
        if (!InstructorAvailabilityRules.Fits(slots, groupClass.BranchId, startsAt, endsAt, zone))
        {
            if (string.IsNullOrWhiteSpace(request.AvailabilityOverrideReason))
            {
                return SessionErrors.InstructorUnavailable;
            }

            overrideReason = request.AvailabilityOverrideReason;
        }

        var session = ClassSession.Create(
            actor.Value.TenantId,
            groupClass.BranchId,
            groupClass.Id,
            null,
            instructorId,
            startsAt,
            endsAt,
            request.Capacity ?? groupClass.Capacity,
            request.Notes,
            overrideReason,
            currentUser.UserId,
            now);
        db.ClassSessions.Add(session);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (SessionScheduling.IsConcurrencyConflict(ex))
        {
            return SessionErrors.InstructorOverlap;
        }

        return await SessionScheduling.ToDtoAsync(db, session, cancellationToken);
    }
}

internal sealed class UpdateSessionHandler(ClassesDbContext db, IAccessContext access, IClock clock, ICurrentUser currentUser)
    : ICommandHandler<UpdateSessionCommand, ClassSessionDto>
{
    public async Task<Result<ClassSessionDto>> HandleAsync(UpdateSessionCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var session = await db.ClassSessions.VisibleTo(actor.Value).FirstOrDefaultAsync(s => s.Id == command.Id, cancellationToken);
        if (session is null)
        {
            return SessionErrors.NotFound;
        }

        var request = command.Request;
        var now = clock.UtcNow;
        if (!session.CanBeModified(now))
        {
            return SessionErrors.NotModifiable;
        }

        if (request.StartsAt is null && request.InstructorId is null && request.Capacity is null && request.Notes is null)
        {
            return SessionErrors.NothingToChange;
        }

        var startsAt = request.StartsAt?.ToUniversalTime() ?? session.StartsAt;
        var endsAt = startsAt + (session.EndsAt - session.StartsAt);
        var instructorId = request.InstructorId ?? session.InstructorId;
        var timeChanged = startsAt != session.StartsAt;
        var instructorChanged = instructorId != session.InstructorId;

        if (timeChanged && startsAt <= now)
        {
            return SessionErrors.InPast;
        }

        if (instructorChanged && await GroupClassQueries.CheckInstructorAsync(db, instructorId, session.BranchId, cancellationToken) is { } instructorError)
        {
            return instructorError;
        }

        var overrideReason = session.AvailabilityOverrideReason;
        if (timeChanged || instructorChanged)
        {
            if (await SessionScheduling.OverlapsAsync(db, instructorId, startsAt, endsAt, session.Id, cancellationToken))
            {
                return SessionErrors.InstructorOverlap;
            }

            var zone = await SessionScheduling.ZoneAsync(db, session.BranchId, cancellationToken);
            var slots = await SessionScheduling.SlotsAsync(db, instructorId, cancellationToken);
            if (InstructorAvailabilityRules.Fits(slots, session.BranchId, startsAt, endsAt, zone))
            {
                overrideReason = null;
            }
            else if (string.IsNullOrWhiteSpace(request.AvailabilityOverrideReason))
            {
                return SessionErrors.InstructorUnavailable;
            }
            else
            {
                overrideReason = request.AvailabilityOverrideReason;
            }
        }

        // Nadie baja la capacidad por debajo de los cupos ya ocupados (la base lo exige también, por si dos cambios se cruzan).
        if (request.Capacity is { } newCapacity && newCapacity < session.Capacity
            && (await SessionScheduling.ReservationCountsAsync(db, [session.Id], cancellationToken)).GetValueOrDefault(session.Id).Booked > newCapacity)
        {
            return SessionErrors.CapacityBelowBooked;
        }

        var notes = request.Notes is null ? session.Notes : request.Notes;
        var capacityBefore = session.Capacity;
        session.Reschedule(startsAt, endsAt, instructorId, request.Capacity ?? session.Capacity, notes, overrideReason, currentUser.UserId, now);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return SessionErrors.Changed;
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Reservations.Booking.CapacityBelowBooked })
        {
            return SessionErrors.CapacityBelowBooked;
        }
        catch (DbUpdateException ex) when (SessionScheduling.IsConcurrencyConflict(ex))
        {
            return SessionErrors.InstructorOverlap;
        }

        // Más capacidad = cupos libres: se ofrecen de inmediato a la lista de espera (si falla, el barrido del Worker lo completa).
        if (session.Capacity > capacityBefore)
        {
            await Reservations.Waitlist.PromoteSessionAsync(db, clock, session.Id, currentUser.UserId, cancellationToken);
        }

        return await SessionScheduling.ToDtoAsync(db, session, cancellationToken);
    }
}

internal sealed class CancelSessionHandler(ClassesDbContext db, IAccessContext access, IClock clock, ICurrentUser currentUser)
    : ICommandHandler<CancelSessionCommand, ClassSessionDto>
{
    public async Task<Result<ClassSessionDto>> HandleAsync(CancelSessionCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        if (!await db.ClassSessions.AsNoTracking().VisibleTo(actor.Value).AnyAsync(s => s.Id == command.Id, cancellationToken))
        {
            return SessionErrors.NotFound;
        }

        // Cancelar la sesión y sus reservas es una sola operación: se bloquea la sesión primero, así nadie reserva a mitad de camino.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await SessionScheduling.LockSessionsAsync(db, [command.Id], cancellationToken);
        var session = await db.ClassSessions.FirstAsync(s => s.Id == command.Id, cancellationToken);

        // Cancelar dos veces es idempotente: devuelve la sesión tal como quedó, sin tocar el motivo original.
        if (session.Status == SessionStatus.Cancelled)
        {
            return await SessionScheduling.ToDtoAsync(db, session, cancellationToken);
        }

        var now = clock.UtcNow;
        if (!session.IsScheduled || session.EndsAt <= now)
        {
            return SessionErrors.NotModifiable;
        }

        session.Cancel(command.Request.Reason, currentUser.UserId, now);
        await SessionScheduling.CancelLiveReservationsAsync(db, [session.Id], command.Request.Reason, currentUser.UserId, now, cancellationToken);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return SessionErrors.Changed;
        }

        return await SessionScheduling.ToDtoAsync(db, session, cancellationToken);
    }
}

internal sealed class ListSeriesHandler(ClassesDbContext db, IAccessContext access)
    : IQueryHandler<ListSeriesQuery, PagedResult<ClassSeriesDto>>
{
    public async Task<Result<PagedResult<ClassSeriesDto>>> HandleAsync(ListSeriesQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        if (query.BranchId is { } requested && !actor.Value.CanAccessBranch(requested))
        {
            return PlatformErrors.BranchNotAllowed;
        }

        var series = db.ClassSeries.AsNoTracking().VisibleTo(actor.Value);
        if (query.BranchId is { } branchId)
        {
            series = series.Where(s => s.BranchId == branchId);
        }

        if (query.ClassId is { } classId)
        {
            series = series.Where(s => s.ClassId == classId);
        }

        if (query.Status is { } status)
        {
            series = series.Where(s => s.Status == status);
        }

        var paged = await series.OrderByDescending(s => s.StartDate).ThenBy(s => s.Id).ToPagedAsync(query.Page, cancellationToken);
        return new PagedResult<ClassSeriesDto>([.. await SessionScheduling.ToSeriesDtosAsync(db, paged.Items, cancellationToken)], paged.Page, paged.PageSize, paged.TotalCount);
    }
}

internal sealed class GetSeriesHandler(ClassesDbContext db, IAccessContext access) : IQueryHandler<GetSeriesQuery, ClassSeriesDto>
{
    public async Task<Result<ClassSeriesDto>> HandleAsync(GetSeriesQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var series = await db.ClassSeries.AsNoTracking().VisibleTo(actor.Value).FirstOrDefaultAsync(s => s.Id == query.Id, cancellationToken);
        return series is null ? SessionErrors.SeriesNotFound : await SessionScheduling.ToSeriesDtoAsync(db, series, cancellationToken);
    }
}

internal sealed class CreateSeriesHandler(ClassesDbContext db, IAccessContext access, IClock clock, ICurrentUser currentUser)
    : ICommandHandler<CreateSeriesCommand, SeriesRunDto>
{
    public async Task<Result<SeriesRunDto>> HandleAsync(CreateSeriesCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var request = command.Request;
        var groupClass = await db.GroupClasses.AsNoTracking().VisibleTo(actor.Value).FirstOrDefaultAsync(c => c.Id == request.ClassId, cancellationToken);
        if (groupClass is null)
        {
            return GroupClassErrors.NotFound;
        }

        if (!groupClass.IsActive)
        {
            return SessionErrors.ClassInactive;
        }

        if (request.EndDate < request.StartDate || request.EndDate.DayNumber - request.StartDate.DayNumber > ClassSeries.MaxSpanDays)
        {
            return SessionErrors.SeriesRangeInvalid;
        }

        var now = clock.UtcNow;
        var zone = await SessionScheduling.ZoneAsync(db, groupClass.BranchId, cancellationToken);
        if (request.StartDate < DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime))
        {
            return SessionErrors.SeriesInPast;
        }

        var instructorId = request.InstructorId ?? groupClass.InstructorId;
        if (await GroupClassQueries.CheckInstructorAsync(db, instructorId, groupClass.BranchId, cancellationToken) is { } instructorError)
        {
            return instructorError;
        }

        // Dos solicitudes simultáneas con el mismo instructor y horario: la segunda choca con la restricción de la base. Se reintenta una
        // vez con la foto actual (que ya incluye lo de la primera) y, si vuelve a chocar, se informa el conflicto.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var series = ClassSeries.Create(
                actor.Value.TenantId,
                groupClass.BranchId,
                groupClass.Id,
                instructorId,
                request.Capacity ?? groupClass.Capacity,
                groupClass.DurationMinutes,
                request.StartDate,
                request.EndDate,
                request.StartTime,
                request.Days,
                currentUser.UserId,
                now);
            if (series.Dates(series.EndDate).Count() > ClassSeries.MaxSessionsPerRun)
            {
                return SessionErrors.SeriesTooManySessions;
            }

            var plan = await SessionScheduling.PlanAsync(db, series, zone, currentUser.UserId, now, cancellationToken);
            if (plan.Created.Count == 0)
            {
                return SessionErrors.SeriesNoSessions;
            }

            db.ClassSeries.Add(series);
            db.ClassSessions.AddRange(plan.Created);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (SessionScheduling.IsRetryable(ex))
            {
                db.ChangeTracker.Clear();
                continue;
            }

            return new SeriesRunDto(await SessionScheduling.ToSeriesDtoAsync(db, series, cancellationToken), plan.Created.Count, plan.AlreadyExisted, plan.Past, plan.Skipped);
        }

        return SessionErrors.InstructorOverlap;
    }
}

internal sealed class GenerateSeriesHandler(ClassesDbContext db, IAccessContext access, IClock clock, ICurrentUser currentUser)
    : ICommandHandler<GenerateSeriesCommand, SeriesRunDto>
{
    public async Task<Result<SeriesRunDto>> HandleAsync(GenerateSeriesCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        // Regenerar es idempotente: si dos solicitudes lo hacen a la vez, la segunda choca con la unicidad (serie, hueco), reintenta y
        // encuentra ya creado lo de la primera, así que termina sin duplicar nada.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            db.ChangeTracker.Clear();
            var series = await db.ClassSeries.VisibleTo(actor.Value).FirstOrDefaultAsync(s => s.Id == command.Id, cancellationToken);
            if (series is null)
            {
                return SessionErrors.SeriesNotFound;
            }

            if (!series.IsActive)
            {
                return SessionErrors.SeriesCancelled;
            }

            var endDate = command.Request.EndDate ?? series.EndDate;
            if (endDate < series.EndDate)
            {
                return SessionErrors.SeriesShrink;
            }

            if (endDate.DayNumber - series.StartDate.DayNumber > ClassSeries.MaxSpanDays)
            {
                return SessionErrors.SeriesRangeInvalid;
            }

            if (!await db.GroupClasses.AsNoTracking().AnyAsync(c => c.Id == series.ClassId && c.IsActive, cancellationToken))
            {
                return SessionErrors.ClassInactive;
            }

            if (await GroupClassQueries.CheckInstructorAsync(db, series.InstructorId, series.BranchId, cancellationToken) is { } instructorError)
            {
                return instructorError;
            }

            var now = clock.UtcNow;
            if (endDate > series.EndDate)
            {
                series.ExtendTo(endDate, currentUser.UserId, now);
            }

            if (series.Dates(series.EndDate).Count() > ClassSeries.MaxSessionsPerRun)
            {
                return SessionErrors.SeriesTooManySessions;
            }

            var zone = await SessionScheduling.ZoneAsync(db, series.BranchId, cancellationToken);
            var plan = await SessionScheduling.PlanAsync(db, series, zone, currentUser.UserId, now, cancellationToken);
            db.ClassSessions.AddRange(plan.Created);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (SessionScheduling.IsRetryable(ex))
            {
                continue;
            }

            return new SeriesRunDto(await SessionScheduling.ToSeriesDtoAsync(db, series, cancellationToken), plan.Created.Count, plan.AlreadyExisted, plan.Past, plan.Skipped);
        }

        return SessionErrors.InstructorOverlap;
    }
}

internal sealed class CancelSeriesHandler(ClassesDbContext db, IAccessContext access, IClock clock, ICurrentUser currentUser)
    : ICommandHandler<CancelSeriesCommand, CancelSeriesResultDto>
{
    public async Task<Result<CancelSeriesResultDto>> HandleAsync(CancelSeriesCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var series = await db.ClassSeries.VisibleTo(actor.Value).FirstOrDefaultAsync(s => s.Id == command.Id, cancellationToken);
        if (series is null)
        {
            return SessionErrors.SeriesNotFound;
        }

        if (!series.IsActive)
        {
            return new CancelSeriesResultDto(await SessionScheduling.ToSeriesDtoAsync(db, series, cancellationToken), 0);
        }

        // Cancela la serie, las sesiones futuras que aún están programadas y sus reservas; las pasadas conservan su historia. Se bloquean
        // las sesiones antes de leerlas, así nadie reserva en una a mitad de camino.
        var now = clock.UtcNow;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var futureIds = await db.ClassSessions.AsNoTracking()
            .Where(s => s.SeriesId == series.Id && s.Status == SessionStatus.Scheduled && s.StartsAt > now)
            .Select(s => s.Id)
            .ToListAsync(cancellationToken);
        await SessionScheduling.LockSessionsAsync(db, futureIds, cancellationToken);
        var future = await db.ClassSessions
            .Where(s => s.SeriesId == series.Id && s.Status == SessionStatus.Scheduled && s.StartsAt > now)
            .ToListAsync(cancellationToken);
        foreach (var session in future)
        {
            session.Cancel(command.Request.Reason, currentUser.UserId, now);
        }

        await SessionScheduling.CancelLiveReservationsAsync(db, [.. future.Select(s => s.Id)], command.Request.Reason, currentUser.UserId, now, cancellationToken);
        series.Cancel(command.Request.Reason, currentUser.UserId, now);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CancelSeriesResultDto(await SessionScheduling.ToSeriesDtoAsync(db, series, cancellationToken), future.Count);
    }
}
