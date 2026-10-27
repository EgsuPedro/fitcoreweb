using FitCore.SharedKernel;

namespace FitCore.Commercial.Domain;

public enum DiscountType
{
    Percentage = 1,
    FixedAmount = 2
}

/// <summary>Plan al que aplica una promoción. Sin filas, la promoción aplica a todos los planes.</summary>
public sealed class PromotionPlan : ITenantOwned, IAuditable
{
    public Guid TenantId { get; init; }
    public Guid PromotionId { get; init; }
    public Guid PlanId { get; init; }
}

public enum PromotionStatus
{
    Active = 1,
    Scheduled = 2,
    Expired = 3,
    Exhausted = 4,
    Inactive = 5
}

public sealed record PromotionDetails(
    string Name,
    string? Code,
    DiscountType DiscountType,
    decimal Value,
    DateOnly ValidFrom,
    DateOnly ValidUntil,
    int? MaxUses,
    string? Restrictions,
    bool IsActive);

/// <summary>Promoción con vigencia y usos limitados. Los topes de descuento se validan en los casos de uso.</summary>
public sealed class Promotion : Entity<Guid>, ITenantOwned, IAuditable
{
    private readonly List<PromotionPlan> _plans = [];

    private Promotion(Guid id)
        : base(id)
    {
    }

    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = string.Empty;

    /// <summary>Código opcional que digita quien aplica la promoción (se guarda en mayúsculas).</summary>
    public string? Code { get; private set; }

    public DiscountType DiscountType { get; private set; }
    public decimal Value { get; private set; }
    public DateOnly ValidFrom { get; private set; }
    public DateOnly ValidUntil { get; private set; }
    public int? MaxUses { get; private set; }

    /// <summary>Usos ya consumidos. Solo cambia por un incremento atómico en la base al aplicarla a una membresía.</summary>
    public int UsedCount { get; private set; }

    public string? Restrictions { get; private set; }
    public bool IsActive { get; private set; } = true;
    public DateTimeOffset CreatedAt { get; private set; }
    public Guid? CreatedBy { get; private set; }
    public DateTimeOffset? UpdatedAt { get; private set; }
    public Guid? UpdatedBy { get; private set; }

    public IReadOnlyCollection<PromotionPlan> Plans => _plans;

    public static Promotion Create(Guid tenantId, PromotionDetails details, IEnumerable<Guid> planIds, Guid? createdBy, DateTimeOffset now)
    {
        var promotion = new Promotion(Guid.CreateVersion7())
        {
            TenantId = tenantId,
            CreatedAt = now,
            CreatedBy = createdBy
        };
        promotion.Apply(details);
        promotion.SetPlans(planIds);
        return promotion;
    }

    public void Update(PromotionDetails details, IEnumerable<Guid> planIds, Guid? updatedBy, DateTimeOffset now)
    {
        Apply(details);
        SetPlans(planIds);
        UpdatedAt = now;
        UpdatedBy = updatedBy;
    }

    public PromotionStatus StatusOn(DateOnly date) =>
        !IsActive ? PromotionStatus.Inactive
        : date < ValidFrom ? PromotionStatus.Scheduled
        : date > ValidUntil ? PromotionStatus.Expired
        : MaxUses is { } max && UsedCount >= max ? PromotionStatus.Exhausted
        : PromotionStatus.Active;

    public bool AppliesToPlan(Guid planId) => _plans.Count == 0 || _plans.Any(p => p.PlanId == planId);

    /// <summary>Descuento sobre un precio: nunca negativo ni superior al precio.</summary>
    public decimal DiscountFor(decimal price) =>
        Math.Min(price, DiscountType == DiscountType.Percentage ? Money.Round(price * Value / 100m) : Money.Round(Value));

    private void Apply(PromotionDetails d)
    {
        Name = d.Name.Trim();
        Code = string.IsNullOrWhiteSpace(d.Code) ? null : d.Code.Trim().ToUpperInvariant();
        DiscountType = d.DiscountType;
        Value = Money.Round(d.Value);
        ValidFrom = d.ValidFrom;
        ValidUntil = d.ValidUntil;
        MaxUses = d.MaxUses;
        Restrictions = string.IsNullOrWhiteSpace(d.Restrictions) ? null : d.Restrictions.Trim();
        IsActive = d.IsActive;
    }

    private void SetPlans(IEnumerable<Guid> planIds)
    {
        var wanted = planIds.Distinct().ToHashSet();
        _plans.RemoveAll(p => !wanted.Contains(p.PlanId));
        foreach (var id in wanted.Where(id => _plans.All(p => p.PlanId != id)))
        {
            _plans.Add(new PromotionPlan { TenantId = TenantId, PromotionId = Id, PlanId = id });
        }
    }
}
