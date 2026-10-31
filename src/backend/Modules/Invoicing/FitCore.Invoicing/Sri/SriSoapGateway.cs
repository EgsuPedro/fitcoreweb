using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using FitCore.Invoicing.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FitCore.Invoicing.Sri;

/// <summary>
/// Configuración del acceso al SRI (sección <c>Invoicing:Sri</c>). Las direcciones son las de los servicios <c>RecepcionComprobantesOffline</c> y <c>AutorizacionComprobantesOffline</c> publicadas en la
/// ficha técnica y comprobadas en el WSDL del ambiente de pruebas; se pueden cambiar sin recompilar porque el SRI puede mover los servicios.
/// </summary>
public sealed class SriOptions
{
    public const string SectionName = "Invoicing:Sri";

    public string TestBaseUrl { get; set; } = "https://celcer.sri.gob.ec/comprobantes-electronicos-ws/";
    public string ProductionBaseUrl { get; set; } = "https://cel.sri.gob.ec/comprobantes-electronicos-ws/";

    /// <summary>Tiempo máximo de cada llamada al SRI.</summary>
    public int RequestTimeoutSeconds { get; set; } = 30;

    /// <summary>Segundos que se espera tras «RECIBIDA» antes de la primera consulta (la ficha pide esperar; parametrizable).</summary>
    public int AuthorizationDelaySeconds { get; set; } = 10;

    /// <summary>Horas desde el primer intento tras las cuales un comprobante sin resolver se «estaciona» para revisión humana (la autorización tarda como máximo 24 h desde «RECIBIDA»).</summary>
    public int MaxAgeHours { get; set; } = 48;

    /// <summary>Comprobantes que se procesan por gimnasio en cada ciclo del Worker.</summary>
    public int BatchSize { get; set; } = 20;

    /// <summary>Cada cuántos segundos revisa la cola el Worker.</summary>
    public int IntervalSeconds { get; set; } = 15;

    public string BaseUrl(SriEnvironment environment) => environment == SriEnvironment.Production ? ProductionBaseUrl : TestBaseUrl;
}

/// <summary>
/// Interpreta las respuestas SOAP 1.1 de los servicios del SRI (contrato verificado en sus WSDL: documento/literal, elementos hijos sin espacio de nombres). Lee con DTD prohibido y sin resolver nada
/// externo, y cualquier cosa que no tenga la forma esperada es «SRI no disponible» (transitorio), nunca una decisión sobre el comprobante.
/// </summary>
public static class SriSoapParser
{
    public static SriReception ParseReception(string soap)
    {
        var body = SoapBody(soap);
        var response = body.Descendants().FirstOrDefault(e => e.Name.LocalName == "RespuestaRecepcionComprobante")
            ?? throw new SriUnavailableException("sri_bad_response", "La respuesta de recepción no trae RespuestaRecepcionComprobante.");
        var raw = Text(response, "estado");
        var state = raw?.Trim().ToUpperInvariant() switch
        {
            "RECIBIDA" => SriReceptionState.Received,
            "DEVUELTA" => SriReceptionState.Returned,
            _ => throw new SriUnavailableException("sri_bad_response", $"Estado de recepción desconocido: «{Truncate(raw, 40)}».")
        };

        return new SriReception(state, raw, Messages(response));
    }

    public static SriAuthorizationResult ParseAuthorization(string soap)
    {
        var body = SoapBody(soap);
        var response = body.Descendants().FirstOrDefault(e => e.Name.LocalName == "RespuestaAutorizacionComprobante")
            ?? throw new SriUnavailableException("sri_bad_response", "La respuesta de autorización no trae RespuestaAutorizacionComprobante.");
        var list = new List<SriAuthorization>();
        foreach (var element in response.Descendants().Where(e => e.Name.LocalName == "autorizacion"))
        {
            var raw = Text(element, "estado");
            DateTimeOffset? at = null;
            if (Text(element, "fechaAutorizacion") is { } dateText && DateTimeOffset.TryParse(dateText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                // El SRI envía la hora con su zona (-05:00); PostgreSQL solo acepta UTC en timestamptz. El instante es el mismo.
                at = parsed.ToUniversalTime();
            }

            list.Add(new SriAuthorization(
                SriAuthorizationStates.Parse(raw), raw, Text(element, "numeroAutorizacion"), at, Text(element, "ambiente"),
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" + element.ToString(SaveOptions.DisableFormatting), Messages(element)));
        }

        return new SriAuthorizationResult(Text(response, "claveAccesoConsultada"), list);
    }

    /// <summary>Lanza «falla SOAP» si el cuerpo es una falla; si es otra cosa (o ilegible) lanza otro código o nada.</summary>
    public static void ThrowIfFault(string soap) => _ = SoapBody(soap);

    private static XElement SoapBody(string soap)
    {
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(new StringReader(soap), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersFromEntities = 0 });
            document = XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException ex)
        {
            throw new SriUnavailableException("sri_bad_response", "La respuesta del SRI no es XML válido.", ex);
        }

        var body = document.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Body")
            ?? throw new SriUnavailableException("sri_bad_response", "La respuesta del SRI no es un sobre SOAP.");
        if (body.Elements().FirstOrDefault(e => e.Name.LocalName == "Fault") is { } fault)
        {
            // El texto de la falla puede traer detalles internos del servidor: se conserva solo un recorte.
            throw new SriUnavailableException("sri_soap_fault", "El SRI respondió con una falla SOAP: " + Truncate(Text(fault, "faultstring"), 160));
        }

        return body;
    }

    private static List<SriMessage> Messages(XElement scope) =>
        [.. scope.Descendants().Where(e => e.Name.LocalName == "mensaje" && e.Elements().Any(c => c.Name.LocalName is "identificador" or "tipo" or "informacionAdicional"))
            .Select(m => new SriMessage(Truncate(Text(m, "identificador"), 20), Truncate(Text(m, "mensaje"), 400), Truncate(Text(m, "informacionAdicional"), 1500), Truncate(Text(m, "tipo"), 20)))];

    private static string? Text(XElement parent, string name)
    {
        var value = parent.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static string? Truncate(string? value, int max) => value is null || value.Length <= max ? value : value[..max];
}

/// <summary>
/// Cliente SOAP 1.1 de los servicios de recepción y autorización del SRI sobre <see cref="HttpClient"/> (sin WCF). Valida el certificado TLS del SRI con la configuración normal del sistema (nunca se
/// desactiva), no sigue redirecciones, limita el tamaño de lo que lee y no registra el XML ni datos del comprador: solo la operación, el ambiente, la duración y el resultado.
/// </summary>
public sealed partial class SriSoapGateway(HttpClient http, IOptions<SriOptions> options, ILogger<SriSoapGateway> logger) : ISriGateway
{
    private const int MaxResponseBytes = 4 * 1024 * 1024;

    public async Task<SriReception> SendAsync(SriEnvironment environment, string signedXml, CancellationToken cancellationToken)
    {
        var envelope =
            "<soapenv:Envelope xmlns:soapenv=\"http://schemas.xmlsoap.org/soap/envelope/\" xmlns:ec=\"http://ec.gob.sri.ws.recepcion\"><soapenv:Header/><soapenv:Body>" +
            $"<ec:validarComprobante><xml>{Convert.ToBase64String(Encoding.UTF8.GetBytes(signedXml))}</xml></ec:validarComprobante></soapenv:Body></soapenv:Envelope>";
        var response = await PostAsync(environment, "RecepcionComprobantesOffline", envelope, "recepcion", cancellationToken);
        return SriSoapParser.ParseReception(response);
    }

    public async Task<SriAuthorizationResult> QueryAuthorizationAsync(SriEnvironment environment, string accessKey, CancellationToken cancellationToken)
    {
        // La clave va dentro de un elemento XML: se exige su forma exacta (49 dígitos) para que nada ajeno llegue al sobre.
        if (!AccessKeyShape().IsMatch(accessKey))
        {
            throw new ArgumentException("La clave de acceso debe tener 49 dígitos.", nameof(accessKey));
        }

        var envelope =
            "<soapenv:Envelope xmlns:soapenv=\"http://schemas.xmlsoap.org/soap/envelope/\" xmlns:ec=\"http://ec.gob.sri.ws.autorizacion\"><soapenv:Header/><soapenv:Body>" +
            $"<ec:autorizacionComprobante><claveAccesoComprobante>{accessKey}</claveAccesoComprobante></ec:autorizacionComprobante></soapenv:Body></soapenv:Envelope>";
        var response = await PostAsync(environment, "AutorizacionComprobantesOffline", envelope, "autorizacion", cancellationToken);
        return SriSoapParser.ParseAuthorization(response);
    }

    private async Task<string> PostAsync(SriEnvironment environment, string service, string envelope, string operation, CancellationToken cancellationToken)
    {
        var url = new Uri(new Uri(options.Value.BaseUrl(environment), UriKind.Absolute), service);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(envelope, new System.Net.Http.Headers.MediaTypeHeaderValue("text/xml") { CharSet = "utf-8" }) };
            request.Headers.TryAddWithoutValidation("SOAPAction", "\"\"");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            var text = await ReadLimitedAsync(response, cancellationToken);
            // Una falla SOAP llega con HTTP 500 y cuerpo XML: se interpreta antes de decidir por el código HTTP.
            if (!response.IsSuccessStatusCode)
            {
                try
                {
                    SriSoapParser.ThrowIfFault(text);
                }
                catch (SriUnavailableException ex) when (ex.Code != "sri_soap_fault")
                {
                    // El cuerpo no era un sobre SOAP legible: manda el código HTTP.
                }

                throw new SriUnavailableException($"sri_http_{(int)response.StatusCode}", $"El SRI respondió HTTP {(int)response.StatusCode}.");
            }

            logger.LogInformation("SRI {Operation} {Environment}: HTTP {Status} en {Elapsed} ms", operation, environment, (int)response.StatusCode, stopwatch.ElapsedMilliseconds);
            return text;
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("SRI {Operation} {Environment}: tiempo agotado tras {Elapsed} ms", operation, environment, stopwatch.ElapsedMilliseconds);
            throw new SriUnavailableException("sri_timeout", "El SRI no respondió a tiempo.", ex);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning("SRI {Operation} {Environment}: sin conexión ({Reason})", operation, environment, ex.HttpRequestError);
            throw new SriUnavailableException("sri_unreachable", "No se pudo conectar con el SRI.", ex);
        }
    }

    private static async Task<string> ReadLimitedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > MaxResponseBytes)
        {
            throw new SriUnavailableException("sri_bad_response", "La respuesta del SRI es demasiado grande.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes)
            {
                throw new SriUnavailableException("sri_bad_response", "La respuesta del SRI es demasiado grande.");
            }

            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    [GeneratedRegex("^[0-9]{49}$")]
    private static partial Regex AccessKeyShape();
}
