using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using FitCore.Api.IntegrationTests.Infrastructure;
using FitCore.Invoicing.Application.Security;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FitCore.Api.IntegrationTests;

/// <summary>
/// Configuración tributaria de la facturación electrónica: emisor, puntos de emisión y certificado de firma. Los certificados son autofirmados y generados en memoria
/// para cada prueba; jamás se usa un certificado real. Todo corre contra PostgreSQL real con el rol limitado.
/// </summary>
[Collection(ApiCollection2.Name)]
public class InvoicingSettingsTests(ApiFixture api)
{
    private const string Ruc = "1790000028001";
    private const string CertPassword = "Certificado-Prueba-2026!";

    private static string NewSlug() => $"fac-{Guid.NewGuid():N}"[..20];

    private static byte[] P12(string password = CertPassword, int validDays = 365, int startedDaysAgo = 1, string subject = "CN=Gimnasio Prueba, SERIALNUMBER=" + Ruc)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, critical: true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-startedDaysAgo), DateTimeOffset.UtcNow.AddDays(validDays));
        return certificate.Export(X509ContentType.Pfx, password);
    }

    private static object Issuer(string ruc = Ruc, string regime = "General", string environment = "Test", bool confirm = false, string? email = "facturas@fitness.test") => new
    {
        ruc, legalName = "Fitness Quito S.A.", tradeName = "Fitness Quito", headOfficeAddress = "Av. Amazonas N24-03 y Colón, Quito", taxRegime = regime, accountingRequired = true,
        specialTaxpayerNumber = (string?)null, withholdingAgentNumber = (string?)null, environment, notificationEmail = email, confirmProduction = confirm
    };

    private static MultipartFormDataContent Form(byte[] file, string? password)
    {
        var content = new MultipartFormDataContent();
        var part = new ByteArrayContent(file);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/x-pkcs12");
        content.Add(part, "file", "firma.p12");
        if (password is not null)
        {
            content.Add(new StringContent(password), "password");
        }

        return content;
    }

    private async Task<string?> ScalarAsync(string sql, params (string Name, object Value)[] args)
    {
        await using var conn = new NpgsqlConnection(api.AdminConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in args)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        return (await cmd.ExecuteScalarAsync())?.ToString();
    }

    private static async Task<(HttpStatusCode Status, string? Code, JsonElement Body, string Text)> ReadAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        var json = string.IsNullOrEmpty(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
        var code = json.ValueKind == JsonValueKind.Object && json.TryGetProperty("code", out var c) ? c.GetString() : null;
        return (response.StatusCode, code, json, text);
    }

    private static bool Check(JsonElement settings, string code) =>
        settings.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("code").GetString() == code).GetProperty("ok").GetBoolean();

    private async Task<HttpClient> StaffAsync(TestTenant tenant, string role)
    {
        var email = $"{role.ToLowerInvariant()}-{Guid.NewGuid():N}"[..24] + "@fac.test";
        var branch = await TestApi.CreateBranchAsync(tenant.Owner, "B" + Guid.NewGuid().ToString("N")[..5]);
        await TestApi.CreateUserAsync(tenant.Owner, email, role, branch);
        return TestApi.Authenticated(api, await TestApi.LoginAsync(api, tenant.Slug, email, TestApi.UserPassword));
    }

    // ---- lectura y permisos -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_new_gym_has_nothing_configured_and_cannot_issue()
    {
        var tenant = await TestApi.NewTenantAsync(api, NewSlug());

        var settings = await ReadAsync(await tenant.Owner.GetAsync("/api/v1/invoicing/settings"));

        Assert.Equal(HttpStatusCode.OK, settings.Status);
        Assert.Equal(JsonValueKind.Null, settings.Body.GetProperty("issuer").ValueKind);
        Assert.Equal(0, settings.Body.GetProperty("emissionPoints").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, settings.Body.GetProperty("certificate").ValueKind);
        Assert.False(settings.Body.GetProperty("canIssue").GetBoolean());
        Assert.True(settings.Body.GetProperty("masterKeyConfigured").GetBoolean());
        Assert.False(Check(settings.Body, "issuer_profile"));
        Assert.False(Check(settings.Body, "certificate"));
    }

    [Fact]
    public async Task Only_staff_with_the_right_permission_can_read_or_change_the_configuration()
    {
        var tenant = await TestApi.NewTenantAsync(api, NewSlug());
        var cashier = await StaffAsync(tenant, "Cajero"); // puede ver (invoicing.read) pero no configurar
        var trainer = await StaffAsync(tenant, "Entrenador"); // sin permisos de facturación
        var accountant = await StaffAsync(tenant, "Contador"); // configura
        using var anon = api.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/v1/invoicing/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await trainer.GetAsync("/api/v1/invoicing/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await cashier.GetAsync("/api/v1/invoicing/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PutAsJsonAsync("/api/v1/invoicing/issuer", Issuer())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PutAsync("/api/v1/invoicing/certificate", Form(P12(), CertPassword))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await trainer.PutAsJsonAsync("/api/v1/invoicing/issuer", Issuer())).StatusCode);
        var accountantResponse = await accountant.PutAsJsonAsync("/api/v1/invoicing/issuer", Issuer());
        Assert.True(accountantResponse.StatusCode == HttpStatusCode.OK, await accountantResponse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_branch_administrator_who_only_reaches_some_branches_cannot_change_the_gym_wide_tax_data()
    {
        var tenant = await TestApi.NewTenantAsync(api, NewSlug());
        var branchAdmin = await StaffAsync(tenant, "AdministradorSucursal");

        // Puede ver y emitir, pero la configuración tributaria es de todo el gimnasio.
        Assert.Equal(HttpStatusCode.OK, (await branchAdmin.GetAsync("/api/v1/invoicing/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await branchAdmin.PutAsJsonAsync("/api/v1/invoicing/issuer", Issuer())).StatusCode);
    }

    // ---- emisor -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_issuer_is_saved_updated_and_normalized()
    {
        var tenant = await TestApi.NewTenantAsync(api, NewSlug());

        var first = await ReadAsync(await tenant.Owner.PutAsJsonAsync("/api/v1/invoicing/issuer", Issuer(ruc: "1790000028-001")));
        var second = await ReadAsync(await tenant.Owner.PutAsJsonAsync("/api/v1/invoicing/issuer", Issuer(regime: "RimpeEmprendedor", email: null)));

        Assert.Equal(HttpStatusCode.OK, first.Status);
        Assert.Equal(Ruc, first.Body.GetProperty("issuer").GetProperty("ruc").GetString());
        Assert.Equal("Test", first.Body.GetProperty("issuer").GetProperty("environment").GetString());
        Assert.True(Check(first.Body, "ruc_valid"));
        Assert.Equal("RimpeEmprendedor", second.Body.GetProperty("issuer").GetProperty("taxRegime").GetString());
        Assert.Equal("1", await ScalarAsync("SELECT count(*)::text FROM invoicing.issuer_profiles WHERE tenant_id = @t", ("t", tenant.Id)));
        Assert.False(Check(second.Body, "notification_email"));
    }

    [Theory]
    [InlineData("1790000029001")]
    [InlineData("179000002800")]
    [InlineData("")]
    public async Task An_invalid_ruc_is_rejected_and_nothing_is_stored(string ruc)
    {
        var tenant = await TestApi.NewTenantAsync(api, NewSlug());

        var response = await tenant.Owner.PutAsJsonAsync("/api/v1/invoicing/issuer", Issuer(ruc: ruc));

        Assert.False(response.IsSuccessStatusCode);
        Assert.Equal("0", await ScalarAsync("SELECT count(*)::text FROM invoicing.issuer_profiles WHERE tenant_id = @t", ("t", tenant.Id)));
    }

    [Fact]
    public async Task Moving_to_production_needs_an_explicit_confirmation()
    {
        var tenant = await TestApi.NewTenantAsync(api, NewSlug());

        var without = await tenant.Owner.PutAsJsonAsync("/api/v1/invoicing/issuer", Issuer(environment: "Production"));
        var with = await ReadAsync(await tenant.Owner.PutAsJsonAsync("/api/v1/invoicing/issuer", Issuer(environment: "Production", confirm: true)));

        Assert.False(without.IsSuccessStatusCode);
        Assert.Equal("Production", with.Body.GetProperty("issuer").GetProperty("environment").GetString());
        Assert.True(Check(with.Body, "production_environment"));
    }

    [Fact]
    public async Task The_popular_business_regime_can_be_recorded_but_blocks_issuing()
    {
        var tenant = await TestApi.NewTenantAsync(api, NewSlug());

        var result = await ReadAsync(await tenant.Owner.PutAsJsonAsync("/api/v1/invoicing/issuer", Issuer(regime: "RimpeNegocioPopular")));

        Assert.False(Check(result.Body, "regime_allows_electronic_invoice"));
        Assert.False(result.Body.GetProperty("canIssue").GetBoolean());
    }

    [Theory]
    [InlineData("legalName")]
    [InlineData("headOfficeAddress")]
    public async Task Required_issuer_fields_cannot_be_empty(string field)
    {
        var tenant = await TestApi.NewTenantAsync(api, NewSlug());
        var body = JsonSerializer.SerializeToElement(Issuer());
        var map = body.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
        map[field] = "";

        Assert.False((await tenant.Owner.PutAsJsonAsync("/api/v1/invoicing/issuer", map)).IsSuccessStatusCode);
    }

    // ---- puntos de emisión ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Emission_points_are_created_updated_deactivated_and_codes_never_repeat()
    {
        var tenant = await TestApi.NewTenantAsync(api, NewSlug());
        var branch = await TestApi.CreateBranchAsync(tenant.Owner, "SUR"); // la sucursal de prueba usa el establecimiento 001

        var created = await ReadAsync(await tenant.Owner.PostAsJsonAsync("/api/v1/invoicing/emission-points",
            new { branchId = branch, establishmentCode = "001", pointCode = "001", establishmentAddress = "Av. Principal" }));
        var duplicate = await ReadAsync(await tenant.Owner.PostAsJsonAsync("/api/v1/invoicing/emission-points",
            new { branchId = (Guid?)null, establishmentCode = "001", pointCode = "001", establishmentAddress = "Otra" }));
        var second = await ReadAsync(await tenant.Owner.PostAsJsonAsync("/api/v1/invoicing/emission-points",
            new { branchId = (Guid?)null, establishmentCode = "001", pointCode = "002", establishmentAddress = "Caja 2" }));

        Assert.Equal(HttpStatusCode.OK, created.Status);
        Assert.Equal("Sucursal SUR", created.Body.GetProperty("emissionPoints")[0].GetProperty("branchName").GetString());
        Assert.Equal("invoicing.emission_point_taken", duplicate.Code);
        Assert.Equal(2, second.Body.GetProperty("emissionPoints").GetArrayLength());

        var id = created.Body.GetProperty("emissionPoints")[0].GetProperty("id").GetGuid();
        var updated = await ReadAsync(await tenant.Owner.PutAsJsonAsync($"/api/v1/invoicing/emission-points/{id}", new { branchId = (Guid?)null, establishmentAddress = "Nueva dirección 123" }));
        var off = await ReadAsync(await tenant.Owner.PatchAsJsonAsync($"/api/v1/invoicing/emission-points/{id}/status", new { isActive = false }));

        Assert.Equal("Nueva dirección 123", updated.Body.GetProperty("emissionPoints")[0].GetProperty("establishmentAddress").GetString());
        Assert.Equal("001", updated.Body.GetProperty("emissionPoints")[0].GetProperty("establishmentCode").GetString()); // los códigos no cambian
        Assert.False(off.Body.GetProperty("emissionPoints")[0].GetProperty("isActive").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await tenant.Owner.PatchAsJsonAsync($"/api/v1/invoicing/emission-points/{Guid.NewGuid()}/status", new { isActive = false })).StatusCode);
    }

    [Theory]
    [InlineData("000", "001")]
    [InlineData("1", "001")]
    [InlineData("001", "000")]
    [InlineData("ABC", "001")]
    [InlineData("001", "")]
    public async Task Invalid_codes_are_rejected(string establishment, string point)
    {
        var tenant = await TestApi.NewTenantAsync(api, NewSlug());

        var response = await tenant.Owner.PostAsJsonAsync("/api/v1/invoicing/emission-points",
            new { branchId = (Guid?)null, establishmentCode = establishment, pointCode = point, establishmentAddress = "Av. Principal" });

        Assert.False(response.IsSuccessStatusCode);
        Assert.Equal("0", await ScalarAsync("SELECT count(*)::text FROM invoicing.emission_points WHERE tenant_id = @t", ("t", tenant.Id)));
    }

    [Fact]
    public async Task The_establishment_code_must_match_the_one_configured_in_the_branch_and_the_branch_must_be_the_gyms_own()
    {
        var a = await TestApi.NewTenantAsync(api, NewSlug());
        var b = await TestApi.NewTenantAsync(api, NewSlug());
        var branchA = await TestApi.CreateBranchAsync(a.Owner, "CENTRO"); // establecimiento SRI 001
        var branchB = await TestApi.CreateBranchAsync(b.Owner, "OTRO");

        var mismatch = await ReadAsync(await a.Owner.PostAsJsonAsync("/api/v1/invoicing/emission-points",
            new { branchId = branchA, establishmentCode = "002", pointCode = "001", establishmentAddress = "X" }));
        var foreign = await ReadAsync(await a.Owner.PostAsJsonAsync("/api/v1/invoicing/emission-points",
            new { branchId = branchB, establishmentCode = "001", pointCode = "001", establishmentAddress = "X" }));

        Assert.Equal("invoicing.establishment_code_mismatch", mismatch.Code);
        Assert.Equal("invoicing.branch_not_found", foreign.Code);
    }

    // ---- certificado --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_certificate_is_stored_encrypted_and_the_response_never_carries_secrets()
    {
        var tenant = await TestApi.NewTenantAsync(api, NewSlug());
        await tenant.Owner.PutAsJsonAsync("/api/v1/invoicing/issuer", Issuer());
        var p12 = P12();

        var result = await ReadAsync(await tenant.Owner.PutAsync("/api/v1/invoicing/certificate", Form(p12, CertPassword)));

        Assert.Equal(HttpStatusCode.OK, result.Status);
        var certificate = result.Body.GetProperty("certificate");
        Assert.Contains("Gimnasio Prueba", certificate.GetProperty("subject").GetString());
        Assert.True(certificate.GetProperty("matchesIssuerRuc").GetBoolean());
        Assert.InRange(certificate.GetProperty("daysToExpire").GetInt32(), 360, 366);
        Assert.True(Check(result.Body, "certificate"));
        Assert.DoesNotContain(CertPassword, result.Text);
        Assert.DoesNotContain(Convert.ToBase64String(p12)[..60], result.Text);
        Assert.DoesNotContain("encrypted", result.Text, StringComparison.OrdinalIgnoreCase);

        var again = await tenant.Owner.GetStringAsync("/api/v1/invoicing/settings");
        Assert.DoesNotContain(CertPassword, again);
        Assert.DoesNotContain("encrypted", again, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_database_holds_only_ciphertext_bound_to_its_gym_and_row()
    {
        var a = await TestApi.NewTenantAsync(api, NewSlug());
        var b = await TestApi.NewTenantAsync(api, NewSlug());
        var p12 = P12();
        await a.Owner.PutAsync("/api/v1/invoicing/certificate", Form(p12, CertPassword));

        await using var conn = new NpgsqlConnection(api.AdminConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT id, encrypted_p12, encrypted_password FROM invoicing.signing_certificates WHERE tenant_id = @t AND is_active", conn);
        cmd.Parameters.AddWithValue("t", a.Id);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var id = reader.GetGuid(0);
        var storedP12 = (byte[])reader["encrypted_p12"];
        var storedPassword = (byte[])reader["encrypted_password"];

        // El archivo y la contraseña en claro no aparecen en lo guardado.
        Assert.False(storedP12.AsSpan().IndexOf(p12.AsSpan(0, 64)) >= 0);
        Assert.False(storedPassword.AsSpan().IndexOf(Encoding.UTF8.GetBytes(CertPassword)) >= 0);

        // Con la clave maestra y el contexto correcto se recuperan; con el contexto de otro gimnasio, no.
        var protector = api.Services.GetRequiredService<ISecretProtector>();
        Assert.Equal(p12, protector.Unprotect(storedP12, $"invoicing:certificate:{a.Id:N}:{id:N}:p12"));
        Assert.Equal(CertPassword, Encoding.UTF8.GetString(protector.Unprotect(storedPassword, $"invoicing:certificate:{a.Id:N}:{id:N}:password")));
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(storedP12, $"invoicing:certificate:{b.Id:N}:{id:N}:p12"));
    }

    [Fact]
    public async Task A_wrong_password_a_garbage_file_a_missing_field_and_an_oversized_file_are_rejected_and_nothing_is_stored()
    {
        var tenant = await TestApi.NewTenantAsync(api, NewSlug());

        var wrong = await ReadAsync(await tenant.Owner.PutAsync("/api/v1/invoicing/certificate", Form(P12(), "otra-clave")));
        var garbage = await ReadAsync(await tenant.Owner.PutAsync("/api/v1/invoicing/certificate", Form([1, 2, 3, 4, 5, 6], "x")));
        var noPassword = await ReadAsync(await tenant.Owner.PutAsync("/api/v1/invoicing/certificate", Form(P12(), null)));
        var huge = await ReadAsync(await tenant.Owner.PutAsync("/api/v1/invoicing/certificate", Form(new byte[600 * 1024], "x")));

        Assert.Equal("invoicing.certificate_password_invalid", wrong.Code);
        Assert.Equal("invoicing.certificate_password_invalid", garbage.Code);
        Assert.Equal("invoicing.certificate_required", noPassword.Code);
        Assert.Equal("invoicing.certificate_too_large", huge.Code);
        Assert.Equal("0", await ScalarAsync("SELECT count(*)::text FROM invoicing.signing_certificates WHERE tenant_id = @t", ("t", tenant.Id)));
    }

    [Fact]
    public async Task An_expired_certificate_is_rejected()
    {
        var tenant = await TestApi.NewTenantAsync(api, NewSlug());

        var result = await ReadAsync(await tenant.Owner.PutAsync("/api/v1/invoicing/certificate", Form(P12(validDays: -1, startedDaysAgo: 400), CertPassword)));

        Assert.Equal("invoicing.certificate_expired", result.Code);
        Assert.Equal("0", await ScalarAsync("SELECT count(*)::text FROM invoicing.signing_certificates WHERE tenant_id = @t", ("t", tenant.Id)));
    }

    [Fact]
    public async Task Replacing_the_certificate_retires_the_old_one_and_wipes_its_secrets_leaving_a_single_active()
    {
        var tenant = await TestApi.NewTenantAsync(api, NewSlug());
        await tenant.Owner.PutAsync("/api/v1/invoicing/certificate", Form(P12(), CertPassword));

        var second = await ReadAsync(await tenant.Owner.PutAsync("/api/v1/invoicing/certificate", Form(P12("otra-clave-2"), "otra-clave-2")));

        Assert.Equal(HttpStatusCode.OK, second.Status);
        Assert.Equal("2", await ScalarAsync("SELECT count(*)::text FROM invoicing.signing_certificates WHERE tenant_id = @t", ("t", tenant.Id)));
        Assert.Equal("1", await ScalarAsync("SELECT count(*)::text FROM invoicing.signing_certificates WHERE tenant_id = @t AND is_active", ("t", tenant.Id)));
        Assert.Equal("0", await ScalarAsync(
            "SELECT (length(encrypted_p12) + length(encrypted_password))::text FROM invoicing.signing_certificates WHERE tenant_id = @t AND NOT is_active", ("t", tenant.Id)));
    }

    [Fact]
    public async Task Retiring_the_certificate_wipes_it_blocks_issuing_and_cannot_be_repeated()
    {
        var tenant = await TestApi.NewTenantAsync(api, NewSlug());
        await tenant.Owner.PutAsync("/api/v1/invoicing/certificate", Form(P12(), CertPassword));

        var retired = await ReadAsync(await tenant.Owner.DeleteAsync("/api/v1/invoicing/certificate"));
        var again = await tenant.Owner.DeleteAsync("/api/v1/invoicing/certificate");

        Assert.Equal(JsonValueKind.Null, retired.Body.GetProperty("certificate").ValueKind);
        Assert.False(Check(retired.Body, "certificate"));
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.Equal("0", await ScalarAsync("SELECT sum(length(encrypted_p12))::text FROM invoicing.signing_certificates WHERE tenant_id = @t", ("t", tenant.Id)));
    }

    [Fact]
    public async Task Without_the_master_key_certificates_cannot_be_stored_and_the_gym_is_told_why()
    {
        var tenant = await TestApi.NewTenantAsync(api, NewSlug());
        using var noKey = api.CreateClientWith(("Invoicing:MasterKey", ""));
        noKey.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await TestApi.LoginAsync(api, tenant.Slug, tenant.OwnerEmail, tenant.OwnerPassword));

        var result = await ReadAsync(await noKey.PutAsync("/api/v1/invoicing/certificate", Form(P12(), CertPassword)));
        var settings = await ReadAsync(await noKey.GetAsync("/api/v1/invoicing/settings"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, result.Status);
        Assert.Equal("invoicing.master_key_missing", result.Code);
        Assert.False(settings.Body.GetProperty("masterKeyConfigured").GetBoolean());
        Assert.False(Check(settings.Body, "master_key"));
        Assert.Equal("0", await ScalarAsync("SELECT count(*)::text FROM invoicing.signing_certificates WHERE tenant_id = @t", ("t", tenant.Id)));
    }

    [Fact]
    public async Task A_certificate_that_seems_to_belong_to_another_ruc_only_warns()
    {
        var tenant = await TestApi.NewTenantAsync(api, NewSlug());
        await tenant.Owner.PutAsJsonAsync("/api/v1/invoicing/issuer", Issuer(ruc: "1760001550001"));

        var result = await ReadAsync(await tenant.Owner.PutAsync("/api/v1/invoicing/certificate", Form(P12(), CertPassword))); // el certificado dice 1790000028001

        Assert.False(result.Body.GetProperty("certificate").GetProperty("matchesIssuerRuc").GetBoolean());
        Assert.False(Check(result.Body, "certificate_matches_ruc"));
    }

    // ---- listo para emitir y aislamiento --------------------------------------------------------------------------------------------

    [Fact]
    public async Task With_issuer_point_certificate_and_key_the_gym_is_ready_to_issue()
    {
        var tenant = await TestApi.NewTenantAsync(api, NewSlug());
        await tenant.Owner.PutAsJsonAsync("/api/v1/invoicing/issuer", Issuer());
        await tenant.Owner.PostAsJsonAsync("/api/v1/invoicing/emission-points", new { branchId = (Guid?)null, establishmentCode = "001", pointCode = "001", establishmentAddress = "Av. Principal" });
        await tenant.Owner.PutAsync("/api/v1/invoicing/certificate", Form(P12(), CertPassword));

        var settings = await ReadAsync(await tenant.Owner.GetAsync("/api/v1/invoicing/settings"));

        Assert.True(settings.Body.GetProperty("canIssue").GetBoolean());
        Assert.All(settings.Body.GetProperty("checks").EnumerateArray().Where(c => c.GetProperty("blocking").GetBoolean()), c => Assert.True(c.GetProperty("ok").GetBoolean(), c.GetProperty("code").GetString()));
        Assert.False(Check(settings.Body, "production_environment")); // aviso: sigue en ambiente de pruebas
    }

    [Fact]
    public async Task Each_gym_only_sees_its_own_configuration_and_the_database_enforces_it()
    {
        var a = await TestApi.NewTenantAsync(api, NewSlug());
        var b = await TestApi.NewTenantAsync(api, NewSlug());
        await a.Owner.PutAsJsonAsync("/api/v1/invoicing/issuer", Issuer());
        await a.Owner.PostAsJsonAsync("/api/v1/invoicing/emission-points", new { branchId = (Guid?)null, establishmentCode = "001", pointCode = "001", establishmentAddress = "A" });
        await a.Owner.PutAsync("/api/v1/invoicing/certificate", Form(P12(), CertPassword));

        var seenByB = await ReadAsync(await b.Owner.GetAsync("/api/v1/invoicing/settings"));
        var emissionForB = await ReadAsync(await b.Owner.PostAsJsonAsync("/api/v1/invoicing/emission-points", new { branchId = (Guid?)null, establishmentCode = "001", pointCode = "001", establishmentAddress = "B" }));

        Assert.Equal(JsonValueKind.Null, seenByB.Body.GetProperty("issuer").ValueKind);
        Assert.Equal(JsonValueKind.Null, seenByB.Body.GetProperty("certificate").ValueKind);
        Assert.Equal(HttpStatusCode.OK, emissionForB.Status); // los mismos códigos en otro gimnasio no chocan

        await using var conn = new NpgsqlConnection(api.AppConnection);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using (var set = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, true)", conn, tx))
        {
            set.Parameters.AddWithValue("t", b.Id.ToString());
            await set.ExecuteNonQueryAsync();
        }

        foreach (var table in new[] { "issuer_profiles", "signing_certificates" })
        {
            await using var cmd = new NpgsqlCommand($"SELECT count(*) FROM invoicing.{table} WHERE tenant_id = @other", conn, tx);
            cmd.Parameters.AddWithValue("other", a.Id);
            Assert.Equal(0L, await cmd.ExecuteScalarAsync());
        }
    }

    [Fact]
    public async Task Certificate_and_issuer_changes_are_audited_without_secrets()
    {
        var tenant = await TestApi.NewTenantAsync(api, NewSlug());
        await tenant.Owner.PutAsJsonAsync("/api/v1/invoicing/issuer", Issuer());
        await tenant.Owner.PutAsync("/api/v1/invoicing/certificate", Form(P12(), CertPassword));
        await tenant.Owner.DeleteAsync("/api/v1/invoicing/certificate");

        var actions = await ScalarAsync(
            "SELECT string_agg(DISTINCT action, ',' ORDER BY action) FROM audit.audit_logs WHERE tenant_id = @t AND (action LIKE 'IssuerProfile.%' OR action LIKE 'SigningCertificate.%')", ("t", tenant.Id));
        var leaked = await ScalarAsync(
            "SELECT count(*)::text FROM audit.audit_logs a WHERE a.tenant_id = @t AND (a::text LIKE '%' || @p || '%' OR a::text ~ 'MIIK|MII[A-Za-z0-9+/]{20}')", ("t", tenant.Id), ("p", CertPassword));
        var redacted = await ScalarAsync(
            "SELECT count(*)::text FROM audit.audit_logs WHERE tenant_id = @t AND action = 'SigningCertificate.Created' AND after::text LIKE '%[redacted]%'", ("t", tenant.Id));

        Assert.Contains("IssuerProfile.Created", actions);
        Assert.Contains("SigningCertificate.Created", actions);
        Assert.Contains("SigningCertificate.Updated", actions);
        Assert.Equal("0", leaked);
        Assert.Equal("1", redacted);
    }

    [Fact]
    public async Task The_database_refuses_an_active_certificate_without_secrets_and_a_retired_one_with_secrets()
    {
        var tenant = await TestApi.NewTenantAsync(api, NewSlug());
        await tenant.Owner.PutAsync("/api/v1/invoicing/certificate", Form(P12(), CertPassword));

        await using var conn = new NpgsqlConnection(api.AdminConnection);
        await conn.OpenAsync();
        await using var wipeActive = new NpgsqlCommand("UPDATE invoicing.signing_certificates SET encrypted_p12 = ''::bytea WHERE tenant_id = @t AND is_active", conn);
        wipeActive.Parameters.AddWithValue("t", tenant.Id);
        await Assert.ThrowsAsync<PostgresException>(() => wipeActive.ExecuteNonQueryAsync());

        await using var retireWithSecrets = new NpgsqlCommand("UPDATE invoicing.signing_certificates SET is_active = false WHERE tenant_id = @t AND is_active", conn);
        retireWithSecrets.Parameters.AddWithValue("t", tenant.Id);
        await Assert.ThrowsAsync<PostgresException>(() => retireWithSecrets.ExecuteNonQueryAsync());
    }
}
