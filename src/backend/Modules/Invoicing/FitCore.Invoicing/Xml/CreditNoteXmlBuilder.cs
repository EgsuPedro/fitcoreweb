using System.Globalization;
using System.Xml.Linq;
using FitCore.Invoicing.Domain;

namespace FitCore.Invoicing.Xml;

/// <summary>
/// Arma el XML de la nota de crédito según el esquema oficial <c>notaCredito_V1.1.0.xsd</c> del SRI. Igual que la factura, solo lee lo que el comprobante ya tiene congelado y escribe compacto, en el orden
/// del XSD. La tarifa del IVA de cada línea es la de la factura que se modifica (lo manda la ficha técnica 2.34); el comprador debe estar identificado (no hay nota de crédito a consumidor final).
/// </summary>
public static class CreditNoteXmlBuilder
{
    public const string Version = "1.1.0";

    public static string Build(Invoice note, string? providerRuc = null)
    {
        if (note.DocumentType != InvoiceDocumentType.CreditNote || note.ModifiedDocumentNumber is null || note.ModifiedIssueDate is null || note.ModificationReason is null)
        {
            throw new InvalidOperationException("El comprobante no es una nota de crédito completa.");
        }

        var root = new XElement("notaCredito", new XAttribute("id", "comprobante"), new XAttribute("version", Version));
        root.Add(InvoiceXmlBuilder.InfoTributaria(note));
        root.Add(InfoNotaCredito(note));
        root.Add(new XElement("detalles", note.Lines.OrderBy(l => l.LineNumber).Select(Detail)));
        if (AdditionalInfo(note, providerRuc) is { } additional)
        {
            root.Add(additional);
        }

        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" + root.ToString(SaveOptions.DisableFormatting);
    }

    private static XElement InfoNotaCredito(Invoice n)
    {
        var e = new XElement("infoNotaCredito",
            new XElement("fechaEmision", n.IssueDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)),
            new XElement("dirEstablecimiento", InvoiceXmlBuilder.Text(n.IssuerEstablishmentAddress)),
            new XElement("tipoIdentificacionComprador", n.BuyerIdType.Code()),
            new XElement("razonSocialComprador", InvoiceXmlBuilder.Text(n.BuyerName)),
            new XElement("identificacionComprador", InvoiceXmlBuilder.Text(n.BuyerIdNumber)));
        InvoiceXmlBuilder.Optional(e, "contribuyenteEspecial", n.SpecialTaxpayerNumber);
        e.Add(
            new XElement("obligadoContabilidad", n.AccountingRequired ? "SI" : "NO"),
            new XElement("codDocModificado", SriCatalogs.InvoiceDocumentCode),
            new XElement("numDocModificado", n.ModifiedDocumentNumber),
            new XElement("fechaEmisionDocSustento", n.ModifiedIssueDate!.Value.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)),
            new XElement("totalSinImpuestos", InvoiceXmlBuilder.Money(n.SubtotalWithoutTaxes)),
            new XElement("valorModificacion", InvoiceXmlBuilder.Money(n.Total)),
            new XElement("moneda", InvoiceXmlBuilder.Currency),
            new XElement("totalConImpuestos", n.Taxes.OrderBy(t => t.RateCode, StringComparer.Ordinal).Select(t => new XElement("totalImpuesto",
                new XElement("codigo", t.TaxCode),
                new XElement("codigoPorcentaje", t.RateCode),
                new XElement("baseImponible", InvoiceXmlBuilder.Money(t.TaxableBase)),
                new XElement("valor", InvoiceXmlBuilder.Money(t.Amount))))),
            new XElement("motivo", InvoiceXmlBuilder.Text(n.ModificationReason!)));
        return e;
    }

    private static XElement Detail(InvoiceLine l) => new("detalle",
        new XElement("codigoInterno", InvoiceXmlBuilder.Text(l.MainCode)),
        new XElement("descripcion", InvoiceXmlBuilder.Text(l.Description)),
        new XElement("cantidad", InvoiceXmlBuilder.Decimal(l.Quantity)),
        new XElement("precioUnitario", InvoiceXmlBuilder.Decimal(l.UnitPrice)),
        new XElement("descuento", InvoiceXmlBuilder.Money(l.Discount)),
        new XElement("precioTotalSinImpuesto", InvoiceXmlBuilder.Money(l.Subtotal)),
        new XElement("impuestos", new XElement("impuesto",
            new XElement("codigo", l.TaxCode),
            new XElement("codigoPorcentaje", l.TaxRateCode),
            new XElement("tarifa", InvoiceXmlBuilder.Money(l.TaxPercent)),
            new XElement("baseImponible", InvoiceXmlBuilder.Money(l.Subtotal)),
            new XElement("valor", InvoiceXmlBuilder.Money(l.TaxAmount)))));

    private static XElement? AdditionalInfo(Invoice n, string? providerRuc)
    {
        var fields = new List<XElement>();
        void Add(string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                fields.Add(new XElement("campoAdicional", new XAttribute("nombre", name), InvoiceXmlBuilder.Text(value)));
            }
        }

        Add("Email", n.BuyerEmail);
        Add("Telefono", n.BuyerPhone);
        Add("Referencia", n.SourceReference);
        Add("RUC Proveedor", providerRuc);
        return fields.Count == 0 ? null : new XElement("infoAdicional", fields);
    }
}
