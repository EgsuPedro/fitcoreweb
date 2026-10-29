using FitCore.Application.Core;
using FitCore.SharedKernel;
using FitCore.Subscriptions.Application.Billing;
using FitCore.Subscriptions.Application.Subscriptions;
using FitCore.Subscriptions.Domain;
using FluentValidation;

namespace FitCore.Subscriptions.Application.Organizations;

public sealed record OrganizationSubscriptionDto(
    Guid SubscriptionId, string PlanCode, string PlanName, string Status, decimal Price, string Currency, string BillingPeriod,
    DateOnly CurrentPeriodEnd, DateOnly NextDueOn, DateOnly? TrialEndsOn, DateOnly? GraceEndsOn, bool CancelAtPeriodEnd);

/// <summary>Consumo agregado medido en <c>MeasuredAt</c>: solo conteos, nunca datos de personas.</summary>
public sealed record OrganizationUsageDto(int Branches, int InternalUsers, int Members, DateTimeOffset MeasuredAt);

public sealed record OrganizationSummaryDto(
    Guid Id, string Slug, string Name, string TenantStatus, string TimeZone, DateTimeOffset CreatedAt,
    OrganizationSubscriptionDto? Subscription, OrganizationUsageDto? Usage);

public sealed record EntitlementLimitDto(string Code, long? Limit);

public sealed record OrganizationEntitlementDto(string AccessMode, IReadOnlyList<string> Features, IReadOnlyList<EntitlementLimitDto> Limits);

public sealed record OrganizationDto(
    Guid Id, string Slug, string Name, string TenantStatus, string TimeZone, DateTimeOffset CreatedAt,
    SubscriptionDto? Subscription, OrganizationEntitlementDto? Entitlement, OrganizationUsageDto? Usage,
    int OpenInvoices, decimal OpenInvoicesAmount, IReadOnlyList<InvoiceSummaryDto> RecentInvoices, int NotesCount);

public sealed record NoteDto(Guid Id, string Text, Guid? AuthorId, string? AuthorName, DateTimeOffset CreatedAt);

/// <summary>Acción administrativa auditada sobre la organización (suscripción, cobros, notas, suspensión). Es lo que hizo el equipo de plataforma, no datos del gimnasio.</summary>
public sealed record ActivityEntryDto(long Id, string Action, string Entity, string? EntityId, Guid? ActorId, string? ActorName, DateTimeOffset OccurredAt, string? Changes);

public sealed record AddNoteRequest(string Text);

public sealed record ListOrganizationsQuery(PageRequest Page, string? Search, string? TenantStatus, string? SubscriptionStatus, string? PlanCode, int? DueWithinDays)
    : IQuery<PagedResult<OrganizationSummaryDto>>;

public sealed record GetOrganizationQuery(Guid Id) : IQuery<OrganizationDto>;

public sealed record ListNotesQuery(Guid TenantId, PageRequest Page) : IQuery<PagedResult<NoteDto>>;

public sealed record GetActivityQuery(Guid TenantId, PageRequest Page) : IQuery<PagedResult<ActivityEntryDto>>;

public sealed record AddNoteCommand(Guid TenantId, AddNoteRequest Request) : ICommand<NoteDto>;

public sealed record RefreshUsageCommand(Guid TenantId) : ICommand<OrganizationUsageDto>;

public sealed record GetMetricsQuery(DateOnly? From, DateOnly? To) : IQuery<MetricsDto>;

public sealed record PlanDistributionDto(string PlanCode, string PlanName, int Subscriptions, decimal Mrr);

/// <summary>
/// Métricas agregadas del producto SaaS. Solo hay conteos y sumas de suscripciones, facturas y pagos: ningún dato personal, clínico ni financiero
/// de los alumnos de los gimnasios. <b>MRR</b> = suma, sobre las suscripciones Active y PastDue de pago, del precio contratado normalizado a un mes
/// (precio ÷ meses de la periodicidad); no incluye pruebas, suspendidas, canceladas ni planes gratuitos, y es lo <i>contratado</i>, no lo cobrado.
/// <b>ARR</b> = MRR × 12. Lo <b>cobrado</b> son los pagos confirmados del período (antes de reembolsos).
/// </summary>
public sealed record MetricsDto(
    DateOnly From, DateOnly To, string Currency,
    int Organizations, int SuspendedOrganizations, int OrganizationsWithoutSubscription,
    IReadOnlyDictionary<string, int> SubscriptionsByStatus, int TrialsInProgress, int PastDue, int OverdueInvoices, decimal OverdueAmount,
    IReadOnlyList<PlanDistributionDto> ByPlan,
    decimal CollectedAmount, decimal RefundedAmount, decimal NetCollectedAmount, decimal PendingInvoicedAmount,
    int RenewalsPaid, int ActivationsPaid, int FreeRenewals, int Cancellations,
    int TrialsConverted, int TrialsLost, decimal? TrialConversionPercent,
    decimal Mrr, decimal Arr, string MrrFormula);

internal static class OrganizationErrors
{
    public static readonly Error NotFound = Error.NotFound("organizations.not_found", "Organización no encontrada.");
    public static readonly Error PlatformProtected = Error.Conflict("organizations.platform_protected", "El gimnasio de plataforma no es una organización cliente.");
}

public sealed class AddNoteRequestValidator : AbstractValidator<AddNoteRequest>
{
    public AddNoteRequestValidator() => RuleFor(x => x.Text).NotEmpty().MaximumLength(PlatformNote.MaxLength);
}
