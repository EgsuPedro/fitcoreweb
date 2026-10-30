using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using FitCore.Api.IntegrationTests.Infrastructure;
using static FitCore.Api.IntegrationTests.Infrastructure.CommercialTestApi;

namespace FitCore.Api.IntegrationTests;

/// <summary>
/// Origen de lo que se factura: Comercial entrega la obligación cobrada a Facturación por el contrato de BuildingBlocks y Facturación resuelve el comprador con los tipos del
/// SRI. Contra API y PostgreSQL reales; los certificados son autofirmados y generados aquí, nunca reales.
/// </summary>
[Collection(ApiCollection2.Name)]
public class InvoicingSourcesTests(ApiFixture api)
{
    private const string Ruc = "1790000028001";
    private const string CertPassword = "Certificado-Prueba-2026!";

    private static string NewSlug() => $"ori-{Guid.NewGuid():N}"[..20];

    private sealed record Gym(TestTenant Tenant, Guid Branch);

    private async Task<Gym> NewGymAsync()
    {
        var tenant = await TestApi.NewTenantAsync(api, NewSlug());
        return new Gym(tenant, await TestApi.CreateBranchAsync(tenant.Owner, "UIO"));
    }

    private static async Task<Guid> NewMemberAsync(HttpClient owner, Guid branch, string idType = "Cedula", string? idNumber = null, string? email = "ana.perez@correo.test", string? address = "Av. Principal 123")
    {
        var response = await owner.PostAsJsonAsync("/api/v1/members", new
        {
            branchId = branch, firstName = "Ana", lastName = "Pérez", idType, idNumber = idNumber ?? MemberTests.NewCedula(), birthDate = "1992-03-04", email, phone = "0991234567", address
        });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> NewObligationAsync(HttpClient owner, Guid memberId, decimal amount, string taxKind = "General")
    {
        var response = await owner.PostAsJsonAsync("/api/v1/payment-obligations", new { memberId, concept = "Membresía mensual", amount, taxKind });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static Task<PaymentDto> PayAsync(HttpClient owner, Guid memberId, Guid obligationId, decimal amount, string method = "Card") =>
        CommercialTestApi.PayAsync(owner, memberId, amount, method, "VCH-" + Guid.NewGuid().ToString("N")[..6], [new { obligationId, amount }]);

    private static async Task<(HttpStatusCode Status, JsonElement Body)> PreviewAsync(HttpClient client, Guid obligationId, string? buyerIdType = null)
    {
        var url = $"/api/v1/invoicing/sources/obligations/{obligationId}/preview" + (buyerIdType is null ? string.Empty : $"?buyerIdType={buyerIdType}");
        var response = await client.GetAsync(url);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, string.IsNullOrEmpty(text) ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private static bool HasIssue(JsonElement preview, string code, bool? blocking = null) =>
        preview.GetProperty("issues").EnumerateArray().Any(i => i.GetProperty("code").GetString() == code && (blocking is null || i.GetProperty("blocking").GetBoolean() == blocking));

    private static byte[] P12()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN=Gimnasio Prueba, SERIALNUMBER={Ruc}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, critical: true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(365));
        return certificate.Export(X509ContentType.Pfx, CertPassword);
    }

    /// <summary>Deja al gimnasio listo para emitir: emisor, un punto de emisión y un certificado de prueba.</summary>
    private static async Task ConfigureIssuerAsync(HttpClient owner)
    {
        var issuer = await owner.PutAsJsonAsync("/api/v1/invoicing/issuer", new
        {
            ruc = Ruc, legalName = "Fitness Quito S.A.", tradeName = "Fitness Quito", headOfficeAddress = "Av. Amazonas N24-03 y Colón, Quito", taxRegime = "General", accountingRequired = true,
            specialTaxpayerNumber = (string?)null, withholdingAgentNumber = (string?)null, environment = "Test", notificationEmail = "facturas@fitness.test", confirmProduction = false
        });
        Assert.Equal(HttpStatusCode.OK, issuer.StatusCode);
        var point = await owner.PostAsJsonAsync("/api/v1/invoicing/emission-points", new { branchId = (Guid?)null, establishmentCode = "001", pointCode = "001", establishmentAddress = "Av. Amazonas N24-03 y Colón, Quito" });
        Assert.Equal(HttpStatusCode.OK, point.StatusCode);
        using var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(P12());
        part.Headers.ContentType = new MediaTypeHeaderValue("application/x-pkcs12");
        form.Add(part, "file", "firma.p12");
        form.Add(new StringContent(CertPassword), "password");
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsync("/api/v1/invoicing/certificate", form)).StatusCode);
    }

    private async Task<HttpClient> StaffAsync(TestTenant tenant, string role, params Guid[] branches)
    {
        var email = $"{role.ToLowerInvariant()}-{Guid.NewGuid():N}"[..24] + "@ori.test";
        await TestApi.CreateUserAsync(tenant.Owner, email, role, branches);
        return TestApi.Authenticated(api, await TestApi.LoginAsync(api, tenant.Slug, email, TestApi.UserPassword));
    }

    // ---- el contrato entrega lo cobrado ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_paid_obligation_is_delivered_with_buyer_frozen_tax_and_confirmed_payments()
    {
        var gym = await NewGymAsync();
        var owner = gym.Tenant.Owner;
        await ConfigureIssuerAsync(owner);
        var cedula = MemberTests.NewCedula();
        var member = await NewMemberAsync(owner, gym.Branch, idNumber: cedula);
        var obligation = await NewObligationAsync(owner, member, 30m);
        await PayAsync(owner, member, obligation, 30m);

        var (status, preview) = await PreviewAsync(owner, obligation);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("obligation", preview.GetProperty("sourceType").GetString());
        Assert.Equal("Paid", preview.GetProperty("sourceStatus").GetString());
        Assert.Equal("USD", preview.GetProperty("currency").GetString());
        Assert.Equal(30m, preview.GetProperty("payableTotal").GetDecimal());
        Assert.Equal(30m, preview.GetProperty("paidTotal").GetDecimal());

        var buyer = preview.GetProperty("buyer");
        Assert.Equal("05", buyer.GetProperty("idTypeCode").GetString());
        Assert.Equal(cedula, buyer.GetProperty("idNumber").GetString());
        Assert.Equal("Ana Pérez", buyer.GetProperty("legalName").GetString());
        Assert.Equal("ana.perez@correo.test", buyer.GetProperty("email").GetString());
        Assert.False(buyer.GetProperty("isFinalConsumer").GetBoolean());

        var line = Assert.Single(preview.GetProperty("lines").EnumerateArray());
        Assert.Equal("Membresía mensual", line.GetProperty("description").GetString());
        Assert.Equal("General", line.GetProperty("taxKind").GetString());
        Assert.Equal(30m, line.GetProperty("payableAmount").GetDecimal());
        Assert.Equal(line.GetProperty("payableAmount").GetDecimal(), line.GetProperty("subtotal").GetDecimal() + line.GetProperty("taxAmount").GetDecimal());
        Assert.True(line.GetProperty("taxPercent").GetDecimal() > 0m);

        var payment = Assert.Single(preview.GetProperty("payments").EnumerateArray());
        Assert.Equal("Card", payment.GetProperty("method").GetString());
        Assert.Equal(30m, payment.GetProperty("amount").GetDecimal());

        Assert.True(preview.GetProperty("issuerReady").GetBoolean());
        Assert.Empty(preview.GetProperty("issues").EnumerateArray());
        Assert.True(preview.GetProperty("canIssue").GetBoolean());
    }

    [Fact]
    public async Task Without_tax_configuration_it_cannot_issue_even_if_the_sale_is_paid()
    {
        var gym = await NewGymAsync();
        var member = await NewMemberAsync(gym.Tenant.Owner, gym.Branch);
        var obligation = await NewObligationAsync(gym.Tenant.Owner, member, 30m);
        await PayAsync(gym.Tenant.Owner, member, obligation, 30m);

        var (_, preview) = await PreviewAsync(gym.Tenant.Owner, obligation);

        Assert.False(preview.GetProperty("issuerReady").GetBoolean());
        Assert.True(HasIssue(preview, "issuer_not_ready", true));
        Assert.False(preview.GetProperty("canIssue").GetBoolean());
    }

    [Fact]
    public async Task Only_confirmed_and_complete_payments_make_an_obligation_invoiceable()
    {
        var gym = await NewGymAsync();
        var owner = gym.Tenant.Owner;
        await ConfigureIssuerAsync(owner);
        var member = await NewMemberAsync(owner, gym.Branch);

        // Sin pagos y con un abono: sigue debiendo.
        var open = await NewObligationAsync(owner, member, 30m);
        var (_, unpaid) = await PreviewAsync(owner, open);
        Assert.Equal("Open", unpaid.GetProperty("sourceStatus").GetString());
        Assert.True(HasIssue(unpaid, "source_not_paid", true));
        await PayAsync(owner, member, open, 10m);
        var (_, partial) = await PreviewAsync(owner, open);
        Assert.True(HasIssue(partial, "source_not_paid", true));
        Assert.Equal(10m, partial.GetProperty("paidTotal").GetDecimal());

        // Una transferencia sin verificar no cuenta como cobrada.
        var transfer = await NewObligationAsync(owner, member, 20m);
        await PayAsync(owner, member, transfer, 20m, "BankTransfer");
        var (_, pending) = await PreviewAsync(owner, transfer);
        Assert.True(HasIssue(pending, "source_not_paid", true));
        Assert.Empty(pending.GetProperty("payments").EnumerateArray());

        // Una obligación anulada no se factura.
        var voided = await NewObligationAsync(owner, member, 15m);
        Assert.True((await owner.PostAsJsonAsync($"/api/v1/payment-obligations/{voided}/void", new { reason = "Se anuló la venta" })).IsSuccessStatusCode);
        var (_, cancelled) = await PreviewAsync(owner, voided);
        Assert.Equal("Voided", cancelled.GetProperty("sourceStatus").GetString());
        Assert.True(HasIssue(cancelled, "source_voided", true));
        Assert.False(cancelled.GetProperty("canIssue").GetBoolean());
    }

    // ---- receptores -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Final_consumer_is_allowed_only_up_to_the_limit_and_never_by_default()
    {
        var gym = await NewGymAsync();
        var owner = gym.Tenant.Owner;
        await ConfigureIssuerAsync(owner);
        var member = await NewMemberAsync(owner, gym.Branch);
        var small = await NewObligationAsync(owner, member, 30m);
        await PayAsync(owner, member, small, 30m);
        var big = await NewObligationAsync(owner, member, 80m);
        await PayAsync(owner, member, big, 80m);

        // Por defecto se identifica a la persona, aunque la venta sea pequeña.
        var (_, byDefault) = await PreviewAsync(owner, small);
        Assert.Equal("05", byDefault.GetProperty("buyer").GetProperty("idTypeCode").GetString());

        var (_, consumer) = await PreviewAsync(owner, small, "FinalConsumer");
        var buyer = consumer.GetProperty("buyer");
        Assert.Equal("07", buyer.GetProperty("idTypeCode").GetString());
        Assert.Equal("9999999999999", buyer.GetProperty("idNumber").GetString());
        Assert.Equal("CONSUMIDOR FINAL", buyer.GetProperty("legalName").GetString());
        Assert.Equal(JsonValueKind.Null, buyer.GetProperty("email").ValueKind);
        Assert.True(consumer.GetProperty("canIssue").GetBoolean());
        Assert.Equal(50m, consumer.GetProperty("finalConsumerMaxAmount").GetDecimal());

        var (_, tooBig) = await PreviewAsync(owner, big, "FinalConsumer");
        Assert.True(HasIssue(tooBig, "final_consumer_over_limit", true));
        Assert.False(tooBig.GetProperty("canIssue").GetBoolean());
    }

    [Fact]
    public async Task A_document_registered_as_other_needs_a_chosen_SRI_type_and_a_passport_maps_to_06()
    {
        var gym = await NewGymAsync();
        var owner = gym.Tenant.Owner;
        await ConfigureIssuerAsync(owner);

        var foreigner = await NewMemberAsync(owner, gym.Branch, "Other", "DOC12345");
        var other = await NewObligationAsync(owner, foreigner, 30m);
        await PayAsync(owner, foreigner, other, 30m);
        var (_, unresolved) = await PreviewAsync(owner, other);
        Assert.True(HasIssue(unresolved, "id_type_required", true));
        Assert.Equal(JsonValueKind.Null, unresolved.GetProperty("buyer").ValueKind);
        var (_, asForeign) = await PreviewAsync(owner, other, "Foreign");
        Assert.Equal("08", asForeign.GetProperty("buyer").GetProperty("idTypeCode").GetString());
        Assert.True(asForeign.GetProperty("canIssue").GetBoolean());

        var tourist = await NewMemberAsync(owner, gym.Branch, "Passport", "X1234567");
        var pass = await NewObligationAsync(owner, tourist, 30m);
        await PayAsync(owner, tourist, pass, 30m);
        var (_, passport) = await PreviewAsync(owner, pass);
        Assert.Equal("06", passport.GetProperty("buyer").GetProperty("idTypeCode").GetString());
    }

    [Fact]
    public async Task Missing_email_or_address_only_warns_while_a_wrong_chosen_type_blocks()
    {
        var gym = await NewGymAsync();
        var owner = gym.Tenant.Owner;
        await ConfigureIssuerAsync(owner);
        var member = await NewMemberAsync(owner, gym.Branch, email: null, address: null);
        var obligation = await NewObligationAsync(owner, member, 30m);
        await PayAsync(owner, member, obligation, 30m);

        var (_, preview) = await PreviewAsync(owner, obligation);
        Assert.True(HasIssue(preview, "email_missing", false));
        Assert.True(HasIssue(preview, "address_missing", false));
        Assert.True(preview.GetProperty("canIssue").GetBoolean());

        // Una cédula no es un RUC: elegir ese tipo la bloquea.
        var (_, asRuc) = await PreviewAsync(owner, obligation, "Ruc");
        Assert.True(HasIssue(asRuc, "ruc_invalid", true));
        Assert.False(asRuc.GetProperty("canIssue").GetBoolean());
    }

    [Fact]
    public async Task Tax_free_sales_keep_their_frozen_tax_kind_and_have_no_tax_amount()
    {
        var gym = await NewGymAsync();
        var owner = gym.Tenant.Owner;
        await ConfigureIssuerAsync(owner);
        var member = await NewMemberAsync(owner, gym.Branch);
        var obligation = await NewObligationAsync(owner, member, 25m, "Zero");
        await PayAsync(owner, member, obligation, 25m);

        var (_, preview) = await PreviewAsync(owner, obligation);

        var line = Assert.Single(preview.GetProperty("lines").EnumerateArray());
        Assert.Equal("Zero", line.GetProperty("taxKind").GetString());
        Assert.Equal(0m, line.GetProperty("taxAmount").GetDecimal());
        Assert.Equal(25m, line.GetProperty("subtotal").GetDecimal());
    }

    // ---- permisos y aislamiento -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Only_staff_who_can_issue_see_the_preview_and_only_within_their_branches()
    {
        var gym = await NewGymAsync();
        var owner = gym.Tenant.Owner;
        var otherBranch = await TestApi.CreateBranchAsync(owner, "GYE");
        var member = await NewMemberAsync(owner, gym.Branch);
        var obligation = await NewObligationAsync(owner, member, 30m);
        await PayAsync(owner, member, obligation, 30m);

        var cashier = await StaffAsync(gym.Tenant, "Cajero", gym.Branch);
        var cashierElsewhere = await StaffAsync(gym.Tenant, "Cajero", otherBranch);
        var trainer = await StaffAsync(gym.Tenant, "Entrenador", gym.Branch);
        var reception = await StaffAsync(gym.Tenant, "Recepcion", gym.Branch);
        using var anonymous = api.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await PreviewAsync(cashier, obligation)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await PreviewAsync(cashierElsewhere, obligation)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await PreviewAsync(trainer, obligation)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await PreviewAsync(reception, obligation)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PreviewAsync(anonymous, obligation)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await PreviewAsync(owner, Guid.NewGuid())).Status);
    }

    [Fact]
    public async Task One_gym_cannot_preview_the_sales_of_another()
    {
        var gym = await NewGymAsync();
        var member = await NewMemberAsync(gym.Tenant.Owner, gym.Branch);
        var obligation = await NewObligationAsync(gym.Tenant.Owner, member, 30m);
        await PayAsync(gym.Tenant.Owner, member, obligation, 30m);
        var stranger = await NewGymAsync();

        var (status, body) = await PreviewAsync(stranger.Tenant.Owner, obligation);

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal("invoicing.source_not_found", body.GetProperty("code").GetString());
    }
}
