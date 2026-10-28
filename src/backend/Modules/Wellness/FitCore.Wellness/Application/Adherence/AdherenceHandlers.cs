using FitCore.Application.Core;
using FitCore.Platform.Application;
using FitCore.Platform.Security;
using FitCore.SharedKernel;
using FitCore.Wellness.Application.Nutrition;
using FitCore.Wellness.Application.Training;
using FitCore.Wellness.Domain;
using FitCore.Wellness.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FitCore.Wellness.Application.Adherence;

internal static class AdherenceRange
{
    public static readonly Error Invalid = Error.Validation("adherence.range_invalid", "El rango de fechas no es válido (máximo 92 días).");

    /// <summary>Rango en días de la sucursal: por defecto las últimas cuatro semanas, nunca más allá de hoy.</summary>
    public static Result<(DateOnly From, DateOnly To)> Resolve(DateOnly today, DateOnly? from, DateOnly? to)
    {
        var end = to is { } t && t < today ? t : today;
        var start = from ?? end.AddDays(-(AdherenceRules.DefaultRangeDays - 1));
        return start > end || end.DayNumber - start.DayNumber >= AdherenceRules.MaxRangeDays ? Invalid : (start, end);
    }

    /// <summary>Semanas (lunes a domingo) que toca el rango, cada una recortada al rango.</summary>
    public static IEnumerable<(DateOnly WeekStart, DateOnly Start, DateOnly End)> Weeks(DateOnly from, DateOnly to)
    {
        for (var week = AdherenceRules.WeekStart(from); week <= to; week = week.AddDays(7))
        {
            yield return (week, week < from ? from : week, week.AddDays(6) > to ? to : week.AddDays(6));
        }
    }
}

internal sealed class TrainingAdherenceHandler(WellnessDbContext db, IAccessContext access, IClock clock)
    : IQueryHandler<TrainingAdherenceQuery, TrainingAdherenceDto>
{
    public async Task<Result<TrainingAdherenceDto>> HandleAsync(TrainingAdherenceQuery query, CancellationToken cancellationToken)
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

        var today = await LocalDates.TodayAtBranchAsync(db, clock, scope.Value.Member.BranchId, cancellationToken);
        var range = AdherenceRange.Resolve(today, query.From, query.To);
        if (range.IsFailure)
        {
            return range.Error;
        }

        var (from, to) = range.Value;
        var assignments = await db.TrainingAssignments.AsNoTracking().Where(a => a.MemberId == query.MemberId && a.StartDate <= to)
            .OrderByDescending(a => a.StartDate).ThenByDescending(a => a.CreatedAt).ToListAsync(cancellationToken);
        var versionIds = assignments.Select(a => a.PlanVersionId).Distinct().ToList();
        var daysPerVersion = await db.PlanDays.AsNoTracking().Where(d => versionIds.Contains(d.VersionId))
            .GroupBy(d => d.VersionId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);
        var sessionDates = await db.WorkoutSessions.AsNoTracking().Where(s => s.MemberId == query.MemberId && s.PerformedOn >= from && s.PerformedOn <= to)
            .Select(s => s.PerformedOn).ToListAsync(cancellationToken);

        var weeks = new List<TrainingWeekDto>();
        var countedSessions = 0;
        var expectedTotal = 0;
        foreach (var (weekStart, start, end) in AdherenceRange.Weeks(from, to))
        {
            var covered = 0;
            var planDays = 0;
            for (var day = start; day <= end; day = day.AddDays(1))
            {
                if (assignments.FirstOrDefault(a => a.IsCurrentOn(day)) is { } current)
                {
                    covered++;
                    planDays = daysPerVersion.GetValueOrDefault(current.PlanVersionId);
                }
            }

            var expected = AdherenceRules.ExpectedSessions(planDays, covered);
            var done = sessionDates.Count(d => d >= start && d <= end);
            weeks.Add(new TrainingWeekDto(weekStart, covered, expected, done, AdherenceRules.Percent(done, expected)));
            expectedTotal += expected;
            countedSessions += Math.Min(done, expected);
        }

        return new TrainingAdherenceDto(from, to, expectedTotal, sessionDates.Count, AdherenceRules.Percent(countedSessions, expectedTotal), weeks);
    }
}

internal sealed class NutritionAdherenceHandler(WellnessDbContext db, IAccessContext access, IClock clock, SecurityAudit audit)
    : IQueryHandler<NutritionAdherenceQuery, NutritionAdherenceDto>
{
    public async Task<Result<NutritionAdherenceDto>> HandleAsync(NutritionAdherenceQuery query, CancellationToken cancellationToken)
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
        var range = AdherenceRange.Resolve(today, query.From, query.To);
        if (range.IsFailure)
        {
            return range.Error;
        }

        var (from, to) = range.Value;
        var assignments = await db.NutritionAssignments.AsNoTracking().Where(a => a.MemberId == query.MemberId && a.StartDate <= to)
            .OrderByDescending(a => a.StartDate).ThenByDescending(a => a.CreatedAt).ToListAsync(cancellationToken);
        var versionIds = assignments.Select(a => a.PlanVersionId).Distinct().ToList();
        var mealsPerVersion = await db.NutritionMeals.AsNoTracking().Where(m => versionIds.Contains(m.VersionId))
            .GroupBy(m => m.VersionId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);
        var logs = (await db.MealLogs.AsNoTracking().Where(l => l.MemberId == query.MemberId && l.LoggedOn >= from && l.LoggedOn <= to)
            .Select(l => new { l.LoggedOn, l.Status }).ToListAsync(cancellationToken)).ToLookup(l => l.LoggedOn);

        var weeks = new List<NutritionWeekDto>();
        foreach (var (weekStart, start, end) in AdherenceRange.Weeks(from, to))
        {
            int planned = 0, completed = 0, partial = 0, skipped = 0;
            for (var day = start; day <= end; day = day.AddDays(1))
            {
                var forDay = logs[day].ToList();
                completed += forDay.Count(l => l.Status == MealLogStatus.Completed);
                partial += forDay.Count(l => l.Status == MealLogStatus.Partial);
                skipped += forDay.Count(l => l.Status == MealLogStatus.Skipped);
                if (assignments.FirstOrDefault(a => a.IsCurrentOn(day)) is { } current)
                {
                    // Hoy todavía puede registrarse: lo pendiente de hoy no cuenta en contra (solo lo ya registrado).
                    planned += day == today ? forDay.Count : mealsPerVersion.GetValueOrDefault(current.PlanVersionId);
                }
            }

            var notLogged = Math.Max(0, planned - completed - partial - skipped);
            weeks.Add(new NutritionWeekDto(weekStart, planned, completed, partial, skipped, notLogged, AdherenceRules.NutritionPercent(completed, partial, planned)));
        }

        var totals = (Planned: weeks.Sum(w => w.Planned), Completed: weeks.Sum(w => w.Completed), Partial: weeks.Sum(w => w.Partial), Skipped: weeks.Sum(w => w.Skipped), NotLogged: weeks.Sum(w => w.NotLogged));
        if (!scope.Value.IsSelf)
        {
            await audit.RecordAccessAsync("Wellness.NutritionAdherenceRead", "Member", query.MemberId.ToString(), cancellationToken);
        }

        return new NutritionAdherenceDto(
            from, to, totals.Planned, totals.Completed, totals.Partial, totals.Skipped, totals.NotLogged,
            AdherenceRules.NutritionPercent(totals.Completed, totals.Partial, totals.Planned), weeks);
    }
}
