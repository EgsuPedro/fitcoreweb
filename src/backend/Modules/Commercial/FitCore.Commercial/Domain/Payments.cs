using FitCore.SharedKernel;

namespace FitCore.Commercial.Domain;

public enum PaymentMethod
{
    Cash = 1,
    BankTransfer = 2,
    Card = 3,
    Other = 4
}

public enum PaymentStatus
{
    /// <summary>Transferencia registrada que aún nadie ha verificado. Reserva saldo pero no lo paga.</summary>
    PendingVerification = 1,
    Confirmed = 2,

    /// <summary>Revertido: el registro se conserva (los pagos no se borran) pero ya no cuenta en ningún saldo.</summary>
    Reversed = 3
}

/// <summary>Parte de un pago que se aplica a una obligación. Nunca se edita ni se borra.</summary>
public sealed class PaymentAllocation : ITenantOwned, IAuditable
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid PaymentId { get; init; }
    public Guid ObligationId { get; init; }
    public decimal Amount { get; init; }
}

public sealed record PaymentDetails(
    PaymentMethod Method,
    string? MethodDetail,
    string? Reference,
    string? BankName,
    DateTimeOffset PaidAt,
    string? Notes);

/// <summary>
/// Dinero recibido de un alumno. Es inmutable: lo único que cambia es su estado (confirmar una transferencia o revertirlo).
/// El importe total siempre es la suma de sus asignaciones, y el saldo de cada obligación se calcula a partir de ellas.
/// </summary>
public sealed class Payment : Entity<Guid>, ITenantOwned, IAuditable
{
    public const string Currency = "USD";

    private readonly List<PaymentAllocation> _allocations = [];

    private Payment(Guid id)
        : base(id)
    {
    }

    public Guid TenantId { get; private set; }
    public Guid BranchId { get; private set; }
    public Guid MemberId { get; private set; }

    /// <summary>Número de recibo interno, único por gimnasio (RC-000001). No es una factura tributaria.</summary>
    public string ReceiptNumber { get; private set; } = string.Empty;

    public PaymentMethod Method { get; private set; }

    /// <summary>Nombre del medio cuando es "Otro" (por ejemplo, un enlace de pago).</summary>
    public string? MethodDetail { get; private set; }

    public decimal Amount { get; private set; }
    public PaymentStatus Status { get; private set; }

    /// <summary>Referencia de la transferencia o del voucher del datáfono. Nunca el número de una tarjeta.</summary>
    public string? Reference { get; private set; }

    public string? BankName { get; private set; }
    public DateTimeOffset PaidAt { get; private set; }
    public string? Notes { get; private set; }
    /// <summary>Sesión de caja que recibió el efectivo. Solo los pagos en efectivo la tienen.</summary>
    public Guid? CashSessionId { get; private set; }
    public DateTimeOffset? ConfirmedAt { get; private set; }
    public Guid? ConfirmedBy { get; private set; }
    public DateTimeOffset? ReversedAt { get; private set; }
    public Guid? ReversedBy { get; private set; }
    public string? ReversalReason { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public Guid? CreatedBy { get; private set; }

    public IReadOnlyCollection<PaymentAllocation> Allocations => _allocations;

    /// <summary>Las transferencias esperan verificación; tarjeta y otros medios (con referencia) se confirman al registrarse.</summary>
    public static PaymentStatus InitialStatus(PaymentMethod method) =>
        method == PaymentMethod.BankTransfer ? PaymentStatus.PendingVerification : PaymentStatus.Confirmed;

    public static Payment Create(
        Guid tenantId,
        Guid branchId,
        Guid memberId,
        string receiptNumber,
        PaymentDetails details,
        IReadOnlyList<(Guid ObligationId, decimal Amount)> allocations,
        Guid? createdBy,
        DateTimeOffset now,
        Guid? cashSessionId = null)
    {
        var payment = new Payment(Guid.CreateVersion7())
        {
            TenantId = tenantId,
            BranchId = branchId,
            MemberId = memberId,
            ReceiptNumber = receiptNumber,
            Method = details.Method,
            MethodDetail = Clean(details.MethodDetail),
            Reference = Clean(details.Reference),
            BankName = Clean(details.BankName),
            PaidAt = details.PaidAt,
            Notes = Clean(details.Notes),
            Status = InitialStatus(details.Method),
            CashSessionId = cashSessionId,
            CreatedAt = now,
            CreatedBy = createdBy
        };

        foreach (var (obligationId, amount) in allocations)
        {
            payment._allocations.Add(new PaymentAllocation
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                PaymentId = payment.Id,
                ObligationId = obligationId,
                Amount = Money.Round(amount)
            });
        }

        payment.Amount = payment._allocations.Sum(a => a.Amount);
        if (payment.Status == PaymentStatus.Confirmed)
        {
            payment.ConfirmedAt = now;
            payment.ConfirmedBy = createdBy;
        }

        return payment;
    }

    public Result Confirm(Guid? actor, DateTimeOffset now)
    {
        if (Status != PaymentStatus.PendingVerification)
        {
            return Result.Failure(PaymentErrors.NotPending);
        }

        Status = PaymentStatus.Confirmed;
        ConfirmedAt = now;
        ConfirmedBy = actor;
        return Result.Success();
    }

    public Result Reverse(string reason, Guid? actor, DateTimeOffset now)
    {
        if (Status == PaymentStatus.Reversed)
        {
            return Result.Failure(PaymentErrors.AlreadyReversed);
        }

        Status = PaymentStatus.Reversed;
        ReversalReason = reason.Trim();
        ReversedAt = now;
        ReversedBy = actor;
        return Result.Success();
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Respuesta guardada de una solicitud con <c>Idempotency-Key</c>: repetirla devuelve lo mismo sin duplicar el efecto.</summary>
public sealed class IdempotencyKey : ITenantOwned
{
    public Guid TenantId { get; init; }
    public string Key { get; init; } = string.Empty;
    public string Scope { get; init; } = string.Empty;
    public string RequestHash { get; init; } = string.Empty;
    public Guid? ActorId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public int? ResponseStatus { get; set; }
    public string? ResponseBody { get; set; }
}

public static class PaymentErrors
{
    public static readonly Error NotPending = Error.Conflict("payments.not_pending", "El pago no está pendiente de verificación.");
    public static readonly Error AlreadyReversed = Error.Conflict("payments.already_reversed", "El pago ya fue revertido.");
}

/// <summary>Reglas para que una membresía pase de pendiente a activa según la política de acceso del gimnasio.</summary>
public static class ActivationRules
{
    /// <summary>
    /// ¿El pago cumple la política? Exigir pago completo: saldada. Permitir hasta el vencimiento: al menos un pago.
    /// Permitir con mínimo: pagado al menos el porcentaje configurado.
    /// </summary>
    public static bool IsSatisfied(AccessPolicy policy, decimal paid, decimal payable, int minPaymentPercent) =>
        policy switch
        {
            AccessPolicy.RequireFullPayment => paid >= payable,
            AccessPolicy.AllowWithMinimumPayment => paid > 0m && paid * 100m >= payable * minPaymentPercent,
            _ => paid > 0m
        };
}
