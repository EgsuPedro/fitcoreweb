using FitCore.Application.Core;
using FitCore.SharedKernel;
using FitCore.Subscriptions.Application.Billing;
using FitCore.Subscriptions.Application.Plans;
using FitCore.Subscriptions.Domain;
using FluentValidation;

namespace FitCore.Subscriptions.Application.Portal;

// Portal «Mi suscripción»: lo que el propietario de un gimnasio ve y pide sobre su propio contrato. Todo sale del gimnasio del token; el gimnasio no
// modifica su suscripción (solo la plataforma puede): pide, y el equipo aprueba o rechaza.

/// <summary>Una situación que el gimnasio debe conocer ahora (para el banner). El frontend la traduce por <see cref="Code"/>; <see cref="Date"/> es la fecha que aplique.</summary>
public sealed record SubscriptionAlertDto(string Code, string Severity, DateOnly? Date);

public sealed record OpenInvoiceDto(Guid Id, string Number, string Status, decimal Amount, string Currency, DateOnly PeriodStart, DateOnly PeriodEnd, DateOnly DueOn, bool Overdue);

public sealed record SubscriptionRequestDto(
    Guid Id, string Kind, string Status, Guid? RequestedPlanId, string? RequestedPlanName, string? Message,
    DateTimeOffset CreatedAt, DateTimeOffset? ResolvedAt, string? ResolutionNote)
{
    internal static SubscriptionRequestDto From(SubscriptionRequest r, string? planName) =>
        new(r.Id, r.Kind.ToString(), r.Status.ToString(), r.RequestedPlanId, planName, r.Message, r.CreatedAt, r.ResolvedAt, r.ResolutionNote);
}

/// <summary>Solicitud tal como la ve la plataforma: con el gimnasio y su suscripción actual.</summary>
public sealed record PlatformRequestDto(
    Guid Id, Guid TenantId, string TenantSlug, string TenantName, string Kind, string Status, Guid? RequestedPlanId, string? RequestedPlanName,
    string? Message, Guid? RequestedBy, DateTimeOffset CreatedAt, DateTimeOffset? ResolvedAt, Guid? ResolvedBy, string? ResolutionNote,
    string? CurrentPlanName, string? SubscriptionStatus);

public sealed record MySubscriptionDto(
    Guid Id, string PlanCode, string PlanName, int PlanVersion, decimal Price, string Currency, string BillingPeriod, string Status, string AccessMode,
    DateTimeOffset StartedAt, DateOnly CurrentPeriodStart, DateOnly CurrentPeriodEnd, DateOnly? TrialEndsOn, DateOnly NextDueOn, DateOnly? GraceEndsOn,
    bool CancelAtPeriodEnd, string? PendingPlanName, OpenInvoiceDto? OpenInvoice, IReadOnlyList<SubscriptionAlertDto> Alerts,
    IReadOnlyList<SubscriptionRequestDto> PendingRequests, int UnreadNotices, bool IsFree);

public sealed record NoticeDto(Guid Id, string Type, string Severity, string? PlanName, DateOnly? RelatedDate, string? Reference, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt)
{
    internal static NoticeDto From(SubscriptionNotice n) =>
        new(n.Id, n.Type, n.Severity.ToString(), n.PlanName, n.RelatedDate, n.Reference, n.CreatedAt, n.ReadAt);
}

/// <summary>Un plan que el gimnasio puede pedir, con cómo se trataría el cambio (<c>Upgrade</c> entra de inmediato; <c>Downgrade</c>, al fin del período).</summary>
public sealed record AvailablePlanDto(PlanSummaryDto Plan, bool IsCurrent, string ChangeKind);

public sealed record NewSubscriptionRequest(SubscriptionRequestKind Kind, Guid? PlanId, string? Message);

public sealed record ResolveSubscriptionRequest(string? Note);

public sealed record GetMySubscriptionQuery : IQuery<MySubscriptionDto>;

public sealed record ListMyInvoicesQuery(PageRequest Page) : IQuery<PagedResult<InvoiceSummaryDto>>;

public sealed record ListMyNoticesQuery(PageRequest Page, bool UnreadOnly) : IQuery<PagedResult<NoticeDto>>;

public sealed record ListMyRequestsQuery(PageRequest Page) : IQuery<PagedResult<SubscriptionRequestDto>>;

public sealed record ListMyPlansQuery : IQuery<IReadOnlyList<AvailablePlanDto>>;

public sealed record MarkNoticeReadCommand(Guid? NoticeId) : ICommand<int>;

public sealed record CreateRequestCommand(NewSubscriptionRequest Request) : ICommand<SubscriptionRequestDto>;

public sealed record WithdrawRequestCommand(Guid Id) : ICommand<SubscriptionRequestDto>;

public sealed record ListPlatformRequestsQuery(PageRequest Page, string? Status, Guid? TenantId) : IQuery<PagedResult<PlatformRequestDto>>;

public sealed record ApproveRequestCommand(Guid Id, ResolveSubscriptionRequest Request) : ICommand<PlatformRequestDto>;

public sealed record RejectRequestCommand(Guid Id, ResolveSubscriptionRequest Request) : ICommand<PlatformRequestDto>;

internal static class PortalErrors
{
    public static readonly Error RequestNotFound = Error.NotFound("subscription_requests.not_found", "La solicitud no existe.");
    public static readonly Error NoticeNotFound = Error.NotFound("subscription_notices.not_found", "El aviso no existe.");
    public static readonly Error AlreadyPending = Error.Conflict("subscription_requests.already_pending", "Ya hay una solicitud pendiente de este tipo: espere la respuesta o retírela.");
    public static readonly Error PlanNotAvailable = Error.Validation("subscription_requests.plan_not_available", "Ese plan no está disponible.");
    public static readonly Error SamePlan = Error.Conflict("subscription_requests.same_plan", "Ya está en ese plan.");
    public static readonly Error InvalidState = Error.Conflict("subscription_requests.invalid_state", "La suscripción no admite esa solicitud en su estado actual.");
    public static readonly Error NotApplicable = Error.Conflict("subscription_requests.reactivation_not_applicable", "La suscripción no está suspendida ni con cancelación programada: asigne un plan o renueve desde la organización y rechace la solicitud con una nota.");
    public static readonly Error StatusInvalid = Error.Validation("subscription_requests.status_invalid", "El estado no es válido.");
}

public sealed class NewSubscriptionRequestValidator : AbstractValidator<NewSubscriptionRequest>
{
    public NewSubscriptionRequestValidator()
    {
        RuleFor(x => x.Kind).IsInEnum();
        RuleFor(x => x.Message).MaximumLength(SubscriptionRequest.MaxMessageLength);
    }
}

public sealed class ResolveSubscriptionRequestValidator : AbstractValidator<ResolveSubscriptionRequest>
{
    public ResolveSubscriptionRequestValidator() => RuleFor(x => x.Note).MaximumLength(SubscriptionRequest.MaxMessageLength);
}
