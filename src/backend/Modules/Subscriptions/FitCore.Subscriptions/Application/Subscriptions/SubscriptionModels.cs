using FitCore.Application.Core;
using FitCore.SharedKernel;
using FitCore.Subscriptions.Domain;
using FluentValidation;

namespace FitCore.Subscriptions.Application.Subscriptions;

/// <summary>Valores de plataforma de las suscripciones (sección <c>Subscriptions</c> de la configuración).</summary>
public sealed class SubscriptionOptions
{
    public const string SectionName = "Subscriptions";

    /// <summary>Días de gracia tras un vencimiento sin pago cuando el plan no define los suyos.</summary>
    public int GraceDays { get; set; } = 7;

    /// <summary>Plan que recibe un gimnasio nuevo cuando el alta no indica ninguno. El plan <c>legacy</c> no tiene límites ni vencimiento.</summary>
    public string DefaultPlanCode { get; set; } = "legacy";
}

public sealed record SubscriptionEventDto(
    Guid Id, string Type, DateTimeOffset OccurredAt, Guid? ActorId, string? FromStatus, string? ToStatus, string? Detail)
{
    internal static SubscriptionEventDto From(SubscriptionEvent e) =>
        new(e.Id, e.Type.ToString(), e.OccurredAt, e.ActorId, e.FromStatus?.ToString(), e.ToStatus?.ToString(), e.Detail);
}

public sealed record SubscriptionDto(
    Guid Id, Guid TenantId, string TenantSlug, string TenantName,
    Guid PlanId, string PlanCode, string PlanName, int PlanVersion, decimal Price, string Currency, string BillingPeriod,
    string Status, DateTimeOffset StartedAt, DateOnly CurrentPeriodStart, DateOnly CurrentPeriodEnd,
    DateOnly? TrialEndsOn, DateOnly NextDueOn, DateOnly? GraceEndsOn, bool CancelAtPeriodEnd,
    DateTimeOffset? CancelledAt, string? CancelReason, DateTimeOffset? SuspendedAt, string? SuspensionReason,
    DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt, IReadOnlyList<SubscriptionEventDto> RecentEvents)
{
    internal static SubscriptionDto From(Subscription s, string tenantSlug, string tenantName, IEnumerable<SubscriptionEvent> events) => new(
        s.Id, s.TenantId, tenantSlug, tenantName,
        s.PlanId, s.PlanCode, s.PlanName, s.PlanVersionNumber, s.Price, s.Currency, s.Period.ToString(),
        s.Status.ToString(), s.StartedAt, s.CurrentPeriodStart, s.CurrentPeriodEnd,
        s.TrialEndsOn, s.NextDueOn, s.GraceEndsOn, s.CancelAtPeriodEnd,
        s.CancelledAt, s.CancelReason, s.SuspendedAt, s.SuspensionReason,
        s.CreatedAt, s.UpdatedAt, [.. events.OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Id).Select(SubscriptionEventDto.From)]);
}

/// <summary>Fila de listados: la suscripción vigente de cada gimnasio.</summary>
public sealed record SubscriptionSummaryDto(
    Guid Id, Guid TenantId, string TenantSlug, string TenantName, string TenantStatus,
    string PlanCode, string PlanName, string Status, decimal Price, string Currency, string BillingPeriod,
    DateOnly CurrentPeriodEnd, DateOnly NextDueOn, DateOnly? TrialEndsOn, DateOnly? GraceEndsOn);

public sealed record SubscriptionHistoryItemDto(
    Guid Id, Guid SubscriptionId, string PlanCode, string Type, DateTimeOffset OccurredAt, Guid? ActorId, string? FromStatus, string? ToStatus, string? Detail);

/// <summary>Asigna un plan al gimnasio. <c>StartTrial</c> exige que el plan tenga prueba gratuita; sin prueba la suscripción nace activa.</summary>
public sealed record CreateSubscriptionRequest(Guid PlanId, bool StartTrial, string? Note);

public sealed record ActivateSubscriptionRequest(string? Note);

public sealed record SuspendSubscriptionRequest(string Reason);

public sealed record ReactivateSubscriptionRequest(string? Note);

public sealed record GetTenantSubscriptionQuery(Guid TenantId) : IQuery<SubscriptionDto>;

public sealed record GetSubscriptionHistoryQuery(Guid TenantId, PageRequest Page) : IQuery<PagedResult<SubscriptionHistoryItemDto>>;

public sealed record ListSubscriptionsQuery(PageRequest Page, string? Status, string? PlanCode, string? Search, int? DueWithinDays)
    : IQuery<PagedResult<SubscriptionSummaryDto>>;

public sealed record CreateSubscriptionCommand(Guid TenantId, CreateSubscriptionRequest Request) : ICommand<SubscriptionDto>;

public sealed record ActivateSubscriptionCommand(Guid TenantId, ActivateSubscriptionRequest Request) : ICommand<SubscriptionDto>;

public sealed record SuspendSubscriptionCommand(Guid TenantId, SuspendSubscriptionRequest Request) : ICommand<SubscriptionDto>;

public sealed record ReactivateSubscriptionCommand(Guid TenantId, ReactivateSubscriptionRequest Request) : ICommand<SubscriptionDto>;

internal static class SubscriptionAppErrors
{
    public static readonly Error TenantNotFound = Error.NotFound("subscriptions.tenant_not_found", "Gimnasio no encontrado.");
    public static readonly Error NotFound = Error.NotFound("subscriptions.not_found", "El gimnasio no tiene una suscripción vigente.");
    public static readonly Error AlreadyExists = Error.Conflict("subscriptions.already_exists", "El gimnasio ya tiene una suscripción vigente. Cancélela antes de asignar otro plan.");
    public static readonly Error PlatformProtected = Error.Conflict("subscriptions.platform_protected", "El gimnasio de plataforma no tiene suscripción comercial.");
    public static readonly Error PlanNotFound = Error.Validation("subscriptions.plan_not_found", "El plan indicado no existe.");
    public static readonly Error PlanInactive = Error.Validation("subscriptions.plan_inactive", "El plan está inactivo: no se puede asignar a suscripciones nuevas.");
}

public sealed class CreateSubscriptionRequestValidator : AbstractValidator<CreateSubscriptionRequest>
{
    public CreateSubscriptionRequestValidator()
    {
        RuleFor(x => x.PlanId).NotEmpty();
        RuleFor(x => x.Note).MaximumLength(500);
    }
}

public sealed class ActivateSubscriptionRequestValidator : AbstractValidator<ActivateSubscriptionRequest>
{
    public ActivateSubscriptionRequestValidator() => RuleFor(x => x.Note).MaximumLength(500);
}

public sealed class SuspendSubscriptionRequestValidator : AbstractValidator<SuspendSubscriptionRequest>
{
    public SuspendSubscriptionRequestValidator() => RuleFor(x => x.Reason).NotEmpty().MaximumLength(500);
}

public sealed class ReactivateSubscriptionRequestValidator : AbstractValidator<ReactivateSubscriptionRequest>
{
    public ReactivateSubscriptionRequestValidator() => RuleFor(x => x.Note).MaximumLength(500);
}
