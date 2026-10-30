using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace FitCore.Api.IntegrationTests.Infrastructure;

/// <summary>Utilidades de las pruebas de facturación electrónica. Los certificados son autofirmados y se generan en memoria: jamás se usa uno real.</summary>
public static class InvoicingTestApi
{
    public const string Ruc = "1790000028001";
    public const string CertPassword = "Certificado-Prueba-2026!";

    public static async Task<Guid> NewMemberAsync(HttpClient owner, Guid branch, string idType = "Cedula", string? idNumber = null, string? email = "ana.perez@correo.test", string? address = "Av. Principal 123")
    {
        var response = await owner.PostAsJsonAsync("/api/v1/members", new
        {
            branchId = branch, firstName = "Ana", lastName = "Pérez", idType, idNumber = idNumber ?? MemberTests.NewCedula(), birthDate = "1992-03-04", email, phone = "0991234567", address
        });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    public static async Task<Guid> NewObligationAsync(HttpClient owner, Guid memberId, decimal amount, string taxKind = "General")
    {
        var response = await owner.PostAsJsonAsync("/api/v1/payment-obligations", new { memberId, concept = "Membresía mensual", amount, taxKind });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    public static Task<PaymentDto> PayAsync(HttpClient owner, Guid memberId, Guid obligationId, decimal amount, string method = "Card") =>
        CommercialTestApi.PayAsync(owner, memberId, amount, method, "VCH-" + Guid.NewGuid().ToString("N")[..6], [new { obligationId, amount }]);

    /// <summary>Una venta cobrada y lista para facturar.</summary>
    public static async Task<(Guid MemberId, Guid ObligationId)> PaidSaleAsync(HttpClient owner, Guid branch, decimal amount, string method = "Card", string taxKind = "General")
    {
        var member = await NewMemberAsync(owner, branch);
        var obligation = await NewObligationAsync(owner, member, amount, taxKind);
        await PayAsync(owner, member, obligation, amount, method);
        return (member, obligation);
    }

    public static byte[] P12()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN=Gimnasio Prueba, SERIALNUMBER={Ruc}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, critical: true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(365));
        return certificate.Export(X509ContentType.Pfx, CertPassword);
    }

    public static object IssuerBody(string environment = "Test", bool confirmProduction = false, string cardPaymentMethod = "Credit") => new
    {
        ruc = Ruc, legalName = "Fitness Quito S.A.", tradeName = "Fitness Quito", headOfficeAddress = "Av. Amazonas N24-03 y Colón, Quito", taxRegime = "General", accountingRequired = true,
        specialTaxpayerNumber = (string?)null, withholdingAgentNumber = (string?)null, environment, notificationEmail = "facturas@fitness.test", confirmProduction, cardPaymentMethod
    };

    /// <summary>Deja al gimnasio listo para emitir: emisor, un punto de emisión (sin sucursal salvo que se indique) y un certificado de prueba.</summary>
    public static async Task ConfigureIssuerAsync(HttpClient owner, string establishmentCode = "001", string pointCode = "001", Guid? branchId = null, string cardPaymentMethod = "Credit")
    {
        var issuer = await owner.PutAsJsonAsync("/api/v1/invoicing/issuer", IssuerBody(cardPaymentMethod: cardPaymentMethod));
        Assert.Equal(HttpStatusCode.OK, issuer.StatusCode);
        await AddEmissionPointAsync(owner, establishmentCode, pointCode, branchId);
        using var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(P12());
        part.Headers.ContentType = new MediaTypeHeaderValue("application/x-pkcs12");
        form.Add(part, "file", "firma.p12");
        form.Add(new StringContent(CertPassword), "password");
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsync("/api/v1/invoicing/certificate", form)).StatusCode);
    }

    public static async Task<Guid> AddEmissionPointAsync(HttpClient owner, string establishmentCode, string pointCode, Guid? branchId = null)
    {
        var response = await owner.PostAsJsonAsync("/api/v1/invoicing/emission-points", new { branchId, establishmentCode, pointCode, establishmentAddress = "Av. Amazonas N24-03 y Colón, Quito" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var points = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("emissionPoints").EnumerateArray();
        return points.Single(p => p.GetProperty("establishmentCode").GetString() == establishmentCode && p.GetProperty("pointCode").GetString() == pointCode).GetProperty("id").GetGuid();
    }
}
