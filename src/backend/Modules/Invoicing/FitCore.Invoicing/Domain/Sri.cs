namespace FitCore.Invoicing.Domain;

/// <summary>Estado con que el servicio de recepción del SRI contesta a <c>validarComprobante</c>: <c>RECIBIDA</c> o <c>DEVUELTA</c> (ficha técnica, sección 7.2).</summary>
public enum SriReceptionState
{
    Received = 1,
    Returned = 2
}

/// <summary>
/// Estado de una autorización. La ficha nombra los estados como procesamiento (PPR), autorizado (AUT) y no autorizado (NAT) y sus ejemplos muestran «AUTORIZADO» y «RECHAZADO»; el texto exacto
/// que devuelve hoy el servicio se confirma en el ambiente de pruebas (bloque 7), así que se interpreta con tolerancia y se conserva el texto original.
/// </summary>
public enum SriAuthorizationState
{
    Authorized = 1,
    NotAuthorized = 2,
    Processing = 3,

    /// <summary>Texto que no se reconoce: se trata como «aún sin resolver» y se registra para revisarlo.</summary>
    Unknown = 4
}

/// <summary>Mensaje del SRI (<c>identificador</c>, <c>mensaje</c>, <c>informacionAdicional</c>, <c>tipo</c> ERROR o ADVERTENCIA). Texto del SRI: se guarda tal cual, sin datos del sistema.</summary>
public sealed record SriMessage(string? Id, string? Text, string? Detail, string? Type)
{
    public bool IsError => string.Equals(Type, "ERROR", StringComparison.OrdinalIgnoreCase);
}

public sealed record SriReception(SriReceptionState State, string? RawState, IReadOnlyList<SriMessage> Messages);

public sealed record SriAuthorization(
    SriAuthorizationState State,
    string? RawState,
    string? Number,
    DateTimeOffset? AuthorizedAt,
    string? EnvironmentName,
    string? AuthorizationXml,
    IReadOnlyList<SriMessage> Messages);

/// <summary>Respuesta a una consulta de autorización por clave de acceso. Sin autorizaciones (<c>numeroComprobantes</c> = 0) el SRI todavía no resolvió o no conoce la clave.</summary>
public sealed record SriAuthorizationResult(string? QueriedKey, IReadOnlyList<SriAuthorization> Authorizations);

/// <summary>El SRI no se pudo consultar o contestó algo ilegible (red, tiempo agotado, HTTP 5xx, falla SOAP, respuesta mal formada). Es transitorio: se reintenta, nunca rechaza el comprobante.</summary>
public sealed class SriUnavailableException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>Código estable (<c>sri_timeout</c>, <c>sri_unreachable</c>, <c>sri_http_503</c>, <c>sri_soap_fault</c>, <c>sri_bad_response</c>…); es lo único que se guarda del fallo.</summary>
    public string Code { get; } = code;
}

public static class SriMessageCodes
{
    /// <summary>43 «clave de acceso registrada» y 70 «clave de acceso en procesamiento»: el SRI ya conoce el comprobante. No se reenvía: se consulta su autorización (ficha, sección 10, notas 1 y 2).</summary>
    public static bool MeansAlreadyKnown(IEnumerable<SriMessage> messages) => messages.Any(m => m.Id?.Trim() is "43" or "70");

    /// <summary>Resumen corto y legible para el historial del comprobante («35: DOCUMENTO INVÁLIDO; 52: ERROR EN DIFERENCIAS»).</summary>
    public static string Summary(IEnumerable<SriMessage> messages, int maxLength = 400)
    {
        var text = string.Join("; ", messages.Where(m => m.IsError || m.Type is null).Select(m => $"{m.Id}: {m.Text}".Trim(' ', ':')).Where(s => s.Length > 0));
        if (text.Length == 0)
        {
            text = string.Join("; ", messages.Select(m => $"{m.Id}: {m.Text}".Trim(' ', ':')).Where(s => s.Length > 0));
        }

        return text.Length <= maxLength ? text : text[..(maxLength - 1)] + "…";
    }
}

public static class SriAuthorizationStates
{
    public static SriAuthorizationState Parse(string? raw)
    {
        var text = (raw ?? string.Empty).Trim().ToUpperInvariant();
        if (text.Length == 0)
        {
            return SriAuthorizationState.Unknown;
        }

        // «NO AUTORIZADO» contiene «AUTORIZADO»: se descarta primero.
        if (text is "NAT" || text.StartsWith("NO AUTORIZ", StringComparison.Ordinal) || text.StartsWith("RECHAZ", StringComparison.Ordinal))
        {
            return SriAuthorizationState.NotAuthorized;
        }

        if (text is "AUT" || text.StartsWith("AUTORIZ", StringComparison.Ordinal))
        {
            return SriAuthorizationState.Authorized;
        }

        if (text is "PPR" || text.Contains("PROCES", StringComparison.Ordinal))
        {
            return SriAuthorizationState.Processing;
        }

        return SriAuthorizationState.Unknown;
    }
}

/// <summary>Puerto hacia los servicios web del SRI (esquema offline). La implementación real habla SOAP; las pruebas usan una simulada. Nunca registra el XML ni datos del comprador.</summary>
public interface ISriGateway
{
    /// <summary><c>validarComprobante</c>: envía el XML firmado. Lanza <see cref="SriUnavailableException"/> si el SRI no responde algo utilizable.</summary>
    Task<SriReception> SendAsync(SriEnvironment environment, string signedXml, CancellationToken cancellationToken);

    /// <summary><c>autorizacionComprobante</c>: consulta el resultado por clave de acceso.</summary>
    Task<SriAuthorizationResult> QueryAuthorizationAsync(SriEnvironment environment, string accessKey, CancellationToken cancellationToken);
}

/// <summary>
/// Calendario de la transmisión. Fallos del SRI: espera creciente 30 s → 30 min con variación aleatoria para no golpear todos a la vez. Consulta de autorización: 10 s → 15 min (la ficha sugiere esperar
/// antes de consultar y fija hasta 24 horas desde «RECIBIDA»). Pasado el plazo máximo el trabajo se «estaciona» (queda a la vista para una persona) en lugar de reintentar sin fin.
/// </summary>
public static class TransmissionSchedule
{
    private static readonly TimeSpan[] UnavailableSteps = [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30)];
    private static readonly TimeSpan[] PollSteps = [TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(40), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(15)];

    /// <param name="attempts">Intentos fallidos seguidos (1 = el primero que falló).</param>
    /// <param name="jitter">Valor entre 0 y 1 (por defecto aleatorio); permite pruebas deterministas.</param>
    public static TimeSpan AfterUnavailable(int attempts, double? jitter = null) =>
        WithJitter(UnavailableSteps[Math.Clamp(attempts - 1, 0, UnavailableSteps.Length - 1)], jitter);

    /// <param name="polls">Consultas ya hechas sin respuesta definitiva (0 = justo después de «RECIBIDA»).</param>
    public static TimeSpan AfterPending(int polls, double? jitter = null) =>
        WithJitter(PollSteps[Math.Clamp(polls, 0, PollSteps.Length - 1)], jitter);

    /// <summary>¿Pasó el plazo desde el primer intento? Entonces se estaciona.</summary>
    public static bool ShouldPark(DateTimeOffset firstAttemptAt, DateTimeOffset now, TimeSpan maxAge) => now - firstAttemptAt >= maxAge;

    // ±20 % alrededor del paso.
    private static TimeSpan WithJitter(TimeSpan step, double? jitter) =>
        TimeSpan.FromTicks((long)(step.Ticks * (0.8 + (0.4 * (jitter ?? Random.Shared.NextDouble())))));
}
