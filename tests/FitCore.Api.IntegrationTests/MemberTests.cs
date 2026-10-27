using System.Net;
using System.Net.Http.Json;
using FitCore.Api.IntegrationTests.Infrastructure;
using Npgsql;

namespace FitCore.Api.IntegrationTests;

public sealed record MemberDto(
    Guid Id,
    string Code,
    Guid BranchId,
    string FirstName,
    string LastName,
    string IdType,
    string IdNumber,
    string? Email,
    string? Phone,
    string Status,
    DateOnly EnrolledOn,
    Guid? CreatedBy,
    Guid? UpdatedBy);

[Collection(ApiCollection1.Name)]
public class MemberTests(ApiFixture api)
{
    private static int _sequence = 100000;

    /// <summary>Cédula válida (provincia 17, persona natural) con el dígito verificador calculado.</summary>
    public static string NewCedula()
    {
        var body = "171" + Interlocked.Increment(ref _sequence).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
        var sum = 0;
        for (var i = 0; i < 9; i++)
        {
            var product = (body[i] - '0') * (i % 2 == 0 ? 2 : 1);
            sum += product > 9 ? product - 9 : product;
        }

        return body + ((10 - (sum % 10)) % 10);
    }

    public static object NewMember(Guid branchId, string? idNumber = null, string first = "Ana", string last = "Pérez", string? phone = "0991234567") => new
    {
        branchId,
        firstName = first,
        lastName = last,
        idType = "Cedula",
        idNumber = idNumber ?? NewCedula(),
        birthDate = "1995-04-12",
        email = "Ana.Perez@Correo.test",
        phone,
        address = "Av. Principal 123",
        emergencyContactName = "Luis Pérez",
        emergencyContactPhone = "0987654321",
        emergencyContactRelationship = "Hermano"
    };

    public static async Task<MemberDto> CreateMemberAsync(HttpClient client, Guid branchId, string? idNumber = null, string first = "Ana", string last = "Pérez", string? phone = "0991234567")
    {
        var response = await client.PostAsJsonAsync("/api/v1/members", NewMember(branchId, idNumber, first, last, phone));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<MemberDto>())!;
    }

    [Fact]
    public async Task Crear_un_alumno_valido_normaliza_los_datos_y_registra_quien_lo_creo()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var branch = await TestApi.CreateBranchAsync(tenant.Owner, "ALU");
        var cedula = NewCedula();

        var response = await tenant.Owner.PostAsJsonAsync("/api/v1/members", NewMember(branch, $"{cedula[..4]} {cedula[4..]}"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var member = (await response.Content.ReadFromJsonAsync<MemberDto>())!;
        Assert.Equal("A-000001", member.Code);
        Assert.Equal(cedula, member.IdNumber);
        Assert.Equal("ana.perez@correo.test", member.Email);
        Assert.Equal("Active", member.Status);
        Assert.Equal(branch, member.BranchId);
        Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow), member.EnrolledOn);
        var users = await tenant.Owner.GetFromJsonAsync<PagedDto<UserDto>>("/api/v1/users");
        Assert.Equal(users!.Items.Single(u => u.Email == tenant.OwnerEmail).Id, member.CreatedBy);

        var fetched = await tenant.Owner.GetFromJsonAsync<MemberDto>($"/api/v1/members/{member.Id}");
        Assert.Equal(member.Id, fetched!.Id);
    }

    [Fact]
    public async Task Validacion_rechaza_documento_invalido_correo_telefono_fecha_y_foto()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var branch = await TestApi.CreateBranchAsync(tenant.Owner, "VAL");

        var response = await tenant.Owner.PostAsJsonAsync("/api/v1/members", new
        {
            branchId = branch,
            firstName = "",
            lastName = "X",
            idType = "Cedula",
            idNumber = "1712345676",
            birthDate = "2999-01-01",
            email = "no-es-correo",
            phone = "abc",
            photoUrl = "javascript:alert(1)"
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("validation.failed", await TestApi.ProblemCodeAsync(response));
        var body = await response.Content.ReadAsStringAsync();
        foreach (var field in new[] { "firstName", "idNumber", "birthDate", "email", "phone", "photoUrl" })
        {
            Assert.Contains($"\"{field}\"", body, StringComparison.OrdinalIgnoreCase);
        }

        // Nada se persistió.
        var members = await tenant.Owner.GetFromJsonAsync<PagedDto<MemberDto>>("/api/v1/members");
        Assert.Empty(members!.Items);
    }

    [Fact]
    public async Task Un_ruc_y_un_pasaporte_validos_se_aceptan_y_uno_invalido_no()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var branch = await TestApi.CreateBranchAsync(tenant.Owner, "DOC");

        var ruc = await tenant.Owner.PostAsJsonAsync("/api/v1/members", new { branchId = branch, firstName = "Gym", lastName = "SA", idType = "Ruc", idNumber = "1790000028001" });
        var passport = await tenant.Owner.PostAsJsonAsync("/api/v1/members", new { branchId = branch, firstName = "John", lastName = "Doe", idType = "Passport", idNumber = "X1234567" });
        var badRuc = await tenant.Owner.PostAsJsonAsync("/api/v1/members", new { branchId = branch, firstName = "Gym", lastName = "SA", idType = "Ruc", idNumber = "1790000029001" });

        Assert.Equal(HttpStatusCode.Created, ruc.StatusCode);
        Assert.Equal(HttpStatusCode.Created, passport.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badRuc.StatusCode);
    }

    [Fact]
    public async Task El_mismo_documento_no_se_registra_dos_veces_en_el_gimnasio_pero_si_en_otro()
    {
        var a = await TestApi.NewTenantAsync(api);
        var b = await TestApi.NewTenantAsync(api);
        var branchA = await TestApi.CreateBranchAsync(a.Owner, "DUP");
        var branchB = await TestApi.CreateBranchAsync(b.Owner, "DUP");
        var cedula = NewCedula();
        await CreateMemberAsync(a.Owner, branchA, cedula);

        var duplicate = await a.Owner.PostAsJsonAsync("/api/v1/members", NewMember(branchA, cedula, "Otra", "Persona"));
        var otherTenant = await b.Owner.PostAsJsonAsync("/api/v1/members", NewMember(branchB, cedula));

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("members.duplicate_identification", await TestApi.ProblemCodeAsync(duplicate));
        Assert.Equal(HttpStatusCode.Created, otherTenant.StatusCode);
    }

    [Fact]
    public async Task Actualizar_no_permite_duplicar_el_documento_de_otro_alumno_ni_cambia_codigo_sucursal_o_tenant()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var north = await TestApi.CreateBranchAsync(tenant.Owner, "N");
        var south = await TestApi.CreateBranchAsync(tenant.Owner, "S");
        var first = await CreateMemberAsync(tenant.Owner, north, first: "Uno");
        var second = await CreateMemberAsync(tenant.Owner, north, first: "Dos");

        var clash = await tenant.Owner.PutAsJsonAsync($"/api/v1/members/{second.Id}", new { firstName = "Dos", lastName = "X", idType = "Cedula", idNumber = first.IdNumber });
        Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);

        // El cuerpo intenta mover al alumno de sucursal, de código y de gimnasio: esos campos no se aceptan.
        var update = await tenant.Owner.PutAsJsonAsync($"/api/v1/members/{second.Id}", new
        {
            firstName = "Dos Editado", lastName = "Apellido", idType = "Cedula", idNumber = second.IdNumber, phone = "0999999999",
            branchId = south, code = "HACK", tenantId = Guid.NewGuid()
        });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var updated = (await update.Content.ReadFromJsonAsync<MemberDto>())!;
        Assert.Equal("Dos Editado", updated.FirstName);
        Assert.Equal(north, updated.BranchId);
        Assert.Equal(second.Code, updated.Code);
        Assert.NotNull(updated.UpdatedBy);
    }

    [Fact]
    public async Task Un_alumno_de_otro_gimnasio_responde_404_en_lectura_edicion_y_estado_y_no_aparece_en_listados()
    {
        var a = await TestApi.NewTenantAsync(api);
        var b = await TestApi.NewTenantAsync(api);
        var branchA = await TestApi.CreateBranchAsync(a.Owner, "AIS");
        var branchB = await TestApi.CreateBranchAsync(b.Owner, "BIS");
        var memberB = await CreateMemberAsync(b.Owner, branchB);
        await CreateMemberAsync(a.Owner, branchA);

        Assert.Equal(HttpStatusCode.NotFound, (await a.Owner.GetAsync($"/api/v1/members/{memberB.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.Owner.PutAsJsonAsync($"/api/v1/members/{memberB.Id}",
            new { firstName = "X", lastName = "Y", idType = "Cedula", idNumber = NewCedula() })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.Owner.PatchAsJsonAsync($"/api/v1/members/{memberB.Id}/status", new { isActive = false })).StatusCode);

        var list = await a.Owner.GetFromJsonAsync<PagedDto<MemberDto>>("/api/v1/members?pageSize=100");
        Assert.DoesNotContain(list!.Items, m => m.Id == memberB.Id);
        Assert.Equal(1, list.TotalCount);

        // Registrar un alumno en la sucursal de otro gimnasio tampoco funciona.
        var foreignBranch = await a.Owner.PostAsJsonAsync("/api/v1/members", NewMember(branchB));
        Assert.Equal(HttpStatusCode.NotFound, foreignBranch.StatusCode);
        Assert.Equal("branches.not_found", await TestApi.ProblemCodeAsync(foreignBranch));

        // El alumno de B sigue intacto.
        var untouched = await b.Owner.GetFromJsonAsync<MemberDto>($"/api/v1/members/{memberB.Id}");
        Assert.Equal("Ana", untouched!.FirstName);
    }

    [Fact]
    public async Task Quien_solo_accede_a_una_sucursal_ve_y_registra_solo_en_ella()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var north = await TestApi.CreateBranchAsync(tenant.Owner, "NOR");
        var south = await TestApi.CreateBranchAsync(tenant.Owner, "SUR");
        var memberNorth = await CreateMemberAsync(tenant.Owner, north);
        var memberSouth = await CreateMemberAsync(tenant.Owner, south);
        var staff = await TestApi.CreateUserAsync(tenant.Owner, "recepcion.norte@gym.test", "Recepcion", north);
        using var client = TestApi.Authenticated(api, await TestApi.LoginAsync(api, tenant.Slug, staff.Email, TestApi.UserPassword));

        var list = await client.GetFromJsonAsync<PagedDto<MemberDto>>("/api/v1/members?pageSize=100");
        Assert.Equal([memberNorth.Id], list!.Items.Select(m => m.Id));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/members/{memberSouth.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/v1/members?branchId={south}")).StatusCode);

        var foreign = await client.PostAsJsonAsync("/api/v1/members", NewMember(south));
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        Assert.Equal("branches.not_allowed", await TestApi.ProblemCodeAsync(foreign));

        var own = await client.PostAsJsonAsync("/api/v1/members", NewMember(north));
        Assert.Equal(HttpStatusCode.Created, own.StatusCode);
    }

    [Fact]
    public async Task Los_permisos_del_rol_se_aplican_leer_crear_editar_y_cambiar_estado()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var branch = await TestApi.CreateBranchAsync(tenant.Owner, "PER");
        var member = await CreateMemberAsync(tenant.Owner, branch);

        async Task<HttpClient> ClientAsync(string email, string role)
        {
            await TestApi.CreateUserAsync(tenant.Owner, email, role, branch);
            return TestApi.Authenticated(api, await TestApi.LoginAsync(api, tenant.Slug, email, TestApi.UserPassword));
        }

        using var trainer = await ClientAsync("entrenador@gym.test", "Entrenador");
        using var reception = await ClientAsync("recepcion@gym.test", "Recepcion");
        using var student = await ClientAsync("alumno@gym.test", "Alumno");
        var update = new { firstName = "Ana", lastName = "Editada", idType = "Cedula", idNumber = member.IdNumber };

        // Entrenador: solo consulta.
        Assert.Equal(HttpStatusCode.OK, (await trainer.GetAsync($"/api/v1/members/{member.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await trainer.PostAsJsonAsync("/api/v1/members", NewMember(branch))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await trainer.PutAsJsonAsync($"/api/v1/members/{member.Id}", update)).StatusCode);

        // Recepción: crea y edita, pero no desactiva.
        Assert.Equal(HttpStatusCode.Created, (await reception.PostAsJsonAsync("/api/v1/members", NewMember(branch))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await reception.PutAsJsonAsync($"/api/v1/members/{member.Id}", update)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reception.PatchAsJsonAsync($"/api/v1/members/{member.Id}/status", new { isActive = false })).StatusCode);

        // Alumno: sin ningún permiso.
        Assert.Equal(HttpStatusCode.Forbidden, (await student.GetAsync("/api/v1/members")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().GetAsync("/api/v1/members")).StatusCode);
    }

    [Fact]
    public async Task Desactivar_y_reactivar_un_alumno_conserva_el_registro_y_queda_auditado()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var branch = await TestApi.CreateBranchAsync(tenant.Owner, "EST");
        var member = await CreateMemberAsync(tenant.Owner, branch);

        var off = await tenant.Owner.PatchAsJsonAsync($"/api/v1/members/{member.Id}/status", new { isActive = false });
        Assert.Equal("Inactive", (await off.Content.ReadFromJsonAsync<MemberDto>())!.Status);
        var inactive = await tenant.Owner.GetFromJsonAsync<PagedDto<MemberDto>>("/api/v1/members?status=Inactive");
        Assert.Equal([member.Id], inactive!.Items.Select(m => m.Id));
        var active = await tenant.Owner.GetFromJsonAsync<PagedDto<MemberDto>>("/api/v1/members?status=Active");
        Assert.Empty(active!.Items);

        var on = await tenant.Owner.PatchAsJsonAsync($"/api/v1/members/{member.Id}/status", new { isActive = true });
        Assert.Equal("Active", (await on.Content.ReadFromJsonAsync<MemberDto>())!.Status);

        var audit = await tenant.Owner.GetFromJsonAsync<PagedDto<AuditDto>>("/api/v1/audit-logs?pageSize=100");
        Assert.Contains(audit!.Items, l => l.Action == "Member.Created" && l.EntityId == member.Id.ToString());
        Assert.Equal(2, audit.Items.Count(l => l.Action == "Member.Updated" && l.EntityId == member.Id.ToString()));
    }

    [Fact]
    public async Task Buscar_por_nombre_documento_codigo_o_telefono_y_filtrar_por_sucursal()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var north = await TestApi.CreateBranchAsync(tenant.Owner, "BNO");
        var south = await TestApi.CreateBranchAsync(tenant.Owner, "BSU");
        var maria = await CreateMemberAsync(tenant.Owner, north, first: "María", last: "Zambrano", phone: "0981112233");
        var jose = await CreateMemberAsync(tenant.Owner, south, first: "José", last: "Lema", phone: "0974445566");

        async Task<Guid[]> SearchAsync(string query)
        {
            var page = await tenant.Owner.GetFromJsonAsync<PagedDto<MemberDto>>($"/api/v1/members?{query}");
            return [.. page!.Items.Select(m => m.Id)];
        }

        Assert.Equal([maria.Id], await SearchAsync("search=zambr"));
        Assert.Equal([jose.Id], await SearchAsync($"search={jose.IdNumber}"));
        Assert.Equal([jose.Id], await SearchAsync($"search={jose.Code}"));
        Assert.Equal([maria.Id], await SearchAsync("search=0981112"));
        Assert.Equal([maria.Id], await SearchAsync("search=mar%C3%ADa%20zamb"));
        Assert.Empty(await SearchAsync("search=%25"));
        Assert.Equal([jose.Id], await SearchAsync($"branchId={south}"));
        Assert.Equal(2, (await SearchAsync(string.Empty)).Length);
    }

    [Fact]
    public async Task Los_codigos_de_alumno_son_unicos_y_consecutivos_aunque_se_creen_en_paralelo()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var branch = await TestApi.CreateBranchAsync(tenant.Owner, "PAR");

        var created = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => CreateMemberAsync(tenant.Owner, branch)));

        var codes = created.Select(m => m.Code).Order().ToArray();
        Assert.Equal(Enumerable.Range(1, 12).Select(n => $"A-{n:D6}"), codes);
    }

    [Fact]
    public async Task Un_alta_fallida_no_consume_numero_de_codigo()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var branch = await TestApi.CreateBranchAsync(tenant.Owner, "GAP");
        var cedula = NewCedula();
        await CreateMemberAsync(tenant.Owner, branch, cedula);

        await tenant.Owner.PostAsJsonAsync("/api/v1/members", NewMember(branch, cedula));
        var next = await CreateMemberAsync(tenant.Owner, branch);

        Assert.Equal("A-000002", next.Code);
    }

    [Fact]
    public async Task Row_Level_Security_y_claves_compuestas_protegen_los_alumnos_aunque_el_codigo_falle()
    {
        var a = await TestApi.NewTenantAsync(api);
        var b = await TestApi.NewTenantAsync(api);
        var branchA = await TestApi.CreateBranchAsync(a.Owner, "RLA");
        var branchB = await TestApi.CreateBranchAsync(b.Owner, "RLB");
        var memberA = await CreateMemberAsync(a.Owner, branchA);
        var memberB = await CreateMemberAsync(b.Owner, branchB);

        await using var connection = new NpgsqlConnection(api.AppConnection);
        await connection.OpenAsync();
        await Exec(connection, $"SELECT set_config('app.tenant_id', '{a.Id}', false)");

        Assert.Equal(1L, await Scalar(connection, "SELECT count(*) FROM members.members"));
        Assert.Equal(0L, await Scalar(connection, $"SELECT count(*) FROM members.members WHERE id = '{memberB.Id}'"));
        Assert.Equal(0, await Exec(connection, $"UPDATE members.members SET first_name = 'x' WHERE id = '{memberB.Id}'"));
        Assert.Equal(0, await Exec(connection, $"DELETE FROM members.members WHERE id = '{memberB.Id}'"));

        // Un alumno de A apuntando a la sucursal de B: pasa RLS pero la FK compuesta lo impide.
        var cross = await Assert.ThrowsAsync<PostgresException>(() => Exec(connection,
            $"UPDATE members.members SET branch_id = '{branchB}' WHERE id = '{memberA.Id}'"));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, cross.SqlState);

        // Escribir una fila de otro tenant viola la política.
        var foreign = await Assert.ThrowsAsync<PostgresException>(() => Exec(connection,
            $"UPDATE members.members SET tenant_id = '{b.Id}' WHERE id = '{memberA.Id}'"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, foreign.SqlState);

        await Exec(connection, "SELECT set_config('app.tenant_id', '', false)");
        Assert.Equal(0L, await Scalar(connection, "SELECT count(*) FROM members.members"));
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
