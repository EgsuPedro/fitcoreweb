using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using FitCore.Api.IntegrationTests.Infrastructure;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

namespace FitCore.Api.IntegrationTests;

/// <summary>Un usuario nunca debe ver ni tocar datos de otro gimnasio, aunque cambie los identificadores.</summary>
[Collection(ApiCollection.Name)]
public class TenantIsolationTests(ApiFixture api)
{
    [Fact]
    public async Task Listados_solo_devuelven_datos_del_propio_tenant()
    {
        var a = await TestApi.NewTenantAsync(api);
        var b = await TestApi.NewTenantAsync(api);
        await TestApi.CreateBranchAsync(a.Owner, "A1");
        await TestApi.CreateBranchAsync(b.Owner, "B1");
        await TestApi.CreateBranchAsync(b.Owner, "B2");

        var branchesA = await a.Owner.GetFromJsonAsync<PagedDto<BranchDto>>("/api/v1/branches");
        var usersA = await a.Owner.GetFromJsonAsync<PagedDto<UserDto>>("/api/v1/users");
        var rolesA = await a.Owner.GetFromJsonAsync<RoleDto[]>("/api/v1/roles");

        Assert.Equal(["A1"], branchesA!.Items.Select(x => x.Code));
        Assert.All(usersA!.Items, u => Assert.EndsWith($"@{a.Slug}.test", u.Email, StringComparison.Ordinal));
        Assert.Equal(8, rolesA!.Length);
    }

    [Fact]
    public async Task Acceder_a_una_sucursal_ajena_por_id_responde_404_no_403()
    {
        var a = await TestApi.NewTenantAsync(api);
        var b = await TestApi.NewTenantAsync(api);
        var branchB = await TestApi.CreateBranchAsync(b.Owner, "B1");

        var get = await a.Owner.GetAsync($"/api/v1/branches/{branchB}");
        var put = await a.Owner.PutAsJsonAsync($"/api/v1/branches/{branchB}",
            new { name = "Robada", timeZone = "America/Guayaquil", isActive = false });

        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);

        var intact = await b.Owner.GetFromJsonAsync<BranchDto>($"/api/v1/branches/{branchB}");
        Assert.True(intact!.IsActive);
        Assert.Equal("Sucursal B1", intact.Name);
    }

    [Fact]
    public async Task Acceder_a_un_usuario_ajeno_por_id_responde_404_en_lectura_y_escritura()
    {
        var a = await TestApi.NewTenantAsync(api);
        var b = await TestApi.NewTenantAsync(api);
        var branchB = await TestApi.CreateBranchAsync(b.Owner);
        var userB = await TestApi.CreateUserAsync(b.Owner, "alguien@b.test", "Recepcion", branchB);

        Assert.Equal(HttpStatusCode.NotFound, (await a.Owner.GetAsync($"/api/v1/users/{userB.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await a.Owner.PutAsJsonAsync($"/api/v1/users/{userB.Id}/status", new { isActive = false })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await a.Owner.PutAsJsonAsync($"/api/v1/users/{userB.Id}/access", new { roleIds = Array.Empty<Guid>(), branchIds = Array.Empty<Guid>() })).StatusCode);
    }

    [Fact]
    public async Task No_se_puede_asignar_sucursales_o_roles_de_otro_tenant_al_crear_un_usuario()
    {
        var a = await TestApi.NewTenantAsync(api);
        var b = await TestApi.NewTenantAsync(api);
        var branchB = await TestApi.CreateBranchAsync(b.Owner);
        var roleB = await TestApi.RoleIdAsync(b.Owner, "Recepcion");
        var roleA = await TestApi.RoleIdAsync(a.Owner, "Recepcion");
        var branchA = await TestApi.CreateBranchAsync(a.Owner);

        var withForeignBranch = await a.Owner.PostAsJsonAsync("/api/v1/users", new
        {
            email = "x@a.test", fullName = "X", password = TestApi.UserPassword, roleIds = new[] { roleA }, branchIds = new[] { branchB }
        });
        var withForeignRole = await a.Owner.PostAsJsonAsync("/api/v1/users", new
        {
            email = "y@a.test", fullName = "Y", password = TestApi.UserPassword, roleIds = new[] { roleB }, branchIds = new[] { branchA }
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, withForeignBranch.StatusCode);
        Assert.Equal("users.branch_not_found", await TestApi.ProblemCodeAsync(withForeignBranch));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, withForeignRole.StatusCode);
        Assert.Equal("users.role_not_found", await TestApi.ProblemCodeAsync(withForeignRole));
    }

    [Fact]
    public async Task La_auditoria_solo_muestra_eventos_del_propio_tenant()
    {
        var a = await TestApi.NewTenantAsync(api);
        var b = await TestApi.NewTenantAsync(api);
        var branchB = await TestApi.CreateBranchAsync(b.Owner, "B1");

        var logsA = await a.Owner.GetFromJsonAsync<PagedDto<AuditDto>>("/api/v1/audit-logs?pageSize=100");

        Assert.DoesNotContain(logsA!.Items, l => l.EntityId == branchB.ToString());
        Assert.Contains(logsA.Items, l => l.Action == "Auth.LoginSucceeded");
    }

    [Fact]
    public async Task Un_token_firmado_con_otra_clave_o_con_tenant_alterado_se_rechaza()
    {
        var a = await TestApi.NewTenantAsync(api);
        var b = await TestApi.NewTenantAsync(api);
        var ownerId = await OwnerIdAsync(a);

        var forgedKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("otra-clave-otra-clave-otra-clave-otra-clave-1234"));
        var token = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            "fitcore360",
            "fitcore360-clients",
            [new Claim("sub", ownerId.ToString()), new Claim("tid", b.Id.ToString())],
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(forgedKey, SecurityAlgorithms.HmacSha256)));

        using var forged = TestApi.Authenticated(api, token);

        Assert.Equal(HttpStatusCode.Unauthorized, (await forged.GetAsync("/api/v1/branches")).StatusCode);
    }

    [Fact]
    public async Task Row_Level_Security_oculta_filas_ajenas_y_bloquea_escrituras_cruzadas_aunque_el_codigo_falle()
    {
        var a = await TestApi.NewTenantAsync(api);
        var b = await TestApi.NewTenantAsync(api);
        var branchA = await TestApi.CreateBranchAsync(a.Owner, "RLS-A");
        var branchB = await TestApi.CreateBranchAsync(b.Owner, "RLS-B");

        await using var connection = new NpgsqlConnection(api.AppConnection);
        await connection.OpenAsync();

        await SetTenantAsync(connection, a.Id);
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT count(*) FROM organizations.branches"));
        Assert.Equal(0L, await ScalarAsync(connection, $"SELECT count(*) FROM organizations.branches WHERE id = '{branchB}'"));

        // Sin tenant activo no se ve nada.
        await SetTenantAsync(connection, null);
        Assert.Equal(0L, await ScalarAsync(connection, "SELECT count(*) FROM organizations.branches"));
        Assert.Equal(0L, await ScalarAsync(connection, "SELECT count(*) FROM identity.app_users"));

        // Escribir una fila de otro tenant viola la política (WITH CHECK).
        await SetTenantAsync(connection, a.Id);
        var insertForeign = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection,
            $"INSERT INTO organizations.branches (id, tenant_id, code, name, time_zone, is_active, created_at) " +
            $"VALUES (gen_random_uuid(), '{b.Id}', 'HACK', 'Hack', 'UTC', true, now())"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, insertForeign.SqlState);

        // Modificar o borrar filas ajenas no afecta a ninguna fila.
        Assert.Equal(0, await ExecuteAsync(connection, $"UPDATE organizations.branches SET name = 'x' WHERE id = '{branchB}'"));
        Assert.Equal(0, await ExecuteAsync(connection, $"DELETE FROM organizations.branches WHERE id = '{branchB}'"));
        Assert.Equal(1, await ExecuteAsync(connection, $"UPDATE organizations.branches SET name = 'propia' WHERE id = '{branchA}'"));
    }

    [Fact]
    public async Task Las_claves_foraneas_compuestas_impiden_relacionar_filas_de_tenants_distintos()
    {
        var a = await TestApi.NewTenantAsync(api);
        var b = await TestApi.NewTenantAsync(api);
        var branchB = await TestApi.CreateBranchAsync(b.Owner, "FK-B");
        var ownerA = await OwnerIdAsync(a);

        await using var connection = new NpgsqlConnection(api.AppConnection);
        await connection.OpenAsync();
        await SetTenantAsync(connection, a.Id);

        // Usuario de A con la sucursal de B: pasa RLS (tenant A) pero la FK (tenant_id, branch_id) no existe.
        var crossLink = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection,
            $"INSERT INTO identity.user_branches (tenant_id, user_id, branch_id) VALUES ('{a.Id}', '{ownerA}', '{branchB}')"));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, crossLink.SqlState);
    }

    [Fact]
    public async Task El_rol_de_la_aplicacion_no_es_superusuario_ni_omite_RLS_y_la_auditoria_es_append_only()
    {
        await using var connection = new NpgsqlConnection(api.AppConnection);
        await connection.OpenAsync();

        await using (var command = new NpgsqlCommand("SELECT rolsuper, rolbypassrls FROM pg_roles WHERE rolname = current_user", connection))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.False(reader.GetBoolean(0));
            Assert.False(reader.GetBoolean(1));
        }

        var a = await TestApi.NewTenantAsync(api);
        await SetTenantAsync(connection, a.Id);
        Assert.True(await ScalarAsync(connection, "SELECT count(*) FROM audit.audit_logs") > 0);

        var update = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection, "UPDATE audit.audit_logs SET action = 'x'"));
        var delete = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection, "DELETE FROM audit.audit_logs"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, update.SqlState);
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, delete.SqlState);
    }

    private static async Task<Guid> OwnerIdAsync(TestTenant tenant)
    {
        var users = await tenant.Owner.GetFromJsonAsync<PagedDto<UserDto>>("/api/v1/users");
        return users!.Items.Single(u => u.Email == tenant.OwnerEmail).Id;
    }

    private static async Task SetTenantAsync(NpgsqlConnection connection, Guid? tenantId) =>
        await ExecuteAsync(connection, $"SELECT set_config('app.tenant_id', '{tenantId}', false)");

    private static async Task<int> ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
