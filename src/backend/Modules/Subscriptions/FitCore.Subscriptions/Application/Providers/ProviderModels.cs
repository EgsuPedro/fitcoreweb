using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FitCore.Application.Core;
using FitCore.SharedKernel;
using FitCore.Subscriptions.Domain;
using Microsoft.Extensions.Options;

namespace FitCore.Subscriptions.Application.Providers;

/// <summary>Configuración del proveedor de pagos (sección <c>Subscriptions:Payments</c>). Sin secreto no hay proveedor: los webhooks se rechazan y todo sigue siendo manual.</summary>
public sealed class PaymentProviderOptions
{
    public const string SectionName = "Subscriptions:Payments";

    /// <summary>Código del proveedor tal como se guarda en pagos y eventos (por ejemplo, el nombre de la pasarela contratada).</summary>
    public string ProviderCode { get; set; } = "webhook";

    /// <summary>Secreto compartido con el proveedor para firmar los webhooks. Viene de una variable de entorno o del gestor de secretos: nunca del repositorio.</summary>
    public string? WebhookSecret { get; set; }

    /// <summary>Secreto anterior, aceptado durante la rotación del secreto. Se retira al terminar.</summary>
    public string? PreviousWebhookSecret { get; set; }

    /// <summary>Diferencia máxima, en segundos, entre la marca de tiempo firmada y la hora del servidor. Acota el tiempo en que se puede reenviar una petición capturada.</summary>
    public int ToleranceSeconds { get; set; } = 300;

    /// <summary>Tamaño máximo del cuerpo aceptado, en bytes.</summary>
    public int MaxBodyBytes { get; set; } = 65_536;
}

public enum ProviderEventType
{
    PaymentSucceeded = 1,
    PaymentFailed = 2,
    PaymentRefunded = 3
}

/// <summary>Lo que el proveedor comunica, ya verificado y traducido al lenguaje de FitCore 360. No contiene datos de tarjeta.</summary>
public sealed record ProviderNotification(
    string ExternalEventId, ProviderEventType Type, string InvoiceNumber, string? ProviderReference, decimal Amount, string Currency, DateTimeOffset OccurredAt, string? FailureReason);

/// <summary>
/// Puerto hacia el proveedor de pagos. Hoy solo recibe: verifica la autenticidad de un webhook y lo traduce. Un proveedor real se agrega implementando
/// esta interfaz con su propio esquema de firma y formato; nada más del sistema cambia. <b>No se simula ningún proveedor</b>: sin secreto configurado
/// <see cref="IsConfigured"/> es falso y el sistema funciona solo con pagos registrados a mano.
/// </summary>
public interface IPaymentProviderGateway
{
    string Code { get; }

    bool IsConfigured { get; }

    int MaxBodyBytes { get; }

    /// <summary>Verifica la firma y la vigencia de la marca de tiempo del cuerpo recibido y lo traduce. No lee la base ni cambia nada.</summary>
    Result<ProviderNotification> VerifyAndParse(string? signatureHeader, ReadOnlySpan<byte> body, DateTimeOffset now);
}

/// <summary>
/// Contrato de webhook firmado de FitCore 360: encabezado <c>X-FitCore-Signature: t=&lt;segundos unix&gt;,v1=&lt;hex&gt;</c> donde <c>v1 = HMAC-SHA256(secreto, "t.cuerpo")</c>.
/// La marca de tiempo va dentro de lo firmado (no se puede reutilizar una firma con otra hora), se rechaza fuera de tolerancia y la comparación es de tiempo
/// constante. Cuerpo JSON: <c>{ "id", "type": "payment.succeeded|payment.failed|payment.refunded", "occurredAt", "data": { "invoiceNumber", "providerReference", "amount", "currency", "failureReason" } }</c>.
/// Un adaptador de un proveedor concreto reemplaza esta clase con su propio esquema.
/// </summary>
public sealed class SignedWebhookGateway(IOptions<PaymentProviderOptions> options) : IPaymentProviderGateway
{
    public const string SignatureHeader = "X-FitCore-Signature";

    private PaymentProviderOptions Options => options.Value;

    public string Code => Options.ProviderCode.Trim().ToLowerInvariant();

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Options.WebhookSecret) && Options.WebhookSecret.Length >= 16;

    public int MaxBodyBytes => Options.MaxBodyBytes;

    public Result<ProviderNotification> VerifyAndParse(string? signatureHeader, ReadOnlySpan<byte> body, DateTimeOffset now)
    {
        if (!IsConfigured)
        {
            return ProviderErrors.NotConfigured;
        }

        if (body.Length == 0 || body.Length > Options.MaxBodyBytes)
        {
            return ProviderErrors.PayloadInvalid;
        }

        if (!TryParseSignature(signatureHeader, out var timestamp, out var signatures))
        {
            return ProviderErrors.SignatureInvalid;
        }

        var secrets = new[] { Options.WebhookSecret, Options.PreviousWebhookSecret }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!);
        var matches = false;
        foreach (var secret in secrets)
        {
            var expected = Sign(secret, timestamp, body);
            foreach (var candidate in signatures)
            {
                // Se recorren todas las combinaciones sin cortar al primer acierto, y CryptographicOperations compara en tiempo constante.
                matches |= CryptographicOperations.FixedTimeEquals(expected, candidate);
            }
        }

        if (!matches)
        {
            return ProviderErrors.SignatureInvalid;
        }

        // Solo después de comprobar la firma se mira la hora: así un atacante sin el secreto no aprende nada de la tolerancia.
        var signedAt = DateTimeOffset.FromUnixTimeSeconds(timestamp);
        if (Math.Abs((now - signedAt).TotalSeconds) > Options.ToleranceSeconds)
        {
            return ProviderErrors.TimestampOutOfTolerance;
        }

        return Parse(body);
    }

    /// <summary>Firma de un cuerpo con una marca de tiempo. Pública para que las pruebas y el proveedor firmen igual que el verificador.</summary>
    public static byte[] Sign(string secret, long timestamp, ReadOnlySpan<byte> body)
    {
        var prefix = Encoding.UTF8.GetBytes(timestamp.ToString(CultureInfo.InvariantCulture) + ".");
        var payload = new byte[prefix.Length + body.Length];
        prefix.CopyTo(payload, 0);
        body.CopyTo(payload.AsSpan(prefix.Length));
        return HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), payload);
    }

    private static bool TryParseSignature(string? header, out long timestamp, out List<byte[]> signatures)
    {
        timestamp = 0;
        signatures = [];
        if (string.IsNullOrWhiteSpace(header) || header.Length > 512)
        {
            return false;
        }

        var haveTimestamp = false;
        foreach (var part in header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }

            var name = part[..eq];
            var value = part[(eq + 1)..];
            if (name == "t" && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out timestamp))
            {
                haveTimestamp = true;
            }
            else if (name == "v1" && value.Length == 64 && Convert.FromHexString(value.ToUpperInvariant()) is { Length: 32 } bytes)
            {
                signatures.Add(bytes);
            }
        }

        return haveTimestamp && signatures.Count > 0;
    }

    private static Result<ProviderNotification> Parse(ReadOnlySpan<byte> body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body.ToArray());
            var root = doc.RootElement;
            var id = root.GetProperty("id").GetString();
            var type = root.GetProperty("type").GetString() switch
            {
                "payment.succeeded" => ProviderEventType.PaymentSucceeded,
                "payment.failed" => ProviderEventType.PaymentFailed,
                "payment.refunded" => ProviderEventType.PaymentRefunded,
                _ => (ProviderEventType?)null
            };
            var occurredAt = root.GetProperty("occurredAt").GetDateTimeOffset();
            var data = root.GetProperty("data");
            var number = data.GetProperty("invoiceNumber").GetString();
            var reference = data.TryGetProperty("providerReference", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
            var amount = data.GetProperty("amount").GetDecimal();
            var currency = data.GetProperty("currency").GetString();
            var reason = data.TryGetProperty("failureReason", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;

            if (type is null || string.IsNullOrWhiteSpace(id) || id.Length > 100 || string.IsNullOrWhiteSpace(number) || number.Length > 40
                || string.IsNullOrWhiteSpace(currency) || currency.Length != 3 || amount <= 0m || amount > Plan.MaxPrice || decimal.Round(amount, 2) != amount
                || reference is { Length: > 100 } || (type != ProviderEventType.PaymentFailed && string.IsNullOrWhiteSpace(reference)))
            {
                return ProviderErrors.PayloadInvalid;
            }

            // Un texto libre del proveedor nunca se guarda tal cual: se recorta y se descarta si parece un número de tarjeta.
            var safeReason = string.IsNullOrWhiteSpace(reason) || CardDataGuard.LooksLikeCardNumber(reason)
                ? null
                : reason.Trim()[..Math.Min(reason.Trim().Length, ProviderEventRecord.MaxFailureReason)];
            return new ProviderNotification(id.Trim(), type.Value, number.Trim(), reference?.Trim(), amount, currency.Trim().ToUpperInvariant(), occurredAt, safeReason);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return ProviderErrors.PayloadInvalid;
        }
    }
}

public static class ProviderErrors
{
    public static readonly Error NotConfigured = Error.Failure("payments.provider_not_configured", "No hay un proveedor de pagos configurado.");
    public static readonly Error SignatureInvalid = Error.Unauthorized("payments.signature_invalid", "La firma del webhook no es válida.");
    public static readonly Error TimestampOutOfTolerance = Error.Unauthorized("payments.timestamp_out_of_tolerance", "La marca de tiempo del webhook está fuera de la tolerancia.");
    public static readonly Error PayloadInvalid = Error.Validation("payments.payload_invalid", "El contenido del webhook no es válido.");
}

/// <summary>Estado de la integración con el proveedor, para la plataforma. Nunca incluye el secreto.</summary>
public sealed record ProviderStatusDto(bool Configured, string Provider, string SignatureHeader, string WebhookPath, int ToleranceSeconds, bool RotationInProgress);

public sealed record ProviderEventDto(
    Guid Id, string Provider, string ExternalEventId, string Type, Guid? TenantId, string? TenantName, Guid? InvoiceId, string? InvoiceNumber, string? ProviderReference,
    decimal? Amount, string? Currency, DateTimeOffset OccurredAt, DateTimeOffset ReceivedAt, string Outcome, string? OutcomeCode);

/// <summary>Respuesta al proveedor: solo si el evento fue procesado, nunca detalles internos.</summary>
public sealed record WebhookReceiptDto(bool Received, string Outcome, bool Duplicate);

public sealed record ProcessProviderEventCommand(ProviderNotification Notification) : ICommand<WebhookReceiptDto>;

public sealed record GetProviderStatusQuery : IQuery<ProviderStatusDto>;

public sealed record ListProviderEventsQuery(PageRequest Page, string? Outcome, Guid? TenantId) : IQuery<PagedResult<ProviderEventDto>>;
