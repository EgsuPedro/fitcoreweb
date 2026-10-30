using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using FitCore.Api.IntegrationTests.Infrastructure;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

namespace FitCore.Api.IntegrationTests;

/// <summary>Validación de tokens, migraciones y salud del servicio.</summary>
[Collection(ApiCollection4.Name)]
public class PlatformInfrastructureTests(ApiFixture api)
{
    [Fact]
    public async Task Un_token_con_firma_correcta_funciona_pero_expirado_o_de_otro_emisor_o_audiencia_se_rechaza()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var users = await tenant.Owner.GetFromJsonAsync<PagedDto<UserDto>>("/api/v1/users");
        var ownerId = users!.Items.Single(u => u.Email == tenant.OwnerEmail).Id;

        string Token(string issuer, string audience, DateTime notBefore, DateTime expires) =>
            new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
                issuer,
                audience,
                [new Claim("sub", ownerId.ToString()), new Claim("tid", tenant.Id.ToString())],
                notBefore,
                expires,
                new SigningCredentials(
                    new SymmetricSecurityKey(Encoding.UTF8.GetBytes(ApiFixture.SigningKey)), SecurityAlgorithms.HmacSha256)));

        var now = DateTime.UtcNow;
        var valid = Token("fitcore360", "fitcore360-clients", now.AddMinutes(-1), now.AddMinutes(5));
        var expired = Token("fitcore360", "fitcore360-clients", now.AddMinutes(-20), now.AddMinutes(-5));
        var wrongIssuer = Token("otro-emisor", "fitcore360-clients", now.AddMinutes(-1), now.AddMinutes(5));
        var wrongAudience = Token("fitcore360", "otra-audiencia", now.AddMinutes(-1), now.AddMinutes(5));

        // El token bien formado sirve (control positivo): así los rechazos de abajo se deben a un solo defecto cada uno.
        using (var ok = TestApi.Authenticated(api, valid))
        {
            Assert.Equal(HttpStatusCode.OK, (await ok.GetAsync("/api/v1/branches")).StatusCode);
        }

        foreach (var bad in new[] { expired, wrongIssuer, wrongAudience })
        {
            using var client = TestApi.Authenticated(api, bad);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/branches")).StatusCode);
        }
    }

    [Fact]
    public async Task Las_migraciones_estan_aplicadas_y_el_esquema_tiene_las_tablas_y_RLS_esperadas()
    {
        await using var connection = new NpgsqlConnection(api.AdminConnection);
        await connection.OpenAsync();

        var applied = await ScalarAsync<long>(connection, "SELECT count(*) FROM \"__EFMigrationsHistory\"");
        Assert.Equal(9, applied); // InitialCore, AddRowLevelSecurity, AddMustChangePassword, AddAccountKind, AddTenantLogos, AddEmailQueueAndAccountTokens, AddPublicRegistration, AddOnboarding, AddInvitations

        string[] tables =
        [
            "organizations.tenants", "organizations.branches", "identity.app_users", "identity.app_roles",
            "identity.user_roles", "identity.permissions", "identity.role_permissions", "identity.user_branches",
            "identity.refresh_tokens", "audit.audit_logs", "messaging.outbox_messages"
        ];
        foreach (var table in tables)
        {
            Assert.True(
                await ScalarAsync<bool>(connection, $"SELECT to_regclass('{table}') IS NOT NULL"),
                $"Falta la tabla {table}");
        }

        // Las tablas de datos del tenant deben tener RLS activo y forzado, con su política de aislamiento.
        string[] tenantTables =
        [
            "identity.app_users", "identity.app_roles", "identity.user_roles", "identity.role_permissions",
            "identity.user_branches", "identity.refresh_tokens", "organizations.branches", "audit.audit_logs"
        ];
        foreach (var table in tenantTables)
        {
            Assert.True(
                await ScalarAsync<bool>(connection,
                    $"SELECT relrowsecurity AND relforcerowsecurity FROM pg_class WHERE oid = '{table}'::regclass"),
                $"{table} no tiene RLS forzado");
            Assert.Equal(
                1,
                await ScalarAsync<long>(connection,
                    $"SELECT count(*) FROM pg_policy WHERE polrelid = '{table}'::regclass AND polname = 'tenant_isolation'"));
        }
    }

    [Fact]
    public async Task Health_ready_responde_200_con_la_base_de_datos_disponible()
    {
        using var client = api.CreateClient();

        var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }
}
