using FitCore.SharedKernel;

namespace FitCore.Subscriptions.Domain;

public enum SubscriptionRequestKind
{
    /// <summary>Pedir pasar a otro plan (mejora o baja).</summary>
    ChangePlan = 1,

    /// <summary>Pedir que la suscripción termine al final del período en curso.</summary>
    Cancellation = 2,

    /// <summary>Pedir que se levante una suspensión comercial o se retire una cancelación programada.</summary>
    Reactivation = 3
}

public enum SubscriptionRequestStatus
{
    Pending = 1,
    Approved = 2,
    Rejected = 3,
    Withdrawn = 4
}

/// <summary>
/// Solicitud del propietario de un gimnasio sobre su suscripción comercial. El gimnasio <b>no modifica</b> su contrato: pide, y el equipo de plataforma
/// aprueba (lo que ejecuta la acción con sus reglas y su auditoría) o rechaza con una nota. Solo hay una solicitud pendiente por suscripción y tipo.
/// </summary>
public sealed class SubscriptionRequest : IAuditable
{
    public const int MaxMessageLength = 500;

    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid SubscriptionId { get; init; }
    public SubscriptionRequestKind Kind { get; init; }
    public SubscriptionRequestStatus Status { get; private set; }
    public Guid? RequestedPlanId { get; init; }
    public string? Message { get; init; }
    public Guid? RequestedBy { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ResolvedAt { get; private set; }
    public Guid? ResolvedBy { get; private set; }
    public string? ResolutionNote { get; private set; }

    public static Result<SubscriptionRequest> Create(
        Guid tenantId, Guid subscriptionId, SubscriptionRequestKind kind, Guid? planId, string? message, Guid? requestedBy, DateTimeOffset now)
    {
        var text = string.IsNullOrWhiteSpace(message) ? null : message.Trim();
        if (kind == SubscriptionRequestKind.ChangePlan && planId is null)
        {
            return Error.Validation("subscription_requests.plan_required", "Elija el plan al que desea cambiar.");
        }

        if (kind != SubscriptionRequestKind.ChangePlan && planId is not null)
        {
            return Error.Validation("subscription_requests.plan_not_allowed", "Esta solicitud no lleva un plan.");
        }

        if (kind == SubscriptionRequestKind.Cancellation && text is null)
        {
            return Error.Validation("subscription_requests.message_required", "Cuéntenos el motivo de la cancelación.");
        }

        if (text is { Length: > MaxMessageLength })
        {
            return Error.Validation("subscription_requests.message_too_long", $"El mensaje no puede superar {MaxMessageLength} caracteres.");
        }

        if (CardDataGuard.LooksLikeCardNumber(text))
        {
            return Error.Validation("subscription_requests.message_sensitive", "El mensaje parece contener un número de tarjeta: no escriba datos de pago.");
        }

        return new SubscriptionRequest
        {
            Id = Guid.CreateVersion7(), TenantId = tenantId, SubscriptionId = subscriptionId, Kind = kind, Status = SubscriptionRequestStatus.Pending,
            RequestedPlanId = planId, Message = text, RequestedBy = requestedBy, CreatedAt = now
        };
    }

    public Result Approve(string? note, Guid? by, DateTimeOffset now) => Resolve(SubscriptionRequestStatus.Approved, note, by, now);

    public Result Reject(string? note, Guid? by, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(note))
        {
            return Error.Validation("subscription_requests.note_required", "Indique la razón del rechazo: el gimnasio la verá.");
        }

        return Resolve(SubscriptionRequestStatus.Rejected, note, by, now);
    }

    public Result Withdraw(Guid? by, DateTimeOffset now) => Resolve(SubscriptionRequestStatus.Withdrawn, null, by, now);

    private Result Resolve(SubscriptionRequestStatus status, string? note, Guid? by, DateTimeOffset now)
    {
        if (Status != SubscriptionRequestStatus.Pending)
        {
            return Error.Conflict("subscription_requests.already_resolved", "La solicitud ya fue resuelta.");
        }

        var text = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (text is { Length: > MaxMessageLength })
        {
            return Error.Validation("subscription_requests.note_too_long", $"La nota no puede superar {MaxMessageLength} caracteres.");
        }

        if (CardDataGuard.LooksLikeCardNumber(text))
        {
            return Error.Validation("subscription_requests.note_sensitive", "La nota parece contener un número de tarjeta: no escriba datos de pago.");
        }

        Status = status;
        ResolutionNote = text;
        ResolvedBy = by;
        ResolvedAt = now;
        return Result.Success();
    }
}

public enum NoticeSeverity
{
    Info = 1,
    Warning = 2,
    Critical = 3
}

/// <summary>Códigos estables de los avisos comerciales (el frontend los traduce; el aviso solo guarda datos, nunca texto libre del equipo).</summary>
public static class NoticeTypes
{
    public const string TrialStarted = "trial_started";
    public const string TrialEnding = "trial_ending";
    public const string Activated = "activated";
    public const string DueSoon = "due_soon";
    public const string PastDue = "past_due";
    public const string GraceEnding = "grace_ending";
    public const string Expired = "expired";
    public const string Suspended = "suspended";
    public const string Reactivated = "reactivated";
    public const string Cancelled = "cancelled";
    public const string CancellationScheduled = "cancellation_scheduled";
    public const string CancellationResumed = "cancellation_resumed";
    public const string Renewed = "renewed";
    public const string PlanChanged = "plan_changed";
    public const string PlanChangeScheduled = "plan_change_scheduled";
    public const string Extended = "extended";
    public const string InvoiceIssued = "invoice_issued";
    public const string PaymentConfirmed = "payment_confirmed";
    public const string PaymentFailed = "payment_failed";
    public const string PaymentRefunded = "payment_refunded";

    /// <summary>Aviso que corresponde a un hecho del historial de la suscripción, o <c>null</c> si ese hecho no se avisa (alta interna, anulación de factura).</summary>
    public static (string Type, NoticeSeverity Severity)? For(SubscriptionEventType type) => type switch
    {
        SubscriptionEventType.TrialStarted => (TrialStarted, NoticeSeverity.Info),
        SubscriptionEventType.Activated => (Activated, NoticeSeverity.Info),
        SubscriptionEventType.PastDue => (PastDue, NoticeSeverity.Warning),
        SubscriptionEventType.Suspended => (Suspended, NoticeSeverity.Critical),
        SubscriptionEventType.Reactivated => (Reactivated, NoticeSeverity.Info),
        SubscriptionEventType.Expired => (Expired, NoticeSeverity.Critical),
        SubscriptionEventType.Cancelled => (Cancelled, NoticeSeverity.Warning),
        SubscriptionEventType.CancellationScheduled => (CancellationScheduled, NoticeSeverity.Info),
        SubscriptionEventType.CancellationResumed => (CancellationResumed, NoticeSeverity.Info),
        SubscriptionEventType.Renewed => (Renewed, NoticeSeverity.Info),
        SubscriptionEventType.PlanChanged => (PlanChanged, NoticeSeverity.Info),
        SubscriptionEventType.PlanChangeScheduled => (PlanChangeScheduled, NoticeSeverity.Info),
        SubscriptionEventType.Extended => (Extended, NoticeSeverity.Info),
        SubscriptionEventType.InvoiceIssued => (InvoiceIssued, NoticeSeverity.Info),
        SubscriptionEventType.PaymentConfirmed => (PaymentConfirmed, NoticeSeverity.Info),
        SubscriptionEventType.PaymentFailed => (PaymentFailed, NoticeSeverity.Warning),
        SubscriptionEventType.PaymentRefunded => (PaymentRefunded, NoticeSeverity.Info),
        _ => null
    };
}

/// <summary>
/// Aviso comercial interno, visible en el portal «Mi suscripción». No existe ningún canal externo (correo, SMS, WhatsApp): sin proveedor no se
/// simula un envío. Lo genera el proceso de vencimientos; <see cref="DedupeKey"/> hace que cada hecho se avise una sola vez por más veces que corra.
/// Guarda solo datos neutros (plan, fecha, número de factura): nunca las notas ni los motivos internos del equipo de plataforma.
/// </summary>
public sealed class SubscriptionNotice
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid? SubscriptionId { get; init; }
    public string Type { get; init; } = string.Empty;
    public NoticeSeverity Severity { get; init; }
    public string DedupeKey { get; init; } = string.Empty;
    public string? PlanName { get; init; }
    public DateOnly? RelatedDate { get; init; }
    public string? Reference { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ReadAt { get; private set; }

    public void MarkRead(DateTimeOffset now) => ReadAt ??= now;
}
