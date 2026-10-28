using FitCore.Application.Core;
using FitCore.Platform.Security;
using FitCore.SharedKernel;
using FitCore.Wellness.Domain;
using FitCore.Wellness.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FitCore.Wellness.Application.Training;

internal static class SessionMapper
{
    public static async Task<IReadOnlyList<WorkoutSessionDto>> ToDtosAsync(WellnessDbContext db, IReadOnlyList<WorkoutSession> sessions, CancellationToken cancellationToken)
    {
        if (sessions.Count == 0)
        {
            return [];
        }

        var sessionIds = sessions.Select(s => s.Id).ToList();
        var versionIds = sessions.Select(s => s.PlanVersionId).Distinct().ToList();
        var dayIds = sessions.Select(s => s.DayId).Distinct().ToList();
        var versions = await db.PlanVersions.AsNoTracking().Where(v => versionIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, cancellationToken);
        var days = await db.PlanDays.AsNoTracking().Where(d => dayIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, cancellationToken);
        var entries = await db.WorkoutSessionExercises.AsNoTracking().Where(e => sessionIds.Contains(e.SessionId)).ToListAsync(cancellationToken);
        var entryIds = entries.Select(e => e.Id).ToList();
        var planExerciseIds = entries.Select(e => e.PlanExerciseId).Distinct().ToList();
        var planned = await db.PlanExercises.AsNoTracking().Where(e => planExerciseIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, cancellationToken);
        var sets = (await db.WorkoutSets.AsNoTracking().Where(s => entryIds.Contains(s.SessionExerciseId)).OrderBy(s => s.SetNumber).ToListAsync(cancellationToken))
            .ToLookup(s => s.SessionExerciseId);
        var entriesBySession = entries.ToLookup(e => e.SessionId);

        return
        [
            .. sessions.Select(session =>
            {
                versions.TryGetValue(session.PlanVersionId, out var version);
                days.TryGetValue(session.DayId, out var day);
                var list = entriesBySession[session.Id]
                    .OrderBy(e => planned.TryGetValue(e.PlanExerciseId, out var p) ? p.Position : int.MaxValue)
                    .Select(e =>
                    {
                        planned.TryGetValue(e.PlanExerciseId, out var p);
                        return new SessionExerciseDto(
                            e.PlanExerciseId, e.ExerciseId, e.ExerciseName, e.TrackingMode.ToString(), e.Completed, e.Notes,
                            p?.Sets ?? 0, p?.Reps, p?.DurationSeconds, p?.RecommendedLoadKg,
                            [.. sets[e.Id].Select(s => new SetDto(s.SetNumber, s.Reps, s.DurationSeconds, s.LoadKg))]);
                    })
                    .ToList();
                return new WorkoutSessionDto(
                    session.Id,
                    session.MemberId,
                    session.PerformedOn,
                    session.DayId,
                    day?.Name ?? string.Empty,
                    version?.PlanId ?? Guid.Empty,
                    version?.Name ?? string.Empty,
                    version?.Number ?? 0,
                    session.DurationMinutes,
                    session.MemberNotes,
                    session.TrainerComment,
                    session.TrainerCommentAt,
                    list.Count(e => e.Completed),
                    list.Count,
                    list.Sum(e => e.Sets.Count),
                    list.Sum(e => e.Sets.Sum(s => TrainingRules.Volume(new ActualSet(s.Reps, s.DurationSeconds, s.LoadKg)))),
                    list);
            })
        ];
    }

    public static ExerciseTotalsDto Totals(IEnumerable<SetDto> sets)
    {
        var list = sets.ToList();
        return new ExerciseTotalsDto(
            list.Count,
            list.Sum(s => s.Reps ?? 0),
            list.Sum(s => s.DurationSeconds ?? 0),
            list.Where(s => s.LoadKg is not null).Select(s => s.LoadKg).DefaultIfEmpty(null).Max(),
            list.Sum(s => TrainingRules.Volume(new ActualSet(s.Reps, s.DurationSeconds, s.LoadKg))));
    }
}

internal sealed class CreateWorkoutSessionHandler(WellnessDbContext db, IAccessContext access, IClock clock)
    : ICommandHandler<CreateWorkoutSessionCommand, WorkoutSessionDto>
{
    private const int MaxDaysBack = 30;

    public async Task<Result<WorkoutSessionDto>> HandleAsync(CreateWorkoutSessionCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var request = command.Request;

        // El alumno solo registra la suya: el identificador del cliente nunca decide de quién es la sesión.
        var own = await db.MemberRefs.AsNoTracking().FirstOrDefaultAsync(m => m.UserId == actor.Value.UserId, cancellationToken);
        if (own is not null && request.MemberId is { } sent && sent != own.Id)
        {
            return TrainingErrors.OtherMember;
        }

        var memberId = own?.Id ?? request.MemberId;
        if (memberId is null)
        {
            return TrainingErrors.MemberNotFound;
        }

        var scope = await MemberScope.ResolveAsync(db, actor.Value, memberId.Value, cancellationToken);
        if (scope.IsFailure)
        {
            return scope.Error;
        }

        var member = scope.Value.Member;
        if (!member.IsActive)
        {
            return TrainingErrors.MemberInactive;
        }

        var today = await LocalDates.TodayAtBranchAsync(db, clock, member.BranchId, cancellationToken);
        var performedOn = request.PerformedOn ?? today;
        if (performedOn > today || performedOn < today.AddDays(-MaxDaysBack))
        {
            return TrainingErrors.SessionDateInvalid;
        }

        var assignment = await AssignmentMapper.CurrentAsync(db, member.Id, performedOn, cancellationToken);
        if (assignment is null)
        {
            return performedOn == today ? TrainingErrors.NoActivePlan : TrainingErrors.SessionDateInvalid;
        }

        var day = await db.PlanDays.AsNoTracking().FirstOrDefaultAsync(d => d.Id == request.DayId && d.VersionId == assignment.PlanVersionId, cancellationToken);
        if (day is null)
        {
            return TrainingErrors.DayNotInPlan;
        }

        var dayExercises = await db.PlanExercises.AsNoTracking().Where(e => e.DayId == day.Id).ToDictionaryAsync(e => e.Id, cancellationToken);
        var entriesRequest = request.Exercises ?? [];
        if (entriesRequest.Select(e => e.PlanExerciseId).Distinct().Count() != entriesRequest.Count || entriesRequest.Any(e => !dayExercises.ContainsKey(e.PlanExerciseId)))
        {
            return TrainingErrors.ExerciseNotInDay;
        }

        if (!entriesRequest.Any(e => e.Completed))
        {
            return TrainingErrors.SessionEmpty;
        }

        // Cada serie debe ser coherente con cómo se registra ese ejercicio (repeticiones y carga, repeticiones, o tiempo).
        foreach (var entry in entriesRequest)
        {
            var planned = dayExercises[entry.PlanExerciseId];
            var sets = entry.Sets ?? [];
            if (entry.Completed && sets.Count == 0)
            {
                return TrainingErrors.CompletedWithoutSets;
            }

            foreach (var set in sets)
            {
                var problem = TrainingRules.ValidateActualSet(planned.TrackingMode, new ActualSet(set.Reps, set.DurationSeconds, set.LoadKg));
                if (problem is not null)
                {
                    return Error.Validation(problem, $"«{planned.ExerciseName}»: una serie no es coherente con cómo se registra este ejercicio ({planned.TrackingMode}).");
                }
            }
        }

        var now = clock.UtcNow;
        var session = WorkoutSession.Create(actor.Value.TenantId, member.Id, assignment.Id, assignment.PlanVersionId, day.Id, performedOn, request.DurationMinutes, request.Notes, actor.Value.UserId, now);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.WorkoutSessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);
        var created = new List<(WorkoutSessionExercise Entry, IReadOnlyList<SetRequest> Sets)>();
        foreach (var entry in entriesRequest)
        {
            var planned = dayExercises[entry.PlanExerciseId];
            created.Add((WorkoutSessionExercise.Create(actor.Value.TenantId, session.Id, planned.Id, planned.ExerciseId, planned.ExerciseName, planned.TrackingMode, entry.Completed, entry.Notes), entry.Sets ?? []));
        }

        db.WorkoutSessionExercises.AddRange(created.Select(c => c.Entry));
        await db.SaveChangesAsync(cancellationToken);
        foreach (var (entry, sets) in created)
        {
            var number = 0;
            db.WorkoutSets.AddRange(sets.Select(s => WorkoutSet.Create(actor.Value.TenantId, entry.Id, ++number, new ActualSet(s.Reps, s.DurationSeconds, s.LoadKg))));
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (await SessionMapper.ToDtosAsync(db, [session], cancellationToken))[0];
    }
}

internal sealed class AddTrainerCommentHandler(WellnessDbContext db, IAccessContext access, IClock clock)
    : ICommandHandler<AddTrainerCommentCommand, WorkoutSessionDto>
{
    public async Task<Result<WorkoutSessionDto>> HandleAsync(AddTrainerCommentCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var session = await db.WorkoutSessions.FirstOrDefaultAsync(s => s.Id == command.SessionId, cancellationToken);
        if (session is null)
        {
            return TrainingErrors.SessionNotFound;
        }

        var scope = await MemberScope.ResolveAsync(db, actor.Value, session.MemberId, cancellationToken);
        if (scope.IsFailure)
        {
            return TrainingErrors.SessionNotFound;
        }

        // Comenta el personal que sigue al alumno; el alumno no se comenta a sí mismo como si fuera su entrenador.
        if (scope.Value.IsSelf)
        {
            return TrainingErrors.CommentNotAllowed;
        }

        if (session.TrainerComment is not null)
        {
            return TrainingErrors.CommentExists;
        }

        session.AddTrainerComment(command.Request.Comment, clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        return (await SessionMapper.ToDtosAsync(db, [session], cancellationToken))[0];
    }
}

internal sealed class ListMemberSessionsHandler(WellnessDbContext db, IAccessContext access)
    : IQueryHandler<ListMemberSessionsQuery, PagedResult<WorkoutSessionDto>>
{
    public async Task<Result<PagedResult<WorkoutSessionDto>>> HandleAsync(ListMemberSessionsQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var scope = await MemberScope.ResolveAsync(db, actor.Value, query.MemberId, cancellationToken);
        if (scope.IsFailure)
        {
            return scope.Error;
        }

        var sessions = db.WorkoutSessions.AsNoTracking().Where(s => s.MemberId == query.MemberId);
        if (query.From is { } from)
        {
            sessions = sessions.Where(s => s.PerformedOn >= from);
        }

        if (query.To is { } to)
        {
            sessions = sessions.Where(s => s.PerformedOn <= to);
        }

        var paged = await sessions.OrderByDescending(s => s.PerformedOn).ThenByDescending(s => s.CreatedAt).ToPagedAsync(query.Page, cancellationToken);
        return new PagedResult<WorkoutSessionDto>([.. await SessionMapper.ToDtosAsync(db, paged.Items, cancellationToken)], paged.Page, paged.PageSize, paged.TotalCount);
    }
}

internal sealed class GetWorkoutSessionHandler(WellnessDbContext db, IAccessContext access) : IQueryHandler<GetWorkoutSessionQuery, WorkoutSessionDto>
{
    public async Task<Result<WorkoutSessionDto>> HandleAsync(GetWorkoutSessionQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var session = await db.WorkoutSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == query.Id, cancellationToken);
        if (session is null)
        {
            return TrainingErrors.SessionNotFound;
        }

        var scope = await MemberScope.ResolveAsync(db, actor.Value, session.MemberId, cancellationToken);
        return scope.IsFailure ? TrainingErrors.SessionNotFound : (await SessionMapper.ToDtosAsync(db, [session], cancellationToken))[0];
    }
}

internal sealed class CompareSessionsHandler(WellnessDbContext db, IAccessContext access) : IQueryHandler<CompareSessionsQuery, SessionComparisonDto>
{
    public async Task<Result<SessionComparisonDto>> HandleAsync(CompareSessionsQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var scope = await MemberScope.ResolveAsync(db, actor.Value, query.MemberId, cancellationToken);
        if (scope.IsFailure)
        {
            return scope.Error;
        }

        var ids = new[] { query.SessionA, query.SessionB };
        var sessions = await db.WorkoutSessions.AsNoTracking().Where(s => ids.Contains(s.Id) && s.MemberId == query.MemberId).ToListAsync(cancellationToken);
        var a = sessions.FirstOrDefault(s => s.Id == query.SessionA);
        var b = sessions.FirstOrDefault(s => s.Id == query.SessionB);
        if (a is null || b is null)
        {
            // Una sesión de otro alumno no existe para quien consulta (no se distingue de una inexistente).
            return TrainingErrors.SessionNotFound;
        }

        var dtos = await SessionMapper.ToDtosAsync(db, [a, b], cancellationToken);
        var dtoA = dtos.First(d => d.Id == a.Id);
        var dtoB = dtos.First(d => d.Id == b.Id);

        var byExerciseA = dtoA.Exercises.Where(e => e.Completed).ToDictionary(e => e.ExerciseId);
        var byExerciseB = dtoB.Exercises.Where(e => e.Completed).ToDictionary(e => e.ExerciseId);
        var comparisons = byExerciseA.Keys.Union(byExerciseB.Keys).Select(id =>
        {
            byExerciseA.TryGetValue(id, out var ea);
            byExerciseB.TryGetValue(id, out var eb);
            var reference = ea ?? eb!;
            var ta = ea is null ? null : SessionMapper.Totals(ea.Sets);
            var tb = eb is null ? null : SessionMapper.Totals(eb.Sets);
            return new ExerciseComparisonDto(
                id,
                reference.ExerciseName,
                reference.TrackingMode,
                ta,
                tb,
                (tb?.VolumeKg ?? 0m) - (ta?.VolumeKg ?? 0m),
                ta?.MaxLoadKg is { } la && tb?.MaxLoadKg is { } lb ? lb - la : null,
                (tb?.TotalReps ?? 0) - (ta?.TotalReps ?? 0),
                (tb?.TotalDurationSeconds ?? 0) - (ta?.TotalDurationSeconds ?? 0));
        })
        .OrderBy(c => c.ExerciseName, StringComparer.CurrentCultureIgnoreCase)
        .ToList();

        return new SessionComparisonDto(dtoA, dtoB, comparisons);
    }
}

internal sealed class ExerciseProgressHandler(WellnessDbContext db, IAccessContext access) : IQueryHandler<ExerciseProgressQuery, ExerciseProgressDto>
{
    public async Task<Result<ExerciseProgressDto>> HandleAsync(ExerciseProgressQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var scope = await MemberScope.ResolveAsync(db, actor.Value, query.MemberId, cancellationToken);
        if (scope.IsFailure)
        {
            return scope.Error;
        }

        var sessions = db.WorkoutSessions.AsNoTracking().Where(s => s.MemberId == query.MemberId);
        if (query.From is { } from)
        {
            sessions = sessions.Where(s => s.PerformedOn >= from);
        }

        if (query.To is { } to)
        {
            sessions = sessions.Where(s => s.PerformedOn <= to);
        }

        var entries = await sessions
            .Join(db.WorkoutSessionExercises.AsNoTracking().Where(e => e.ExerciseId == query.ExerciseId && e.Completed), s => s.Id, e => e.SessionId, (s, e) => new { s.Id, s.PerformedOn, s.CreatedAt, Entry = e })
            .OrderBy(x => x.PerformedOn).ThenBy(x => x.CreatedAt)
            .ToListAsync(cancellationToken);
        if (entries.Count == 0)
        {
            return TrainingErrors.ProgressExerciseNotFound;
        }

        var entryIds = entries.Select(x => x.Entry.Id).ToList();
        var sets = (await db.WorkoutSets.AsNoTracking().Where(s => entryIds.Contains(s.SessionExerciseId)).ToListAsync(cancellationToken)).ToLookup(s => s.SessionExerciseId);
        var points = entries.Select(x =>
        {
            var totals = SessionMapper.Totals(sets[x.Entry.Id].Select(s => new SetDto(s.SetNumber, s.Reps, s.DurationSeconds, s.LoadKg)));
            return new ProgressPointDto(x.Id, x.PerformedOn, totals.MaxLoadKg, totals.VolumeKg, totals.TotalReps, totals.TotalDurationSeconds);
        }).ToList();
        var latest = entries[^1].Entry;
        return new ExerciseProgressDto(query.ExerciseId, latest.ExerciseName, latest.TrackingMode.ToString(), points);
    }
}
