using System.Globalization;
using System.Text;
using System.Xml.Linq;
using FitCore.Invoicing.Domain;

namespace FitCore.Invoicing.Xml;

/// <summary>
/// Arma el XML de la factura según el esquema oficial del SRI <c>factura_V1.1.0.xsd</c> (versión 1.1.0: cantidad y precio unitario con hasta 6 decimales). Solo lee lo que el comprobante ya
/// tiene congelado: no recalcula nada. El orden de los elementos es el del XSD (es una secuencia); los importes llevan 2 decimales con punto. Se genera compacto (sin sangrías) para que la
/// firma no dependa de espacios en blanco. El XML resultante se valida contra el XSD antes de firmarlo (<see cref="InvoiceXmlValidator"/>).
/// </summary>
public static class InvoiceXmlBuilder
{
    public const string Version = "1.1.0";
    public const string RimpeLegend = "CONTRIBUYENTE RÉGIMEN RIMPE";
    public const string Currency = "DOLAR";

    /// <param name="providerRuc">RUC del proveedor del sistema de facturación, si se configuró: va en la información adicional como «RUC Proveedor» (Anexo 26 de la ficha técnica 2.34).</param>
    public static string Build(Invoice invoice, string? providerRuc = null)
    {
        var root = new XElement("factura", new XAttribute("id", "comprobante"), new XAttribute("version", Version));
        root.Add(InfoTributaria(invoice));
        root.Add(InfoFactura(invoice));
        root.Add(new XElement("detalles", invoice.Lines.OrderBy(l => l.LineNumber).Select(Detail)));
        if (AdditionalInfo(invoice, providerRuc) is { } additional)
        {
            root.Add(additional);
        }

        // Se arma a mano para garantizar «UTF-8» en mayúsculas y nada de sangrías ni saltos de línea.
        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" + root.ToString(SaveOptions.DisableFormatting);
    }

    private static XElement InfoTributaria(Invoice i)
    {
        var e = new XElement("infoTributaria",
            new XElement("ambiente", SriCatalogs.EnvironmentCode(i.Environment)),
            new XElement("tipoEmision", SriCatalogs.NormalEmissionCode),
            new XElement("razonSocial", Text(i.IssuerLegalName)));
        Optional(e, "nombreComercial", i.IssuerTradeName);
        e.Add(
            new XElement("ruc", i.IssuerRuc),
            new XElement("claveAcceso", i.AccessKey),
            new XElement("codDoc", SriCatalogs.InvoiceDocumentCode),
            new XElement("estab", i.EstablishmentCode),
            new XElement("ptoEmi", i.PointCode),
            new XElement("secuencial", i.Sequential.ToString("D9", CultureInfo.InvariantCulture)),
            new XElement("dirMatriz", Text(i.IssuerHeadOfficeAddress)));
        Optional(e, "agenteRetencion", i.WithholdingAgentNumber);
        if (i.TaxRegime == TaxRegime.RimpeEmprendedor)
        {
            e.Add(new XElement("contribuyenteRimpe", RimpeLegend));
        }

        return e;
    }

    private static XElement InfoFactura(Invoice i)
    {
        var e = new XElement("infoFactura",
            new XElement("fechaEmision", i.IssueDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)),
            new XElement("dirEstablecimiento", Text(i.IssuerEstablishmentAddress)));
        Optional(e, "contribuyenteEspecial", i.SpecialTaxpayerNumber);
        e.Add(
            new XElement("obligadoContabilidad", i.AccountingRequired ? "SI" : "NO"),
            new XElement("tipoIdentificacionComprador", i.BuyerIdType.Code()),
            new XElement("razonSocialComprador", Text(i.BuyerName)),
            new XElement("identificacionComprador", Text(i.BuyerIdNumber)));
        Optional(e, "direccionComprador", i.BuyerAddress);
        e.Add(
            new XElement("totalSinImpuestos", Money(i.SubtotalWithoutTaxes)),
            new XElement("totalDescuento", Money(i.TotalDiscount)),
            new XElement("totalConImpuestos", i.Taxes.OrderBy(t => t.RateCode, StringComparer.Ordinal).Select(t => new XElement("totalImpuesto",
                new XElement("codigo", t.TaxCode),
                new XElement("codigoPorcentaje", t.RateCode),
                new XElement("baseImponible", Money(t.TaxableBase)),
                new XElement("tarifa", Money(t.Percent)),
                new XElement("valor", Money(t.Amount))))),
            new XElement("propina", Money(0m)),
            new XElement("importeTotal", Money(i.Total)),
            new XElement("moneda", Currency),
            new XElement("pagos", i.Payments.Select(p => new XElement("pago", new XElement("formaPago", p.MethodCode), new XElement("total", Money(p.Amount))))));
        return e;
    }

    private static XElement Detail(InvoiceLine l) => new("detalle",
        new XElement("codigoPrincipal", Text(l.MainCode)),
        new XElement("descripcion", Text(l.Description)),
        new XElement("cantidad", Decimal(l.Quantity)),
        new XElement("precioUnitario", Decimal(l.UnitPrice)),
        new XElement("descuento", Money(l.Discount)),
        new XElement("precioTotalSinImpuesto", Money(l.Subtotal)),
        new XElement("impuestos", new XElement("impuesto",
            new XElement("codigo", l.TaxCode),
            new XElement("codigoPorcentaje", l.TaxRateCode),
            new XElement("tarifa", Money(l.TaxPercent)),
            new XElement("baseImponible", Money(l.Subtotal)),
            new XElement("valor", Money(l.TaxAmount)))));

    /// <summary>Datos de contacto y referencia (máximo 15 campos de hasta 300 caracteres según el XSD); solo los que existen.</summary>
    private static XElement? AdditionalInfo(Invoice i, string? providerRuc)
    {
        var fields = new List<XElement>();
        void Add(string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                fields.Add(new XElement("campoAdicional", new XAttribute("nombre", name), Text(value)));
            }
        }

        Add("Email", i.BuyerEmail);
        Add("Telefono", i.BuyerPhone);
        Add("Referencia", i.SourceReference);
        Add("RUC Proveedor", providerRuc);
        return fields.Count == 0 ? null : new XElement("infoAdicional", fields);
    }

    private static void Optional(XElement parent, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            parent.Add(new XElement(name, Text(value)));
        }
    }

    private static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>Cantidad y precio unitario admiten hasta 6 decimales; se escriben con al menos 2.</summary>
    private static string Decimal(decimal value) => value.ToString("0.00####", CultureInfo.InvariantCulture);

    /// <summary>
    /// Texto apto para el XML: sin caracteres de control (no válidos en XML 1.0), sin saltos de línea (los patrones del XSD son <c>[^\n]*</c>) y con los espacios recogidos.
    /// El escape de «&amp;», «&lt;» y «&gt;» lo hace el propio serializador, como exige la ficha técnica.
    /// </summary>
    internal static string Text(string value)
    {
        var builder = new StringBuilder(value.Length);
        var lastWasSpace = true;
        // Se recorre por caracteres Unicode completos: un sustituto suelto (no válido en XML) se vuelve U+FFFD y U+FFFE/U+FFFF se descartan.
        foreach (var rune in value.EnumerateRunes())
        {
            if (rune.Value is 0xFFFE or 0xFFFF)
            {
                continue;
            }

            var isSpace = Rune.IsControl(rune) || Rune.IsWhiteSpace(rune);
            if (isSpace)
            {
                if (lastWasSpace)
                {
                    continue;
                }

                builder.Append(' ');
                lastWasSpace = true;
                continue;
            }

            builder.Append(rune.ToString());
            lastWasSpace = false;
        }

        return builder.ToString().TrimEnd();
    }
}
