using FitCore.Application.Core;
using FitCore.SharedKernel;

namespace FitCore.Invoicing.Domain;

public sealed record CalculatedLine(
    int LineNumber, string MainCode, string Description, decimal Quantity, decimal UnitPrice, decimal Discount, decimal Subtotal,
    string TaxKind, string RateCode, decimal TaxPercent, decimal TaxAmount);

public sealed record CalculatedTax(string TaxCode, string RateCode, decimal Percent, decimal TaxableBase, decimal Amount);

public sealed record CalculatedPayment(string MethodCode, string Method, decimal Amount);

/// <summary>
/// Resultado del cálculo de un comprobante. <c>RoundingDifference</c> = lo cobrado menos el total del comprobante: es cero salvo cuando ninguna base de centavos permite que
/// base + IVA redondeado dé exactamente lo cobrado (ver <see cref="InvoiceCalculator.SplitGross"/>).
/// </summary>
public sealed record InvoiceCalculation(
    IReadOnlyList<CalculatedLine> Lines,
    IReadOnlyList<CalculatedTax> Taxes,
    IReadOnlyList<CalculatedPayment> Payments,
    decimal SubtotalWithoutTaxes,
    decimal TotalDiscount,
    decimal TotalTax,
    decimal Total,
    decimal RoundingDifference);

public static class CalculationErrors
{
    public static readonly Error TaxRateUnsupported = Error.Validation("invoicing.tax_rate_unsupported", "La tarifa de impuesto de la venta no tiene un código del SRI conocido.");
    public static readonly Error QuantityUnsupported = Error.Validation("invoicing.quantity_unsupported", "Solo se factura una unidad por línea.");
    public static readonly Error AmountsInvalid = Error.Validation("invoicing.line_amounts_invalid", "Los importes de la venta no son válidos.");
}

/// <summary>
/// Motor de cálculo del comprobante. El gimnasio vende con el IVA <b>incluido</b>; el SRI exige base, descuento y precio sin impuestos con 2 decimales, y que el IVA de cada línea
/// sea la tarifa aplicada a su base (error 52 «error en diferencias» si los cálculos no cuadran). Todo importe sigue la única regla de redondeo del sistema (2 decimales, lejos de cero).
/// </summary>
public static class InvoiceCalculator
{
    /// <summary>
    /// Desglosa un precio con impuesto en base e IVA de modo que <c>IVA = redondeo(base × tarifa)</c> y <c>base + IVA</c> sea lo cobrado. Se prueban las bases de centavos vecinas a
    /// <c>precio / (1 + tarifa)</c>. Si ninguna cuadra exactamente (ocurre en una fracción pequeña de precios, porque al subir la base un centavo el total sube uno o dos), se elige la que
    /// deja el total más cercano al cobrado y, en un empate, el <b>menor</b> total: nunca se declara una venta mayor a la cobrada. La diferencia (1 centavo) queda registrada en el comprobante.
    /// </summary>
    public static (decimal Base, decimal Tax) SplitGross(decimal gross, decimal percent)
    {
        gross = Money.Round(gross);
        if (percent == 0m)
        {
            return (gross, 0m);
        }

        var approx = Money.Round(gross / (1m + (percent / 100m)));
        var best = (Base: 0m, Tax: 0m, Distance: decimal.MaxValue);
        for (var step = -3; step <= 3; step++)
        {
            var candidate = approx + (step / 100m);
            if (candidate < 0m)
            {
                continue;
            }

            var tax = Money.Round(candidate * percent / 100m);
            var distance = Math.Abs(candidate + tax - gross);
            // Empate: se queda con el primero, que es la base menor (se recorren de menor a mayor).
            if (distance < best.Distance)
            {
                best = (candidate, tax, distance);
            }
        }

        return (best.Base, best.Tax);
    }

    public static Result<InvoiceCalculation> Calculate(InvoiceSource source, CardPaymentMethod card)
    {
        var lines = new List<CalculatedLine>();
        var number = 0;
        foreach (var line in source.Lines)
        {
            number++;
            if (line.Quantity != 1m)
            {
                return CalculationErrors.QuantityUnsupported;
            }

            if (line.GrossAmount < 0m || line.DiscountAmount < 0m || line.PayableAmount <= 0m || line.DiscountAmount > line.GrossAmount)
            {
                return CalculationErrors.AmountsInvalid;
            }

            var rateCode = SriCatalogs.IvaRateCode(line.TaxKind, line.TaxPercent);
            if (rateCode is null)
            {
                return CalculationErrors.TaxRateUnsupported;
            }

            var net = SplitGross(line.PayableAmount, line.TaxPercent);
            var netBeforeDiscount = SplitGross(line.GrossAmount, line.TaxPercent).Base;
            // precio unitario × cantidad − descuento = base de la línea, siempre exacto.
            var discount = Math.Max(0m, netBeforeDiscount - net.Base);
            lines.Add(new CalculatedLine(
                number, string.IsNullOrWhiteSpace(source.Reference) ? "VENTA" : source.Reference, line.Description, 1m, net.Base + discount, discount, net.Base,
                line.TaxKind, rateCode, line.TaxPercent, net.Tax));
        }

        if (lines.Count == 0)
        {
            return CalculationErrors.AmountsInvalid;
        }

        // IVA total por tarifa: la ficha manda sumar las bases de cada tarifa y aplicarle la tarifa vigente.
        var taxes = lines.GroupBy(l => (l.RateCode, l.TaxPercent))
            .Select(g =>
            {
                var taxable = g.Sum(l => l.Subtotal);
                return new CalculatedTax(SriCatalogs.IvaTaxCode, g.Key.RateCode, g.Key.TaxPercent, taxable, Money.Round(taxable * g.Key.TaxPercent / 100m));
            })
            .OrderBy(t => t.RateCode, StringComparer.Ordinal)
            .ToList();

        var subtotal = lines.Sum(l => l.Subtotal);
        var totalTax = taxes.Sum(t => t.Amount);
        var total = subtotal + totalTax;

        var payments = source.Payments
            .Select(p => new CalculatedPayment(SriCatalogs.PaymentMethodCode(p.Method, card), p.Method, Money.Round(p.Amount)))
            .ToList();
        var collected = source.PayableTotal;
        if (payments.Count > 0)
        {
            // Las formas de pago deben sumar el total del comprobante: la diferencia de redondeo se aplica al pago mayor.
            var delta = total - payments.Sum(p => p.Amount);
            if (delta != 0m)
            {
                var index = payments.IndexOf(payments.MaxBy(p => p.Amount)!);
                payments[index] = payments[index] with { Amount = payments[index].Amount + delta };
            }
        }

        return new InvoiceCalculation(lines, taxes, payments, subtotal, lines.Sum(l => l.Discount), totalTax, total, collected - total);
    }
}
