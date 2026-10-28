using FitCore.Application.Core;
using FitCore.Platform.Security;
using FitCore.SharedKernel;
using FitCore.Wellness.Domain;
using FitCore.Wellness.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FitCore.Wellness.Application.Nutrition;

internal sealed record BuiltNutrition(NutritionPlanVersion Version, IReadOnlyList<NutritionMeal> Meals, IReadOnlyList<NutritionMealItem> Items);

internal static class NutritionContent
{
    /// <summary>Valida y arma una versión de plan nutricional. Los valores son orientativos y se comprueban solo por coherencia.</summary>
    public static Result<BuiltNutrition> Build(Guid tenantId, Guid planId, int versionNumber, NutritionPlanRequest request, Guid? userId, DateTimeOffset now)
    {
        var targets = new NutritionTargets(request.TargetCalories, request.TargetProteinG, request.TargetCarbsG, request.TargetFatG);
        if (NutritionRules.ValidateTargets(targets) is { } targetsProblem)
        {
            return Error.Validation(targetsProblem, "Los objetivos del plan no son válidos (calorías entre 300 y 10000; gramos entre 0 y 1000).");
        }

        var requested = request.Meals ?? [];
        if (requested.Select(m => m.Name.Trim().ToUpperInvariant()).Distinct().Count() != requested.Count)
        {
            return NutritionErrors.DuplicateMeal;
        }

        var header = new NutritionHeader(request.Name, request.Goal, request.Description, targets, request.Recommendations, request.ValidFrom, request.ValidUntil);
        var version = NutritionPlanVersion.Create(tenantId, planId, versionNumber, header, request.ChangeNote, userId, now);
        var meals = new List<NutritionMeal>();
        var items = new List<NutritionMealItem>();
        var mealPosition = 0;
        foreach (var mealRequest in requested)
        {
            var meal = NutritionMeal.Create(tenantId, version.Id, ++mealPosition, mealRequest.Name, mealRequest.SuggestedTime, mealRequest.Notes);
            meals.Add(meal);
            var position = 0;
            foreach (var i in mealRequest.Items ?? [])
            {
                var portion = new FoodPortion(i.FoodName, i.Quantity, i.Unit ?? string.Empty, new FoodNutrients(i.Calories, i.ProteinG, i.CarbsG, i.FatG), i.Notes);
                if (NutritionRules.ValidatePortion(portion) is { } problem)
                {
                    return Error.Validation(problem, $"«{i.FoodName}»: la cantidad, la unidad o los valores nutricionales no son válidos.");
                }

                items.Add(NutritionMealItem.Create(tenantId, meal.Id, ++position, portion));
            }
        }

        return new BuiltNutrition(version, meals, items);
    }

    public static async Task<NutritionPlanDetailDto> ToDetailAsync(WellnessDbContext db, NutritionPlan plan, NutritionPlanVersion version, CancellationToken cancellationToken)
    {
        var meals = await db.NutritionMeals.AsNoTracking().Where(m => m.VersionId == version.Id).OrderBy(m => m.Position).ToListAsync(cancellationToken);
        var mealIds = meals.Select(m => m.Id).ToList();
        var items = await db.NutritionMealItems.AsNoTracking().Where(i => mealIds.Contains(i.MealId)).OrderBy(i => i.Position).ToListAsync(cancellationToken);
        var byMeal = items.ToLookup(i => i.MealId);
        return new NutritionPlanDetailDto(
            plan.Id,
            plan.Status.ToString(),
            version.Number,
            version.Id,
            plan.CurrentVersionId == version.Id,
            version.Name,
            version.Goal,
            version.Description,
            version.TargetCalories,
            version.TargetProteinG,
            version.TargetCarbsG,
            version.TargetFatG,
            version.Recommendations,
            version.ValidFrom,
            version.ValidUntil,
            version.ChangeNote,
            [.. meals.Select(m => new MealDto(m.Id, m.Position, m.Name, m.SuggestedTime, m.Notes, NutritionRules.EstimatedCalories(byMeal[m.Id].Select(i => i.Calories)), [.. byMeal[m.Id].Select(ToDto)]))],
            version.CreatedAt,
            plan.CreatedAt,
            plan.UpdatedAt);
    }

    public static MealItemDto ToDto(NutritionMealItem i) => new(i.Id, i.Position, i.FoodName, i.Quantity, i.Unit, i.Calories, i.ProteinG, i.CarbsG, i.FatG, i.Notes);

    public static async Task SaveContentAsync(WellnessDbContext db, BuiltNutrition content, CancellationToken cancellationToken)
    {
        // Versión, comidas y alimentos se guardan por etapas: cada nivel referencia al anterior y la base los exige en ese orden.
        db.NutritionVersions.Add(content.Version);
        await db.SaveChangesAsync(cancellationToken);
        db.NutritionMeals.AddRange(content.Meals);
        await db.SaveChangesAsync(cancellationToken);
        db.NutritionMealItems.AddRange(content.Items);
        await db.SaveChangesAsync(cancellationToken);
    }
}

internal sealed class ListNutritionPlansHandler(WellnessDbContext db, IAccessContext access) : IQueryHandler<ListNutritionPlansQuery, PagedResult<NutritionPlanSummaryDto>>
{
    public async Task<Result<PagedResult<NutritionPlanSummaryDto>>> HandleAsync(ListNutritionPlansQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var plans = db.NutritionPlans.AsNoTracking()
            .Join(db.NutritionVersions.AsNoTracking(), p => p.CurrentVersionId, v => v.Id, (p, v) => new { Plan = p, Version = v });
        if (query.Status is { } status)
        {
            plans = plans.Where(x => x.Plan.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var pattern = LikePattern.Contains(query.Search);
            plans = plans.Where(x => EF.Functions.ILike(x.Version.Name, pattern) || (x.Version.Goal != null && EF.Functions.ILike(x.Version.Goal, pattern)));
        }

        var paged = await plans.OrderBy(x => x.Version.Name).ThenBy(x => x.Plan.Id).ToPagedAsync(query.Page, cancellationToken);
        var items = await NutritionSummaries.BuildAsync(db, [.. paged.Items.Select(x => (x.Plan, x.Version))], cancellationToken);
        return new PagedResult<NutritionPlanSummaryDto>(items, paged.Page, paged.PageSize, paged.TotalCount);
    }
}

internal static class NutritionSummaries
{
    public static async Task<IReadOnlyList<NutritionPlanSummaryDto>> BuildAsync(
        WellnessDbContext db,
        IReadOnlyList<(NutritionPlan Plan, NutritionPlanVersion Version)> pairs,
        CancellationToken cancellationToken)
    {
        var versionIds = pairs.Select(x => x.Version.Id).ToList();
        var planIds = pairs.Select(x => x.Plan.Id).ToList();
        var mealCounts = await db.NutritionMeals.AsNoTracking().Where(m => versionIds.Contains(m.VersionId))
            .GroupBy(m => m.VersionId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);
        var assignments = await db.NutritionAssignments.AsNoTracking().Where(a => planIds.Contains(a.PlanId) && a.EndedOn == null)
            .GroupBy(a => a.PlanId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);

        return [.. pairs.Select(x => new NutritionPlanSummaryDto(
            x.Plan.Id,
            x.Version.Name,
            x.Version.Goal,
            x.Version.TargetCalories,
            x.Plan.Status.ToString(),
            x.Version.Number,
            x.Version.ValidFrom,
            x.Version.ValidUntil,
            mealCounts.GetValueOrDefault(x.Version.Id),
            assignments.GetValueOrDefault(x.Plan.Id),
            x.Plan.CreatedAt,
            x.Plan.UpdatedAt))];
    }
}

internal sealed class GetNutritionPlanHandler(WellnessDbContext db, IAccessContext access) : IQueryHandler<GetNutritionPlanQuery, NutritionPlanDetailDto>
{
    public async Task<Result<NutritionPlanDetailDto>> HandleAsync(GetNutritionPlanQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var plan = await db.NutritionPlans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == query.Id, cancellationToken);
        if (plan is null)
        {
            return NutritionErrors.PlanNotFound;
        }

        var version = query.Version is { } number
            ? await db.NutritionVersions.AsNoTracking().FirstOrDefaultAsync(v => v.PlanId == plan.Id && v.Number == number, cancellationToken)
            : await db.NutritionVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == plan.CurrentVersionId, cancellationToken);
        return version is null ? NutritionErrors.VersionNotFound : await NutritionContent.ToDetailAsync(db, plan, version, cancellationToken);
    }
}

internal sealed class ListNutritionVersionsHandler(WellnessDbContext db, IAccessContext access) : IQueryHandler<ListNutritionVersionsQuery, IReadOnlyList<NutritionVersionDto>>
{
    public async Task<Result<IReadOnlyList<NutritionVersionDto>>> HandleAsync(ListNutritionVersionsQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        if (!await db.NutritionPlans.AsNoTracking().AnyAsync(p => p.Id == query.Id, cancellationToken))
        {
            return NutritionErrors.PlanNotFound;
        }

        var versions = await db.NutritionVersions.AsNoTracking().Where(v => v.PlanId == query.Id).OrderByDescending(v => v.Number).ToListAsync(cancellationToken);
        return versions.Select(v => new NutritionVersionDto(v.Id, v.Number, v.Name, v.ChangeNote, v.CreatedAt, v.CreatedBy)).ToList();
    }
}

internal sealed class CreateNutritionPlanHandler(WellnessDbContext db, IAccessContext access, IClock clock, ICurrentUser currentUser)
    : ICommandHandler<CreateNutritionPlanCommand, NutritionPlanDetailDto>
{
    public async Task<Result<NutritionPlanDetailDto>> HandleAsync(CreateNutritionPlanCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var now = clock.UtcNow;
        var plan = NutritionPlan.Create(actor.Value.TenantId, currentUser.UserId, now);
        var content = NutritionContent.Build(actor.Value.TenantId, plan.Id, 1, command.Request, currentUser.UserId, now);
        if (content.IsFailure)
        {
            return content.Error;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.NutritionPlans.Add(plan);
        await db.SaveChangesAsync(cancellationToken);
        await NutritionContent.SaveContentAsync(db, content.Value, cancellationToken);
        plan.PublishVersion(content.Value.Version, currentUser.UserId, now);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await NutritionContent.ToDetailAsync(db, plan, content.Value.Version, cancellationToken);
    }
}

internal sealed class UpdateNutritionPlanHandler(WellnessDbContext db, IAccessContext access, IClock clock, ICurrentUser currentUser)
    : ICommandHandler<UpdateNutritionPlanCommand, NutritionPlanDetailDto>
{
    public async Task<Result<NutritionPlanDetailDto>> HandleAsync(UpdateNutritionPlanCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var plan = await db.NutritionPlans.FirstOrDefaultAsync(p => p.Id == command.Id, cancellationToken);
        if (plan is null)
        {
            return NutritionErrors.PlanNotFound;
        }

        if (plan.Status == PlanStatus.Archived)
        {
            return NutritionErrors.PlanArchived;
        }

        var now = clock.UtcNow;
        var content = NutritionContent.Build(actor.Value.TenantId, plan.Id, plan.CurrentVersion + 1, command.Request, currentUser.UserId, now);
        if (content.IsFailure)
        {
            return content.Error;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await NutritionContent.SaveContentAsync(db, content.Value, cancellationToken);
        plan.PublishVersion(content.Value.Version, currentUser.UserId, now);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict("conflict.concurrency", "El plan cambió mientras lo editaba. Recárguelo e intente de nuevo.");
        }

        await transaction.CommitAsync(cancellationToken);
        return await NutritionContent.ToDetailAsync(db, plan, content.Value.Version, cancellationToken);
    }
}

internal sealed class DuplicateNutritionPlanHandler(WellnessDbContext db, IAccessContext access, IClock clock, ICurrentUser currentUser)
    : ICommandHandler<DuplicateNutritionPlanCommand, NutritionPlanDetailDto>
{
    public async Task<Result<NutritionPlanDetailDto>> HandleAsync(DuplicateNutritionPlanCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var source = await db.NutritionPlans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == command.Id, cancellationToken);
        var version = source is null ? null : await db.NutritionVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == source.CurrentVersionId, cancellationToken);
        if (source is null || version is null)
        {
            return NutritionErrors.PlanNotFound;
        }

        var detail = await NutritionContent.ToDetailAsync(db, source, version, cancellationToken);
        var request = new NutritionPlanRequest(
            $"{detail.Name} (copia)",
            detail.Goal,
            detail.Description,
            detail.TargetCalories,
            detail.TargetProteinG,
            detail.TargetCarbsG,
            detail.TargetFatG,
            detail.Recommendations,
            detail.ValidFrom,
            detail.ValidUntil,
            $"Copia de «{detail.Name}» versión {detail.Version}",
            [.. detail.Meals.Select(m => new MealRequest(m.Name, m.SuggestedTime, m.Notes, [.. m.Items.Select(i => new MealItemRequest(i.FoodName, i.Quantity, i.Unit, i.Calories, i.ProteinG, i.CarbsG, i.FatG, i.Notes))]))]);

        var now = clock.UtcNow;
        var plan = NutritionPlan.Create(actor.Value.TenantId, currentUser.UserId, now);
        var content = NutritionContent.Build(actor.Value.TenantId, plan.Id, 1, request, currentUser.UserId, now);
        if (content.IsFailure)
        {
            return content.Error;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.NutritionPlans.Add(plan);
        await db.SaveChangesAsync(cancellationToken);
        await NutritionContent.SaveContentAsync(db, content.Value, cancellationToken);
        plan.PublishVersion(content.Value.Version, currentUser.UserId, now);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await NutritionContent.ToDetailAsync(db, plan, content.Value.Version, cancellationToken);
    }
}

internal sealed class SetNutritionPlanStatusHandler(WellnessDbContext db, IAccessContext access, IClock clock, ICurrentUser currentUser)
    : ICommandHandler<SetNutritionPlanStatusCommand, NutritionPlanSummaryDto>
{
    public async Task<Result<NutritionPlanSummaryDto>> HandleAsync(SetNutritionPlanStatusCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var plan = await db.NutritionPlans.FirstOrDefaultAsync(p => p.Id == command.Id, cancellationToken);
        if (plan is null)
        {
            return NutritionErrors.PlanNotFound;
        }

        // Archivar no toca lo ya asignado ni registrado: solo deja de ofrecerse para nuevas asignaciones.
        plan.SetStatus(command.Request.Active ? PlanStatus.Active : PlanStatus.Archived, currentUser.UserId, clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);

        var version = await db.NutritionVersions.AsNoTracking().FirstAsync(v => v.Id == plan.CurrentVersionId, cancellationToken);
        return (await NutritionSummaries.BuildAsync(db, [(plan, version)], cancellationToken))[0];
    }
}
