using System.Net;
using System.Text;
using FitCore.Invoicing.Domain;
using FitCore.Invoicing.Sri;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FitCore.Invoicing.UnitTests;

/// <summary>Respuestas SOAP de ejemplo: las de la ficha técnica del SRI (sección 7) y la forma observada contra el ambiente de pruebas real.</summary>
internal static class SriSamples
{
    public const string Received =
        """<soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/"><soap:Body><ns2:validarComprobanteResponse xmlns:ns2="http://ec.gob.sri.ws.recepcion"><RespuestaRecepcionComprobante><estado>RECIBIDA</estado><comprobantes/></RespuestaRecepcionComprobante></ns2:validarComprobanteResponse></soap:Body></soap:Envelope>""";

    public static string Returned(string id = "35", string text = "DOCUMENTO INVÁLIDO", string detail = "Se encontró el siguiente error en la estructura del comprobante: cvc-complex-type.2.4.a", string type = "ERROR") =>
        $"""<soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/"><soap:Body><ns2:validarComprobanteResponse xmlns:ns2="http://ec.gob.sri.ws.recepcion"><RespuestaRecepcionComprobante><estado>DEVUELTA</estado><comprobantes><comprobante><claveAcceso>1702201205176001321000110010030001000011234567816</claveAcceso><mensajes><mensaje><identificador>{id}</identificador><mensaje>{text}</mensaje><informacionAdicional>{detail}</informacionAdicional><tipo>{type}</tipo></mensaje></mensajes></comprobante></comprobantes></RespuestaRecepcionComprobante></ns2:validarComprobanteResponse></soap:Body></soap:Envelope>""";

    public const string SignedInvoice = """<?xml version="1.0" encoding="UTF-8"?><factura id="comprobante" version="1.1.0"><infoTributaria><ruc>1790000028001</ruc></infoTributaria></factura>""";

    public static string Authorization(string state, string? number = "0503201201176001321000110010030009900641234567814", string at = "2012-03-05T16:57:34.997-05:00", string extra = "") =>
        $"""<soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/"><soap:Body><ns2:autorizacionComprobanteResponse xmlns:ns2="http://ec.gob.sri.ws.autorizacion"><RespuestaAutorizacionComprobante><claveAccesoConsultada>0503201201176001321000110010030009900641234567814</claveAccesoConsultada><numeroComprobantes>1</numeroComprobantes><autorizaciones><autorizacion><estado>{state}</estado>{(number is null ? string.Empty : $"<numeroAutorizacion>{number}</numeroAutorizacion>")}<fechaAutorizacion>{at}</fechaAutorizacion><ambiente>PRUEBAS</ambiente><comprobante><![CDATA[{SignedInvoice}]]></comprobante><mensajes><mensaje><identificador>60</identificador><mensaje>ESTE PROCESO FUE REALIZADO EN EL AMBIENTE DE PRUEBAS</mensaje><tipo>ADVERTENCIA</tipo></mensaje>{extra}</mensajes></autorizacion></autorizaciones></RespuestaAutorizacionComprobante></ns2:autorizacionComprobanteResponse></soap:Body></soap:Envelope>""";

    public const string NoAuthorizations =
        """<soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/"><soap:Body><ns2:autorizacionComprobanteResponse xmlns:ns2="http://ec.gob.sri.ws.autorizacion"><RespuestaAutorizacionComprobante><claveAccesoConsultada>3010202601179000002800110010019999999990000000012</claveAccesoConsultada><numeroComprobantes>0</numeroComprobantes><autorizaciones/></RespuestaAutorizacionComprobante></ns2:autorizacionComprobanteResponse></soap:Body></soap:Envelope>""";

    public const string Fault =
        """<soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/"><soap:Body><soap:Fault><faultcode>soap:Server</faultcode><faultstring>Error interno con detalles del servidor que no deben mostrarse completos</faultstring></soap:Fault></soap:Body></soap:Envelope>""";
}

public class SriSoapParserTests
{
    [Fact]
    public void A_received_response_is_parsed()
    {
        var result = SriSoapParser.ParseReception(SriSamples.Received);

        Assert.Equal(SriReceptionState.Received, result.State);
        Assert.Equal("RECIBIDA", result.RawState);
        Assert.Empty(result.Messages);
    }

    [Fact]
    public void A_returned_response_carries_the_messages_of_the_SRI()
    {
        var result = SriSoapParser.ParseReception(SriSamples.Returned());

        Assert.Equal(SriReceptionState.Returned, result.State);
        var message = Assert.Single(result.Messages);
        Assert.Equal(("35", "DOCUMENTO INVÁLIDO", "ERROR"), (message.Id, message.Text, message.Type));
        Assert.StartsWith("Se encontró el siguiente error", message.Detail, StringComparison.Ordinal);
        Assert.True(message.IsError);
        Assert.Equal("35: DOCUMENTO INVÁLIDO", SriMessageCodes.Summary(result.Messages));
    }

    [Fact]
    public void An_authorized_response_keeps_the_authorization_element_with_the_signed_invoice_inside()
    {
        var result = SriSoapParser.ParseAuthorization(SriSamples.Authorization("AUTORIZADO"));

        Assert.Equal("0503201201176001321000110010030009900641234567814", result.QueriedKey);
        var authorization = Assert.Single(result.Authorizations);
        Assert.Equal(SriAuthorizationState.Authorized, authorization.State);
        Assert.Equal("0503201201176001321000110010030009900641234567814", authorization.Number);
        Assert.Equal(new DateTimeOffset(2012, 3, 5, 16, 57, 34, 997, TimeSpan.FromHours(-5)), authorization.AuthorizedAt);
        Assert.Equal("PRUEBAS", authorization.EnvironmentName);
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-8\"?><autorizacion>", authorization.AuthorizationXml, StringComparison.Ordinal);
        Assert.Contains("<![CDATA[<?xml version=\"1.0\" encoding=\"UTF-8\"?><factura id=\"comprobante\"", authorization.AuthorizationXml, StringComparison.Ordinal);
        var warning = Assert.Single(authorization.Messages);
        Assert.Equal(("60", "ADVERTENCIA"), (warning.Id, warning.Type));
        Assert.False(warning.IsError);
    }

    [Theory]
    [InlineData("AUTORIZADO", SriAuthorizationState.Authorized)]
    [InlineData("AUT", SriAuthorizationState.Authorized)]
    [InlineData("autorizado", SriAuthorizationState.Authorized)]
    [InlineData("NO AUTORIZADO", SriAuthorizationState.NotAuthorized)]
    [InlineData("NAT", SriAuthorizationState.NotAuthorized)]
    [InlineData("RECHAZADO", SriAuthorizationState.NotAuthorized)]
    [InlineData("EN PROCESAMIENTO", SriAuthorizationState.Processing)]
    [InlineData("EN PROCESO", SriAuthorizationState.Processing)]
    [InlineData("PPR", SriAuthorizationState.Processing)]
    [InlineData("", SriAuthorizationState.Unknown)]
    [InlineData(null, SriAuthorizationState.Unknown)]
    [InlineData("ALGO NUEVO", SriAuthorizationState.Unknown)]
    public void Authorization_states_are_read_tolerantly_and_no_authorized_is_never_mistaken_for_authorized(string? raw, SriAuthorizationState expected) =>
        Assert.Equal(expected, SriAuthorizationStates.Parse(raw));

    [Fact]
    public void A_not_authorized_response_exposes_its_error_messages()
    {
        var extra = "<mensaje><identificador>52</identificador><mensaje>ERROR EN DIFERENCIAS</mensaje><informacionAdicional>detalle</informacionAdicional><tipo>ERROR</tipo></mensaje>";
        var result = SriSoapParser.ParseAuthorization(SriSamples.Authorization("NO AUTORIZADO", number: null, extra: extra));

        var authorization = Assert.Single(result.Authorizations);
        Assert.Equal(SriAuthorizationState.NotAuthorized, authorization.State);
        Assert.Null(authorization.Number);
        Assert.Equal("52: ERROR EN DIFERENCIAS", SriMessageCodes.Summary(authorization.Messages));
    }

    [Fact]
    public void No_authorizations_means_the_SRI_has_not_resolved_it_yet()
    {
        var result = SriSoapParser.ParseAuthorization(SriSamples.NoAuthorizations);

        Assert.Empty(result.Authorizations);
        Assert.Equal("3010202601179000002800110010019999999990000000012", result.QueriedKey);
    }

    [Theory]
    [InlineData("43", true)]
    [InlineData("70", true)]
    [InlineData("35", false)]
    [InlineData("45", false)]
    public void Registered_or_processing_keys_are_known_to_the_SRI_and_must_not_be_resent(string id, bool known) =>
        Assert.Equal(known, SriMessageCodes.MeansAlreadyKnown([new SriMessage(id, "x", null, "ERROR")]));

    [Fact]
    public void A_soap_fault_is_a_transient_failure_with_a_clipped_description()
    {
        var ex = Assert.Throws<SriUnavailableException>(() => SriSoapParser.ParseReception(SriSamples.Fault));

        Assert.Equal("sri_soap_fault", ex.Code);
        Assert.True(ex.Message.Length < 260);
    }

    [Theory]
    [InlineData("")]
    [InlineData("no es xml")]
    [InlineData("<html><body>503</body></html>")]
    [InlineData("<soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\"><soap:Body><otra/></soap:Body></soap:Envelope>")]
    [InlineData("<soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\"><soap:Body><R><RespuestaRecepcionComprobante><estado>???</estado></RespuestaRecepcionComprobante></R></soap:Body></soap:Envelope>")]
    public void Anything_that_is_not_the_expected_response_is_a_transient_bad_response(string body)
    {
        var ex = Assert.Throws<SriUnavailableException>(() => SriSoapParser.ParseReception(body));

        Assert.Equal("sri_bad_response", ex.Code);
    }

    [Fact]
    public void A_response_with_a_doctype_is_refused_to_prevent_entity_attacks()
    {
        const string hostile = """<!DOCTYPE x [<!ENTITY e SYSTEM "file:///etc/passwd">]><soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/"><soap:Body><R>&e;</R></soap:Body></soap:Envelope>""";

        var ex = Assert.Throws<SriUnavailableException>(() => SriSoapParser.ParseAuthorization(hostile));

        Assert.Equal("sri_bad_response", ex.Code);
    }

    [Fact]
    public void A_long_message_summary_is_clipped()
    {
        var messages = Enumerable.Range(0, 30).Select(i => new SriMessage(i.ToString(System.Globalization.CultureInfo.InvariantCulture), new string('x', 60), null, "ERROR")).ToList();

        Assert.True(SriMessageCodes.Summary(messages, 400).Length <= 400);
    }
}

public class TransmissionScheduleTests
{
    [Fact]
    public void Failures_wait_longer_each_time_and_stop_growing_at_thirty_minutes()
    {
        var waits = Enumerable.Range(1, 12).Select(n => TransmissionSchedule.AfterUnavailable(n, jitter: 0.5)).ToList();

        Assert.Equal(TimeSpan.FromSeconds(30), waits[0]);
        Assert.Equal(waits.OrderBy(w => w), waits);
        Assert.Equal(TimeSpan.FromMinutes(30), waits[^1]);
        Assert.Equal(TimeSpan.FromMinutes(30), waits[^2]);
    }

    [Fact]
    public void Authorization_polls_start_at_ten_seconds_and_cap_at_fifteen_minutes()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), TransmissionSchedule.AfterPending(0, jitter: 0.5));
        Assert.Equal(TimeSpan.FromMinutes(15), TransmissionSchedule.AfterPending(50, jitter: 0.5));
    }

    [Theory]
    [InlineData(0.0, 0.8)]
    [InlineData(1.0, 1.2)]
    public void The_random_variation_is_plus_or_minus_twenty_percent(double jitter, double factor) =>
        Assert.Equal(TimeSpan.FromSeconds(30 * factor), TransmissionSchedule.AfterUnavailable(1, jitter));

    [Fact]
    public void Without_a_fixed_jitter_the_wait_stays_inside_the_band()
    {
        for (var i = 0; i < 200; i++)
        {
            var wait = TransmissionSchedule.AfterUnavailable(3);
            Assert.InRange(wait.TotalSeconds, 96, 144); // 2 min ± 20 %
        }
    }

    [Fact]
    public void An_item_is_parked_once_it_is_older_than_the_limit()
    {
        var start = new DateTimeOffset(2026, 3, 9, 10, 0, 0, TimeSpan.Zero);

        Assert.False(TransmissionSchedule.ShouldPark(start, start.AddHours(47), TimeSpan.FromHours(48)));
        Assert.True(TransmissionSchedule.ShouldPark(start, start.AddHours(48), TimeSpan.FromHours(48)));
    }
}

public class SriSoapGatewayTests
{
    private sealed class Stub(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add((request, await request.Content!.ReadAsStringAsync(cancellationToken)));
            return await respond(request);
        }
    }

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/xml") };

    private static SriSoapGateway Gateway(Stub stub) => new(new HttpClient(stub), Options.Create(new SriOptions()), NullLogger<SriSoapGateway>.Instance);

    [Fact]
    public async Task Sending_posts_the_signed_xml_in_base64_to_the_reception_service_of_the_right_environment()
    {
        var stub = new Stub(_ => Task.FromResult(Ok(SriSamples.Received)));

        var test = await Gateway(stub).SendAsync(SriEnvironment.Test, SriSamples.SignedInvoice, CancellationToken.None);
        await Gateway(stub).SendAsync(SriEnvironment.Production, SriSamples.SignedInvoice, CancellationToken.None);

        Assert.Equal(SriReceptionState.Received, test.State);
        Assert.Equal("https://celcer.sri.gob.ec/comprobantes-electronicos-ws/RecepcionComprobantesOffline", stub.Calls[0].Request.RequestUri!.ToString());
        Assert.Equal("https://cel.sri.gob.ec/comprobantes-electronicos-ws/RecepcionComprobantesOffline", stub.Calls[1].Request.RequestUri!.ToString());
        var (request, body) = stub.Calls[0];
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("text/xml", request.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("utf-8", request.Content.Headers.ContentType.CharSet);
        Assert.Equal("\"\"", request.Headers.GetValues("SOAPAction").Single());
        Assert.Contains("xmlns:ec=\"http://ec.gob.sri.ws.recepcion\"", body, StringComparison.Ordinal);
        Assert.Contains($"<ec:validarComprobante><xml>{Convert.ToBase64String(Encoding.UTF8.GetBytes(SriSamples.SignedInvoice))}</xml></ec:validarComprobante>", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Querying_posts_the_access_key_to_the_authorization_service()
    {
        var stub = new Stub(_ => Task.FromResult(Ok(SriSamples.NoAuthorizations)));
        const string key = "3010202601179000002800110010019999999990000000012";

        var result = await Gateway(stub).QueryAuthorizationAsync(SriEnvironment.Test, key, CancellationToken.None);

        Assert.Empty(result.Authorizations);
        Assert.Equal("https://celcer.sri.gob.ec/comprobantes-electronicos-ws/AutorizacionComprobantesOffline", stub.Calls[0].Request.RequestUri!.ToString());
        Assert.Contains($"<ec:autorizacionComprobante><claveAccesoComprobante>{key}</claveAccesoComprobante></ec:autorizacionComprobante>", stub.Calls[0].Body, StringComparison.Ordinal);
        Assert.Contains("xmlns:ec=\"http://ec.gob.sri.ws.autorizacion\"", stub.Calls[0].Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("123")]
    [InlineData("")]
    [InlineData("3010202601179000002800110010019999999990000000012</claveAccesoComprobante><x/>")]
    [InlineData("301020260117900000280011001001999999999000000001A")]
    public async Task An_access_key_that_is_not_exactly_49_digits_never_reaches_the_envelope(string key)
    {
        var stub = new Stub(_ => Task.FromResult(Ok(SriSamples.NoAuthorizations)));

        await Assert.ThrowsAsync<ArgumentException>(() => Gateway(stub).QueryAuthorizationAsync(SriEnvironment.Test, key, CancellationToken.None));
        Assert.Empty(stub.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "sri_http_503")]
    [InlineData(HttpStatusCode.BadGateway, "sri_http_502")]
    [InlineData(HttpStatusCode.NotFound, "sri_http_404")]
    [InlineData(HttpStatusCode.Redirect, "sri_http_302")]
    public async Task An_http_error_is_a_transient_failure_with_a_stable_code(HttpStatusCode status, string code)
    {
        var stub = new Stub(_ => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("<html>cuerpo del error</html>") }));

        var ex = await Assert.ThrowsAsync<SriUnavailableException>(() => Gateway(stub).SendAsync(SriEnvironment.Test, SriSamples.SignedInvoice, CancellationToken.None));

        Assert.Equal(code, ex.Code);
        Assert.DoesNotContain("cuerpo del error", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_soap_fault_with_http_500_is_reported_as_a_fault()
    {
        var stub = new Stub(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent(SriSamples.Fault, Encoding.UTF8, "text/xml") }));

        var ex = await Assert.ThrowsAsync<SriUnavailableException>(() => Gateway(stub).QueryAuthorizationAsync(SriEnvironment.Test, "3010202601179000002800110010019999999990000000012", CancellationToken.None));

        Assert.Equal("sri_soap_fault", ex.Code);
    }

    [Fact]
    public async Task Network_failures_and_timeouts_are_transient_and_tell_apart()
    {
        var down = new Stub(_ => throw new HttpRequestException("sin conexión"));
        var slow = new Stub(_ => throw new TaskCanceledException("tiempo", new TimeoutException()));

        Assert.Equal("sri_unreachable", (await Assert.ThrowsAsync<SriUnavailableException>(() => Gateway(down).SendAsync(SriEnvironment.Test, "<a/>", CancellationToken.None))).Code);
        Assert.Equal("sri_timeout", (await Assert.ThrowsAsync<SriUnavailableException>(() => Gateway(slow).SendAsync(SriEnvironment.Test, "<a/>", CancellationToken.None))).Code);
    }

    [Fact]
    public async Task A_cancellation_requested_by_the_caller_is_not_reported_as_an_SRI_timeout()
    {
        using var cts = new CancellationTokenSource();
        var stub = new Stub(_ => throw new TaskCanceledException("cancelado", null, cts.Token));
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Gateway(stub).SendAsync(SriEnvironment.Test, "<a/>", cts.Token));
    }

    [Fact]
    public async Task An_oversized_response_is_refused()
    {
        var big = new string('a', 5 * 1024 * 1024);
        var stub = new Stub(_ => Task.FromResult(Ok(big)));

        var ex = await Assert.ThrowsAsync<SriUnavailableException>(() => Gateway(stub).SendAsync(SriEnvironment.Test, "<a/>", CancellationToken.None));

        Assert.Equal("sri_bad_response", ex.Code);
    }

    [Fact]
    public void The_service_addresses_are_configurable_per_environment()
    {
        var options = new SriOptions { TestBaseUrl = "https://prueba.local/ws/", ProductionBaseUrl = "https://real.local/ws/" };

        Assert.Equal("https://prueba.local/ws/", options.BaseUrl(SriEnvironment.Test));
        Assert.Equal("https://real.local/ws/", options.BaseUrl(SriEnvironment.Production));
    }
}
