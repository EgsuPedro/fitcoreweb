using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Xml.Linq;
using FitCore.Api.IntegrationTests.Infrastructure;
using FitCore.Invoicing.Xml;
using Npgsql;
using static FitCore.Api.IntegrationTests.Infrastructure.InvoicingTestApi;

namespace FitCore.Api.IntegrationTests;

/// <summary>
/// Del comprobante pendiente al XML firmado: generación, validación contra el XSD oficial, firma XAdES-BES con el certificado cifrado del gimnasio, verificación local, almacenamiento inmutable,
/// descarga auditada y el ciclo de vida que hace cumplir la base. Certificados autofirmados y generados para cada prueba; nunca uno real.
/// </summary>
[Collection(ApiCollection2.Name)]
public class InvoicingSigningTests(ApiFixture api)
{
    private static string NewSlug() => $"fir-{Guid.NewGuid():N}"[..20];

    private sealed record Gym(TestTenant Tenant, Guid Branch);

    private async Task<Gym> NewGymAsync()
    {
        var tenant = await TestApi.NewTenantAsync(api, NewSlug());
        var gym = new Gym(tenant, await TestApi.CreateBranchAsync(tenant.Owner, "UIO"));
        await ConfigureIssuerAsync(tenant.Owner);
        return gym;
    }

    private async Task<HttpClient> StaffAsync(TestTenant tenant, string role, params Guid[] branches)
    {
        var email = $"{role.ToLowerInvariant()}-{Guid.NewGuid():N}"[..24] + "@fir.test";
        await TestApi.CreateUserAsync(tenant.Owner, email, role, branches);
        return TestApi.Authenticated(api, await TestApi.LoginAsync(api, tenant.Slug, email, TestApi.UserPassword));
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

    private async Task<string?> SqlStateAsync(string sql, params (string Name, object Value)[] args)
    {
        try
        {
            await ScalarAsync(sql, args);
            return null;
        }
        catch (PostgresException ex)
        {
            return ex.SqlState;
        }
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> ReadAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, string.IsNullOrEmpty(text) ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private static async Task<Guid> IssueAsync(HttpClient client, Guid obligation)
    {
        var response = await client.PostAsJsonAsync("/api/v1/invoicing/invoices", new { sourceType = "obligation", sourceId = obligation });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static Task<(HttpStatusCode Status, JsonElement Body)> SignAsync(HttpClient client, Guid id) =>
        ReadAsync(client.PostAsync($"/api/v1/invoicing/invoices/{id}/sign", null).GetAwaiter().GetResult());

    private async Task<(Gym Gym, Guid Id, JsonElement Invoice)> IssuedInvoiceAsync(decimal amount = 30m)
    {
        var gym = await NewGymAsync();
        var (_, obligation) = await PaidSaleAsync(gym.Tenant.Owner, gym.Branch, amount);
        var id = await IssueAsync(gym.Tenant.Owner, obligation);
        var invoice = await gym.Tenant.Owner.GetFromJsonAsync<JsonElement>($"/api/v1/invoicing/invoices/{id}");
        return (gym, id, invoice);
    }

    // ---- firma ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Signing_stores_the_unsigned_and_signed_xml_and_moves_the_invoice_to_signed()
    {
        var (gym, id, issued) = await IssuedInvoiceAsync();
        Assert.Equal("PendingSignature", issued.GetProperty("status").GetString());
        Assert.Equal(0, issued.GetProperty("documents").GetArrayLength());

        var (status, signed) = await SignAsync(gym.Tenant.Owner, id);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("SignedDocument", signed.GetProperty("status").GetString());
        var documents = signed.GetProperty("documents").EnumerateArray().ToDictionary(d => d.GetProperty("kind").GetString()!);
        Assert.Equal(["SignedXml", "Xml"], documents.Keys.Order(StringComparer.Ordinal));
        Assert.All(documents.Values, d =>
        {
            Assert.Matches("^[0-9a-f]{64}$", d.GetProperty("sha256").GetString()!);
            Assert.True(d.GetProperty("sizeBytes").GetInt32() > 1000);
        });
        Assert.True(documents["SignedXml"].GetProperty("sizeBytes").GetInt32() > documents["Xml"].GetProperty("sizeBytes").GetInt32());
        var events = signed.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("type").GetString()).ToList();
        Assert.Equal(["Created", "Signed"], events);
    }

    [Fact]
    public async Task The_downloaded_signed_xml_matches_the_official_schema_and_verifies_against_the_gyms_certificate()
    {
        var (gym, id, invoice) = await IssuedInvoiceAsync(45m);
        await SignAsync(gym.Tenant.Owner, id);
        var settings = await gym.Tenant.Owner.GetFromJsonAsync<JsonElement>("/api/v1/invoicing/settings");
        var thumbprint = settings.GetProperty("certificate").GetProperty("thumbprint").GetString()!;

        var response = await gym.Tenant.Owner.GetAsync($"/api/v1/invoicing/invoices/{id}/xml");
        var xml = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/xml", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal($"{invoice.GetProperty("accessKey").GetString()}.xml", response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        Assert.Empty(InvoiceXmlValidator.Validate(xml));
        var check = XadesBesSigner.Verify(xml, thumbprint);
        Assert.True(check.IsValid, string.Join("; ", check.Problems));

        var root = XDocument.Parse(xml).Root!;
        Assert.Equal(invoice.GetProperty("accessKey").GetString(), root.Element("infoTributaria")!.Element("claveAcceso")!.Value);
        Assert.Equal("45.00", root.Element("infoFactura")!.Element("importeTotal")!.Value);
        Assert.Equal(Ruc, root.Element("infoTributaria")!.Element("ruc")!.Value);
        Assert.Equal("ana.perez@correo.test", root.Element("infoAdicional")!.Elements().Single(e => e.Attribute("nombre")!.Value == "Email").Value);
    }

    [Fact]
    public async Task The_stored_document_hash_is_the_hash_of_what_is_downloaded()
    {
        var (gym, id, _) = await IssuedInvoiceAsync();
        var signed = (await SignAsync(gym.Tenant.Owner, id)).Body;
        var hash = signed.GetProperty("documents").EnumerateArray().Single(d => d.GetProperty("kind").GetString() == "SignedXml").GetProperty("sha256").GetString();

        var bytes = await gym.Tenant.Owner.GetByteArrayAsync($"/api/v1/invoicing/invoices/{id}/xml");

        Assert.Equal(hash, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)));
        Assert.Equal(hash, await ScalarAsync("SELECT sha256 FROM invoicing.invoice_documents WHERE invoice_id = @i AND kind = 'SignedXml'", ("i", id)));
    }

    [Fact]
    public async Task The_unsigned_xml_is_available_after_signing_but_nothing_is_downloadable_before()
    {
        var (gym, id, _) = await IssuedInvoiceAsync();
        var before = await ReadAsync(await gym.Tenant.Owner.GetAsync($"/api/v1/invoicing/invoices/{id}/xml"));
        Assert.Equal(HttpStatusCode.NotFound, before.Status);
        Assert.Equal("invoicing.document_not_found", before.Body.GetProperty("code").GetString());

        await SignAsync(gym.Tenant.Owner, id);
        var plain = await gym.Tenant.Owner.GetStringAsync($"/api/v1/invoicing/invoices/{id}/xml?signed=false");

        Assert.DoesNotContain("Signature", plain, StringComparison.Ordinal);
        Assert.Empty(InvoiceXmlValidator.Validate(plain));
        Assert.False(XadesBesSigner.Verify(plain).IsValid);
    }

    [Fact]
    public async Task Signing_twice_changes_nothing_and_parallel_requests_sign_exactly_once()
    {
        var (gym, id, _) = await IssuedInvoiceAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => gym.Tenant.Owner.PostAsync($"/api/v1/invoicing/invoices/{id}/sign", null)));
        var again = await SignAsync(gym.Tenant.Owner, id);

        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(HttpStatusCode.OK, again.Status);
        Assert.Equal("2", await ScalarAsync("SELECT count(*)::text FROM invoicing.invoice_documents WHERE invoice_id = @i", ("i", id)));
        Assert.Equal("1", await ScalarAsync("SELECT count(*)::text FROM invoicing.invoice_events WHERE invoice_id = @i AND type = 'Signed'", ("i", id)));
        Assert.Equal("SignedDocument", await ScalarAsync("SELECT status FROM invoicing.invoices WHERE id = @i", ("i", id)));
    }

    [Fact]
    public async Task Every_download_is_recorded_in_the_invoice_history()
    {
        var (gym, id, _) = await IssuedInvoiceAsync();
        await SignAsync(gym.Tenant.Owner, id);

        await gym.Tenant.Owner.GetStringAsync($"/api/v1/invoicing/invoices/{id}/xml");
        await gym.Tenant.Owner.GetStringAsync($"/api/v1/invoicing/invoices/{id}/xml?signed=false");

        Assert.Equal("2", await ScalarAsync("SELECT count(*)::text FROM invoicing.invoice_events WHERE invoice_id = @i AND type = 'XmlDownloaded' AND actor_id IS NOT NULL", ("i", id)));
    }

    // ---- fallos que no dejan rastro --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Without_a_certificate_the_invoice_stays_pending_and_can_be_signed_after_loading_one()
    {
        var (gym, id, _) = await IssuedInvoiceAsync();
        Assert.Equal(HttpStatusCode.OK, (await gym.Tenant.Owner.DeleteAsync("/api/v1/invoicing/certificate")).StatusCode);

        var (status, body) = await SignAsync(gym.Tenant.Owner, id);

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal("invoicing.certificate_not_found", body.GetProperty("code").GetString());
        Assert.Equal("PendingSignature", await ScalarAsync("SELECT status FROM invoicing.invoices WHERE id = @i", ("i", id)));
        Assert.Equal("0", await ScalarAsync("SELECT count(*)::text FROM invoicing.invoice_documents WHERE invoice_id = @i", ("i", id)));

        // Se carga uno nuevo y el mismo comprobante se firma sin problema.
        using var form = new MultipartFormDataContent { { new ByteArrayContent(P12()), "file", "firma.p12" }, { new StringContent(CertPassword), "password" } };
        Assert.Equal(HttpStatusCode.OK, (await gym.Tenant.Owner.PutAsync("/api/v1/invoicing/certificate", form)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SignAsync(gym.Tenant.Owner, id)).Status);
    }

    [Fact]
    public async Task An_expired_certificate_blocks_signing_and_changes_nothing()
    {
        var (gym, id, _) = await IssuedInvoiceAsync();
        await ScalarAsync("UPDATE invoicing.signing_certificates SET not_after = now() - interval '1 day' WHERE tenant_id = @t AND is_active", ("t", gym.Tenant.Id));

        var (status, body) = await SignAsync(gym.Tenant.Owner, id);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal("invoicing.certificate_expired", body.GetProperty("code").GetString());
        Assert.Equal("PendingSignature", await ScalarAsync("SELECT status FROM invoicing.invoices WHERE id = @i", ("i", id)));
    }

    // ---- permisos y aislamiento --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Signing_needs_the_send_permission_and_downloading_needs_the_download_permission()
    {
        var (gym, id, _) = await IssuedInvoiceAsync();
        var cashier = await StaffAsync(gym.Tenant, "Cajero", gym.Branch);
        var trainer = await StaffAsync(gym.Tenant, "Entrenador", gym.Branch);
        var accountant = await StaffAsync(gym.Tenant, "Contador");
        using var anonymous = api.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync($"/api/v1/invoicing/invoices/{id}/sign", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PostAsync($"/api/v1/invoicing/invoices/{id}/sign", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await trainer.PostAsync($"/api/v1/invoicing/invoices/{id}/sign", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await accountant.PostAsync($"/api/v1/invoicing/invoices/{id}/sign", null)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await cashier.GetAsync($"/api/v1/invoicing/invoices/{id}/xml")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await trainer.GetAsync($"/api/v1/invoicing/invoices/{id}/xml")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/v1/invoicing/invoices/{id}/xml")).StatusCode);
    }

    [Fact]
    public async Task Another_branch_and_another_gym_can_neither_sign_nor_download()
    {
        var (gym, id, _) = await IssuedInvoiceAsync();
        await SignAsync(gym.Tenant.Owner, id);
        var otherBranch = await TestApi.CreateBranchAsync(gym.Tenant.Owner, "GYE");
        var elsewhere = await StaffAsync(gym.Tenant, "Cajero", otherBranch);
        var stranger = await TestApi.NewTenantAsync(api, NewSlug());

        Assert.Equal(HttpStatusCode.NotFound, (await elsewhere.GetAsync($"/api/v1/invoicing/invoices/{id}/xml")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.Owner.GetAsync($"/api/v1/invoicing/invoices/{id}/xml")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.Owner.PostAsync($"/api/v1/invoicing/invoices/{id}/sign", null)).StatusCode);
    }

    // ---- la base hace cumplir el ciclo de vida ---------------------------------------------------------------------------------------

    [Fact]
    public async Task The_database_only_allows_valid_transitions_and_never_a_signed_state_without_the_signed_document()
    {
        var (gym, id, _) = await IssuedInvoiceAsync();

        // Sin XML firmado guardado, ni siquiera el estado «firmado» es posible.
        Assert.Equal("FC131", await SqlStateAsync("UPDATE invoicing.invoices SET status = 'SignedDocument' WHERE id = @i", ("i", id)));
        // Saltos de estado no permitidos.
        Assert.Equal("FC131", await SqlStateAsync("UPDATE invoicing.invoices SET status = 'Authorized' WHERE id = @i", ("i", id)));

        await SignAsync(gym.Tenant.Owner, id);
        Assert.Equal("FC131", await SqlStateAsync("UPDATE invoicing.invoices SET status = 'PendingSignature' WHERE id = @i", ("i", id)));
        Assert.Equal("FC131", await SqlStateAsync("UPDATE invoicing.invoices SET status = 'Voided' WHERE id = @i", ("i", id)));
        Assert.Null(await SqlStateAsync("UPDATE invoicing.invoices SET status = 'PendingSubmission', updated_at = now() WHERE id = @i", ("i", id)));
    }

    [Fact]
    public async Task Stored_documents_cannot_be_rewritten_or_deleted()
    {
        var (gym, id, _) = await IssuedInvoiceAsync();
        await SignAsync(gym.Tenant.Owner, id);

        Assert.Equal("FC130", await SqlStateAsync("UPDATE invoicing.invoice_documents SET content = 'x' WHERE invoice_id = @i", ("i", id)));
        Assert.Equal("FC130", await SqlStateAsync("DELETE FROM invoicing.invoice_documents WHERE invoice_id = @i", ("i", id)));
        Assert.Equal("23514", await SqlStateAsync("""
            INSERT INTO invoicing.invoice_documents (id, tenant_id, invoice_id, kind, content, sha256, size_bytes, created_at)
            SELECT gen_random_uuid(), tenant_id, invoice_id, 'Xml', '<a/>', 'NO-ES-UNA-HUELLA', 4, now() FROM invoicing.invoice_documents WHERE invoice_id = @i LIMIT 1
            """, ("i", id)));
    }

    // ---- configuración del emisor según el XSD -----------------------------------------------------------------------------------------

    [Fact]
    public async Task The_issuer_data_is_checked_against_the_limits_of_the_official_schema()
    {
        var tenant = await TestApi.NewTenantAsync(api, NewSlug());
        object Body(string ruc = Ruc, string? withholding = null, string? special = null) => new
        {
            ruc, legalName = "Fitness Quito S.A.", tradeName = (string?)null, headOfficeAddress = "Av. Amazonas N24-03 y Colón, Quito", taxRegime = "General", accountingRequired = true,
            specialTaxpayerNumber = special, withholdingAgentNumber = withholding, environment = "Test", notificationEmail = (string?)null, confirmProduction = false
        };

        // El esquema del SRI solo admite RUC de emisor terminados en 001.
        var notFirst = await tenant.Owner.PutAsJsonAsync("/api/v1/invoicing/issuer", Body("1790000028002"));
        var longAgent = await tenant.Owner.PutAsJsonAsync("/api/v1/invoicing/issuer", Body(withholding: "123456789"));
        var lettersAgent = await tenant.Owner.PutAsJsonAsync("/api/v1/invoicing/issuer", Body(withholding: "AB12"));
        var shortSpecial = await tenant.Owner.PutAsJsonAsync("/api/v1/invoicing/issuer", Body(special: "12"));
        var fine = await tenant.Owner.PutAsJsonAsync("/api/v1/invoicing/issuer", Body(withholding: "12345678", special: "5368"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, notFirst.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, longAgent.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, lettersAgent.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, shortSpecial.StatusCode);
        Assert.Equal(HttpStatusCode.OK, fine.StatusCode);
    }
}
