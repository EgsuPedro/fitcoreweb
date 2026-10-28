using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FitCore.Api.IntegrationTests.Infrastructure;
using Npgsql;
using static FitCore.Api.IntegrationTests.Infrastructure.CommercialTestApi;

namespace FitCore.Api.IntegrationTests;

/// <summary>Reportes financieros (bloque 4): cada cifra contra datos reales en PostgreSQL, con sus reglas de fechas, reversos y alcance.</summary>
[Collection(ApiCollection2.Name)]
public class AnalyticsFinancialReportTests(ApiFixture api)
{
    private static readonly TimeSpan Ecuador = TimeSpan.FromHours(-5);

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.Add(Ecuador));

    private static string Last(int days) => $"preset=Custom&from={Today.AddDays(-(days - 1)):yyyy-MM-dd}&to={Today:yyyy-MM-dd}";

    private sealed record Gym(TestTenant Tenant, Guid Uio, Guid Gye, PlanDto Mensual, PlanDto Trimestral);

    private async Task<Gym> NewGymAsync()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var uio = await TestApi.CreateBranchAsync(tenant.Owner, "UIO");
        var gye = await TestApi.CreateBranchAsync(tenant.Owner, "GYE");
        var mensual = await CreatePlanAsync(tenant.Owner, "Mensual", 30m);
        var trimestral = await CreatePlanAsync(tenant.Owner, "Trimestral", 80m, durationValue: 3);
        return new Gym(tenant, uio, gye, mensual, trimestral);
    }

    private async Task<JsonElement> GetAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync($"/api/v1/reports/financial/{url}");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Respondió {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static JsonElement Kpi(JsonElement report, string key) => report.GetProperty("summary").EnumerateArray().Single(k => k.GetProperty("key").GetString() == key);

    private static decimal Num(JsonElement element, string property) => element.GetProperty(property).GetDecimal();

    private async Task SqlAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(api.AdminConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static int _members;

    private static async Task<MemberDto> NewMemberAsync(HttpClient owner, Guid branch)
    {
        var n = Interlocked.Increment(ref _members);
        var response = await owner.PostAsJsonAsync("/api/v1/members", new
        {
            branchId = branch, firstName = $"Socio{n}", lastName = "Reporte", idType = "Cedula", idNumber = MemberTests.NewCedula(), birthDate = "1992-03-04",
            email = $"s{n}.{Guid.NewGuid():N}"[..24] + "@alumnos.test", phone = "0991234567"
        });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<MemberDto>())!;
    }

    private static async Task<MembershipDto> EnrollAsync(HttpClient owner, MemberDto member, PlanDto plan)
    {
        var response = await owner.PostAsJsonAsync("/api/v1/memberships", new { memberId = member.Id, planId = plan.Id });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<MembershipDto>())!;
    }

    private static async Task<ObligationDto> ExtraAsync(HttpClient owner, MemberDto member, decimal amount)
    {
        var response = await owner.PostAsJsonAsync("/api/v1/payment-obligations", new { memberId = member.Id, concept = "Camiseta", amount, taxKind = "General" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ObligationDto>())!;
    }

    private async Task<HttpClient> StaffAsync(Gym gym, string email, string role, params Guid[] branches)
    {
        await TestApi.CreateUserAsync(gym.Tenant.Owner, email, role, branches);
        return TestApi.Authenticated(api, await TestApi.LoginAsync(api, gym.Tenant.Slug, email, TestApi.UserPassword));
    }

    // ---- Cobros --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Los_cobros_se_reparten_por_sucursal_y_por_plan_sin_contar_nada_dos_veces_y_distinguen_los_abonos()
    {
        var gym = await NewGymAsync();
        var owner = gym.Tenant.Owner;

        // A: plan mensual pagado completo (30). B: trimestral con un abono de 20 de 80. C: mensual (30) + camiseta (20) en un solo pago de 50 repartido.
        // D (Guayaquil): mensual pagado (30). E: pago revertido (no cuenta).
        var a = await NewMemberAsync(owner, gym.Uio);
        await EnrollAsync(owner, a, gym.Mensual);
        await PayAsync(owner, a.Id, 30m, "Card", "V-A");
        var b = await NewMemberAsync(owner, gym.Uio);
        await EnrollAsync(owner, b, gym.Trimestral);
        await PayAsync(owner, b.Id, 20m, "Card", "V-B");
        var c = await NewMemberAsync(owner, gym.Uio);
        var membershipC = await EnrollAsync(owner, c, gym.Mensual);
        var shirt = await ExtraAsync(owner, c, 20m);
        await PayAsync(owner, c.Id, 50m, "BankTransfer", "T-C", allocations: [new { obligationId = membershipC.Obligation!.Id, amount = 30m }, new { obligationId = shirt.Id, amount = 20m }]);
        var d = await NewMemberAsync(owner, gym.Gye);
        await EnrollAsync(owner, d, gym.Mensual);
        await PayAsync(owner, d.Id, 30m, "Card", "V-D");
        var e = await NewMemberAsync(owner, gym.Uio);
        await EnrollAsync(owner, e, gym.Mensual);
        var reversed = await PayAsync(owner, e.Id, 30m, "Card", "V-E");
        await PostWithKeyAsync(owner, $"/api/v1/payments/{reversed.Id}/reverse", new { reason = "Pago duplicado" });

        // La transferencia de C todavía no está verificada: se informa aparte y no es cobrado.
        var report = await GetAsync(owner, $"collections?{Last(10)}");
        Assert.Equal(80m, Num(Kpi(report, "collected"), "value")); // 30 + 20 + 30 (la transferencia y lo revertido quedan fuera)
        Assert.Equal(50m, Num(Kpi(report, "pending_verification"), "value"));
        Assert.Equal(3m, Num(Kpi(report, "payments_count"), "value"));

        // Abonos: el pago de 20 sobre un trimestral de 80 es parcial.
        Assert.Equal(20m, Num(Kpi(report, "partial_amount"), "value"));
        Assert.Equal(1m, Num(Kpi(report, "partial_count"), "value"));

        // Por sucursal y por plan suman exactamente el total cobrado.
        Assert.Equal(80m, report.GetProperty("byBranch").EnumerateArray().Sum(x => Num(x, "amount")));
        Assert.Equal(80m, report.GetProperty("byPlan").EnumerateArray().Sum(x => Num(x, "amount")));
        var byBranch = report.GetProperty("byBranch").EnumerateArray().ToDictionary(x => x.GetProperty("branchName").GetString()!, x => Num(x, "amount"));
        Assert.Equal(50m, byBranch["Sucursal UIO"]);
        Assert.Equal(30m, byBranch["Sucursal GYE"]);
        var byPlan = report.GetProperty("byPlan").EnumerateArray().ToDictionary(x => x.GetProperty("planName").GetString()!, x => Num(x, "amount"));
        Assert.Equal(60m, byPlan["Mensual"]);
        Assert.Equal(20m, byPlan["Trimestral"]);

        // El detalle por defecto solo trae lo confirmado; el histórico de revertidos se pide a propósito.
        var rows = report.GetProperty("payments").GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Equal("Confirmed", r.GetProperty("status").GetString()));
        Assert.Equal(1, rows.Count(r => r.GetProperty("isPartial").GetBoolean()));
        var history = await GetAsync(owner, $"collections?{Last(10)}&status=Reversed");
        var reversedRow = history.GetProperty("payments").GetProperty("items").EnumerateArray().Single();
        Assert.Equal(reversed.ReceiptNumber, reversedRow.GetProperty("receiptNumber").GetString());
        Assert.Equal("Reversed", reversedRow.GetProperty("status").GetString());
        Assert.Equal(80m, Num(Kpi(history, "collected"), "value")); // el resumen sigue siendo lo vigente
        Assert.Equal(5, (await GetAsync(owner, $"collections?{Last(10)}&status=All")).GetProperty("payments").GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task Los_filtros_de_plan_medio_y_sucursal_acotan_el_reporte_y_el_total_es_la_parte_asignada_al_plan()
    {
        var gym = await NewGymAsync();
        var owner = gym.Tenant.Owner;
        var c = await NewMemberAsync(owner, gym.Uio);
        var membership = await EnrollAsync(owner, c, gym.Mensual);
        var shirt = await ExtraAsync(owner, c, 20m);
        await PayAsync(owner, c.Id, 50m, "Card", "V-C", allocations: [new { obligationId = membership.Obligation!.Id, amount = 30m }, new { obligationId = shirt.Id, amount = 20m }]);
        var d = await NewMemberAsync(owner, gym.Gye);
        await EnrollAsync(owner, d, gym.Trimestral);
        await PayAsync(owner, d.Id, 25m, "Card", "V-D");

        // Plan mensual: de los 50 del pago de C solo 30 pertenecen al plan; el resto (la camiseta) no.
        var plan = await GetAsync(owner, $"collections?{Last(10)}&planId={gym.Mensual.Id}");
        Assert.Equal(30m, Num(Kpi(plan, "collected"), "value"));
        Assert.Equal(1, plan.GetProperty("payments").GetProperty("totalCount").GetInt32());

        var cards = await GetAsync(owner, $"collections?{Last(10)}&method=Card");
        Assert.Equal(75m, Num(Kpi(cards, "collected"), "value"));
        var cash = await GetAsync(owner, $"collections?{Last(10)}&method=Cash");
        Assert.Equal(0m, Num(Kpi(cash, "collected"), "value"));
        Assert.True(Kpi(cash, "collected").GetProperty("isEmpty").GetBoolean());

        var gye = await GetAsync(owner, $"collections?{Last(10)}&branchId={gym.Gye}");
        Assert.Equal(25m, Num(Kpi(gye, "collected"), "value"));

        var invalid = await owner.GetAsync($"/api/v1/reports/financial/collections?{Last(10)}&method=Bitcoin");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        Assert.Equal("analytics.method_invalid", await TestApi.ProblemCodeAsync(invalid));
    }

    [Fact]
    public async Task Los_importes_decimales_conservan_la_precision_y_los_pagos_se_ordenan_del_mas_reciente()
    {
        var gym = await NewGymAsync();
        var owner = gym.Tenant.Owner;
        var member = await NewMemberAsync(owner, gym.Uio);
        var extra = await ExtraAsync(owner, member, 10m);
        foreach (var amount in new[] { 0.10m, 0.20m, 0.33m })
        {
            await PayAsync(owner, member.Id, amount, "Card", $"V-{amount}", allocations: [new { obligationId = extra.Id, amount }]);
        }

        var report = await GetAsync(owner, $"collections?{Last(10)}");
        Assert.Equal(0.63m, Num(Kpi(report, "collected"), "value"));
        Assert.Equal(0.21m, Num(Kpi(report, "average_payment"), "value"));
        Assert.Equal(0.63m, report.GetProperty("byPlan").EnumerateArray().Single().GetProperty("amount").GetDecimal());
        Assert.Equal("Cobros sin plan", report.GetProperty("byPlan").EnumerateArray().Single().GetProperty("planName").GetString());
        var times = report.GetProperty("payments").GetProperty("items").EnumerateArray().Select(r => r.GetProperty("paidAt").GetDateTimeOffset()).ToList();
        Assert.Equal(times.OrderByDescending(t => t), times);
    }

    // ---- Medios de pago --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Los_medios_de_pago_incluyen_siempre_los_cuatro_y_comparan_con_el_periodo_anterior()
    {
        var gym = await NewGymAsync();
        var owner = gym.Tenant.Owner;
        var member = await NewMemberAsync(owner, gym.Uio);
        var extra = await ExtraAsync(owner, member, 100m);

        // Hace 15 días: 10 en tarjeta (período anterior de «últimos 10 días»). Hoy: 40 en tarjeta y 20 de «Otro» con su detalle.
        await PostPaymentAsync(owner, PaymentBody(member.Id, 10m, "Card", "V-1", allocations: [new { obligationId = extra.Id, amount = 10m }], paidAt: DateTimeOffset.UtcNow.AddDays(-15)));
        await PayAsync(owner, member.Id, 40m, "Card", "V-2", allocations: [new { obligationId = extra.Id, amount = 40m }]);
        Assert.Equal(HttpStatusCode.Created, (await PostPaymentAsync(owner, PaymentBody(member.Id, 20m, "Other", "L-1", allocations: [new { obligationId = extra.Id, amount = 20m }], methodDetail: "Enlace de pago"))).StatusCode);

        var report = await GetAsync(owner, $"payment-methods?{Last(10)}");
        Assert.Equal(60m, Num(report, "total"));
        Assert.Equal(10m, Num(report, "previousTotal"));
        var methods = report.GetProperty("methods").EnumerateArray().ToDictionary(m => m.GetProperty("method").GetString()!);
        Assert.Equal(["Cash", "BankTransfer", "Card", "Other"], report.GetProperty("methods").EnumerateArray().Select(m => m.GetProperty("method").GetString()));
        Assert.Equal(40m, Num(methods["Card"], "amount"));
        Assert.Equal(300.0m, Num(methods["Card"].GetProperty("variation"), "percent")); // 10 → 40
        Assert.Equal(0m, Num(methods["Cash"], "amount"));
        Assert.Equal(66.7m, Num(methods["Card"], "share"));
        Assert.Equal(20m, Num(methods["Other"], "amount"));
        Assert.Equal("NoBaseline", methods["Other"].GetProperty("variation").GetProperty("state").GetString());
        Assert.Equal(["Enlace de pago"], report.GetProperty("otherDetails").EnumerateArray().Select(d => d.GetString()));
    }

    // ---- Saldos pendientes --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Los_saldos_pendientes_se_ven_por_alumno_y_por_obligacion_con_su_antiguedad_y_sin_datos_de_contacto()
    {
        var gym = await NewGymAsync();
        var owner = gym.Tenant.Owner;

        // A debe 30 (vence en el futuro). B pagó 10 de 30: debe 20 y su obligación venció hace 45 días. C pagó todo. D debe 30 vencidos hace 5 días.
        var a = await NewMemberAsync(owner, gym.Uio);
        await EnrollAsync(owner, a, gym.Mensual);
        var b = await NewMemberAsync(owner, gym.Uio);
        var membershipB = await EnrollAsync(owner, b, gym.Mensual);
        await PayAsync(owner, b.Id, 10m, "Card", "V-B");
        var c = await NewMemberAsync(owner, gym.Uio);
        await EnrollAsync(owner, c, gym.Mensual);
        await PayAsync(owner, c.Id, 30m, "Card", "V-C");
        var d = await NewMemberAsync(owner, gym.Gye);
        var membershipD = await EnrollAsync(owner, d, gym.Trimestral);
        await SqlAsync($"UPDATE billing.payment_obligations SET issue_date = current_date - 60, due_date = current_date - 45 WHERE id = '{membershipB.Obligation!.Id}'");
        await SqlAsync($"UPDATE billing.payment_obligations SET issue_date = current_date - 20, due_date = current_date - 5 WHERE id = '{membershipD.Obligation!.Id}'");

        var report = await GetAsync(owner, "outstanding-balances");
        Assert.Equal(130m, Num(report, "outstanding")); // 30 + 20 + 80
        Assert.Equal(100m, Num(report, "overdue")); // 20 + 80
        Assert.Equal(3, report.GetProperty("openCount").GetInt32());
        Assert.Equal(2, report.GetProperty("overdueCount").GetInt32());

        var aging = report.GetProperty("aging").EnumerateArray().ToDictionary(x => x.GetProperty("label").GetString()!, x => Num(x, "amount"));
        Assert.Equal(30m, aging["Al día"]);
        Assert.Equal(80m, aging["1-30 días"]);
        Assert.Equal(20m, aging["31-60 días"]);
        Assert.Equal(0m, aging["Más de 90 días"]);

        // Misma regla que la cartera que ya existía: las dos vistas deben dar exactamente lo mismo.
        var legacy = (await owner.GetFromJsonAsync<ReceivablesSummaryDto>("/api/v1/receivables/summary"))!;
        Assert.Equal(legacy.Outstanding, Num(report, "outstanding"));
        Assert.Equal(legacy.Overdue, Num(report, "overdue"));
        Assert.Equal(legacy.OpenCount, report.GetProperty("openCount").GetInt32());
        Assert.Equal(legacy.OverdueCount, report.GetProperty("overdueCount").GetInt32());
        Assert.Equal(legacy.Aging.Select(x => x.Amount), report.GetProperty("aging").EnumerateArray().Select(x => Num(x, "amount")));

        // Por alumno: el que más debe vencido primero; solo código y nombre, jamás teléfono, correo ni documento.
        var members = report.GetProperty("members").GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(3, members.Count);
        Assert.Equal(80m, Num(members[0], "overdue"));
        Assert.Equal(20m, Num(members[1], "overdue"));
        Assert.Equal(0m, Num(members[2], "overdue"));
        var raw = report.GetRawText();
        Assert.DoesNotContain("phone", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("email", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("idNumber", raw, StringComparison.OrdinalIgnoreCase);

        // Solo vencidos, y la vista por obligación con el cobrado y los días de atraso.
        var overdueOnly = await GetAsync(owner, "outstanding-balances?overdueOnly=true&view=Obligations");
        Assert.Equal(100m, Num(overdueOnly, "outstanding"));
        var obligations = overdueOnly.GetProperty("obligations").GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, obligations.Count);
        var onB = obligations.Single(o => Num(o, "balance") == 20m);
        Assert.Equal(10m, Num(onB, "paid"));
        Assert.Equal(45, onB.GetProperty("daysOverdue").GetInt32());
        Assert.Equal("Overdue", onB.GetProperty("status").GetString());
        Assert.Equal("Mensual", onB.GetProperty("planName").GetString());
        Assert.Equal(JsonValueKind.Null, overdueOnly.GetProperty("members").ValueKind);

        // Por plan y por sucursal.
        Assert.Equal(80m, Num(await GetAsync(owner, $"outstanding-balances?planId={gym.Trimestral.Id}"), "outstanding"));
        Assert.Equal(80m, Num(await GetAsync(owner, $"outstanding-balances?branchId={gym.Gye}"), "outstanding"));
    }

    [Fact]
    public async Task Los_saldos_pendientes_se_paginan_y_respetan_la_sucursal_del_usuario()
    {
        var gym = await NewGymAsync();
        var owner = gym.Tenant.Owner;
        for (var i = 0; i < 3; i++)
        {
            await EnrollAsync(owner, await NewMemberAsync(owner, gym.Uio), gym.Mensual);
        }

        await EnrollAsync(owner, await NewMemberAsync(owner, gym.Gye), gym.Mensual);

        var pageOne = await GetAsync(owner, "outstanding-balances?pageSize=2&page=1");
        Assert.Equal(2, pageOne.GetProperty("members").GetProperty("items").GetArrayLength());
        Assert.Equal(4, pageOne.GetProperty("members").GetProperty("totalCount").GetInt32());
        Assert.Equal(2, (await GetAsync(owner, "outstanding-balances?pageSize=2&page=2")).GetProperty("members").GetProperty("items").GetArrayLength());

        var manager = await StaffAsync(gym, "gerente@gym.test", "AdministradorSucursal", gym.Uio);
        var scoped = await GetAsync(manager, "outstanding-balances");
        Assert.Equal(90m, Num(scoped, "outstanding"));
        Assert.Equal(1, scoped.GetProperty("byBranch").GetArrayLength());
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync($"/api/v1/reports/financial/outstanding-balances?branchId={gym.Gye}")).StatusCode);
    }

    // ---- Caja ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task La_caja_informa_aperturas_cierres_y_diferencias_tal_como_se_guardaron()
    {
        var gym = await NewGymAsync();
        var owner = gym.Tenant.Owner;
        var register = await CreateRegisterAsync(owner, gym.Uio);

        // Turno 1: abre con 50, cobra 30 en efectivo y cierra contando 75 (esperado 80): faltan 5.
        await OpenOkAsync(owner, register.Id, 50m);
        var member = await NewMemberAsync(owner, gym.Uio);
        await EnrollAsync(owner, member, gym.Mensual);
        await PayAsync(owner, member.Id, 30m, "Cash", null);
        Assert.Equal(HttpStatusCode.OK, (await CloseCashAsync(owner, register.Id, 75m, "Falta un billete")).StatusCode);

        // Turno 2: abre con 20 y cierra contando 25 (esperado 20): sobran 5. Turno 3: abre y queda abierto.
        await OpenOkAsync(owner, register.Id, 20m);
        Assert.Equal(HttpStatusCode.OK, (await CloseCashAsync(owner, register.Id, 25m, "Sobraron 5 dólares")).StatusCode);
        await OpenOkAsync(owner, register.Id, 10m);

        var report = await GetAsync(owner, $"cash-registers?{Last(10)}");
        Assert.Equal(3, report.GetProperty("sessionsOpened").GetInt32());
        Assert.Equal(1, report.GetProperty("sessionsStillOpen").GetInt32());
        Assert.Equal(2, report.GetProperty("sessionsClosed").GetInt32());
        Assert.Equal(5m, Num(report, "shortage"));
        Assert.Equal(5m, Num(report, "surplus"));
        Assert.Equal(2, report.GetProperty("sessionsWithDifference").GetInt32());

        var sessions = report.GetProperty("sessions").GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(3, sessions.Count);
        var firstClosed = sessions.Single(s => s.GetProperty("openingAmount").GetDecimal() == 50m);
        Assert.Equal(80m, Num(firstClosed, "expected"));
        Assert.Equal(75m, Num(firstClosed, "counted"));
        Assert.Equal(-5m, Num(firstClosed, "difference"));
        Assert.Equal("Closed", firstClosed.GetProperty("status").GetString());
        var open = sessions.Single(s => s.GetProperty("status").GetString() == "Open");
        Assert.Equal(JsonValueKind.Null, open.GetProperty("expected").ValueKind);
        Assert.Equal(JsonValueKind.Null, open.GetProperty("difference").ValueKind);

        // Movimientos del período: el cobro en efectivo.
        var payment = report.GetProperty("movements").EnumerateArray().Single(m => m.GetProperty("type").GetString() == "CashPayment");
        Assert.Equal(30m, Num(payment, "amount"));
        Assert.Equal("In", payment.GetProperty("direction").GetString());

        // Solo con diferencia.
        Assert.Equal(2, (await GetAsync(owner, $"cash-registers?{Last(10)}&onlyWithDifference=true")).GetProperty("sessions").GetProperty("totalCount").GetInt32());
    }

    // ---- Reversiones --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Las_reversiones_y_anulaciones_se_informan_por_su_propia_fecha_con_su_motivo_y_responsable()
    {
        var gym = await NewGymAsync();
        var owner = gym.Tenant.Owner;
        var member = await NewMemberAsync(owner, gym.Uio);
        await EnrollAsync(owner, member, gym.Mensual);
        var payment = await PayAsync(owner, member.Id, 30m, "Card", "V-R");
        Assert.Equal(HttpStatusCode.OK, (await PostWithKeyAsync(owner, $"/api/v1/payments/{payment.Id}/reverse", new { reason = "Cobro duplicado en el datáfono" })).StatusCode);
        var shirt = await ExtraAsync(owner, member, 20m);
        Assert.True((await owner.PostAsJsonAsync($"/api/v1/payment-obligations/{shirt.Id}/void", new { reason = "Se anuló la venta" })).IsSuccessStatusCode);

        var report = await GetAsync(owner, $"reversals?{Last(10)}");
        Assert.Equal(30m, Num(report, "reversedAmount"));
        Assert.Equal(1, report.GetProperty("reversedCount").GetInt32());
        Assert.Equal(20m, Num(report, "voidedAmount"));
        var row = report.GetProperty("reversals").GetProperty("items").EnumerateArray().Single();
        Assert.Equal(payment.ReceiptNumber, row.GetProperty("receiptNumber").GetString());
        Assert.Equal("Cobro duplicado en el datáfono", row.GetProperty("reason").GetString());
        Assert.Equal("Dueño de Prueba", row.GetProperty("reversedBy").GetString());
        Assert.True(row.GetProperty("reversedAt").GetDateTimeOffset() >= row.GetProperty("paidAt").GetDateTimeOffset());
        var voided = report.GetProperty("voidedObligations").GetProperty("items").EnumerateArray().Single();
        Assert.Equal("Se anuló la venta", voided.GetProperty("reason").GetString());
        Assert.Equal(20m, Num(voided, "payable"));

        // Un período donde nada se revirtió está vacío (no falla).
        var empty = await GetAsync(owner, "reversals?preset=Custom&from=2020-01-01&to=2020-01-31");
        Assert.Equal(0m, Num(empty, "reversedAmount"));
        Assert.Equal(0, empty.GetProperty("reversals").GetProperty("totalCount").GetInt32());
    }

    // ---- Seguridad -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Solo_quien_tiene_el_permiso_financiero_ve_los_reportes_y_cada_uno_solo_lo_de_su_alcance()
    {
        var a = await NewGymAsync();
        var b = await NewGymAsync();
        var memberA = await NewMemberAsync(a.Tenant.Owner, a.Uio);
        await EnrollAsync(a.Tenant.Owner, memberA, a.Mensual);
        await PayAsync(a.Tenant.Owner, memberA.Id, 30m, "Card", "V-A");
        var memberB = await NewMemberAsync(b.Tenant.Owner, b.Uio);
        await EnrollAsync(b.Tenant.Owner, memberB, b.Mensual);
        await PayAsync(b.Tenant.Owner, memberB.Id, 30m, "Card", "V-B");

        string[] reports = ["collections", "payment-methods", "outstanding-balances", "cash-registers", "reversals"];

        // Recepción y entrenador no tienen el permiso; sin sesión, 401.
        var reception = await StaffAsync(a, "recep@gym.test", "Recepcion", a.Uio);
        var trainer = await StaffAsync(a, "entrenador@gym.test", "Entrenador", a.Uio);
        foreach (var report in reports)
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await reception.GetAsync($"/api/v1/reports/financial/{report}")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await trainer.GetAsync($"/api/v1/reports/financial/{report}")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().GetAsync($"/api/v1/reports/financial/{report}")).StatusCode);
        }

        // El cajero ve lo financiero de su sucursal; el otro gimnasio no existe para él.
        var cashier = await StaffAsync(a, "cajero@gym.test", "Cajero", a.Uio);
        Assert.Equal(30m, Num(Kpi(await GetAsync(cashier, $"collections?{Last(10)}"), "collected"), "value"));
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync($"/api/v1/reports/financial/collections?{Last(10)}&branchId={a.Gye}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await a.Tenant.Owner.GetAsync($"/api/v1/reports/financial/collections?{Last(10)}&branchId={b.Uio}")).StatusCode);
        Assert.Equal(30m, Num(Kpi(await GetAsync(a.Tenant.Owner, $"collections?{Last(10)}"), "collected"), "value"));

        // Período inválido o planId de otro gimnasio: sin errores del servidor ni datos ajenos.
        var invalid = await a.Tenant.Owner.GetAsync("/api/v1/reports/financial/collections?preset=Custom&from=2026-03-10&to=2026-03-01");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        Assert.Equal(0m, Num(Kpi(await GetAsync(a.Tenant.Owner, $"collections?{Last(10)}&planId={b.Mensual.Id}"), "collected"), "value"));
    }
}
