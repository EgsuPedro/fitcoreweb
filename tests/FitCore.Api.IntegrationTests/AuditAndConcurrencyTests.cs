using System.Net;
using System.Net.Http.Json;
using FitCore.Api.IntegrationTests.Infrastructure;
using FitCore.Platform.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FitCore.Api.IntegrationTests;

[Collection(ApiCollection2.Name)]
public class AuditAndConcurrencyTests(ApiFixture api)
{
    [Fact]
    public async Task Crear_y_editar_una_sucursal_queda_auditado_con_antes_y_despues()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var branch = await TestApi.CreateBranchAsync(tenant.Owner, "AUD");
        await tenant.Owner.PutAsJsonAsync($"/api/v1/branches/{branch}",
            new { name = "Nombre nuevo", timeZone = "America/Guayaquil", sriEstablishmentCode = "002", isActive = true });

        var logs = await tenant.Owner.GetFromJsonAsync<PagedDto<AuditDto>>($"/api/v1/audit-logs?entity=Branch&entityId={branch}");
        var actions = logs!.Items.Select(l => l.Action).ToList();
        Assert.Contains("Branch.Created", actions);
        Assert.Contains("Branch.Updated", actions);

        var raw = await tenant.Owner.GetStringAsync($"/api/v1/audit-logs?entity=Branch&entityId={branch}");
        Assert.Contains("Nombre nuevo", raw, StringComparison.Ordinal);
        Assert.Contains("Sucursal AUD", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task La_auditoria_no_guarda_hashes_de_clave_ni_valores_secretos()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var branch = await TestApi.CreateBranchAsync(tenant.Owner);
        await TestApi.CreateUserAsync(tenant.Owner, "secreto@gym.test", "Recepcion", branch);

        var raw = await tenant.Owner.GetStringAsync("/api/v1/audit-logs?entity=AppUser&pageSize=100");

        Assert.Contains("[redacted]", raw, StringComparison.Ordinal);
        Assert.DoesNotContain(TestApi.UserPassword, raw, StringComparison.Ordinal);
        Assert.DoesNotContain("AQAAAA", raw, StringComparison.Ordinal); // prefijo de los hashes de ASP.NET Identity
    }

    [Fact]
    public async Task Los_intentos_de_login_fallidos_quedan_registrados_en_la_auditoria_del_tenant()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        using var client = api.CreateClient();
        await client.PostAsJsonAsync("/api/v1/auth/login",
            new { tenantSlug = tenant.Slug, email = tenant.OwnerEmail, password = "Incorrecta12345" });

        var logs = await tenant.Owner.GetFromJsonAsync<PagedDto<AuditDto>>("/api/v1/audit-logs?entity=AppUser&pageSize=100");

        Assert.Contains(logs!.Items, l => l.Action == "Auth.LoginFailed");
    }

    [Fact]
    public async Task Crear_un_gimnasio_publica_el_evento_en_la_outbox_y_el_procesador_lo_marca_como_procesado()
    {
        var tenant = await TestApi.NewTenantAsync(api);

        await using (var connection = new NpgsqlConnection(api.AdminConnection))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM messaging.outbox_messages WHERE tenant_id = @t AND type LIKE '%TenantCreated'", connection);
            command.Parameters.AddWithValue("t", tenant.Id);
            Assert.Equal(1L, Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
        }

        var processor = new OutboxProcessor(
            api.Services.GetRequiredService<IServiceScopeFactory>(),
            api.Services.GetRequiredService<FitCore.SharedKernel.IClock>(),
            api.Services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<OutboxProcessor>>());

        // El procesador toma lotes de 20: se repite hasta vaciar la cola (otras pruebas también generan eventos).
        for (var batches = 0; batches < 50 && await processor.ProcessBatchAsync(CancellationToken.None) > 0; batches++)
        {
        }

        await using var check = new NpgsqlConnection(api.AdminConnection);
        await check.OpenAsync();
        await using var pending = new NpgsqlCommand(
            "SELECT count(*) FROM messaging.outbox_messages WHERE tenant_id = @t AND processed_at IS NULL", check);
        pending.Parameters.AddWithValue("t", tenant.Id);
        Assert.Equal(0L, Convert.ToInt64(await pending.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Crear_la_misma_sucursal_en_paralelo_produce_un_solo_registro_y_el_resto_recibe_409()
    {
        var tenant = await TestApi.NewTenantAsync(api);

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            tenant.Owner.PostAsJsonAsync("/api/v1/branches",
                new { code = "RACE", name = "Carrera", timeZone = "America/Guayaquil" })));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.Created), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        var list = await tenant.Owner.GetFromJsonAsync<PagedDto<BranchDto>>("/api/v1/branches?search=RACE");
        Assert.Single(list!.Items);
    }

    [Fact]
    public async Task Crear_el_mismo_correo_en_paralelo_produce_un_solo_usuario()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var branch = await TestApi.CreateBranchAsync(tenant.Owner);
        var roleId = await TestApi.RoleIdAsync(tenant.Owner, "Recepcion");

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            tenant.Owner.PostAsJsonAsync("/api/v1/users", new
            {
                email = "duplicado@gym.test", fullName = "Dup", password = TestApi.UserPassword, roleIds = new[] { roleId }, branchIds = new[] { branch }
            })));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.Created), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
    }

    [Fact]
    public async Task El_mismo_codigo_de_sucursal_puede_existir_en_tenants_distintos()
    {
        var a = await TestApi.NewTenantAsync(api);
        var b = await TestApi.NewTenantAsync(api);

        await TestApi.CreateBranchAsync(a.Owner, "MATRIZ");
        await TestApi.CreateBranchAsync(b.Owner, "MATRIZ");
        var duplicate = await a.Owner.PostAsJsonAsync("/api/v1/branches",
            new { code = "matriz", name = "Otra", timeZone = "America/Guayaquil" });

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("branches.code_taken", await TestApi.ProblemCodeAsync(duplicate));
    }

    [Fact]
    public async Task Alta_de_gimnasio_con_slug_repetido_o_reservado_se_rechaza()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        using var platform = await TestApi.PlatformClientAsync(api);

        object Body(string slug) => new
        {
            slug, name = "X", timeZone = "America/Guayaquil", locale = "es-EC",
            ownerEmail = $"o@{slug}.test", ownerFullName = "O", ownerPassword = TestApi.OwnerPassword
        };

        var duplicate = await platform.PostAsJsonAsync("/api/v1/platform/tenants", Body(tenant.Slug));
        var reserved = await platform.PostAsJsonAsync("/api/v1/platform/tenants", Body("platform"));
        var weakPassword = await platform.PostAsJsonAsync("/api/v1/platform/tenants", new
        {
            slug = $"debil-{Guid.NewGuid():N}"[..16], name = "X", timeZone = "America/Guayaquil", locale = "es-EC",
            ownerEmail = "o@debil.test", ownerFullName = "O", ownerPassword = "corta"
        });

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, reserved.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, weakPassword.StatusCode);
        Assert.Equal("users.password_invalid", await TestApi.ProblemCodeAsync(weakPassword));
    }
}
