using FitCore.SharedKernel;

namespace FitCore.Commercial.Domain;

/// <summary>
/// Estado guardado de una obligación. <see cref="Overdue"/> no se guarda: es "pendiente o parcial con la fecha de
/// vencimiento superada" (ver <see cref="PaymentObligation.EffectiveStatus"/>).
/// </summary>
public enum ObligationStatus
{
    Pending = 1,
    PartiallyPaid = 2,
    Paid = 3,
    Voided = 4,
    Overdue = 5
}

/// <summary>
/// Lo que un alumno debe por una inscripción, renovación u otro concepto. No es una factura tributaria (eso llega en la
/// Fase 8). El saldo no se almacena: se calcula con los pagos confirmados asignados a la obligación.
/// </summary>
public sealed class PaymentObligation : Entity<Guid>, ITenantOwned, IAuditable
{
    public const string Currency = "USD";

    private PaymentObligation(Guid id)
        : base(id)
    {
    }

    public Guid TenantId { get; private set; }
    public Guid BranchId { get; private set; }
    public Guid MemberId { get; private set; }
    public Guid? MembershipId { get; private set; }

    /// <summary>Número legible, único por gimnasio (OB-000001).</summary>
    public string Reference { get; private set; } = string.Empty;

    public string Concept { get; private set; } = string.Empty;
    public DateOnly IssueDate { get; private set; }
    public DateOnly DueDate { get; private set; }
    public decimal OriginalAmount { get; private set; }
    public decimal DiscountAmount { get; private set; }

    /// <summary>Importe exigible: original menos descuentos, con IVA incluido.</summary>
    public decimal PayableAmount { get; private set; }

    public decimal Subtotal { get; private set; }
    public decimal TaxAmount { get; private set; }
    public TaxKind TaxKind { get; private set; }

    /// <summary>Tarifa de IVA congelada al emitir: un decreto posterior no la modifica.</summary>
    public decimal TaxPercent { get; private set; }

    public ObligationStatus Status { get; private set; } = ObligationStatus.Pending;
    public DateTimeOffset? VoidedAt { get; private set; }
    public Guid? VoidedBy { get; private set; }
    public string? VoidReason { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public Guid? CreatedBy { get; private set; }
    public DateTimeOffset? UpdatedAt { get; private set; }
    public Guid? UpdatedBy { get; private set; }

    public static PaymentObligation Create(
        Guid tenantId,
        Guid branchId,
        Guid memberId,
        Guid? membershipId,
        string reference,
        string concept,
        DateOnly issueDate,
        DateOnly dueDate,
        decimal originalAmount,
        decimal discountAmount,
        TaxKind taxKind,
        decimal taxPercent,
        Guid? createdBy,
        DateTimeOffset now)
    {
        var original = Money.Round(originalAmount);
        var discount = Money.Round(discountAmount);
        var payable = original - discount;
        var (subtotal, tax) = TaxRate.Split(payable, taxPercent);

        return new PaymentObligation(Guid.CreateVersion7())
        {
            TenantId = tenantId,
            BranchId = branchId,
            MemberId = memberId,
            MembershipId = membershipId,
            Reference = reference,
            Concept = concept.Trim(),
            IssueDate = issueDate,
            DueDate = dueDate,
            OriginalAmount = original,
            DiscountAmount = discount,
            PayableAmount = payable,
            Subtotal = subtotal,
            TaxAmount = tax,
            TaxKind = taxKind,
            TaxPercent = taxPercent,
            CreatedAt = now,
            CreatedBy = createdBy
        };
    }

    public ObligationStatus EffectiveStatus(DateOnly today) =>
        Status is ObligationStatus.Pending or ObligationStatus.PartiallyPaid && DueDate < today ? ObligationStatus.Overdue : Status;

    /// <summary>
    /// Actualiza el estado a partir de lo realmente cobrado (suma de pagos confirmados). Es el único camino para pasar entre
    /// pendiente, parcial y pagada, y se llama dentro de la misma transacción que el pago o el reverso.
    /// </summary>
    public void ApplyConfirmedPayments(decimal confirmed, Guid? actor, DateTimeOffset now)
    {
        if (Status == ObligationStatus.Voided)
        {
            return;
        }

        var next = confirmed >= PayableAmount ? ObligationStatus.Paid
            : confirmed > 0m ? ObligationStatus.PartiallyPaid
            : ObligationStatus.Pending;
        if (next == Status)
        {
            return;
        }

        Status = next;
        UpdatedAt = now;
        UpdatedBy = actor;
    }

    public Result Void(string reason, Guid? actor, DateTimeOffset now)
    {
        if (Status == ObligationStatus.Voided)
        {
            return Result.Failure(Error.Conflict("obligations.already_voided", "La obligación ya está anulada."));
        }

        Status = ObligationStatus.Voided;
        VoidReason = reason.Trim();
        VoidedAt = now;
        VoidedBy = actor;
        UpdatedAt = now;
        UpdatedBy = actor;
        return Result.Success();
    }
}
