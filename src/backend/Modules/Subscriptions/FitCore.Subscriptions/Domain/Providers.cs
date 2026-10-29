namespace FitCore.Subscriptions.Domain;

public enum ProviderEventOutcome
{
    /// <summary>El evento se aplicó (pago confirmado, intento fallido registrado, reembolso registrado).</summary>
    Applied = 1,

    /// <summary>El evento era válido y estaba firmado, pero no se pudo aplicar (importe distinto, factura inexistente o cerrada…). No cambió nada: queda para revisión manual.</summary>
    Rejected = 2,

    /// <summary>El evento es de un tipo que no interesa, o ya estaba aplicado por otro camino. No cambió nada.</summary>
    Ignored = 3
}

/// <summary>
/// Constancia de un evento recibido del proveedor de pagos. Es lo que hace los webhooks <b>idempotentes</b>: el par (proveedor, id del evento) es único,
/// así que un reenvío —el proveedor reintenta, o alguien repite la petición— nunca vuelve a aplicarse. Guarda solo lo mínimo para auditar (tipo, factura,
/// importe, moneda, resultado): nunca el cuerpo completo de la petición ni datos de tarjeta. Solo se agrega; no se edita ni se borra.
/// </summary>
public sealed class ProviderEventRecord
{
    public const int MaxFailureReason = 200;

    public Guid Id { get; init; }
    public string Provider { get; init; } = string.Empty;
    public string ExternalEventId { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public Guid? TenantId { get; init; }
    public Guid? InvoiceId { get; init; }
    public string? InvoiceNumber { get; init; }
    public string? ProviderReference { get; init; }
    public decimal? Amount { get; init; }
    public string? Currency { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
    public DateTimeOffset ReceivedAt { get; init; }
    public ProviderEventOutcome Outcome { get; init; }

    /// <summary>Código estable del motivo cuando no se aplicó (por ejemplo <c>billing.amount_mismatch</c>).</summary>
    public string? OutcomeCode { get; init; }
}
