using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FitCore.Invoicing.Application.Security;
using FitCore.Invoicing.Domain;
using Microsoft.Extensions.Options;

namespace FitCore.Invoicing.UnitTests;

/// <summary>Certificados de prueba generados en memoria (autofirmados, solo para estas pruebas; jamás se usa un certificado real).</summary>
internal static class TestCertificates
{
    public static byte[] Rsa(string password, DateTimeOffset notBefore, DateTimeOffset notAfter, string subject = "CN=Gimnasio de Prueba, SERIALNUMBER=1790000028001",
        X509KeyUsageFlags usage = X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(usage, critical: true));
        using var certificate = request.CreateSelfSigned(notBefore, notAfter);
        return certificate.Export(X509ContentType.Pfx, password);
    }

    public static byte[] Ecdsa(string password, DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Curva Eliptica", key, HashAlgorithmName.SHA256);
        using var certificate = request.CreateSelfSigned(notBefore, notAfter);
        return certificate.Export(X509ContentType.Pfx, password);
    }

    public static byte[] PublicOnly()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Solo publico", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return certificate.Export(X509ContentType.Cert); // sin clave privada
    }
}

public class CertificateReaderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_valid_certificate_is_read_with_its_public_data_and_the_ruc_in_its_subject()
    {
        var p12 = TestCertificates.Rsa("Clave-Prueba-1", Now.AddDays(-10), Now.AddDays(300));

        var result = CertificateReader.Read(p12, "Clave-Prueba-1", Now);

        Assert.True(result.IsSuccess);
        Assert.Contains("Gimnasio de Prueba", result.Value.Subject);
        Assert.Equal("1790000028001", result.Value.RucInSubject);
        Assert.Equal(64, result.Value.Thumbprint.Length);
        Assert.InRange(result.Value.NotAfter.ToUnixTimeSeconds(), Now.AddDays(300).ToUnixTimeSeconds() - 5, Now.AddDays(300).ToUnixTimeSeconds() + 5);
    }

    [Fact]
    public void A_subject_without_a_valid_ruc_reports_none()
    {
        var p12 = TestCertificates.Rsa("x", Now.AddDays(-1), Now.AddDays(30), "CN=Sin RUC 1234567890123");

        Assert.Null(CertificateReader.Read(p12, "x", Now).Value.RucInSubject); // 13 dígitos pero no es un RUC válido
    }

    [Fact]
    public void A_wrong_password_or_a_file_that_is_not_a_p12_is_refused_the_same_way()
    {
        var p12 = TestCertificates.Rsa("correcta", Now.AddDays(-1), Now.AddDays(30));

        Assert.Equal("invoicing.certificate_password_invalid", CertificateReader.Read(p12, "incorrecta", Now).Error.Code);
        Assert.Equal("invoicing.certificate_password_invalid", CertificateReader.Read([1, 2, 3, 4, 5], "x", Now).Error.Code);
        Assert.Equal("invoicing.certificate_password_invalid", CertificateReader.Read([], "x", Now).Error.Code);
    }

    [Fact]
    public void An_expired_or_not_yet_valid_certificate_is_refused()
    {
        var expired = TestCertificates.Rsa("x", Now.AddDays(-400), Now.AddDays(-1));
        var future = TestCertificates.Rsa("x", Now.AddDays(5), Now.AddDays(400));

        Assert.Equal("invoicing.certificate_expired", CertificateReader.Read(expired, "x", Now).Error.Code);
        Assert.Equal("invoicing.certificate_not_yet_valid", CertificateReader.Read(future, "x", Now).Error.Code);
    }

    [Fact]
    public void A_certificate_without_private_key_is_refused()
    {
        var result = CertificateReader.Read(TestCertificates.PublicOnly(), string.Empty, DateTimeOffset.UtcNow);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Error.Code, new[] { "invoicing.certificate_no_private_key", "invoicing.certificate_password_invalid" });
    }

    [Fact]
    public void A_non_rsa_key_is_refused()
    {
        var p12 = TestCertificates.Ecdsa("x", Now.AddDays(-1), Now.AddDays(30));

        Assert.Equal("invoicing.certificate_not_rsa", CertificateReader.Read(p12, "x", Now).Error.Code);
    }

    [Fact]
    public void A_certificate_that_cannot_sign_is_refused()
    {
        var p12 = TestCertificates.Rsa("x", Now.AddDays(-1), Now.AddDays(30), usage: X509KeyUsageFlags.KeyEncipherment);

        Assert.Equal("invoicing.certificate_cannot_sign", CertificateReader.Read(p12, "x", Now).Error.Code);
    }
}

public class SecretProtectorTests
{
    private static readonly string Key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static AesGcmSecretProtector Protector(string? key = null, int version = 1) =>
        new(Options.Create(new InvoicingOptions { MasterKey = key ?? Key, MasterKeyVersion = version }));

    [Fact]
    public void A_secret_survives_a_round_trip()
    {
        var protector = Protector();
        var secret = RandomNumberGenerator.GetBytes(4000);

        var package = protector.Protect(secret, "ctx");

        Assert.Equal(secret, protector.Unprotect(package, "ctx"));
    }

    [Fact]
    public void The_package_does_not_contain_the_plain_secret_and_differs_every_time()
    {
        var protector = Protector();
        var secret = System.Text.Encoding.UTF8.GetBytes("MiClaveSuperSecreta-12345");

        var a = protector.Protect(secret, "ctx");
        var b = protector.Protect(secret, "ctx");

        Assert.False(a.AsSpan().IndexOf(secret) >= 0);
        Assert.NotEqual(a, b); // nonces y claves de datos aleatorios
    }

    [Fact]
    public void A_tampered_package_a_wrong_context_or_a_wrong_master_key_cannot_be_opened()
    {
        var protector = Protector();
        var package = protector.Protect([1, 2, 3, 4, 5, 6, 7, 8], "gimnasio-A");

        var tampered = (byte[])package.Clone();
        tampered[^1] ^= 0xFF;

        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(tampered, "gimnasio-A"));
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(package, "gimnasio-B")); // copiado a otro gimnasio
        Assert.ThrowsAny<CryptographicException>(() => Protector(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))).Unprotect(package, "gimnasio-A"));
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(package.AsSpan(0, 20), "gimnasio-A"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("no-es-base64!!")]
    [InlineData("YWJj")] // base64 válido pero solo 3 bytes
    public void Without_a_valid_master_key_the_protector_is_not_configured_and_refuses_to_work(string key)
    {
        var protector = Protector(key);

        Assert.False(protector.IsConfigured);
        Assert.Throws<InvalidOperationException>(() => protector.Protect([1], "ctx"));
    }

    [Fact]
    public void The_key_version_is_exposed_so_it_can_be_stored_with_each_secret()
    {
        Assert.Equal(3, Protector(version: 3).KeyVersion);
        Assert.True(Protector().IsConfigured);
    }
}

public class IssuerReadinessTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private static IssuerProfile Issuer(TaxRegime regime = TaxRegime.General, string ruc = "1790000028001") => new()
    {
        TenantId = Guid.NewGuid(), Ruc = ruc, LegalName = "Fitness SA", HeadOfficeAddress = "Quito", TaxRegime = regime, NotificationEmail = "facturas@fitness.test"
    };

    private static EmissionPoint Point(bool active = true, string establishment = "001", string point = "001") => new()
    {
        Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), EstablishmentCode = establishment, PointCode = point, EstablishmentAddress = "Av. Amazonas", IsActive = active
    };

    private static SigningCertificate Certificate(DateTimeOffset? notAfter = null, bool active = true) => new()
    {
        Id = Guid.NewGuid(), IsActive = active, NotBefore = Now.AddDays(-10), NotAfter = notAfter ?? Now.AddDays(300), EncryptedP12 = active ? [1] : [], EncryptedPassword = active ? [1] : []
    };

    private static bool Ok(IEnumerable<ReadinessCheck> checks, string code) => checks.Single(c => c.Code == code).Ok;

    [Fact]
    public void A_complete_configuration_can_issue()
    {
        var checks = IssuerReadiness.Evaluate(Issuer(), [Point()], Certificate(), masterKeyConfigured: true, Now, certificateMatchesRuc: true);

        Assert.True(IssuerReadiness.CanIssue(checks));
        Assert.All(checks.Where(c => c.Blocking), c => Assert.True(c.Ok, c.Code));
    }

    [Fact]
    public void An_empty_configuration_cannot_issue_and_says_what_is_missing()
    {
        var checks = IssuerReadiness.Evaluate(null, [], null, masterKeyConfigured: false, Now, null);

        Assert.False(IssuerReadiness.CanIssue(checks));
        foreach (var code in new[] { "issuer_profile", "ruc_valid", "emission_point", "master_key", "certificate", "certificate_valid" })
        {
            Assert.False(Ok(checks, code), code);
        }
    }

    [Theory]
    [InlineData("1790000029001")] // verificador incorrecto
    [InlineData("179000002800")] // corto
    [InlineData("abc")]
    public void An_invalid_ruc_blocks_issuing(string ruc)
    {
        var checks = IssuerReadiness.Evaluate(Issuer(ruc: ruc), [Point()], Certificate(), true, Now, null);

        Assert.False(Ok(checks, "ruc_valid"));
        Assert.False(IssuerReadiness.CanIssue(checks));
    }

    [Fact]
    public void The_popular_business_regime_cannot_issue_electronic_invoices()
    {
        var checks = IssuerReadiness.Evaluate(Issuer(TaxRegime.RimpeNegocioPopular), [Point()], Certificate(), true, Now, null);

        Assert.False(Ok(checks, "regime_allows_electronic_invoice"));
        Assert.False(IssuerReadiness.CanIssue(checks));
        Assert.True(IssuerReadiness.CanIssue(IssuerReadiness.Evaluate(Issuer(TaxRegime.RimpeEmprendedor), [Point()], Certificate(), true, Now, null)));
    }

    [Fact]
    public void Only_an_active_emission_point_with_valid_codes_counts()
    {
        Assert.False(Ok(IssuerReadiness.Evaluate(Issuer(), [Point(active: false)], Certificate(), true, Now, null), "emission_point"));
        Assert.False(Ok(IssuerReadiness.Evaluate(Issuer(), [Point(establishment: "000")], Certificate(), true, Now, null), "emission_point"));
        Assert.False(Ok(IssuerReadiness.Evaluate(Issuer(), [Point(point: "1")], Certificate(), true, Now, null), "emission_point"));
        Assert.True(Ok(IssuerReadiness.Evaluate(Issuer(), [Point(active: false), Point(point: "002")], Certificate(), true, Now, null), "emission_point"));
    }

    [Fact]
    public void An_expired_or_retired_certificate_blocks_and_one_close_to_expiry_only_warns()
    {
        var expired = IssuerReadiness.Evaluate(Issuer(), [Point()], Certificate(Now.AddDays(-1)), true, Now, null);
        var retired = IssuerReadiness.Evaluate(Issuer(), [Point()], Certificate(active: false), true, Now, null);
        var soon = IssuerReadiness.Evaluate(Issuer(), [Point()], Certificate(Now.AddDays(10)), true, Now, null);

        Assert.False(IssuerReadiness.CanIssue(expired));
        Assert.False(IssuerReadiness.CanIssue(retired));
        Assert.True(IssuerReadiness.CanIssue(soon));
        Assert.False(Ok(soon, "certificate_expiring"));
    }

    [Fact]
    public void A_certificate_of_another_ruc_and_the_test_environment_are_only_warnings()
    {
        var checks = IssuerReadiness.Evaluate(Issuer(), [Point()], Certificate(), true, Now, certificateMatchesRuc: false);

        Assert.True(IssuerReadiness.CanIssue(checks));
        Assert.False(Ok(checks, "certificate_matches_ruc"));
        Assert.False(Ok(checks, "production_environment")); // el emisor de la prueba está en ambiente de pruebas
        Assert.All(checks.Where(c => c.Code is "certificate_matches_ruc" or "production_environment" or "notification_email" or "certificate_expiring"), c => Assert.False(c.Blocking));
    }
}
