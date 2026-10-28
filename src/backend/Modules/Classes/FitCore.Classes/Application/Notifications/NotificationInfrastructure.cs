using System.Text.Json;
using FitCore.Classes.Domain;
using FitCore.Classes.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FitCore.Classes.Application.Notifications;

/// <summary>Contenido que se entrega por un canal. Solo lo necesario: nada de datos del alumno más allá de lo que ya dice el aviso.</summary>
public sealed record NotificationMessage(Guid NotificationId, Guid RecipientUserId, string Type, string Title, string Body);

/// <param name="Succeeded">El canal aceptó el aviso.</param>
/// <param name="Transient">Si falló, si vale la pena reintentar (corte de red, límite de uso) o es definitivo (destinatario inválido).</param>
/// <param name="ErrorCode">Código corto del fallo. Nunca el texto de una excepción ni datos del destinatario: se guarda tal cual.</param>
public sealed record ChannelResult(bool Succeeded, bool Transient, string? ErrorCode)
{
    public static readonly ChannelResult Success = new(true, false, null);

    public static ChannelResult TransientFailure(string errorCode) => new(false, true, errorCode);

    public static ChannelResult PermanentFailure(string errorCode) => new(false, false, errorCode);
}

/// <summary>
/// Puerto de un canal de entrega. Hoy solo existe la bandeja interna. Email, SMS, WhatsApp y push se añaden implementando este puerto
/// <b>solo cuando haya un proveedor y credenciales configurados</b> (por configuración, nunca en el repositorio): un canal sin configurar
/// no se registra, y no se simula ningún envío exitoso.
/// </summary>
public interface INotificationChannel
{
    /// <summary>Uno de <see cref="NotificationChannels.All"/>.</summary>
    string Name { get; }

    bool IsConfigured { get; }

    Task<ChannelResult> SendAsync(NotificationMessage message, CancellationToken cancellationToken);
}

/// <summary>La bandeja del propio sistema: el aviso ya está guardado y visible, así que entregarlo es solo reconocerlo.</summary>
internal sealed class InAppChannel : INotificationChannel
{
    public string Name => NotificationChannels.InApp;

    public bool IsConfigured => true;

    public Task<ChannelResult> SendAsync(NotificationMessage message, CancellationToken cancellationToken) => Task.FromResult(ChannelResult.Success);
}

internal sealed record NewNotification(
    Guid TenantId,
    Guid RecipientUserId,
    Guid? BranchId,
    NotificationType Type,
    string Title,
    string Body,
    object Data,
    string DedupeKey);

internal static class NotificationWriter
{
    /// <summary>
    /// Registra un aviso respetando la preferencia del usuario (los críticos no se pueden apagar) y sin duplicar: la base descarta, por la
    /// clave del hecho, un aviso que ya existe para ese usuario, así que reintentar un evento nunca avisa dos veces.
    /// Devuelve true si se creó.
    /// </summary>
    public static async Task<bool> TryCreateAsync(ClassesDbContext db, NewNotification n, DateTimeOffset now, CancellationToken cancellationToken, string channel = NotificationChannels.InApp)
    {
        if (!NotificationCatalog.IsCritical(n.Type)
            && await db.NotificationPreferences.AsNoTracking().AnyAsync(p => p.UserId == n.RecipientUserId && p.Type == n.Type && !p.Enabled, cancellationToken))
        {
            return false;
        }

        var notification = InboxNotification.Create(
            n.TenantId, n.RecipientUserId, n.BranchId, n.Type, n.Title, n.Body, JsonSerializer.Serialize(n.Data), n.DedupeKey, channel, now);
        var type = notification.Type.ToString();
        var status = notification.Status.ToString();
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO classes.inbox_notifications
              (id, tenant_id, recipient_user_id, branch_id, type, title, body, data, dedupe_key, channel, status, attempts, created_at, delivered_at, next_attempt_at)
            VALUES
              ({notification.Id}, {notification.TenantId}, {notification.RecipientUserId}, {notification.BranchId}, {type}, {notification.Title}, {notification.Body},
               {notification.Data}::jsonb, {notification.DedupeKey}, {notification.Channel}, {status}, 0, {notification.CreatedAt}, {notification.DeliveredAt}, {notification.NextAttemptAt})
            ON CONFLICT (tenant_id, recipient_user_id, dedupe_key) DO NOTHING
            """,
            cancellationToken);
        return inserted == 1;
    }
}

/// <summary>Fechas en español y en la zona de la sucursal, sin depender de la cultura del servidor (en contenedores suele ser invariante).</summary>
internal static class NotificationText
{
    private static readonly string[] Days = ["domingo", "lunes", "martes", "miércoles", "jueves", "viernes", "sábado"];

    private static readonly string[] Months =
        ["enero", "febrero", "marzo", "abril", "mayo", "junio", "julio", "agosto", "septiembre", "octubre", "noviembre", "diciembre"];

    /// <summary>«lunes 2 de noviembre, 09:00».</summary>
    public static string When(DateTimeOffset instant, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(instant, zone);
        return $"{Days[(int)local.DayOfWeek]} {local.Day} de {Months[local.Month - 1]}, {local.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture)}";
    }

    public static string Time(DateTimeOffset instant, TimeZoneInfo zone) => TimeZoneInfo.ConvertTime(instant, zone).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
}
