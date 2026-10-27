using System.Net;
using System.Net.Http.Json;
using FitCore.Api.IntegrationTests.Infrastructure;
using FitCore.Platform.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FitCore.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
public class RoleManagementTests(ApiFixture api)
{
    private static Task<HttpResponseMessage> CreateRole(HttpClient client, string name, bool allBranches = false, params string[] permissions) =>
        client.PostAsJsonAsync("/api/v1/roles", new { name, description = $"Rol {name}", grantsAllBranches = allBranches, permissions });

    [Fact]
    public async Task Un_rol_propio_cambia_lo_que_puede_hacer_el_usuario_de_inmediato()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var branch = await TestApi.CreateBranchAsync(tenant.Owner);

        var created = await CreateRole(tenant.Owner, "Instructor", false, "branches.read");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var role = (await created.Content.ReadFromJsonAsync<RoleDto>())!;

        var user = (await (await tenant.Owner.PostAsJsonAsync("/api/v1/users", new
        {
            email = "instructor@gym.test", fullName = "Instructor", password = TestApi.UserPassword,
            roleIds = new[] { role.Id }, branchIds = new[] { branch }
        })).Content.ReadFromJsonAsync<UserDto>())!;
        using var userClient = TestApi.Authenticated(api, await TestApi.LoginAsync(api, tenant.Slug, user.Email, TestApi.UserPassword));

        Assert.Equal(HttpStatusCode.OK, (await userClient.GetAsync("/api/v1/branches")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await userClient.GetAsync("/api/v1/users")).StatusCode);

        // Se amplía el rol: el mismo token del usuario gana el permiso sin volver a iniciar sesión.
        var grant = await tenant.Owner.PutAsJsonAsync($"/api/v1/roles/{role.Id}", new
        {
            name = "Instructor", description = "Con usuarios", grantsAllBranches = false, permissions = new[] { "branches.read", "users.read" }
        });
        Assert.Equal(HttpStatusCode.OK, grant.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await userClient.GetAsync("/api/v1/users")).StatusCode);

        // Y se revoca igual de rápido.
        await tenant.Owner.PutAsJsonAsync($"/api/v1/roles/{role.Id}", new
        {
            name = "Instructor", description = "Sin usuarios", grantsAllBranches = false, permissions = new[] { "branches.read" }
        });
        Assert.Equal(HttpStatusCode.Forbidden, (await userClient.GetAsync("/api/v1/users")).StatusCode);
    }

    [Fact]
    public async Task Los_cambios_de_roles_quedan_auditados()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var role = (await (await CreateRole(tenant.Owner, "Auditado", false, "branches.read")).Content.ReadFromJsonAsync<RoleDto>())!;
        await tenant.Owner.PutAsJsonAsync($"/api/v1/roles/{role.Id}", new
        {
            name = "Auditado", description = "x", grantsAllBranches = false, permissions = new[] { "branches.read", "users.read" }
        });

        var logs = await tenant.Owner.GetFromJsonAsync<PagedDto<AuditDto>>("/api/v1/audit-logs?pageSize=100");

        Assert.Contains(logs!.Items, l => l.Action == "AppRole.Created" && l.EntityId == role.Id.ToString());
        Assert.Contains(logs.Items, l => l.Action == "RolePermission.Created" && l.EntityId == $"{role.Id}|users.read");
    }

    [Fact]
    public async Task Los_roles_de_plantilla_se_pueden_ajustar_pero_no_renombrar_ni_eliminar()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var cashier = await TestApi.RoleIdAsync(tenant.Owner, "Cajero");

        var adjust = await tenant.Owner.PutAsJsonAsync($"/api/v1/roles/{cashier}", new
        {
            name = "Cajero", description = "Ajustado", grantsAllBranches = false, permissions = new[] { "branches.read", "organization.read", "users.read" }
        });
        var rename = await tenant.Owner.PutAsJsonAsync($"/api/v1/roles/{cashier}", new
        {
            name = "Cajero Pro", description = "x", grantsAllBranches = false, permissions = Array.Empty<string>()
        });
        var delete = await tenant.Owner.DeleteAsync($"/api/v1/roles/{cashier}");

        Assert.Equal(HttpStatusCode.OK, adjust.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, rename.StatusCode);
        Assert.Equal("roles.system_protected", await TestApi.ProblemCodeAsync(rename));
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
    }

    [Fact]
    public async Task El_rol_Propietario_es_intocable()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var owner = await TestApi.RoleIdAsync(tenant.Owner, "Propietario");

        var update = await tenant.Owner.PutAsJsonAsync($"/api/v1/roles/{owner}", new
        {
            name = "Propietario", description = "x", grantsAllBranches = true, permissions = Array.Empty<string>()
        });
        var delete = await tenant.Owner.DeleteAsync($"/api/v1/roles/{owner}");

        Assert.Equal(HttpStatusCode.Forbidden, update.StatusCode);
        Assert.Equal("roles.owner_protected", await TestApi.ProblemCodeAsync(update));
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
    }

    [Fact]
    public async Task Eliminar_un_rol_en_uso_se_rechaza_y_uno_libre_se_elimina()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var branch = await TestApi.CreateBranchAsync(tenant.Owner);
        var role = (await (await CreateRole(tenant.Owner, "Temporal", false, "branches.read")).Content.ReadFromJsonAsync<RoleDto>())!;
        var user = (await (await tenant.Owner.PostAsJsonAsync("/api/v1/users", new
        {
            email = "temporal@gym.test", fullName = "T", password = TestApi.UserPassword,
            roleIds = new[] { role.Id }, branchIds = new[] { branch }
        })).Content.ReadFromJsonAsync<UserDto>())!;

        var inUse = await tenant.Owner.DeleteAsync($"/api/v1/roles/{role.Id}");
        Assert.Equal(HttpStatusCode.Conflict, inUse.StatusCode);
        Assert.Equal("roles.in_use", await TestApi.ProblemCodeAsync(inUse));

        await tenant.Owner.PutAsJsonAsync($"/api/v1/users/{user.Id}/access", new { roleIds = Array.Empty<Guid>(), branchIds = new[] { branch } });
        var deleted = await tenant.Owner.DeleteAsync($"/api/v1/roles/{role.Id}");

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        var roles = await tenant.Owner.GetFromJsonAsync<RoleDto[]>("/api/v1/roles");
        Assert.DoesNotContain(roles!, r => r.Id == role.Id);
    }

    [Fact]
    public async Task No_se_pueden_conceder_permisos_de_plataforma_ni_inexistentes()
    {
        var tenant = await TestApi.NewTenantAsync(api);

        var platform = await CreateRole(tenant.Owner, "Platform", false, "platform.tenants.manage");
        var unknown = await CreateRole(tenant.Owner, "Inventado", false, "inventado.todo");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, platform.StatusCode);
        Assert.Equal("roles.permission_not_found", await TestApi.ProblemCodeAsync(platform));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);
    }

    [Fact]
    public async Task Quien_gestiona_roles_no_puede_conceder_mas_de_lo_que_tiene()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var branch = await TestApi.CreateBranchAsync(tenant.Owner);

        // Un "gestor de roles" limitado: puede gestionar roles, pero solo posee estos permisos.
        var managerRole = (await (await CreateRole(tenant.Owner, "GestorRoles", false, "roles.manage", "roles.read", "branches.read"))
            .Content.ReadFromJsonAsync<RoleDto>())!;
        var manager = (await (await tenant.Owner.PostAsJsonAsync("/api/v1/users", new
        {
            email = "gestor@gym.test", fullName = "Gestor", password = TestApi.UserPassword,
            roleIds = new[] { managerRole.Id }, branchIds = new[] { branch }
        })).Content.ReadFromJsonAsync<UserDto>())!;
        using var managerClient = TestApi.Authenticated(api, await TestApi.LoginAsync(api, tenant.Slug, manager.Email, TestApi.UserPassword));

        var withUnheld = await CreateRole(managerClient, "ConMas", false, "users.create");
        var allBranches = await CreateRole(managerClient, "Global", true, "branches.read");
        var ok = await CreateRole(managerClient, "Menos", false, "branches.read");

        Assert.Equal(HttpStatusCode.Forbidden, withUnheld.StatusCode);
        Assert.Equal("roles.permission_not_grantable", await TestApi.ProblemCodeAsync(withUnheld));
        Assert.Equal(HttpStatusCode.Forbidden, allBranches.StatusCode);
        Assert.Equal("roles.all_branches_not_grantable", await TestApi.ProblemCodeAsync(allBranches));
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);

        // Tampoco puede tocar roles con más alcance que el suyo.
        var accountant = await TestApi.RoleIdAsync(managerClient, "Contador");
        var tamper = await managerClient.PutAsJsonAsync($"/api/v1/roles/{accountant}", new
        {
            name = "Contador", description = "x", grantsAllBranches = false, permissions = new[] { "branches.read" }
        });
        Assert.Equal(HttpStatusCode.Forbidden, tamper.StatusCode);
        Assert.Equal("roles.not_manageable", await TestApi.ProblemCodeAsync(tamper));
    }

    [Fact]
    public async Task Sin_el_permiso_roles_manage_no_se_gestionan_roles()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var branch = await TestApi.CreateBranchAsync(tenant.Owner);
        var admin = await TestApi.CreateUserAsync(tenant.Owner, "admin@gym.test", "AdministradorSucursal", branch);
        using var adminClient = TestApi.Authenticated(api, await TestApi.LoginAsync(api, tenant.Slug, admin.Email, TestApi.UserPassword));

        Assert.Equal(HttpStatusCode.Forbidden, (await CreateRole(adminClient, "Nuevo", false, "branches.read")).StatusCode);
    }

    [Fact]
    public async Task Los_roles_de_otro_gimnasio_no_se_pueden_ver_editar_ni_borrar()
    {
        var a = await TestApi.NewTenantAsync(api);
        var b = await TestApi.NewTenantAsync(api);
        var roleB = (await (await CreateRole(b.Owner, "SoloB", false, "branches.read")).Content.ReadFromJsonAsync<RoleDto>())!;

        var update = await a.Owner.PutAsJsonAsync($"/api/v1/roles/{roleB.Id}", new
        {
            name = "SoloB", description = "hack", grantsAllBranches = false, permissions = new[] { "branches.read", "users.read" }
        });
        var delete = await a.Owner.DeleteAsync($"/api/v1/roles/{roleB.Id}");

        Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
        var intact = (await b.Owner.GetFromJsonAsync<RoleDto[]>("/api/v1/roles"))!.Single(r => r.Id == roleB.Id);
        Assert.Equal(["branches.read"], intact.Permissions);
    }

    [Fact]
    public async Task Nombre_repetido_o_invalido_se_rechaza()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        await CreateRole(tenant.Owner, "Unico", false, "branches.read");

        var duplicate = await CreateRole(tenant.Owner, "unico", false, "branches.read");
        var empty = await CreateRole(tenant.Owner, "", false, "branches.read");

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("roles.name_taken", await TestApi.ProblemCodeAsync(duplicate));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, empty.StatusCode);
    }

    [Fact]
    public async Task Al_arrancar_el_Propietario_de_gimnasios_existentes_recupera_los_permisos_nuevos()
    {
        var tenant = await TestApi.NewTenantAsync(api);

        // Simula un gimnasio creado antes de que existiera roles.manage.
        await using (var connection = new NpgsqlConnection(api.AdminConnection))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "DELETE FROM identity.role_permissions WHERE tenant_id = @t AND permission_code = 'roles.manage'", connection);
            command.Parameters.AddWithValue("t", tenant.Id);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await CreateRole(tenant.Owner, "SinPermiso", false, "branches.read")).StatusCode);

        await DatabaseInitializer.InitializeAsync(api.Services, api.Services.GetRequiredService<IConfiguration>());

        Assert.Equal(HttpStatusCode.Created, (await CreateRole(tenant.Owner, "ConPermiso", false, "branches.read")).StatusCode);
    }
}
