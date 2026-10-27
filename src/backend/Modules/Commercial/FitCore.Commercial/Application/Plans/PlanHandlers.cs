using FitCore.Application.Core;
using FitCore.Commercial.Application.Settings;
using FitCore.Commercial.Domain;
using FitCore.Commercial.Persistence;
using FitCore.Platform.Application;
using FitCore.Platform.Security;
using FitCore.SharedKernel;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace FitCore.Commercial.Application.Plans;

public sealed record PlanDto(
    Guid Id,
    string Name,
    string? Description,
    decimal Price,
    string Currency,
    int DurationValue,
    string DurationUnit,
    string? AllowedAccess,
    string? Restrictions,
    string? CommercialTerms,
    string TaxKind,
    decimal TaxPercent,
    decimal Subtotal,
    decimal TaxAmount,
    bool IsActive,
    DateOnly? ValidFrom,
    DateOnly? ValidUntil,
    bool AllowFreeze,
    int MaxFreezeDays,
    IReadOnlyList<Guid> BranchIds,
    DateTimeOffset CreatedAt,
    Guid? CreatedBy,
    DateTimeOffset? UpdatedAt,
    Guid? UpdatedBy)
{
    /// <summary>El desglose de IVA es informativo: usa la tarifa vigente hoy. Cada obligación congela la suya al crearse.</summary>
    public static PlanDto From(MembershipPlan p, decimal taxPercent)
    {
        var (subtotal, tax) = TaxRate.Split(p.Price, taxPercent);
        return new PlanDto(
            p.Id,
            p.Name,
            p.Description,
            p.Price,
            MembershipPlan.Currency,
            p.DurationValue,
            p.DurationUnit.ToString(),
            p.AllowedAccess,
            p.Restrictions,
            p.CommercialTerms,
            p.TaxKind.ToString(),
            taxPercent,
            subtotal,
            tax,
            p.IsActive,
            p.ValidFrom,
            p.ValidUntil,
            p.AllowFreeze,
            p.MaxFreezeDays,
            [.. p.Branches.Select(b => b.BranchId).Order()],
            p.CreatedAt,
            p.CreatedBy,
            p.UpdatedAt,
            p.UpdatedBy);
    }
}

/// <summary>Cuerpo de crear y editar un plan. <c>BranchIds</c> vacío significa "todas las sucursales".</summary>
public sealed record PlanRequest(
    string Name,
    string? Description,
    decimal Price,
    int DurationValue,
    DurationUnit DurationUnit,
    string? AllowedAccess,
    string? Restrictions,
    string? CommercialTerms,
    TaxKind TaxKind,
    bool IsActive,
    DateOnly? ValidFrom,
    DateOnly? ValidUntil,
    bool AllowFreeze,
    int MaxFreezeDays,
    IReadOnlyList<Guid>? BranchIds)
{
    public PlanDetails ToDetails() =>
        new(Name, Description, Price, DurationValue, DurationUnit, AllowedAccess, Restrictions, CommercialTerms, TaxKind, IsActive, ValidFrom, ValidUntil, AllowFreeze, MaxFreezeDays);
}

public sealed record ListPlansQuery(PageRequest Page, string? Search, bool? IsActive, Guid? BranchId) : IQuery<PagedResult<PlanDto>>;

public sealed record GetPlanQuery(Guid Id) : IQuery<PlanDto>;

public sealed record CreatePlanCommand(PlanRequest Request) : ICommand<PlanDto>;

public sealed record UpdatePlanCommand(Guid Id, PlanRequest Request) : ICommand<PlanDto>;

public sealed class PlanRequestValidator : AbstractValidator<PlanRequest>
{
    public PlanRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Description).MaximumLength(500);
        RuleFor(x => x.Price).GreaterThan(0m).LessThanOrEqualTo(MembershipPlan.MaxPrice).PrecisionScale(12, 2, ignoreTrailingZeros: true);
        RuleFor(x => x.DurationUnit).IsInEnum();
        RuleFor(x => x.DurationValue).GreaterThanOrEqualTo(1);
        RuleFor(x => x.DurationValue)
            .Must((request, value) => value <= MaxDuration(request.DurationUnit))
            .When(x => Enum.IsDefined(x.DurationUnit))
            .WithMessage("La duración es demasiado larga para esa unidad.");
        RuleFor(x => x.TaxKind).IsInEnum();
        RuleFor(x => x.AllowedAccess).MaximumLength(500);
        RuleFor(x => x.Restrictions).MaximumLength(500);
        RuleFor(x => x.CommercialTerms).MaximumLength(1000);
        RuleFor(x => x.ValidUntil).GreaterThanOrEqualTo(x => x.ValidFrom!.Value)
            .When(x => x.ValidFrom is not null && x.ValidUntil is not null)
            .WithMessage("La vigencia termina antes de empezar.");
        RuleFor(x => x.MaxFreezeDays).InclusiveBetween(0, 365);
        RuleFor(x => x.MaxFreezeDays).GreaterThan(0).When(x => x.AllowFreeze).WithMessage("Indique cuántos días se puede congelar.");
        RuleFor(x => x.BranchIds).Must(ids => ids!.Distinct().Count() == ids!.Count).When(x => x.BranchIds is not null)
            .WithMessage("Hay sucursales repetidas.");
    }

    private static int MaxDuration(DurationUnit unit) => unit switch
    {
        DurationUnit.Day => 365,
        DurationUnit.Week => 104,
        DurationUnit.Month => 60,
        _ => 10
    };
}

internal static class PlanErrors
{
    public static readonly Error NotFound = Error.NotFound("plans.not_found", "Plan no encontrado.");
    public static readonly Error NameTaken = Error.Conflict("plans.name_taken", "Ya existe un plan con ese nombre.");
    public static readonly Error ScopeNotAllowed = Error.Forbidden("plans.scope_not_allowed", "No puede administrar planes de sucursales fuera de su alcance.");
    public static readonly Error BranchesRequired = Error.Validation("plans.branches_required", "Indique en qué sucursales de su alcance se vende el plan.");
}

internal static class PlanQueries
{
    /// <summary>Planes que el usuario puede ver: los de todas las sucursales o los que se venden en alguna de las suyas.</summary>
    public static IQueryable<MembershipPlan> VisibleTo(this IQueryable<MembershipPlan> plans, UserAccess actor)
    {
        if (actor.AllBranches)
        {
            return plans;
        }

        var ids = actor.BranchIds.ToList();
        return plans.Where(p => !p.Branches.Any() || p.Branches.Any(b => ids.Contains(b.BranchId)));
    }

    /// <summary>
    /// ¿Puede el usuario administrar este plan? Quien no accede a todas las sucursales solo administra planes
    /// que se venden exclusivamente en sucursales de su alcance.
    /// </summary>
    public static bool CanManage(UserAccess actor, IEnumerable<Guid> planBranchIds)
    {
        if (actor.AllBranches)
        {
            return true;
        }

        var ids = planBranchIds.ToList();
        return ids.Count > 0 && ids.All(actor.CanAccessBranch);
    }

    public static async Task<decimal> TaxPercentAsync(CommercialDbContext db, TaxKind kind, DateOnly today, CancellationToken cancellationToken)
    {
        var rates = await TaxRateLookup.ForDateAsync(db, today, cancellationToken);
        return rates.TryGetValue(kind, out var rate) ? rate.Percent : 0m;
    }

    public static async Task<Dictionary<TaxKind, decimal>> TaxPercentsAsync(CommercialDbContext db, DateOnly today, CancellationToken cancellationToken) =>
        (await TaxRateLookup.ForDateAsync(db, today, cancellationToken)).ToDictionary(r => r.Key, r => r.Value.Percent);
}

internal sealed class ListPlansHandler(CommercialDbContext db, IAccessContext access, IClock clock)
    : IQueryHandler<ListPlansQuery, PagedResult<PlanDto>>
{
    public async Task<Result<PagedResult<PlanDto>>> HandleAsync(ListPlansQuery query, CancellationToken cancellationToken)
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

        var plans = db.Plans.AsNoTracking().Include(p => p.Branches).VisibleTo(actor.Value);
        if (query.IsActive is { } isActive)
        {
            plans = plans.Where(p => p.IsActive == isActive);
        }

        if (query.BranchId is { } branchId)
        {
            plans = plans.Where(p => !p.Branches.Any() || p.Branches.Any(b => b.BranchId == branchId));
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var pattern = LikePattern.Contains(query.Search);
            plans = plans.Where(p => EF.Functions.ILike(p.Name, pattern));
        }

        var paged = await plans.OrderBy(p => p.Name).ToPagedAsync(query.Page, cancellationToken);
        var percents = await PlanQueries.TaxPercentsAsync(db, LocalDates.Today(clock), cancellationToken);
        return new PagedResult<PlanDto>(
            [.. paged.Items.Select(p => PlanDto.From(p, percents.GetValueOrDefault(p.TaxKind)))],
            paged.Page,
            paged.PageSize,
            paged.TotalCount);
    }
}

internal sealed class GetPlanHandler(CommercialDbContext db, IAccessContext access, IClock clock) : IQueryHandler<GetPlanQuery, PlanDto>
{
    public async Task<Result<PlanDto>> HandleAsync(GetPlanQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var plan = await db.Plans.AsNoTracking().Include(p => p.Branches).VisibleTo(actor.Value).FirstOrDefaultAsync(p => p.Id == query.Id, cancellationToken);
        if (plan is null)
        {
            return PlanErrors.NotFound;
        }

        return PlanDto.From(plan, await PlanQueries.TaxPercentAsync(db, plan.TaxKind, LocalDates.Today(clock), cancellationToken));
    }
}

internal sealed class CreatePlanHandler(CommercialDbContext db, IAccessContext access, IClock clock, ICurrentUser currentUser)
    : ICommandHandler<CreatePlanCommand, PlanDto>
{
    public async Task<Result<PlanDto>> HandleAsync(CreatePlanCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var request = command.Request;
        var branchIds = request.BranchIds ?? [];
        var scope = await ValidateScopeAsync(db, actor.Value, branchIds, cancellationToken);
        if (scope.IsFailure)
        {
            return scope.Error;
        }

        if (await db.Plans.AnyAsync(p => EF.Functions.ILike(p.Name, LikePattern.Exact(request.Name)), cancellationToken))
        {
            return PlanErrors.NameTaken;
        }

        var plan = MembershipPlan.Create(actor.Value.TenantId, request.ToDetails(), branchIds, currentUser.UserId, clock.UtcNow);
        db.Plans.Add(plan);
        await db.SaveChangesAsync(cancellationToken);
        return PlanDto.From(plan, await PlanQueries.TaxPercentAsync(db, plan.TaxKind, LocalDates.Today(clock), cancellationToken));
    }

    internal static async Task<Result> ValidateScopeAsync(CommercialDbContext db, UserAccess actor, IReadOnlyList<Guid> branchIds, CancellationToken cancellationToken)
    {
        if (!actor.AllBranches && branchIds.Count == 0)
        {
            return Result.Failure(PlanErrors.BranchesRequired);
        }

        foreach (var branchId in branchIds)
        {
            var branch = await db.ResolveBranchAsync(actor, branchId, cancellationToken, requireActive: false);
            if (branch.IsFailure)
            {
                return Result.Failure(branch.Error);
            }
        }

        return Result.Success();
    }
}

internal sealed class UpdatePlanHandler(CommercialDbContext db, IAccessContext access, IClock clock, ICurrentUser currentUser)
    : ICommandHandler<UpdatePlanCommand, PlanDto>
{
    public async Task<Result<PlanDto>> HandleAsync(UpdatePlanCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var plan = await db.Plans.Include(p => p.Branches).VisibleTo(actor.Value).FirstOrDefaultAsync(p => p.Id == command.Id, cancellationToken);
        if (plan is null)
        {
            return PlanErrors.NotFound;
        }

        if (!PlanQueries.CanManage(actor.Value, plan.Branches.Select(b => b.BranchId)))
        {
            return PlanErrors.ScopeNotAllowed;
        }

        var request = command.Request;
        var branchIds = request.BranchIds ?? [];
        var scope = await CreatePlanHandler.ValidateScopeAsync(db, actor.Value, branchIds, cancellationToken);
        if (scope.IsFailure)
        {
            return scope.Error;
        }

        if (await db.Plans.AnyAsync(p => p.Id != plan.Id && EF.Functions.ILike(p.Name, LikePattern.Exact(request.Name)), cancellationToken))
        {
            return PlanErrors.NameTaken;
        }

        // Las membresías ya contratadas conservan su propia copia de precio y condiciones: esta edición no las toca.
        plan.Update(request.ToDetails(), branchIds, currentUser.UserId, clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        return PlanDto.From(plan, await PlanQueries.TaxPercentAsync(db, plan.TaxKind, LocalDates.Today(clock), cancellationToken));
    }
}
