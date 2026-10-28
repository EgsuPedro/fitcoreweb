using FitCore.Application.Core;
using FitCore.Platform.Application;
using FitCore.Platform.Security;
using FitCore.SharedKernel;
using FitCore.Wellness.Domain;
using FitCore.Wellness.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FitCore.Wellness.Application.Nutrition;

internal sealed class LogMealHandler(WellnessDbContext db, IAccessContext access, IClock clock)
    : ICommandHandler<LogMealCommand, MealLogDto>
{
    private const int MaxDaysBack = 7;

    public async Task<Result<MealLogDto>> HandleAsync(LogMealCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var request = command.Request;

        // El alumno solo registra el suyo: el identificador del cliente nunca decide de quién es el registro.
        var own = await db.MemberRefs.AsNoTracking().FirstOrDefaultAsync(m => m.UserId == actor.Value.UserId, cancellationToken);
        if (own is not null && request.MemberId is { } sent && sent != own.Id)
        {
            return NutritionErrors.OtherMember;
        }

        var memberId = own?.Id ?? request.MemberId;
        if (memberId is null)
        {
            return NutritionErrors.MemberNotFound;
        }

        var scope = await NutritionScope.ResolveAsync(db, actor.Value, memberId.Value, cancellationToken);
        if (scope.IsFailure)
        {
            return scope.Error;
        }

        var member = scope.Value.Member;
        if (!member.IsActive)
        {
            return NutritionErrors.MemberInactive;
        }

        var today = await LocalDates.TodayAtBranchAsync(db, clock, member.BranchId, cancellationToken);
        var day = request.Date ?? today;
        if (day > today || day < today.AddDays(-MaxDaysBack))
        {
            return NutritionErrors.LogDateInvalid;
        }

        var assignment = await NutritionAssignmentMapper.CurrentAsync(db, member.Id, day, cancellationToken);
        if (assignment is null)
        {
            return day == today ? NutritionErrors.NoActivePlan : NutritionErrors.LogDateInvalid;
        }

        var meal = await db.NutritionMeals.AsNoTracking().FirstOrDefaultAsync(m => m.Id == request.MealId && m.VersionId == assignment.PlanVersionId, cancellationToken);
        if (meal is null)
        {
            return NutritionErrors.MealNotInPlan;
        }

        var version = await db.NutritionVersions.AsNoTracking().FirstAsync(v => v.Id == assignment.PlanVersionId, cancellationToken);
        var log = MealLog.Create(actor.Value.TenantId, member.Id, assignment.Id, assignment.PlanVersionId, meal.Id, day, request.Status, request.Notes, actor.Value.UserId, clock.UtcNow);
        try
        {
            db.MealLogs.Add(log);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return NutritionErrors.MealAlreadyLogged;
        }

        return new MealLogDto(log.Id, log.MemberId, meal.Id, meal.Name, log.LoggedOn, log.Status.ToString(), log.Notes, version.Number, log.CreatedAt);
    }
}

internal sealed class ListMealLogsHandler(WellnessDbContext db, IAccessContext access, IClock clock, SecurityAudit audit)
    : IQueryHandler<ListMealLogsQuery, NutritionLogsDto>
{
    private const int MaxRangeDays = 92;
    private const int DefaultRangeDays = 14;

    public async Task<Result<NutritionLogsDto>> HandleAsync(ListMealLogsQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var scope = await NutritionScope.ResolveAsync(db, actor.Value, query.MemberId, cancellationToken);
        if (scope.IsFailure)
        {
            return scope.Error;
        }

        var today = await LocalDates.TodayAtBranchAsync(db, clock, scope.Value.Member.BranchId, cancellationToken);
        var to = query.To ?? today;
        var from = query.From ?? to.AddDays(-(DefaultRangeDays - 1));
        if (from > to || to.DayNumber - from.DayNumber >= MaxRangeDays)
        {
            return NutritionErrors.RangeInvalid;
        }

        var logs = await db.MealLogs.AsNoTracking().Where(l => l.MemberId == query.MemberId && l.LoggedOn >= from && l.LoggedOn <= to)
            .OrderBy(l => l.LoggedOn).ThenBy(l => l.CreatedAt).ToListAsync(cancellationToken);
        var assignments = await db.NutritionAssignments.AsNoTracking().Where(a => a.MemberId == query.MemberId && a.StartDate <= to)
            .OrderByDescending(a => a.StartDate).ThenByDescending(a => a.CreatedAt).Take(50).ToListAsync(cancellationToken);

        var versionIds = assignments.Select(a => a.PlanVersionId).Concat(logs.Select(l => l.PlanVersionId)).Distinct().ToList();
        var mealsByVersion = (await db.NutritionMeals.AsNoTracking().Where(m => versionIds.Contains(m.VersionId)).ToListAsync(cancellationToken)).ToLookup(m => m.VersionId);
        var mealNames = mealsByVersion.SelectMany(g => g).ToDictionary(m => m.Id, m => m.Name);
        var versionNumbers = await db.NutritionVersions.AsNoTracking().Where(v => versionIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.Number, cancellationToken);
        var logsByDay = logs.ToLookup(l => l.LoggedOn);

        // Cumplimiento por día frente al plan que regía ese día: lo no registrado se cuenta aparte (no se supone hecho ni omitido).
        var days = new List<DayAdherenceDto>();
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            var current = assignments.FirstOrDefault(a => a.IsCurrentOn(day));
            var planned = current is null ? 0 : mealsByVersion[current.PlanVersionId].Count();
            var forDay = logsByDay[day].ToList();
            if (planned == 0 && forDay.Count == 0)
            {
                continue;
            }

            var completed = forDay.Count(l => l.Status == MealLogStatus.Completed);
            var partial = forDay.Count(l => l.Status == MealLogStatus.Partial);
            var skipped = forDay.Count(l => l.Status == MealLogStatus.Skipped);
            days.Add(new DayAdherenceDto(day, planned, completed, partial, skipped, day > today ? 0 : Math.Max(0, planned - forDay.Count)));
        }

        if (!scope.Value.IsSelf)
        {
            await audit.RecordAccessAsync("Wellness.NutritionLogsRead", "Member", query.MemberId.ToString(), cancellationToken);
        }

        return new NutritionLogsDto(
            from,
            to,
            days,
            [.. logs.Select(l => new MealLogDto(l.Id, l.MemberId, l.MealId, mealNames.GetValueOrDefault(l.MealId, string.Empty), l.LoggedOn, l.Status.ToString(), l.Notes, versionNumbers.GetValueOrDefault(l.PlanVersionId), l.CreatedAt))]);
    }
}
