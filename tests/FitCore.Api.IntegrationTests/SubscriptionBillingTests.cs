using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FitCore.Api.IntegrationTests.Infrastructure;
using Npgsql;

namespace FitCore.Api.IntegrationTests;

/// <summary>Cobros comerciales: facturas internas, pagos, intentos, reembolsos e idempotencia, separados de los pagos de los alumnos.</summary>
[Collection(ApiCollection4.Name)]
public class SubscriptionBillingTests(ApiFixture api)
{
    private static readonly string[] AllFeatures =
        ["members", "branches", "cash_payments", "attendance", "training", "nutrition", "classes", "advanced_reports", "exports", "advanced_permissions", "extra_storage"];

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-5));

    private static string D(int days) => Today.AddDays(days).ToString("yyyy-MM-dd");

    private static string NewKey() => $"k-{Guid.NewGuid():N}";

    private async Task<(Guid Id, string Code)> PlanAsync(decimal price, int trialDays = 10, int? graceDays = null)
    {
        using var platform = await TestApi.PlatformClientAsync(api);
        var code = $"bl-{Guid.NewGuid():N}"[..20];
        var body = await Json(await platform.PostAsJsonAsync("/api/v1/platform/plans", new
        {
            code, name = $"Plan {code}", isActive = true, currency = "USD", billingPeriod = "Monthly", price, trialDays, graceDays, features = AllFeatures,
            limits = new object[]
            {
                new { code = "branches", value = (long?)null }, new { code = "internal_users", value = (long?)null },
                new { code = "members", value = (long?)null }, new { code = "storage_mb", value = (long?)null }
            }
        }), HttpStatusCode.Created);
        return (body.GetProperty("id").GetGuid(), code);
    }

    private async Task<TestTenant> TenantAsync(string planCode, bool trial = false)
    {
        var slug = $"gym-{Guid.NewGuid():N}"[..20];
        using var platform = await TestApi.PlatformClientAsync(api);
        var created = await Json(await platform.PostAsJsonAsync("/api/v1/platform/tenants", new
        {
            slug, name = $"Gimnasio {slug}", timeZone = "America/Guayaquil", locale = "es-EC", ownerEmail = $"owner@{slug}.test",
            ownerFullName = "Dueño", ownerPassword = TestApi.OwnerPassword, planCode, startTrial = trial
        }), HttpStatusCode.Created);
        var owner = TestApi.Authenticated(api, await TestApi.LoginAsync(api, slug, $"owner@{slug}.test", TestApi.OwnerPassword));
        return new TestTenant(created.GetProperty("id").GetGuid(), slug, $"owner@{slug}.test", TestApi.OwnerPassword, owner);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"Respondió {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private async Task<(HttpStatusCode Status, string? Code, JsonElement? Body)> SendAsync(HttpMethod method, string url, object? body = null, string? key = null)
    {
        using var platform = await TestApi.PlatformClientAsync(api);
        using var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        var response = await platform.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        var json = string.IsNullOrEmpty(text) ? (JsonElement?)null : JsonDocument.Parse(text).RootElement.Clone();
        var code = !response.IsSuccessStatusCode && json is { } j && j.TryGetProperty("code", out var c) ? c.GetString() : null;
        return (response.StatusCode, code, json);
    }

    private Task<(HttpStatusCode Status, string? Code, JsonElement? Body)> IssueAsync(Guid tenantId) =>
        SendAsync(HttpMethod.Post, $"/api/v1/platform/tenants/{tenantId}/subscription/invoices");

    private async Task<JsonElement> IssueOkAsync(Guid tenantId)
    {
        var (status, code, body) = await IssueAsync(tenantId);
        Assert.True(status == HttpStatusCode.Created, $"Emitir respondió {(int)status} {code}");
        return body!.Value;
    }

    private static object PayBody(decimal amount, string method = "BankTransfer", string? reference = null, string currency = "USD", string? notes = null) =>
        new { amount, currency, method, reference = reference ?? $"TRF-{Guid.NewGuid():N}"[..16], notes };

    private Task<(HttpStatusCode Status, string? Code, JsonElement? Body)> PayAsync(string invoiceId, object body, string? key) =>
        SendAsync(HttpMethod.Post, $"/api/v1/platform/billing/invoices/{invoiceId}/pay", body, key);

    private async Task<JsonElement> PayOkAsync(JsonElement invoice, string? key = null)
    {
        var (status, code, body) = await PayAsync(invoice.GetProperty("id").GetString()!, PayBody(invoice.GetProperty("amount").GetDecimal()), key ?? NewKey());
        Assert.True(status == HttpStatusCode.OK, $"Pagar respondió {(int)status} {code}: {body}");
        return body!.Value;
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(api.AdminConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private async Task ExecAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(api.AdminConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<string?> SqlErrorAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(api.AdminConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        try
        {
            await command.ExecuteNonQueryAsync();
            return null;
        }
        catch (PostgresException ex)
        {
            return ex.MessageText;
        }
    }

    private Task<string> StatusAsync(Guid tenantId) =>
        ScalarAsync<string>($"SELECT status FROM subscriptions.subscriptions WHERE tenant_id = '{tenantId}' AND status <> 'Cancelled'");

    private Task SetPeriodAsync(Guid tenantId, int endOffsetDays) =>
        ExecAsync($"UPDATE subscriptions.subscriptions SET current_period_start = '{D(endOffsetDays - 30)}', current_period_end = '{D(endOffsetDays)}', next_due_on = '{D(endOffsetDays)}' WHERE tenant_id = '{tenantId}' AND status <> 'Cancelled'");

    // ---- Emisión -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Emitir_la_factura_de_activacion_de_una_prueba_copia_importe_y_plan_y_queda_abierta()
    {
        var (_, code) = await PlanAsync(59.90m);
        var tenant = await TenantAsync(code, trial: true);

        var invoice = await IssueOkAsync(tenant.Id);

        Assert.Equal("Pending", invoice.GetProperty("status").GetString());
        Assert.Equal("Activation", invoice.GetProperty("kind").GetString());
        Assert.Equal(59.90m, invoice.GetProperty("amount").GetDecimal());
        Assert.Equal("USD", invoice.GetProperty("currency").GetString());
        Assert.Equal(code, invoice.GetProperty("planCode").GetString());
        Assert.Matches($@"^FC-{DateTime.UtcNow.Year}-\d{{6}}$", invoice.GetProperty("number").GetString());
        Assert.Equal(D(0), invoice.GetProperty("periodStart").GetString());
        Assert.Equal(Today.AddMonths(1).ToString("yyyy-MM-dd"), invoice.GetProperty("periodEnd").GetString());
        Assert.Equal(D(10), invoice.GetProperty("dueOn").GetString()); // la prueba termina en 10 días
        Assert.Contains("No es un comprobante tributario", invoice.GetProperty("notice").GetString());
        Assert.True(invoice.GetProperty("isOpen").GetBoolean());

        Assert.Equal(59.90m, await ScalarAsync<decimal>($"SELECT amount FROM subscriptions.invoices WHERE tenant_id = '{tenant.Id}'"));
        Assert.Equal(1L, await ScalarAsync<long>($"SELECT count(*) FROM subscriptions.subscription_events WHERE tenant_id = '{tenant.Id}' AND type = 'InvoiceIssued'"));
        Assert.Equal("Trialing", await StatusAsync(tenant.Id)); // emitir no activa nada
    }

    [Fact]
    public async Task Solo_hay_una_factura_abierta_por_suscripcion_y_no_se_emite_en_estados_que_no_cobran()
    {
        var (_, paid) = await PlanAsync(40m);
        var (_, free) = await PlanAsync(0m);
        var tenant = await TenantAsync(paid);
        await SetPeriodAsync(tenant.Id, 5);

        await IssueOkAsync(tenant.Id);
        Assert.Equal((HttpStatusCode.Conflict, "billing.invoice_open_exists"), (await IssueAsync(tenant.Id)).Let(r => (r.Status, r.Code)));

        var freeTenant = await TenantAsync(free);
        Assert.Equal((HttpStatusCode.Conflict, "billing.free_plan"), (await IssueAsync(freeTenant.Id)).Let(r => (r.Status, r.Code)));

        var suspended = await TenantAsync(paid);
        await SendAsync(HttpMethod.Post, $"/api/v1/platform/tenants/{suspended.Id}/subscription/suspend", new { reason = "x" });
        Assert.Equal((HttpStatusCode.Conflict, "billing.subscription_not_billable"), (await IssueAsync(suspended.Id)).Let(r => (r.Status, r.Code)));

        var scheduled = await TenantAsync(paid);
        await SendAsync(HttpMethod.Post, $"/api/v1/platform/tenants/{scheduled.Id}/subscription/cancel", new { reason = "Se va", atPeriodEnd = true });
        Assert.Equal((HttpStatusCode.Conflict, "billing.cancellation_scheduled"), (await IssueAsync(scheduled.Id)).Let(r => (r.Status, r.Code)));

        Assert.Equal(1L, await ScalarAsync<long>($"SELECT count(*) FROM subscriptions.invoices WHERE tenant_id = '{tenant.Id}'"));
        Assert.Equal(0L, await ScalarAsync<long>($"SELECT count(*) FROM subscriptions.invoices WHERE tenant_id IN ('{freeTenant.Id}', '{suspended.Id}', '{scheduled.Id}')"));
    }

    [Fact]
    public async Task Emisiones_simultaneas_dejan_una_sola_factura_abierta()
    {
        var (_, code) = await PlanAsync(40m);
        var tenant = await TenantAsync(code);
        await SetPeriodAsync(tenant.Id, 5);

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => IssueAsync(tenant.Id)));

        Assert.All(results, r => Assert.True(r.Status is HttpStatusCode.Created or HttpStatusCode.Conflict, $"Respondió {(int)r.Status}"));
        Assert.Equal(1, results.Count(r => r.Status == HttpStatusCode.Created));
        Assert.Equal(1L, await ScalarAsync<long>($"SELECT count(*) FROM subscriptions.invoices WHERE tenant_id = '{tenant.Id}'"));
    }

    // ---- Pago --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Pagar_la_factura_de_una_prueba_activa_la_suscripcion_y_registra_el_pago_trazable()
    {
        var (_, code) = await PlanAsync(40m);
        var tenant = await TenantAsync(code, trial: true);
        var invoice = await IssueOkAsync(tenant.Id);

        var paid = await PayOkAsync(invoice);

        Assert.Equal("Paid", paid.GetProperty("status").GetString());
        Assert.Equal(D(0), paid.GetProperty("periodStart").GetString());
        var payment = paid.GetProperty("payment");
        Assert.Equal(40m, payment.GetProperty("amount").GetDecimal());
        Assert.Equal("BankTransfer", payment.GetProperty("method").GetString());
        Assert.Equal(JsonValueKind.Null, payment.GetProperty("provider").ValueKind);

        Assert.Equal("Active", await StatusAsync(tenant.Id));
        Assert.Equal(Today.AddMonths(1).ToString("yyyy-MM-dd"), await ScalarAsync<string>($"SELECT current_period_end::text FROM subscriptions.subscriptions WHERE tenant_id = '{tenant.Id}'"));
        Assert.Equal("Full", await ScalarAsync<string>($"SELECT access_mode FROM subscriptions.tenant_entitlements WHERE tenant_id = '{tenant.Id}'"));
        Assert.Equal(1L, await ScalarAsync<long>($"SELECT count(*) FROM subscriptions.payments WHERE tenant_id = '{tenant.Id}' AND amount = 40 AND provider IS NULL"));
        var events = await ScalarAsync<string[]>($"SELECT array_agg(type ORDER BY occurred_at, id) FROM subscriptions.subscription_events WHERE tenant_id = '{tenant.Id}'");
        Assert.Contains("PaymentConfirmed", events);
        Assert.Contains("Activated", events);
    }

    [Fact]
    public async Task Pagar_una_renovacion_extiende_el_periodo_desde_donde_termino_y_levanta_la_mora()
    {
        var (_, code) = await PlanAsync(40m);
        var tenant = await TenantAsync(code);
        await SetPeriodAsync(tenant.Id, 4);
        var invoice = await IssueOkAsync(tenant.Id);
        Assert.Equal("Renewal", invoice.GetProperty("kind").GetString());
        Assert.Equal(D(4), invoice.GetProperty("periodStart").GetString());

        var paid = await PayOkAsync(invoice);

        Assert.Equal(D(4), paid.GetProperty("periodStart").GetString());
        Assert.Equal(Today.AddDays(4).AddMonths(1).ToString("yyyy-MM-dd"), paid.GetProperty("periodEnd").GetString());
        Assert.Equal("Active", await StatusAsync(tenant.Id));

        // En mora: el pago la levanta y la gracia desaparece.
        var late = await TenantAsync(code);
        await ExecAsync($"UPDATE subscriptions.subscriptions SET status = 'PastDue', grace_ends_on = '{D(3)}', current_period_start = '{D(-33)}', current_period_end = '{D(-3)}', next_due_on = '{D(-3)}' WHERE tenant_id = '{late.Id}'");
        await PayOkAsync(await IssueOkAsync(late.Id));
        Assert.Equal("Active", await StatusAsync(late.Id));
        Assert.Equal("null", await ScalarAsync<string>($"SELECT coalesce(grace_ends_on::text, 'null') FROM subscriptions.subscriptions WHERE tenant_id = '{late.Id}'"));
    }

    [Fact]
    public async Task Repetir_el_pago_con_la_misma_clave_devuelve_lo_mismo_y_no_cobra_dos_veces()
    {
        var (_, code) = await PlanAsync(40m);
        var tenant = await TenantAsync(code, trial: true);
        var invoice = await IssueOkAsync(tenant.Id);
        var id = invoice.GetProperty("id").GetString()!;
        var key = NewKey();
        var body = PayBody(40m, reference: "TRF-UNICA-1");

        var first = await PayAsync(id, body, key);
        var second = await PayAsync(id, body, key);

        Assert.Equal(HttpStatusCode.OK, first.Status);
        Assert.Equal(HttpStatusCode.OK, second.Status);
        Assert.Equal(first.Body!.Value.GetProperty("payment").GetProperty("id").GetString(), second.Body!.Value.GetProperty("payment").GetProperty("id").GetString());
        Assert.Equal(1L, await ScalarAsync<long>($"SELECT count(*) FROM subscriptions.payments WHERE tenant_id = '{tenant.Id}'"));
        Assert.Equal(1L, await ScalarAsync<long>($"SELECT count(*) FROM subscriptions.subscription_events WHERE tenant_id = '{tenant.Id}' AND type = 'Activated'"));

        // La misma clave en otra factura es un error, no un reintento.
        var other = await TenantAsync(code, trial: true);
        var otherInvoice = await IssueOkAsync(other.Id);
        var reused = await PayAsync(otherInvoice.GetProperty("id").GetString()!, PayBody(40m), key);
        Assert.Equal((HttpStatusCode.Conflict, "billing.idempotency_key_reused"), (reused.Status, reused.Code));

        // Sin clave no se mueve dinero.
        var missing = await PayAsync(otherInvoice.GetProperty("id").GetString()!, PayBody(40m), key: null);
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "billing.idempotency_key_required"), (missing.Status, missing.Code));
        Assert.Equal(0L, await ScalarAsync<long>($"SELECT count(*) FROM subscriptions.payments WHERE tenant_id = '{other.Id}'"));
    }

    [Fact]
    public async Task Un_importe_o_una_moneda_que_no_coinciden_no_confirman_nada()
    {
        var (_, code) = await PlanAsync(40m);
        var tenant = await TenantAsync(code, trial: true);
        var invoice = await IssueOkAsync(tenant.Id);
        var id = invoice.GetProperty("id").GetString()!;

        foreach (var body in new object[] { PayBody(39.99m), PayBody(40.01m), PayBody(40m, currency: "EUR") })
        {
            var result = await PayAsync(id, body, NewKey());
            Assert.Equal(HttpStatusCode.UnprocessableEntity, result.Status);
            Assert.True(result.Code is "billing.amount_mismatch" or "validation.failed", result.Code);
        }

        Assert.Equal(0L, await ScalarAsync<long>($"SELECT count(*) FROM subscriptions.payments WHERE tenant_id = '{tenant.Id}'"));
        Assert.Equal("Pending", await ScalarAsync<string>($"SELECT status FROM subscriptions.invoices WHERE tenant_id = '{tenant.Id}'"));
        Assert.Equal("Trialing", await StatusAsync(tenant.Id));

        // Aunque alguien lo intente por SQL, la base rechaza un pago que no coincide con su factura.
        var invoiceId = Guid.Parse(id);
        var error = await SqlErrorAsync($"""
            INSERT INTO subscriptions.payments (id, tenant_id, invoice_id, amount, refunded_amount, currency, method, idempotency_key, paid_at, confirmed_at)
            VALUES (gen_random_uuid(), '{tenant.Id}', '{invoiceId}', 1, 0, 'USD', 'Cash', 'sql-{Guid.NewGuid():N}', now(), now())
            """);
        Assert.Contains("no coincide con la factura", error ?? "NO ERROR");
    }

    [Fact]
    public async Task Un_cobro_fallido_no_activa_la_suscripcion_y_la_factura_se_puede_pagar_despues()
    {
        var (_, code) = await PlanAsync(40m);
        var tenant = await TenantAsync(code, trial: true);
        var invoice = await IssueOkAsync(tenant.Id);
        var id = invoice.GetProperty("id").GetString()!;

        var failed = await SendAsync(HttpMethod.Post, $"/api/v1/platform/billing/invoices/{id}/attempts", new { method = "BankTransfer", reference = "TRF-RECHAZADA", reason = "La transferencia fue rechazada por el banco" });

        Assert.Equal(HttpStatusCode.OK, failed.Status);
        Assert.Equal("Failed", failed.Body!.Value.GetProperty("status").GetString());
        Assert.Equal(1, failed.Body!.Value.GetProperty("attemptCount").GetInt32());
        Assert.Equal(1, failed.Body!.Value.GetProperty("attempts").GetArrayLength());
        Assert.Equal("Trialing", await StatusAsync(tenant.Id)); // un pago fallido NO activa una suscripción de pago
        Assert.Equal(0L, await ScalarAsync<long>($"SELECT count(*) FROM subscriptions.payments WHERE tenant_id = '{tenant.Id}'"));
        Assert.Equal(1L, await ScalarAsync<long>($"SELECT count(*) FROM subscriptions.subscription_events WHERE tenant_id = '{tenant.Id}' AND type = 'PaymentFailed'"));

        // Sigue abierta: un pago posterior la salda y sí activa.
        var paid = await PayOkAsync(failed.Body!.Value);
        Assert.Equal("Paid", paid.GetProperty("status").GetString());
        Assert.Equal(1, paid.GetProperty("attemptCount").GetInt32());
        Assert.Equal("Active", await StatusAsync(tenant.Id));
    }

    [Fact]
    public async Task Una_factura_anulada_o_con_la_suscripcion_suspendida_o_con_cancelacion_programada_no_se_paga_y_no_deja_pagos_a_medias()
    {
        var (_, code) = await PlanAsync(40m);

        var voided = await TenantAsync(code, trial: true);
        var voidedInvoice = await IssueOkAsync(voided.Id);
        var voidResult = await SendAsync(HttpMethod.Post, $"/api/v1/platform/billing/invoices/{voidedInvoice.GetProperty("id").GetString()}/void", new { reason = "Emitida por error" });
        Assert.Equal("Void", voidResult.Body!.Value.GetProperty("status").GetString());
        var payVoided = await PayAsync(voidedInvoice.GetProperty("id").GetString()!, PayBody(40m), NewKey());
        Assert.Equal((HttpStatusCode.Conflict, "billing.invoice_not_open"), (payVoided.Status, payVoided.Code));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "validation.failed"), (await SendAsync(HttpMethod.Post, $"/api/v1/platform/billing/invoices/{voidedInvoice.GetProperty("id").GetString()}/void", new { reason = " " })).Let(r => (r.Status, r.Code)));

        var suspended = await TenantAsync(code, trial: true);
        var suspendedInvoice = await IssueOkAsync(suspended.Id);
        await SendAsync(HttpMethod.Post, $"/api/v1/platform/tenants/{suspended.Id}/subscription/suspend", new { reason = "Revisión" });
        var paySuspended = await PayAsync(suspendedInvoice.GetProperty("id").GetString()!, PayBody(40m), NewKey());
        Assert.Equal((HttpStatusCode.Conflict, "billing.subscription_suspended"), (paySuspended.Status, paySuspended.Code));

        // Una cancelación programada anula la factura abierta; si se emite y luego se programa, tampoco quedan pagos.
        var scheduled = await TenantAsync(code);
        await SetPeriodAsync(scheduled.Id, 4);
        var scheduledInvoice = await IssueOkAsync(scheduled.Id);
        await SendAsync(HttpMethod.Post, $"/api/v1/platform/tenants/{scheduled.Id}/subscription/cancel", new { reason = "Se va", atPeriodEnd = true });
        Assert.Equal("Void", await ScalarAsync<string>($"SELECT status FROM subscriptions.invoices WHERE id = '{scheduledInvoice.GetProperty("id").GetString()}'"));

        Assert.Equal(0L, await ScalarAsync<long>($"SELECT count(*) FROM subscriptions.payments WHERE tenant_id IN ('{voided.Id}', '{suspended.Id}', '{scheduled.Id}')"));
        Assert.Equal("Suspended", await StatusAsync(suspended.Id));
    }

    [Fact]
    public async Task Una_referencia_bancaria_no_se_registra_dos_veces_y_no_se_aceptan_datos_de_tarjeta()
    {
        var (_, code) = await PlanAsync(40m);
        var first = await TenantAsync(code, trial: true);
        var second = await TenantAsync(code, trial: true);
        var invoice1 = await IssueOkAsync(first.Id);
        var invoice2 = await IssueOkAsync(second.Id);
        var reference = $"TRF-DUP-{Guid.NewGuid():N}"[..20];

        Assert.Equal(HttpStatusCode.OK, (await PayAsync(invoice1.GetProperty("id").GetString()!, PayBody(40m, reference: reference), NewKey())).Status);
        var duplicate = await PayAsync(invoice2.GetProperty("id").GetString()!, PayBody(40m, reference: reference.ToUpperInvariant()), NewKey());
        Assert.Equal((HttpStatusCode.Conflict, "billing.reference_duplicated"), (duplicate.Status, duplicate.Code));

        // Tarjeta: no se registra a mano, y un número de tarjeta en cualquier texto se rechaza.
        var card = await PayAsync(invoice2.GetProperty("id").GetString()!, PayBody(40m, method: "Card", reference: "x"), NewKey());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, card.Status);
        Assert.Equal("billing.card_requires_provider", card.Code);

        foreach (var body in new object[] { PayBody(40m, reference: "4111 1111 1111 1111"), PayBody(40m, notes: "pagó con 5500005555555559") })
        {
            var rejected = await PayAsync(invoice2.GetProperty("id").GetString()!, body, NewKey());
            Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.Status);
            Assert.Contains("número de tarjeta", rejected.Body!.Value.ToString());
        }

        Assert.Equal(0L, await ScalarAsync<long>($"SELECT count(*) FROM subscriptions.payments WHERE tenant_id = '{second.Id}'"));

        // La base también lo exige: un pago con tarjeta necesita proveedor.
        var cardSql = await SqlErrorAsync($"""
            INSERT INTO subscriptions.payments (id, tenant_id, invoice_id, amount, refunded_amount, currency, method, idempotency_key, paid_at, confirmed_at)
            VALUES (gen_random_uuid(), '{second.Id}', '{invoice2.GetProperty("id").GetString()}', 40, 0, 'USD', 'Card', 'sql-{Guid.NewGuid():N}', now(), now())
            """);
        Assert.Contains("ck_payments_card_provider", cardSql ?? "NO ERROR");
    }

    [Fact]
    public async Task Pagos_simultaneos_de_la_misma_factura_confirman_uno_solo()
    {
        var (_, code) = await PlanAsync(40m);
        var tenant = await TenantAsync(code);
        await SetPeriodAsync(tenant.Id, 4);
        var invoice = await IssueOkAsync(tenant.Id);
        var id = invoice.GetProperty("id").GetString()!;

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => PayAsync(id, PayBody(40m), NewKey())));

        Assert.All(results, r => Assert.True(r.Status is HttpStatusCode.OK or HttpStatusCode.Conflict, $"Respondió {(int)r.Status} {r.Code}"));
        Assert.Equal(1, results.Count(r => r.Status == HttpStatusCode.OK));
        Assert.Equal(1L, await ScalarAsync<long>($"SELECT count(*) FROM subscriptions.payments WHERE tenant_id = '{tenant.Id}'"));
        Assert.Equal(1L, await ScalarAsync<long>($"SELECT count(*) FROM subscriptions.subscription_events WHERE tenant_id = '{tenant.Id}' AND type = 'Renewed'"));
        Assert.Equal(Today.AddDays(4).AddMonths(1).ToString("yyyy-MM-dd"), await ScalarAsync<string>($"SELECT current_period_end::text FROM subscriptions.subscriptions WHERE tenant_id = '{tenant.Id}'"));
    }

    // ---- Reembolsos y anulación --------------------------------------------------------------------------------------

    [Fact]
    public async Task El_reembolso_parcial_y_total_cambian_el_estado_de_la_factura_sin_tocar_la_suscripcion()
    {
        var (_, code) = await PlanAsync(40m);
        var tenant = await TenantAsync(code, trial: true);
        var paid = await PayOkAsync(await IssueOkAsync(tenant.Id));
        var paymentId = paid.GetProperty("payment").GetProperty("id").GetString()!;

        var partial = await SendAsync(HttpMethod.Post, $"/api/v1/platform/billing/payments/{paymentId}/refund", new { amount = 15m, reason = "Cobro duplicado en el banco" }, NewKey());
        Assert.Equal(HttpStatusCode.OK, partial.Status);
        Assert.Equal("PartiallyRefunded", partial.Body!.Value.GetProperty("status").GetString());
        Assert.Equal(15m, partial.Body!.Value.GetProperty("refundedAmount").GetDecimal());
        Assert.Equal("Active", await StatusAsync(tenant.Id));

        var tooMuch = await SendAsync(HttpMethod.Post, $"/api/v1/platform/billing/payments/{paymentId}/refund", new { amount = 25.01m, reason = "x" }, NewKey());
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "billing.refund_amount_invalid"), (tooMuch.Status, tooMuch.Code));

        var key = NewKey();
        var full = await SendAsync(HttpMethod.Post, $"/api/v1/platform/billing/payments/{paymentId}/refund", new { amount = 25m, reason = "Devolución total" }, key);
        var replay = await SendAsync(HttpMethod.Post, $"/api/v1/platform/billing/payments/{paymentId}/refund", new { amount = 25m, reason = "Devolución total" }, key);
        Assert.Equal("Refunded", full.Body!.Value.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.OK, replay.Status);

        Assert.Equal(40m, await ScalarAsync<decimal>($"SELECT refunded_amount FROM subscriptions.payments WHERE id = '{paymentId}'"));
        Assert.Equal(2L, await ScalarAsync<long>($"SELECT count(*) FROM subscriptions.payment_refunds WHERE payment_id = '{paymentId}'"));
        Assert.Equal("Active", await StatusAsync(tenant.Id)); // reembolsar no cancela ni suspende: es otra decisión con su motivo

        var missingKey = await SendAsync(HttpMethod.Post, $"/api/v1/platform/billing/payments/{paymentId}/refund", new { amount = 1m, reason = "x" });
        Assert.Equal("billing.idempotency_key_required", missingKey.Code);
        var pending = await TenantAsync(code, trial: true);
        var open = await IssueOkAsync(pending.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Post, $"/api/v1/platform/billing/payments/{open.GetProperty("id").GetString()}/refund", new { amount = 1m, reason = "x" }, NewKey())).Status);
    }

    [Fact]
    public async Task Cambiar_de_plan_anula_la_factura_abierta_porque_su_importe_ya_no_corresponde()
    {
        var (_, basic) = await PlanAsync(20m);
        var (proId, _) = await PlanAsync(60m);
        var tenant = await TenantAsync(basic);
        await SetPeriodAsync(tenant.Id, 4);
        var invoice = await IssueOkAsync(tenant.Id);
        Assert.Equal(20m, invoice.GetProperty("amount").GetDecimal());

        await SendAsync(HttpMethod.Post, $"/api/v1/platform/tenants/{tenant.Id}/subscription/change-plan", new { planId = proId });

        Assert.Equal("Void", await ScalarAsync<string>($"SELECT status FROM subscriptions.invoices WHERE id = '{invoice.GetProperty("id").GetString()}'"));
        var reissued = await IssueOkAsync(tenant.Id);
        Assert.Equal(60m, reissued.GetProperty("amount").GetDecimal());
    }

    // ---- Facturación automática --------------------------------------------------------------------------------------

    [Fact]
    public async Task El_proceso_emite_la_factura_de_las_suscripciones_de_pago_que_estan_por_vencer_sin_duplicarla()
    {
        var (_, paid) = await PlanAsync(40m);
        var (_, free) = await PlanAsync(0m);
        var near = await TenantAsync(paid);
        var far = await TenantAsync(paid);
        var freeTenant = await TenantAsync(free);
        var scheduled = await TenantAsync(paid);
        await SetPeriodAsync(near.Id, 5);
        await SetPeriodAsync(far.Id, 25);
        await SetPeriodAsync(freeTenant.Id, 5);
        await SetPeriodAsync(scheduled.Id, 5);
        await SendAsync(HttpMethod.Post, $"/api/v1/platform/tenants/{scheduled.Id}/subscription/cancel", new { reason = "Se va", atPeriodEnd = true });

        var run = await SendAsync(HttpMethod.Post, "/api/v1/platform/subscriptions/run-lifecycle");
        Assert.Equal(HttpStatusCode.OK, run.Status);
        Assert.True(run.Body!.Value.GetProperty("invoicesIssued").GetInt32() >= 1);

        Assert.Equal(1L, await ScalarAsync<long>($"SELECT count(*) FROM subscriptions.invoices WHERE tenant_id = '{near.Id}' AND status = 'Pending' AND kind = 'Renewal'"));
        Assert.Equal(0L, await ScalarAsync<long>($"SELECT count(*) FROM subscriptions.invoices WHERE tenant_id IN ('{far.Id}', '{freeTenant.Id}', '{scheduled.Id}')"));
        Assert.Equal(D(5), await ScalarAsync<string>($"SELECT due_on::text FROM subscriptions.invoices WHERE tenant_id = '{near.Id}'"));

        await SendAsync(HttpMethod.Post, "/api/v1/platform/subscriptions/run-lifecycle");
        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => SendAsync(HttpMethod.Post, "/api/v1/platform/subscriptions/run-lifecycle")));
        Assert.Equal(1L, await ScalarAsync<long>($"SELECT count(*) FROM subscriptions.invoices WHERE tenant_id = '{near.Id}'"));

        // Una suscripción que pasa a mora también recibe su factura.
        var overdue = await TenantAsync(paid);
        await SetPeriodAsync(overdue.Id, 0);
        await SendAsync(HttpMethod.Post, "/api/v1/platform/subscriptions/run-lifecycle");
        Assert.Equal("PastDue", await StatusAsync(overdue.Id));
        Assert.Equal(1L, await ScalarAsync<long>($"SELECT count(*) FROM subscriptions.invoices WHERE tenant_id = '{overdue.Id}' AND status = 'Pending'"));
    }

    // ---- Consultas, resumen y seguridad ------------------------------------------------------------------------------

    [Fact]
    public async Task Los_listados_filtran_por_gimnasio_estado_y_vencimiento_y_el_resumen_distingue_emitido_cobrado_y_pendiente()
    {
        var (_, code) = await PlanAsync(40m);
        var tenant = await TenantAsync(code, trial: true);
        var paidInvoice = await PayOkAsync(await IssueOkAsync(tenant.Id));
        var next = await TenantAsync(code);
        await SetPeriodAsync(next.Id, -3);
        var overdue = await IssueOkAsync(next.Id);

        var mine = await SendAsync(HttpMethod.Get, $"/api/v1/platform/billing/invoices?tenantId={tenant.Id}");
        Assert.Equal(1, mine.Body!.Value.GetProperty("totalCount").GetInt32());
        Assert.Equal("Paid", mine.Body!.Value.GetProperty("items")[0].GetProperty("status").GetString());

        var open = await SendAsync(HttpMethod.Get, $"/api/v1/platform/billing/invoices?tenantId={next.Id}&status=Pending&overdue=true");
        Assert.Equal(overdue.GetProperty("id").GetString(), open.Body!.Value.GetProperty("items")[0].GetProperty("id").GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await SendAsync(HttpMethod.Get, "/api/v1/platform/billing/invoices?status=Inventado")).Status);

        var payments = await SendAsync(HttpMethod.Get, $"/api/v1/platform/billing/payments?tenantId={tenant.Id}");
        Assert.Equal(1, payments.Body!.Value.GetProperty("totalCount").GetInt32());
        Assert.Equal(paidInvoice.GetProperty("number").GetString(), payments.Body!.Value.GetProperty("items")[0].GetProperty("invoiceNumber").GetString());

        var summary = (await SendAsync(HttpMethod.Get, "/api/v1/platform/billing/summary")).Body!.Value;
        Assert.Equal("USD", summary.GetProperty("currency").GetString());
        Assert.True(summary.GetProperty("collectedAmount").GetDecimal() >= 40m);
        Assert.True(summary.GetProperty("issuedAmount").GetDecimal() >= 80m);
        Assert.True(summary.GetProperty("pendingAmount").GetDecimal() >= 40m);
        Assert.True(summary.GetProperty("overdueAmount").GetDecimal() >= 40m);
        Assert.Equal(summary.GetProperty("collectedAmount").GetDecimal() - summary.GetProperty("refundedAmount").GetDecimal(), summary.GetProperty("netCollectedAmount").GetDecimal());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await SendAsync(HttpMethod.Get, "/api/v1/platform/billing/summary?from=2026-12-01&to=2026-01-01")).Status);
    }

    [Fact]
    public async Task Solo_la_plataforma_cobra_y_un_gimnasio_no_ve_ni_toca_las_facturas_de_otro()
    {
        var (_, code) = await PlanAsync(40m);
        var a = await TenantAsync(code, trial: true);
        var b = await TenantAsync(code, trial: true);
        var invoiceA = await IssueOkAsync(a.Id);
        var invoiceB = await IssueOkAsync(b.Id);
        var idB = invoiceB.GetProperty("id").GetString()!;

        foreach (var path in new[] { "/api/v1/platform/billing/invoices", $"/api/v1/platform/billing/invoices/{idB}", "/api/v1/platform/billing/payments", "/api/v1/platform/billing/summary" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await a.Owner.GetAsync(path)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await a.Owner.PostAsync($"/api/v1/platform/tenants/{a.Id}/subscription/invoices", null)).StatusCode);
        using var tamper = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/platform/billing/invoices/{idB}/pay") { Content = JsonContent.Create(PayBody(40m)) };
        tamper.Headers.Add("Idempotency-Key", NewKey());
        Assert.Equal(HttpStatusCode.Forbidden, (await a.Owner.SendAsync(tamper)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await a.Owner.PostAsJsonAsync($"/api/v1/platform/billing/invoices/{invoiceA.GetProperty("id").GetString()}/void", new { reason = "yo mismo" })).StatusCode);
        Assert.Equal("Pending", await ScalarAsync<string>($"SELECT status FROM subscriptions.invoices WHERE id = '{idB}'"));

        // La base: cada gimnasio ve solo sus facturas y no puede escribir ninguna, ni siquiera la propia.
        await using var connection = new NpgsqlConnection(api.AppConnection);
        await connection.OpenAsync();
        await using (var set = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, false)", connection))
        {
            set.Parameters.AddWithValue("t", a.Id.ToString());
            await set.ExecuteScalarAsync();
        }

        await using var count = new NpgsqlCommand("SELECT count(*) FROM subscriptions.invoices", connection);
        Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!);
        await using var update = new NpgsqlCommand($"UPDATE subscriptions.invoices SET status = 'Void', voided_at = now(), void_reason = 'x' WHERE tenant_id = '{a.Id}'", connection);
        Assert.Equal(0, await update.ExecuteNonQueryAsync());
        await using var delete = new NpgsqlCommand("DELETE FROM subscriptions.invoices", connection);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync());
        Assert.Equal("42501", ex.SqlState);
    }

    [Fact]
    public async Task La_base_protege_lo_emitido_y_lo_cobrado_y_los_pagos_de_alumnos_no_se_alteran()
    {
        var (_, code) = await PlanAsync(40m);
        var tenant = await TenantAsync(code, trial: true);
        var paid = await PayOkAsync(await IssueOkAsync(tenant.Id));
        var invoiceId = paid.GetProperty("id").GetString();
        var paymentId = paid.GetProperty("payment").GetProperty("id").GetString();

        Assert.Contains("no cambia su número", await SqlErrorAsync($"UPDATE subscriptions.invoices SET amount = 1 WHERE id = '{invoiceId}'") ?? "NO ERROR");
        Assert.Contains("Transición de factura no permitida", await SqlErrorAsync($"UPDATE subscriptions.invoices SET status = 'Pending', paid_at = NULL WHERE id = '{invoiceId}'") ?? "NO ERROR");
        Assert.Contains("no se puede modificar", await SqlErrorAsync($"UPDATE subscriptions.payments SET amount = 1, refunded_amount = 0 WHERE id = '{paymentId}'") ?? "NO ERROR");
        Assert.Contains("histórico", await SqlErrorAsync($"DELETE FROM subscriptions.invoices WHERE id = '{invoiceId}'") ?? "NO ERROR");
        Assert.Contains("histórico", await SqlErrorAsync($"DELETE FROM subscriptions.payments WHERE id = '{paymentId}'") ?? "NO ERROR");
        Assert.Contains("ck_invoices_refund_status", await SqlErrorAsync($"UPDATE subscriptions.invoices SET status = 'Refunded' WHERE id = '{invoiceId}'") ?? "NO ERROR");

        var failed = await SendAsync(HttpMethod.Post, $"/api/v1/platform/billing/invoices/{(await IssueOkAsync((await TenantAsync(code, trial: true)).Id)).GetProperty("id").GetString()}/attempts", new { method = "Cash", reason = "No llegó el efectivo" });
        Assert.Equal(HttpStatusCode.OK, failed.Status);
        Assert.Contains("histórico", await SqlErrorAsync("UPDATE subscriptions.payment_attempts SET reason = 'otra' WHERE reason = 'No llegó el efectivo'") ?? "NO ERROR");

        // Dominios separados: los cobros comerciales de este gimnasio no crearon ningún pago ni obligación de alumnos.
        Assert.Equal(0L, await ScalarAsync<long>($"SELECT count(*) FROM billing.payments WHERE tenant_id = '{tenant.Id}'"));
        Assert.Equal(0L, await ScalarAsync<long>($"SELECT count(*) FROM billing.payment_obligations WHERE tenant_id = '{tenant.Id}'"));
        Assert.Equal(0L, await ScalarAsync<long>($"SELECT count(*) FROM members.memberships WHERE tenant_id = '{tenant.Id}'"));
    }

    [Fact]
    public async Task Los_cobros_quedan_auditados_y_en_el_historial_de_la_suscripcion()
    {
        var (_, code) = await PlanAsync(40m);
        var tenant = await TenantAsync(code, trial: true);
        var paid = await PayOkAsync(await IssueOkAsync(tenant.Id));
        var invoiceId = paid.GetProperty("id").GetString();

        var actions = await ScalarAsync<string[]>($"SELECT array_agg(DISTINCT action) FROM audit.audit_logs WHERE (entity = 'SubscriptionInvoice' AND entity_id = '{invoiceId}') OR (entity = 'SubscriptionPayment' AND after::text LIKE '%{invoiceId}%')");
        Assert.Contains("SubscriptionInvoice.Created", actions);
        Assert.Contains("SubscriptionInvoice.Updated", actions);
        Assert.Contains("SubscriptionPayment.Created", actions);

        var history = await SendAsync(HttpMethod.Get, $"/api/v1/platform/tenants/{tenant.Id}/subscription/history");
        var types = history.Body!.Value.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("type").GetString()!).ToArray();
        Assert.Contains("InvoiceIssued", types);
        Assert.Contains("PaymentConfirmed", types);
    }
}

internal static class BillingTestExtensions
{
    public static TResult Let<TIn, TResult>(this TIn value, Func<TIn, TResult> map) => map(value);
}
