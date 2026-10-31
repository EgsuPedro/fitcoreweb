using System.Text.RegularExpressions;
using System.Xml.Linq;
using FitCore.Invoicing.Domain;
using FitCore.Invoicing.Rendering;
using FitCore.Invoicing.Xml;

namespace FitCore.Invoicing.UnitTests;

internal static class SampleCreditNotes
{
    /// <summary>Una nota de crédito armada igual que el caso de uso: sobre una factura de muestra, con el cálculo real del dominio.</summary>
    public static (Invoice Original, Invoice Note) Create(
        decimal amount = 30m, IReadOnlyList<CreditRequestLine>? lines = null, string reason = "Devolución de la membresía", decimal percent = 15m, string taxKind = "General",
        SriBuyerIdType buyerType = SriBuyerIdType.Cedula)
    {
        var original = SampleInvoices.Create(amount, amount, taxKind, percent, buyerType: buyerType, buyerId: buyerType == SriBuyerIdType.Cedula ? "1712345675" : "9999999999999");
        var creditable = CreditNoteCalculator.Creditable(original, new Dictionary<Guid, CreditedAmount>());
        var c = CreditNoteCalculator.Calculate(creditable, lines).Value;
        var id = Guid.NewGuid();
        var note = new Invoice
        {
            Id = id, TenantId = original.TenantId, DocumentType = InvoiceDocumentType.CreditNote, SourceType = "invoice", SourceId = original.Id, SourceReference = original.Number,
            BranchId = original.BranchId, EmissionPointId = original.EmissionPointId, EstablishmentCode = "001", PointCode = "001", Sequential = 7, Environment = SriEnvironment.Test,
            IssueDate = new DateOnly(2026, 3, 20), NumericCode = "87654321",
            AccessKey = AccessKey.Build(new DateOnly(2026, 3, 20), SriCatalogs.CreditNoteDocumentCode, SampleInvoices.Ruc, SriEnvironment.Test, "001", "001", 7, "87654321"),
            IssuerRuc = original.IssuerRuc, IssuerLegalName = original.IssuerLegalName, IssuerTradeName = original.IssuerTradeName, IssuerHeadOfficeAddress = original.IssuerHeadOfficeAddress,
            IssuerEstablishmentAddress = original.IssuerEstablishmentAddress, TaxRegime = original.TaxRegime, AccountingRequired = original.AccountingRequired,
            SpecialTaxpayerNumber = original.SpecialTaxpayerNumber, WithholdingAgentNumber = original.WithholdingAgentNumber,
            BuyerIdType = original.BuyerIdType, BuyerIdNumber = original.BuyerIdNumber, BuyerName = original.BuyerName, BuyerAddress = original.BuyerAddress, BuyerEmail = original.BuyerEmail,
            BuyerPhone = original.BuyerPhone, ModifiedInvoiceId = original.Id, ModifiedDocumentNumber = original.Number, ModifiedIssueDate = original.IssueDate, ModificationReason = reason,
            SubtotalWithoutTaxes = c.SubtotalWithoutTaxes, TotalTax = c.TotalTax, Total = c.Total, CreatedAt = DateTimeOffset.UtcNow
        };
        foreach (var l in c.Lines)
        {
            note.Lines.Add(new InvoiceLine
            {
                Id = Guid.NewGuid(), TenantId = note.TenantId, InvoiceId = id, LineNumber = l.LineNumber, MainCode = l.MainCode, Description = l.Description, Quantity = 1m, UnitPrice = l.Base,
                Subtotal = l.Base, TaxKind = l.TaxKind, TaxCode = l.TaxCode, TaxRateCode = l.RateCode, TaxPercent = l.TaxPercent, TaxAmount = l.TaxAmount, ModifiedLineId = l.ModifiedLineId
            });
        }

        foreach (var t in c.Taxes)
        {
            note.Taxes.Add(new InvoiceTax { Id = Guid.NewGuid(), TenantId = note.TenantId, InvoiceId = id, TaxCode = t.TaxCode, RateCode = t.RateCode, Percent = t.Percent, TaxableBase = t.TaxableBase, Amount = t.Amount });
        }

        return (original, note);
    }
}

public class CreditNoteCalculatorTests
{
    private static IReadOnlyList<CreditableLine> Creditable(Invoice invoice, params (Guid Line, decimal Base, decimal Tax)[] credited) =>
        CreditNoteCalculator.Creditable(invoice, credited.ToDictionary(c => c.Line, c => new CreditedAmount(c.Base, c.Tax)));

    [Fact]
    public void Crediting_everything_returns_the_exact_base_and_tax_of_the_invoice()
    {
        var invoice = SampleInvoices.Create(30m);

        var result = CreditNoteCalculator.Calculate(Creditable(invoice), null).Value;

        Assert.Equal(invoice.SubtotalWithoutTaxes, result.SubtotalWithoutTaxes);
        Assert.Equal(invoice.TotalTax, result.TotalTax);
        Assert.Equal(invoice.Total, result.Total);
        var line = Assert.Single(result.Lines);
        Assert.Equal(invoice.Lines[0].Id, line.ModifiedLineId);
    }

    [Theory]
    [InlineData("General", 15, "4")]
    [InlineData("General", 12, "2")]
    [InlineData("Zero", 0, "0")]
    [InlineData("Exempt", 0, "7")]
    public void Each_line_keeps_the_tax_rate_of_the_invoice_it_modifies(string kind, int percent, string code)
    {
        var invoice = SampleInvoices.Create(40m, 40m, kind, percent);

        var result = CreditNoteCalculator.Calculate(Creditable(invoice), null).Value;

        Assert.Equal(code, result.Lines[0].RateCode);
        Assert.Equal(percent, result.Lines[0].TaxPercent);
        Assert.Equal(code, result.Taxes.Single().RateCode);
    }

    [Fact]
    public void A_partial_credit_splits_the_amount_like_the_invoice_does()
    {
        var invoice = SampleInvoices.Create(30m);
        var line = invoice.Lines[0];

        var result = CreditNoteCalculator.Calculate(Creditable(invoice), [new CreditRequestLine(line.Id, 11.50m)]).Value;

        Assert.Equal(11.50m, result.Total);
        Assert.Equal(10m, result.SubtotalWithoutTaxes);
        Assert.Equal(1.50m, result.TotalTax);
    }

    [Fact]
    public void Credits_add_up_to_the_invoice_and_never_beyond()
    {
        var invoice = SampleInvoices.Create(30m);
        var line = invoice.Lines[0];

        var first = CreditNoteCalculator.Calculate(Creditable(invoice), [new CreditRequestLine(line.Id, 11.50m)]).Value;
        var second = CreditNoteCalculator.Calculate(Creditable(invoice, (line.Id, first.SubtotalWithoutTaxes, first.TotalTax)), null).Value;

        Assert.Equal(invoice.SubtotalWithoutTaxes, first.SubtotalWithoutTaxes + second.SubtotalWithoutTaxes);
        Assert.Equal(invoice.Total, first.Total + second.Total);

        // Ya no queda nada.
        var third = CreditNoteCalculator.Calculate(Creditable(invoice, (line.Id, invoice.Lines[0].Subtotal, invoice.Lines[0].TaxAmount)), null);
        Assert.True(third.IsFailure);
        Assert.Equal("invoicing.invoice_fully_credited", third.Error.Code);
    }

    [Theory]
    [InlineData(0, "invoicing.credit_amount_invalid")]
    [InlineData(-5, "invoicing.credit_amount_invalid")]
    [InlineData(10.123, "invoicing.credit_amount_invalid")]
    [InlineData(30.01, "invoicing.credit_amount_exceeds")]
    public void Invalid_amounts_are_refused_with_a_clear_code(double amount, string code)
    {
        var invoice = SampleInvoices.Create(30m);

        var result = CreditNoteCalculator.Calculate(Creditable(invoice), [new CreditRequestLine(invoice.Lines[0].Id, (decimal)amount)]);

        Assert.True(result.IsFailure);
        Assert.Equal(code, result.Error.Code);
    }

    [Fact]
    public void A_line_that_is_not_in_the_invoice_or_is_repeated_is_refused()
    {
        var invoice = SampleInvoices.Create(30m);
        var id = invoice.Lines[0].Id;

        Assert.Equal("invoicing.credit_line_not_found", CreditNoteCalculator.Calculate(Creditable(invoice), [new CreditRequestLine(Guid.NewGuid(), 5m)]).Error.Code);
        Assert.Equal("invoicing.credit_line_repeated", CreditNoteCalculator.Calculate(Creditable(invoice), [new CreditRequestLine(id, 5m), new CreditRequestLine(id, 5m)]).Error.Code);
    }

    [Fact]
    public void A_line_already_credited_in_full_cannot_be_asked_again_even_if_others_remain()
    {
        var invoice = SampleInvoices.Create(30m);
        var original = invoice.Lines[0];
        var second = new InvoiceLine
        {
            Id = Guid.NewGuid(), TenantId = invoice.TenantId, InvoiceId = invoice.Id, LineNumber = 2, MainCode = "X", Description = "Otra", Quantity = 1, UnitPrice = 10m, Subtotal = 10m,
            TaxKind = "General", TaxCode = "2", TaxRateCode = "4", TaxPercent = 15m, TaxAmount = 1.5m
        };
        invoice.Lines.Add(second);
        var creditable = Creditable(invoice, (original.Id, original.Subtotal, original.TaxAmount));

        Assert.Equal("invoicing.credit_line_exhausted", CreditNoteCalculator.Calculate(creditable, [new CreditRequestLine(original.Id, null)]).Error.Code);
        var rest = CreditNoteCalculator.Calculate(creditable, null).Value;
        Assert.Equal(11.5m, rest.Total);
        Assert.Equal(second.Id, Assert.Single(rest.Lines).ModifiedLineId);
    }
}

public class CreditNoteXmlTests
{
    [Theory]
    [InlineData("factura_V1.1.0.xsd", "62db9bf0ecceb00ef2b7ed136e59224815e5e5e33c77efc6a0552001e052eb8b")]
    [InlineData("notaCredito_V1.1.0.xsd", "379e9f270d71ab4578f130a736c9abe5eb1d7969375ecdef55d2973583ec5ab0")]
    public void The_embedded_official_schemas_are_exact_copies_of_the_ones_published_by_the_SRI(string name, string sha256)
    {
        var assembly = typeof(InvoiceXmlValidator).Assembly;
        using var stream = assembly.GetManifestResourceStream(assembly.GetManifestResourceNames().Single(n => n.EndsWith(name, StringComparison.Ordinal)))!;
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();

        Assert.Equal(sha256, hash);
    }

    [Fact]
    public void The_credit_note_xml_complies_with_the_official_schema_and_carries_the_modified_invoice()
    {
        var (original, note) = SampleCreditNotes.Create();

        var xml = CreditNoteXmlBuilder.Build(note);

        Assert.Empty(InvoiceXmlValidator.Validate(xml, InvoiceDocumentType.CreditNote));
        var doc = XDocument.Parse(xml);
        Assert.Equal("notaCredito", doc.Root!.Name.LocalName);
        Assert.Equal("04", doc.Root.Element("infoTributaria")!.Element("codDoc")!.Value);
        var info = doc.Root.Element("infoNotaCredito")!;
        Assert.Equal("01", info.Element("codDocModificado")!.Value);
        Assert.Equal(original.Number, info.Element("numDocModificado")!.Value);
        Assert.Equal("09/03/2026", info.Element("fechaEmisionDocSustento")!.Value);
        Assert.Equal("20/03/2026", info.Element("fechaEmision")!.Value);
        Assert.Equal("30.00", info.Element("valorModificacion")!.Value);
        Assert.Equal("26.09", info.Element("totalSinImpuestos")!.Value);
        Assert.Equal("Devolución de la membresía", info.Element("motivo")!.Value);
        Assert.Equal("DOLAR", info.Element("moneda")!.Value);
        var tax = info.Element("totalConImpuestos")!.Element("totalImpuesto")!;
        Assert.Equal(["2", "4", "26.09", "3.91"], [tax.Element("codigo")!.Value, tax.Element("codigoPorcentaje")!.Value, tax.Element("baseImponible")!.Value, tax.Element("valor")!.Value]);
        Assert.Equal(note.AccessKey, doc.Root.Element("infoTributaria")!.Element("claveAcceso")!.Value);
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-8\"?><notaCredito id=\"comprobante\" version=\"1.1.0\">", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void A_partial_credit_note_with_a_zero_rate_and_special_characters_also_validates()
    {
        var (_, note) = SampleCreditNotes.Create(40m, reason: "Cobro duplicado & ajuste <manual>", percent: 0m, taxKind: "Zero");

        var xml = CreditNoteXmlBuilder.Build(note, "1790012345001");

        Assert.Empty(InvoiceXmlValidator.Validate(xml, InvoiceDocumentType.CreditNote));
        Assert.Contains("&amp; ajuste &lt;manual&gt;", xml, StringComparison.Ordinal);
        Assert.Contains("nombre=\"RUC Proveedor\"", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void The_credit_note_is_validated_against_its_own_schema_not_the_invoice_one()
    {
        var (_, note) = SampleCreditNotes.Create();
        var xml = CreditNoteXmlBuilder.Build(note);

        Assert.NotEmpty(InvoiceXmlValidator.Validate(xml, InvoiceDocumentType.Invoice));
        Assert.NotEmpty(InvoiceXmlValidator.Validate(InvoiceXmlBuilder.Build(SampleInvoices.Create()), InvoiceDocumentType.CreditNote));
    }

    [Fact]
    public void A_credit_note_xml_that_breaks_the_schema_is_reported()
    {
        var (_, note) = SampleCreditNotes.Create();
        var broken = CreditNoteXmlBuilder.Build(note).Replace("<codDocModificado>01</codDocModificado>", "<codDocModificado>1</codDocModificado>", StringComparison.Ordinal);

        Assert.NotEmpty(InvoiceXmlValidator.Validate(broken, InvoiceDocumentType.CreditNote));
    }

    [Fact]
    public void It_refuses_to_build_the_xml_of_something_that_is_not_a_credit_note()
    {
        Assert.Throws<InvalidOperationException>(() => CreditNoteXmlBuilder.Build(SampleInvoices.Create()));
    }

    [Fact]
    public void The_credit_note_is_signed_and_the_signature_verifies_with_the_same_signer_as_the_invoice()
    {
        var (_, note) = SampleCreditNotes.Create();
        using var certificate = SampleInvoices.Certificate();
        var xml = CreditNoteXmlBuilder.Build(note);

        var signed = XadesBesSigner.Sign(xml, certificate, DateTimeOffset.UtcNow);

        Assert.Empty(InvoiceXmlValidator.Validate(signed, InvoiceDocumentType.CreditNote));
        var thumbprint = certificate.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256).ToLowerInvariant();
        Assert.True(XadesBesSigner.Verify(signed, thumbprint).IsValid);
    }

    [Fact]
    public void The_access_key_of_a_credit_note_carries_document_code_04()
    {
        var (_, note) = SampleCreditNotes.Create();

        Assert.True(AccessKey.IsValid(note.AccessKey));
        Assert.Equal("04", note.AccessKey.Substring(8, 2));
        Assert.Matches(new Regex("^[0-9]{49}$"), note.AccessKey);
    }
}

public class CreditNoteRideTests
{
    private static byte[] Render(Invoice note)
    {
        note.AuthorizationNumber = note.AccessKey;
        note.AuthorizedAt = RideSamples.AuthorizedAt;
        note.Status = InvoiceStatus.Authorized;
        return RideRenderer.Render(RideModel.From(note, RideSamples.AuthorizedAt, null));
    }

    [Fact]
    public void The_ride_of_a_credit_note_names_the_modified_invoice_and_the_reason_and_has_no_payment_table()
    {
        var (original, note) = SampleCreditNotes.Create();

        var text = RideSamples.TextOf(Render(note));

        foreach (var expected in new[]
        {
            "NOTA DE CRÉDITO", $"No. {note.Number}", "Comprobante que se modifica:", $"FACTURA {original.Number}", "Fecha comprobante modificado:", "09/03/2026", "Razón de modificación:",
            "Devolución de la membresía", "SUBTOTAL 15%", "IVA 15%", "VALOR TOTAL", "30,00", "Ana Pérez"
        })
        {
            Assert.Contains(expected, text, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("Forma de pago", text, StringComparison.Ordinal);
        Assert.DoesNotContain("FACTURA\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_pdf_is_titled_as_a_credit_note()
    {
        var (_, note) = SampleCreditNotes.Create();

        using var document = UglyToad.PdfPig.PdfDocument.Open(Render(note));

        Assert.Equal($"Nota de crédito {note.Number}", document.Information.Title);
        Assert.Equal(1, document.NumberOfPages);
    }
}
