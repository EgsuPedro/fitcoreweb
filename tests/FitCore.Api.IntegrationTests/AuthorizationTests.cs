using System.Net;
using System.Net.Http.Json;
using FitCore.Api.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace FitCore.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
public class AuthorizationTests(ApiFixture api)
{
    [Fact]
    public void Todo_endpoint_de_la_API_declara_explicitamente_su_autorizacion()
    {
        var endpoints = api.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api/v1", StringComparison.Ordinal) == true)
            .ToList();

        Assert.NotEmpty(endpoints);
        var undeclared = endpoints
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is null && e.Metadata.GetOrderedMetadata<IAuthorizeData>().Count == 0)
            .Select(e => $"{string.Join(',', e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])} {e.RoutePattern.RawText}")
            .ToList();

        Assert.True(undeclared.Count == 0, $"Endpoints sin AllowAnonymous ni autorización explícita: {string.Join("; ", undeclared)}");
    }

    [Fact]
    public async Task Los_endpoints_de_negocio_exigen_un_permiso_concreto_no_solo_sesion()
    {
        var endpoints = api.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api/v1", StringComparison.Ordinal) == true)
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is null)
            .ToList();

        // Solo estos pueden exigir únicamente sesión: operan sobre el propio usuario.
        var sessionOnly = new[] { "/api/v1/auth/change-password", "/api/v1/auth/me" };
        var weak = endpoints
            .Where(e => !sessionOnly.Contains(e.RoutePattern.RawText))
            .Where(e => !e.Metadata.GetOrderedMetadata<IAuthorizeData>().Any(a => a.Policy?.StartsWith("perm:", StringComparison.Ordinal) == true))
            .Select(e => e.RoutePattern.RawText)
            .ToList();

        Assert.True(weak.Count == 0, $"Endpoints sin permiso específico: {string.Join("; ", weak)}");
        await Task.CompletedTask;
    }

    [Theory]
    [InlineData("/api/v1/branches")]
    [InlineData("/api/v1/users")]
    [InlineData("/api/v1/roles")]
    [InlineData("/api/v1/permissions")]
    [InlineData("/api/v1/audit-logs")]
    [InlineData("/api/v1/organization")]
    [InlineData("/api/v1/platform/tenants")]
    [InlineData("/api/v1/auth/me")]
    public async Task Sin_token_todo_responde_401(string path)
    {
        using var client = api.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task Un_propietario_de_gimnasio_no_puede_usar_la_administracion_de_plataforma()
    {
        var tenant = await TestApi.NewTenantAsync(api);

        Assert.Equal(HttpStatusCode.Forbidden, (await tenant.Owner.GetAsync("/api/v1/platform/tenants")).StatusCode);
        var create = await tenant.Owner.PostAsJsonAsync("/api/v1/platform/tenants", new
        {
            slug = "intruso", name = "Intruso", timeZone = "America/Guayaquil", locale = "es-EC",
            ownerEmail = "i@i.test", ownerFullName = "I", ownerPassword = TestApi.OwnerPassword
        });
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
    }

    [Fact]
    public async Task Recepcion_puede_consultar_pero_no_crear_ni_administrar()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var branch = await TestApi.CreateBranchAsync(tenant.Owner);
        var user = await TestApi.CreateUserAsync(tenant.Owner, "recepcion@gym.test", "Recepcion", branch);
        using var recepcion = TestApi.Authenticated(api, await TestApi.LoginAsync(api, tenant.Slug, user.Email, TestApi.UserPassword));

        Assert.Equal(HttpStatusCode.OK, (await recepcion.GetAsync("/api/v1/branches")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await recepcion.GetAsync("/api/v1/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await recepcion.GetAsync("/api/v1/audit-logs")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await recepcion.PostAsJsonAsync("/api/v1/branches",
            new { code = "X", name = "X", timeZone = "America/Guayaquil" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await recepcion.PutAsJsonAsync("/api/v1/organization",
            new { name = "Hack", timeZone = "America/Guayaquil", locale = "es-EC" })).StatusCode);
    }

    [Fact]
    public async Task Administrador_de_sucursal_solo_ve_y_administra_su_sucursal()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var north = await TestApi.CreateBranchAsync(tenant.Owner, "NORTE");
        var south = await TestApi.CreateBranchAsync(tenant.Owner, "SUR");
        var admin = await TestApi.CreateUserAsync(tenant.Owner, "admin.norte@gym.test", "AdministradorSucursal", north);
        var southUser = await TestApi.CreateUserAsync(tenant.Owner, "recep.sur@gym.test", "Recepcion", south);
        using var adminClient = TestApi.Authenticated(api, await TestApi.LoginAsync(api, tenant.Slug, admin.Email, TestApi.UserPassword));

        // Solo ve su sucursal; la otra es "inexistente" para él.
        var branches = await adminClient.GetFromJsonAsync<PagedDto<BranchDto>>("/api/v1/branches");
        Assert.Equal(["NORTE"], branches!.Items.Select(b => b.Code));
        Assert.Equal(HttpStatusCode.NotFound, (await adminClient.GetAsync($"/api/v1/branches/{south}")).StatusCode);

        // Editar sucursales no forma parte de su rol (solo consulta), sea cual sea la sucursal.
        Assert.Equal(HttpStatusCode.Forbidden, (await adminClient.PutAsJsonAsync($"/api/v1/branches/{south}",
            new { name = "x", timeZone = "America/Guayaquil", isActive = false })).StatusCode);

        // Solo ve a los usuarios de su sucursal.
        var users = await adminClient.GetFromJsonAsync<PagedDto<UserDto>>("/api/v1/users");
        Assert.Contains(users!.Items, u => u.Id == admin.Id);
        Assert.DoesNotContain(users.Items, u => u.Id == southUser.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await adminClient.GetAsync($"/api/v1/users/{southUser.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await adminClient.GetAsync($"/api/v1/users?branchId={south}")).StatusCode);

        // No puede crear sucursales (no tiene el permiso) ni usuarios en otra sucursal.
        Assert.Equal(HttpStatusCode.Forbidden, (await adminClient.PostAsJsonAsync("/api/v1/branches",
            new { code = "NUEVA", name = "Nueva", timeZone = "America/Guayaquil" })).StatusCode);
        var roleId = await TestApi.RoleIdAsync(adminClient, "Recepcion");
        var foreignBranchUser = await adminClient.PostAsJsonAsync("/api/v1/users", new
        {
            email = "intruso@gym.test", fullName = "I", password = TestApi.UserPassword, roleIds = new[] { roleId }, branchIds = new[] { south }
        });
        Assert.Equal(HttpStatusCode.Forbidden, foreignBranchUser.StatusCode);
        Assert.Equal("branches.not_allowed", await TestApi.ProblemCodeAsync(foreignBranchUser));

        // Sí puede crear usuarios en su propia sucursal.
        var created = await TestApi.CreateUserAsync(adminClient, "nuevo.norte@gym.test", "Recepcion", north);
        Assert.Equal([north], created.BranchIds);
    }

    [Fact]
    public async Task Cabecera_X_Branch_Id_se_valida_contra_las_sucursales_autorizadas()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var north = await TestApi.CreateBranchAsync(tenant.Owner, "NORTE");
        var south = await TestApi.CreateBranchAsync(tenant.Owner, "SUR");
        var admin = await TestApi.CreateUserAsync(tenant.Owner, "admin@gym.test", "AdministradorSucursal", north);
        using var adminClient = TestApi.Authenticated(api, await TestApi.LoginAsync(api, tenant.Slug, admin.Email, TestApi.UserPassword));

        using var own = new HttpRequestMessage(HttpMethod.Get, "/api/v1/branches");
        own.Headers.Add("X-Branch-Id", north.ToString());
        using var foreign = new HttpRequestMessage(HttpMethod.Get, "/api/v1/branches");
        foreign.Headers.Add("X-Branch-Id", south.ToString());
        using var invalid = new HttpRequestMessage(HttpMethod.Get, "/api/v1/branches");
        invalid.Headers.Add("X-Branch-Id", "no-es-guid");

        Assert.Equal(HttpStatusCode.OK, (await adminClient.SendAsync(own)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await adminClient.SendAsync(foreign)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await adminClient.SendAsync(invalid)).StatusCode);
    }

    [Fact]
    public async Task No_se_puede_escalar_privilegios_asignando_roles_mayores_ni_editando_el_propio_acceso()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var north = await TestApi.CreateBranchAsync(tenant.Owner, "NORTE");
        var admin = await TestApi.CreateUserAsync(tenant.Owner, "admin@gym.test", "AdministradorSucursal", north);
        using var adminClient = TestApi.Authenticated(api, await TestApi.LoginAsync(api, tenant.Slug, admin.Email, TestApi.UserPassword));

        var ownerRole = await TestApi.RoleIdAsync(adminClient, "Propietario");
        var accountantRole = await TestApi.RoleIdAsync(adminClient, "Contador");

        // Roles con permisos que el administrador no posee, o que abarcan todas las sucursales.
        var createOwner = await adminClient.PostAsJsonAsync("/api/v1/users", new
        {
            email = "otro.dueno@gym.test", fullName = "O", password = TestApi.UserPassword, roleIds = new[] { ownerRole }, branchIds = new[] { north }
        });
        var createAccountant = await adminClient.PostAsJsonAsync("/api/v1/users", new
        {
            email = "contador@gym.test", fullName = "C", password = TestApi.UserPassword, roleIds = new[] { accountantRole }, branchIds = new[] { north }
        });
        Assert.Equal(HttpStatusCode.Forbidden, createOwner.StatusCode);
        Assert.Equal("users.role_not_assignable", await TestApi.ProblemCodeAsync(createOwner));
        Assert.Equal(HttpStatusCode.Forbidden, createAccountant.StatusCode);

        // No puede darse más acceso a sí mismo.
        var self = await adminClient.PutAsJsonAsync($"/api/v1/users/{admin.Id}/access",
            new { roleIds = new[] { ownerRole }, branchIds = new[] { north } });
        Assert.Equal(HttpStatusCode.Forbidden, self.StatusCode);
        Assert.Equal("users.cannot_edit_self", await TestApi.ProblemCodeAsync(self));

        // El propietario ni siquiera es visible para él (no comparte sucursal).
        var users = await tenant.Owner.GetFromJsonAsync<PagedDto<UserDto>>("/api/v1/users");
        var owner = users!.Items.Single(u => u.Email == tenant.OwnerEmail);
        Assert.Equal(HttpStatusCode.NotFound,
            (await adminClient.PutAsJsonAsync($"/api/v1/users/{owner.Id}/status", new { isActive = false })).StatusCode);

        // Si un usuario más privilegiado comparte su sucursal, lo ve pero no puede modificarlo.
        var coOwner = await TestApi.CreateUserAsync(tenant.Owner, "co.dueno@gym.test", "Propietario", north);
        Assert.Equal(HttpStatusCode.OK, (await adminClient.GetAsync($"/api/v1/users/{coOwner.Id}")).StatusCode);
        var deactivate = await adminClient.PutAsJsonAsync($"/api/v1/users/{coOwner.Id}/status", new { isActive = false });
        Assert.Equal(HttpStatusCode.Forbidden, deactivate.StatusCode);
        Assert.Equal("users.role_not_assignable", await TestApi.ProblemCodeAsync(deactivate));
        var demote = await adminClient.PutAsJsonAsync($"/api/v1/users/{coOwner.Id}/access",
            new { roleIds = Array.Empty<Guid>(), branchIds = new[] { north } });
        Assert.Equal(HttpStatusCode.Forbidden, demote.StatusCode);
    }

    [Fact]
    public async Task Validacion_devuelve_422_con_detalle_por_campo()
    {
        var tenant = await TestApi.NewTenantAsync(api);

        var response = await tenant.Owner.PostAsJsonAsync("/api/v1/branches",
            new { code = "con espacios", name = "", timeZone = "Marte/Olimpo", sriEstablishmentCode = "12" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("validation.failed", await TestApi.ProblemCodeAsync(response));
        var body = await response.Content.ReadAsStringAsync();
        foreach (var field in new[] { "code", "name", "timeZone", "sriEstablishmentCode" })
        {
            Assert.Contains($"\"{field}\"", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Paginacion_respeta_el_tamano_maximo()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        for (var i = 0; i < 3; i++)
        {
            await TestApi.CreateBranchAsync(tenant.Owner, $"P{i}");
        }

        var page = await tenant.Owner.GetFromJsonAsync<PagedDto<BranchDto>>("/api/v1/branches?page=2&pageSize=2");
        var huge = await tenant.Owner.GetFromJsonAsync<PagedDto<BranchDto>>("/api/v1/branches?pageSize=100000");

        Assert.Equal(3, page!.TotalCount);
        Assert.Single(page.Items);
        Assert.Equal(100, huge!.PageSize);
    }
}
