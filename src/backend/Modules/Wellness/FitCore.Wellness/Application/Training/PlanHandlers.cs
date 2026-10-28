using FitCore.Application.Core;
using FitCore.Platform.Security;
using FitCore.SharedKernel;
using FitCore.Wellness.Domain;
using FitCore.Wellness.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FitCore.Wellness.Application.Training;

internal sealed record BuiltContent(TrainingPlanVersion Version, IReadOnlyList<TrainingPlanDay> Days, IReadOnlyList<TrainingPlanExercise> Exercises);

internal static class PlanContent
{
    /// <summary>
    /// Valida y arma una versión de rutina. Cada ejercicio debe existir en la biblioteca y estar activo (salvo que la rutina ya lo
    /// tuviera: no se obliga a quitarlo de lo que ya se prescribía), y su prescripción debe ser coherente con su modo de registro.
    /// </summary>
    public static async Task<Result<BuiltContent>> BuildAsync(
        WellnessDbContext db,
        Guid tenantId,
        Guid planId,
        int versionNumber,
        TrainingPlanRequest request,
        IReadOnlySet<Guid> alreadyInPlan,
        Guid? userId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var requested = (request.Days ?? []).SelectMany(d => d.Exercises ?? []).Select(e => e.ExerciseId).Distinct().ToList();
        var exercises = await db.Exercises.AsNoTracking().Where(e => requested.Contains(e.Id)).ToDictionaryAsync(e => e.Id, cancellationToken);
        if (exercises.Count != requested.Count)
        {
            return TrainingErrors.ExerciseNotFound;
        }

        if (requested.Any(id => !exercises[id].IsActive && !alreadyInPlan.Contains(id)))
        {
            return TrainingErrors.ExerciseInactive;
        }

        var header = new PlanHeader(request.Name, request.Goal, request.Description, request.Level, request.EstimatedMinutes, request.ValidFrom, request.ValidUntil);
        var version = TrainingPlanVersion.Create(tenantId, planId, versionNumber, header, request.ChangeNote, userId, now);
        var days = new List<TrainingPlanDay>();
        var items = new List<TrainingPlanExercise>();
        var dayPosition = 0;
        foreach (var dayRequest in request.Days ?? [])
        {
            var day = TrainingPlanDay.Create(tenantId, version.Id, ++dayPosition, dayRequest.Name, dayRequest.Notes);
            days.Add(day);
            var seen = new HashSet<Guid>();
            var position = 0;
            foreach (var e in dayRequest.Exercises ?? [])
            {
                if (!seen.Add(e.ExerciseId))
                {
                    return TrainingErrors.DuplicateExercise;
                }

                var exercise = exercises[e.ExerciseId];
                var prescription = new Prescription(e.Sets, e.Reps, e.DurationSeconds, e.RestSeconds, e.RecommendedLoadKg, e.Notes);
                var problem = TrainingRules.ValidatePrescription(exercise.TrackingMode, prescription);
                if (problem is not null)
                {
                    return Error.Validation(problem, $"«{exercise.Name}»: la prescripción no es coherente con cómo se registra este ejercicio ({exercise.TrackingMode}).");
                }

                items.Add(TrainingPlanExercise.Create(tenantId, day.Id, exercise.Id, ++position, exercise.Name, exercise.TrackingMode, prescription));
            }
        }

        return new BuiltContent(version, days, items);
    }

    public static async Task<PlanDetailDto> ToDetailAsync(WellnessDbContext db, TrainingPlan plan, TrainingPlanVersion version, CancellationToken cancellationToken)
    {
        var days = await db.PlanDays.AsNoTracking().Where(d => d.VersionId == version.Id).OrderBy(d => d.Position).ToListAsync(cancellationToken);
        var dayIds = days.Select(d => d.Id).ToList();
        var exercises = await db.PlanExercises.AsNoTracking().Where(e => dayIds.Contains(e.DayId)).OrderBy(e => e.Position).ToListAsync(cancellationToken);
        var byDay = exercises.ToLookup(e => e.DayId);
        return new PlanDetailDto(
            plan.Id,
            plan.Status.ToString(),
            version.Number,
            version.Id,
            plan.CurrentVersionId == version.Id,
            version.Name,
            version.Goal,
            version.Description,
            version.Level.ToString(),
            version.EstimatedMinutes,
            version.ValidFrom,
            version.ValidUntil,
            version.ChangeNote,
            [.. days.Select(d => new PlanDayDto(d.Id, d.Position, d.Name, d.Notes, [.. byDay[d.Id].Select(ToDto)]))],
            version.CreatedAt,
            plan.CreatedAt,
            plan.UpdatedAt);
    }

    public static PlanExerciseDto ToDto(TrainingPlanExercise e) =>
        new(e.Id, e.ExerciseId, e.Position, e.ExerciseName, e.TrackingMode.ToString(), e.Sets, e.Reps, e.DurationSeconds, e.RestSeconds, e.RecommendedLoadKg, e.Notes);

    /// <summary>Ejercicios de la versión vigente de una rutina (para permitir que sigan en una versión nueva aunque se hayan desactivado).</summary>
    public static async Task<HashSet<Guid>> CurrentExerciseIdsAsync(WellnessDbContext db, TrainingPlan plan, CancellationToken cancellationToken)
    {
        if (plan.CurrentVersionId is not { } versionId)
        {
            return [];
        }

        var ids = await db.PlanDays.AsNoTracking().Where(d => d.VersionId == versionId)
            .Join(db.PlanExercises, d => d.Id, e => e.DayId, (_, e) => e.ExerciseId)
            .Distinct()
            .ToListAsync(cancellationToken);
        return [.. ids];
    }

    public static async Task SaveContentAsync(WellnessDbContext db, BuiltContent content, CancellationToken cancellationToken)
    {
        // Versión, días y ejercicios se guardan por etapas: cada nivel referencia al anterior y la base los exige en ese orden.
        db.PlanVersions.Add(content.Version);
        await db.SaveChangesAsync(cancellationToken);
        db.PlanDays.AddRange(content.Days);
        await db.SaveChangesAsync(cancellationToken);
        db.PlanExercises.AddRange(content.Exercises);
        await db.SaveChangesAsync(cancellationToken);
    }
}

internal sealed class ListPlansHandler(WellnessDbContext db, IAccessContext access) : IQueryHandler<ListPlansQuery, PagedResult<PlanSummaryDto>>
{
    public async Task<Result<PagedResult<PlanSummaryDto>>> HandleAsync(ListPlansQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var plans = db.Plans.AsNoTracking()
            .Join(db.PlanVersions.AsNoTracking(), p => p.CurrentVersionId, v => v.Id, (p, v) => new { Plan = p, Version = v });
        if (query.Status is { } status)
        {
            plans = plans.Where(x => x.Plan.Status == status);
        }

        if (query.Level is { } level)
        {
            plans = plans.Where(x => x.Version.Level == level);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var pattern = LikePattern.Contains(query.Search);
            plans = plans.Where(x => EF.Functions.ILike(x.Version.Name, pattern) || (x.Version.Goal != null && EF.Functions.ILike(x.Version.Goal, pattern)));
        }

        var paged = await plans.OrderBy(x => x.Version.Name).ThenBy(x => x.Plan.Id).ToPagedAsync(query.Page, cancellationToken);
        var items = await PlanSummaries.BuildAsync(db, [.. paged.Items.Select(x => (x.Plan, x.Version))], cancellationToken);
        return new PagedResult<PlanSummaryDto>(items, paged.Page, paged.PageSize, paged.TotalCount);
    }
}

internal static class PlanSummaries
{
    public static async Task<IReadOnlyList<PlanSummaryDto>> BuildAsync(
        WellnessDbContext db,
        IReadOnlyList<(TrainingPlan Plan, TrainingPlanVersion Version)> pairs,
        CancellationToken cancellationToken)
    {
        var versionIds = pairs.Select(x => x.Version.Id).ToList();
        var planIds = pairs.Select(x => x.Plan.Id).ToList();
        var dayCounts = await db.PlanDays.AsNoTracking().Where(d => versionIds.Contains(d.VersionId))
            .GroupBy(d => d.VersionId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);
        var exerciseCounts = await db.PlanDays.AsNoTracking().Where(d => versionIds.Contains(d.VersionId))
            .Join(db.PlanExercises, d => d.Id, e => e.DayId, (d, _) => d.VersionId)
            .GroupBy(v => v).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);
        var assignments = await db.TrainingAssignments.AsNoTracking().Where(a => planIds.Contains(a.PlanId) && a.EndedOn == null)
            .GroupBy(a => a.PlanId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);

        return [.. pairs.Select(x => new PlanSummaryDto(
            x.Plan.Id,
            x.Version.Name,
            x.Version.Goal,
            x.Version.Level.ToString(),
            x.Version.EstimatedMinutes,
            x.Plan.Status.ToString(),
            x.Version.Number,
            x.Version.ValidFrom,
            x.Version.ValidUntil,
            dayCounts.GetValueOrDefault(x.Version.Id),
            exerciseCounts.GetValueOrDefault(x.Version.Id),
            assignments.GetValueOrDefault(x.Plan.Id),
            x.Plan.CreatedAt,
            x.Plan.UpdatedAt))];
    }
}

internal sealed class GetPlanHandler(WellnessDbContext db, IAccessContext access) : IQueryHandler<GetPlanQuery, PlanDetailDto>
{
    public async Task<Result<PlanDetailDto>> HandleAsync(GetPlanQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var plan = await db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == query.Id, cancellationToken);
        if (plan is null)
        {
            return TrainingErrors.PlanNotFound;
        }

        var version = query.Version is { } number
            ? await db.PlanVersions.AsNoTracking().FirstOrDefaultAsync(v => v.PlanId == plan.Id && v.Number == number, cancellationToken)
            : await db.PlanVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == plan.CurrentVersionId, cancellationToken);
        return version is null ? TrainingErrors.VersionNotFound : await PlanContent.ToDetailAsync(db, plan, version, cancellationToken);
    }
}

internal sealed class ListPlanVersionsHandler(WellnessDbContext db, IAccessContext access) : IQueryHandler<ListPlanVersionsQuery, IReadOnlyList<PlanVersionDto>>
{
    public async Task<Result<IReadOnlyList<PlanVersionDto>>> HandleAsync(ListPlanVersionsQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        if (!await db.Plans.AsNoTracking().AnyAsync(p => p.Id == query.Id, cancellationToken))
        {
            return TrainingErrors.PlanNotFound;
        }

        var versions = await db.PlanVersions.AsNoTracking().Where(v => v.PlanId == query.Id).OrderByDescending(v => v.Number).ToListAsync(cancellationToken);
        return versions.Select(v => new PlanVersionDto(v.Id, v.Number, v.Name, v.ChangeNote, v.CreatedAt, v.CreatedBy)).ToList();
    }
}

internal sealed class CreatePlanHandler(WellnessDbContext db, IAccessContext access, IClock clock, ICurrentUser currentUser)
    : ICommandHandler<CreatePlanCommand, PlanDetailDto>
{
    public async Task<Result<PlanDetailDto>> HandleAsync(CreatePlanCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var now = clock.UtcNow;
        var plan = TrainingPlan.Create(actor.Value.TenantId, currentUser.UserId, now);
        var content = await PlanContent.BuildAsync(db, actor.Value.TenantId, plan.Id, 1, command.Request, new HashSet<Guid>(), currentUser.UserId, now, cancellationToken);
        if (content.IsFailure)
        {
            return content.Error;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.Plans.Add(plan);
        await db.SaveChangesAsync(cancellationToken);
        await PlanContent.SaveContentAsync(db, content.Value, cancellationToken);
        plan.PublishVersion(content.Value.Version, currentUser.UserId, now);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await PlanContent.ToDetailAsync(db, plan, content.Value.Version, cancellationToken);
    }
}

internal sealed class UpdatePlanHandler(WellnessDbContext db, IAccessContext access, IClock clock, ICurrentUser currentUser)
    : ICommandHandler<UpdatePlanCommand, PlanDetailDto>
{
    public async Task<Result<PlanDetailDto>> HandleAsync(UpdatePlanCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var plan = await db.Plans.FirstOrDefaultAsync(p => p.Id == command.Id, cancellationToken);
        if (plan is null)
        {
            return TrainingErrors.PlanNotFound;
        }

        if (plan.Status == PlanStatus.Archived)
        {
            return TrainingErrors.PlanArchived;
        }

        var now = clock.UtcNow;
        var inPlan = await PlanContent.CurrentExerciseIdsAsync(db, plan, cancellationToken);
        var content = await PlanContent.BuildAsync(db, actor.Value.TenantId, plan.Id, plan.CurrentVersion + 1, command.Request, inPlan, currentUser.UserId, now, cancellationToken);
        if (content.IsFailure)
        {
            return content.Error;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await PlanContent.SaveContentAsync(db, content.Value, cancellationToken);
        plan.PublishVersion(content.Value.Version, currentUser.UserId, now);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Otra persona modificó la rutina al mismo tiempo: no se pisa su versión.
            return Error.Conflict("conflict.concurrency", "La rutina cambió mientras la editaba. Recárguela e intente de nuevo.");
        }

        await transaction.CommitAsync(cancellationToken);
        return await PlanContent.ToDetailAsync(db, plan, content.Value.Version, cancellationToken);
    }
}

internal sealed class DuplicatePlanHandler(WellnessDbContext db, IAccessContext access, IClock clock, ICurrentUser currentUser)
    : ICommandHandler<DuplicatePlanCommand, PlanDetailDto>
{
    public async Task<Result<PlanDetailDto>> HandleAsync(DuplicatePlanCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var source = await db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == command.Id, cancellationToken);
        var version = source is null ? null : await db.PlanVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == source.CurrentVersionId, cancellationToken);
        if (source is null || version is null)
        {
            return TrainingErrors.PlanNotFound;
        }

        var detail = await PlanContent.ToDetailAsync(db, source, version, cancellationToken);
        var request = new TrainingPlanRequest(
            $"{detail.Name} (copia)",
            detail.Goal,
            detail.Description,
            Enum.Parse<Difficulty>(detail.Level),
            detail.EstimatedMinutes,
            detail.ValidFrom,
            detail.ValidUntil,
            $"Copia de «{detail.Name}» versión {detail.Version}",
            [.. detail.Days.Select(d => new PlanDayRequest(d.Name, d.Notes, [.. d.Exercises.Select(e => new PlanExerciseRequest(e.ExerciseId, e.Sets, e.Reps, e.DurationSeconds, e.RestSeconds, e.RecommendedLoadKg, e.Notes))]))]);

        var now = clock.UtcNow;
        var plan = TrainingPlan.Create(actor.Value.TenantId, currentUser.UserId, now);
        var allExercises = detail.Days.SelectMany(d => d.Exercises).Select(e => e.ExerciseId).ToHashSet();
        var content = await PlanContent.BuildAsync(db, actor.Value.TenantId, plan.Id, 1, request, allExercises, currentUser.UserId, now, cancellationToken);
        if (content.IsFailure)
        {
            return content.Error;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.Plans.Add(plan);
        await db.SaveChangesAsync(cancellationToken);
        await PlanContent.SaveContentAsync(db, content.Value, cancellationToken);
        plan.PublishVersion(content.Value.Version, currentUser.UserId, now);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await PlanContent.ToDetailAsync(db, plan, content.Value.Version, cancellationToken);
    }
}

internal sealed class SetPlanStatusHandler(WellnessDbContext db, IAccessContext access, IClock clock, ICurrentUser currentUser)
    : ICommandHandler<SetPlanStatusCommand, PlanSummaryDto>
{
    public async Task<Result<PlanSummaryDto>> HandleAsync(SetPlanStatusCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var plan = await db.Plans.FirstOrDefaultAsync(p => p.Id == command.Id, cancellationToken);
        if (plan is null)
        {
            return TrainingErrors.PlanNotFound;
        }

        // Archivar no toca lo ya asignado ni entrenado: solo deja de ofrecerse para nuevas asignaciones.
        plan.SetStatus(command.Request.Active ? PlanStatus.Active : PlanStatus.Archived, currentUser.UserId, clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);

        var version = await db.PlanVersions.AsNoTracking().FirstAsync(v => v.Id == plan.CurrentVersionId, cancellationToken);
        return (await PlanSummaries.BuildAsync(db, [(plan, version)], cancellationToken))[0];
    }
}
