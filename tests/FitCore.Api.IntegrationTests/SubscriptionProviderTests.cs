using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FitCore.Api.IntegrationTests.Infrastructure;
using Npgsql;

namespace FitCore.Api.IntegrationTests;

/// <summary>Webhooks del proveedor de pagos: firma, vigencia, idempotencia, concordancia de importe y moneda, y que nada se aplique a medias.</summary>
[Collection(ApiCollection4.Name)]
public class SubscriptionProviderTests(ApiFixture api)
{
    private const string Provider = "testpay";

    private static readonly string[] AllFeatures =
        ["members", "branches", "cash_payments", "attendance", "training", "nutrition", "classes", "advanced_reports", "exports", "advanced_permissions", "extra_storage"];

    private async Task<string> PlanAsync(decimal price)
    {
        using var platform = await TestApi.PlatformClientAsync(api);
        var code = $"pv-{Guid.NewGuid():N}"[..20];
        (await platform.PostAsJsonAsync("/api/v1/platform/plans", new
        {
            code, name = $"Plan {code}", isActive = true, currency = "USD", billingPeriod = "Monthly", price, trialDays = 10, features = AllFeatures,
            limits = new object[]
            {
                new { code = "branches", value = 3L }, new { code = "internal_users", value = (long?)null },
                new { code = "members", value = 100L }, new { code = "storage_mb", value = (long?)null }
            }
        })).EnsureSuccessStatusCode();
        return code;
    }

    /// <summary>Gimnasio en prueba con su factura de activación emitida y abierta.</summary>
    private async Task<(TestTenant Tenant, string Number, decimal Amount)> OpenInvoiceAsync(decimal price = 30m)
    {
        var plan = await PlanAsync(price);
        var slug = $"gym-{Guid.NewGuid():N}"[..20];
        using var platform = await TestApi.PlatformClientAsync(api);
        var created = await Json(await platform.PostAsJsonAsync("/api/v1/platform/tenants", new
        {
            slug, name = $"Gimnasio Pagos {slug}", timeZone = "America/Guayaquil", locale = "es-EC", ownerEmail = $"owner@{slug}.test",
            ownerFullName = "Dueño", ownerPassword = TestApi.OwnerPassword, planCode = plan, startTrial = true
        }), HttpStatusCode.Created);
        var id = created.GetProperty("id").GetGuid();
        var owner = TestApi.Authenticated(api, await TestApi.LoginAsync(api, slug, $"owner@{slug}.test", TestApi.OwnerPassword));
        var invoice = await Json(await platform.PostAsync($"/api/v1/platform/tenants/{id}/subscription/invoices", null), HttpStatusCode.Created);
        return (new TestTenant(id, slug, $"owner@{slug}.test", TestApi.OwnerPassword, owner), invoice.GetProperty("number").GetString()!, invoice.GetProperty("amount").GetDecimal());
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"Respondió {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static string Body(string id, string type, string number, decimal amount, string currency = "USD", string? reference = null, string? reason = null, DateTimeOffset? at = null) =>
        JsonSerializer.Serialize(new
        {
            id, type, occurredAt = (at ?? DateTimeOffset.UtcNow).ToString("O", CultureInfo.InvariantCulture),
            data = new { invoiceNumber = number, providerReference = reference, amount, currency, failureReason = reason }
        });

    private static string Signature(string body, string secret = ApiFixture.WebhookSecret, long? timestamp = null)
    {
        var t = timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{t}.{body}"));
        return $"t={t},v1={Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string body, string? signature)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/payments") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (signature is not null)
        {
            request.Headers.Add("X-FitCore-Signature", signature);
        }

        return client.SendAsync(request);
    }

    private Task<HttpResponseMessage> SendAsync(string body, string? signature = null) => PostAsync(api.CreateClient(), body, signature ?? Signature(body));

    private async Task<JsonElement> InvoiceAsync(string number)
    {
        using var platform = await TestApi.PlatformClientAsync(api);
        var list = await Json(await platform.GetAsync($"/api/v1/platform/billing/invoices?search={number}"));
        var id = list.GetProperty("items")[0].GetProperty("id").GetGuid();
        return await Json(await platform.GetAsync($"/api/v1/platform/billing/invoices/{id}"));
    }

    private async Task<JsonElement> SubscriptionAsync(Guid tenantId)
    {
        using var platform = await TestApi.PlatformClientAsync(api);
        return await Json(await platform.GetAsync($"/api/v1/platform/tenants/{tenantId}/subscription"));
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(api.AdminConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static string EventId() => $"evt_{Guid.NewGuid():N}";

    // ---- Pago confirmado -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Un_pago_confirmado_y_firmado_salda_la_factura_y_activa_la_suscripcion()
    {
        var (tenant, number, amount) = await OpenInvoiceAsync();
        Assert.Equal("Trialing", (await SubscriptionAsync(tenant.Id)).GetProperty("status").GetString());

        var body = Body(EventId(), "payment.succeeded", number, amount, reference: $"ch_{Guid.NewGuid():N}");
        var response = await Json(await SendAsync(body));
        Assert.Equal("Applied", response.GetProperty("outcome").GetString());
        Assert.False(response.GetProperty("duplicate").GetBoolean());

        var invoice = await InvoiceAsync(number);
        Assert.Equal("Paid", invoice.GetProperty("status").GetString());
        var payment = invoice.GetProperty("payment");
        Assert.Equal("Card", payment.GetProperty("method").GetString());
        Assert.Equal(Provider, payment.GetProperty("provider").GetString());
        Assert.Equal("Active", (await SubscriptionAsync(tenant.Id)).GetProperty("status").GetString());

        // El gimnasio ve el pago en sus facturas; la plataforma ve el evento aplicado.
        var mine = await Json(await tenant.Owner.GetAsync("/api/v1/my/subscription/invoices"));
        Assert.Equal("Paid", mine.GetProperty("items")[0].GetProperty("status").GetString());
        using var platform = await TestApi.PlatformClientAsync(api);
        var events = await Json(await platform.GetAsync($"/api/v1/platform/billing/provider-events?tenantId={tenant.Id}"));
        Assert.Equal("Applied", events.GetProperty("items")[0].GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Reenviar_el_mismo_evento_no_cobra_dos_veces_ni_con_otro_id_si_la_referencia_es_la_misma()
    {
        var (tenant, number, amount) = await OpenInvoiceAsync();
        var reference = $"ch_{Guid.NewGuid():N}";
        var id = EventId();
        var body = Body(id, "payment.succeeded", number, amount, reference: reference);

        Assert.Equal("Applied", (await Json(await SendAsync(body))).GetProperty("outcome").GetString());
        var replay = await Json(await SendAsync(body));
        Assert.True(replay.GetProperty("duplicate").GetBoolean());

        // El proveedor reenvía el mismo cobro bajo otro id de evento: se reconoce por la referencia y no cambia nada.
        var again = await Json(await SendAsync(Body(EventId(), "payment.succeeded", number, amount, reference: reference)));
        Assert.Equal("Ignored", again.GetProperty("outcome").GetString());

        Assert.Equal(1L, await ScalarAsync<long>($"SELECT count(*) FROM subscriptions.payments WHERE tenant_id = '{tenant.Id}'"));
        Assert.Equal(2L, await ScalarAsync<long>($"SELECT count(*) FROM subscriptions.provider_events WHERE tenant_id = '{tenant.Id}'"));
    }

    [Fact]
    public async Task Entregas_simultaneas_del_mismo_evento_aplican_el_pago_una_sola_vez()
    {
        var (tenant, number, amount) = await OpenInvoiceAsync();
        var body = Body(EventId(), "payment.succeeded", number, amount, reference: $"ch_{Guid.NewGuid():N}");
        var signature = Signature(body);

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ => await Json(await PostAsync(api.CreateClient(), body, signature))));
        Assert.Equal(1, responses.Count(r => r.GetProperty("outcome").GetString() == "Applied"));
        Assert.Equal(7, responses.Count(r => r.GetProperty("duplicate").GetBoolean()));
        Assert.Equal(1L, await ScalarAsync<long>($"SELECT count(*) FROM subscriptions.payments WHERE tenant_id = '{tenant.Id}'"));
        Assert.Equal("Paid", (await InvoiceAsync(number)).GetProperty("status").GetString());
    }

    // ---- Autenticidad ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Sin_firma_valida_vigente_no_se_procesa_nada_y_todas_las_respuestas_son_iguales()
    {
        var (tenant, number, amount) = await OpenInvoiceAsync();
        var body = Body(EventId(), "payment.succeeded", number, amount, reference: $"ch_{Guid.NewGuid():N}");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var attempts = new List<HttpResponseMessage>
        {
            await PostAsync(api.CreateClient(), body, null),                                                         // sin encabezado
            await PostAsync(api.CreateClient(), body, "basura"),                                                    // ilegible
            await PostAsync(api.CreateClient(), body, Signature(body, "otro-secreto-que-no-es-el-correcto-123")),    // otro secreto
            await PostAsync(api.CreateClient(), body + " ", Signature(body)),                                       // cuerpo alterado
            await PostAsync(api.CreateClient(), body, Signature(body, timestamp: now - 3600)),                       // marca de tiempo vieja (reenvío capturado)
            await PostAsync(api.CreateClient(), body, Signature(body, timestamp: now + 3600)),                       // marca de tiempo futura
            await PostAsync(api.CreateClient(), body, Signature(body).Replace("t=", "t=1", StringComparison.Ordinal)) // marca de tiempo cambiada sin re-firmar
        };

        Assert.All(attempts, a => Assert.Equal(HttpStatusCode.Unauthorized, a.StatusCode));
        var texts = await Task.WhenAll(attempts.Select(a => a.Content.ReadAsStringAsync()));
        Assert.Single(texts.Distinct()); // no revela cuál falló

        Assert.Equal("Pending", (await InvoiceAsync(number)).GetProperty("status").GetString());
        Assert.Equal("Trialing", (await SubscriptionAsync(tenant.Id)).GetProperty("status").GetString());
        Assert.Equal(0L, await ScalarAsync<long>($"SELECT count(*) FROM subscriptions.provider_events WHERE invoice_number = '{number}'"));
    }

    [Fact]
    public async Task Una_sesion_de_usuario_no_reemplaza_la_firma()
    {
        var (tenant, number, amount) = await OpenInvoiceAsync();
        var body = Body(EventId(), "payment.succeeded", number, amount, reference: $"ch_{Guid.NewGuid():N}");
        using var platform = await TestApi.PlatformClientAsync(api);

        // Ni el administrador de plataforma, ni el propietario del gimnasio, sin firma: 401.
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(platform, body, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(tenant.Owner, body, null)).StatusCode);
        Assert.Equal("Pending", (await InvoiceAsync(number)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Un_cuerpo_firmado_pero_ilegible_es_400_y_uno_demasiado_grande_413()
    {
        var bad = "{\"id\":\"x\"}";
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(bad)).StatusCode);

        var (_, number, amount) = await OpenInvoiceAsync();
        var noReference = Body(EventId(), "payment.succeeded", number, amount);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(noReference)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(Body(EventId(), "payment.otro", number, amount, reference: "r"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(Body(EventId(), "payment.succeeded", number, 0m, reference: "r"))).StatusCode);

        var huge = new string('x', 70_000);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await SendAsync(huge)).StatusCode);
    }

    [Fact]
    public async Task Sin_proveedor_configurado_responde_503_y_con_el_secreto_anterior_durante_la_rotacion_sigue_funcionando()
    {
        var (_, number, amount) = await OpenInvoiceAsync();
        var body = Body(EventId(), "payment.succeeded", number, amount, reference: $"ch_{Guid.NewGuid():N}");

        using var none = api.CreateClientWith(("Subscriptions:Payments:WebhookSecret", ""));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await PostAsync(none, body, Signature(body))).StatusCode);
        Assert.Equal("Pending", (await InvoiceAsync(number)).GetProperty("status").GetString());

        // Rotación: el secreto vigente es otro y el anterior sigue aceptándose hasta retirarlo.
        using var rotating = api.CreateClientWith(
            ("Subscriptions:Payments:WebhookSecret", "whsec-nuevo-secreto-0123456789abcdef"), ("Subscriptions:Payments:PreviousWebhookSecret", ApiFixture.WebhookSecret));
        Assert.Equal("Applied", (await Json(await PostAsync(rotating, body, Signature(body)))).GetProperty("outcome").GetString());
    }

    // ---- Concordancia con lo emitido ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Un_importe_o_una_moneda_distintos_se_registran_como_rechazados_y_no_cambian_nada()
    {
        var (tenant, number, amount) = await OpenInvoiceAsync();

        var less = await Json(await SendAsync(Body(EventId(), "payment.succeeded", number, amount - 1m, reference: $"ch_{Guid.NewGuid():N}")));
        var currency = await Json(await SendAsync(Body(EventId(), "payment.succeeded", number, amount, "EUR", reference: $"ch_{Guid.NewGuid():N}")));
        var unknown = await Json(await SendAsync(Body(EventId(), "payment.succeeded", "FC-1999-000001", amount, reference: $"ch_{Guid.NewGuid():N}")));
        Assert.Equal("Rejected", less.GetProperty("outcome").GetString());
        Assert.Equal("Rejected", currency.GetProperty("outcome").GetString());
        Assert.Equal("Rejected", unknown.GetProperty("outcome").GetString());

        Assert.Equal("Pending", (await InvoiceAsync(number)).GetProperty("status").GetString());
        Assert.Equal("Trialing", (await SubscriptionAsync(tenant.Id)).GetProperty("status").GetString());
        Assert.Equal(0L, await ScalarAsync<long>($"SELECT count(*) FROM subscriptions.payments WHERE tenant_id = '{tenant.Id}'"));
        Assert.Equal(
            ["billing.amount_mismatch"],
            await Codes($"SELECT outcome_code FROM subscriptions.provider_events WHERE invoice_number = '{number}' AND outcome = 'Rejected' GROUP BY outcome_code"));
        Assert.Equal(1L, await ScalarAsync<long>("SELECT count(*) FROM subscriptions.provider_events WHERE invoice_number = 'FC-1999-000001' AND outcome_code = 'billing.invoice_not_found'"));
    }

    private async Task<List<string>> Codes(string sql)
    {
        await using var connection = new NpgsqlConnection(api.AdminConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var codes = new List<string>();
        while (await reader.ReadAsync())
        {
            codes.Add(reader.GetString(0));
        }

        return codes;
    }

    [Fact]
    public async Task Un_pago_sobre_una_suscripcion_suspendida_o_una_factura_anulada_se_rechaza_sin_dejar_nada_a_medias()
    {
        var (tenant, number, amount) = await OpenInvoiceAsync();
        using var platform = await TestApi.PlatformClientAsync(api);

        // Se anula la factura: el cobro tardío del proveedor no la revive.
        var invoiceId = (await InvoiceAsync(number)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await platform.PostAsJsonAsync($"/api/v1/platform/billing/invoices/{invoiceId}/void", new { reason = "Emitida por error" })).StatusCode);
        var late = await Json(await SendAsync(Body(EventId(), "payment.succeeded", number, amount, reference: $"ch_{Guid.NewGuid():N}")));
        Assert.Equal("Rejected", late.GetProperty("outcome").GetString());
        Assert.Equal("Void", (await InvoiceAsync(number)).GetProperty("status").GetString());

        // Otra factura de un gimnasio suspendido.
        var (suspended, number2, amount2) = await OpenInvoiceAsync();
        Assert.Equal(HttpStatusCode.OK, (await platform.PostAsJsonAsync($"/api/v1/platform/tenants/{suspended.Id}/subscription/suspend", new { reason = "x" })).StatusCode);
        var response = await Json(await SendAsync(Body(EventId(), "payment.succeeded", number2, amount2, reference: $"ch_{Guid.NewGuid():N}")));
        Assert.Equal("Rejected", response.GetProperty("outcome").GetString());
        Assert.Equal("Suspended", (await SubscriptionAsync(suspended.Id)).GetProperty("status").GetString());
        Assert.Equal("Pending", (await InvoiceAsync(number2)).GetProperty("status").GetString());
        Assert.Equal(0L, await ScalarAsync<long>($"SELECT count(*) FROM subscriptions.payments WHERE tenant_id = '{suspended.Id}'"));
        Assert.Equal(0L, await ScalarAsync<long>($"SELECT count(*) FROM subscriptions.payments WHERE tenant_id = '{tenant.Id}'"));
    }

    // ---- Cobros fallidos y reembolsos ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Un_cobro_fallido_deja_constancia_sin_activar_nada_y_la_factura_se_puede_pagar_despues()
    {
        var (tenant, number, amount) = await OpenInvoiceAsync();

        var failed = await Json(await SendAsync(Body(EventId(), "payment.failed", number, amount, reason: "Fondos insuficientes")));
        Assert.Equal("Applied", failed.GetProperty("outcome").GetString());

        var invoice = await InvoiceAsync(number);
        Assert.Equal("Failed", invoice.GetProperty("status").GetString());
        Assert.Equal(1, invoice.GetProperty("attempts").GetArrayLength());
        Assert.Equal("Trialing", (await SubscriptionAsync(tenant.Id)).GetProperty("status").GetString());

        var paid = await Json(await SendAsync(Body(EventId(), "payment.succeeded", number, amount, reference: $"ch_{Guid.NewGuid():N}")));
        Assert.Equal("Applied", paid.GetProperty("outcome").GetString());
        Assert.Equal("Active", (await SubscriptionAsync(tenant.Id)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Un_reembolso_del_proveedor_se_registra_dentro_de_lo_pagado_y_no_cancela_la_suscripcion()
    {
        var (tenant, number, amount) = await OpenInvoiceAsync();
        var reference = $"ch_{Guid.NewGuid():N}";
        await SendAsync(Body(EventId(), "payment.succeeded", number, amount, reference: reference));

        var partial = await Json(await SendAsync(Body(EventId(), "payment.refunded", number, 10m, reference: reference)));
        Assert.Equal("Applied", partial.GetProperty("outcome").GetString());
        var invoice = await InvoiceAsync(number);
        Assert.Equal("PartiallyRefunded", invoice.GetProperty("status").GetString());
        Assert.Equal(10m, invoice.GetProperty("refundedAmount").GetDecimal());

        // Más de lo disponible o de un cobro que no existe: rechazado.
        var over = await Json(await SendAsync(Body(EventId(), "payment.refunded", number, amount, reference: reference)));
        Assert.Equal("Rejected", over.GetProperty("outcome").GetString());
        var ghost = await Json(await SendAsync(Body(EventId(), "payment.refunded", number, 5m, reference: "ch_inexistente")));
        Assert.Equal("Rejected", ghost.GetProperty("outcome").GetString());
        Assert.Equal(10m, (await InvoiceAsync(number)).GetProperty("refundedAmount").GetDecimal());
        Assert.Equal("Active", (await SubscriptionAsync(tenant.Id)).GetProperty("status").GetString());
    }

    // ---- Privilegios y datos ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Los_eventos_y_el_estado_del_proveedor_son_solo_de_la_plataforma_y_no_exponen_el_secreto()
    {
        var (tenant, number, amount) = await OpenInvoiceAsync();
        await SendAsync(Body(EventId(), "payment.succeeded", number, amount, reference: $"ch_{Guid.NewGuid():N}"));

        Assert.Equal(HttpStatusCode.Forbidden, (await tenant.Owner.GetAsync("/api/v1/platform/billing/provider")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tenant.Owner.GetAsync("/api/v1/platform/billing/provider-events")).StatusCode);

        using var platform = await TestApi.PlatformClientAsync(api);
        var status = await platform.GetStringAsync("/api/v1/platform/billing/provider");
        Assert.DoesNotContain(ApiFixture.WebhookSecret, status, StringComparison.Ordinal);
        var parsed = JsonDocument.Parse(status).RootElement;
        Assert.True(parsed.GetProperty("configured").GetBoolean());
        Assert.Equal(Provider, parsed.GetProperty("provider").GetString());
        Assert.False(parsed.GetProperty("rotationInProgress").GetBoolean());

        // La base lo respalda: desde el contexto del gimnasio no se ve ningún evento, y el esquema no guarda el cuerpo ni datos de tarjeta.
        await using var connection = new NpgsqlConnection(api.AppConnection);
        await connection.OpenAsync();
        await using (var set = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, false)", connection))
        {
            set.Parameters.AddWithValue("t", tenant.Id.ToString());
            await set.ExecuteScalarAsync();
        }

        await using (var count = new NpgsqlCommand("SELECT count(*) FROM subscriptions.provider_events", connection))
        {
            Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
        }

        var columns = await Codes("SELECT column_name FROM information_schema.columns WHERE table_schema = 'subscriptions' AND table_name = 'provider_events'");
        Assert.DoesNotContain(columns, c => c.Contains("payload", StringComparison.Ordinal) || c.Contains("body", StringComparison.Ordinal) || c.Contains("card", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Los_eventos_registrados_no_se_editan_ni_se_borran_y_el_pago_manual_con_tarjeta_sigue_prohibido()
    {
        var (tenant, number, amount) = await OpenInvoiceAsync();
        await SendAsync(Body(EventId(), "payment.succeeded", number, amount, reference: $"ch_{Guid.NewGuid():N}"));

        await using var admin = new NpgsqlConnection(api.AdminConnection);
        await admin.OpenAsync();
        await using var update = new NpgsqlCommand($"UPDATE subscriptions.provider_events SET outcome = 'Rejected' WHERE invoice_number = '{number}'", admin);
        await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync());
        await using var delete = new NpgsqlCommand($"DELETE FROM subscriptions.provider_events WHERE invoice_number = '{number}'", admin);
        await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync());

        // A mano, la tarjeta sigue sin poder registrarse (solo existe a través del proveedor).
        using var platform = await TestApi.PlatformClientAsync(api);
        var (_, number2, amount2) = await OpenInvoiceAsync();
        var invoiceId = (await InvoiceAsync(number2)).GetProperty("id").GetGuid();
        using var pay = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/platform/billing/invoices/{invoiceId}/pay")
        {
            Content = JsonContent.Create(new { amount = amount2, currency = "USD", method = "Card", reference = "x" })
        };
        pay.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await platform.SendAsync(pay)).StatusCode);
        Assert.NotEqual(Guid.Empty, tenant.Id);
    }
}
