using FitCore.Application.Core;
using FitCore.SharedKernel;
using FitCore.Subscriptions.Domain;
using FluentValidation;

namespace FitCore.Subscriptions.Application.Billing;

public sealed record PaymentAttemptDto(Guid Id, string Method, string? Reference, string Reason, DateTimeOffset AttemptedAt);

public sealed record SubscriptionPaymentDto(
    Guid Id, Guid InvoiceId, string InvoiceNumber, Guid TenantId, string TenantSlug, string TenantName,
    decimal Amount, string Currency, string Method, string? Reference, string? Provider,
    DateTimeOffset PaidAt, DateTimeOffset ConfirmedAt, decimal RefundedAmount, string? Notes);

public sealed record InvoiceDto(
    Guid Id, string Number, Guid TenantId, string TenantSlug, string TenantName, Guid SubscriptionId,
    string Kind, string Status, decimal Amount, string Currency, string PlanCode, string PlanName, int PlanVersion,
    DateOnly PeriodStart, DateOnly PeriodEnd, DateTimeOffset IssuedAt, DateOnly DueOn, DateTimeOffset? PaidAt,
    DateTimeOffset? VoidedAt, string? VoidReason, decimal RefundedAmount, int AttemptCount, bool IsOpen, string Notice,
    SubscriptionPaymentDto? Payment, IReadOnlyList<PaymentAttemptDto> Attempts);

/// <summary>Fila de listados de facturas.</summary>
public sealed record InvoiceSummaryDto(
    Guid Id, string Number, Guid TenantId, string TenantSlug, string TenantName, string Kind, string Status,
    decimal Amount, string Currency, string PlanCode, DateOnly PeriodStart, DateOnly PeriodEnd, DateTimeOffset IssuedAt, DateOnly DueOn,
    DateTimeOffset? PaidAt, decimal RefundedAmount, int AttemptCount);

/// <summary>
/// Resumen de cobros comerciales. Distingue lo <b>emitido</b> (facturas, aunque no estén pagadas), lo <b>cobrado</b> (pagos confirmados, antes de
/// reembolsos), lo <b>reembolsado</b> y lo <b>pendiente</b> (facturas abiertas hoy). Solo hay dólares, por eso no se agrupa por moneda. No es MRR
/// ni ARR: son sumas de documentos y pagos reales del período.
/// </summary>
public sealed record BillingSummaryDto(
    DateOnly From, DateOnly To, string Currency,
    decimal IssuedAmount, int IssuedCount, decimal CollectedAmount, int CollectedCount, decimal RefundedAmount, decimal NetCollectedAmount,
    decimal PendingAmount, int PendingCount, decimal OverdueAmount, int OverdueCount, int FailedCount);

/// <summary>
/// Registra el pago confirmado de una factura. Exige <c>Idempotency-Key</c>. El importe y la moneda deben coincidir con los de la factura. El pago
/// con tarjeta no se registra a mano (solo a través de un proveedor de pagos): nunca se guardan números de tarjeta.
/// </summary>
public sealed record PayInvoiceRequest(decimal Amount, string Currency, SubscriptionPaymentMethod Method, string? Reference, DateTimeOffset? PaidAt, string? Notes);

public sealed record RecordFailedAttemptRequest(SubscriptionPaymentMethod Method, string? Reference, string Reason);

public sealed record VoidInvoiceRequest(string Reason);

public sealed record RefundPaymentRequest(decimal Amount, string Reason);

public sealed record ListInvoicesQuery(PageRequest Page, string? Status, Guid? TenantId, string? Search, DateOnly? From, DateOnly? To, bool OverdueOnly)
    : IQuery<PagedResult<InvoiceSummaryDto>>;

public sealed record GetInvoiceQuery(Guid Id) : IQuery<InvoiceDto>;

public sealed record ListSubscriptionPaymentsQuery(PageRequest Page, Guid? TenantId, DateOnly? From, DateOnly? To) : IQuery<PagedResult<SubscriptionPaymentDto>>;

public sealed record GetBillingSummaryQuery(DateOnly? From, DateOnly? To) : IQuery<BillingSummaryDto>;

public sealed record IssueInvoiceCommand(Guid TenantId) : ICommand<InvoiceDto>;

public sealed record PayInvoiceCommand(Guid InvoiceId, PayInvoiceRequest Request, string? IdempotencyKey) : ICommand<InvoiceDto>;

public sealed record RecordFailedAttemptCommand(Guid InvoiceId, RecordFailedAttemptRequest Request) : ICommand<InvoiceDto>;

public sealed record VoidInvoiceCommand(Guid InvoiceId, VoidInvoiceRequest Request) : ICommand<InvoiceDto>;

public sealed record RefundPaymentCommand(Guid PaymentId, RefundPaymentRequest Request, string? IdempotencyKey) : ICommand<InvoiceDto>;

internal static class BillingErrors
{
    public static readonly Error InvoiceNotFound = Error.NotFound("billing.invoice_not_found", "Factura no encontrada.");
    public static readonly Error PaymentNotFound = Error.NotFound("billing.payment_not_found", "Pago no encontrado.");
    public static readonly Error OpenInvoiceExists = Error.Conflict("billing.invoice_open_exists", "La suscripción ya tiene una factura abierta: páguela o anúlela antes de emitir otra.");
    public static readonly Error CancellationScheduled = Error.Conflict("billing.cancellation_scheduled", "La suscripción tiene una cancelación programada: no se emiten facturas.");
    public static readonly Error SubscriptionSuspended = Error.Conflict("billing.subscription_suspended", "La suscripción está suspendida: reactívela antes de registrar el pago.");
    public static readonly Error SubscriptionNotBillable = Error.Conflict("billing.subscription_not_billable", "La suscripción no admite cobros en su estado actual.");
    public static readonly Error KeyRequired = Error.Validation("billing.idempotency_key_required", "Esta operación exige el encabezado Idempotency-Key.");
    public static readonly Error KeyReused = Error.Conflict("billing.idempotency_key_reused", "Esa clave de idempotencia ya se usó en otra operación.");
    public static readonly Error AmountMismatch = Error.Validation("billing.amount_mismatch", "El importe o la moneda no coinciden con los de la factura.");
    public static readonly Error CardNotManual = Error.Validation("billing.card_requires_provider", "El pago con tarjeta solo se registra a través de un proveedor de pagos: FitCore 360 no guarda datos de tarjeta.");
    public static readonly Error CardDataRejected = Error.Validation("billing.card_data_rejected", "El texto parece contener un número de tarjeta. No escriba datos de tarjeta: use la referencia del comprobante.");
    public static readonly Error FutureDate = Error.Validation("billing.paid_at_future", "La fecha de pago no puede ser futura.");
}

public sealed class PayInvoiceRequestValidator : AbstractValidator<PayInvoiceRequest>
{
    public PayInvoiceRequestValidator()
    {
        RuleFor(x => x.Amount).GreaterThan(0m).LessThanOrEqualTo(Plan.MaxPrice).PrecisionScale(12, 2, ignoreTrailingZeros: true);
        RuleFor(x => x.Currency).NotEmpty().Length(3);
        RuleFor(x => x.Method).IsInEnum();
        RuleFor(x => x.Reference).MaximumLength(100).Must(r => !CardDataGuard.LooksLikeCardNumber(r)).WithMessage(BillingErrors.CardDataRejected.Description);
        RuleFor(x => x.Notes).MaximumLength(500).Must(n => !CardDataGuard.LooksLikeCardNumber(n)).WithMessage(BillingErrors.CardDataRejected.Description);
        RuleFor(x => x.Reference).NotEmpty().When(x => x.Method == SubscriptionPaymentMethod.BankTransfer).WithMessage("Indique la referencia del comprobante de la transferencia.");
    }
}

public sealed class RecordFailedAttemptRequestValidator : AbstractValidator<RecordFailedAttemptRequest>
{
    public RecordFailedAttemptRequestValidator()
    {
        RuleFor(x => x.Method).IsInEnum();
        RuleFor(x => x.Reference).MaximumLength(100).Must(r => !CardDataGuard.LooksLikeCardNumber(r)).WithMessage(BillingErrors.CardDataRejected.Description);
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(500).Must(r => !CardDataGuard.LooksLikeCardNumber(r)).WithMessage(BillingErrors.CardDataRejected.Description);
    }
}

public sealed class VoidInvoiceRequestValidator : AbstractValidator<VoidInvoiceRequest>
{
    public VoidInvoiceRequestValidator() => RuleFor(x => x.Reason).NotEmpty().MaximumLength(500);
}

public sealed class RefundPaymentRequestValidator : AbstractValidator<RefundPaymentRequest>
{
    public RefundPaymentRequestValidator()
    {
        RuleFor(x => x.Amount).GreaterThan(0m).PrecisionScale(12, 2, ignoreTrailingZeros: true);
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(500).Must(r => !CardDataGuard.LooksLikeCardNumber(r)).WithMessage(BillingErrors.CardDataRejected.Description);
    }
}
