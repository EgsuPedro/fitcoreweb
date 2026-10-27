namespace FitCore.Platform.Persistence;

/// <summary>Registro inmutable de auditoría (append-only: el rol de la aplicación no puede actualizar ni borrar).</summary>
public sealed class AuditLog
{
    public long Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid? UserId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string Entity { get; set; } = string.Empty;
    public string? EntityId { get; set; }
    public string? Before { get; set; }
    public string? After { get; set; }
    public string? IpAddress { get; set; }
    public string? CorrelationId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

/// <summary>Evento de dominio pendiente de publicar (patrón outbox transaccional).</summary>
public sealed class OutboxMessage
{
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public int Attempts { get; set; }
    public string? Error { get; set; }
}
