using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FitCore.Api.IntegrationTests.Infrastructure;
using Npgsql;
using static FitCore.Api.IntegrationTests.Infrastructure.CommercialTestApi;

namespace FitCore.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
public class AttendanceTests(ApiFixture api)
{
    private sealed record Setup(
        TestTenant Tenant,
        Guid Branch,
        MemberDto Member,
        PlanDto Plan,
        MembershipDto Membership,
        HttpClient Reception);

    private static DateOnly Today =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/Guayaquil")).DateTime);

    /// <summary>Gimnasio con una sucursal, un plan de 30.00 y un alumno inscrito; con <c>paid</c> la membresía queda pagada y activa.</summary>
    private async Task<Setup> NewSetupAsync(string code = "AS", bool paid = true, bool allowFreeze = false, Guid[]? planBranches = null)
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var branch = await TestApi.CreateBranchAsync(tenant.Owner, code);
        var member = await MemberTests.CreateMemberAsync(tenant.Owner, branch);
        var planResponse = await tenant.Owner.PostAsJsonAsync("/api/v1/membership-plans",
            PlanBody("Mensual", 30m, allowFreeze: allowFreeze, maxFreezeDays: allowFreeze ? 15 : 0, branchIds: planBranches));
        Assert.Equal(HttpStatusCode.Created, planResponse.StatusCode);
        var plan = (await planResponse.Content.ReadFromJsonAsync<PlanDto>())!;
        var membership = await EnrollAsync(tenant.Owner, member.Id, plan.Id);
        if (paid)
        {
            await PayAsync(tenant.Owner, member.Id, 30m);
            membership = await GetMembershipAsync(tenant.Owner, membership.Id);
        }

        var email = $"recepcion.{code.ToLowerInvariant()}@gym.test";
        await TestApi.CreateUserAsync(tenant.Owner, email, "Recepcion", branch);
        var reception = TestApi.Authenticated(api, await TestApi.LoginAsync(api, tenant.Slug, email, TestApi.UserPassword));
        return new Setup(tenant, branch, member, plan, membership, reception);
    }

    private static async Task<MembershipDto> EnrollAsync(HttpClient client, Guid memberId, Guid planId, DateOnly? start = null)
    {
        var response = await client.PostAsJsonAsync("/api/v1/memberships", new { memberId, planId, startDate = start });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<MembershipDto>())!;
    }

    private static async Task<MembershipDto> GetMembershipAsync(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<MembershipDto>($"/api/v1/memberships/{id}"))!;

    private static async Task SetPolicyAsync(HttpClient owner, string policy, int minPercent = 50) =>
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/v1/commercial-settings", new { accessPolicy = policy, minPaymentPercent = minPercent, dueDays = 7, maxDiscountPercent = 30 })).StatusCode);

    private static async Task<AttendanceDto> CheckInOkAsync(HttpClient client, Guid branchId, Guid? memberId = null, string? code = null, string? qr = null)
    {
        var response = await CheckInAsync(client, branchId, memberId, code, qr);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AttendanceDto>())!;
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

    private static async Task AssertDeniedAsync(HttpResponseMessage response, string code)
    {
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(code, await TestApi.ProblemCodeAsync(response));
    }

    // ---- Entrada y salida ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Una_entrada_manual_valida_se_registra_con_la_hora_del_servidor_y_la_membresia()
    {
        var s = await NewSetupAsync();
        var before = DateTimeOffset.UtcNow;

        // El cliente intenta fijar su propia hora: se ignora.
        var response = await s.Reception.PostAsJsonAsync("/api/v1/attendance/check-in",
            new { branchId = s.Branch, memberId = s.Member.Id, checkInAt = DateTimeOffset.UtcNow.AddDays(-3) });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var entry = (await response.Content.ReadFromJsonAsync<AttendanceDto>())!;
        Assert.Equal("Manual", entry.Method);
        Assert.True(entry.IsOpen);
        Assert.Null(entry.CheckOutAt);
        Assert.InRange(entry.CheckInAt, before.AddSeconds(-5), DateTimeOffset.UtcNow.AddSeconds(5));
        Assert.Equal(s.Membership.Id, entry.MembershipId);
        Assert.Equal("Mensual", entry.PlanName);
        Assert.False(entry.IsException);
        Assert.Equal(s.Member.Code, entry.MemberCode);
        Assert.Contains("recepcion", entry.RegisteredByName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(entry.Id, (await s.Reception.GetFromJsonAsync<AttendanceDto>($"/api/v1/attendance/{entry.Id}"))!.Id);
    }

    [Fact]
    public async Task La_salida_cierra_la_entrada_abierta_y_luego_el_alumno_puede_volver_a_entrar()
    {
        var s = await NewSetupAsync();
        var entry = await CheckInOkAsync(s.Reception, s.Branch, s.Member.Id);

        var response = await CheckOutAsync(s.Reception, memberCode: s.Member.Code);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var closed = (await response.Content.ReadFromJsonAsync<AttendanceDto>())!;
        Assert.Equal(entry.Id, closed.Id);
        Assert.False(closed.IsOpen);
        Assert.NotNull(closed.CheckOutAt);
        Assert.True(closed.CheckOutAt >= closed.CheckInAt);
        Assert.Equal(0, closed.DurationMinutes);
        Assert.Equal("Manual", closed.CheckOutMethod);
        Assert.NotNull(closed.CheckOutBy);

        var again = await CheckOutAsync(s.Reception, s.Member.Id);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, again.StatusCode);
        Assert.Equal("attendance.no_open_entry", await TestApi.ProblemCodeAsync(again));
        var second = await CheckInOkAsync(s.Reception, s.Branch, code: s.Member.Code);
        Assert.NotEqual(entry.Id, second.Id);
    }

    [Fact]
    public async Task Hay_que_indicar_al_alumno_por_una_sola_via()
    {
        var s = await NewSetupAsync();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await CheckInAsync(s.Reception, s.Branch)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await CheckInAsync(s.Reception, s.Branch, s.Member.Id, s.Member.Code)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await CheckOutAsync(s.Reception)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await ValidateAccessAsync(s.Reception, s.Branch)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await CheckInAsync(s.Reception, Guid.Empty, s.Member.Id)).StatusCode);
    }

    // ---- Código QR -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task El_QR_es_un_token_opaco_sin_datos_del_alumno_y_en_la_base_solo_queda_su_huella()
    {
        var s = await NewSetupAsync();

        var response = await s.Reception.PostAsync($"/api/v1/members/{s.Member.Id}/qr-token", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var qr = (await response.Content.ReadFromJsonAsync<QrTokenDto>())!;
        Assert.StartsWith("fc1_", qr.Token, StringComparison.Ordinal);
        Assert.Equal(47, qr.Token.Length);
        // Nada del alumno ni del gimnasio viaja en el código.
        foreach (var secret in new[] { s.Member.Id.ToString(), s.Member.Id.ToString("N"), s.Member.Code, s.Tenant.Id.ToString(), s.Tenant.Id.ToString("N"), s.Tenant.Slug, s.Member.IdNumber })
        {
            Assert.DoesNotContain(secret, qr.Token, StringComparison.OrdinalIgnoreCase);
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(qr.Token))).ToLowerInvariant();
        Assert.Equal(hash, await ScalarAsync<string>($"SELECT qr_token_hash FROM members.members WHERE id = '{s.Member.Id}'"));
        Assert.Equal(0L, await ScalarAsync<long>($"SELECT count(*) FROM members.members WHERE qr_token_hash = '{qr.Token}'"));

        // La huella tampoco se registra en la auditoría.
        Assert.Equal(0L, await ScalarAsync<long>($"SELECT count(*) FROM audit.audit_logs WHERE tenant_id = '{s.Tenant.Id}' AND (after::text LIKE '%{hash}%' OR before::text LIKE '%{hash}%')"));
        Assert.True(await ScalarAsync<long>($"SELECT count(*) FROM audit.audit_logs WHERE tenant_id = '{s.Tenant.Id}' AND after::text LIKE '%QrTokenHash%redacted%'") >= 1);

        using var member = JsonDocument.Parse(await s.Reception.GetStringAsync($"/api/v1/members/{s.Member.Id}"));
        Assert.True(member.RootElement.GetProperty("hasQrToken").GetBoolean());
        Assert.DoesNotContain("qrTokenHash", member.RootElement.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Entrar_y_salir_con_QR_funciona_y_un_QR_reemplazado_o_ajeno_no()
    {
        var a = await NewSetupAsync("QA");
        var b = await NewSetupAsync("QB");
        var qrA = (await (await a.Reception.PostAsync($"/api/v1/members/{a.Member.Id}/qr-token", null)).Content.ReadFromJsonAsync<QrTokenDto>())!;
        var qrB = (await (await b.Reception.PostAsync($"/api/v1/members/{b.Member.Id}/qr-token", null)).Content.ReadFromJsonAsync<QrTokenDto>())!;

        var entry = await CheckInOkAsync(a.Reception, a.Branch, qr: qrA.Token);
        Assert.Equal("Qr", entry.Method);
        var closed = (await (await CheckOutAsync(a.Reception, qrToken: qrA.Token)).Content.ReadFromJsonAsync<AttendanceDto>())!;
        Assert.Equal("Qr", closed.CheckOutMethod);

        // Emitir otro QR invalida el anterior.
        var renewed = (await (await a.Reception.PostAsync($"/api/v1/members/{a.Member.Id}/qr-token", null)).Content.ReadFromJsonAsync<QrTokenDto>())!;
        Assert.NotEqual(qrA.Token, renewed.Token);
        var old = await CheckInAsync(a.Reception, a.Branch, qrToken: qrA.Token);
        Assert.Equal(HttpStatusCode.NotFound, old.StatusCode);
        Assert.Equal("attendance.invalid_qr", await TestApi.ProblemCodeAsync(old));
        Assert.Equal(HttpStatusCode.Created, (await CheckInAsync(a.Reception, a.Branch, qrToken: renewed.Token)).StatusCode);

        // El QR de otro gimnasio, uno inventado o con formato incorrecto: no existen.
        foreach (var token in new[] { qrB.Token, "fc1_" + new string('A', 43), "no-es-un-qr" })
        {
            var invalid = await CheckInAsync(a.Reception, a.Branch, qrToken: token);
            Assert.Equal(HttpStatusCode.NotFound, invalid.StatusCode);
            Assert.Equal("attendance.invalid_qr", await TestApi.ProblemCodeAsync(invalid));
        }
    }

    [Fact]
    public async Task El_QR_de_un_alumno_inactivo_no_da_acceso_y_emitirlo_exige_permiso()
    {
        var s = await NewSetupAsync();
        var qr = (await (await s.Reception.PostAsync($"/api/v1/members/{s.Member.Id}/qr-token", null)).Content.ReadFromJsonAsync<QrTokenDto>())!;
        await s.Tenant.Owner.PatchAsJsonAsync($"/api/v1/members/{s.Member.Id}/status", new { isActive = false });
        await TestApi.CreateUserAsync(s.Tenant.Owner, "entrenador@gym.test", "Entrenador", s.Branch);
        using var trainer = TestApi.Authenticated(api, await TestApi.LoginAsync(api, s.Tenant.Slug, "entrenador@gym.test", TestApi.UserPassword));

        await AssertDeniedAsync(await CheckInAsync(s.Reception, s.Branch, qrToken: qr.Token), "attendance.member_inactive");
        Assert.Equal(HttpStatusCode.Forbidden, (await trainer.PostAsync($"/api/v1/members/{s.Member.Id}/qr-token", null)).StatusCode);
    }

    // ---- Reglas de acceso: membresía -------------------------------------------------------------------------------

    [Fact]
    public async Task Sin_membresia_vigente_el_acceso_se_rechaza_con_el_motivo_exacto_y_sin_dejar_rastro()
    {
        var s = await NewSetupAsync("MV", paid: false);
        var plan = s.Plan;
        var branch = s.Branch;

        async Task<MemberDto> NewMemberAsync() => await MemberTests.CreateMemberAsync(s.Tenant.Owner, branch);

        // Nunca tuvo membresía.
        var none = await NewMemberAsync();
        await AssertDeniedAsync(await CheckInAsync(s.Reception, branch, none.Id), "attendance.no_active_membership");

        // Inscrito pero sin pagar.
        await AssertDeniedAsync(await CheckInAsync(s.Reception, branch, s.Member.Id), "attendance.membership_pending_payment");

        // Venció.
        var expired = await NewMemberAsync();
        var expiredMembership = await EnrollAsync(s.Tenant.Owner, expired.Id, plan.Id);
        await PayAsync(s.Tenant.Owner, expired.Id, 30m);
        await SqlAsync($"UPDATE members.memberships SET start_date = start_date - 60, end_date = end_date - 60 WHERE id = '{expiredMembership.Id}'");
        await AssertDeniedAsync(await CheckInAsync(s.Reception, branch, expired.Id), "attendance.membership_expired");

        // Aún no empieza.
        var future = await NewMemberAsync();
        await EnrollAsync(s.Tenant.Owner, future.Id, plan.Id, Today.AddDays(10));
        await PayAsync(s.Tenant.Owner, future.Id, 30m);
        await AssertDeniedAsync(await CheckInAsync(s.Reception, branch, future.Id), "attendance.membership_not_started");

        // Cancelada.
        var cancelled = await NewMemberAsync();
        var cancelledMembership = await EnrollAsync(s.Tenant.Owner, cancelled.Id, plan.Id);
        await s.Tenant.Owner.PostAsJsonAsync($"/api/v1/memberships/{cancelledMembership.Id}/cancel", new { reason = "Prueba de cancelación" });
        await AssertDeniedAsync(await CheckInAsync(s.Reception, branch, cancelled.Id), "attendance.no_active_membership");

        // Alumno inactivo.
        var inactive = await NewMemberAsync();
        await s.Tenant.Owner.PatchAsJsonAsync($"/api/v1/members/{inactive.Id}/status", new { isActive = false });
        await AssertDeniedAsync(await CheckInAsync(s.Reception, branch, inactive.Id), "attendance.member_inactive");

        Assert.Equal(0L, await ScalarAsync<long>($"SELECT count(*) FROM access.attendance WHERE tenant_id = '{s.Tenant.Id}'"));
    }

    [Fact]
    public async Task Una_membresia_congelada_no_da_acceso_y_el_ultimo_dia_de_vigencia_todavia_si()
    {
        var s = await NewSetupAsync("CG", allowFreeze: true);
        var other = await MemberTests.CreateMemberAsync(s.Tenant.Owner, s.Branch);
        var otherMembership = await EnrollAsync(s.Tenant.Owner, other.Id, s.Plan.Id);
        await PayAsync(s.Tenant.Owner, other.Id, 30m);

        await s.Tenant.Owner.PostAsJsonAsync($"/api/v1/memberships/{s.Membership.Id}/freeze", new { reason = "Viaje" });
        await AssertDeniedAsync(await CheckInAsync(s.Reception, s.Branch, s.Member.Id), "attendance.membership_frozen");
        await s.Tenant.Owner.PostAsync($"/api/v1/memberships/{s.Membership.Id}/reactivate", null);
        Assert.Equal(HttpStatusCode.Created, (await CheckInAsync(s.Reception, s.Branch, s.Member.Id)).StatusCode);

        // El último día todavía entra; al día siguiente ya no.
        await SqlAsync($"UPDATE members.memberships SET start_date = '{Today.AddDays(-29):yyyy-MM-dd}', end_date = '{Today:yyyy-MM-dd}' WHERE id = '{otherMembership.Id}'");
        Assert.Equal(HttpStatusCode.Created, (await CheckInAsync(s.Reception, s.Branch, other.Id)).StatusCode);
        await CheckOutAsync(s.Reception, other.Id);
        await SqlAsync($"UPDATE members.memberships SET start_date = '{Today.AddDays(-30):yyyy-MM-dd}', end_date = '{Today.AddDays(-1):yyyy-MM-dd}' WHERE id = '{otherMembership.Id}'");
        await AssertDeniedAsync(await CheckInAsync(s.Reception, s.Branch, other.Id), "attendance.membership_expired");
    }

    // ---- Reglas de acceso: deuda -----------------------------------------------------------------------------------

    [Fact]
    public async Task Permitir_hasta_el_vencimiento_deja_entrar_con_deuda_y_la_corta_cuando_vence_y_se_restablece_al_pagar()
    {
        var s = await NewSetupAsync("D1", paid: false);
        await PayAsync(s.Tenant.Owner, s.Member.Id, 10m); // abono: la membresía queda activa

        var decision = (await (await ValidateAccessAsync(s.Reception, s.Branch, s.Member.Id)).Content.ReadFromJsonAsync<AccessDecisionDto>())!;
        Assert.True(decision.Allowed);
        Assert.Equal(20m, decision.OwedAmount);
        Assert.Equal("AllowUntilOverdue", decision.Policy);
        Assert.Equal(HttpStatusCode.Created, (await CheckInAsync(s.Reception, s.Branch, s.Member.Id)).StatusCode);
        await CheckOutAsync(s.Reception, s.Member.Id);

        // La cuota vence sin saldarse: se corta el acceso.
        await SqlAsync($"UPDATE billing.payment_obligations SET issue_date = issue_date - 10, due_date = due_date - 10 WHERE id = '{s.Membership.Obligation!.Id}'");
        await AssertDeniedAsync(await CheckInAsync(s.Reception, s.Branch, s.Member.Id), "attendance.debt_overdue");

        await PayAsync(s.Tenant.Owner, s.Member.Id, 20m, reference: "VCH-2");
        Assert.Equal(HttpStatusCode.Created, (await CheckInAsync(s.Reception, s.Branch, s.Member.Id)).StatusCode);
    }

    [Fact]
    public async Task Exigir_pago_completo_corta_el_acceso_si_se_revierte_un_pago_aunque_la_membresia_siga_activa()
    {
        var s = await NewSetupAsync("D2", paid: false);
        await SetPolicyAsync(s.Tenant.Owner, "RequireFullPayment");
        var partial = await PayAsync(s.Tenant.Owner, s.Member.Id, 20m);
        await AssertDeniedAsync(await CheckInAsync(s.Reception, s.Branch, s.Member.Id), "attendance.membership_pending_payment");

        await PayAsync(s.Tenant.Owner, s.Member.Id, 10m, reference: "VCH-2");
        Assert.Equal(HttpStatusCode.Created, (await CheckInAsync(s.Reception, s.Branch, s.Member.Id)).StatusCode);
        await CheckOutAsync(s.Reception, s.Member.Id);

        // Se revierte el primer pago: la membresía sigue "Activa", pero ya hay saldo y el acceso se corta.
        await PostWithKeyAsync(s.Tenant.Owner, $"/api/v1/payments/{partial.Id}/reverse", new { reason = "Cobro mal aplicado" });
        Assert.Equal("Active", (await GetMembershipAsync(s.Tenant.Owner, s.Membership.Id)).Status);
        await AssertDeniedAsync(await CheckInAsync(s.Reception, s.Branch, s.Member.Id), "attendance.debt_pending");
        var decision = (await (await ValidateAccessAsync(s.Reception, s.Branch, s.Member.Id)).Content.ReadFromJsonAsync<AccessDecisionDto>())!;
        Assert.False(decision.Allowed);
        Assert.Equal(20m, decision.OwedAmount);
        Assert.True(decision.Overridable);
    }

    [Fact]
    public async Task Con_pago_minimo_hay_que_alcanzar_el_porcentaje_para_activar_y_para_seguir_entrando()
    {
        var s = await NewSetupAsync("D3", paid: false);
        await SetPolicyAsync(s.Tenant.Owner, "AllowWithMinimumPayment", 60);

        await PayAsync(s.Tenant.Owner, s.Member.Id, 17m); // 56,7 %: no alcanza
        await AssertDeniedAsync(await CheckInAsync(s.Reception, s.Branch, s.Member.Id), "attendance.membership_pending_payment");

        var second = await PayAsync(s.Tenant.Owner, s.Member.Id, 1m, reference: "VCH-2"); // 60 %: activa y alcanza
        Assert.Equal(HttpStatusCode.Created, (await CheckInAsync(s.Reception, s.Branch, s.Member.Id)).StatusCode);
        await CheckOutAsync(s.Reception, s.Member.Id);

        await PostWithKeyAsync(s.Tenant.Owner, $"/api/v1/payments/{second.Id}/reverse", new { reason = "Error de monto" }); // vuelve a 56,7 %
        await AssertDeniedAsync(await CheckInAsync(s.Reception, s.Branch, s.Member.Id), "attendance.minimum_payment_not_met");
    }

    // ---- Duplicados y concurrencia ---------------------------------------------------------------------------------

    [Fact]
    public async Task No_se_permite_una_segunda_entrada_abierta_ni_en_la_misma_ni_en_otra_sucursal()
    {
        var s = await NewSetupAsync("DU");
        var other = await TestApi.CreateBranchAsync(s.Tenant.Owner, "DU2");
        await CheckInOkAsync(s.Reception, s.Branch, s.Member.Id);

        var same = await CheckInAsync(s.Reception, s.Branch, s.Member.Id);
        Assert.Equal(HttpStatusCode.Conflict, same.StatusCode);
        Assert.Equal("attendance.already_checked_in", await TestApi.ProblemCodeAsync(same));

        var elsewhere = await CheckInAsync(s.Tenant.Owner, other, s.Member.Id);
        Assert.Equal(HttpStatusCode.Conflict, elsewhere.StatusCode);
        Assert.Equal("attendance.open_entry_elsewhere", await TestApi.ProblemCodeAsync(elsewhere));

        // Tampoco con una excepción autorizada: el alumno ya está adentro.
        var withOverride = await s.Tenant.Owner.PostAsJsonAsync("/api/v1/attendance/check-in", new { branchId = s.Branch, memberId = s.Member.Id, overrideReason = "Intento de doble entrada" });
        Assert.Equal(HttpStatusCode.Conflict, withOverride.StatusCode);
        Assert.Equal(1L, await ScalarAsync<long>($"SELECT count(*) FROM access.attendance WHERE member_id = '{s.Member.Id}'"));
    }

    [Fact]
    public async Task Ocho_entradas_simultaneas_del_mismo_alumno_dejan_una_sola_abierta()
    {
        var s = await NewSetupAsync("PR");

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => CheckInAsync(s.Reception, s.Branch, s.Member.Id)));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        foreach (var rejected in responses.Where(r => r.StatusCode != HttpStatusCode.Created))
        {
            Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
            Assert.Equal("attendance.already_checked_in", await TestApi.ProblemCodeAsync(rejected));
        }

        Assert.Equal(1L, await ScalarAsync<long>($"SELECT count(*) FROM access.attendance WHERE member_id = '{s.Member.Id}'"));
    }

    // ---- Sucursales ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task El_plan_puede_limitar_las_sucursales_y_un_alumno_puede_entrenar_en_otra_si_el_plan_lo_permite()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var north = await TestApi.CreateBranchAsync(tenant.Owner, "BN");
        var south = await TestApi.CreateBranchAsync(tenant.Owner, "BS");
        var restricted = await CreatePlanAsync(tenant.Owner, "Solo norte", 30m, [north]);
        var open = await CreatePlanAsync(tenant.Owner, "Todas", 30m);
        var memberRestricted = await MemberTests.CreateMemberAsync(tenant.Owner, north);
        var memberOpen = await MemberTests.CreateMemberAsync(tenant.Owner, north);
        await EnrollAsync(tenant.Owner, memberRestricted.Id, restricted.Id);
        await PayAsync(tenant.Owner, memberRestricted.Id, 30m);
        await EnrollAsync(tenant.Owner, memberOpen.Id, open.Id);
        await PayAsync(tenant.Owner, memberOpen.Id, 30m);
        await TestApi.CreateUserAsync(tenant.Owner, "recepcion.sur@gym.test", "Recepcion", south);
        using var receptionSouth = TestApi.Authenticated(api, await TestApi.LoginAsync(api, tenant.Slug, "recepcion.sur@gym.test", TestApi.UserPassword));

        // Plan solo para el norte: en el sur no entra.
        await AssertDeniedAsync(await CheckInAsync(tenant.Owner, south, memberRestricted.Id), "attendance.plan_branch_restricted");
        Assert.Equal(HttpStatusCode.Created, (await CheckInAsync(tenant.Owner, north, memberRestricted.Id)).StatusCode);

        // Plan de todas las sucursales: la recepción del sur registra a un alumno inscrito en el norte.
        var visit = await CheckInAsync(receptionSouth, south, memberOpen.Id);
        Assert.Equal(HttpStatusCode.Created, visit.StatusCode);
        var entry = (await visit.Content.ReadFromJsonAsync<AttendanceDto>())!;
        Assert.Equal(south, entry.BranchId);

        // La recepción del sur no puede registrar en el norte, que no es su sucursal.
        var denied = await CheckInAsync(receptionSouth, north, memberOpen.Id);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal("branches.not_allowed", await TestApi.ProblemCodeAsync(denied));
    }

    [Fact]
    public async Task Una_sucursal_inactiva_o_de_otro_gimnasio_no_sirve_para_registrar_entradas()
    {
        var a = await NewSetupAsync("IA");
        var b = await NewSetupAsync("IB");
        var inactive = await TestApi.CreateBranchAsync(a.Tenant.Owner, "INA");
        await a.Tenant.Owner.PutAsJsonAsync($"/api/v1/branches/{inactive}", new { name = "Inactiva", timeZone = "America/Guayaquil", isActive = false });

        var inactiveBranch = await CheckInAsync(a.Tenant.Owner, inactive, a.Member.Id);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, inactiveBranch.StatusCode);
        Assert.Equal("branches.inactive", await TestApi.ProblemCodeAsync(inactiveBranch));

        var foreignBranch = await CheckInAsync(a.Tenant.Owner, b.Branch, a.Member.Id);
        Assert.Equal(HttpStatusCode.NotFound, foreignBranch.StatusCode);
        Assert.Equal("branches.not_found", await TestApi.ProblemCodeAsync(foreignBranch));
    }

    // ---- Excepciones autorizadas -----------------------------------------------------------------------------------

    [Fact]
    public async Task Una_excepcion_exige_permiso_y_motivo_y_queda_registrada_y_auditada()
    {
        var s = await NewSetupAsync("EX", paid: false); // pendiente de pago: acceso denegado
        await TestApi.CreateUserAsync(s.Tenant.Owner, "admin@gym.test", "AdministradorSucursal", s.Branch);
        using var admin = TestApi.Authenticated(api, await TestApi.LoginAsync(api, s.Tenant.Slug, "admin@gym.test", TestApi.UserPassword));

        // Sin motivo: se rechaza. Con motivo pero sin permiso (recepción): se rechaza con otro código.
        await AssertDeniedAsync(await CheckInAsync(admin, s.Branch, s.Member.Id), "attendance.membership_pending_payment");
        var noPermission = await CheckInAsync(s.Reception, s.Branch, s.Member.Id, overrideReason: "El gerente me autorizó");
        Assert.Equal(HttpStatusCode.Forbidden, noPermission.StatusCode);
        Assert.Equal("attendance.override_not_allowed", await TestApi.ProblemCodeAsync(noPermission));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await CheckInAsync(admin, s.Branch, s.Member.Id, overrideReason: "no")).StatusCode);
        Assert.Equal(0L, await ScalarAsync<long>($"SELECT count(*) FROM access.attendance WHERE tenant_id = '{s.Tenant.Id}'"));

        var response = await CheckInAsync(admin, s.Branch, s.Member.Id, overrideReason: "Pago por transferencia en camino, autorizado por el gerente");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var entry = (await response.Content.ReadFromJsonAsync<AttendanceDto>())!;
        Assert.True(entry.IsException);
        Assert.Equal("Pago por transferencia en camino, autorizado por el gerente", entry.ExceptionReason);
        Assert.Equal(s.Membership.Id, entry.MembershipId);
        Assert.Contains("admin", entry.RegisteredByName ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        Assert.True(await ScalarAsync<long>($"SELECT count(*) FROM audit.audit_logs WHERE tenant_id = '{s.Tenant.Id}' AND action = 'AttendanceEntry.Created' AND after::text LIKE '%autorizado por el gerente%'") >= 1);
        var exceptions = await admin.GetFromJsonAsync<PagedDto<AttendanceDto>>("/api/v1/attendance?exception=true");
        Assert.Equal([entry.Id], exceptions!.Items.Select(e => e.Id));
    }

    [Fact]
    public async Task Una_excepcion_no_aplica_a_alumnos_inactivos_y_si_el_acceso_ya_esta_permitido_no_se_marca_como_excepcion()
    {
        var s = await NewSetupAsync("EY");
        var inactive = await MemberTests.CreateMemberAsync(s.Tenant.Owner, s.Branch);
        await s.Tenant.Owner.PatchAsJsonAsync($"/api/v1/members/{inactive.Id}/status", new { isActive = false });

        var stillDenied = await CheckInAsync(s.Tenant.Owner, s.Branch, inactive.Id, overrideReason: "Intento de excepción en alumno inactivo");
        await AssertDeniedAsync(stillDenied, "attendance.member_inactive");

        var normal = await CheckInAsync(s.Tenant.Owner, s.Branch, s.Member.Id, overrideReason: "Motivo innecesario porque ya podía entrar");
        Assert.Equal(HttpStatusCode.Created, normal.StatusCode);
        var entry = (await normal.Content.ReadFromJsonAsync<AttendanceDto>())!;
        Assert.False(entry.IsException);
        Assert.Null(entry.ExceptionReason);
    }

    // ---- Validar acceso --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Validar_el_acceso_informa_sin_registrar_y_avisa_si_el_alumno_ya_esta_adentro()
    {
        var s = await NewSetupAsync();

        var decision = (await (await ValidateAccessAsync(s.Reception, s.Branch, memberCode: s.Member.Code)).Content.ReadFromJsonAsync<AccessDecisionDto>())!;

        Assert.True(decision.Allowed);
        Assert.Null(decision.Code);
        Assert.False(decision.AlreadyInside);
        Assert.Equal(s.Member.Id, decision.MemberId);
        Assert.Equal(s.Membership.Id, decision.MembershipId);
        Assert.Equal("Mensual", decision.PlanName);
        Assert.Equal(s.Membership.EndDate, decision.MembershipEnd);
        Assert.Equal(0m, decision.OwedAmount);
        Assert.Equal(0L, await ScalarAsync<long>($"SELECT count(*) FROM access.attendance WHERE tenant_id = '{s.Tenant.Id}'"));

        await CheckInOkAsync(s.Reception, s.Branch, s.Member.Id);
        var inside = (await (await ValidateAccessAsync(s.Reception, s.Branch, s.Member.Id)).Content.ReadFromJsonAsync<AccessDecisionDto>())!;
        Assert.False(inside.Allowed);
        Assert.True(inside.AlreadyInside);
        Assert.Equal("attendance.already_checked_in", inside.Code);
        Assert.False(inside.Overridable);
    }

    // ---- Permisos y aislamiento ------------------------------------------------------------------------------------

    [Fact]
    public async Task Los_permisos_del_rol_se_aplican_a_la_asistencia()
    {
        var s = await NewSetupAsync("PM");
        await TestApi.CreateUserAsync(s.Tenant.Owner, "entrenador@gym.test", "Entrenador", s.Branch);
        await TestApi.CreateUserAsync(s.Tenant.Owner, "alumno@gym.test", "Alumno", s.Branch);
        using var trainer = TestApi.Authenticated(api, await TestApi.LoginAsync(api, s.Tenant.Slug, "entrenador@gym.test", TestApi.UserPassword));
        using var student = TestApi.Authenticated(api, await TestApi.LoginAsync(api, s.Tenant.Slug, "alumno@gym.test", TestApi.UserPassword));
        var entry = await CheckInOkAsync(s.Reception, s.Branch, s.Member.Id);

        // El entrenador consulta pero no registra ni valida (el registro manual está protegido por permiso).
        Assert.Equal(HttpStatusCode.OK, (await trainer.GetAsync("/api/v1/attendance")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await trainer.GetAsync($"/api/v1/attendance/{entry.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await CheckInAsync(trainer, s.Branch, s.Member.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await CheckOutAsync(trainer, s.Member.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ValidateAccessAsync(trainer, s.Branch, s.Member.Id)).StatusCode);

        // El alumno no puede ni consultar ni registrar su propia asistencia.
        Assert.Equal(HttpStatusCode.Forbidden, (await student.GetAsync("/api/v1/attendance")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await CheckInAsync(student, s.Branch, s.Member.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().PostAsJsonAsync("/api/v1/attendance/check-in", new { branchId = s.Branch, memberId = s.Member.Id })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().GetAsync("/api/v1/attendance")).StatusCode);
    }

    [Fact]
    public async Task Otro_gimnasio_no_ve_ni_registra_asistencia_ajena()
    {
        var a = await NewSetupAsync("TA");
        var b = await NewSetupAsync("TB");
        var entryB = await CheckInOkAsync(b.Reception, b.Branch, b.Member.Id);

        var member = await CheckInAsync(a.Reception, a.Branch, b.Member.Id);
        Assert.Equal(HttpStatusCode.NotFound, member.StatusCode);
        Assert.Equal("members.not_found", await TestApi.ProblemCodeAsync(member));
        // Los códigos de alumno son por gimnasio: uno que solo existe en B (el segundo alumno de B) no existe en A.
        var secondInB = await MemberTests.CreateMemberAsync(b.Tenant.Owner, b.Branch);
        Assert.Equal("A-000002", secondInB.Code);
        var byCode = await CheckInAsync(a.Reception, a.Branch, memberCode: secondInB.Code);
        Assert.Equal(HttpStatusCode.NotFound, byCode.StatusCode);
        Assert.Equal("members.not_found", await TestApi.ProblemCodeAsync(byCode));
        Assert.Equal(HttpStatusCode.NotFound, (await CheckOutAsync(a.Reception, b.Member.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.Reception.GetAsync($"/api/v1/attendance/{entryB.Id}")).StatusCode);
        Assert.Equal(0, (await a.Tenant.Owner.GetFromJsonAsync<PagedDto<AttendanceDto>>("/api/v1/attendance"))!.TotalCount);
        Assert.True((await b.Tenant.Owner.GetFromJsonAsync<AttendanceDto>($"/api/v1/attendance/{entryB.Id}"))!.IsOpen);
    }

    [Fact]
    public async Task La_consulta_filtra_por_alumno_sucursal_metodo_estado_fecha_y_busqueda_y_respeta_el_alcance()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var north = await TestApi.CreateBranchAsync(tenant.Owner, "LN");
        var south = await TestApi.CreateBranchAsync(tenant.Owner, "LS");
        var plan = await CreatePlanAsync(tenant.Owner, "Todas", 30m);
        var ana = await MemberTests.CreateMemberAsync(tenant.Owner, north, first: "Ana", last: "Zapata");
        var beto = await MemberTests.CreateMemberAsync(tenant.Owner, south, first: "Beto", last: "Yánez");
        foreach (var m in new[] { ana, beto })
        {
            await EnrollAsync(tenant.Owner, m.Id, plan.Id);
            await PayAsync(tenant.Owner, m.Id, 30m);
        }

        var qr = (await (await tenant.Owner.PostAsync($"/api/v1/members/{beto.Id}/qr-token", null)).Content.ReadFromJsonAsync<QrTokenDto>())!;
        var anaEntry = await CheckInOkAsync(tenant.Owner, north, ana.Id);
        await CheckOutAsync(tenant.Owner, ana.Id);
        var betoEntry = await CheckInOkAsync(tenant.Owner, south, qr: qr.Token);
        await TestApi.CreateUserAsync(tenant.Owner, "recepcion.norte@gym.test", "Recepcion", north);
        using var receptionNorth = TestApi.Authenticated(api, await TestApi.LoginAsync(api, tenant.Slug, "recepcion.norte@gym.test", TestApi.UserPassword));
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        async Task<Guid[]> ListAsync(HttpClient client, string query) =>
            [.. (await client.GetFromJsonAsync<PagedDto<AttendanceDto>>($"/api/v1/attendance?{query}"))!.Items.Select(a => a.Id)];

        Assert.Equal([anaEntry.Id], await ListAsync(tenant.Owner, $"memberId={ana.Id}"));
        Assert.Equal([betoEntry.Id], await ListAsync(tenant.Owner, $"branchId={south}"));
        Assert.Equal([betoEntry.Id], await ListAsync(tenant.Owner, "method=Qr"));
        Assert.Equal([anaEntry.Id], await ListAsync(tenant.Owner, "method=Manual"));
        Assert.Equal([betoEntry.Id], await ListAsync(tenant.Owner, "open=true"));
        Assert.Equal([anaEntry.Id], await ListAsync(tenant.Owner, "open=false"));
        Assert.Equal([betoEntry.Id], await ListAsync(tenant.Owner, "search=Y%C3%A1nez"));
        Assert.Equal([anaEntry.Id], await ListAsync(tenant.Owner, $"search={ana.Code}"));
        Assert.Equal(2, (await ListAsync(tenant.Owner, $"from={today.AddDays(-1):yyyy-MM-dd}&to={today.AddDays(1):yyyy-MM-dd}")).Length);
        Assert.Empty(await ListAsync(tenant.Owner, $"to={today.AddDays(-2):yyyy-MM-dd}"));
        Assert.Empty(await ListAsync(tenant.Owner, "exception=true"));

        // La recepción del norte solo ve lo del norte.
        Assert.Equal([anaEntry.Id], await ListAsync(receptionNorth, string.Empty));
        Assert.Equal(HttpStatusCode.NotFound, (await receptionNorth.GetAsync($"/api/v1/attendance/{betoEntry.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await receptionNorth.GetAsync($"/api/v1/attendance?branchId={south}")).StatusCode);
    }

    [Fact]
    public async Task Las_entradas_y_salidas_quedan_auditadas()
    {
        var s = await NewSetupAsync("AU");
        var entry = await CheckInOkAsync(s.Reception, s.Branch, s.Member.Id);
        await CheckOutAsync(s.Reception, s.Member.Id);

        var audit = (await s.Tenant.Owner.GetFromJsonAsync<PagedDto<AuditDto>>("/api/v1/audit-logs?pageSize=100"))!;

        Assert.Contains(audit.Items, l => l.Action == "AttendanceEntry.Created" && l.EntityId == entry.Id.ToString());
        Assert.Contains(audit.Items, l => l.Action == "AttendanceEntry.Updated" && l.EntityId == entry.Id.ToString());
    }

    // ---- La base como última defensa -------------------------------------------------------------------------------

    [Fact]
    public async Task La_base_impide_dos_entradas_abiertas_modificar_visitas_y_borrarlas_aunque_el_codigo_falle()
    {
        var a = await NewSetupAsync("DA");
        var b = await NewSetupAsync("DB");
        var open = await CheckInOkAsync(a.Reception, a.Branch, a.Member.Id);
        var closedMember = await MemberTests.CreateMemberAsync(a.Tenant.Owner, a.Branch);
        await EnrollAsync(a.Tenant.Owner, closedMember.Id, a.Plan.Id);
        await PayAsync(a.Tenant.Owner, closedMember.Id, 30m);
        var closed = await CheckInOkAsync(a.Reception, a.Branch, closedMember.Id);
        await CheckOutAsync(a.Reception, closedMember.Id);
        var entryB = await CheckInOkAsync(b.Reception, b.Branch, b.Member.Id);

        await using var connection = new NpgsqlConnection(api.AppConnection);
        await connection.OpenAsync();
        await Exec(connection, $"SELECT set_config('app.tenant_id', '{a.Tenant.Id}', false)");

        // RLS.
        Assert.Equal(0L, await Scalar(connection, $"SELECT count(*) FROM access.attendance WHERE id = '{entryB.Id}'"));
        Assert.Equal(0, await Exec(connection, $"UPDATE access.attendance SET check_out_at = now(), check_out_by = gen_random_uuid(), check_out_method = 'Manual' WHERE id = '{entryB.Id}'"));

        // Dos entradas abiertas del mismo alumno: lo impide un índice único.
        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => Exec(connection, $"""
            INSERT INTO access.attendance (id, tenant_id, branch_id, member_id, check_in_at, method, is_exception, registered_by)
            VALUES (gen_random_uuid(), '{a.Tenant.Id}', '{a.Branch}', '{a.Member.Id}', now(), 'Manual', false, gen_random_uuid())
            """));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);

        // Una excepción sin motivo, o un motivo sin excepción, no existe.
        var noReason = await Assert.ThrowsAsync<PostgresException>(() => Exec(connection, $"""
            INSERT INTO access.attendance (id, tenant_id, branch_id, member_id, check_in_at, method, is_exception, registered_by)
            VALUES (gen_random_uuid(), '{a.Tenant.Id}', '{a.Branch}', '{closedMember.Id}', now(), 'Manual', true, gen_random_uuid())
            """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, noReason.SqlState);

        // La entrada no se reescribe, y una visita cerrada es definitiva.
        var method = await Assert.ThrowsAsync<PostgresException>(() => Exec(connection, $"UPDATE access.attendance SET method = 'Qr' WHERE id = '{open.Id}'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, method.SqlState);
        var checkIn = await Assert.ThrowsAsync<PostgresException>(() => Exec(connection, $"UPDATE access.attendance SET check_in_at = now() - interval '2 days' WHERE id = '{open.Id}'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, checkIn.SqlState);
        var reopen = await Assert.ThrowsAsync<PostgresException>(() => Exec(connection,
            $"UPDATE access.attendance SET check_out_at = NULL, check_out_by = NULL, check_out_method = NULL WHERE id = '{closed.Id}'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, reopen.SqlState);
        var backwards = await Assert.ThrowsAsync<PostgresException>(() => Exec(connection,
            $"UPDATE access.attendance SET check_out_at = check_in_at - interval '1 hour', check_out_by = gen_random_uuid(), check_out_method = 'Manual' WHERE id = '{open.Id}'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, backwards.SqlState);

        // Las visitas no se borran.
        var delete = await Assert.ThrowsAsync<PostgresException>(() => Exec(connection, "DELETE FROM access.attendance"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, delete.SqlState);

        // Una entrada de A no puede apuntar a un alumno de B (clave compuesta); se usa uno sin entradas para llegar a esa regla.
        var freeMemberB = await MemberTests.CreateMemberAsync(b.Tenant.Owner, b.Branch);
        var cross = await Assert.ThrowsAsync<PostgresException>(() => Exec(connection, $"""
            INSERT INTO access.attendance (id, tenant_id, branch_id, member_id, check_in_at, method, is_exception, registered_by)
            VALUES (gen_random_uuid(), '{a.Tenant.Id}', '{a.Branch}', '{freeMemberB.Id}', now(), 'Manual', false, gen_random_uuid())
            """));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, cross.SqlState);
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
