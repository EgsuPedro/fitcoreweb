using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Xml;

namespace FitCore.Invoicing.Xml;

/// <summary>Resultado de comprobar una firma: si es válida y, si no, por qué.</summary>
public sealed record SignatureCheck(bool IsValid, IReadOnlyList<string> Problems, string? CertificateThumbprint, DateTimeOffset? SigningTime);

/// <summary>
/// Firma XAdES-BES «enveloped» del comprobante con el perfil del SRI (ficha técnica, sección 6 y Anexo 14, versión 2.20): firma <b>RSA-SHA1</b> con huellas <b>SHA-1</b>, canonicalización
/// <c>xml-c14n-20010315</c>, esquema XAdES 1.3.2, <c>ds:KeyInfo</c> con el certificado en base64 y la clave RSA, y tres referencias firmadas: las propiedades firmadas (<c>SignedProperties</c>), el
/// certificado (<c>KeyInfo</c>) y todo el comprobante (<c>#comprobante</c>, con transformación enveloped). SHA-1 es débil, pero es lo que exige el SRI; se confirma con su ambiente de pruebas
/// (bloque 7). La estructura se arma a mano, con los prefijos <c>ds:</c> y <c>etsi:</c> del ejemplo oficial, porque la canonicalización depende de ellos; la verificación usa el verificador independiente
/// de .NET (<see cref="SignedXml"/>).
/// </summary>
public static class XadesBesSigner
{
    public const string DsNamespace = "http://www.w3.org/2000/09/xmldsig#";
    public const string EtsiNamespace = "http://uri.etsi.org/01903/v1.3.2#";
    private const string C14N = "http://www.w3.org/TR/2001/REC-xml-c14n-20010315";
    private const string RsaSha1 = "http://www.w3.org/2000/09/xmldsig#rsa-sha1";
    private const string Sha1 = "http://www.w3.org/2000/09/xmldsig#sha1";
    private const string Enveloped = "http://www.w3.org/2000/09/xmldsig#enveloped-signature";
    private const string SignedPropertiesType = "http://uri.etsi.org/01903#SignedProperties";

    /// <summary>Firma el comprobante. <paramref name="certificate"/> debe traer la clave privada RSA.</summary>
    public static string Sign(string xml, X509Certificate2 certificate, DateTimeOffset signingTime)
    {
        using var rsa = certificate.GetRSAPrivateKey() ?? throw new InvalidOperationException("El certificado no tiene una clave privada RSA.");
        var document = Load(xml);
        var root = document.DocumentElement ?? throw new InvalidOperationException("El comprobante no tiene elemento raíz.");

        var n = RandomNumberGenerator.GetInt32(100_000, 999_999);
        var signatureId = $"Signature{n}";
        var signedInfoId = $"Signature-SignedInfo{RandomNumberGenerator.GetInt32(100_000, 999_999)}";
        var signatureValueId = $"SignatureValue{RandomNumberGenerator.GetInt32(100_000, 999_999)}";
        var keyInfoId = $"Certificate{RandomNumberGenerator.GetInt32(100_000, 999_999)}";
        var objectId = $"{signatureId}-Object{RandomNumberGenerator.GetInt32(100_000, 999_999)}";
        var signedPropertiesId = $"{signatureId}-SignedProperties{RandomNumberGenerator.GetInt32(100_000, 999_999)}";
        var signedPropertiesRefId = $"SignedPropertiesID{RandomNumberGenerator.GetInt32(100_000, 999_999)}";
        var documentRefId = $"Reference-ID-{RandomNumberGenerator.GetInt32(100_000, 999_999)}";

        // 1. Se arma la firma completa, con los valores de resumen y de firma vacíos, y se la coloca en su lugar definitivo: así los prefijos y los espacios de nombres que se canonicalizan
        //    son exactamente los del documento final.
        var signature = document.CreateElement("ds", "Signature", DsNamespace);
        Declare(signature, "etsi", EtsiNamespace);
        signature.SetAttribute("Id", signatureId);

        var signedInfo = Element(document, "ds", "SignedInfo", DsNamespace, ("Id", signedInfoId));
        signedInfo.AppendChild(Element(document, "ds", "CanonicalizationMethod", DsNamespace, ("Algorithm", C14N)));
        signedInfo.AppendChild(Element(document, "ds", "SignatureMethod", DsNamespace, ("Algorithm", RsaSha1)));
        var propertiesRef = Reference(document, signedPropertiesRefId, $"#{signedPropertiesId}", SignedPropertiesType, enveloped: false);
        var keyInfoRef = Reference(document, null, $"#{keyInfoId}", null, enveloped: false);
        var documentRef = Reference(document, documentRefId, "#comprobante", null, enveloped: true);
        signedInfo.AppendChild(propertiesRef);
        signedInfo.AppendChild(keyInfoRef);
        signedInfo.AppendChild(documentRef);
        signature.AppendChild(signedInfo);

        var signatureValue = Element(document, "ds", "SignatureValue", DsNamespace, ("Id", signatureValueId));
        signature.AppendChild(signatureValue);

        var keyInfo = Element(document, "ds", "KeyInfo", DsNamespace, ("Id", keyInfoId));
        var x509Data = Element(document, "ds", "X509Data", DsNamespace);
        x509Data.AppendChild(Text(document, "ds", "X509Certificate", DsNamespace, Convert.ToBase64String(certificate.RawData)));
        keyInfo.AppendChild(x509Data);
        var keyValue = Element(document, "ds", "KeyValue", DsNamespace);
        var rsaKeyValue = Element(document, "ds", "RSAKeyValue", DsNamespace);
        var parameters = rsa.ExportParameters(includePrivateParameters: false);
        rsaKeyValue.AppendChild(Text(document, "ds", "Modulus", DsNamespace, Convert.ToBase64String(parameters.Modulus!)));
        rsaKeyValue.AppendChild(Text(document, "ds", "Exponent", DsNamespace, Convert.ToBase64String(parameters.Exponent!)));
        keyValue.AppendChild(rsaKeyValue);
        keyInfo.AppendChild(keyValue);
        signature.AppendChild(keyInfo);

        var dataObject = Element(document, "ds", "Object", DsNamespace, ("Id", objectId));
        dataObject.AppendChild(QualifyingProperties(document, signatureId, signedPropertiesId, documentRefId, certificate, signingTime));
        signature.AppendChild(dataObject);

        root.AppendChild(signature);

        // 2. Se serializa y se vuelve a leer: así cada elemento tiene sus declaraciones de espacio de nombres explícitas, igual que en el XML que se enviará, y la canonicalización
        //    que se firma es la misma que verificará el SRI.
        var final = Load(root.OuterXml);
        var finalRoot = final.DocumentElement!;
        var finalSignature = (XmlElement)finalRoot.GetElementsByTagName("Signature", DsNamespace)[0]!;
        var finalSignedInfo = (XmlElement)finalSignature.GetElementsByTagName("SignedInfo", DsNamespace)[0]!;
        var finalKeyInfo = Find(finalSignature, keyInfoId);
        var references = finalSignedInfo.GetElementsByTagName("Reference", DsNamespace).Cast<XmlElement>().ToList();

        // 3. Resúmenes SHA-1 de lo que se firma (propiedades firmadas, certificado y todo el comprobante sin la firma).
        SetDigest(references[0], Digest(Canonical(Find(finalSignature, signedPropertiesId))));
        SetDigest(references[1], Digest(Canonical(finalKeyInfo)));
        SetDigest(references[2], Digest(CanonicalWithoutSignature(final)));

        // 4. La firma RSA-SHA1 se calcula sobre la forma canónica de SignedInfo (ya con los resúmenes).
        var signed = rsa.SignData(Canonical(finalSignedInfo), HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1);
        ((XmlElement)finalSignature.GetElementsByTagName("SignatureValue", DsNamespace)[0]!).InnerText = Convert.ToBase64String(signed);

        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" + finalRoot.OuterXml;
    }

    /// <summary>
    /// Comprueba la firma con el verificador de .NET (resúmenes de las tres referencias y firma RSA con el certificado incorporado) y la estructura exigida por el SRI. Si se indica
    /// <paramref name="expectedThumbprint"/>, el certificado de la firma debe ser ese.
    /// </summary>
    public static SignatureCheck Verify(string signedXml, string? expectedThumbprint = null)
    {
        var problems = new List<string>();
        XmlDocument document;
        try
        {
            document = Load(signedXml);
        }
        catch (XmlException ex)
        {
            return new SignatureCheck(false, [$"XML mal formado: {ex.Message}"], null, null);
        }

        var signatures = document.GetElementsByTagName("Signature", DsNamespace);
        if (signatures.Count != 1 || signatures[0] is not XmlElement signatureElement)
        {
            return new SignatureCheck(false, ["El comprobante debe llevar exactamente una firma."], null, null);
        }

        // Se construye con el propio elemento de la firma: .NET toma de ahí los espacios de nombres heredados (xmlns:ds y xmlns:etsi) que forman parte de la forma canónica de SignedInfo.
        var signedXmlObject = new DocumentSignedXml(signatureElement);
        string? thumbprint = null;
        DateTimeOffset? signingTime = null;
        try
        {
            signedXmlObject.LoadXml(signatureElement);
            var references = signedXmlObject.SignedInfo?.References.Cast<Reference>().ToList() ?? [];
            if (references.Count != 3)
            {
                problems.Add($"Se esperaban 3 referencias firmadas y hay {references.Count}.");
            }

            if (!references.Any(r => r.Uri == "#comprobante"))
            {
                problems.Add("La firma no cubre todo el comprobante (#comprobante).");
            }

            if (signedXmlObject.SignedInfo?.SignatureMethod != RsaSha1)
            {
                problems.Add("El algoritmo de firma debe ser RSA-SHA1.");
            }

            if (signedXmlObject.SignedInfo?.CanonicalizationMethod != C14N)
            {
                problems.Add("La canonicalización debe ser xml-c14n-20010315.");
            }

            if (references.Any(r => r.DigestMethod != Sha1))
            {
                problems.Add("Los resúmenes deben ser SHA-1.");
            }

            var certificate = signedXmlObject.KeyInfo?.OfType<KeyInfoX509Data>().FirstOrDefault()?.Certificates?.OfType<X509Certificate2>().FirstOrDefault();
            if (certificate is null)
            {
                problems.Add("La firma no incluye el certificado en KeyInfo.");
            }
            else
            {
                thumbprint = certificate.GetCertHashString(HashAlgorithmName.SHA256).ToLowerInvariant();
                if (expectedThumbprint is not null && !string.Equals(expectedThumbprint, thumbprint, StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add("La firma no corresponde al certificado esperado.");
                }

                // CheckSignature(cert, verifySignatureOnly:true) valida resúmenes y firma sin exigir cadena de confianza (los certificados de prueba son autofirmados).
                if (!signedXmlObject.CheckSignature(certificate, verifySignatureOnly: true))
                {
                    problems.Add("La firma o alguno de los resúmenes no coincide con el contenido.");
                }

                var qualifying = CheckQualifyingProperties(document, certificate, problems);
                signingTime = qualifying;
            }
        }
        catch (Exception ex) when (ex is CryptographicException or XmlException or InvalidOperationException or ArgumentException)
        {
            problems.Add($"No se pudo verificar la firma: {ex.Message}");
        }

        return new SignatureCheck(problems.Count == 0, problems, thumbprint, signingTime);
    }

    /// <summary>
    /// Verificador de .NET que busca los elementos referenciados (<c>#Id</c>) en el documento tal como llegó. Por defecto regenera <c>KeyInfo</c> a partir de su modelo en lugar de leer el que
    /// vino, y su forma canónica deja de coincidir con la firmada.
    /// </summary>
    private sealed class DocumentSignedXml(XmlElement signature) : SignedXml(signature)
    {
        public override XmlElement? GetIdElement(XmlDocument? doc, string idValue)
        {
            if (doc?.DocumentElement is not { } root)
            {
                return null;
            }

            // <factura id="comprobante"> usa «id» en minúscula; los demás elementos firmados, «Id».
            return root.GetAttribute("id") == idValue
                ? root
                : doc.SelectNodes("//*[@Id]")!.Cast<XmlElement>().SingleOrDefault(e => e.GetAttribute("Id") == idValue);
        }
    }

    // ---- XAdES -----------------------------------------------------------------------------------------------------------------------

    private static XmlElement QualifyingProperties(XmlDocument d, string signatureId, string signedPropertiesId, string documentRefId, X509Certificate2 certificate, DateTimeOffset signingTime)
    {
        var qualifying = Element(d, "etsi", "QualifyingProperties", EtsiNamespace, ("Target", $"#{signatureId}"));
        var signedProperties = Element(d, "etsi", "SignedProperties", EtsiNamespace, ("Id", signedPropertiesId));

        var signatureProperties = Element(d, "etsi", "SignedSignatureProperties", EtsiNamespace);
        signatureProperties.AppendChild(Text(d, "etsi", "SigningTime", EtsiNamespace, signingTime.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture)));
        var signingCertificate = Element(d, "etsi", "SigningCertificate", EtsiNamespace);
        var cert = Element(d, "etsi", "Cert", EtsiNamespace);
        var certDigest = Element(d, "etsi", "CertDigest", EtsiNamespace);
        certDigest.AppendChild(Element(d, "ds", "DigestMethod", DsNamespace, ("Algorithm", Sha1)));
        certDigest.AppendChild(Text(d, "ds", "DigestValue", DsNamespace, Convert.ToBase64String(Digest(certificate.RawData))));
        cert.AppendChild(certDigest);
        var issuerSerial = Element(d, "etsi", "IssuerSerial", EtsiNamespace);
        issuerSerial.AppendChild(Text(d, "ds", "X509IssuerName", DsNamespace, certificate.IssuerName.Name));
        issuerSerial.AppendChild(Text(d, "ds", "X509SerialNumber", DsNamespace, SerialAsDecimal(certificate)));
        cert.AppendChild(issuerSerial);
        signingCertificate.AppendChild(cert);
        signatureProperties.AppendChild(signingCertificate);
        signedProperties.AppendChild(signatureProperties);

        var dataProperties = Element(d, "etsi", "SignedDataObjectProperties", EtsiNamespace);
        var format = Element(d, "etsi", "DataObjectFormat", EtsiNamespace, ("ObjectReference", $"#{documentRefId}"));
        format.AppendChild(Text(d, "etsi", "Description", EtsiNamespace, "contenido comprobante"));
        format.AppendChild(Text(d, "etsi", "MimeType", EtsiNamespace, "text/xml"));
        dataProperties.AppendChild(format);
        signedProperties.AppendChild(dataProperties);

        qualifying.AppendChild(signedProperties);
        return qualifying;
    }

    private static DateTimeOffset? CheckQualifyingProperties(XmlDocument document, X509Certificate2 certificate, List<string> problems)
    {
        var manager = new XmlNamespaceManager(document.NameTable);
        manager.AddNamespace("ds", DsNamespace);
        manager.AddNamespace("etsi", EtsiNamespace);

        var digest = document.SelectSingleNode("//etsi:SigningCertificate/etsi:Cert/etsi:CertDigest/ds:DigestValue", manager)?.InnerText;
        if (digest is null || digest != Convert.ToBase64String(Digest(certificate.RawData)))
        {
            problems.Add("El resumen del certificado en SigningCertificate no coincide con el certificado de la firma.");
        }

        var serial = document.SelectSingleNode("//etsi:IssuerSerial/ds:X509SerialNumber", manager)?.InnerText;
        if (serial != SerialAsDecimal(certificate))
        {
            problems.Add("El número de serie en IssuerSerial no coincide con el del certificado.");
        }

        var format = document.SelectSingleNode("//etsi:DataObjectFormat/@ObjectReference", manager)?.Value;
        var documentReference = document.SelectSingleNode("//ds:SignedInfo/ds:Reference[@URI='#comprobante']/@Id", manager)?.Value;
        if (format is null || documentReference is null || format != "#" + documentReference)
        {
            problems.Add("DataObjectFormat no apunta a la referencia del comprobante.");
        }

        var time = document.SelectSingleNode("//etsi:SigningTime", manager)?.InnerText;
        if (time is null || !DateTimeOffset.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            problems.Add("Falta la hora de firma (SigningTime).");
            return null;
        }

        return parsed;
    }

    // ---- ayudas ----------------------------------------------------------------------------------------------------------------------

    private static XmlDocument Load(string xml)
    {
        var document = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
        document.LoadXml(xml);
        return document;
    }

    private static XmlElement Element(XmlDocument d, string prefix, string name, string ns, params (string Name, string Value)[] attributes)
    {
        var element = d.CreateElement(prefix, name, ns);
        foreach (var (attribute, value) in attributes)
        {
            element.SetAttribute(attribute, value);
        }

        return element;
    }

    private static XmlElement Text(XmlDocument d, string prefix, string name, string ns, string text)
    {
        var element = d.CreateElement(prefix, name, ns);
        element.InnerText = text;
        return element;
    }

    private static XmlElement Reference(XmlDocument d, string? id, string uri, string? type, bool enveloped)
    {
        var reference = Element(d, "ds", "Reference", DsNamespace);
        if (id is not null)
        {
            reference.SetAttribute("Id", id);
        }

        reference.SetAttribute("URI", uri);
        if (type is not null)
        {
            reference.SetAttribute("Type", type);
        }

        if (enveloped)
        {
            var transforms = Element(d, "ds", "Transforms", DsNamespace);
            transforms.AppendChild(Element(d, "ds", "Transform", DsNamespace, ("Algorithm", Enveloped)));
            reference.AppendChild(transforms);
        }

        reference.AppendChild(Element(d, "ds", "DigestMethod", DsNamespace, ("Algorithm", Sha1)));
        reference.AppendChild(d.CreateElement("ds", "DigestValue", DsNamespace));
        return reference;
    }

    private static void SetDigest(XmlElement reference, byte[] digest) =>
        ((XmlElement)reference.GetElementsByTagName("DigestValue", DsNamespace)[0]!).InnerText = Convert.ToBase64String(digest);

    private static XmlElement Find(XmlElement scope, string id) =>
        scope.SelectNodes($".//*[@Id='{id}']")!.Cast<XmlElement>().Single();

    /// <summary>
    /// Forma canónica (c14n inclusiva 1.0) de un elemento y sus descendientes tal como están en el documento: se canonicaliza una copia aislada a la que se le reponen las declaraciones de espacios
    /// de nombres heredadas de sus ancestros, lo que equivale a la forma canónica del subconjunto dentro del documento completo.
    /// </summary>
    private static byte[] Canonical(XmlElement element)
    {
        var isolated = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
        var copy = (XmlElement)isolated.ImportNode(element, deep: true);
        foreach (var (prefix, uri) in InScopeNamespaces(element))
        {
            if (!copy.HasAttribute(prefix is null ? "xmlns" : "xmlns:" + prefix))
            {
                Declare(copy, prefix, uri);
            }
        }

        isolated.AppendChild(copy);
        return Canonicalize(isolated);
    }

    /// <summary>Forma canónica del comprobante sin la firma (transformación enveloped).</summary>
    private static byte[] CanonicalWithoutSignature(XmlDocument document)
    {
        var clone = Load(document.OuterXml);
        var copy = clone.GetElementsByTagName("Signature", DsNamespace).Cast<XmlElement>().Single();
        copy.ParentNode!.RemoveChild(copy);
        return Canonicalize(clone);
    }

    private static byte[] Canonicalize(XmlDocument document)
    {
        var transform = new XmlDsigC14NTransform();
        transform.LoadInput(document);
        using var stream = (Stream)transform.GetOutput(typeof(Stream));
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static void Declare(XmlElement element, string? prefix, string uri)
    {
        var attribute = prefix is null
            ? element.OwnerDocument.CreateAttribute("xmlns", "http://www.w3.org/2000/xmlns/")
            : element.OwnerDocument.CreateAttribute("xmlns", prefix, "http://www.w3.org/2000/xmlns/");
        attribute.Value = uri;
        element.SetAttributeNode(attribute);
    }

    private static IEnumerable<(string? Prefix, string Uri)> InScopeNamespaces(XmlElement element)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (XmlNode? node = element; node is XmlElement e; node = node.ParentNode)
        {
            foreach (XmlAttribute attribute in e.Attributes)
            {
                if (attribute.Prefix == "xmlns" && seen.Add(attribute.LocalName))
                {
                    yield return (attribute.LocalName, attribute.Value);
                }
                else if (attribute is { Prefix.Length: 0, LocalName: "xmlns" } && seen.Add(string.Empty))
                {
                    yield return (null, attribute.Value);
                }
            }
        }
    }

#pragma warning disable CA5350 // SHA-1: lo exige el SRI para la firma de comprobantes (ficha técnica 6.8 y Anexo 14); no se usa para ningún otro fin.
    private static byte[] Digest(byte[] data) => SHA1.HashData(data);
#pragma warning restore CA5350

    private static string SerialAsDecimal(X509Certificate2 certificate) =>
        BigInteger.Parse("0" + certificate.SerialNumber, NumberStyles.HexNumber, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
}
