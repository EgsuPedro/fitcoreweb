using System.Net;
using System.Net.Http.Json;
using FitCore.Api.IntegrationTests.Infrastructure;
using Npgsql;
using static FitCore.Api.IntegrationTests.Infrastructure.CommercialTestApi;

namespace FitCore.Api.IntegrationTests;

[Collection(ApiCollection2.Name)]
public class CashTests(ApiFixture api)
{
    private sealed record Setup(
        TestTenant Tenant,
        Guid Branch,
        MemberDto Member,
        MembershipDto Membership,
        CashRegisterDto Register,
        HttpClient Cashier,
        string CashierEmail);

    /// <summary>Gimnasio con una sucursal, un alumno con una obligación de 100.00, una caja y un cajero con su rol de plantilla.</summary>
    private async Task<Setup> NewSetupAsync(string code = "CJ", bool allowMultiple = false)
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var branch = await TestApi.CreateBranchAsync(tenant.Owner, code);
        var member = await MemberTests.CreateMemberAsync(tenant.Owner, branch);
        var plan = await CreatePlanAsync(tenant.Owner, "Trimestral", 100m);
        var enroll = await tenant.Owner.PostAsJsonAsync("/api/v1/memberships", new { memberId = member.Id, planId = plan.Id });
        Assert.Equal(HttpStatusCode.Created, enroll.StatusCode);
        var membership = (await enroll.Content.ReadFromJsonAsync<MembershipDto>())!;
        var register = await CreateRegisterAsync(tenant.Owner, branch, "Caja principal", allowMultiple);
        var email = $"cajero.{code.ToLowerInvariant()}@gym.test";
        await TestApi.CreateUserAsync(tenant.Owner, email, "Cajero", branch);
        var cashier = TestApi.Authenticated(api, await TestApi.LoginAsync(api, tenant.Slug, email, TestApi.UserPassword));
        return new Setup(tenant, branch, member, membership, register, cashier, email);
    }

    private static async Task<CashSessionDto> GetSessionAsync(HttpClient client, Guid registerId, Guid sessionId) =>
        (await client.GetFromJsonAsync<CashSessionDto>($"/api/v1/cash-registers/{registerId}/sessions/{sessionId}"))!;

    private static async Task<CashMovementDto[]> MovementsAsync(HttpClient client, Guid registerId, Guid? sessionId = null) =>
        (await client.GetFromJsonAsync<PagedDto<CashMovementDto>>($"/api/v1/cash-registers/{registerId}/movements?pageSize=100{(sessionId is null ? string.Empty : $"&sessionId={sessionId}")}"))!.Items;

    private static async Task<PaymentDto> PayCashAsync(HttpClient client, Guid memberId, decimal amount)
    {
        var response = await PostPaymentAsync(client, PaymentBody(memberId, amount, "Cash", null));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PaymentDto>())!;
    }

    private async Task SqlAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(api.AdminConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(api.AdminConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    // ---- Configuración de cajas ------------------------------------------------------------------------------------

    [Fact]
    public async Task Crear_una_caja_valida_su_sucursal_y_no_repite_el_nombre_en_la_sucursal()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var other = await TestApi.NewTenantAsync(api);
        var north = await TestApi.CreateBranchAsync(tenant.Owner, "CN");
        var south = await TestApi.CreateBranchAsync(tenant.Owner, "CS");
        var foreign = await TestApi.CreateBranchAsync(other.Owner, "CF");

        var register = await CreateRegisterAsync(tenant.Owner, north, "Caja 1");
        Assert.True(register.IsActive);
        Assert.False(register.AllowMultipleOpenSessions);
        Assert.Equal("CN", register.BranchCode);
        Assert.Empty(register.OpenSessions);

        var duplicate = await tenant.Owner.PostAsJsonAsync("/api/v1/cash-registers", new { branchId = north, name = "CAJA 1", allowMultipleOpenSessions = false });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("cash.register_name_taken", await TestApi.ProblemCodeAsync(duplicate));
        await CreateRegisterAsync(tenant.Owner, south, "Caja 1");
        await CreateRegisterAsync(other.Owner, foreign, "Caja 1");

        var foreignBranch = await tenant.Owner.PostAsJsonAsync("/api/v1/cash-registers", new { branchId = foreign, name = "Caja ajena", allowMultipleOpenSessions = false });
        Assert.Equal(HttpStatusCode.NotFound, foreignBranch.StatusCode);
        Assert.Equal("branches.not_found", await TestApi.ProblemCodeAsync(foreignBranch));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await tenant.Owner.PostAsJsonAsync("/api/v1/cash-registers", new { branchId = north, name = "", allowMultipleOpenSessions = false })).StatusCode);
    }

    [Fact]
    public async Task Los_permisos_y_el_alcance_por_sucursal_se_aplican_a_las_cajas()
    {
        var s = await NewSetupAsync("PE");
        var south = await TestApi.CreateBranchAsync(s.Tenant.Owner, "PEX");
        var southRegister = await CreateRegisterAsync(s.Tenant.Owner, south, "Caja sur");
        await TestApi.CreateUserAsync(s.Tenant.Owner, "entrenador@gym.test", "Entrenador", s.Branch);
        using var trainer = TestApi.Authenticated(api, await TestApi.LoginAsync(api, s.Tenant.Slug, "entrenador@gym.test", TestApi.UserPassword));

        // El cajero consulta y opera, pero no configura cajas.
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Cashier.PostAsJsonAsync("/api/v1/cash-registers", new { branchId = s.Branch, name = "Otra", allowMultipleOpenSessions = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Cashier.PutAsJsonAsync($"/api/v1/cash-registers/{s.Register.Id}", new { name = "X", isActive = true, allowMultipleOpenSessions = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await trainer.GetAsync("/api/v1/cash-registers")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().GetAsync("/api/v1/cash-registers")).StatusCode);

        var list = await s.Cashier.GetFromJsonAsync<PagedDto<CashRegisterDto>>("/api/v1/cash-registers");
        Assert.Equal([s.Register.Id], list!.Items.Select(r => r.Id));
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Cashier.GetAsync($"/api/v1/cash-registers?branchId={south}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Cashier.GetAsync($"/api/v1/cash-registers/{southRegister.Id}/movements")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await OpenCashAsync(s.Cashier, southRegister.Id)).StatusCode);
    }

    [Fact]
    public async Task Una_caja_con_sesion_abierta_no_se_desactiva_y_una_inactiva_no_se_abre()
    {
        var s = await NewSetupAsync("DE");
        await OpenOkAsync(s.Cashier, s.Register.Id);

        var blocked = await s.Tenant.Owner.PutAsJsonAsync($"/api/v1/cash-registers/{s.Register.Id}", new { name = "Caja principal", isActive = false, allowMultipleOpenSessions = false });
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Equal("cash.has_open_sessions", await TestApi.ProblemCodeAsync(blocked));

        await CloseCashAsync(s.Cashier, s.Register.Id, 50m);
        var off = await s.Tenant.Owner.PutAsJsonAsync($"/api/v1/cash-registers/{s.Register.Id}", new { name = "Caja principal", isActive = false, allowMultipleOpenSessions = false });
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        var open = await OpenCashAsync(s.Cashier, s.Register.Id);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, open.StatusCode);
        Assert.Equal("cash.register_inactive", await TestApi.ProblemCodeAsync(open));
    }

    // ---- Apertura --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Abrir_caja_registra_el_saldo_inicial_el_responsable_y_es_idempotente()
    {
        var s = await NewSetupAsync("AP");
        var key = Guid.NewGuid().ToString();

        var first = await OpenCashAsync(s.Cashier, s.Register.Id, 50m, key);
        var retry = await OpenCashAsync(s.Cashier, s.Register.Id, 50m, key);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var session = (await first.Content.ReadFromJsonAsync<CashSessionDto>())!;
        Assert.Equal("Open", session.Status);
        Assert.Equal(50m, session.OpeningAmount);
        Assert.Equal(50m, session.ExpectedAmount);
        Assert.Equal(0m, session.TotalIn);
        Assert.Contains("cajero", session.OpenedByName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(s.Register.Id, session.RegisterId);
        Assert.Equal(session.Id, (await retry.Content.ReadFromJsonAsync<CashSessionDto>())!.Id);
        Assert.Equal(1L, await ScalarAsync<long>($"SELECT count(*) FROM cash.cash_sessions WHERE tenant_id = '{s.Tenant.Id}'"));

        var registers = await s.Cashier.GetFromJsonAsync<PagedDto<CashRegisterDto>>("/api/v1/cash-registers");
        Assert.Equal([session.Id], registers!.Items.Single().OpenSessions.Select(x => x.Id));

        // Otra solicitud con la misma clave pero otro saldo se rechaza.
        var reused = await OpenCashAsync(s.Cashier, s.Register.Id, 999m, key);
        Assert.Equal("idempotency.key_reused", await TestApi.ProblemCodeAsync(reused));
    }

    [Fact]
    public async Task La_apertura_valida_el_saldo_inicial_y_exige_la_clave_de_idempotencia()
    {
        var s = await NewSetupAsync("VA");

        foreach (var opening in new[] { -1m, 100000.01m, 10.123m })
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await OpenCashAsync(s.Cashier, s.Register.Id, opening)).StatusCode);
        }

        using var noKey = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/cash-registers/{s.Register.Id}/open") { Content = JsonContent.Create(new { openingAmount = 10m }) };
        var response = await s.Cashier.SendAsync(noKey);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("idempotency.key_required", await TestApi.ProblemCodeAsync(response));
        Assert.Equal(0L, await ScalarAsync<long>($"SELECT count(*) FROM cash.cash_sessions WHERE tenant_id = '{s.Tenant.Id}'"));

        Assert.Equal(HttpStatusCode.OK, (await OpenCashAsync(s.Cashier, s.Register.Id, 0m)).StatusCode); // abrir en cero es válido
    }

    [Fact]
    public async Task Una_caja_exclusiva_admite_una_sola_sesion_y_cada_usuario_solo_una_abierta()
    {
        var s = await NewSetupAsync("EX");
        var second = await CreateRegisterAsync(s.Tenant.Owner, s.Branch, "Caja 2");
        await OpenOkAsync(s.Cashier, s.Register.Id);

        // Otro usuario no puede abrir la misma caja; el mismo usuario no puede abrir otra caja.
        var ownerOpen = await OpenCashAsync(s.Tenant.Owner, s.Register.Id);
        Assert.Equal(HttpStatusCode.Conflict, ownerOpen.StatusCode);
        Assert.Equal("cash.register_already_open", await TestApi.ProblemCodeAsync(ownerOpen));
        var cashierSecond = await OpenCashAsync(s.Cashier, second.Id);
        Assert.Equal(HttpStatusCode.Conflict, cashierSecond.StatusCode);
        Assert.Equal("cash.user_has_open_session", await TestApi.ProblemCodeAsync(cashierSecond));

        // Pero otro usuario sí abre otra caja.
        Assert.Equal(HttpStatusCode.OK, (await OpenCashAsync(s.Tenant.Owner, second.Id)).StatusCode);
    }

    [Fact]
    public async Task Una_caja_configurada_para_varias_sesiones_las_admite()
    {
        var s = await NewSetupAsync("MU", allowMultiple: true);

        Assert.Equal(HttpStatusCode.OK, (await OpenCashAsync(s.Cashier, s.Register.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await OpenCashAsync(s.Tenant.Owner, s.Register.Id)).StatusCode);

        var register = (await s.Tenant.Owner.GetFromJsonAsync<PagedDto<CashRegisterDto>>("/api/v1/cash-registers"))!.Items.Single();
        Assert.Equal(2, register.OpenSessions.Length);
    }

    [Fact]
    public async Task Dos_aperturas_simultaneas_de_una_caja_exclusiva_solo_dejan_una_sesion()
    {
        var s = await NewSetupAsync("CC");
        var clients = new List<HttpClient> { s.Cashier, s.Tenant.Owner };
        for (var i = 0; i < 4; i++)
        {
            var email = $"extra{i}@gym.test";
            await TestApi.CreateUserAsync(s.Tenant.Owner, email, "Cajero", s.Branch);
            clients.Add(TestApi.Authenticated(api, await TestApi.LoginAsync(api, s.Tenant.Slug, email, TestApi.UserPassword)));
        }

        var responses = await Task.WhenAll(clients.Select(c => OpenCashAsync(c, s.Register.Id)));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        foreach (var rejected in responses.Where(r => r.StatusCode != HttpStatusCode.OK))
        {
            Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
            Assert.Equal("cash.register_already_open", await TestApi.ProblemCodeAsync(rejected));
        }

        Assert.Equal(1L, await ScalarAsync<long>($"SELECT count(*) FROM cash.cash_sessions WHERE register_id = '{s.Register.Id}' AND status = 'Open'"));
    }

    [Fact]
    public async Task El_mismo_usuario_abriendo_dos_cajas_a_la_vez_solo_logra_una()
    {
        var s = await NewSetupAsync("UU");
        var second = await CreateRegisterAsync(s.Tenant.Owner, s.Branch, "Caja 2");

        var responses = await Task.WhenAll(OpenCashAsync(s.Cashier, s.Register.Id), OpenCashAsync(s.Cashier, second.Id));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        var rejected = responses.Single(r => r.StatusCode != HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.Equal("cash.user_has_open_session", await TestApi.ProblemCodeAsync(rejected));
    }

    // ---- Cobros en efectivo ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Un_cobro_en_efectivo_entra_a_la_sesion_abierta_del_cajero_y_cuadra_con_el_pago_y_la_obligacion()
    {
        var s = await NewSetupAsync("CO");
        var session = await OpenOkAsync(s.Cashier, s.Register.Id, 50m);

        var payment = await PayCashAsync(s.Cashier, s.Member.Id, 30m);

        Assert.Equal("Confirmed", payment.Status);
        Assert.Equal("Cash", payment.Method);
        Assert.Equal(session.Id, payment.CashSessionId);
        var after = await GetSessionAsync(s.Cashier, s.Register.Id, session.Id);
        Assert.Equal(30m, after.TotalIn);
        Assert.Equal(0m, after.TotalOut);
        Assert.Equal(80m, after.ExpectedAmount);
        Assert.Equal(1, after.MovementCount);

        var movement = Assert.Single(await MovementsAsync(s.Cashier, s.Register.Id, session.Id));
        Assert.Equal("CashPayment", movement.Type);
        Assert.Equal("In", movement.Direction);
        Assert.Equal(30m, movement.Amount);
        Assert.Equal(payment.Id, movement.PaymentId);
        Assert.Equal(payment.ReceiptNumber, movement.ReceiptNumber);
        Assert.Contains("cajero", movement.CreatedByName, StringComparison.OrdinalIgnoreCase);

        // El saldo de la caja y el saldo del alumno son cosas distintas.
        var obligation = (await s.Cashier.GetFromJsonAsync<ObligationDto>($"/api/v1/payment-obligations/{s.Membership.Obligation!.Id}"))!;
        Assert.Equal(30m, obligation.PaidAmount);
        Assert.Equal(70m, obligation.Balance);
        Assert.Equal(30m, await ScalarAsync<decimal>($"SELECT COALESCE(SUM(amount), 0) FROM cash.cash_movements WHERE session_id = '{session.Id}'"));
    }

    [Fact]
    public async Task Sin_una_caja_abierta_propia_en_la_sucursal_el_efectivo_se_rechaza_sin_dejar_rastro()
    {
        var s = await NewSetupAsync("SI");
        var otherBranch = await TestApi.CreateBranchAsync(s.Tenant.Owner, "SIO");
        var otherRegister = await CreateRegisterAsync(s.Tenant.Owner, otherBranch, "Caja otra");

        // Nadie ha abierto caja.
        var none = await PostPaymentAsync(s.Cashier, PaymentBody(s.Member.Id, 10m, "Cash", null));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, none.StatusCode);
        Assert.Equal("payments.cash_requires_open_cash_session", await TestApi.ProblemCodeAsync(none));

        // La caja abierta por otra persona no cuenta: cada cajero responde por la suya.
        await OpenOkAsync(s.Tenant.Owner, s.Register.Id);
        var notMine = await PostPaymentAsync(s.Cashier, PaymentBody(s.Member.Id, 10m, "Cash", null));
        Assert.Equal("payments.cash_requires_open_cash_session", await TestApi.ProblemCodeAsync(notMine));

        // Una caja abierta en otra sucursal tampoco sirve para el alumno de esta sucursal.
        await CloseCashAsync(s.Tenant.Owner, s.Register.Id, 50m);
        await OpenOkAsync(s.Tenant.Owner, otherRegister.Id);
        var wrongBranch = await PostPaymentAsync(s.Tenant.Owner, PaymentBody(s.Member.Id, 10m, "Cash", null));
        Assert.Equal("payments.cash_requires_open_cash_session", await TestApi.ProblemCodeAsync(wrongBranch));

        Assert.Equal(0L, await ScalarAsync<long>($"SELECT count(*) FROM billing.payments WHERE tenant_id = '{s.Tenant.Id}'"));
        Assert.Equal(0L, await ScalarAsync<long>($"SELECT count(*) FROM cash.cash_movements WHERE tenant_id = '{s.Tenant.Id}'"));
    }

    [Fact]
    public async Task Otros_medios_de_pago_no_tocan_la_caja()
    {
        var s = await NewSetupAsync("OM");
        var session = await OpenOkAsync(s.Cashier, s.Register.Id, 50m);

        await PayAsync(s.Cashier, s.Member.Id, 10m, "Card", "VCH-1");
        await PayAsync(s.Cashier, s.Member.Id, 10m, "BankTransfer", "TRX-1");

        var after = await GetSessionAsync(s.Cashier, s.Register.Id, session.Id);
        Assert.Equal(50m, after.ExpectedAmount);
        Assert.Equal(0, after.MovementCount);
    }

    // ---- Ingresos y egresos manuales -------------------------------------------------------------------------------

    [Fact]
    public async Task Los_ingresos_y_egresos_manuales_exigen_motivo_y_un_egreso_no_deja_la_caja_en_negativo()
    {
        var s = await NewSetupAsync("MA");
        var noSession = await CashMovementAsync(s.Cashier, s.Register.Id, "ManualIn", 10m);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noSession.StatusCode);
        Assert.Equal("cash.no_open_session", await TestApi.ProblemCodeAsync(noSession));

        var session = await OpenOkAsync(s.Cashier, s.Register.Id, 50m);

        Assert.Equal(HttpStatusCode.Created, (await CashMovementAsync(s.Cashier, s.Register.Id, "ManualIn", 25m, "Fondo adicional de la gerencia")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await CashMovementAsync(s.Cashier, s.Register.Id, "ManualOut", 15.50m, "Compra de insumos de limpieza")).StatusCode);
        var after = await GetSessionAsync(s.Cashier, s.Register.Id, session.Id);
        Assert.Equal(25m, after.TotalIn);
        Assert.Equal(15.50m, after.TotalOut);
        Assert.Equal(59.50m, after.ExpectedAmount);

        var tooMuch = await CashMovementAsync(s.Cashier, s.Register.Id, "ManualOut", 59.51m, "Retiro excesivo de prueba");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooMuch.StatusCode);
        Assert.Equal("cash.insufficient_funds", await TestApi.ProblemCodeAsync(tooMuch));
        Assert.Equal(HttpStatusCode.Created, (await CashMovementAsync(s.Cashier, s.Register.Id, "ManualOut", 59.50m, "Retiro de todo el efectivo")).StatusCode);
        Assert.Equal(0m, (await GetSessionAsync(s.Cashier, s.Register.Id, session.Id)).ExpectedAmount);

        // Validación del movimiento.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await CashMovementAsync(s.Cashier, s.Register.Id, "ManualIn", 10m, "")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await CashMovementAsync(s.Cashier, s.Register.Id, "ManualIn", 10m, "no")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await CashMovementAsync(s.Cashier, s.Register.Id, "ManualIn", 0m)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await CashMovementAsync(s.Cashier, s.Register.Id, "ManualIn", 1.234m)).StatusCode);
        // Los cobros y reversos de efectivo los genera el sistema: no se pueden inventar a mano.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await CashMovementAsync(s.Cashier, s.Register.Id, "CashPayment", 10m)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await CashMovementAsync(s.Cashier, s.Register.Id, "PaymentReversal", 10m)).StatusCode);

        var movements = await MovementsAsync(s.Cashier, s.Register.Id, session.Id);
        Assert.Equal(3, movements.Length);
        Assert.Contains(movements, m => m.Reason == "Compra de insumos de limpieza" && m.Direction == "Out");
    }

    [Fact]
    public async Task Repetir_un_movimiento_manual_con_la_misma_clave_no_lo_duplica()
    {
        var s = await NewSetupAsync("MI");
        var session = await OpenOkAsync(s.Cashier, s.Register.Id, 0m);
        var key = Guid.NewGuid().ToString();

        var first = await CashMovementAsync(s.Cashier, s.Register.Id, "ManualIn", 20m, "Fondo para cambio", key);
        var retry = await CashMovementAsync(s.Cashier, s.Register.Id, "ManualIn", 20m, "Fondo para cambio", key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal((await first.Content.ReadFromJsonAsync<CashMovementDto>())!.Id, (await retry.Content.ReadFromJsonAsync<CashMovementDto>())!.Id);
        Assert.Equal(20m, (await GetSessionAsync(s.Cashier, s.Register.Id, session.Id)).ExpectedAmount);
    }

    // ---- Cierre y arqueo -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Cerrar_con_el_saldo_exacto_no_deja_diferencia_y_el_esperado_siempre_lo_calcula_el_sistema()
    {
        var s = await NewSetupAsync("CE");
        var session = await OpenOkAsync(s.Cashier, s.Register.Id, 50m);
        await PayCashAsync(s.Cashier, s.Member.Id, 30m);

        // El cliente intenta imponer su propio "esperado": se ignora.
        var response = await PostWithKeyAsync(s.Cashier, $"/api/v1/cash-registers/{s.Register.Id}/close",
            new { countedAmount = 80m, notes = (string?)null, expectedAmount = 999m, difference = 0m });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var closed = (await response.Content.ReadFromJsonAsync<CashSessionDto>())!;
        Assert.Equal("Closed", closed.Status);
        Assert.Equal(80m, closed.ExpectedAmount);
        Assert.Equal(80m, closed.CountedAmount);
        Assert.Equal(0m, closed.Difference);
        Assert.NotNull(closed.ClosedAt);
        Assert.Equal(session.Id, closed.Id);
        Assert.Contains("cajero", closed.ClosedByName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Una_diferencia_en_el_arqueo_exige_observacion_y_queda_registrada_como_faltante_o_sobrante()
    {
        var shortage = await NewSetupAsync("FA");
        await OpenOkAsync(shortage.Cashier, shortage.Register.Id, 50m);
        await PayCashAsync(shortage.Cashier, shortage.Member.Id, 30m);

        var withoutNotes = await CloseCashAsync(shortage.Cashier, shortage.Register.Id, 75m);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, withoutNotes.StatusCode);
        Assert.Equal("cash.difference_requires_notes", await TestApi.ProblemCodeAsync(withoutNotes));
        Assert.Equal("Open", (await shortage.Cashier.GetFromJsonAsync<PagedDto<CashRegisterDto>>("/api/v1/cash-registers"))!.Items.Single().OpenSessions.Single().Status);

        var closed = (await (await CloseCashAsync(shortage.Cashier, shortage.Register.Id, 75m, "Faltaron 5 dólares, se revisará la cámara")).Content.ReadFromJsonAsync<CashSessionDto>())!;
        Assert.Equal(-5m, closed.Difference);
        Assert.Equal("Faltaron 5 dólares, se revisará la cámara", closed.CloseNotes);

        var surplus = await NewSetupAsync("SO");
        await OpenOkAsync(surplus.Cashier, surplus.Register.Id, 10m);
        var over = (await (await CloseCashAsync(surplus.Cashier, surplus.Register.Id, 12.50m, "Sobrante sin explicación aparente")).Content.ReadFromJsonAsync<CashSessionDto>())!;
        Assert.Equal(2.50m, over.Difference);
    }

    [Fact]
    public async Task Cerrar_valida_el_importe_la_sesion_y_quien_puede_cerrarla()
    {
        var s = await NewSetupAsync("CL");
        var noSession = await CloseCashAsync(s.Cashier, s.Register.Id, 0m);
        Assert.Equal("cash.no_open_session", await TestApi.ProblemCodeAsync(noSession));

        var session = await OpenOkAsync(s.Cashier, s.Register.Id, 50m);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await CloseCashAsync(s.Cashier, s.Register.Id, -1m)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await CloseCashAsync(s.Cashier, s.Register.Id, 1.234m)).StatusCode);

        // Otro cajero (sin permiso de supervisor) no puede cerrar la sesión ajena aunque conozca su identificador.
        await TestApi.CreateUserAsync(s.Tenant.Owner, "otro.cajero@gym.test", "Cajero", s.Branch);
        using var other = TestApi.Authenticated(api, await TestApi.LoginAsync(api, s.Tenant.Slug, "otro.cajero@gym.test", TestApi.UserPassword));
        var denied = await CloseCashAsync(other, s.Register.Id, 50m, sessionId: session.Id);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal("cash.close_not_allowed", await TestApi.ProblemCodeAsync(denied));

        // Un supervisor (permiso cash.manage) sí puede.
        var byOwner = await CloseCashAsync(s.Tenant.Owner, s.Register.Id, 50m, sessionId: session.Id);
        Assert.Equal(HttpStatusCode.OK, byOwner.StatusCode);

        // Ya cerrada: no se cierra dos veces ni acepta más dinero.
        var again = await CloseCashAsync(s.Tenant.Owner, s.Register.Id, 50m, sessionId: session.Id);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("cash.session_closed", await TestApi.ProblemCodeAsync(again));
        Assert.Equal("cash.no_open_session", await TestApi.ProblemCodeAsync(await CashMovementAsync(s.Cashier, s.Register.Id, "ManualIn", 5m)));
        Assert.Equal("payments.cash_requires_open_cash_session", await TestApi.ProblemCodeAsync(await PostPaymentAsync(s.Cashier, PaymentBody(s.Member.Id, 5m, "Cash", null))));
    }

    [Fact]
    public async Task Los_cierres_anteriores_se_consultan_con_sus_diferencias_y_filtros()
    {
        var s = await NewSetupAsync("HI");
        var first = await OpenOkAsync(s.Cashier, s.Register.Id, 20m);
        await CloseCashAsync(s.Cashier, s.Register.Id, 20m);
        var second = await OpenOkAsync(s.Cashier, s.Register.Id, 30m);
        await CloseCashAsync(s.Cashier, s.Register.Id, 28m, "Faltante de cambio");
        var third = await OpenOkAsync(s.Cashier, s.Register.Id, 40m);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        async Task<CashSessionDto[]> ListAsync(string query) =>
            (await s.Cashier.GetFromJsonAsync<PagedDto<CashSessionDto>>($"/api/v1/cash-registers/{s.Register.Id}/sessions?{query}"))!.Items;

        Assert.Equal([third.Id, second.Id, first.Id], (await ListAsync(string.Empty)).Select(x => x.Id));
        Assert.Equal([second.Id, first.Id], (await ListAsync("status=Closed")).Select(x => x.Id));
        Assert.Equal([third.Id], (await ListAsync("status=Open")).Select(x => x.Id));
        Assert.Equal(-2m, (await ListAsync("status=Closed")).Single(x => x.Id == second.Id).Difference);
        Assert.Equal(3, (await ListAsync($"from={today.AddDays(-1):yyyy-MM-dd}&to={today.AddDays(1):yyyy-MM-dd}")).Length);
        Assert.Empty(await ListAsync($"to={today.AddDays(-3):yyyy-MM-dd}"));
        Assert.Equal(HttpStatusCode.NotFound, (await s.Cashier.GetAsync($"/api/v1/cash-registers/{s.Register.Id}/sessions/{Guid.NewGuid()}")).StatusCode);
    }

    // ---- Reversos de efectivo --------------------------------------------------------------------------------------

    [Fact]
    public async Task Revertir_un_cobro_en_efectivo_con_la_caja_original_abierta_genera_el_egreso_en_esa_caja()
    {
        var s = await NewSetupAsync("RA");
        var session = await OpenOkAsync(s.Cashier, s.Register.Id, 50m);
        var payment = await PayCashAsync(s.Cashier, s.Member.Id, 30m);
        Assert.Equal(80m, (await GetSessionAsync(s.Cashier, s.Register.Id, session.Id)).ExpectedAmount);

        // Revierte el propietario, que no tiene caja abierta: el efectivo nunca salió del cajón del cajero.
        var reverse = await PostWithKeyAsync(s.Tenant.Owner, $"/api/v1/payments/{payment.Id}/reverse", new { reason = "Cobro duplicado" });

        Assert.Equal(HttpStatusCode.OK, reverse.StatusCode);
        var after = await GetSessionAsync(s.Cashier, s.Register.Id, session.Id);
        Assert.Equal(30m, after.TotalIn);
        Assert.Equal(30m, after.TotalOut);
        Assert.Equal(50m, after.ExpectedAmount);
        var movements = await MovementsAsync(s.Cashier, s.Register.Id, session.Id);
        var reversal = movements.Single(m => m.Type == "PaymentReversal");
        Assert.Equal("Out", reversal.Direction);
        Assert.Equal(payment.Id, reversal.PaymentId);
        Assert.Contains(payment.ReceiptNumber, reversal.Reason, StringComparison.Ordinal);
        Assert.Contains("Cobro duplicado", reversal.Reason, StringComparison.Ordinal);
        Assert.Equal(100m, (await s.Cashier.GetFromJsonAsync<ObligationDto>($"/api/v1/payment-obligations/{s.Membership.Obligation!.Id}"))!.Balance);
    }

    [Fact]
    public async Task Revertir_un_cobro_de_una_caja_ya_cerrada_sale_de_la_caja_abierta_de_quien_revierte()
    {
        var s = await NewSetupAsync("RC");
        var otherRegister = await CreateRegisterAsync(s.Tenant.Owner, s.Branch, "Caja 2");
        await OpenOkAsync(s.Cashier, s.Register.Id, 0m);
        var payment = await PayCashAsync(s.Cashier, s.Member.Id, 30m);
        var closed = (await (await CloseCashAsync(s.Cashier, s.Register.Id, 30m)).Content.ReadFromJsonAsync<CashSessionDto>())!;
        var url = $"/api/v1/payments/{payment.Id}/reverse";

        // Sin una caja propia abierta no se puede devolver efectivo.
        var noSession = await PostWithKeyAsync(s.Tenant.Owner, url, new { reason = "Devolución al alumno" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noSession.StatusCode);
        Assert.Equal("payments.reversal_requires_open_cash_session", await TestApi.ProblemCodeAsync(noSession));
        Assert.Equal("Confirmed", (await s.Tenant.Owner.GetFromJsonAsync<PaymentDto>($"/api/v1/payments/{payment.Id}"))!.Status);

        // Con una caja abierta pero sin efectivo suficiente, tampoco.
        var current = await OpenOkAsync(s.Tenant.Owner, otherRegister.Id, 10m);
        var insufficient = await PostWithKeyAsync(s.Tenant.Owner, url, new { reason = "Devolución al alumno" });
        Assert.Equal("cash.insufficient_funds", await TestApi.ProblemCodeAsync(insufficient));

        // Con efectivo suficiente, el egreso queda en la caja de quien devuelve; la sesión cerrada no cambia.
        await CashMovementAsync(s.Tenant.Owner, otherRegister.Id, "ManualIn", 50m, "Fondo para devoluciones");
        var ok = await PostWithKeyAsync(s.Tenant.Owner, url, new { reason = "Devolución al alumno" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var currentAfter = await GetSessionAsync(s.Tenant.Owner, otherRegister.Id, current.Id);
        Assert.Equal(30m, currentAfter.TotalOut);
        Assert.Equal(30m, currentAfter.ExpectedAmount); // 10 + 50 - 30
        var closedAfter = await GetSessionAsync(s.Tenant.Owner, s.Register.Id, closed.Id);
        Assert.Equal(30m, closedAfter.ExpectedAmount);
        Assert.Equal(0m, closedAfter.TotalOut);
    }

    // ---- Concurrencia ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Varios_cobros_en_efectivo_simultaneos_suman_exactamente_en_la_caja()
    {
        var s = await NewSetupAsync("PA");
        var session = await OpenOkAsync(s.Cashier, s.Register.Id, 10m);

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => PostPaymentAsync(s.Cashier, PaymentBody(s.Member.Id, 3m, "Cash", null))));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        var after = await GetSessionAsync(s.Cashier, s.Register.Id, session.Id);
        Assert.Equal(30m, after.TotalIn);
        Assert.Equal(40m, after.ExpectedAmount);
        Assert.Equal(10, after.MovementCount);
        Assert.Equal(30m, (await s.Cashier.GetFromJsonAsync<ObligationDto>($"/api/v1/payment-obligations/{s.Membership.Obligation!.Id}"))!.PaidAmount);
    }

    [Fact]
    public async Task Cerrar_la_caja_mientras_entran_cobros_nunca_deja_dinero_fuera_del_arqueo()
    {
        var s = await NewSetupAsync("RR");
        var session = await OpenOkAsync(s.Cashier, s.Register.Id, 0m);

        var payments = Enumerable.Range(0, 12).Select(_ => PostPaymentAsync(s.Cashier, PaymentBody(s.Member.Id, 2m, "Cash", null))).ToList();
        var close = CloseCashAsync(s.Tenant.Owner, s.Register.Id, 0m, "Cierre durante cobros", session.Id);
        var results = await Task.WhenAll(payments);
        var closeResponse = await close;

        // Sea cual sea el orden en que llegaron, lo que cuenta es la coherencia final.
        Assert.Contains(closeResponse.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.UnprocessableEntity });
        var accepted = results.Count(r => r.StatusCode == HttpStatusCode.Created);
        Assert.All(results.Where(r => r.StatusCode != HttpStatusCode.Created), r => Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode));
        var inSession = await ScalarAsync<long>($"SELECT count(*) FROM cash.cash_movements WHERE session_id = '{session.Id}'");
        Assert.Equal(accepted, inSession);

        if (closeResponse.StatusCode == HttpStatusCode.OK)
        {
            // El esperado guardado al cerrar es exactamente lo registrado en esa sesión, y nada entró después.
            var expectedAtClose = await ScalarAsync<decimal>($"SELECT expected_at_close FROM cash.cash_sessions WHERE id = '{session.Id}'");
            var movementsTotal = await ScalarAsync<decimal>($"SELECT COALESCE(SUM(amount), 0) FROM cash.cash_movements WHERE session_id = '{session.Id}'");
            Assert.Equal(movementsTotal, expectedAtClose);
            Assert.Equal(0L, await ScalarAsync<long>($"""
                SELECT count(*) FROM cash.cash_movements m JOIN cash.cash_sessions c ON c.id = m.session_id
                WHERE c.id = '{session.Id}' AND m.created_at > c.closed_at
                """));
        }
    }

    // ---- Aislamiento -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Otro_gimnasio_no_ve_ni_opera_cajas_sesiones_ni_movimientos_ajenos()
    {
        var a = await NewSetupAsync("IA");
        var b = await NewSetupAsync("IB");
        var sessionB = await OpenOkAsync(b.Cashier, b.Register.Id, 20m);
        await PayCashAsync(b.Cashier, b.Member.Id, 10m);

        Assert.Equal(HttpStatusCode.NotFound, (await a.Tenant.Owner.GetAsync($"/api/v1/cash-registers/{b.Register.Id}/movements")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.Tenant.Owner.GetAsync($"/api/v1/cash-registers/{b.Register.Id}/sessions")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.Tenant.Owner.GetAsync($"/api/v1/cash-registers/{b.Register.Id}/sessions/{sessionB.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await OpenCashAsync(a.Tenant.Owner, b.Register.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await CloseCashAsync(a.Tenant.Owner, b.Register.Id, 0m, sessionId: sessionB.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await CashMovementAsync(a.Tenant.Owner, b.Register.Id, "ManualIn", 5m)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.Tenant.Owner.PutAsJsonAsync($"/api/v1/cash-registers/{b.Register.Id}", new { name = "Hack", isActive = false, allowMultipleOpenSessions = true })).StatusCode);

        var registers = await a.Tenant.Owner.GetFromJsonAsync<PagedDto<CashRegisterDto>>("/api/v1/cash-registers");
        Assert.Equal([a.Register.Id], registers!.Items.Select(r => r.Id));
        Assert.Equal("Open", (await GetSessionAsync(b.Cashier, b.Register.Id, sessionB.Id)).Status);
    }

    [Fact]
    public async Task La_base_protege_las_cajas_aunque_el_codigo_falle()
    {
        var a = await NewSetupAsync("DA");
        var b = await NewSetupAsync("DB");
        var sessionA = await OpenOkAsync(a.Cashier, a.Register.Id, 50m);
        var paymentA = await PayCashAsync(a.Cashier, a.Member.Id, 30m);
        var sessionB = await OpenOkAsync(b.Cashier, b.Register.Id, 20m);
        var closedSession = await OpenOkAsync(a.Tenant.Owner, (await CreateRegisterAsync(a.Tenant.Owner, a.Branch, "Caja cerrada")).Id, 5m);
        await CloseCashAsync(a.Tenant.Owner, closedSession.RegisterId, 5m);

        await using var connection = new NpgsqlConnection(api.AppConnection);
        await connection.OpenAsync();
        await Exec(connection, $"SELECT set_config('app.tenant_id', '{a.Tenant.Id}', false)");

        // RLS: solo lo del gimnasio activo.
        Assert.Equal(0L, await Scalar(connection, $"SELECT count(*) FROM cash.cash_sessions WHERE id = '{sessionB.Id}'"));
        Assert.Equal(0, await Exec(connection, $"UPDATE cash.cash_registers SET name = 'x' WHERE id = '{b.Register.Id}'"));

        // Dos sesiones abiertas de la misma caja exclusiva, o de un mismo usuario: lo impide un índice único.
        var duplicateRegister = await Assert.ThrowsAsync<PostgresException>(() => Exec(connection, $"""
            INSERT INTO cash.cash_sessions (id, tenant_id, branch_id, register_id, "exclusive", opened_by, opened_at, opening_amount, status)
            VALUES (gen_random_uuid(), '{a.Tenant.Id}', '{a.Branch}', '{a.Register.Id}', true, gen_random_uuid(), now(), 1, 'Open')
            """));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicateRegister.SqlState);

        // Un movimiento no puede ir a una sesión cerrada, ni tener una dirección contraria a su tipo, ni importe no positivo.
        var intoClosed = await Assert.ThrowsAsync<PostgresException>(() => Exec(connection, $"""
            INSERT INTO cash.cash_movements (id, tenant_id, branch_id, session_id, type, direction, amount, reason, created_at)
            VALUES (gen_random_uuid(), '{a.Tenant.Id}', '{a.Branch}', '{closedSession.Id}', 'ManualIn', 'In', 5, 'Intento en sesión cerrada', now())
            """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, intoClosed.SqlState);
        var wrongDirection = await Assert.ThrowsAsync<PostgresException>(() => Exec(connection, $"""
            INSERT INTO cash.cash_movements (id, tenant_id, branch_id, session_id, type, direction, amount, reason, created_at)
            VALUES (gen_random_uuid(), '{a.Tenant.Id}', '{a.Branch}', '{sessionA.Id}', 'ManualIn', 'Out', 5, 'Dirección contraria', now())
            """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, wrongDirection.SqlState);
        var zero = await Assert.ThrowsAsync<PostgresException>(() => Exec(connection, $"""
            INSERT INTO cash.cash_movements (id, tenant_id, branch_id, session_id, type, direction, amount, reason, created_at)
            VALUES (gen_random_uuid(), '{a.Tenant.Id}', '{a.Branch}', '{sessionA.Id}', 'ManualIn', 'In', 0, 'Importe cero', now())
            """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, zero.SqlState);
        var noReason = await Assert.ThrowsAsync<PostgresException>(() => Exec(connection, $"""
            INSERT INTO cash.cash_movements (id, tenant_id, branch_id, session_id, type, direction, amount, created_at)
            VALUES (gen_random_uuid(), '{a.Tenant.Id}', '{a.Branch}', '{sessionA.Id}', 'ManualOut', 'Out', 5, now())
            """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, noReason.SqlState);

        // Los movimientos no se modifican ni se borran; las sesiones tampoco se borran, y una cerrada no se reabre.
        var updateMovement = await Assert.ThrowsAsync<PostgresException>(() => Exec(connection, "UPDATE cash.cash_movements SET amount = 1"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, updateMovement.SqlState);
        var deleteMovement = await Assert.ThrowsAsync<PostgresException>(() => Exec(connection, "DELETE FROM cash.cash_movements"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, deleteMovement.SqlState);
        var deleteSession = await Assert.ThrowsAsync<PostgresException>(() => Exec(connection, "DELETE FROM cash.cash_sessions"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, deleteSession.SqlState);
        var reopen = await Assert.ThrowsAsync<PostgresException>(() => Exec(connection,
            $"UPDATE cash.cash_sessions SET status = 'Open', closed_at = NULL, closed_by = NULL, counted_amount = NULL, expected_at_close = NULL, difference = NULL WHERE id = '{closedSession.Id}'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, reopen.SqlState);
        var changeOpening = await Assert.ThrowsAsync<PostgresException>(() => Exec(connection, $"UPDATE cash.cash_sessions SET opening_amount = 9999 WHERE id = '{sessionA.Id}'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, changeOpening.SqlState);

        // Un pago en efectivo siempre lleva su sesión (y solo ellos), y ese dato no se puede cambiar.
        var cashWithoutSession = await Assert.ThrowsAsync<PostgresException>(() => Exec(connection, $"""
            INSERT INTO billing.payments (id, tenant_id, branch_id, member_id, receipt_number, method, amount, status, paid_at, created_at, confirmed_at)
            VALUES (gen_random_uuid(), '{a.Tenant.Id}', '{a.Branch}', '{a.Member.Id}', 'RC-Z1', 'Cash', 5, 'Confirmed', now(), now(), now())
            """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, cashWithoutSession.SqlState);
        var moveSession = await Assert.ThrowsAsync<PostgresException>(() => Exec(connection,
            $"UPDATE billing.payments SET cash_session_id = '{closedSession.Id}' WHERE id = '{paymentA.Id}'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, moveSession.SqlState);

        // Un cobro de A no puede apuntar a la sesión de B (clave compuesta).
        var crossTenant = await Assert.ThrowsAsync<PostgresException>(() => Exec(connection, $"""
            INSERT INTO cash.cash_movements (id, tenant_id, branch_id, session_id, type, direction, amount, reason, created_at)
            VALUES (gen_random_uuid(), '{a.Tenant.Id}', '{a.Branch}', '{sessionB.Id}', 'ManualIn', 'In', 5, 'Sesión de otro gimnasio', now())
            """));
        Assert.True(crossTenant.SqlState is PostgresErrorCodes.ForeignKeyViolation or PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task Las_aperturas_cierres_y_cajas_quedan_auditados()
    {
        var s = await NewSetupAsync("AU");
        var session = await OpenOkAsync(s.Cashier, s.Register.Id, 10m);
        await CloseCashAsync(s.Cashier, s.Register.Id, 10m);

        var audit = (await s.Tenant.Owner.GetFromJsonAsync<PagedDto<AuditDto>>("/api/v1/audit-logs?pageSize=100"))!;

        Assert.Contains(audit.Items, l => l.Action == "CashRegister.Created" && l.EntityId == s.Register.Id.ToString());
        Assert.Contains(audit.Items, l => l.Action == "CashSession.Created" && l.EntityId == session.Id.ToString());
        Assert.Contains(audit.Items, l => l.Action == "CashSession.Updated" && l.EntityId == session.Id.ToString());
    }

    // ---- Cartera ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task La_cartera_resume_lo_que_se_debe_por_antiguedad_sucursal_y_deudor()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var north = await TestApi.CreateBranchAsync(tenant.Owner, "KN");
        var south = await TestApi.CreateBranchAsync(tenant.Owner, "KS");
        var plan = await CreatePlanAsync(tenant.Owner, "Mensual", 100m);
        var ana = await MemberTests.CreateMemberAsync(tenant.Owner, north, first: "Ana", last: "Aguirre", phone: "0991111111");
        var beto = await MemberTests.CreateMemberAsync(tenant.Owner, north, first: "Beto", last: "Bravo", phone: "0992222222");
        var cleo = await MemberTests.CreateMemberAsync(tenant.Owner, south, first: "Cleo", last: "Cruz", phone: "0993333333");
        var dani = await MemberTests.CreateMemberAsync(tenant.Owner, south, first: "Dani", last: "Diaz");

        async Task<MembershipDto> EnrollAsync(MemberDto m)
        {
            var r = await tenant.Owner.PostAsJsonAsync("/api/v1/memberships", new { memberId = m.Id, planId = plan.Id });
            Assert.Equal(HttpStatusCode.Created, r.StatusCode);
            return (await r.Content.ReadFromJsonAsync<MembershipDto>())!;
        }

        var anaMembership = await EnrollAsync(ana);   // vence en 7 días: al día
        var betoMembership = await EnrollAsync(beto); // se vencerá hace 20 días, con un abono de 40
        var cleoMembership = await EnrollAsync(cleo); // se vencerá hace 70 días
        var daniMembership = await EnrollAsync(dani); // se pagará completa y se anulará otra
        await PayAsync(tenant.Owner, beto.Id, 40m);
        await PayAsync(tenant.Owner, dani.Id, 100m);
        var voided = (await (await tenant.Owner.PostAsJsonAsync("/api/v1/payment-obligations", new { memberId = dani.Id, concept = "Camiseta", amount = 20m, taxKind = "General" })).Content.ReadFromJsonAsync<ObligationDto>())!;
        await tenant.Owner.PostAsJsonAsync($"/api/v1/payment-obligations/{voided.Id}/void", new { reason = "Se anuló la venta" });
        await PayAsync(tenant.Owner, ana.Id, 20m, "BankTransfer", "TRX-PENDIENTE"); // pendiente: sigue debiendo
        await SqlAsync($"UPDATE billing.payment_obligations SET issue_date = issue_date - 40, due_date = due_date - 27 WHERE id = '{betoMembership.Obligation!.Id}'");
        await SqlAsync($"UPDATE billing.payment_obligations SET issue_date = issue_date - 90, due_date = due_date - 77 WHERE id = '{cleoMembership.Obligation!.Id}'");

        var summary = (await tenant.Owner.GetFromJsonAsync<ReceivablesSummaryDto>("/api/v1/receivables/summary"))!;

        Assert.Equal("USD", summary.Currency);
        Assert.Equal(260m, summary.Outstanding); // ana 100 + beto 60 + cleo 100
        Assert.Equal(160m, summary.Overdue);     // beto 60 + cleo 100
        Assert.Equal(3, summary.OpenCount);
        Assert.Equal(2, summary.OverdueCount);
        Assert.Equal(100m, summary.Aging.Single(b => b.Label == "Al día").Amount);
        Assert.Equal(60m, summary.Aging.Single(b => b.Label == "1-30 días").Amount);
        Assert.Equal(100m, summary.Aging.Single(b => b.Label == "61-90 días").Amount);
        Assert.Equal(0m, summary.Aging.Single(b => b.Label == "Más de 90 días").Amount);
        Assert.Equal(summary.Outstanding, summary.Aging.Sum(b => b.Amount));

        Assert.Equal(160m, summary.ByBranch.Single(b => b.BranchCode == "KN").Outstanding);
        Assert.Equal(60m, summary.ByBranch.Single(b => b.BranchCode == "KN").Overdue);
        Assert.Equal(100m, summary.ByBranch.Single(b => b.BranchCode == "KS").Outstanding);

        // Los deudores van de mayor a menor deuda vencida, con su teléfono para cobrar; el que pagó todo no aparece.
        Assert.Equal(["Cleo Cruz", "Beto Bravo", "Ana Aguirre"], summary.TopDebtors.Select(d => d.MemberName));
        Assert.Equal("0993333333", summary.TopDebtors[0].Phone);
        Assert.Equal(100m, summary.TopDebtors[0].Overdue);
        Assert.DoesNotContain(summary.TopDebtors, d => d.MemberName == "Dani Diaz");
        Assert.Equal(100m, summary.TopDebtors.Single(d => d.MemberName == "Ana Aguirre").Outstanding);

        var northOnly = (await tenant.Owner.GetFromJsonAsync<ReceivablesSummaryDto>($"/api/v1/receivables/summary?branchId={north}"))!;
        Assert.Equal(160m, northOnly.Outstanding);
        Assert.Equal(2, northOnly.OpenCount);
        Assert.Equal(1, (await tenant.Owner.GetFromJsonAsync<ReceivablesSummaryDto>("/api/v1/receivables/summary?top=1"))!.TopDebtors.Length);
        Assert.NotNull(anaMembership);
        Assert.NotNull(daniMembership);
    }

    [Fact]
    public async Task La_cartera_respeta_sucursal_gimnasio_y_permisos()
    {
        var a = await NewSetupAsync("KA");
        var b = await NewSetupAsync("KB");
        var south = await TestApi.CreateBranchAsync(a.Tenant.Owner, "KAS");
        await TestApi.CreateUserAsync(a.Tenant.Owner, "entrenador@gym.test", "Entrenador", a.Branch);
        using var trainer = TestApi.Authenticated(api, await TestApi.LoginAsync(api, a.Tenant.Slug, "entrenador@gym.test", TestApi.UserPassword));

        // El cajero solo ve la cartera de su sucursal, y nunca la de otro gimnasio.
        var summary = (await a.Cashier.GetFromJsonAsync<ReceivablesSummaryDto>("/api/v1/receivables/summary"))!;
        Assert.Equal(100m, summary.Outstanding);
        Assert.Single(summary.TopDebtors);
        Assert.DoesNotContain(summary.TopDebtors, d => d.MemberId == b.Member.Id);
        Assert.Equal(HttpStatusCode.Forbidden, (await a.Cashier.GetAsync($"/api/v1/receivables/summary?branchId={south}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await trainer.GetAsync("/api/v1/receivables/summary")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().GetAsync("/api/v1/receivables/summary")).StatusCode);
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
