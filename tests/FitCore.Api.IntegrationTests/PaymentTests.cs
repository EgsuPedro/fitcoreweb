using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FitCore.Api.IntegrationTests.Infrastructure;
using Npgsql;
using static FitCore.Api.IntegrationTests.Infrastructure.CommercialTestApi;

namespace FitCore.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
public class PaymentTests(ApiFixture api)
{
    private sealed record Setup(TestTenant Tenant, Guid Branch, MemberDto Member, PlanDto Plan, MembershipDto Membership);

    private async Task<Setup> NewSetupAsync(decimal price = 34.50m, string code = "PAY")
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var branch = await TestApi.CreateBranchAsync(tenant.Owner, code);
        var member = await MemberTests.CreateMemberAsync(tenant.Owner, branch);
        var plan = await CreatePlanAsync(tenant.Owner, "Mensual", price);
        var membership = await EnrollAsync(tenant.Owner, member.Id, plan.Id);
        return new Setup(tenant, branch, member, plan, membership);
    }

    private static async Task<MembershipDto> EnrollAsync(HttpClient client, Guid memberId, Guid planId, DateOnly? start = null)
    {
        var response = await client.PostAsJsonAsync("/api/v1/memberships", new { memberId, planId, startDate = start });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<MembershipDto>())!;
    }

    private static async Task<ObligationDto> GetObligationAsync(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<ObligationDto>($"/api/v1/payment-obligations/{id}"))!;

    private static async Task<MembershipDto> GetMembershipAsync(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<MembershipDto>($"/api/v1/memberships/{id}"))!;

    private static async Task SetPolicyAsync(HttpClient owner, string policy, int minPercent = 50) =>
        Assert.Equal(
            HttpStatusCode.OK,
            (await owner.PutAsJsonAsync("/api/v1/commercial-settings", new { accessPolicy = policy, minPaymentPercent = minPercent, dueDays = 7, maxDiscountPercent = 30 })).StatusCode);

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(api.AdminConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    // ---- Pagos completos y abonos ----------------------------------------------------------------------------------

    [Fact]
    public async Task Un_pago_completo_salda_la_obligacion_genera_recibo_y_activa_la_membresia()
    {
        var s = await NewSetupAsync();

        var payment = await PayAsync(s.Tenant.Owner, s.Member.Id, 34.50m);

        Assert.Equal("Confirmed", payment.Status);
        Assert.Equal("RC-000001", payment.ReceiptNumber);
        Assert.Equal(34.50m, payment.Amount);
        Assert.Equal(s.Branch, payment.BranchId);
        Assert.Equal("VCH-0001", payment.Reference);
        Assert.NotNull(payment.CreatedBy);
        var line = Assert.Single(payment.Allocations);
        Assert.Equal(s.Membership.Obligation!.Id, line.ObligationId);
        Assert.Equal(34.50m, line.Amount);

        var obligation = await GetObligationAsync(s.Tenant.Owner, s.Membership.Obligation.Id);
        Assert.Equal("Paid", obligation.Status);
        Assert.Equal(34.50m, obligation.PaidAmount);
        Assert.Equal(0m, obligation.ReservedAmount);
        Assert.Equal(0m, obligation.Balance);

        var membership = await GetMembershipAsync(s.Tenant.Owner, s.Membership.Id);
        Assert.Equal("Active", membership.Status);
        Assert.Equal("Paid", membership.Obligation!.Status);
        var events = await s.Tenant.Owner.GetFromJsonAsync<MembershipEventDto[]>($"/api/v1/memberships/{s.Membership.Id}/events");
        Assert.Equal(["Created", "Activated"], events!.Select(e => e.Type));
    }

    [Fact]
    public async Task Varios_abonos_van_reduciendo_el_saldo_y_nunca_lo_dejan_negativo()
    {
        var s = await NewSetupAsync();
        var obligationId = s.Membership.Obligation!.Id;

        await PayAsync(s.Tenant.Owner, s.Member.Id, 10m, reference: "VCH-A");
        var afterFirst = await GetObligationAsync(s.Tenant.Owner, obligationId);
        Assert.Equal("PartiallyPaid", afterFirst.Status);
        Assert.Equal(10m, afterFirst.PaidAmount);
        Assert.Equal(24.50m, afterFirst.Balance);

        await PayAsync(s.Tenant.Owner, s.Member.Id, 14.50m, reference: "VCH-B");
        var afterSecond = await GetObligationAsync(s.Tenant.Owner, obligationId);
        Assert.Equal(10m, afterSecond.Balance);

        await PayAsync(s.Tenant.Owner, s.Member.Id, 10m, reference: "VCH-C");
        var paid = await GetObligationAsync(s.Tenant.Owner, obligationId);
        Assert.Equal("Paid", paid.Status);
        Assert.Equal(34.50m, paid.PaidAmount);
        Assert.Equal(0m, paid.Balance);

        // Ya está saldada: un centavo más se rechaza y no cambia nada.
        var extra = await PostPaymentAsync(s.Tenant.Owner, PaymentBody(s.Member.Id, 0.01m, reference: "VCH-D"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, extra.StatusCode);
        Assert.Equal("payments.nothing_to_pay", await TestApi.ProblemCodeAsync(extra));
        Assert.Equal(3, (await s.Tenant.Owner.GetFromJsonAsync<PagedDto<PaymentDto>>("/api/v1/payments"))!.TotalCount);
    }

    [Fact]
    public async Task Un_importe_mayor_al_saldo_se_rechaza_en_el_backend_con_o_sin_asignaciones()
    {
        var s = await NewSetupAsync();
        await PayAsync(s.Tenant.Owner, s.Member.Id, 30m);

        var auto = await PostPaymentAsync(s.Tenant.Owner, PaymentBody(s.Member.Id, 4.51m));
        Assert.Equal("payments.exceeds_balance", await TestApi.ProblemCodeAsync(auto));

        var explicitAllocation = await PostPaymentAsync(s.Tenant.Owner, PaymentBody(s.Member.Id, 5m,
            allocations: [new { obligationId = s.Membership.Obligation!.Id, amount = 5m }]));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, explicitAllocation.StatusCode);
        Assert.Equal("payments.exceeds_balance", await TestApi.ProblemCodeAsync(explicitAllocation));

        // Justo el saldo restante sí pasa.
        Assert.Equal(HttpStatusCode.Created, (await PostPaymentAsync(s.Tenant.Owner, PaymentBody(s.Member.Id, 4.50m))).StatusCode);
    }

    [Fact]
    public async Task Una_asignacion_explicita_reparte_el_pago_entre_obligaciones_y_la_suma_debe_coincidir()
    {
        var s = await NewSetupAsync();
        var second = (await (await s.Tenant.Owner.PostAsJsonAsync("/api/v1/payment-obligations",
            new { memberId = s.Member.Id, concept = "Clase de spinning", amount = 11.50m, taxKind = "General" })).Content.ReadFromJsonAsync<ObligationDto>())!;
        var first = s.Membership.Obligation!;

        var mismatch = await PostPaymentAsync(s.Tenant.Owner, PaymentBody(s.Member.Id, 40m,
            allocations: [new { obligationId = first.Id, amount = 30m }, new { obligationId = second.Id, amount = 5m }]));
        Assert.Equal("payments.allocation_mismatch", await TestApi.ProblemCodeAsync(mismatch));

        var payment = await PayAsync(s.Tenant.Owner, s.Member.Id, 40m,
            allocations: [new { obligationId = first.Id, amount = 30m }, new { obligationId = second.Id, amount = 10m }]);

        Assert.Equal(2, payment.Allocations.Length);
        Assert.Equal(40m, payment.Allocations.Sum(a => a.Amount));
        Assert.Equal(4.50m, (await GetObligationAsync(s.Tenant.Owner, first.Id)).Balance);
        Assert.Equal(1.50m, (await GetObligationAsync(s.Tenant.Owner, second.Id)).Balance);
    }

    [Fact]
    public async Task Sin_asignaciones_el_pago_cubre_primero_la_obligacion_mas_antigua()
    {
        var s = await NewSetupAsync();
        var newer = (await (await s.Tenant.Owner.PostAsJsonAsync("/api/v1/payment-obligations",
            new { memberId = s.Member.Id, concept = "Camiseta", amount = 20m, taxKind = "General", dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(20)) })).Content.ReadFromJsonAsync<ObligationDto>())!;
        var older = s.Membership.Obligation!; // vence en 7 días: es la más antigua

        var payment = await PayAsync(s.Tenant.Owner, s.Member.Id, 40m);

        Assert.Equal(["OB-000001", "OB-000002"], payment.Allocations.Select(a => a.ObligationReference));
        Assert.Equal(34.50m, payment.Allocations.Single(a => a.ObligationId == older.Id).Amount);
        Assert.Equal(5.50m, payment.Allocations.Single(a => a.ObligationId == newer.Id).Amount);
        Assert.Equal("Paid", (await GetObligationAsync(s.Tenant.Owner, older.Id)).Status);
        Assert.Equal("PartiallyPaid", (await GetObligationAsync(s.Tenant.Owner, newer.Id)).Status);
    }

    [Fact]
    public async Task La_activacion_de_la_membresia_sigue_la_politica_de_acceso_del_gimnasio()
    {
        // Exigir pago completo: un abono no activa; el saldo completo sí.
        var full = await NewSetupAsync(code: "POL1");
        await SetPolicyAsync(full.Tenant.Owner, "RequireFullPayment");
        await PayAsync(full.Tenant.Owner, full.Member.Id, 20m);
        Assert.Equal("PendingPayment", (await GetMembershipAsync(full.Tenant.Owner, full.Membership.Id)).Status);
        await PayAsync(full.Tenant.Owner, full.Member.Id, 14.50m, reference: "VCH-2");
        Assert.Equal("Active", (await GetMembershipAsync(full.Tenant.Owner, full.Membership.Id)).Status);

        // Mínimo del 60 %: 50 % no alcanza; llegar al 60 % sí.
        var minimum = await NewSetupAsync(price: 100m, code: "POL2");
        await SetPolicyAsync(minimum.Tenant.Owner, "AllowWithMinimumPayment", 60);
        await PayAsync(minimum.Tenant.Owner, minimum.Member.Id, 50m);
        Assert.Equal("PendingPayment", (await GetMembershipAsync(minimum.Tenant.Owner, minimum.Membership.Id)).Status);
        await PayAsync(minimum.Tenant.Owner, minimum.Member.Id, 10m, reference: "VCH-2");
        Assert.Equal("Active", (await GetMembershipAsync(minimum.Tenant.Owner, minimum.Membership.Id)).Status);

        // Permitir hasta el vencimiento (por defecto): el primer abono activa.
        var lenient = await NewSetupAsync(code: "POL3");
        await PayAsync(lenient.Tenant.Owner, lenient.Member.Id, 1m);
        Assert.Equal("Active", (await GetMembershipAsync(lenient.Tenant.Owner, lenient.Membership.Id)).Status);
    }

    // ---- Transferencias --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Una_transferencia_queda_pendiente_reserva_saldo_y_no_activa_hasta_confirmarse()
    {
        var s = await NewSetupAsync();
        var obligationId = s.Membership.Obligation!.Id;

        var transfer = await PayAsync(s.Tenant.Owner, s.Member.Id, 30m, "BankTransfer", "TRX-884211");

        Assert.Equal("PendingVerification", transfer.Status);
        var pending = await GetObligationAsync(s.Tenant.Owner, obligationId);
        Assert.Equal("Pending", pending.Status);
        Assert.Equal(0m, pending.PaidAmount);
        Assert.Equal(30m, pending.ReservedAmount);
        Assert.Equal(34.50m, pending.Balance);
        Assert.Equal("PendingPayment", (await GetMembershipAsync(s.Tenant.Owner, s.Membership.Id)).Status);

        // Lo reservado cuenta: solo quedan 4.50 libres para otros pagos.
        var over = await PostPaymentAsync(s.Tenant.Owner, PaymentBody(s.Member.Id, 5m));
        Assert.Equal("payments.exceeds_balance", await TestApi.ProblemCodeAsync(over));
        Assert.Equal(HttpStatusCode.Created, (await PostPaymentAsync(s.Tenant.Owner, PaymentBody(s.Member.Id, 4.50m, reference: "VCH-OK"))).StatusCode);

        var confirm = await PostWithKeyAsync(s.Tenant.Owner, $"/api/v1/payments/{transfer.Id}/confirm", null);
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        var confirmed = (await confirm.Content.ReadFromJsonAsync<PaymentDto>())!;
        Assert.Equal("Confirmed", confirmed.Status);
        Assert.NotNull(confirmed.ConfirmedAt);
        Assert.NotNull(confirmed.ConfirmedBy);

        var after = await GetObligationAsync(s.Tenant.Owner, obligationId);
        Assert.Equal("Paid", after.Status);
        Assert.Equal(0m, after.Balance);
        Assert.Equal("Active", (await GetMembershipAsync(s.Tenant.Owner, s.Membership.Id)).Status);

        var twice = await PostWithKeyAsync(s.Tenant.Owner, $"/api/v1/payments/{transfer.Id}/confirm", null);
        Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);
        Assert.Equal("payments.not_pending", await TestApi.ProblemCodeAsync(twice));
    }

    [Fact]
    public async Task Solo_quien_tiene_permiso_confirma_transferencias()
    {
        var s = await NewSetupAsync();
        var transfer = await PayAsync(s.Tenant.Owner, s.Member.Id, 10m, "BankTransfer", "TRX-1");
        await TestApi.CreateUserAsync(s.Tenant.Owner, "recepcion@gym.test", "Recepcion", s.Branch);
        await TestApi.CreateUserAsync(s.Tenant.Owner, "cajero@gym.test", "Cajero", s.Branch);
        using var reception = TestApi.Authenticated(api, await TestApi.LoginAsync(api, s.Tenant.Slug, "recepcion@gym.test", TestApi.UserPassword));
        using var cashier = TestApi.Authenticated(api, await TestApi.LoginAsync(api, s.Tenant.Slug, "cajero@gym.test", TestApi.UserPassword));

        Assert.Equal(HttpStatusCode.Forbidden, (await PostWithKeyAsync(reception, $"/api/v1/payments/{transfer.Id}/confirm", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostWithKeyAsync(cashier, $"/api/v1/payments/{transfer.Id}/confirm", null)).StatusCode);
    }

    [Fact]
    public async Task Rechazar_una_transferencia_pendiente_libera_lo_reservado()
    {
        var s = await NewSetupAsync();
        var transfer = await PayAsync(s.Tenant.Owner, s.Member.Id, 34.50m, "BankTransfer", "TRX-2");

        var reverse = await PostWithKeyAsync(s.Tenant.Owner, $"/api/v1/payments/{transfer.Id}/reverse", new { reason = "El comprobante es falso" });

        Assert.Equal(HttpStatusCode.OK, reverse.StatusCode);
        var obligation = await GetObligationAsync(s.Tenant.Owner, s.Membership.Obligation!.Id);
        Assert.Equal(0m, obligation.ReservedAmount);
        Assert.Equal(34.50m, obligation.Balance);
        Assert.Equal(HttpStatusCode.Created, (await PostPaymentAsync(s.Tenant.Owner, PaymentBody(s.Member.Id, 34.50m, reference: "VCH-OK"))).StatusCode);
    }

    // ---- Datos del pago --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Las_referencias_y_el_medio_son_obligatorios_segun_el_metodo_y_nunca_se_guardan_numeros_de_tarjeta()
    {
        var s = await NewSetupAsync();

        async Task<HttpResponseMessage> TryAsync(string method, string? reference, string? detail = null) =>
            await PostPaymentAsync(s.Tenant.Owner, PaymentBody(s.Member.Id, 5m, method, reference, methodDetail: detail));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await TryAsync("BankTransfer", null)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await TryAsync("Card", "  ")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await TryAsync("Other", null)).StatusCode);

        // Un número de tarjeta (válido por Luhn) no se acepta como referencia, con o sin separadores.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await TryAsync("Card", "4111111111111111")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await TryAsync("Card", "4111 1111 1111 1111")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await TryAsync("Other", "5500-0000-0000-0004", "Link")).StatusCode);
        Assert.Equal(0L, await ScalarAsync<long>($"SELECT count(*) FROM billing.payments WHERE tenant_id = '{s.Tenant.Id}'"));

        // Una referencia de voucher normal sí; también "Otro" con su medio.
        Assert.Equal(HttpStatusCode.Created, (await TryAsync("Card", "VCH-123456")).StatusCode);
        var other = await TryAsync("Other", null, "Enlace de pago");
        Assert.Equal(HttpStatusCode.Created, other.StatusCode);
        Assert.Equal("Enlace de pago", (await other.Content.ReadFromJsonAsync<PaymentDto>())!.MethodDetail);
    }

    [Fact]
    public async Task Los_importes_fechas_y_obligaciones_invalidas_se_rechazan_sin_dejar_rastro()
    {
        var s = await NewSetupAsync();
        var other = await MemberTests.CreateMemberAsync(s.Tenant.Owner, s.Branch);
        var otherPlan = await CreatePlanAsync(s.Tenant.Owner, "Otro", 20m);
        var otherMembership = await EnrollAsync(s.Tenant.Owner, other.Id, otherPlan.Id);

        foreach (var amount in new[] { 0m, -1m, 100000.01m, 1.234m })
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostPaymentAsync(s.Tenant.Owner, PaymentBody(s.Member.Id, amount))).StatusCode);
        }

        // Fecha futura o de hace más de 30 días.
        var future = await PostPaymentAsync(s.Tenant.Owner, PaymentBody(s.Member.Id, 5m, paidAt: DateTimeOffset.UtcNow.AddDays(2)));
        Assert.Equal("payments.paid_at_invalid", await TestApi.ProblemCodeAsync(future));
        var old = await PostPaymentAsync(s.Tenant.Owner, PaymentBody(s.Member.Id, 5m, paidAt: DateTimeOffset.UtcNow.AddDays(-45)));
        Assert.Equal("payments.paid_at_invalid", await TestApi.ProblemCodeAsync(old));

        // Obligación de otro alumno: no existe para este pago.
        var foreignObligation = await PostPaymentAsync(s.Tenant.Owner, PaymentBody(s.Member.Id, 5m,
            allocations: [new { obligationId = otherMembership.Obligation!.Id, amount = 5m }]));
        Assert.Equal(HttpStatusCode.NotFound, foreignObligation.StatusCode);
        Assert.Equal("obligations.not_found", await TestApi.ProblemCodeAsync(foreignObligation));

        // Obligación anulada o ya pagada.
        await PostWithKeyAsync(s.Tenant.Owner, $"/api/v1/payment-obligations/{otherMembership.Obligation.Id}/void", null);
        await s.Tenant.Owner.PostAsJsonAsync($"/api/v1/payment-obligations/{otherMembership.Obligation.Id}/void", new { reason = "Prueba de anulación" });
        var voided = await PostPaymentAsync(s.Tenant.Owner, PaymentBody(other.Id, 5m,
            allocations: [new { obligationId = otherMembership.Obligation.Id, amount = 5m }]));
        Assert.Equal("payments.obligation_not_payable", await TestApi.ProblemCodeAsync(voided));

        await PayAsync(s.Tenant.Owner, s.Member.Id, 34.50m);
        var alreadyPaid = await PostPaymentAsync(s.Tenant.Owner, PaymentBody(s.Member.Id, 5m,
            allocations: [new { obligationId = s.Membership.Obligation!.Id, amount = 5m }]));
        Assert.Equal("payments.obligation_not_payable", await TestApi.ProblemCodeAsync(alreadyPaid));

        Assert.Equal(1L, await ScalarAsync<long>($"SELECT count(*) FROM billing.payments WHERE tenant_id = '{s.Tenant.Id}'"));
    }

    [Fact]
    public async Task El_efectivo_sin_una_caja_abierta_se_rechaza()
    {
        var s = await NewSetupAsync();

        var response = await PostPaymentAsync(s.Tenant.Owner, PaymentBody(s.Member.Id, 10m, "Cash", null));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("payments.cash_requires_open_cash_session", await TestApi.ProblemCodeAsync(response));
        Assert.Equal(0L, await ScalarAsync<long>($"SELECT count(*) FROM billing.payments WHERE tenant_id = '{s.Tenant.Id}'"));
    }

    // ---- Idempotencia ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Sin_clave_de_idempotencia_o_con_una_demasiado_larga_el_pago_se_rechaza()
    {
        var s = await NewSetupAsync();

        var missing = await PostPaymentAsync(s.Tenant.Owner, PaymentBody(s.Member.Id, 5m), sendKey: false);
        var tooLong = await PostPaymentAsync(s.Tenant.Owner, PaymentBody(s.Member.Id, 5m), key: new string('k', 101));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, missing.StatusCode);
        Assert.Equal("idempotency.key_required", await TestApi.ProblemCodeAsync(missing));
        Assert.Equal("idempotency.key_required", await TestApi.ProblemCodeAsync(tooLong));
        Assert.Equal(0L, await ScalarAsync<long>($"SELECT count(*) FROM billing.payments WHERE tenant_id = '{s.Tenant.Id}'"));
    }

    [Fact]
    public async Task Repetir_la_misma_solicitud_con_la_misma_clave_no_duplica_el_pago()
    {
        var s = await NewSetupAsync();
        var key = Guid.NewGuid().ToString();
        var body = PaymentBody(s.Member.Id, 10m);

        var first = await PostPaymentAsync(s.Tenant.Owner, body, key);
        var retry = await PostPaymentAsync(s.Tenant.Owner, body, key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        var a = (await first.Content.ReadFromJsonAsync<PaymentDto>())!;
        var b = (await retry.Content.ReadFromJsonAsync<PaymentDto>())!;
        Assert.Equal(a.Id, b.Id);
        Assert.Equal(a.ReceiptNumber, b.ReceiptNumber);
        Assert.Equal(1L, await ScalarAsync<long>($"SELECT count(*) FROM billing.payments WHERE tenant_id = '{s.Tenant.Id}'"));
        Assert.Equal(24.50m, (await GetObligationAsync(s.Tenant.Owner, s.Membership.Obligation!.Id)).Balance);
    }

    [Fact]
    public async Task Reusar_una_clave_con_otro_contenido_u_otro_usuario_se_rechaza()
    {
        var s = await NewSetupAsync();
        await TestApi.CreateUserAsync(s.Tenant.Owner, "cajero@gym.test", "Cajero", s.Branch);
        using var cashier = TestApi.Authenticated(api, await TestApi.LoginAsync(api, s.Tenant.Slug, "cajero@gym.test", TestApi.UserPassword));
        var key = Guid.NewGuid().ToString();
        await PostPaymentAsync(s.Tenant.Owner, PaymentBody(s.Member.Id, 10m), key);

        var otherAmount = await PostPaymentAsync(s.Tenant.Owner, PaymentBody(s.Member.Id, 11m), key);
        var otherUser = await PostPaymentAsync(cashier, PaymentBody(s.Member.Id, 10m), key);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, otherAmount.StatusCode);
        Assert.Equal("idempotency.key_reused", await TestApi.ProblemCodeAsync(otherAmount));
        Assert.Equal("idempotency.key_reused", await TestApi.ProblemCodeAsync(otherUser));
        Assert.Equal(1L, await ScalarAsync<long>($"SELECT count(*) FROM billing.payments WHERE tenant_id = '{s.Tenant.Id}'"));
    }

    [Fact]
    public async Task Ocho_solicitudes_simultaneas_con_la_misma_clave_producen_un_solo_pago()
    {
        var s = await NewSetupAsync();
        var key = Guid.NewGuid().ToString();
        var body = PaymentBody(s.Member.Id, 10m);

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => PostPaymentAsync(s.Tenant.Owner, body, key)));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        var ids = await Task.WhenAll(responses.Select(async r => (await r.Content.ReadFromJsonAsync<PaymentDto>())!.Id));
        Assert.Single(ids.Distinct());
        Assert.Equal(1L, await ScalarAsync<long>($"SELECT count(*) FROM billing.payments WHERE tenant_id = '{s.Tenant.Id}'"));
        Assert.Equal(10m, (await GetObligationAsync(s.Tenant.Owner, s.Membership.Obligation!.Id)).PaidAmount);
    }

    // ---- Concurrencia ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Diez_abonos_simultaneos_nunca_pasan_del_importe_exigible_ni_dejan_saldo_negativo()
    {
        var s = await NewSetupAsync(); // exigible 34.50

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(i =>
            PostPaymentAsync(s.Tenant.Owner, PaymentBody(s.Member.Id, 10m, reference: $"VCH-{i}"))));

        // 34.50 admite exactamente tres abonos de 10.00; el resto debe ser rechazado.
        Assert.Equal(3, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        foreach (var rejected in responses.Where(r => r.StatusCode != HttpStatusCode.Created))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
            Assert.Contains(await TestApi.ProblemCodeAsync(rejected), new[] { "payments.exceeds_balance", "payments.nothing_to_pay" });
        }

        var obligation = await GetObligationAsync(s.Tenant.Owner, s.Membership.Obligation!.Id);
        Assert.Equal(30m, obligation.PaidAmount);
        Assert.Equal(4.50m, obligation.Balance);
        Assert.Equal("PartiallyPaid", obligation.Status);
        Assert.Equal(30m, await ScalarAsync<decimal>($"SELECT COALESCE(SUM(amount), 0) FROM billing.payment_allocations WHERE tenant_id = '{s.Tenant.Id}'"));
        Assert.Equal(3L, await ScalarAsync<long>($"SELECT count(*) FROM billing.payments WHERE tenant_id = '{s.Tenant.Id}'"));
    }

    [Fact]
    public async Task Dos_usuarios_pagando_el_saldo_completo_a_la_vez_solo_uno_lo_logra()
    {
        var s = await NewSetupAsync();
        await TestApi.CreateUserAsync(s.Tenant.Owner, "cajero@gym.test", "Cajero", s.Branch);
        using var cashier = TestApi.Authenticated(api, await TestApi.LoginAsync(api, s.Tenant.Slug, "cajero@gym.test", TestApi.UserPassword));

        var responses = await Task.WhenAll(
            PostPaymentAsync(s.Tenant.Owner, PaymentBody(s.Member.Id, 34.50m, reference: "A")),
            PostPaymentAsync(cashier, PaymentBody(s.Member.Id, 34.50m, reference: "B")));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        var obligation = await GetObligationAsync(s.Tenant.Owner, s.Membership.Obligation!.Id);
        Assert.Equal("Paid", obligation.Status);
        Assert.Equal(0m, obligation.Balance);
    }

    // ---- Reversos --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Reversar_un_pago_conserva_el_registro_devuelve_el_saldo_y_exige_motivo()
    {
        var s = await NewSetupAsync();
        var first = await PayAsync(s.Tenant.Owner, s.Member.Id, 20m, reference: "VCH-A");
        await PayAsync(s.Tenant.Owner, s.Member.Id, 14.50m, reference: "VCH-B");
        var url = $"/api/v1/payments/{first.Id}/reverse";

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostWithKeyAsync(s.Tenant.Owner, url, new { reason = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostWithKeyAsync(s.Tenant.Owner, url, new { reason = "no" })).StatusCode);
        Assert.Equal("Paid", (await GetObligationAsync(s.Tenant.Owner, s.Membership.Obligation!.Id)).Status);

        var response = await PostWithKeyAsync(s.Tenant.Owner, url, new { reason = "Cobro duplicado por error" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var reversed = (await response.Content.ReadFromJsonAsync<PaymentDto>())!;
        Assert.Equal("Reversed", reversed.Status);
        Assert.Equal("Cobro duplicado por error", reversed.ReversalReason);
        Assert.NotNull(reversed.ReversedAt);
        Assert.Equal(20m, reversed.Amount);
        Assert.Single(reversed.Allocations);

        var obligation = await GetObligationAsync(s.Tenant.Owner, s.Membership.Obligation.Id);
        Assert.Equal("PartiallyPaid", obligation.Status);
        Assert.Equal(14.50m, obligation.PaidAmount);
        Assert.Equal(20m, obligation.Balance);

        // El registro sigue en el historial y el saldo liberado se puede volver a cobrar.
        Assert.Equal(2, (await s.Tenant.Owner.GetFromJsonAsync<PagedDto<PaymentDto>>("/api/v1/payments"))!.TotalCount);
        Assert.Equal(HttpStatusCode.Created, (await PostPaymentAsync(s.Tenant.Owner, PaymentBody(s.Member.Id, 20m, reference: "VCH-C"))).StatusCode);

        var again = await PostWithKeyAsync(s.Tenant.Owner, url, new { reason = "Otra vez" });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("payments.already_reversed", await TestApi.ProblemCodeAsync(again));
    }

    [Fact]
    public async Task Reversar_el_unico_pago_deja_la_obligacion_pendiente_y_repetir_el_reverso_con_la_misma_clave_devuelve_lo_mismo()
    {
        var s = await NewSetupAsync();
        var payment = await PayAsync(s.Tenant.Owner, s.Member.Id, 34.50m);
        var key = Guid.NewGuid().ToString();

        var first = await PostWithKeyAsync(s.Tenant.Owner, $"/api/v1/payments/{payment.Id}/reverse", new { reason = "Error de digitación" }, key);
        var retry = await PostWithKeyAsync(s.Tenant.Owner, $"/api/v1/payments/{payment.Id}/reverse", new { reason = "Error de digitación" }, key);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal("Reversed", (await retry.Content.ReadFromJsonAsync<PaymentDto>())!.Status);
        var obligation = await GetObligationAsync(s.Tenant.Owner, s.Membership.Obligation!.Id);
        Assert.Equal("Pending", obligation.Status);
        Assert.Equal(34.50m, obligation.Balance);
        // La membresía ya activada no se desactiva sola: el acceso se decide con los saldos vigentes.
        Assert.Equal("Active", (await GetMembershipAsync(s.Tenant.Owner, s.Membership.Id)).Status);
    }

    [Fact]
    public async Task Solo_quien_tiene_permiso_revierte_pagos()
    {
        var s = await NewSetupAsync();
        var payment = await PayAsync(s.Tenant.Owner, s.Member.Id, 10m);
        await TestApi.CreateUserAsync(s.Tenant.Owner, "cajero@gym.test", "Cajero", s.Branch);
        await TestApi.CreateUserAsync(s.Tenant.Owner, "admin@gym.test", "AdministradorSucursal", s.Branch);
        using var cashier = TestApi.Authenticated(api, await TestApi.LoginAsync(api, s.Tenant.Slug, "cajero@gym.test", TestApi.UserPassword));
        using var admin = TestApi.Authenticated(api, await TestApi.LoginAsync(api, s.Tenant.Slug, "admin@gym.test", TestApi.UserPassword));

        var denied = await PostWithKeyAsync(cashier, $"/api/v1/payments/{payment.Id}/reverse", new { reason = "Sin permiso" });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal("Confirmed", (await s.Tenant.Owner.GetFromJsonAsync<PaymentDto>($"/api/v1/payments/{payment.Id}"))!.Status);

        Assert.Equal(HttpStatusCode.OK, (await PostWithKeyAsync(admin, $"/api/v1/payments/{payment.Id}/reverse", new { reason = "Autorizado por administración" })).StatusCode);
    }

    // ---- Obligaciones ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Una_obligacion_con_pagos_vigentes_no_se_anula_hasta_revertirlos()
    {
        var s = await NewSetupAsync();
        var payment = await PayAsync(s.Tenant.Owner, s.Member.Id, 10m);
        var url = $"/api/v1/payment-obligations/{s.Membership.Obligation!.Id}/void";

        var blocked = await s.Tenant.Owner.PostAsJsonAsync(url, new { reason = "Intento con pagos" });
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Equal("obligations.has_payments", await TestApi.ProblemCodeAsync(blocked));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await s.Tenant.Owner.PostAsJsonAsync(url, new { reason = "" })).StatusCode);

        await PostWithKeyAsync(s.Tenant.Owner, $"/api/v1/payments/{payment.Id}/reverse", new { reason = "Se devolvió el dinero" });
        var voided = await s.Tenant.Owner.PostAsJsonAsync(url, new { reason = "Alumno desistió" });

        Assert.Equal(HttpStatusCode.OK, voided.StatusCode);
        var dto = (await voided.Content.ReadFromJsonAsync<ObligationDto>())!;
        Assert.Equal("Voided", dto.Status);
        Assert.Equal("Alumno desistió", dto.VoidReason);
        Assert.Equal(0m, dto.Balance);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Tenant.Owner.PostAsJsonAsync(url, new { reason = "Otra vez" })).StatusCode);
    }

    [Fact]
    public async Task Cancelar_una_membresia_con_pagos_conserva_la_obligacion_y_el_dinero()
    {
        var s = await NewSetupAsync();
        await PayAsync(s.Tenant.Owner, s.Member.Id, 10m);

        var cancel = await s.Tenant.Owner.PostAsJsonAsync($"/api/v1/memberships/{s.Membership.Id}/cancel", new { reason = "El alumno se mudó" });

        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        var membership = (await cancel.Content.ReadFromJsonAsync<MembershipDto>())!;
        Assert.Equal("Cancelled", membership.Status);
        Assert.Equal("PartiallyPaid", membership.Obligation!.Status);
        Assert.Equal(10m, membership.Obligation.PaidAmount);
        var events = await s.Tenant.Owner.GetFromJsonAsync<MembershipEventDto[]>($"/api/v1/memberships/{s.Membership.Id}/events");
        using var details = JsonDocument.Parse(events!.Last().Details!);
        Assert.Equal("OB-000001", details.RootElement.GetProperty("obligationsKeptWithPayments")[0].GetString());
    }

    [Fact]
    public async Task Una_obligacion_manual_desglosa_el_IVA_se_valida_y_se_puede_cobrar()
    {
        var s = await NewSetupAsync();

        var response = await s.Tenant.Owner.PostAsJsonAsync("/api/v1/payment-obligations",
            new { memberId = s.Member.Id, concept = "Clase de yoga", amount = 23m, taxKind = "General" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var obligation = (await response.Content.ReadFromJsonAsync<ObligationDto>())!;
        Assert.Null(obligation.MembershipId);
        Assert.Equal(20.00m, obligation.Subtotal);
        Assert.Equal(3.00m, obligation.TaxAmount);
        Assert.Equal(15m, obligation.TaxPercent);
        Assert.Equal("Pending", obligation.Status);
        Assert.Equal(23m, obligation.Balance);

        foreach (var body in new object[]
        {
            new { memberId = s.Member.Id, concept = "ab", amount = 10m, taxKind = "General" },
            new { memberId = s.Member.Id, concept = "Concepto válido", amount = 0m, taxKind = "General" },
            new { memberId = s.Member.Id, concept = "Concepto válido", amount = 10.123m, taxKind = "General" }
        })
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await s.Tenant.Owner.PostAsJsonAsync("/api/v1/payment-obligations", body)).StatusCode);
        }

        var payment = await PayAsync(s.Tenant.Owner, s.Member.Id, 23m, allocations: [new { obligationId = obligation.Id, amount = 23m }]);
        Assert.Equal("Paid", (await GetObligationAsync(s.Tenant.Owner, obligation.Id)).Status);
        Assert.Equal(23m, payment.Amount);
    }

    [Fact]
    public async Task Crear_o_anular_obligaciones_exige_sus_permisos()
    {
        var s = await NewSetupAsync();
        await TestApi.CreateUserAsync(s.Tenant.Owner, "recepcion@gym.test", "Recepcion", s.Branch);
        await TestApi.CreateUserAsync(s.Tenant.Owner, "admin@gym.test", "AdministradorSucursal", s.Branch);
        using var reception = TestApi.Authenticated(api, await TestApi.LoginAsync(api, s.Tenant.Slug, "recepcion@gym.test", TestApi.UserPassword));
        using var admin = TestApi.Authenticated(api, await TestApi.LoginAsync(api, s.Tenant.Slug, "admin@gym.test", TestApi.UserPassword));
        var body = new { memberId = s.Member.Id, concept = "Clase suelta", amount = 10m, taxKind = "General" };

        Assert.Equal(HttpStatusCode.Forbidden, (await reception.PostAsJsonAsync("/api/v1/payment-obligations", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reception.PostAsJsonAsync($"/api/v1/payment-obligations/{s.Membership.Obligation!.Id}/void", new { reason = "Sin permiso" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/v1/payment-obligations", body)).StatusCode);
    }

    // ---- Consultas y recibo ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Los_pagos_se_consultan_por_alumno_medio_estado_fecha_y_busqueda()
    {
        var s = await NewSetupAsync();
        var otherMember = await MemberTests.CreateMemberAsync(s.Tenant.Owner, s.Branch, first: "Beatriz", last: "Cevallos");
        var otherMembership = await EnrollAsync(s.Tenant.Owner, otherMember.Id, s.Plan.Id);
        var card = await PayAsync(s.Tenant.Owner, s.Member.Id, 10m, "Card", "VCH-777");
        var transfer = await PayAsync(s.Tenant.Owner, otherMember.Id, 20m, "BankTransfer", "TRX-555");
        var reversed = await PayAsync(s.Tenant.Owner, s.Member.Id, 5m, "Card", "VCH-888");
        await PostWithKeyAsync(s.Tenant.Owner, $"/api/v1/payments/{reversed.Id}/reverse", new { reason = "Error de monto" });
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        async Task<Guid[]> ListAsync(string query) =>
            [.. (await s.Tenant.Owner.GetFromJsonAsync<PagedDto<PaymentDto>>($"/api/v1/payments?{query}"))!.Items.Select(p => p.Id)];

        Assert.Equivalent(new[] { card.Id, reversed.Id }, await ListAsync($"memberId={s.Member.Id}"));
        Assert.Equal([transfer.Id], await ListAsync("method=BankTransfer"));
        Assert.Equal([reversed.Id], await ListAsync("status=Reversed"));
        Assert.Equal([transfer.Id], await ListAsync("status=PendingVerification"));
        Assert.Equal([card.Id], await ListAsync("search=VCH-777"));
        Assert.Equal([transfer.Id], await ListAsync("search=cevallos"));
        Assert.Equal([card.Id], await ListAsync($"search={card.ReceiptNumber}"));
        Assert.Equal(3, (await ListAsync($"from={today.AddDays(-1):yyyy-MM-dd}&to={today.AddDays(1):yyyy-MM-dd}&branchId={s.Branch}")).Length);
        Assert.Empty(await ListAsync($"to={today.AddDays(-5):yyyy-MM-dd}"));
        Assert.Empty(await ListAsync($"from={today.AddDays(5):yyyy-MM-dd}"));
        Assert.Equal(3, (await s.Tenant.Owner.GetFromJsonAsync<PagedDto<PaymentDto>>("/api/v1/payments?pageSize=2"))!.TotalCount);
        Assert.Equal(otherMembership.Obligation!.PayableAmount, (await GetObligationAsync(s.Tenant.Owner, otherMembership.Obligation.Id)).Balance + 0m);
    }

    [Fact]
    public async Task El_recibo_interno_muestra_las_lineas_el_total_y_quien_recibio_el_pago()
    {
        var s = await NewSetupAsync();
        var payment = await PayAsync(s.Tenant.Owner, s.Member.Id, 20m);

        var receipt = (await s.Tenant.Owner.GetFromJsonAsync<ReceiptDto>($"/api/v1/payments/{payment.Id}/receipt"))!;

        Assert.Equal("RC-000001", receipt.ReceiptNumber);
        Assert.Contains("No es una factura", receipt.Notice, StringComparison.Ordinal);
        Assert.Equal("Confirmed", receipt.Status);
        Assert.Equal("PAY", receipt.BranchCode);
        Assert.Equal(20m, receipt.Total);
        Assert.Equal("Dueño de Prueba", receipt.ReceivedBy);
        var line = Assert.Single(receipt.Lines);
        Assert.Equal("OB-000001", line.ObligationReference);
        Assert.Equal(20m, line.Amount);
        Assert.Equal(34.50m, line.ObligationPayable);
        Assert.Equal(14.50m, line.ObligationBalance);
    }

    // ---- Aislamiento y permisos ------------------------------------------------------------------------------------

    [Fact]
    public async Task Otro_gimnasio_no_puede_ver_cobrar_confirmar_ni_revertir()
    {
        var a = await NewSetupAsync(code: "ISA");
        var b = await NewSetupAsync(code: "ISB");
        var paymentB = await PayAsync(b.Tenant.Owner, b.Member.Id, 10m, "BankTransfer", "TRX-9");

        Assert.Equal(HttpStatusCode.NotFound, (await a.Tenant.Owner.GetAsync($"/api/v1/payments/{paymentB.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.Tenant.Owner.GetAsync($"/api/v1/payments/{paymentB.Id}/receipt")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostWithKeyAsync(a.Tenant.Owner, $"/api/v1/payments/{paymentB.Id}/confirm", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostWithKeyAsync(a.Tenant.Owner, $"/api/v1/payments/{paymentB.Id}/reverse", new { reason = "Intento ajeno" })).StatusCode);

        var foreignMember = await PostPaymentAsync(a.Tenant.Owner, PaymentBody(b.Member.Id, 5m));
        Assert.Equal(HttpStatusCode.NotFound, foreignMember.StatusCode);
        var foreignObligation = await PostPaymentAsync(a.Tenant.Owner, PaymentBody(a.Member.Id, 5m,
            allocations: [new { obligationId = b.Membership.Obligation!.Id, amount = 5m }]));
        Assert.Equal(HttpStatusCode.NotFound, foreignObligation.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.Tenant.Owner.PostAsJsonAsync($"/api/v1/payment-obligations/{b.Membership.Obligation.Id}/void", new { reason = "Intento ajeno" })).StatusCode);

        Assert.Equal(0, (await a.Tenant.Owner.GetFromJsonAsync<PagedDto<PaymentDto>>("/api/v1/payments"))!.TotalCount);
        Assert.Equal("PendingVerification", (await b.Tenant.Owner.GetFromJsonAsync<PaymentDto>($"/api/v1/payments/{paymentB.Id}"))!.Status);
        Assert.Equal(34.50m, (await GetObligationAsync(b.Tenant.Owner, b.Membership.Obligation.Id)).Balance);
    }

    [Fact]
    public async Task Quien_solo_accede_a_una_sucursal_no_ve_ni_cobra_en_otra()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var north = await TestApi.CreateBranchAsync(tenant.Owner, "CN");
        var south = await TestApi.CreateBranchAsync(tenant.Owner, "CS");
        var plan = await CreatePlanAsync(tenant.Owner, "Global", 30m);
        var memberNorth = await MemberTests.CreateMemberAsync(tenant.Owner, north);
        var memberSouth = await MemberTests.CreateMemberAsync(tenant.Owner, south);
        await EnrollAsync(tenant.Owner, memberNorth.Id, plan.Id);
        await EnrollAsync(tenant.Owner, memberSouth.Id, plan.Id);
        var paymentSouth = await PayAsync(tenant.Owner, memberSouth.Id, 10m);
        await TestApi.CreateUserAsync(tenant.Owner, "cajero.norte@gym.test", "Cajero", north);
        using var cashier = TestApi.Authenticated(api, await TestApi.LoginAsync(api, tenant.Slug, "cajero.norte@gym.test", TestApi.UserPassword));

        Assert.Equal(HttpStatusCode.NotFound, (await cashier.GetAsync($"/api/v1/payments/{paymentSouth.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync($"/api/v1/payments?branchId={south}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostWithKeyAsync(cashier, $"/api/v1/payments/{paymentSouth.Id}/confirm", null)).StatusCode);
        var denied = await PostPaymentAsync(cashier, PaymentBody(memberSouth.Id, 5m));
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);

        var own = await PostPaymentAsync(cashier, PaymentBody(memberNorth.Id, 5m));
        Assert.Equal(HttpStatusCode.Created, own.StatusCode);
        var list = await cashier.GetFromJsonAsync<PagedDto<PaymentDto>>("/api/v1/payments");
        Assert.Equal(1, list!.TotalCount);
        Assert.Equal(north, list.Items.Single().BranchId);
    }

    [Fact]
    public async Task Los_permisos_del_rol_se_aplican_a_los_pagos_y_un_alumno_inactivo_puede_saldar_su_deuda()
    {
        var s = await NewSetupAsync();
        await TestApi.CreateUserAsync(s.Tenant.Owner, "recepcion@gym.test", "Recepcion", s.Branch);
        await TestApi.CreateUserAsync(s.Tenant.Owner, "entrenador@gym.test", "Entrenador", s.Branch);
        using var reception = TestApi.Authenticated(api, await TestApi.LoginAsync(api, s.Tenant.Slug, "recepcion@gym.test", TestApi.UserPassword));
        using var trainer = TestApi.Authenticated(api, await TestApi.LoginAsync(api, s.Tenant.Slug, "entrenador@gym.test", TestApi.UserPassword));

        Assert.Equal(HttpStatusCode.Forbidden, (await trainer.GetAsync("/api/v1/payments")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostPaymentAsync(trainer, PaymentBody(s.Member.Id, 5m))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await PostPaymentAsync(reception, PaymentBody(s.Member.Id, 5m))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await reception.GetAsync("/api/v1/payments")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().GetAsync("/api/v1/payments")).StatusCode);

        // Desactivar al alumno no borra lo que debe: puede seguir pagando.
        await s.Tenant.Owner.PatchAsJsonAsync($"/api/v1/members/{s.Member.Id}/status", new { isActive = false });
        Assert.Equal(HttpStatusCode.Created, (await PostPaymentAsync(s.Tenant.Owner, PaymentBody(s.Member.Id, 5m, reference: "VCH-2"))).StatusCode);
    }

    [Fact]
    public async Task Los_pagos_y_sus_cambios_quedan_auditados_sin_datos_de_tarjeta()
    {
        var s = await NewSetupAsync();
        var payment = await PayAsync(s.Tenant.Owner, s.Member.Id, 10m);
        await PostWithKeyAsync(s.Tenant.Owner, $"/api/v1/payments/{payment.Id}/reverse", new { reason = "Prueba de auditoría" });

        var audit = (await s.Tenant.Owner.GetFromJsonAsync<PagedDto<AuditDto>>("/api/v1/audit-logs?pageSize=100"))!;

        Assert.Contains(audit.Items, l => l.Action == "Payment.Created" && l.EntityId == payment.Id.ToString());
        Assert.Contains(audit.Items, l => l.Action == "Payment.Updated" && l.EntityId == payment.Id.ToString());
        Assert.Contains(audit.Items, l => l.Action == "PaymentAllocation.Created");
        Assert.Contains(audit.Items, l => l.Action == "PaymentObligation.Updated" && l.EntityId == s.Membership.Obligation!.Id.ToString());
    }

    // ---- La base como última defensa -------------------------------------------------------------------------------

    [Fact]
    public async Task La_base_impide_sobreasignar_modificar_pagos_borrarlos_o_reactivar_un_reverso_aunque_el_codigo_falle()
    {
        var a = await NewSetupAsync(code: "DBA");
        var b = await NewSetupAsync(code: "DBB");
        var payment = await PayAsync(a.Tenant.Owner, a.Member.Id, 10m);
        var reversed = await PayAsync(a.Tenant.Owner, a.Member.Id, 5m, reference: "VCH-R");
        await PostWithKeyAsync(a.Tenant.Owner, $"/api/v1/payments/{reversed.Id}/reverse", new { reason = "Para la prueba" });
        var paymentB = await PayAsync(b.Tenant.Owner, b.Member.Id, 10m);

        await using var connection = new NpgsqlConnection(api.AppConnection);
        await connection.OpenAsync();
        await Exec(connection, $"SELECT set_config('app.tenant_id', '{a.Tenant.Id}', false)");

        // RLS: solo los pagos del gimnasio activo.
        Assert.Equal(2L, await Scalar(connection, "SELECT count(*) FROM billing.payments"));
        Assert.Equal(0L, await Scalar(connection, $"SELECT count(*) FROM billing.payments WHERE id = '{paymentB.Id}'"));
        Assert.Equal(0, await Exec(connection, $"UPDATE billing.payments SET status = 'Reversed', reversal_reason = 'x' WHERE id = '{paymentB.Id}'"));

        // Sobreasignación directa: asignar 5000 a una obligación de 10.00 supera lo exigible (lo valida un trigger).
        var small = (await (await a.Tenant.Owner.PostAsJsonAsync("/api/v1/payment-obligations",
            new { memberId = a.Member.Id, concept = "Concepto menor", amount = 10m, taxKind = "General" })).Content.ReadFromJsonAsync<ObligationDto>())!;
        var over = await Assert.ThrowsAsync<PostgresException>(() => Exec(connection, $"""
            INSERT INTO billing.payment_allocations (id, tenant_id, payment_id, obligation_id, amount)
            VALUES (gen_random_uuid(), '{a.Tenant.Id}', '{payment.Id}', '{small.Id}', 5000)
            """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, over.SqlState);

        // Un pago es inmutable: ni su importe, ni su medio, ni sus asignaciones.
        var amount = await Assert.ThrowsAsync<PostgresException>(() => Exec(connection, $"UPDATE billing.payments SET amount = 1 WHERE id = '{payment.Id}'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, amount.SqlState);
        var method = await Assert.ThrowsAsync<PostgresException>(() => Exec(connection, $"UPDATE billing.payments SET method = 'Other' WHERE id = '{payment.Id}'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, method.SqlState);
        var allocation = await Assert.ThrowsAsync<PostgresException>(() => Exec(connection, $"UPDATE billing.payment_allocations SET amount = 1 WHERE payment_id = '{payment.Id}'"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, allocation.SqlState);

        // Un reverso no se deshace ni se vuelve a "pendiente".
        var reactivate = await Assert.ThrowsAsync<PostgresException>(() => Exec(connection,
            $"UPDATE billing.payments SET status = 'Confirmed' WHERE id = '{reversed.Id}'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, reactivate.SqlState);
        var backToPending = await Assert.ThrowsAsync<PostgresException>(() => Exec(connection,
            $"UPDATE billing.payments SET status = 'PendingVerification' WHERE id = '{payment.Id}'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, backToPending.SqlState);

        // Los pagos, sus asignaciones y el historial de membresías no se borran.
        var deletePayment = await Assert.ThrowsAsync<PostgresException>(() => Exec(connection, $"DELETE FROM billing.payments WHERE id = '{payment.Id}'"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, deletePayment.SqlState);
        var deleteAllocation = await Assert.ThrowsAsync<PostgresException>(() => Exec(connection, "DELETE FROM billing.payment_allocations"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, deleteAllocation.SqlState);
        var deleteEvents = await Assert.ThrowsAsync<PostgresException>(() => Exec(connection, "DELETE FROM members.membership_events"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, deleteEvents.SqlState);
        var updateEvents = await Assert.ThrowsAsync<PostgresException>(() => Exec(connection, "UPDATE members.membership_events SET type = 'Created'"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, updateEvents.SqlState);

        // Reglas de integridad del propio pago.
        var zero = await Assert.ThrowsAsync<PostgresException>(() => Exec(connection, $"""
            INSERT INTO billing.payments (id, tenant_id, branch_id, member_id, receipt_number, method, amount, status, paid_at, created_at, confirmed_at)
            VALUES (gen_random_uuid(), '{a.Tenant.Id}', '{a.Branch}', '{a.Member.Id}', 'RC-X1', 'Other', 0, 'Confirmed', now(), now(), now())
            """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, zero.SqlState);
        var noReference = await Assert.ThrowsAsync<PostgresException>(() => Exec(connection, $"""
            INSERT INTO billing.payments (id, tenant_id, branch_id, member_id, receipt_number, method, amount, status, paid_at, created_at, confirmed_at)
            VALUES (gen_random_uuid(), '{a.Tenant.Id}', '{a.Branch}', '{a.Member.Id}', 'RC-X2', 'Card', 5, 'Confirmed', now(), now(), now())
            """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, noReference.SqlState);

        // Un pago de A no puede apuntar a una obligación de B (clave compuesta).
        var crossTenant = await Assert.ThrowsAsync<PostgresException>(() => Exec(connection, $"""
            INSERT INTO billing.payment_allocations (id, tenant_id, payment_id, obligation_id, amount)
            VALUES (gen_random_uuid(), '{a.Tenant.Id}', '{payment.Id}', '{b.Membership.Obligation!.Id}', 1)
            """));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, crossTenant.SqlState);
    }

    private static async Task<int> Exec(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> Scalar(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
