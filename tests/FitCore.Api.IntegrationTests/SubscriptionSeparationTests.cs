using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FitCore.Api.IntegrationTests.Infrastructure;
using Npgsql;
using static FitCore.Api.IntegrationTests.Infrastructure.CommercialTestApi;

namespace FitCore.Api.IntegrationTests;

/// <summary>
/// Pruebas de aceptación de la Fase 7 sobre la regla principal: lo que FitCore 360 le cobra a un gimnasio (suscripciones comerciales) es otro dominio que lo
/// que el gimnasio le cobra a sus alumnos, y ningún cambio comercial corrompe las operaciones del gimnasio ni de otro gimnasio.
/// </summary>
[Collection(ApiCollection1.Name)]
public class SubscriptionSeparationTests(ApiFixture api)
{
    private static readonly string[] AllFeatures =
        ["members", "branches", "cash_payments", "attendance", "training", "nutrition", "classes", "advanced_reports", "exports", "advanced_permissions", "extra_storage"];

    private static readonly string[] StudentTables = ["billing.payments", "billing.payment_obligations", "members.memberships", "members.members"];

    private async Task<(Guid Id, string Code)> PlanAsync(decimal price, int members = 100)
    {
        using var platform = await TestApi.PlatformClientAsync(api);
        var code = $"sp-{Guid.NewGuid():N}"[..20];
        var response = await platform.PostAsJsonAsync("/api/v1/platform/plans", new
        {
            code, name = $"Plan {code}", isActive = true, currency = "USD", billingPeriod = "Monthly", price, trialDays = 10, features = AllFeatures,
            limits = new object[]
            {
                new { code = "branches", value = 3L }, new { code = "internal_users", value = (long?)null },
                new { code = "members", value = (long?)members }, new { code = "storage_mb", value = (long?)null }
            }
        });
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (body.GetProperty("id").GetGuid(), code);
    }

    private async Task<TestTenant> TenantAsync(string planCode, bool trial = true)
    {
        var slug = $"gym-{Guid.NewGuid():N}"[..20];
        using var platform = await TestApi.PlatformClientAsync(api);
        var response = await platform.PostAsJsonAsync("/api/v1/platform/tenants", new
        {
            slug, name = $"Gimnasio Separado {slug}", timeZone = "America/Guayaquil", locale = "es-EC", ownerEmail = $"owner@{slug}.test",
            ownerFullName = "Dueño", ownerPassword = TestApi.OwnerPassword, planCode, startTrial = trial
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        var owner = TestApi.Authenticated(api, await TestApi.LoginAsync(api, slug, $"owner@{slug}.test", TestApi.OwnerPassword));
        return new TestTenant(id, slug, $"owner@{slug}.test", TestApi.OwnerPassword, owner);
    }

    private async Task<(HttpStatusCode Status, JsonElement? Body)> PlatformAsync(HttpMethod method, string url, object? body = null, string? key = null)
    {
        using var platform = await TestApi.PlatformClientAsync(api);
        var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        var response = await platform.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, string.IsNullOrEmpty(text) ? null : JsonDocument.Parse(text).RootElement.Clone());
    }

    private static async Task<HttpResponseMessage> PostMemberAsync(HttpClient owner, Guid branchId) =>
        await owner.PostAsJsonAsync("/api/v1/members", new
        {
            branchId, firstName = "Alumno", lastName = "Separado", idType = "Cedula", idNumber = MemberTests.NewCedula(), birthDate = "1995-04-12",
            email = $"m.{Guid.NewGuid():N}"[..20] + "@alumnos.test", phone = "0991234567"
        });

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Respondió {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(api.AdminConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>Huella de todas las filas de alumnos de un gimnasio: si cambia cualquier valor de cualquier fila, cambia la huella.</summary>
    private async Task<string[]> StudentFingerprintAsync(Guid tenantId)
    {
        var result = new List<string>();
        foreach (var table in StudentTables)
        {
            result.Add(await ScalarAsync<string>(
                $"SELECT count(*) || ':' || coalesce(md5(string_agg(t::text, '|' ORDER BY t.id)), '') FROM {table} t WHERE t.tenant_id = '{tenantId}'"));
        }

        return [.. result];
    }

    private static string Signed(string body, out string signature)
    {
        var t = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(ApiFixture.WebhookSecret), Encoding.UTF8.GetBytes($"{t}.{body}"));
        signature = $"t={t},v1={Convert.ToHexString(hash).ToLowerInvariant()}";
        return body;
    }

    [Fact]
    public async Task Todo_el_ciclo_comercial_deja_intactos_los_cobros_y_membresias_de_los_alumnos_y_los_de_otro_gimnasio()
    {
        var (_, code) = await PlanAsync(30m);
        var (cheaperId, _) = await PlanAsync(10m);
        var tenant = await TenantAsync(code);
        var neighbour = await TenantAsync(code);

        // Cada gimnasio tiene su propia vida de alumnos: alumno, obligación de cobro, pago.
        foreach (var gym in new[] { tenant, neighbour })
        {
            var branch = await TestApi.CreateBranchAsync(gym.Owner, "UIO");
            var memberId = (await JsonAsync(await PostMemberAsync(gym.Owner, branch))).GetProperty("id").GetGuid();
            Assert.Equal(HttpStatusCode.Created, (await gym.Owner.PostAsJsonAsync("/api/v1/payment-obligations", new { memberId, concept = "Producto", amount = 20m, taxKind = "General" })).StatusCode);
            var payment = await PostPaymentAsync(gym.Owner, PaymentBody(memberId, 20m, "Card", $"VCH-{Guid.NewGuid():N}"[..20]));
            Assert.True(payment.StatusCode == HttpStatusCode.Created, await payment.Content.ReadAsStringAsync());
        }

        var before = await StudentFingerprintAsync(tenant.Id);
        var neighbourBefore = await StudentFingerprintAsync(neighbour.Id);
        // Hay datos reales que proteger: pagos, obligaciones y alumnos (aún no hay membresías en este escenario).
        Assert.All(new[] { before[0], before[1], before[3] }, f => Assert.DoesNotMatch("^0:", f));

        // Un ciclo comercial completo: factura, pago, cobro fallido, reembolso, cambio de plan, suspensión, reactivación, cancelación y proceso de vencimientos.
        var invoice = (await PlatformAsync(HttpMethod.Post, $"/api/v1/platform/tenants/{tenant.Id}/subscription/invoices")).Body!.Value;
        var invoiceId = invoice.GetProperty("id").GetGuid();
        var number = invoice.GetProperty("number").GetString()!;
        var amount = invoice.GetProperty("amount").GetDecimal();

        var failedWebhook = Signed(JsonSerializer.Serialize(new
        {
            id = $"evt_{Guid.NewGuid():N}", type = "payment.failed", occurredAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            data = new { invoiceNumber = number, providerReference = (string?)null, amount, currency = "USD", failureReason = "Fondos insuficientes" }
        }), out var failedSignature);
        var failedRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/payments") { Content = new StringContent(failedWebhook, Encoding.UTF8, "application/json") };
        failedRequest.Headers.Add("X-FitCore-Signature", failedSignature);
        Assert.Equal(HttpStatusCode.OK, (await api.CreateClient().SendAsync(failedRequest)).StatusCode);

        var paid = await PlatformAsync(HttpMethod.Post, $"/api/v1/platform/billing/invoices/{invoiceId}/pay",
            new { amount, currency = "USD", method = "BankTransfer", reference = $"TRF-{Guid.NewGuid():N}"[..20] }, Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.OK, paid.Status);
        var paymentId = paid.Body!.Value.GetProperty("payment").GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await PlatformAsync(HttpMethod.Post, $"/api/v1/platform/billing/payments/{paymentId}/refund", new { amount = 5m, reason = "Ajuste" }, Guid.NewGuid().ToString())).Status);
        Assert.Equal(HttpStatusCode.OK, (await PlatformAsync(HttpMethod.Post, $"/api/v1/platform/tenants/{tenant.Id}/subscription/change-plan", new { planId = cheaperId, note = "Baja" })).Status);
        Assert.Equal(HttpStatusCode.OK, (await PlatformAsync(HttpMethod.Post, $"/api/v1/platform/tenants/{tenant.Id}/subscription/suspend", new { reason = "Prueba de separación" })).Status);
        Assert.Equal(HttpStatusCode.OK, (await PlatformAsync(HttpMethod.Post, $"/api/v1/platform/tenants/{tenant.Id}/subscription/reactivate", new { note = (string?)null })).Status);
        Assert.Equal(HttpStatusCode.OK, (await PlatformAsync(HttpMethod.Post, $"/api/v1/platform/tenants/{tenant.Id}/subscription/cancel", new { reason = "Cierre", atPeriodEnd = true })).Status);
        Assert.Equal(HttpStatusCode.OK, (await PlatformAsync(HttpMethod.Post, "/api/v1/platform/subscriptions/run-lifecycle")).Status);
        Assert.Equal(HttpStatusCode.Created, (await tenant.Owner.PostAsJsonAsync("/api/v1/my/subscription/requests", new { kind = "Reactivation", message = "Seguimos" })).StatusCode);

        // El dominio comercial sí cambió…
        Assert.True(await ScalarAsync<long>($"SELECT count(*) FROM subscriptions.payments WHERE tenant_id = '{tenant.Id}'") >= 1);
        Assert.True(await ScalarAsync<long>($"SELECT count(*) FROM subscriptions.subscription_events WHERE tenant_id = '{tenant.Id}'") >= 6);

        // …y el de los alumnos, de este gimnasio y del vecino, quedó byte a byte igual.
        Assert.Equal(before, await StudentFingerprintAsync(tenant.Id));
        Assert.Equal(neighbourBefore, await StudentFingerprintAsync(neighbour.Id));

        // Al revés también: cobrar a un alumno no toca el contrato comercial ni sus facturas.
        var subscriptionBefore = await ScalarAsync<string>($"SELECT md5(string_agg(t::text, '|' ORDER BY t.id)) FROM subscriptions.subscriptions t WHERE t.tenant_id = '{tenant.Id}'");
        var invoicesBefore = await ScalarAsync<string>($"SELECT md5(string_agg(t::text, '|' ORDER BY t.id)) FROM subscriptions.invoices t WHERE t.tenant_id = '{tenant.Id}'");
        var branchId = await TestApi.CreateBranchAsync(tenant.Owner, "GYE");
        var other = (await JsonAsync(await PostMemberAsync(tenant.Owner, branchId))).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Created, (await tenant.Owner.PostAsJsonAsync("/api/v1/payment-obligations", new { memberId = other, concept = "Producto", amount = 15m, taxKind = "General" })).StatusCode);
        var studentPayment = await PostPaymentAsync(tenant.Owner, PaymentBody(other, 15m, "Card", $"VCH-{Guid.NewGuid():N}"[..20]));
        Assert.True(studentPayment.StatusCode == HttpStatusCode.Created, await studentPayment.Content.ReadAsStringAsync());
        Assert.Equal(subscriptionBefore, await ScalarAsync<string>($"SELECT md5(string_agg(t::text, '|' ORDER BY t.id)) FROM subscriptions.subscriptions t WHERE t.tenant_id = '{tenant.Id}'"));
        Assert.Equal(invoicesBefore, await ScalarAsync<string>($"SELECT md5(string_agg(t::text, '|' ORDER BY t.id)) FROM subscriptions.invoices t WHERE t.tenant_id = '{tenant.Id}'"));
    }

    [Fact]
    public async Task Cambiar_o_desactivar_un_plan_conserva_lo_ya_contratado_emitido_y_cobrado()
    {
        var (planId, code) = await PlanAsync(30m);
        var tenant = await TenantAsync(code);
        var invoice = (await PlatformAsync(HttpMethod.Post, $"/api/v1/platform/tenants/{tenant.Id}/subscription/invoices")).Body!.Value;
        var invoiceId = invoice.GetProperty("id").GetGuid();
        var amount = invoice.GetProperty("amount").GetDecimal();
        var paid = await PlatformAsync(HttpMethod.Post, $"/api/v1/platform/billing/invoices/{invoiceId}/pay",
            new { amount, currency = "USD", method = "Cash" }, Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.OK, paid.Status);

        // La plataforma sube el precio, cambia límites y finalmente desactiva el plan.
        var update = await PlatformAsync(HttpMethod.Put, $"/api/v1/platform/plans/{planId}", new
        {
            name = $"Plan {code} v2", description = (string?)null, rules = (string?)null, currency = "USD", billingPeriod = "Monthly", price = 99m, trialDays = 10, graceDays = (int?)null,
            features = AllFeatures,
            limits = new object[]
            {
                new { code = "branches", value = 1L }, new { code = "internal_users", value = (long?)null },
                new { code = "members", value = 5L }, new { code = "storage_mb", value = (long?)null }
            },
            changeNote = "Nuevo precio"
        });
        Assert.Equal(HttpStatusCode.OK, update.Status);
        Assert.Equal(HttpStatusCode.OK, (await PlatformAsync(HttpMethod.Patch, $"/api/v1/platform/plans/{planId}/status", new { isActive = false })).Status);

        // Lo contratado, lo emitido y lo cobrado conservan el precio, el plan y la versión de su momento.
        Assert.Equal(30m, await ScalarAsync<decimal>($"SELECT price FROM subscriptions.subscriptions WHERE tenant_id = '{tenant.Id}'"));
        Assert.Equal(1, await ScalarAsync<int>($"SELECT plan_version_number FROM subscriptions.subscriptions WHERE tenant_id = '{tenant.Id}'"));
        Assert.Equal(30m, await ScalarAsync<decimal>($"SELECT amount FROM subscriptions.invoices WHERE id = '{invoiceId}'"));
        Assert.Equal(30m, await ScalarAsync<decimal>($"SELECT amount FROM subscriptions.payments WHERE invoice_id = '{invoiceId}'"));
        Assert.Equal(2L, await ScalarAsync<long>($"SELECT count(*) FROM subscriptions.plan_versions WHERE plan_id = '{planId}'"));
        Assert.False(await ScalarAsync<bool>($"SELECT is_active FROM subscriptions.plans WHERE id = '{planId}'"));

        // Los límites que ya estaban vigentes para el gimnasio no cambiaron por la edición del plan.
        Assert.Equal(100L, await ScalarAsync<long>($"SELECT (limits ->> 'members')::bigint FROM subscriptions.tenant_entitlements WHERE tenant_id = '{tenant.Id}'"));
        Assert.Equal(HttpStatusCode.OK, (await tenant.Owner.GetAsync("/api/v1/my/subscription")).StatusCode);
    }
}
