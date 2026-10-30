using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using FitCore.SharedKernel;

namespace FitCore.Invoicing.Application.Security;

/// <summary>Datos públicos de un certificado de firma (nunca la clave privada ni la contraseña).</summary>
public sealed record CertificateInfo(
    string Subject, string Issuer, string SerialNumber, string Thumbprint, DateTimeOffset NotBefore, DateTimeOffset NotAfter, string? RucInSubject);

public static class CertificateErrors
{
    public static readonly Error PasswordInvalid = Error.Validation("invoicing.certificate_password_invalid", "La contraseña del certificado no es correcta o el archivo no es un .p12 válido.");
    public static readonly Error NoPrivateKey = Error.Validation("invoicing.certificate_no_private_key", "El archivo no contiene la clave privada del certificado.");
    public static readonly Error NotRsa = Error.Validation("invoicing.certificate_not_rsa", "El certificado de firma debe usar una clave RSA.");
    public static readonly Error CannotSign = Error.Validation("invoicing.certificate_cannot_sign", "El certificado no está habilitado para firmar documentos.");
    public static readonly Error NotYetValid = Error.Validation("invoicing.certificate_not_yet_valid", "El certificado todavía no está vigente.");
    public static readonly Error Expired = Error.Validation("invoicing.certificate_expired", "El certificado está vencido.");
}

/// <summary>
/// Abre un archivo .p12 en memoria (sin tocar el almacén de certificados del sistema) y comprueba lo necesario para firmar: contraseña, clave privada RSA,
/// uso de firma y vigencia. El RUC del titular se busca en el sujeto solo como ayuda: no todos los certificados lo traen ahí.
/// </summary>
public static partial class CertificateReader
{
    public static Result<CertificateInfo> Read(byte[] p12, string password, DateTimeOffset now)
    {
        X509Certificate2 certificate;
        try
        {
            certificate = X509CertificateLoader.LoadPkcs12(p12, password, X509KeyStorageFlags.EphemeralKeySet);
        }
        catch (CryptographicException)
        {
            return CertificateErrors.PasswordInvalid;
        }

        using (certificate)
        {
            if (!certificate.HasPrivateKey)
            {
                return CertificateErrors.NoPrivateKey;
            }

            using (var rsa = certificate.GetRSAPrivateKey())
            {
                if (rsa is null)
                {
                    return CertificateErrors.NotRsa;
                }
            }

            var usage = certificate.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault();
            if (usage is not null && (usage.KeyUsages & (X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation)) == 0)
            {
                return CertificateErrors.CannotSign;
            }

            var notBefore = new DateTimeOffset(certificate.NotBefore.ToUniversalTime(), TimeSpan.Zero);
            var notAfter = new DateTimeOffset(certificate.NotAfter.ToUniversalTime(), TimeSpan.Zero);
            if (now < notBefore)
            {
                return CertificateErrors.NotYetValid;
            }

            if (now >= notAfter)
            {
                return CertificateErrors.Expired;
            }

            var ruc = ThirteenDigits().Match(certificate.Subject) is { Success: true } m && EcuadorIds.IsValidRuc(m.Value) ? m.Value : null;
            return new CertificateInfo(
                certificate.Subject, certificate.Issuer, certificate.SerialNumber, Convert.ToHexString(certificate.GetCertHash(HashAlgorithmName.SHA256)).ToLowerInvariant(),
                notBefore, notAfter, ruc);
        }
    }

    [GeneratedRegex(@"(?<!\d)\d{13}(?!\d)")]
    private static partial Regex ThirteenDigits();
}
