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

    /// <summary>Con cuántos días de anticipación se puede renovar una suscripción activa (evita acumular períodos pagados por adelantado).</summary>
    public int RenewalWindowDays { get; set; } = 30;

    /// <summary>Cada cuántos minutos el Worker revisa vencimientos, moras y renovaciones de planes gratuitos.</summary>
    public int LifecycleIntervalMinutes { get; set; } = 15;
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
    string? ScheduledCancelReason, Guid? PendingPlanId, Guid? PendingPlanVersionId,
    DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt, IReadOnlyList<SubscriptionEventDto> RecentEvents)
{
    internal static SubscriptionDto From(Subscription s, string tenantSlug, string tenantName, IEnumerable<SubscriptionEvent> events) => new(
        s.Id, s.TenantId, tenantSlug, tenantName,
        s.PlanId, s.PlanCode, s.PlanName, s.PlanVersionNumber, s.Price, s.Currency, s.Period.ToString(),
        s.Status.ToString(), s.StartedAt, s.CurrentPeriodStart, s.CurrentPeriodEnd,
        s.TrialEndsOn, s.NextDueOn, s.GraceEndsOn, s.CancelAtPeriodEnd,
        s.CancelledAt, s.CancelReason, s.SuspendedAt, s.SuspensionReason,
        s.ScheduledCancelReason, s.PendingPlanId, s.PendingPlanVersionId,
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

/// <summary>Renueva el período siguiente (el pago se registra como cobro comercial). Sin prorrateo.</summary>
public sealed record RenewSubscriptionRequest(string? Note);

/// <summary>Cambia de plan: una mejora entra en vigor de inmediato; una baja, al terminar el período.</summary>
public sealed record ChangeSubscriptionPlanRequest(Guid PlanId, string? Note);

/// <summary>Cancela de inmediato o, con <c>AtPeriodEnd</c>, al terminar el período (sigue vigente hasta entonces).</summary>
public sealed record CancelSubscriptionRequest(string Reason, bool AtPeriodEnd);

public sealed record ResumeSubscriptionRequest(string? Note);

/// <summary>Prórroga administrativa de 1 a 90 días, con motivo obligatorio.</summary>
public sealed record ExtendSubscriptionRequest(int Days, string Reason);

/// <summary>Resultado de una pasada del proceso de vencimientos.</summary>
public sealed record LifecycleRunSummary(int Examined, int TrialsExpired, int FreeRenewed, int Cancelled, int MarkedPastDue, int GraceExpired, int Failed)
{
    public static readonly LifecycleRunSummary Empty = new(0, 0, 0, 0, 0, 0, 0);
}

public sealed record GetTenantSubscriptionQuery(Guid TenantId) : IQuery<SubscriptionDto>;

public sealed record GetSubscriptionHistoryQuery(Guid TenantId, PageRequest Page) : IQuery<PagedResult<SubscriptionHistoryItemDto>>;

public sealed record ListSubscriptionsQuery(PageRequest Page, string? Status, string? PlanCode, string? Search, int? DueWithinDays)
    : IQuery<PagedResult<SubscriptionSummaryDto>>;

public sealed record RenewSubscriptionCommand(Guid TenantId, RenewSubscriptionRequest Request) : ICommand<SubscriptionDto>;

public sealed record ChangeSubscriptionPlanCommand(Guid TenantId, ChangeSubscriptionPlanRequest Request) : ICommand<SubscriptionDto>;

public sealed record CancelSubscriptionCommand(Guid TenantId, CancelSubscriptionRequest Request) : ICommand<SubscriptionDto>;

public sealed record ResumeSubscriptionCommand(Guid TenantId, ResumeSubscriptionRequest Request) : ICommand<SubscriptionDto>;

public sealed record ExtendSubscriptionCommand(Guid TenantId, ExtendSubscriptionRequest Request) : ICommand<SubscriptionDto>;

public sealed record RunLifecycleCommand : ICommand<LifecycleRunSummary>;

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

public sealed class RenewSubscriptionRequestValidator : AbstractValidator<RenewSubscriptionRequest>
{
    public RenewSubscriptionRequestValidator() => RuleFor(x => x.Note).MaximumLength(500);
}

public sealed class ChangeSubscriptionPlanRequestValidator : AbstractValidator<ChangeSubscriptionPlanRequest>
{
    public ChangeSubscriptionPlanRequestValidator()
    {
        RuleFor(x => x.PlanId).NotEmpty();
        RuleFor(x => x.Note).MaximumLength(500);
    }
}

public sealed class CancelSubscriptionRequestValidator : AbstractValidator<CancelSubscriptionRequest>
{
    public CancelSubscriptionRequestValidator() => RuleFor(x => x.Reason).NotEmpty().MaximumLength(500);
}

public sealed class ResumeSubscriptionRequestValidator : AbstractValidator<ResumeSubscriptionRequest>
{
    public ResumeSubscriptionRequestValidator() => RuleFor(x => x.Note).MaximumLength(500);
}

public sealed class ExtendSubscriptionRequestValidator : AbstractValidator<ExtendSubscriptionRequest>
{
    public ExtendSubscriptionRequestValidator()
    {
        RuleFor(x => x.Days).InclusiveBetween(1, Domain.Subscription.MaxExtensionDays);
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(500);
    }
}
