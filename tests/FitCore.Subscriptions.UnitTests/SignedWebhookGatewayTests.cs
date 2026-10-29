using System.Text;
using System.Text.Json;
using FitCore.Subscriptions.Application.Providers;
using Microsoft.Extensions.Options;

namespace FitCore.Subscriptions.UnitTests;

public class SignedWebhookGatewayTests
{
    private const string Secret = "whsec-0123456789abcdef";
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 15, 0, 0, TimeSpan.Zero);

    private static SignedWebhookGateway Gateway(string? secret = Secret, string? previous = null, int tolerance = 300) =>
        new(Options.Create(new PaymentProviderOptions { ProviderCode = "Pasarela", WebhookSecret = secret, PreviousWebhookSecret = previous, ToleranceSeconds = tolerance }));

    private static byte[] Body(string type = "payment.succeeded", decimal amount = 30m, string? reference = "ch_1", string? reason = null) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            id = "evt_1", type, occurredAt = "2026-10-29T14:59:00Z",
            data = new { invoiceNumber = "FC-2026-000001", providerReference = reference, amount, currency = "usd", failureReason = reason }
        });

    private static string Header(byte[] body, string secret = Secret, DateTimeOffset? at = null)
    {
        var t = (at ?? Now).ToUnixTimeSeconds();
        return $"t={t},v1={Convert.ToHexString(SignedWebhookGateway.Sign(secret, t, body)).ToLowerInvariant()}";
    }

    [Fact]
    public void Una_firma_correcta_se_traduce_al_lenguaje_de_FitCore()
    {
        var body = Body();
        var result = Gateway().VerifyAndParse(Header(body), body, Now);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Code : null);
        var n = result.Value;
        Assert.Equal("evt_1", n.ExternalEventId);
        Assert.Equal(ProviderEventType.PaymentSucceeded, n.Type);
        Assert.Equal("FC-2026-000001", n.InvoiceNumber);
        Assert.Equal("ch_1", n.ProviderReference);
        Assert.Equal(30m, n.Amount);
        Assert.Equal("USD", n.Currency);
    }

    [Fact]
    public void El_codigo_del_proveedor_se_normaliza_y_sin_secreto_o_con_uno_corto_no_hay_proveedor()
    {
        Assert.Equal("pasarela", Gateway().Code);
        Assert.False(Gateway(secret: null).IsConfigured);
        Assert.False(Gateway(secret: "").IsConfigured);
        Assert.False(Gateway(secret: "corto").IsConfigured);
        var body = Body();
        Assert.Equal("payments.provider_not_configured", Gateway(secret: null).VerifyAndParse(Header(body), body, Now).Error.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("basura")]
    [InlineData("t=abc,v1=00")]
    [InlineData("v1=0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("t=1")]
    public void Un_encabezado_ausente_o_mal_formado_es_firma_invalida(string? header)
    {
        var body = Body();
        Assert.Equal("payments.signature_invalid", Gateway().VerifyAndParse(header, body, Now).Error.Code);
    }

    [Fact]
    public void Otro_secreto_o_un_cuerpo_alterado_o_una_marca_cambiada_invalidan_la_firma()
    {
        var body = Body();
        var other = Header(body, "otro-secreto-completamente-distinto");
        Assert.Equal("payments.signature_invalid", Gateway().VerifyAndParse(other, body, Now).Error.Code);

        var tampered = Body(amount: 1m);
        Assert.Equal("payments.signature_invalid", Gateway().VerifyAndParse(Header(body), tampered, Now).Error.Code);

        // La marca de tiempo va dentro de lo firmado: no se puede reutilizar la firma con otra hora.
        var good = Header(body);
        var shifted = "t=" + (Now.ToUnixTimeSeconds() + 1) + good[good.IndexOf(',', StringComparison.Ordinal)..];
        Assert.Equal("payments.signature_invalid", Gateway().VerifyAndParse(shifted, body, Now).Error.Code);
    }

    [Fact]
    public void La_marca_de_tiempo_fuera_de_tolerancia_se_rechaza_en_ambos_sentidos_pero_dentro_se_acepta()
    {
        var body = Body();
        Assert.Equal("payments.timestamp_out_of_tolerance", Gateway().VerifyAndParse(Header(body, at: Now.AddSeconds(-301)), body, Now).Error.Code);
        Assert.Equal("payments.timestamp_out_of_tolerance", Gateway().VerifyAndParse(Header(body, at: Now.AddSeconds(301)), body, Now).Error.Code);
        Assert.True(Gateway().VerifyAndParse(Header(body, at: Now.AddSeconds(-299)), body, Now).IsSuccess);
        Assert.True(Gateway(tolerance: 5).VerifyAndParse(Header(body, at: Now.AddSeconds(-4)), body, Now).IsSuccess);
        Assert.False(Gateway(tolerance: 5).VerifyAndParse(Header(body, at: Now.AddSeconds(-6)), body, Now).IsSuccess);
    }

    [Fact]
    public void Durante_la_rotacion_se_acepta_el_secreto_anterior_y_varias_firmas_en_el_mismo_encabezado()
    {
        var body = Body();
        var old = "secreto-anterior-0123456789";
        Assert.False(Gateway().VerifyAndParse(Header(body, old), body, Now).IsSuccess);
        Assert.True(Gateway(previous: old).VerifyAndParse(Header(body, old), body, Now).IsSuccess);

        // El proveedor puede enviar dos firmas (la del secreto viejo y la del nuevo): basta con que una concuerde.
        var t = Now.ToUnixTimeSeconds();
        var both = $"t={t},v1={Convert.ToHexString(SignedWebhookGateway.Sign(old, t, body)).ToLowerInvariant()},v1={Convert.ToHexString(SignedWebhookGateway.Sign(Secret, t, body)).ToLowerInvariant()}";
        Assert.True(Gateway().VerifyAndParse(both, body, Now).IsSuccess);
    }

    [Fact]
    public void Un_cuerpo_vacio_o_demasiado_grande_se_rechaza_antes_de_firmar()
    {
        Assert.Equal("payments.payload_invalid", Gateway().VerifyAndParse(Header([1]), [], Now).Error.Code);
        var huge = new byte[70_000];
        Assert.Equal("payments.payload_invalid", Gateway().VerifyAndParse(Header(huge), huge, Now).Error.Code);
    }

    [Theory]
    [InlineData("payment.otro", 30, "ch_1")]
    [InlineData("payment.succeeded", 0, "ch_1")]
    [InlineData("payment.succeeded", 30.123, "ch_1")]
    [InlineData("payment.succeeded", 30, null)]
    [InlineData("payment.refunded", 30, null)]
    public void Un_contenido_firmado_pero_incoherente_es_payload_invalido(string type, double amount, string? reference)
    {
        var body = Body(type, (decimal)amount, reference);
        Assert.Equal("payments.payload_invalid", Gateway().VerifyAndParse(Header(body), body, Now).Error.Code);
    }

    [Fact]
    public void El_cobro_fallido_no_exige_referencia_y_el_texto_libre_se_recorta_y_no_admite_tarjetas()
    {
        var failed = Body("payment.failed", reference: null, reason: new string('x', 400));
        var result = Gateway().VerifyAndParse(Header(failed), failed, Now);
        Assert.True(result.IsSuccess);
        Assert.Equal(200, result.Value.FailureReason!.Length);

        var card = Body("payment.failed", reference: null, reason: "4111 1111 1111 1111");
        Assert.Null(Gateway().VerifyAndParse(Header(card), card, Now).Value.FailureReason);
    }
}
