using FitCore.SharedKernel;

namespace FitCore.Commercial.Domain;

/// <summary>
/// Estado de una membresía. <see cref="Expired"/> no se guarda: se deriva de la fecha de fin (ver
/// <see cref="Membership.EffectiveStatus"/>), así que nadie tiene que "vencer" membresías con un proceso.
/// </summary>
public enum MembershipStatus
{
    PendingPayment = 1,
    Active = 2,
    Frozen = 3,
    Cancelled = 4,
    Expired = 5
}

public enum MembershipEventType
{
    Created = 1,
    Activated = 2,
    Frozen = 3,
    Reactivated = 4,
    Cancelled = 5,
    Renewed = 6
}

/// <summary>Historial de cambios de una membresía. Solo se agregan filas.</summary>
public sealed class MembershipEvent : ITenantOwned
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid MembershipId { get; init; }
    public MembershipEventType Type { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
    public Guid? ActorId { get; init; }

    /// <summary>JSON con lo que cambió (fechas anteriores y nuevas, motivo, días congelados…).</summary>
    public string? Details { get; init; }
}

/// <summary>Copia de los términos comerciales vigentes al contratar. Los cambios posteriores del plan no la alteran.</summary>
public sealed record MembershipTerms(
    Guid PlanId,
    string PlanName,
    decimal PlanPrice,
    int DurationValue,
    DurationUnit DurationUnit,
    TaxKind TaxKind,
    decimal TaxPercent,
    string? AllowedAccess,
    string? CommercialTerms,
    bool AllowFreeze,
    int MaxFreezeDays,
    Guid? PromotionId,
    string? PromotionName,
    string? PromotionCode,
    decimal DiscountAmount);

/// <summary>
/// Plan contratado por un alumno. Distingue el estado comercial del estado de pago: nace <c>PendingPayment</c> y pasa a
/// <c>Active</c> solo cuando el pago cumple la política del gimnasio. Nunca se reescribe: renovar crea otra membresía.
/// </summary>
public sealed class Membership : Entity<Guid>, ITenantOwned, IAuditable
{
    private Membership(Guid id)
        : base(id)
    {
    }

    public Guid TenantId { get; private set; }
    public Guid BranchId { get; private set; }
    public Guid MemberId { get; private set; }
    public Guid PlanId { get; private set; }

    // Copia de los términos aplicados (no cambia aunque el plan se edite).
    public string PlanName { get; private set; } = string.Empty;
    public decimal PlanPrice { get; private set; }
    public int DurationValue { get; private set; }
    public DurationUnit DurationUnit { get; private set; }
    public TaxKind TaxKind { get; private set; }
    public decimal TaxPercent { get; private set; }
    public string? AllowedAccess { get; private set; }
    public string? CommercialTerms { get; private set; }
    public bool AllowFreeze { get; private set; }
    public int MaxFreezeDays { get; private set; }
    public Guid? PromotionId { get; private set; }
    public string? PromotionName { get; private set; }
    public string? PromotionCode { get; private set; }
    public decimal DiscountAmount { get; private set; }

    /// <summary>Precio acordado con IVA incluido: precio del plan menos el descuento.</summary>
    public decimal AgreedPrice { get; private set; }

    public DateOnly StartDate { get; private set; }
    public DateOnly EndDate { get; private set; }
    public MembershipStatus Status { get; private set; }
    public DateOnly? FreezeStartedOn { get; private set; }
    public int FrozenDaysUsed { get; private set; }
    public DateTimeOffset? CancelledAt { get; private set; }
    public Guid? CancelledBy { get; private set; }
    public string? CancelReason { get; private set; }
    public Guid? RenewedFromId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public Guid? CreatedBy { get; private set; }
    public DateTimeOffset? UpdatedAt { get; private set; }
    public Guid? UpdatedBy { get; private set; }

    public static Membership Create(
        Guid tenantId,
        Guid branchId,
        Guid memberId,
        MembershipTerms terms,
        DateOnly startDate,
        DateOnly endDate,
        Guid? renewedFromId,
        Guid? createdBy,
        DateTimeOffset now) =>
        new(Guid.CreateVersion7())
        {
            TenantId = tenantId,
            BranchId = branchId,
            MemberId = memberId,
            PlanId = terms.PlanId,
            PlanName = terms.PlanName,
            PlanPrice = terms.PlanPrice,
            DurationValue = terms.DurationValue,
            DurationUnit = terms.DurationUnit,
            TaxKind = terms.TaxKind,
            TaxPercent = terms.TaxPercent,
            AllowedAccess = terms.AllowedAccess,
            CommercialTerms = terms.CommercialTerms,
            AllowFreeze = terms.AllowFreeze,
            MaxFreezeDays = terms.MaxFreezeDays,
            PromotionId = terms.PromotionId,
            PromotionName = terms.PromotionName,
            PromotionCode = terms.PromotionCode,
            DiscountAmount = terms.DiscountAmount,
            AgreedPrice = terms.PlanPrice - terms.DiscountAmount,
            StartDate = startDate,
            EndDate = endDate,
            Status = MembershipStatus.PendingPayment,
            RenewedFromId = renewedFromId,
            CreatedAt = now,
            CreatedBy = createdBy
        };

    /// <summary>Estado real hoy: una membresía vigente cuyo fin ya pasó está vencida, sin necesidad de un proceso que la marque.</summary>
    public MembershipStatus EffectiveStatus(DateOnly today) =>
        Status is MembershipStatus.Cancelled or MembershipStatus.Frozen ? Status
        : EndDate < today ? MembershipStatus.Expired
        : Status;

    public int RemainingFreezeDays => Math.Max(0, MaxFreezeDays - FrozenDaysUsed);

    public Result Activate(Guid? actor, DateTimeOffset now)
    {
        if (Status != MembershipStatus.PendingPayment)
        {
            return Result.Success();
        }

        Status = MembershipStatus.Active;
        Touch(actor, now);
        return Result.Success();
    }

    public Result Freeze(DateOnly today, Guid? actor, DateTimeOffset now)
    {
        if (EffectiveStatus(today) != MembershipStatus.Active)
        {
            return Result.Failure(MembershipErrors.NotActive);
        }

        if (!AllowFreeze)
        {
            return Result.Failure(MembershipErrors.FreezeNotAllowed);
        }

        if (RemainingFreezeDays == 0)
        {
            return Result.Failure(MembershipErrors.FreezeLimitReached);
        }

        Status = MembershipStatus.Frozen;
        FreezeStartedOn = today;
        Touch(actor, now);
        return Result.Success();
    }

    /// <summary>
    /// Reactiva y alarga el fin por los días congelados, sin pasar del cupo del plan. Devuelve los días efectivamente
    /// otorgados para que el historial muestre fechas anteriores y nuevas.
    /// </summary>
    public Result<int> Reactivate(DateOnly today, Guid? actor, DateTimeOffset now)
    {
        if (Status != MembershipStatus.Frozen || FreezeStartedOn is not { } started)
        {
            return MembershipErrors.NotFrozen;
        }

        var frozenDays = Math.Max(0, today.DayNumber - started.DayNumber);
        var granted = Math.Min(frozenDays, RemainingFreezeDays);
        EndDate = EndDate.AddDays(granted);
        FrozenDaysUsed += granted;
        FreezeStartedOn = null;
        Status = MembershipStatus.Active;
        Touch(actor, now);
        return granted;
    }

    public Result Cancel(string reason, DateOnly today, Guid? actor, DateTimeOffset now)
    {
        var effective = EffectiveStatus(today);
        if (effective == MembershipStatus.Cancelled)
        {
            return Result.Failure(MembershipErrors.AlreadyCancelled);
        }

        if (effective == MembershipStatus.Expired)
        {
            return Result.Failure(MembershipErrors.AlreadyExpired);
        }

        Status = MembershipStatus.Cancelled;
        CancelReason = reason.Trim();
        CancelledAt = now;
        CancelledBy = actor;
        Touch(actor, now);
        return Result.Success();
    }

    private void Touch(Guid? actor, DateTimeOffset now)
    {
        UpdatedAt = now;
        UpdatedBy = actor;
    }
}

public static class MembershipErrors
{
    public static readonly Error NotActive = Error.Validation("memberships.not_active", "La membresía no está activa.");
    public static readonly Error NotFrozen = Error.Validation("memberships.not_frozen", "La membresía no está congelada.");
    public static readonly Error FreezeNotAllowed = Error.Validation("memberships.freeze_not_allowed", "El plan de esta membresía no permite congelar.");
    public static readonly Error FreezeLimitReached = Error.Validation("memberships.freeze_limit_reached", "Ya se usaron todos los días de congelamiento del plan.");
    public static readonly Error AlreadyCancelled = Error.Conflict("memberships.already_cancelled", "La membresía ya está cancelada.");
    public static readonly Error AlreadyExpired = Error.Conflict("memberships.expired", "La membresía ya venció.");
}
