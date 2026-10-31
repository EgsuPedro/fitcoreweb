using System.Globalization;
using FitCore.Invoicing.Domain;

namespace FitCore.Invoicing.Rendering;

public sealed record RideLine(string MainCode, string Description, decimal Quantity, decimal UnitPrice, decimal Discount, decimal Total);

/// <summary>Una fila de los totales («SUBTOTAL 15%», «IVA 15%», «VALOR TOTAL»…).</summary>
public sealed record RideTotal(string Label, decimal Amount, bool Emphasis = false);

public sealed record RideField(string Label, string Value);

/// <summary>Logo ya validado del gimnasio (PNG o JPEG).</summary>
public sealed record RideLogo(byte[] Content, int Width, int Height);

/// <summary>
/// Todo lo que dibuja el RIDE de una factura, ya resuelto y en texto: el dibujante no calcula nada ni decide reglas tributarias. Sigue el modelo oficial del Anexo 2 de la ficha técnica del SRI
/// (2.34): recuadro del emisor, recuadro con número, autorización, ambiente, emisión y clave de acceso, datos del comprador, detalle, información adicional, forma de pago y totales por tarifa.
/// Como manda la ficha, solo se muestran los subtotales que tienen valor.
/// </summary>
public sealed record RideModel(
    string IssuerLegalName,
    string? IssuerTradeName,
    string IssuerRuc,
    string HeadOfficeAddress,
    string EstablishmentAddress,
    string? SpecialTaxpayerNumber,
    string? WithholdingAgentNumber,
    bool AccountingRequired,
    string? RegimeLegend,
    string DocumentNumber,
    string AuthorizationNumber,
    string AuthorizedAtLocal,
    string EnvironmentName,
    string EmissionName,
    string AccessKey,
    string BuyerName,
    string BuyerIdNumber,
    string IssueDate,
    string? BuyerAddress,
    IReadOnlyList<RideLine> Lines,
    IReadOnlyList<RideField> AdditionalInfo,
    IReadOnlyList<RideField> Payments,
    IReadOnlyList<RideTotal> Totals,
    RideLogo? Logo,
    string DocumentTitle = "FACTURA",
    string DocumentName = "Factura",
    IReadOnlyList<RideField>? ModifiedDocument = null)
{
    /// <summary>Nombres de la Tabla 24 de la ficha 2.34.</summary>
    private static readonly Dictionary<string, string> PaymentNames = new(StringComparer.Ordinal)
    {
        ["01"] = "SIN UTILIZACION DEL SISTEMA FINANCIERO",
        ["15"] = "COMPENSACIÓN DE DEUDAS",
        ["16"] = "TARJETA DE DÉBITO",
        ["17"] = "DINERO ELECTRÓNICO",
        ["18"] = "TARJETA PREPAGO",
        ["19"] = "TARJETA DE CRÉDITO",
        ["20"] = "OTROS CON UTILIZACIÓN DEL SISTEMA FINANCIERO",
        ["21"] = "ENDOSO DE TÍTULOS"
    };

    /// <summary>Porcentaje que muestra cada código de tarifa del IVA (Tabla 17 de la ficha 2.34).</summary>
    private static readonly Dictionary<string, string> RatePercent = new(StringComparer.Ordinal)
    {
        ["0"] = "0%", ["2"] = "12%", ["3"] = "14%", ["4"] = "15%", ["5"] = "5%", ["10"] = "13%"
    };

    /// <summary>
    /// Arma el RIDE de una factura autorizada. La hora de autorización se muestra en hora de Ecuador. Falla si la factura no está autorizada: el RIDE exige el número de autorización.
    /// </summary>
    public static RideModel From(Invoice invoice, DateTimeOffset authorizedAt, RideLogo? logo, string timeZoneId = "America/Guayaquil")
    {
        if (invoice.AuthorizationNumber is null)
        {
            throw new InvalidOperationException("El RIDE solo se genera para una factura autorizada.");
        }

        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        var local = TimeZoneInfo.ConvertTime(authorizedAt, zone);

        var totals = new List<RideTotal>();
        foreach (var group in invoice.Taxes.OrderBy(t => t.RateCode, StringComparer.Ordinal))
        {
            var label = group.RateCode switch
            {
                "6" => "SUBTOTAL NO OBJETO DE IVA",
                "7" => "SUBTOTAL EXENTO DE IVA",
                var code => $"SUBTOTAL {RatePercent.GetValueOrDefault(code, code)}"
            };
            totals.Add(new RideTotal(label, group.TaxableBase));
        }

        totals.Add(new RideTotal("SUBTOTAL SIN IMPUESTOS", invoice.SubtotalWithoutTaxes));
        totals.Add(new RideTotal("TOTAL DESCUENTO", invoice.TotalDiscount));
        foreach (var group in invoice.Taxes.Where(t => t.Percent > 0m).OrderBy(t => t.RateCode, StringComparer.Ordinal))
        {
            totals.Add(new RideTotal($"IVA {RatePercent.GetValueOrDefault(group.RateCode, group.RateCode)}", group.Amount));
        }

        totals.Add(new RideTotal("VALOR TOTAL", invoice.Total, Emphasis: true));

        var additional = new List<RideField>();
        void Add(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                additional.Add(new RideField(label, value));
            }
        }

        Add("Email", invoice.BuyerEmail);
        Add("Teléfono", invoice.BuyerPhone);
        Add("Referencia", invoice.SourceReference);

        return new RideModel(
            invoice.IssuerLegalName,
            invoice.IssuerTradeName,
            invoice.IssuerRuc,
            invoice.IssuerHeadOfficeAddress,
            invoice.IssuerEstablishmentAddress,
            invoice.SpecialTaxpayerNumber,
            invoice.WithholdingAgentNumber,
            invoice.AccountingRequired,
            invoice.TaxRegime == TaxRegime.RimpeEmprendedor ? "CONTRIBUYENTE RÉGIMEN RIMPE" : null,
            invoice.Number,
            invoice.AuthorizationNumber,
            local.ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture),
            invoice.Environment == SriEnvironment.Production ? "PRODUCCIÓN" : "PRUEBAS",
            "NORMAL",
            invoice.AccessKey,
            invoice.BuyerName,
            invoice.BuyerIdNumber,
            invoice.IssueDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
            invoice.BuyerAddress,
            [.. invoice.Lines.OrderBy(l => l.LineNumber).Select(l => new RideLine(l.MainCode, l.Description, l.Quantity, l.UnitPrice, l.Discount, l.Subtotal))],
            additional,
            [.. invoice.Payments.Select(p => new RideField($"{p.MethodCode} - {PaymentNames.GetValueOrDefault(p.MethodCode, p.MethodCode)}", Money(p.Amount)))],
            totals,
            logo,
            invoice.DocumentType == InvoiceDocumentType.CreditNote ? "NOTA DE CRÉDITO" : "FACTURA",
            invoice.DocumentType == InvoiceDocumentType.CreditNote ? "Nota de crédito" : "Factura",
            invoice.DocumentType == InvoiceDocumentType.CreditNote && invoice.ModifiedDocumentNumber is { } modifiedNumber && invoice.ModifiedIssueDate is { } modifiedDate
                ?
                [
                    new RideField("Comprobante que se modifica:", $"FACTURA {modifiedNumber}"),
                    new RideField("Fecha comprobante modificado:", modifiedDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)),
                    new RideField("Razón de modificación:", invoice.ModificationReason ?? string.Empty)
                ]
                : null);
    }

    /// <summary>Importe como en el modelo oficial del RIDE: coma decimal y punto de miles (1.234,50). No usa la cultura del servidor.</summary>
    public static string Money(decimal value) =>
        value.ToString("N2", CultureInfo.InvariantCulture).Replace(",", "§", StringComparison.Ordinal).Replace(".", ",", StringComparison.Ordinal).Replace("§", ".", StringComparison.Ordinal);
}
