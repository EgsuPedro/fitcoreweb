using FitCore.SharedKernel;

namespace FitCore.Commercial.Domain;

public enum DurationUnit
{
    Day = 1,
    Week = 2,
    Month = 3,
    Year = 4
}

/// <summary>Tipo de impuesto del plan. La tarifa concreta sale de <see cref="TaxRate"/> según la fecha.</summary>
public enum TaxKind
{
    General = 1,
    Zero = 2,
    NotSubject = 3,
    Exempt = 4
}

/// <summary>Sucursal en la que se vende un plan. Sin filas, el plan se vende en todas.</summary>
public sealed class PlanBranch : ITenantOwned, IAuditable
{
    public Guid TenantId { get; init; }
    public Guid PlanId { get; init; }
    public Guid BranchId { get; init; }
}

/// <summary>
/// Plan comercial. El precio es el precio final al cliente, con IVA incluido (ADR 0008). Los cambios del plan nunca alteran
/// las membresías ya contratadas: estas guardan una copia de los términos aplicados.
/// </summary>
public sealed class MembershipPlan : Entity<Guid>, ITenantOwned, IAuditable
{
    public const string Currency = "USD";
    public const decimal MaxPrice = 100000m;

    private readonly List<PlanBranch> _branches = [];

    private MembershipPlan(Guid id)
        : base(id)
    {
    }

    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public decimal Price { get; private set; }
    public int DurationValue { get; private set; }
    public DurationUnit DurationUnit { get; private set; }
    public string? AllowedAccess { get; private set; }
    public string? Restrictions { get; private set; }
    public string? CommercialTerms { get; private set; }
    public TaxKind TaxKind { get; private set; } = TaxKind.General;
    public bool IsActive { get; private set; } = true;
    public DateOnly? ValidFrom { get; private set; }
    public DateOnly? ValidUntil { get; private set; }
    public bool AllowFreeze { get; private set; }
    public int MaxFreezeDays { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public Guid? CreatedBy { get; private set; }
    public DateTimeOffset? UpdatedAt { get; private set; }
    public Guid? UpdatedBy { get; private set; }

    public IReadOnlyCollection<PlanBranch> Branches => _branches;

    public static MembershipPlan Create(Guid tenantId, PlanDetails details, IEnumerable<Guid> branchIds, Guid? createdBy, DateTimeOffset now)
    {
        var plan = new MembershipPlan(Guid.CreateVersion7())
        {
            TenantId = tenantId,
            CreatedAt = now,
            CreatedBy = createdBy
        };
        plan.Apply(details);
        plan.SetBranches(branchIds);
        return plan;
    }

    public void Update(PlanDetails details, IEnumerable<Guid> branchIds, Guid? updatedBy, DateTimeOffset now)
    {
        Apply(details);
        SetBranches(branchIds);
        UpdatedAt = now;
        UpdatedBy = updatedBy;
    }

    /// <summary>Último día de la membresía: inicio + duración − 1 día (un mes desde el 1 de enero termina el 31).</summary>
    public DateOnly CalculateEndDate(DateOnly start) => CalculateEndDate(start, DurationValue, DurationUnit);

    public static DateOnly CalculateEndDate(DateOnly start, int value, DurationUnit unit) =>
        (unit switch
        {
            DurationUnit.Day => start.AddDays(value),
            DurationUnit.Week => start.AddDays(7 * value),
            DurationUnit.Month => start.AddMonths(value),
            DurationUnit.Year => start.AddYears(value),
            _ => throw new ArgumentOutOfRangeException(nameof(unit))
        }).AddDays(-1);

    /// <summary>¿Se puede vender en esa fecha y sucursal? Un plan inactivo o fuera de su vigencia no se vende.</summary>
    public bool IsSellableOn(DateOnly date, Guid branchId) =>
        IsActive
        && (ValidFrom is null || date >= ValidFrom)
        && (ValidUntil is null || date <= ValidUntil)
        && (_branches.Count == 0 || _branches.Any(b => b.BranchId == branchId));

    private void Apply(PlanDetails d)
    {
        Name = d.Name.Trim();
        Description = Clean(d.Description);
        Price = Money.Round(d.Price);
        DurationValue = d.DurationValue;
        DurationUnit = d.DurationUnit;
        AllowedAccess = Clean(d.AllowedAccess);
        Restrictions = Clean(d.Restrictions);
        CommercialTerms = Clean(d.CommercialTerms);
        TaxKind = d.TaxKind;
        IsActive = d.IsActive;
        ValidFrom = d.ValidFrom;
        ValidUntil = d.ValidUntil;
        AllowFreeze = d.AllowFreeze;
        MaxFreezeDays = d.AllowFreeze ? d.MaxFreezeDays : 0;
    }

    private void SetBranches(IEnumerable<Guid> branchIds)
    {
        var wanted = branchIds.Distinct().ToHashSet();
        _branches.RemoveAll(b => !wanted.Contains(b.BranchId));
        foreach (var id in wanted.Where(id => _branches.All(b => b.BranchId != id)))
        {
            _branches.Add(new PlanBranch { TenantId = TenantId, PlanId = Id, BranchId = id });
        }
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record PlanDetails(
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
    int MaxFreezeDays);
