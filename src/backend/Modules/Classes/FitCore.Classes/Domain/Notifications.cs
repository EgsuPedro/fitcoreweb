using FitCore.SharedKernel;

namespace FitCore.Classes.Domain;

/// <summary>Qué se le avisa al usuario. Cada tipo tiene su preferencia; los críticos no se pueden apagar.</summary>
public enum NotificationType
{
    ReservationConfirmed = 1,
    WaitlistJoined = 2,

    /// <summary>Se le reservó un cupo con plazo para aceptarlo.</summary>
    WaitlistOffer = 3,

    WaitlistOfferExpired = 4,
    ReservationCancelledByGym = 5,

    /// <summary>La sesión se canceló (al alumno con reserva y al instructor).</summary>
    SessionCancelled = 6,

    SessionRescheduled = 7,
    ClassReminder = 8
}

public enum NotificationStatus
{
    /// <summary>Registrada y pendiente de entregar por su canal (se reintenta si falla de forma transitoria).</summary>
    Pending = 1,

    Delivered = 2,

    /// <summary>Se agotaron los reintentos o el canal rechazó el envío de forma definitiva.</summary>
    Failed = 3
}

/// <summary>Canales. Solo <see cref="InApp"/> existe: los demás requieren un proveedor y credenciales configurados, y no se simulan.</summary>
public static class NotificationChannels
{
    public const string InApp = "InApp";
    public const string Email = "Email";
    public const string Sms = "Sms";
    public const string WhatsApp = "WhatsApp";
    public const string Push = "Push";

    public static readonly IReadOnlyList<string> All = [InApp, Email, Sms, WhatsApp, Push];
}

public sealed record NotificationTypeInfo(NotificationType Type, string Label, string Description, bool Critical);

public static class NotificationCatalog
{
    public static readonly IReadOnlyList<NotificationTypeInfo> All =
    [
        new(NotificationType.ReservationConfirmed, "Reserva confirmada", "Cuando tu reserva queda confirmada.", false),
        new(NotificationType.WaitlistJoined, "Lista de espera", "Cuando entras a la lista de espera de una clase llena.", false),
        new(NotificationType.WaitlistOffer, "Cupo disponible", "Cuando se libera un cupo para ti y tienes un plazo para aceptarlo.", true),
        new(NotificationType.WaitlistOfferExpired, "Invitación vencida", "Cuando el plazo para aceptar un cupo termina sin respuesta.", false),
        new(NotificationType.ReservationCancelledByGym, "Reserva cancelada por el gimnasio", "Cuando el gimnasio cancela tu reserva.", true),
        new(NotificationType.SessionCancelled, "Clase cancelada", "Cuando se cancela una clase en la que estás inscrito o que impartes.", true),
        new(NotificationType.SessionRescheduled, "Cambio de horario", "Cuando una clase cambia de día, hora o instructor.", true),
        new(NotificationType.ClassReminder, "Recordatorio de clase", "Un aviso antes de que empiece tu clase.", false)
    ];

    public static NotificationTypeInfo Info(NotificationType type) => All.First(i => i.Type == type);

    public static bool IsCritical(NotificationType type) => Info(type).Critical;
}

/// <summary>
/// Aviso para un usuario dentro del sistema (su bandeja). El <b>registro</b> (qué se avisa, a quién, con qué clave anti-duplicado) se separa
/// de la <b>entrega</b> (estado, reintentos, error): hoy el único canal es la propia bandeja, que se entrega al registrarse; cuando exista un
/// proveedor externo, sus avisos nacen pendientes y los entrega el Worker con reintentos. No es auditable a propósito: el texto es del
/// destinatario y la auditoría registra hechos, no contenido.
/// </summary>
public sealed class InboxNotification : Entity<Guid>, ITenantOwned
{
    public const int MaxTitleLength = 120;
    public const int MaxBodyLength = 400;
    public const int MaxDedupeKeyLength = 200;
    public const int MaxErrorLength = 200;

    private InboxNotification(Guid id)
        : base(id)
    {
    }

    public Guid TenantId { get; private set; }
    public Guid RecipientUserId { get; private set; }
    public Guid? BranchId { get; private set; }
    public NotificationType Type { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public string Body { get; private set; } = string.Empty;

    /// <summary>Referencias para que la pantalla abra lo que corresponde (sesión, reserva…). Solo identificadores y fechas.</summary>
    public string Data { get; private set; } = "{}";

    /// <summary>Clave que identifica el hecho que se avisa: el mismo hecho nunca genera dos avisos al mismo usuario.</summary>
    public string DedupeKey { get; private set; } = string.Empty;

    public string Channel { get; private set; } = NotificationChannels.InApp;
    public NotificationStatus Status { get; private set; }
    public int Attempts { get; private set; }
    public DateTimeOffset? NextAttemptAt { get; private set; }

    /// <summary>Código corto del último fallo del canal. Nunca el texto de una excepción ni datos del destinatario.</summary>
    public string? LastError { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? DeliveredAt { get; private set; }
    public DateTimeOffset? ReadAt { get; private set; }

    public bool IsRead => ReadAt is not null;

    public static InboxNotification Create(
        Guid tenantId,
        Guid recipientUserId,
        Guid? branchId,
        NotificationType type,
        string title,
        string body,
        string data,
        string dedupeKey,
        string channel,
        DateTimeOffset now) =>
        new(Guid.CreateVersion7())
        {
            TenantId = tenantId,
            RecipientUserId = recipientUserId,
            BranchId = branchId,
            Type = type,
            Title = Truncate(title, MaxTitleLength),
            Body = Truncate(body, MaxBodyLength),
            Data = data,
            DedupeKey = Truncate(dedupeKey, MaxDedupeKeyLength),
            Channel = channel,
            // La bandeja interna se entrega al registrarse; un canal externo espera al Worker.
            Status = channel == NotificationChannels.InApp ? NotificationStatus.Delivered : NotificationStatus.Pending,
            DeliveredAt = channel == NotificationChannels.InApp ? now : null,
            NextAttemptAt = channel == NotificationChannels.InApp ? null : now,
            CreatedAt = now
        };

    public void MarkRead(DateTimeOffset now) => ReadAt ??= now;

    public void MarkDelivered(DateTimeOffset now)
    {
        Status = NotificationStatus.Delivered;
        DeliveredAt = now;
        NextAttemptAt = null;
        LastError = null;
    }

    /// <summary>Un fallo transitorio se reintenta con espera creciente hasta agotar los intentos; uno definitivo no se reintenta.</summary>
    public void RecordFailure(string errorCode, bool transient, DateTimeOffset now)
    {
        Attempts++;
        LastError = Truncate(errorCode, MaxErrorLength);
        if (!transient || Attempts >= NotificationRetryPolicy.MaxAttempts)
        {
            Status = NotificationStatus.Failed;
            NextAttemptAt = null;
            return;
        }

        NextAttemptAt = now + NotificationRetryPolicy.DelayAfter(Attempts);
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}

public static class NotificationRetryPolicy
{
    public const int MaxAttempts = 5;

    /// <summary>Espera antes del siguiente intento: 1 min, 5 min, 30 min, 2 h.</summary>
    public static TimeSpan DelayAfter(int attempts) => attempts switch
    {
        <= 1 => TimeSpan.FromMinutes(1),
        2 => TimeSpan.FromMinutes(5),
        3 => TimeSpan.FromMinutes(30),
        _ => TimeSpan.FromHours(2)
    };
}

/// <summary>Preferencia de un usuario por un tipo de aviso. Sin fila, el aviso está activado.</summary>
public sealed class NotificationPreference : ITenantOwned
{
    public Guid TenantId { get; init; }
    public Guid UserId { get; init; }
    public NotificationType Type { get; init; }
    public bool Enabled { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

// ---- Eventos de dominio: salen en la misma transacción que el cambio (outbox) y de ellos nacen los avisos --------------------------------

public sealed record ReservationConfirmed(Guid ReservationId, Guid SessionId, Guid MemberId, Guid EventId, DateTimeOffset OccurredAt) : DomainEvent(EventId, OccurredAt);

public sealed record WaitlistJoined(Guid ReservationId, Guid SessionId, Guid MemberId, Guid EventId, DateTimeOffset OccurredAt) : DomainEvent(EventId, OccurredAt);

public sealed record WaitlistOfferMade(Guid ReservationId, Guid SessionId, Guid MemberId, DateTimeOffset ExpiresAt, Guid EventId, DateTimeOffset OccurredAt) : DomainEvent(EventId, OccurredAt);

public sealed record WaitlistOfferExpired(Guid ReservationId, Guid SessionId, Guid MemberId, Guid EventId, DateTimeOffset OccurredAt) : DomainEvent(EventId, OccurredAt);

/// <param name="BySession">Se canceló porque la sesión se canceló.</param>
/// <param name="CancelledBy">Usuario que la canceló (para no avisar al alumno de lo que hizo él mismo).</param>
public sealed record ReservationCancelled(Guid ReservationId, Guid SessionId, Guid MemberId, Guid? CancelledBy, bool BySession, string? SessionReason, Guid EventId, DateTimeOffset OccurredAt) : DomainEvent(EventId, OccurredAt);

public sealed record SessionRescheduled(Guid SessionId, DateTimeOffset OldStartsAt, DateTimeOffset NewStartsAt, Guid OldInstructorId, Guid NewInstructorId, Guid EventId, DateTimeOffset OccurredAt) : DomainEvent(EventId, OccurredAt);

public sealed record SessionCancelled(Guid SessionId, string Reason, Guid EventId, DateTimeOffset OccurredAt) : DomainEvent(EventId, OccurredAt);
