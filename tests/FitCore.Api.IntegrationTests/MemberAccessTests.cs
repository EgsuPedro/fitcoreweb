using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FitCore.Api.IntegrationTests.Infrastructure;
using static FitCore.Api.IntegrationTests.Infrastructure.CommercialTestApi;

namespace FitCore.Api.IntegrationTests;

[Collection(ApiCollection4.Name)]
public class MemberAccessTests(ApiFixture api)
{
    private sealed record AccessRow(Guid MemberId, string Email, string TemporaryPassword);

    private sealed record MeRow(string Email, string[] Roles, string[] Permissions, bool MustChangePassword);

    private sealed record MyMemberRow(Guid Id, string Code, string FirstName, string LastName, string BranchName, string Status);

    private sealed record MyMembershipRow(Guid Id, string PlanName, string Status, decimal AgreedPrice, decimal? Balance);

    private sealed record MyPaymentRow(Guid Id, string ReceiptNumber, decimal Amount, string Status);

    private sealed record MyAttendanceRow(Guid Id, string BranchName, DateTimeOffset CheckInAt);

    private const string NewPassword = "Nueva-Clave-2026x";

    private static string UniqueEmail(string prefix) => $"{prefix}.{Guid.NewGuid():N}"[..24] + "@alumnos.test";

    private static async Task<MemberDto> CreateMemberWithEmailAsync(HttpClient owner, Guid branch, string? email, string first = "Ana")
    {
        var response = await owner.PostAsJsonAsync("/api/v1/members", new
        {
            branchId = branch, firstName = first, lastName = "Prueba", idType = "Cedula", idNumber = MemberTests.NewCedula(),
            email, phone = "0991234567"
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<MemberDto>())!;
    }

    private static async Task<AccessRow> GrantAsync(HttpClient staff, Guid memberId)
    {
        var response = await staff.PostAsync($"/api/v1/members/{memberId}/access", null);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
        return (await response.Content.ReadFromJsonAsync<AccessRow>())!;
    }

    private async Task<HttpClient> StudentClientAsync(string slug, string email, string password) =>
        TestApi.Authenticated(api, await TestApi.LoginAsync(api, slug, email, password));

    private static async Task<T> GetAsync<T>(HttpClient client, string url) => (await client.GetFromJsonAsync<T>(url))!;

    /// <summary>Gimnasio con sucursal y un alumno con correo, ya con acceso y la clave temporal cambiada.</summary>
    private async Task<(TestTenant Tenant, Guid Branch, MemberDto Member, HttpClient Student, string Email)> ActiveStudentAsync(string code, string first = "Ana")
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var branch = await TestApi.CreateBranchAsync(tenant.Owner, code);
        var email = UniqueEmail(first.ToLowerInvariant());
        var member = await CreateMemberWithEmailAsync(tenant.Owner, branch, email, first);
        var access = await GrantAsync(tenant.Owner, member.Id);
        var temp = await StudentClientAsync(tenant.Slug, email, access.TemporaryPassword);
        var change = await temp.PostAsJsonAsync("/api/v1/auth/change-password", new { currentPassword = access.TemporaryPassword, newPassword = NewPassword });
        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);
        return (tenant, branch, member, await StudentClientAsync(tenant.Slug, email, NewPassword), email);
    }

    [Fact]
    public async Task Dar_acceso_crea_la_cuenta_con_clave_temporal_y_hasta_cambiarla_no_tiene_permisos()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var branch = await TestApi.CreateBranchAsync(tenant.Owner, "ACC");
        var email = UniqueEmail("pedro");
        var member = await CreateMemberWithEmailAsync(tenant.Owner, branch, email, "Pedro");

        var access = await GrantAsync(tenant.Owner, member.Id);

        Assert.Equal(email, access.Email, ignoreCase: true);
        Assert.Equal(14, access.TemporaryPassword.Length);
        Assert.Matches("[A-Z]", access.TemporaryPassword);
        Assert.Matches("[a-z]", access.TemporaryPassword);
        Assert.Matches("[0-9]", access.TemporaryPassword);
        var refreshed = await GetAsync<MemberDto>(tenant.Owner, $"/api/v1/members/{member.Id}");
        Assert.True(refreshed.HasAccount);

        var student = await StudentClientAsync(tenant.Slug, email, access.TemporaryPassword);
        var me = await GetAsync<MeRow>(student, "/api/v1/auth/me");
        Assert.True(me.MustChangePassword);
        Assert.Empty(me.Permissions);
        Assert.Equal(["Alumno"], me.Roles);
        // Con la clave temporal sin cambiar, ningún recurso responde.
        Assert.Equal(HttpStatusCode.Forbidden, (await student.GetAsync("/api/v1/me/member")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await student.GetAsync("/api/v1/members")).StatusCode);

        var change = await student.PostAsJsonAsync("/api/v1/auth/change-password", new { currentPassword = access.TemporaryPassword, newPassword = NewPassword });
        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);

        var fresh = await StudentClientAsync(tenant.Slug, email, NewPassword);
        var after = await GetAsync<MeRow>(fresh, "/api/v1/auth/me");
        Assert.False(after.MustChangePassword);
        Assert.Equal(["me.read", "nutrition.logs.record", "nutrition.member.read", "training.member.read", "training.sessions.log", "wellness.assessments.read"], after.Permissions.Order());
        var mine = await GetAsync<MyMemberRow>(fresh, "/api/v1/me/member");
        Assert.Equal(member.Id, mine.Id);
        Assert.Equal(member.Code, mine.Code);
        // Sin permisos de personal: no ve el listado ni la ficha de nadie.
        Assert.Equal(HttpStatusCode.Forbidden, (await fresh.GetAsync("/api/v1/members")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await fresh.GetAsync($"/api/v1/members/{member.Id}")).StatusCode);
    }

    [Fact]
    public async Task Sin_correo_no_se_puede_dar_acceso_y_el_acceso_no_se_da_dos_veces()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var branch = await TestApi.CreateBranchAsync(tenant.Owner, "NOE");
        var noEmail = await CreateMemberWithEmailAsync(tenant.Owner, branch, null);
        var withEmail = await CreateMemberWithEmailAsync(tenant.Owner, branch, UniqueEmail("dup"));

        var missing = await tenant.Owner.PostAsync($"/api/v1/members/{noEmail.Id}/access", null);
        await GrantAsync(tenant.Owner, withEmail.Id);
        var twice = await tenant.Owner.PostAsync($"/api/v1/members/{withEmail.Id}/access", null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, missing.StatusCode);
        Assert.Equal("members.access_email_required", await TestApi.ProblemCodeAsync(missing));
        Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);
        Assert.Equal("members.access_exists", await TestApi.ProblemCodeAsync(twice));
    }

    [Fact]
    public async Task Un_correo_ya_usado_por_otro_usuario_se_rechaza()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var branch = await TestApi.CreateBranchAsync(tenant.Owner, "EML");
        var member = await CreateMemberWithEmailAsync(tenant.Owner, branch, tenant.OwnerEmail);

        var response = await tenant.Owner.PostAsync($"/api/v1/members/{member.Id}/access", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("users.email_taken", await TestApi.ProblemCodeAsync(response));
        Assert.False((await GetAsync<MemberDto>(tenant.Owner, $"/api/v1/members/{member.Id}")).HasAccount);
    }

    [Fact]
    public async Task Restablecer_cambia_la_clave_cierra_la_sesion_y_vuelve_a_exigir_el_cambio()
    {
        var s = await ActiveStudentAsync("RST");
        Assert.Equal(HttpStatusCode.OK, (await s.Student.GetAsync("/api/v1/me/member")).StatusCode);

        var response = await s.Tenant.Owner.PostAsync($"/api/v1/members/{s.Member.Id}/access/reset", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var reset = (await response.Content.ReadFromJsonAsync<AccessRow>())!;

        // La sesión que ya tenía queda sin permisos en la siguiente solicitud y la clave anterior deja de servir.
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Student.GetAsync("/api/v1/me/member")).StatusCode);
        using var anonymous = api.CreateClient();
        var oldLogin = await anonymous.PostAsJsonAsync("/api/v1/auth/login", new { tenantSlug = s.Tenant.Slug, email = s.Email, password = NewPassword });
        Assert.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode);
        var again = await StudentClientAsync(s.Tenant.Slug, s.Email, reset.TemporaryPassword);
        Assert.True((await GetAsync<MeRow>(again, "/api/v1/auth/me")).MustChangePassword);
    }

    [Fact]
    public async Task Quitar_el_acceso_desactiva_la_cuenta_y_desactivar_al_alumno_tambien()
    {
        var s = await ActiveStudentAsync("OFF");

        var off = await s.Tenant.Owner.PatchAsJsonAsync($"/api/v1/members/{s.Member.Id}/access", new { active = false });
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.Student.GetAsync("/api/v1/me/member")).StatusCode);
        using var anonymous = api.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/v1/auth/login", new { tenantSlug = s.Tenant.Slug, email = s.Email, password = NewPassword })).StatusCode);

        var on = await s.Tenant.Owner.PatchAsJsonAsync($"/api/v1/members/{s.Member.Id}/access", new { active = true });
        Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        var back = await StudentClientAsync(s.Tenant.Slug, s.Email, NewPassword);
        Assert.Equal(HttpStatusCode.OK, (await back.GetAsync("/api/v1/me/member")).StatusCode);

        // Un alumno inactivo no entra: al desactivarlo se desactiva su cuenta y no se puede reactivar la cuenta mientras siga inactivo.
        Assert.Equal(HttpStatusCode.OK, (await s.Tenant.Owner.PatchAsJsonAsync($"/api/v1/members/{s.Member.Id}/status", new { isActive = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await back.GetAsync("/api/v1/me/member")).StatusCode);
        var blocked = await s.Tenant.Owner.PatchAsJsonAsync($"/api/v1/members/{s.Member.Id}/access", new { active = true });
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Equal("members.access_member_inactive", await TestApi.ProblemCodeAsync(blocked));
    }

    [Fact]
    public async Task El_alumno_solo_ve_sus_propios_datos_del_portal()
    {
        var a = await ActiveStudentAsync("PRT", "Alicia");
        var plan = await CreatePlanAsync(a.Tenant.Owner, "Mensual", 30m);
        // Otro alumno del mismo gimnasio, con su propio pago y su propia asistencia.
        var other = await CreateMemberWithEmailAsync(a.Tenant.Owner, a.Branch, UniqueEmail("otro"), "Otro");
        foreach (var memberId in new[] { a.Member.Id, other.Id })
        {
            var enroll = await a.Tenant.Owner.PostAsJsonAsync("/api/v1/memberships", new { memberId, planId = plan.Id });
            Assert.Equal(HttpStatusCode.Created, enroll.StatusCode);
        }

        await PayAsync(a.Tenant.Owner, a.Member.Id, 30m);
        var otherPayment = await PayAsync(a.Tenant.Owner, other.Id, 10m);
        await CheckInAsync(a.Tenant.Owner, a.Branch, other.Id);

        var memberships = await GetAsync<PagedDto<MyMembershipRow>>(a.Student, "/api/v1/me/memberships");
        var payments = await GetAsync<PagedDto<MyPaymentRow>>(a.Student, "/api/v1/me/payments");
        var attendance = await GetAsync<PagedDto<MyAttendanceRow>>(a.Student, "/api/v1/me/attendance");

        var membership = Assert.Single(memberships.Items);
        Assert.Equal("Mensual", membership.PlanName);
        Assert.Equal(0m, membership.Balance);
        var payment = Assert.Single(payments.Items);
        Assert.Equal(30m, payment.Amount);
        Assert.DoesNotContain(payments.Items, p => p.Id == otherPayment.Id);
        Assert.Empty(attendance.Items);

        // El portal no recibe identificadores: intentar pasar uno por la consulta no cambia de quién son los datos.
        var spoofed = await GetAsync<PagedDto<MyPaymentRow>>(a.Student, $"/api/v1/me/payments?memberId={other.Id}");
        Assert.Equal(payment.Id, Assert.Single(spoofed.Items).Id);
        // Y las rutas del personal con el id de otro alumno están cerradas.
        Assert.Equal(HttpStatusCode.Forbidden, (await a.Student.GetAsync($"/api/v1/payments?memberId={other.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await a.Student.GetAsync($"/api/v1/memberships?memberId={other.Id}")).StatusCode);
    }

    [Fact]
    public async Task Un_usuario_del_personal_sin_ficha_recibe_404_en_el_portal_y_el_aislamiento_entre_gimnasios_se_mantiene()
    {
        var a = await ActiveStudentAsync("ISA", "Ana");
        var b = await ActiveStudentAsync("ISB", "Beto");

        var staff = await a.Tenant.Owner.GetAsync("/api/v1/me/member");
        Assert.Equal(HttpStatusCode.NotFound, staff.StatusCode);
        Assert.Equal("portal.no_member_linked", await TestApi.ProblemCodeAsync(staff));

        var mineA = await GetAsync<MyMemberRow>(a.Student, "/api/v1/me/member");
        var mineB = await GetAsync<MyMemberRow>(b.Student, "/api/v1/me/member");
        Assert.NotEqual(mineA.Id, mineB.Id);
        Assert.Equal("Ana", mineA.FirstName);
        Assert.Equal("Beto", mineB.FirstName);
        // Un alumno de otro gimnasio no existe para el personal (404, no 403).
        Assert.Equal(HttpStatusCode.NotFound, (await a.Tenant.Owner.PostAsync($"/api/v1/members/{b.Member.Id}/access/reset", null)).StatusCode);
    }

    [Fact]
    public async Task Solo_quien_tiene_el_permiso_da_acceso_y_la_recepcion_no_lo_tiene_por_defecto()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var branch = await TestApi.CreateBranchAsync(tenant.Owner, "PRM");
        var member = await CreateMemberWithEmailAsync(tenant.Owner, branch, UniqueEmail("perm"));
        await TestApi.CreateUserAsync(tenant.Owner, "recepcion.perm@gym.test", "Recepcion", branch);
        var reception = TestApi.Authenticated(api, await TestApi.LoginAsync(api, tenant.Slug, "recepcion.perm@gym.test", TestApi.UserPassword));
        await TestApi.CreateUserAsync(tenant.Owner, "admin.perm@gym.test", "AdministradorSucursal", branch);
        var admin = TestApi.Authenticated(api, await TestApi.LoginAsync(api, tenant.Slug, "admin.perm@gym.test", TestApi.UserPassword));

        Assert.Equal(HttpStatusCode.Forbidden, (await reception.PostAsync($"/api/v1/members/{member.Id}/access", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsync($"/api/v1/members/{member.Id}/access", null)).StatusCode);
    }

    [Fact]
    public async Task Cada_operacion_de_acceso_queda_auditada_sin_la_clave()
    {
        var s = await ActiveStudentAsync("AUD");
        await s.Tenant.Owner.PostAsync($"/api/v1/members/{s.Member.Id}/access/reset", null);

        var audit = await s.Tenant.Owner.GetStringAsync("/api/v1/audit-logs?pageSize=100");

        Assert.Contains("Members.AccessGranted", audit);
        Assert.Contains("Members.AccessReset", audit);
        Assert.DoesNotContain("TemporaryPassword", audit, StringComparison.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(audit);
        Assert.True(document.RootElement.GetProperty("items").GetArrayLength() > 0);
    }
}
