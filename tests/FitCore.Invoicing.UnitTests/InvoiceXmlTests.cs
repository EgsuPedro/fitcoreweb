using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;
using FitCore.Application.Core;
using FitCore.Invoicing.Domain;
using FitCore.Invoicing.Xml;

namespace FitCore.Invoicing.UnitTests;

/// <summary>Un comprobante de ejemplo armado con el mismo cálculo que usa la emisión real.</summary>
internal static class SampleInvoices
{
    public const string Ruc = "1790000028001";

    public static Invoice Create(
        decimal payable = 30m, decimal gross = 30m, string taxKind = "General", decimal percent = 15m, TaxRegime regime = TaxRegime.General,
        SriBuyerIdType buyerType = SriBuyerIdType.Cedula, string buyerId = "1712345675", string buyerName = "Ana Pérez", string? address = "Av. Principal 123", string? email = "ana@correo.test",
        string? phone = "0991234567", string? withholding = null, string? special = null, string? tradeName = "Fitness Quito", string method = "Card", int sequential = 1)
    {
        var source = new InvoiceSource(
            InvoiceSourceTypes.Obligation, Guid.NewGuid(), "OB-000001", Guid.NewGuid(), new DateOnly(2026, 3, 9), "USD", InvoiceSourceStatus.Paid,
            new InvoiceSourceBuyer(null, "Ana", "Pérez", SourceIdKind.Cedula, buyerId, email, phone, address),
            [new InvoiceSourceLine("Membresía mensual", 1m, gross, gross - payable, payable, 0m, 0m, taxKind, percent)],
            [new InvoiceSourcePayment(method, payable, DateTimeOffset.UtcNow, null)]);
        var calculation = InvoiceCalculator.Calculate(source, CardPaymentMethod.Credit).Value;
        var id = Guid.NewGuid();
        var numeric = "12345678";
        var invoice = new Invoice
        {
            Id = id, TenantId = Guid.NewGuid(), SourceType = "obligation", SourceId = source.SourceId, SourceReference = "OB-000001", BranchId = source.BranchId, EmissionPointId = Guid.NewGuid(),
            EstablishmentCode = "001", PointCode = "001", Sequential = sequential, Environment = SriEnvironment.Test, IssueDate = new DateOnly(2026, 3, 9), NumericCode = numeric,
            AccessKey = AccessKey.Build(new DateOnly(2026, 3, 9), "01", Ruc, SriEnvironment.Test, "001", "001", sequential, numeric),
            IssuerRuc = Ruc, IssuerLegalName = "Fitness Quito S.A.", IssuerTradeName = tradeName, IssuerHeadOfficeAddress = "Av. Amazonas N24-03 y Colón, Quito",
            IssuerEstablishmentAddress = "Av. Amazonas N24-03 y Colón, Quito", TaxRegime = regime, AccountingRequired = true, SpecialTaxpayerNumber = special, WithholdingAgentNumber = withholding,
            BuyerIdType = buyerType, BuyerIdNumber = buyerId, BuyerName = buyerName, BuyerAddress = address, BuyerEmail = email, BuyerPhone = phone,
            SubtotalWithoutTaxes = calculation.SubtotalWithoutTaxes, TotalDiscount = calculation.TotalDiscount, TotalTax = calculation.TotalTax, Total = calculation.Total,
            RoundingDifference = calculation.RoundingDifference, CreatedAt = DateTimeOffset.UtcNow
        };
        foreach (var l in calculation.Lines)
        {
            invoice.Lines.Add(new InvoiceLine
            {
                Id = Guid.NewGuid(), TenantId = invoice.TenantId, InvoiceId = id, LineNumber = l.LineNumber, MainCode = l.MainCode, Description = l.Description, Quantity = l.Quantity,
                UnitPrice = l.UnitPrice, Discount = l.Discount, Subtotal = l.Subtotal, TaxKind = l.TaxKind, TaxCode = SriCatalogs.IvaTaxCode, TaxRateCode = l.RateCode, TaxPercent = l.TaxPercent, TaxAmount = l.TaxAmount
            });
        }

        foreach (var t in calculation.Taxes)
        {
            invoice.Taxes.Add(new InvoiceTax { Id = Guid.NewGuid(), TenantId = invoice.TenantId, InvoiceId = id, TaxCode = t.TaxCode, RateCode = t.RateCode, Percent = t.Percent, TaxableBase = t.TaxableBase, Amount = t.Amount });
        }

        foreach (var p in calculation.Payments)
        {
            invoice.Payments.Add(new InvoicePayment { Id = Guid.NewGuid(), TenantId = invoice.TenantId, InvoiceId = id, MethodCode = p.MethodCode, Method = p.Method, Amount = p.Amount });
        }

        return invoice;
    }

    /// <summary>Certificado autofirmado de prueba (RSA 2048, firma digital). Jamás se usa uno real.</summary>
    public static X509Certificate2 Certificate(string subject = "CN=Gimnasio Prueba, SERIALNUMBER=" + Ruc)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, critical: true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(365));
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx, "x"), "x");
    }
}

public class InvoiceXmlBuilderTests
{
    private static readonly XNamespace None = XNamespace.None;

    private static XElement Root(string xml) => XDocument.Parse(xml).Root!;

    [Fact]
    public void A_standard_invoice_matches_the_official_schema()
    {
        var xml = InvoiceXmlBuilder.Build(SampleInvoices.Create());

        var problems = InvoiceXmlValidator.Validate(xml);

        Assert.Empty(problems);
    }

    [Fact]
    public void The_document_starts_with_the_utf8_declaration_and_root_attributes_the_SRI_expects()
    {
        var xml = InvoiceXmlBuilder.Build(SampleInvoices.Create());

        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-8\"?><factura id=\"comprobante\" version=\"1.1.0\">", xml, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', xml);
        Assert.DoesNotContain("xmlns", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void Tax_information_comes_from_the_frozen_issuer_and_numbering()
    {
        var invoice = SampleInvoices.Create();
        var root = Root(InvoiceXmlBuilder.Build(invoice));
        var info = root.Element("infoTributaria")!;

        Assert.Equal("1", info.Element("ambiente")!.Value);
        Assert.Equal("1", info.Element("tipoEmision")!.Value);
        Assert.Equal("Fitness Quito S.A.", info.Element("razonSocial")!.Value);
        Assert.Equal("Fitness Quito", info.Element("nombreComercial")!.Value);
        Assert.Equal(SampleInvoices.Ruc, info.Element("ruc")!.Value);
        Assert.Equal(invoice.AccessKey, info.Element("claveAcceso")!.Value);
        Assert.Equal("01", info.Element("codDoc")!.Value);
        Assert.Equal("001", info.Element("estab")!.Value);
        Assert.Equal("001", info.Element("ptoEmi")!.Value);
        Assert.Equal("000000001", info.Element("secuencial")!.Value);
        Assert.Null(info.Element("agenteRetencion"));
        Assert.Null(info.Element("contribuyenteRimpe"));
    }

    [Fact]
    public void Amounts_use_two_decimals_with_a_dot_and_the_tax_codes_of_the_SRI()
    {
        var root = Root(InvoiceXmlBuilder.Build(SampleInvoices.Create()));
        var info = root.Element("infoFactura")!;

        Assert.Equal("09/03/2026", info.Element("fechaEmision")!.Value);
        Assert.Equal("26.09", info.Element("totalSinImpuestos")!.Value);
        Assert.Equal("0.00", info.Element("totalDescuento")!.Value);
        Assert.Equal("30.00", info.Element("importeTotal")!.Value);
        Assert.Equal("DOLAR", info.Element("moneda")!.Value);
        var total = info.Element("totalConImpuestos")!.Element("totalImpuesto")!;
        Assert.Equal(("2", "4", "26.09", "15.00", "3.91"), (total.Element("codigo")!.Value, total.Element("codigoPorcentaje")!.Value, total.Element("baseImponible")!.Value, total.Element("tarifa")!.Value, total.Element("valor")!.Value));
        var payment = info.Element("pagos")!.Element("pago")!;
        Assert.Equal("19", payment.Element("formaPago")!.Value);
        Assert.Equal("30.00", payment.Element("total")!.Value);

        var detail = root.Element("detalles")!.Element("detalle")!;
        Assert.Equal("OB-000001", detail.Element("codigoPrincipal")!.Value);
        Assert.Equal("1.00", detail.Element("cantidad")!.Value);
        Assert.Equal("26.09", detail.Element("precioUnitario")!.Value);
        Assert.Equal("0.00", detail.Element("descuento")!.Value);
        Assert.Equal("26.09", detail.Element("precioTotalSinImpuesto")!.Value);
    }

    [Fact]
    public void The_buyer_identification_and_optional_data_are_included_only_when_they_exist()
    {
        var withData = Root(InvoiceXmlBuilder.Build(SampleInvoices.Create())).Element("infoFactura")!;
        Assert.Equal("05", withData.Element("tipoIdentificacionComprador")!.Value);
        Assert.Equal("Ana Pérez", withData.Element("razonSocialComprador")!.Value);
        Assert.Equal("1712345675", withData.Element("identificacionComprador")!.Value);
        Assert.Equal("Av. Principal 123", withData.Element("direccionComprador")!.Value);

        var root = Root(InvoiceXmlBuilder.Build(SampleInvoices.Create(address: null, email: null, phone: null)));
        Assert.Null(root.Element("infoFactura")!.Element("direccionComprador"));
        Assert.Equal("Referencia", Assert.Single(root.Element("infoAdicional")!.Elements()).Attribute("nombre")!.Value);
        Assert.Empty(InvoiceXmlValidator.Validate(InvoiceXmlBuilder.Build(SampleInvoices.Create(address: null, email: null, phone: null))));
    }

    [Fact]
    public void Additional_information_carries_email_phone_and_reference()
    {
        var additional = Root(InvoiceXmlBuilder.Build(SampleInvoices.Create())).Element("infoAdicional")!.Elements().ToList();

        Assert.Equal(["Email", "Telefono", "Referencia"], additional.Select(e => e.Attribute("nombre")!.Value));
        Assert.Equal(["ana@correo.test", "0991234567", "OB-000001"], additional.Select(e => e.Value));
    }

    [Fact]
    public void The_provider_ruc_goes_in_the_additional_information_when_configured_and_the_schema_still_accepts_it()
    {
        var xml = InvoiceXmlBuilder.Build(SampleInvoices.Create(), "1790012345001");

        var additional = Root(xml).Element("infoAdicional")!.Elements().ToList();
        Assert.Equal(["Email", "Telefono", "Referencia", "RUC Proveedor"], additional.Select(e => e.Attribute("nombre")!.Value));
        Assert.Equal("1790012345001", additional.Last().Value);
        Assert.Empty(InvoiceXmlValidator.Validate(xml));
    }

    [Fact]
    public void Without_a_configured_provider_ruc_the_field_is_not_added()
    {
        Assert.DoesNotContain("RUC Proveedor", InvoiceXmlBuilder.Build(SampleInvoices.Create()), StringComparison.Ordinal);
        Assert.DoesNotContain("RUC Proveedor", InvoiceXmlBuilder.Build(SampleInvoices.Create(), "  "), StringComparison.Ordinal);
    }

    [Fact]
    public void Final_consumer_uses_type_07_and_the_fixed_data_and_still_matches_the_schema()
    {
        var xml = InvoiceXmlBuilder.Build(SampleInvoices.Create(buyerType: SriBuyerIdType.FinalConsumer, buyerId: "9999999999999", buyerName: "CONSUMIDOR FINAL", address: null, email: null, phone: null));

        var info = Root(xml).Element("infoFactura")!;
        Assert.Equal("07", info.Element("tipoIdentificacionComprador")!.Value);
        Assert.Equal("9999999999999", info.Element("identificacionComprador")!.Value);
        Assert.Equal("CONSUMIDOR FINAL", info.Element("razonSocialComprador")!.Value);
        Assert.Empty(InvoiceXmlValidator.Validate(xml));
    }

    [Theory]
    [InlineData(SriBuyerIdType.Ruc, "1790000028001", "04")]
    [InlineData(SriBuyerIdType.Passport, "X1234567", "06")]
    [InlineData(SriBuyerIdType.Foreign, "DOC-12345-AB", "08")]
    public void Every_buyer_type_produces_a_valid_document(SriBuyerIdType type, string id, string code)
    {
        var xml = InvoiceXmlBuilder.Build(SampleInvoices.Create(buyerType: type, buyerId: id));

        Assert.Equal(code, Root(xml).Element("infoFactura")!.Element("tipoIdentificacionComprador")!.Value);
        Assert.Empty(InvoiceXmlValidator.Validate(xml));
    }

    [Theory]
    [InlineData("Zero", 0, 40.0)]
    [InlineData("NotSubject", 0, 40.0)]
    [InlineData("Exempt", 0, 40.0)]
    [InlineData("General", 12, 40.0)]
    [InlineData("General", 14, 40.0)]
    public void Every_tax_rate_produces_a_valid_document(string kind, double percent, double amount)
    {
        var xml = InvoiceXmlBuilder.Build(SampleInvoices.Create((decimal)amount, (decimal)amount, kind, (decimal)percent));

        Assert.Empty(InvoiceXmlValidator.Validate(xml));
    }

    [Fact]
    public void RIMPE_entrepreneurs_carry_the_legend_while_the_general_regime_does_not()
    {
        var rimpe = Root(InvoiceXmlBuilder.Build(SampleInvoices.Create(regime: TaxRegime.RimpeEmprendedor))).Element("infoTributaria")!;
        Assert.Equal("CONTRIBUYENTE RÉGIMEN RIMPE", rimpe.Element("contribuyenteRimpe")!.Value);
        Assert.Empty(InvoiceXmlValidator.Validate(InvoiceXmlBuilder.Build(SampleInvoices.Create(regime: TaxRegime.RimpeEmprendedor))));
    }

    [Fact]
    public void Withholding_agent_and_special_taxpayer_numbers_are_included_in_their_places()
    {
        var xml = InvoiceXmlBuilder.Build(SampleInvoices.Create(withholding: "12345678", special: "5368"));

        var root = Root(xml);
        Assert.Equal("12345678", root.Element("infoTributaria")!.Element("agenteRetencion")!.Value);
        Assert.Equal("5368", root.Element("infoFactura")!.Element("contribuyenteEspecial")!.Value);
        Assert.Empty(InvoiceXmlValidator.Validate(xml));
    }

    [Fact]
    public void Special_characters_are_escaped_and_line_breaks_and_control_characters_removed()
    {
        var xml = InvoiceXmlBuilder.Build(SampleInvoices.Create(buyerName: "Pérez & Hijos <Cía>\r\n  Ltda.\t\u0001", address: "Calle \"A\" y 'B'\n#12"));

        var info = Root(xml).Element("infoFactura")!;
        Assert.Equal("Pérez & Hijos <Cía> Ltda.", info.Element("razonSocialComprador")!.Value);
        Assert.Equal("Calle \"A\" y 'B' #12", info.Element("direccionComprador")!.Value);
        Assert.Contains("Pérez &amp; Hijos &lt;Cía&gt; Ltda.", xml, StringComparison.Ordinal);
        Assert.Empty(InvoiceXmlValidator.Validate(xml));
    }

    [Fact]
    public void Unpaired_surrogates_and_noncharacters_never_break_the_document()
    {
        var xml = InvoiceXmlBuilder.Build(SampleInvoices.Create(buyerName: "Ana\uD800 Pé￿rez 😀"));

        Assert.Empty(InvoiceXmlValidator.Validate(xml));
        Assert.Contains("😀", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void The_schema_validator_reports_what_is_wrong_with_line_and_position()
    {
        var xml = InvoiceXmlBuilder.Build(SampleInvoices.Create());
        var broken = xml.Replace("<codDoc>01</codDoc>", "<codDoc>1</codDoc>", StringComparison.Ordinal).Replace("<ruc>1790000028001</ruc>", "<ruc>1790000028002</ruc>", StringComparison.Ordinal);

        var problems = InvoiceXmlValidator.Validate(broken);

        Assert.True(problems.Count >= 2);
        Assert.All(problems, p => Assert.Contains("línea", p, StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("codDoc", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("ruc", StringComparison.Ordinal));
    }

    [Fact]
    public void An_issuer_ruc_that_does_not_end_in_001_is_rejected_by_the_schema()
    {
        var invoice = SampleInvoices.Create();
        var xml = InvoiceXmlBuilder.Build(invoice).Replace($"<ruc>{SampleInvoices.Ruc}</ruc>", "<ruc>1790000028002</ruc>", StringComparison.Ordinal);

        Assert.NotEmpty(InvoiceXmlValidator.Validate(xml));
    }
}

public class XadesBesSignerTests
{
    private static string Signed(Invoice? invoice = null, X509Certificate2? certificate = null)
    {
        using var own = certificate is null ? SampleInvoices.Certificate() : null;
        return XadesBesSigner.Sign(InvoiceXmlBuilder.Build(invoice ?? SampleInvoices.Create()), certificate ?? own!, new DateTimeOffset(2026, 3, 9, 10, 30, 15, TimeSpan.FromHours(-5)));
    }

    [Fact]
    public void A_signed_invoice_still_matches_the_official_schema_including_the_signature_element()
    {
        var signed = Signed();

        Assert.Empty(InvoiceXmlValidator.Validate(signed));
    }

    [Fact]
    public void The_signature_verifies_with_the_independent_dotnet_verifier()
    {
        using var certificate = SampleInvoices.Certificate();
        var signed = Signed(certificate: certificate);

        var check = XadesBesSigner.Verify(signed, certificate.GetCertHashString(HashAlgorithmName.SHA256));

        Assert.True(check.IsValid, string.Join("; ", check.Problems));
        Assert.Equal(new DateTimeOffset(2026, 3, 9, 10, 30, 15, TimeSpan.FromHours(-5)), check.SigningTime);
    }

    [Fact]
    public void The_signature_follows_the_profile_of_the_technical_sheet()
    {
        var signed = Signed();
        var doc = XDocument.Parse(signed);
        XNamespace ds = XadesBesSigner.DsNamespace;
        XNamespace etsi = XadesBesSigner.EtsiNamespace;
        var signature = doc.Root!.Element(ds + "Signature")!;

        Assert.Equal("http://www.w3.org/TR/2001/REC-xml-c14n-20010315", signature.Element(ds + "SignedInfo")!.Element(ds + "CanonicalizationMethod")!.Attribute("Algorithm")!.Value);
        Assert.Equal("http://www.w3.org/2000/09/xmldsig#rsa-sha1", signature.Element(ds + "SignedInfo")!.Element(ds + "SignatureMethod")!.Attribute("Algorithm")!.Value);
        var references = signature.Element(ds + "SignedInfo")!.Elements(ds + "Reference").ToList();
        Assert.Equal(3, references.Count);
        Assert.StartsWith("#", references[0].Attribute("URI")!.Value, StringComparison.Ordinal);
        Assert.Equal("http://uri.etsi.org/01903#SignedProperties", references[0].Attribute("Type")!.Value);
        Assert.Equal("#" + signature.Element(ds + "KeyInfo")!.Attribute("Id")!.Value, references[1].Attribute("URI")!.Value);
        Assert.Equal("#comprobante", references[2].Attribute("URI")!.Value);
        Assert.Equal("http://www.w3.org/2000/09/xmldsig#enveloped-signature", references[2].Element(ds + "Transforms")!.Element(ds + "Transform")!.Attribute("Algorithm")!.Value);
        Assert.All(references, r => Assert.Equal("http://www.w3.org/2000/09/xmldsig#sha1", r.Element(ds + "DigestMethod")!.Attribute("Algorithm")!.Value));
        Assert.NotNull(signature.Element(ds + "KeyInfo")!.Element(ds + "X509Data")!.Element(ds + "X509Certificate"));
        Assert.NotNull(signature.Element(ds + "KeyInfo")!.Element(ds + "KeyValue")!.Element(ds + "RSAKeyValue")!.Element(ds + "Modulus"));
        var qualifying = signature.Element(ds + "Object")!.Element(etsi + "QualifyingProperties")!;
        Assert.Equal("#" + signature.Attribute("Id")!.Value, qualifying.Attribute("Target")!.Value);
        var properties = qualifying.Element(etsi + "SignedProperties")!;
        Assert.NotNull(properties.Element(etsi + "SignedSignatureProperties")!.Element(etsi + "SigningTime"));
        Assert.Equal("text/xml", properties.Element(etsi + "SignedDataObjectProperties")!.Element(etsi + "DataObjectFormat")!.Element(etsi + "MimeType")!.Value);
        // La firma es el último hijo del comprobante, como en el ejemplo oficial.
        Assert.Equal(ds + "Signature", doc.Root.Elements().Last().Name);
        // Prefijos ds: y etsi: declarados en la propia firma, como en el ejemplo oficial (Anexo 14); el orden de los atributos no importa.
        var startTag = System.Text.RegularExpressions.Regex.Match(signed, "<ds:Signature [^>]*>").Value;
        Assert.Contains("xmlns:ds=\"http://www.w3.org/2000/09/xmldsig#\"", startTag, StringComparison.Ordinal);
        Assert.Contains("xmlns:etsi=\"http://uri.etsi.org/01903/v1.3.2#\"", startTag, StringComparison.Ordinal);
    }

    [Fact]
    public void Changing_any_signed_content_breaks_the_signature()
    {
        using var certificate = SampleInvoices.Certificate();
        var signed = Signed(certificate: certificate);

        foreach (var (from, to) in new[] { ("<importeTotal>30.00</importeTotal>", "<importeTotal>31.00</importeTotal>"), ("Ana Pérez", "Otra Persona"), ("<secuencial>000000001</secuencial>", "<secuencial>000000002</secuencial>") })
        {
            var tampered = signed.Replace(from, to, StringComparison.Ordinal);
            Assert.NotEqual(signed, tampered);
            Assert.False(XadesBesSigner.Verify(tampered).IsValid, from);
        }
    }

    [Fact]
    public void A_tampered_signature_value_or_signed_properties_are_detected()
    {
        var signed = Signed();

        Assert.False(XadesBesSigner.Verify(signed.Replace("<etsi:SigningTime>2026-03-09T10:30:15-05:00", "<etsi:SigningTime>2026-03-09T10:30:16-05:00", StringComparison.Ordinal)).IsValid);
        var doc = XDocument.Parse(signed);
        XNamespace ds = XadesBesSigner.DsNamespace;
        var value = doc.Root!.Element(ds + "Signature")!.Element(ds + "SignatureValue")!;
        value.Value = Convert.ToBase64String(new byte[256]);
        Assert.False(XadesBesSigner.Verify(doc.ToString(SaveOptions.DisableFormatting)).IsValid);
    }

    [Fact]
    public void The_signature_is_rejected_when_it_belongs_to_another_certificate()
    {
        using var certificate = SampleInvoices.Certificate();
        using var other = SampleInvoices.Certificate();
        var signed = Signed(certificate: certificate);

        var check = XadesBesSigner.Verify(signed, other.GetCertHashString(HashAlgorithmName.SHA256));

        Assert.False(check.IsValid);
        Assert.Contains(check.Problems, p => p.Contains("certificado esperado", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unsigned_or_malformed_document_is_not_valid()
    {
        Assert.False(XadesBesSigner.Verify(InvoiceXmlBuilder.Build(SampleInvoices.Create())).IsValid);
        Assert.False(XadesBesSigner.Verify("<factura").IsValid);
    }

    [Fact]
    public void Every_signature_uses_fresh_identifiers_and_a_deterministic_digest_of_the_content()
    {
        using var certificate = SampleInvoices.Certificate();
        var invoice = SampleInvoices.Create();
        var xml = InvoiceXmlBuilder.Build(invoice);
        var time = DateTimeOffset.UtcNow;

        var first = XadesBesSigner.Sign(xml, certificate, time);
        var second = XadesBesSigner.Sign(xml, certificate, time);

        XNamespace ds = XadesBesSigner.DsNamespace;
        string DocumentDigest(string signed) => XDocument.Parse(signed).Root!.Element(ds + "Signature")!.Element(ds + "SignedInfo")!
            .Elements(ds + "Reference").Single(r => r.Attribute("URI")!.Value == "#comprobante").Element(ds + "DigestValue")!.Value;
        Assert.NotEqual(first, second);
        Assert.Equal(DocumentDigest(first), DocumentDigest(second));
        Assert.True(XadesBesSigner.Verify(first).IsValid);
        Assert.True(XadesBesSigner.Verify(second).IsValid);
    }

    [Fact]
    public void Non_ascii_content_survives_signing_and_verification_after_a_utf8_roundtrip()
    {
        using var certificate = SampleInvoices.Certificate();
        var signed = XadesBesSigner.Sign(InvoiceXmlBuilder.Build(SampleInvoices.Create(buyerName: "María José Núñez Peña & Cía", address: "Av. 9 de Octubre y Whymper — Quito")), certificate, DateTimeOffset.UtcNow);

        // Lo que se guarda y se envía son bytes UTF-8: se vuelve a leer desde ellos antes de verificar.
        var roundtrip = System.Text.Encoding.UTF8.GetString(System.Text.Encoding.UTF8.GetBytes(signed));

        Assert.True(XadesBesSigner.Verify(roundtrip).IsValid);
        Assert.Empty(InvoiceXmlValidator.Validate(roundtrip));
    }
}
