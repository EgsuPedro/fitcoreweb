using FitCore.SharedKernel;

namespace FitCore.Subscriptions.Domain;

public enum InvoiceStatus
{
    Pending = 1,
    Paid = 2,
    Failed = 3,
    Void = 4,
    Refunded = 5,
    PartiallyRefunded = 6
}

public enum InvoiceKind
{
    /// <summary>Primer período de pago (al terminar la prueba o al reactivar una suscripción vencida).</summary>
    Activation = 1,

    /// <summary>Período siguiente de una suscripción en curso.</summary>
    Renewal = 2
}

/// <summary>
/// Medios de pago que se registran a mano. El pago con tarjeta solo existirá a través de un proveedor de pagos (que guarda el número y el CVV):
/// FitCore 360 nunca los almacena.
/// </summary>
public enum SubscriptionPaymentMethod
{
    BankTransfer = 1,
    Cash = 2,
    Other = 3,
    Card = 4
}

/// <summary>
/// Documento interno de cobro de una suscripción comercial: lo que FitCore 360 le cobra a un gimnasio por un período. <b>No es un comprobante
/// tributario</b> y no tiene relación con los pagos que un gimnasio recibe de sus alumnos (<c>billing.payments</c>). El importe y la moneda
/// se copian de la suscripción al emitirlo y no cambian.
/// </summary>
public sealed class SubscriptionInvoice : IAuditable
{
    public const string Notice = "Comprobante interno de cobro de FitCore 360. No es un comprobante tributario.";

    public Guid Id { get; init; }
    public string Number { get; init; } = string.Empty;
    public Guid TenantId { get; init; }
    public Guid SubscriptionId { get; init; }
    public InvoiceKind Kind { get; init; }
    public InvoiceStatus Status { get; private set; }
    public decimal Amount { get; init; }
    public string Currency { get; init; } = string.Empty;
    public string PlanCode { get; init; } = string.Empty;
    public string PlanName { get; init; } = string.Empty;
    public int PlanVersion { get; init; }

    /// <summary>Período que cubre. Al emitirlo es el esperado; al pagarse queda el que realmente se aplicó a la suscripción.</summary>
    public DateOnly PeriodStart { get; private set; }

    public DateOnly PeriodEnd { get; private set; }
    public DateTimeOffset IssuedAt { get; init; }
    public DateOnly DueOn { get; init; }
    public DateTimeOffset? PaidAt { get; private set; }
    public DateTimeOffset? VoidedAt { get; private set; }
    public string? VoidReason { get; private set; }
    public decimal RefundedAmount { get; private set; }
    public int AttemptCount { get; private set; }
    public DateTimeOffset CreatedAt { get; init; }
    public Guid? CreatedBy { get; init; }
    public DateTimeOffset? UpdatedAt { get; private set; }
    public Guid? UpdatedBy { get; private set; }

    public bool IsOpen => Status is InvoiceStatus.Pending or InvoiceStatus.Failed;

    public static Result<SubscriptionInvoice> Issue(
        string number, Subscription subscription, PlanSnapshot billedPlan, InvoiceKind kind, DateOnly expectedStart, DateOnly dueOn, Guid? actor, DateTimeOffset now)
    {
        if (billedPlan.Price <= 0m)
        {
            return Error.Conflict("billing.free_plan", "Un plan gratuito no genera cobros.");
        }

        return new SubscriptionInvoice
        {
            Id = Guid.CreateVersion7(),
            Number = number,
            TenantId = subscription.TenantId,
            SubscriptionId = subscription.Id,
            Kind = kind,
            Status = InvoiceStatus.Pending,
            Amount = billedPlan.Price,
            Currency = billedPlan.Currency,
            PlanCode = billedPlan.Code,
            PlanName = billedPlan.Name,
            PlanVersion = billedPlan.VersionNumber,
            PeriodStart = expectedStart,
            PeriodEnd = Subscription.AddPeriod(expectedStart, billedPlan.Period),
            IssuedAt = now,
            DueOn = dueOn,
            CreatedAt = now,
            CreatedBy = actor
        };
    }

    public Result MarkPaid(DateOnly appliedStart, DateOnly appliedEnd, DateTimeOffset paidAt, Guid? actor, DateTimeOffset now)
    {
        if (!IsOpen)
        {
            return Error.Conflict("billing.invoice_not_open", $"La factura {Number} está {Status}: no admite pagos.");
        }

        Status = InvoiceStatus.Paid;
        PaidAt = paidAt;
        PeriodStart = appliedStart;
        PeriodEnd = appliedEnd;
        Touch(actor, now);
        return Result.Success();
    }

    /// <summary>Un intento de cobro rechazado: la factura queda «Failed» (sigue abierta y se puede pagar) y no cambia nada de la suscripción.</summary>
    public Result RecordFailedAttempt(Guid? actor, DateTimeOffset now)
    {
        if (!IsOpen)
        {
            return Error.Conflict("billing.invoice_not_open", $"La factura {Number} está {Status}: no admite intentos de cobro.");
        }

        Status = InvoiceStatus.Failed;
        AttemptCount++;
        Touch(actor, now);
        return Result.Success();
    }

    public Result Void(string reason, Guid? actor, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return Error.Validation("billing.reason_required", "Indique el motivo.");
        }

        if (!IsOpen)
        {
            return Error.Conflict("billing.invoice_not_open", $"La factura {Number} está {Status}: solo se anula una factura abierta.");
        }

        Status = InvoiceStatus.Void;
        VoidedAt = now;
        VoidReason = reason.Trim();
        Touch(actor, now);
        return Result.Success();
    }

    public Result ApplyRefund(decimal amount, Guid? actor, DateTimeOffset now)
    {
        if (Status is not (InvoiceStatus.Paid or InvoiceStatus.PartiallyRefunded))
        {
            return Error.Conflict("billing.invoice_not_paid", $"La factura {Number} está {Status}: solo se reembolsa una factura pagada.");
        }

        if (amount <= 0m || amount > Amount - RefundedAmount)
        {
            return Error.Validation("billing.refund_amount_invalid", $"El reembolso debe ser mayor que 0 y no superar lo disponible ({Amount - RefundedAmount:0.00}).");
        }

        RefundedAmount += amount;
        Status = RefundedAmount == Amount ? InvoiceStatus.Refunded : InvoiceStatus.PartiallyRefunded;
        Touch(actor, now);
        return Result.Success();
    }

    private void Touch(Guid? actor, DateTimeOffset now)
    {
        UpdatedAt = now;
        UpdatedBy = actor;
    }
}

/// <summary>Pago confirmado de una factura comercial. Es todo o nada (no hay pagos parciales) y es inmutable salvo por lo reembolsado.</summary>
public sealed class SubscriptionPayment : IAuditable
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid InvoiceId { get; init; }
    public decimal Amount { get; init; }
    public string Currency { get; init; } = string.Empty;
    public SubscriptionPaymentMethod Method { get; init; }

    /// <summary>Referencia del pago (comprobante bancario). Nunca un número de tarjeta.</summary>
    public string? Reference { get; init; }

    /// <summary>Proveedor de pagos, si lo hubo; hoy todos los pagos se registran a mano y es nulo.</summary>
    public string? Provider { get; init; }

    public string? ProviderReference { get; init; }
    public string IdempotencyKey { get; init; } = string.Empty;
    public DateTimeOffset PaidAt { get; init; }
    public DateTimeOffset ConfirmedAt { get; init; }
    public Guid? RecordedBy { get; init; }
    public string? Notes { get; init; }
    public decimal RefundedAmount { get; private set; }

    public static SubscriptionPayment Confirm(
        SubscriptionInvoice invoice, SubscriptionPaymentMethod method, string? reference, string idempotencyKey, DateTimeOffset paidAt, Guid? actor, string? notes, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = invoice.TenantId,
        InvoiceId = invoice.Id,
        Amount = invoice.Amount,
        Currency = invoice.Currency,
        Method = method,
        Reference = string.IsNullOrWhiteSpace(reference) ? null : reference.Trim(),
        IdempotencyKey = idempotencyKey,
        PaidAt = paidAt,
        ConfirmedAt = now,
        RecordedBy = actor,
        Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim()
    };

    internal void AddRefund(decimal amount) => RefundedAmount += amount;
}

/// <summary>Intento de cobro. Hoy solo se registran los rechazados (el éxito es el pago confirmado); no guarda datos de tarjeta.</summary>
public sealed class SubscriptionPaymentAttempt : IAuditable
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid InvoiceId { get; init; }
    public SubscriptionPaymentMethod Method { get; init; }
    public string? Reference { get; init; }
    public string Reason { get; init; } = string.Empty;
    public DateTimeOffset AttemptedAt { get; init; }
    public Guid? RecordedBy { get; init; }
}

/// <summary>Reembolso total o parcial de un pago comercial. Registra la devolución; no cambia por sí mismo el estado de la suscripción.</summary>
public sealed class SubscriptionPaymentRefund : IAuditable
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid PaymentId { get; init; }
    public Guid InvoiceId { get; init; }
    public decimal Amount { get; init; }
    public string Reason { get; init; } = string.Empty;
    public string IdempotencyKey { get; init; } = string.Empty;
    public DateTimeOffset RefundedAt { get; init; }
    public Guid? RecordedBy { get; init; }
}

/// <summary>Defensa contra datos de pago sensibles: rechaza un texto que contenga algo con forma de número de tarjeta (13 a 19 dígitos, con espacios o guiones, que pasan Luhn).</summary>
public static partial class CardDataGuard
{
    public static bool LooksLikeCardNumber(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (System.Text.RegularExpressions.Match match in Candidate().Matches(text))
        {
            var digits = match.Value.Where(char.IsAsciiDigit).Select(c => c - '0').ToArray();
            if (digits.Length is >= 13 and <= 19 && PassesLuhn(digits))
            {
                return true;
            }
        }

        return false;
    }

    private static bool PassesLuhn(int[] digits)
    {
        var sum = 0;
        var alternate = false;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var d = digits[i];
            if (alternate)
            {
                d *= 2;
                if (d > 9)
                {
                    d -= 9;
                }
            }

            sum += d;
            alternate = !alternate;
        }

        return sum % 10 == 0;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\d(?:[ -]?\d){12,18}")]
    private static partial System.Text.RegularExpressions.Regex Candidate();
}
