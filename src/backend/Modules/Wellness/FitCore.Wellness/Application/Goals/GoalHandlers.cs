using FitCore.Application.Core;
using FitCore.Platform.Application;
using FitCore.Platform.Security;
using FitCore.SharedKernel;
using FitCore.Wellness.Application.Assessments;
using FitCore.Wellness.Application.Training;
using FitCore.Wellness.Domain;
using FitCore.Wellness.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FitCore.Wellness.Application.Goals;

internal static class GoalMapper
{
    public static decimal? ValueOf(AssessmentDto a, GoalMetric metric) => metric switch
    {
        GoalMetric.WeightKg => a.WeightKg,
        GoalMetric.WaistCm => a.WaistCm,
        GoalMetric.HipCm => a.HipCm,
        GoalMetric.ChestCm => a.ChestCm,
        GoalMetric.ArmCm => a.ArmCm,
        GoalMetric.ThighCm => a.ThighCm,
        GoalMetric.BodyFatPercent => a.BodyFatPercent,
        _ => null
    };

    /// <summary>Última medición vigente (no corregida) de una medida, con su fecha.</summary>
    public static (decimal Value, DateOnly Date)? Latest(IReadOnlyList<AssessmentDto> assessments, GoalMetric metric)
    {
        var found = assessments.Where(a => !a.IsCorrected && ValueOf(a, metric) is not null)
            .OrderByDescending(a => a.AssessedOn).ThenByDescending(a => a.CreatedAt).FirstOrDefault();
        return found is null ? null : (ValueOf(found, metric)!.Value, found.AssessedOn);
    }

    public static async Task<IReadOnlyList<GoalDto>> ToDtosAsync(WellnessDbContext db, MemberRef member, IReadOnlyList<Goal> goals, DateOnly today, CancellationToken cancellationToken)
    {
        if (goals.Count == 0)
        {
            return [];
        }

        var assessments = await AssessmentMapper.LoadAsync(db, member, cancellationToken);
        var weekAgo = today.AddDays(-6);
        var sessionsLastWeek = await db.WorkoutSessions.AsNoTracking().CountAsync(s => s.MemberId == member.Id && s.PerformedOn >= weekAgo && s.PerformedOn <= today, cancellationToken);
        return [.. goals.Select(g => ToDto(g, assessments, sessionsLastWeek, today))];
    }

    public static GoalDto ToDto(Goal g, IReadOnlyList<AssessmentDto> assessments, int sessionsLastWeek, DateOnly today)
    {
        decimal? current = null;
        DateOnly? asOf = null;
        decimal? progress = null;
        bool? reached = null;
        if (g.Category == GoalCategory.BodyMetric && g.Metric is { } metric && Latest(assessments, metric) is { } latest)
        {
            current = latest.Value;
            asOf = latest.Date;
            progress = GoalRules.ProgressPercent(g.BaselineValue, g.TargetValue, current);
            reached = GoalRules.Reached(g.BaselineValue, g.TargetValue, current);
        }
        else if (g.Category == GoalCategory.SessionsPerWeek && g.TargetValue is { } target)
        {
            current = sessionsLastWeek;
            asOf = today;
            progress = AdherenceRules.Percent(sessionsLastWeek, (int)target);
            reached = sessionsLastWeek >= target;
        }

        return new GoalDto(
            g.Id, g.MemberId, g.Title, g.Description, g.Category.ToString(), g.Metric?.ToString(), g.Metric is { } m ? GoalRules.UnitOf(m) : null,
            g.TargetValue, g.BaselineValue, current, asOf, progress, reached, g.StartDate, g.TargetDate,
            g.Status == GoalStatus.Active && g.TargetDate is { } due && due < today, g.Status.ToString(), g.ClosedOn, g.CloseNote, g.CreatedAt);
    }
}

internal sealed class CreateGoalHandler(WellnessDbContext db, IAccessContext access, IClock clock, SecurityAudit audit)
    : ICommandHandler<CreateGoalCommand, GoalDto>
{
    public async Task<Result<GoalDto>> HandleAsync(CreateGoalCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var scope = await MemberScope.ResolveAsync(db, actor.Value, command.MemberId, cancellationToken);
        if (scope.IsFailure || scope.Value.IsSelf)
        {
            return GoalErrors.MemberNotFound;
        }

        var member = scope.Value.Member;
        if (!member.IsActive)
        {
            return GoalErrors.MemberInactive;
        }

        var r = command.Request;
        var today = await LocalDates.TodayAtBranchAsync(db, clock, member.BranchId, cancellationToken);
        var metric = r.Category == GoalCategory.SessionsPerWeek ? GoalMetric.SessionsPerWeek : r.Metric;
        if (GoalRules.Validate(r.Category, r.Metric, r.TargetValue, today, r.TargetDate) is { } problem)
        {
            return Error.Validation(problem, "El objetivo no es válido: revise la medida, la meta y la fecha (máximo cinco años, posterior a hoy).");
        }

        var active = await db.Goals.AsNoTracking().Where(g => g.MemberId == member.Id && g.Status == GoalStatus.Active).ToListAsync(cancellationToken);
        if (active.Count >= Goal.MaxActivePerMember)
        {
            return GoalErrors.TooManyActive;
        }

        if (metric is not null && active.Any(g => g.Metric == metric))
        {
            return GoalErrors.DuplicateMetric;
        }

        decimal? baseline = null;
        var assessments = await AssessmentMapper.LoadAsync(db, member, cancellationToken);
        if (r.Category == GoalCategory.BodyMetric && metric is { } bodyMetric)
        {
            baseline = r.BaselineValue ?? GoalMapper.Latest(assessments, bodyMetric)?.Value;
            if (bodyMetric == GoalMetric.WeightKg && r.TargetValue is { } targetKg)
            {
                var height = assessments.Where(a => !a.IsCorrected && a.HeightCm is not null).OrderByDescending(a => a.AssessedOn).ThenByDescending(a => a.CreatedAt).FirstOrDefault()?.HeightCm;
                if (GoalRules.WeightTargetIsUnsafe(targetKg, height, AssessmentRules.AgeOn(member.BirthDate, today)))
                {
                    return GoalErrors.TargetUnsafe;
                }
            }
        }

        var goal = Goal.Create(actor.Value.TenantId, member.Id, r.Title, r.Description, r.Category, metric, r.TargetValue, baseline, today, r.TargetDate, actor.Value.UserId, clock.UtcNow);
        db.Goals.Add(goal);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAccessAsync("Wellness.GoalCreated", "Member", member.Id.ToString(), cancellationToken);
        return (await GoalMapper.ToDtosAsync(db, member, [goal], today, cancellationToken))[0];
    }
}

internal sealed class CloseGoalHandler(WellnessDbContext db, IAccessContext access, IClock clock, SecurityAudit audit)
    : ICommandHandler<CloseGoalCommand, GoalDto>
{
    public async Task<Result<GoalDto>> HandleAsync(CloseGoalCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var goal = await db.Goals.FirstOrDefaultAsync(g => g.Id == command.Id, cancellationToken);
        if (goal is null)
        {
            return GoalErrors.NotFound;
        }

        var scope = await MemberScope.ResolveAsync(db, actor.Value, goal.MemberId, cancellationToken);
        if (scope.IsFailure || scope.Value.IsSelf)
        {
            return GoalErrors.NotFound;
        }

        if (goal.Status != GoalStatus.Active)
        {
            return GoalErrors.AlreadyClosed;
        }

        var today = await LocalDates.TodayAtBranchAsync(db, clock, scope.Value.Member.BranchId, cancellationToken);
        goal.Close(command.Request.Status, today, command.Request.Note);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAccessAsync("Wellness.GoalClosed", "Member", goal.MemberId.ToString(), cancellationToken);
        return (await GoalMapper.ToDtosAsync(db, scope.Value.Member, [goal], today, cancellationToken))[0];
    }
}

internal sealed class ListGoalsHandler(WellnessDbContext db, IAccessContext access, IClock clock, SecurityAudit audit)
    : IQueryHandler<ListGoalsQuery, IReadOnlyList<GoalDto>>
{
    public async Task<Result<IReadOnlyList<GoalDto>>> HandleAsync(ListGoalsQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var scope = await MemberScope.ResolveAsync(db, actor.Value, query.MemberId, cancellationToken);
        if (scope.IsFailure)
        {
            return GoalErrors.MemberNotFound;
        }

        var goals = db.Goals.AsNoTracking().Where(g => g.MemberId == query.MemberId);
        if (query.Status is { } status)
        {
            goals = goals.Where(g => g.Status == status);
        }

        var list = await goals.OrderBy(g => g.Status).ThenBy(g => g.TargetDate == null).ThenBy(g => g.TargetDate).ThenByDescending(g => g.CreatedAt).ToListAsync(cancellationToken);
        var today = await LocalDates.TodayAtBranchAsync(db, clock, scope.Value.Member.BranchId, cancellationToken);
        if (!scope.Value.IsSelf)
        {
            await audit.RecordAccessAsync("Wellness.GoalsRead", "Member", query.MemberId.ToString(), cancellationToken);
        }

        return (await GoalMapper.ToDtosAsync(db, scope.Value.Member, list, today, cancellationToken)).ToList();
    }
}
