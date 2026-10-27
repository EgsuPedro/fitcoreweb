using System.Text.RegularExpressions;
using FitCore.Application.Core;
using FitCore.Commercial.Application.Settings;
using FitCore.Commercial.Domain;
using FitCore.Commercial.Persistence;
using FitCore.Platform.Application;
using FitCore.Platform.Security;
using FitCore.SharedKernel;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace FitCore.Commercial.Application.Promotions;

public sealed record PromotionDto(
    Guid Id,
    string Name,
    string? Code,
    string DiscountType,
    decimal Value,
    DateOnly ValidFrom,
    DateOnly ValidUntil,
    int? MaxUses,
    int UsedCount,
    string? Restrictions,
    bool IsActive,
    string Status,
    IReadOnlyList<Guid> PlanIds,
    DateTimeOffset CreatedAt,
    Guid? CreatedBy,
    DateTimeOffset? UpdatedAt,
    Guid? UpdatedBy)
{
    public static PromotionDto From(Promotion p, DateOnly today) =>
        new(
            p.Id,
            p.Name,
            p.Code,
            p.DiscountType.ToString(),
            p.Value,
            p.ValidFrom,
            p.ValidUntil,
            p.MaxUses,
            p.UsedCount,
            p.Restrictions,
            p.IsActive,
            p.StatusOn(today).ToString(),
            [.. p.Plans.Select(x => x.PlanId).Order()],
            p.CreatedAt,
            p.CreatedBy,
            p.UpdatedAt,
            p.UpdatedBy);
}

/// <summary>Cuerpo de crear y editar una promoción. <c>PlanIds</c> vacío significa "todos los planes".</summary>
public sealed record PromotionRequest(
    string Name,
    string? Code,
    DiscountType DiscountType,
    decimal Value,
    DateOnly ValidFrom,
    DateOnly ValidUntil,
    int? MaxUses,
    string? Restrictions,
    bool IsActive,
    IReadOnlyList<Guid>? PlanIds)
{
    public PromotionDetails ToDetails() => new(Name, Code, DiscountType, Value, ValidFrom, ValidUntil, MaxUses, Restrictions, IsActive);
}

public sealed record ListPromotionsQuery(PageRequest Page, string? Search, PromotionStatus? Status, Guid? PlanId) : IQuery<PagedResult<PromotionDto>>;

public sealed record GetPromotionQuery(Guid Id) : IQuery<PromotionDto>;

public sealed record CreatePromotionCommand(PromotionRequest Request) : ICommand<PromotionDto>;

public sealed record UpdatePromotionCommand(Guid Id, PromotionRequest Request) : ICommand<PromotionDto>;

public sealed partial class PromotionRequestValidator : AbstractValidator<PromotionRequest>
{
    public PromotionRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Code).Must(c => CodePattern().IsMatch(c!.Trim().ToUpperInvariant())).When(x => !string.IsNullOrWhiteSpace(x.Code))
            .WithMessage("El código usa 3 a 30 letras, números, guiones o guion bajo.");
        RuleFor(x => x.DiscountType).IsInEnum();
        RuleFor(x => x.Value).GreaterThan(0m).LessThanOrEqualTo(MembershipPlan.MaxPrice).PrecisionScale(12, 2, ignoreTrailingZeros: true);
        RuleFor(x => x.Value).LessThanOrEqualTo(100m).When(x => x.DiscountType == DiscountType.Percentage)
            .WithMessage("Un porcentaje no puede superar 100.");
        RuleFor(x => x.ValidUntil).GreaterThanOrEqualTo(x => x.ValidFrom).WithMessage("La vigencia termina antes de empezar.");
        RuleFor(x => x.MaxUses).GreaterThanOrEqualTo(1).When(x => x.MaxUses is not null);
        RuleFor(x => x.Restrictions).MaximumLength(500);
        RuleFor(x => x.PlanIds).Must(ids => ids!.Distinct().Count() == ids!.Count).When(x => x.PlanIds is not null)
            .WithMessage("Hay planes repetidos.");
    }

    [GeneratedRegex("^[A-Z0-9_-]{3,30}$")]
    private static partial Regex CodePattern();
}

internal static class PromotionErrors
{
    public static readonly Error NotFound = Error.NotFound("promotions.not_found", "Promoción no encontrada.");
    public static readonly Error CodeTaken = Error.Conflict("promotions.code_taken", "Ya existe una promoción con ese código.");
    public static readonly Error DiscountAboveMaximum = Error.Validation("promotions.discount_above_maximum", "El descuento supera el máximo autorizado para el gimnasio.");
    public static readonly Error MaxUsesBelowUsed = Error.Validation("promotions.max_uses_below_used", "Los usos permitidos no pueden ser menos que los ya consumidos.");
    public static readonly Error PlanNotFound = Error.NotFound("plans.not_found", "Plan no encontrado.");
}

internal static class PromotionRules
{
    /// <summary>
    /// El descuento no puede pasar del máximo autorizado. Un porcentaje se compara tal cual; un monto fijo se compara contra el
    /// precio de cada plan al que aplica (o de todos los planes, si no se limita a ninguno). Al aplicarla a una membresía se vuelve a validar.
    /// </summary>
    public static async Task<Result> ValidateAsync(
        CommercialDbContext db,
        Guid tenantId,
        PromotionRequest request,
        IReadOnlyList<Guid> planIds,
        CancellationToken cancellationToken)
    {
        var settings = await SettingsStore.GetAsync(db, tenantId, cancellationToken);

        var existing = await db.Plans.AsNoTracking().Where(p => planIds.Contains(p.Id)).Select(p => p.Id).ToListAsync(cancellationToken);
        if (existing.Count != planIds.Distinct().Count())
        {
            return Result.Failure(PromotionErrors.PlanNotFound);
        }

        if (request.DiscountType == DiscountType.Percentage)
        {
            return request.Value > settings.MaxDiscountPercent ? Result.Failure(PromotionErrors.DiscountAboveMaximum) : Result.Success();
        }

        var prices = db.Plans.AsNoTracking();
        if (planIds.Count > 0)
        {
            prices = prices.Where(p => planIds.Contains(p.Id));
        }

        var cheapest = await prices.Select(p => (decimal?)p.Price).MinAsync(cancellationToken);
        return cheapest is { } min && request.Value > Money.Round(min * settings.MaxDiscountPercent / 100m)
            ? Result.Failure(PromotionErrors.DiscountAboveMaximum)
            : Result.Success();
    }
}

internal sealed class ListPromotionsHandler(CommercialDbContext db, IAccessContext access, IClock clock)
    : IQueryHandler<ListPromotionsQuery, PagedResult<PromotionDto>>
{
    public async Task<Result<PagedResult<PromotionDto>>> HandleAsync(ListPromotionsQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var today = LocalDates.Today(clock);
        var promotions = db.Promotions.AsNoTracking().Include(p => p.Plans).AsQueryable();
        if (query.PlanId is { } planId)
        {
            promotions = promotions.Where(p => !p.Plans.Any() || p.Plans.Any(x => x.PlanId == planId));
        }

        if (query.Status is { } status)
        {
            promotions = status switch
            {
                PromotionStatus.Inactive => promotions.Where(p => !p.IsActive),
                PromotionStatus.Scheduled => promotions.Where(p => p.IsActive && today < p.ValidFrom),
                PromotionStatus.Expired => promotions.Where(p => p.IsActive && today > p.ValidUntil),
                PromotionStatus.Exhausted => promotions.Where(p => p.IsActive && today >= p.ValidFrom && today <= p.ValidUntil && p.MaxUses != null && p.UsedCount >= p.MaxUses),
                _ => promotions.Where(p => p.IsActive && today >= p.ValidFrom && today <= p.ValidUntil && (p.MaxUses == null || p.UsedCount < p.MaxUses))
            };
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var pattern = LikePattern.Contains(query.Search);
            promotions = promotions.Where(p => EF.Functions.ILike(p.Name, pattern) || (p.Code != null && EF.Functions.ILike(p.Code, pattern)));
        }

        var paged = await promotions.OrderByDescending(p => p.ValidFrom).ThenBy(p => p.Name).ToPagedAsync(query.Page, cancellationToken);
        return new PagedResult<PromotionDto>([.. paged.Items.Select(p => PromotionDto.From(p, today))], paged.Page, paged.PageSize, paged.TotalCount);
    }
}

internal sealed class GetPromotionHandler(CommercialDbContext db, IAccessContext access, IClock clock) : IQueryHandler<GetPromotionQuery, PromotionDto>
{
    public async Task<Result<PromotionDto>> HandleAsync(GetPromotionQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var promotion = await db.Promotions.AsNoTracking().Include(p => p.Plans).FirstOrDefaultAsync(p => p.Id == query.Id, cancellationToken);
        return promotion is null ? PromotionErrors.NotFound : PromotionDto.From(promotion, LocalDates.Today(clock));
    }
}

internal sealed class CreatePromotionHandler(CommercialDbContext db, IAccessContext access, IClock clock, ICurrentUser currentUser)
    : ICommandHandler<CreatePromotionCommand, PromotionDto>
{
    public async Task<Result<PromotionDto>> HandleAsync(CreatePromotionCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        // Los descuentos son una decisión de todo el gimnasio: quien solo administra sucursales no los define.
        if (!actor.Value.AllBranches)
        {
            return PlatformErrors.AllBranchesRequired;
        }

        var request = command.Request;
        var planIds = request.PlanIds ?? [];
        var valid = await PromotionRules.ValidateAsync(db, actor.Value.TenantId, request, planIds, cancellationToken);
        if (valid.IsFailure)
        {
            return valid.Error;
        }

        var code = string.IsNullOrWhiteSpace(request.Code) ? null : request.Code.Trim().ToUpperInvariant();
        if (code is not null && await db.Promotions.AnyAsync(p => p.Code == code, cancellationToken))
        {
            return PromotionErrors.CodeTaken;
        }

        var promotion = Promotion.Create(actor.Value.TenantId, request.ToDetails(), planIds, currentUser.UserId, clock.UtcNow);
        db.Promotions.Add(promotion);
        await db.SaveChangesAsync(cancellationToken);
        return PromotionDto.From(promotion, LocalDates.Today(clock));
    }
}

internal sealed class UpdatePromotionHandler(CommercialDbContext db, IAccessContext access, IClock clock, ICurrentUser currentUser)
    : ICommandHandler<UpdatePromotionCommand, PromotionDto>
{
    public async Task<Result<PromotionDto>> HandleAsync(UpdatePromotionCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        if (!actor.Value.AllBranches)
        {
            return PlatformErrors.AllBranchesRequired;
        }

        var promotion = await db.Promotions.Include(p => p.Plans).FirstOrDefaultAsync(p => p.Id == command.Id, cancellationToken);
        if (promotion is null)
        {
            return PromotionErrors.NotFound;
        }

        var request = command.Request;
        if (request.MaxUses is { } maxUses && maxUses < promotion.UsedCount)
        {
            return PromotionErrors.MaxUsesBelowUsed;
        }

        var planIds = request.PlanIds ?? [];
        var valid = await PromotionRules.ValidateAsync(db, actor.Value.TenantId, request, planIds, cancellationToken);
        if (valid.IsFailure)
        {
            return valid.Error;
        }

        var code = string.IsNullOrWhiteSpace(request.Code) ? null : request.Code.Trim().ToUpperInvariant();
        if (code is not null && await db.Promotions.AnyAsync(p => p.Id != promotion.Id && p.Code == code, cancellationToken))
        {
            return PromotionErrors.CodeTaken;
        }

        promotion.Update(request.ToDetails(), planIds, currentUser.UserId, clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        return PromotionDto.From(promotion, LocalDates.Today(clock));
    }
}
