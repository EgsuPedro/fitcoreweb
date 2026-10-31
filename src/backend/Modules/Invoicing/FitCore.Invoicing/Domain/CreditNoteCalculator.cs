using FitCore.SharedKernel;

namespace FitCore.Invoicing.Domain;

/// <summary>Lo que ya se acreditó de una línea de la factura con notas de crédito vigentes (base e IVA).</summary>
public sealed record CreditedAmount(decimal Base, decimal Tax)
{
    public static readonly CreditedAmount None = new(0m, 0m);

    public decimal Gross => Base + Tax;
}

/// <summary>Una línea de la factura y lo que todavía se puede acreditar de ella.</summary>
public sealed record CreditableLine(InvoiceLine Line, CreditedAmount Credited)
{
    public decimal RemainingBase => Line.Subtotal - Credited.Base;

    public decimal RemainingTax => Line.TaxAmount - Credited.Tax;

    /// <summary>Lo que queda por acreditar con IVA incluido (así vende el gimnasio y así se le pide al usuario).</summary>
    public decimal RemainingGross => RemainingBase + RemainingTax;

    public decimal OriginalGross => Line.Subtotal + Line.TaxAmount;
}

/// <summary>Qué se pide acreditar de una línea: un importe con IVA incluido, o todo lo que quede si va vacío.</summary>
public sealed record CreditRequestLine(Guid LineId, decimal? Amount);

public sealed record CreditNoteLine(
    int LineNumber, Guid ModifiedLineId, string MainCode, string Description, decimal Base, string TaxKind, string TaxCode, string RateCode, decimal TaxPercent, decimal TaxAmount);

public sealed record CreditNoteCalculation(IReadOnlyList<CreditNoteLine> Lines, IReadOnlyList<CalculatedTax> Taxes, decimal SubtotalWithoutTaxes, decimal TotalTax, decimal Total);

public static class CreditNoteErrors
{
    public static readonly Error FullyCredited = Error.Conflict("invoicing.invoice_fully_credited", "La factura ya está acreditada por completo.");
    public static readonly Error LineNotFound = Error.Validation("invoicing.credit_line_not_found", "Una de las líneas no pertenece a la factura.");
    public static readonly Error LineRepeated = Error.Validation("invoicing.credit_line_repeated", "Una línea se indicó más de una vez.");
    public static readonly Error LineExhausted = Error.Validation("invoicing.credit_line_exhausted", "Una de las líneas ya está acreditada por completo.");
    public static readonly Error AmountInvalid = Error.Validation("invoicing.credit_amount_invalid", "El importe a acreditar debe ser mayor que cero y tener como máximo dos decimales.");
    public static readonly Error AmountExceeds = Error.Validation("invoicing.credit_amount_exceeds", "El importe a acreditar supera lo que queda de esa línea.");
    public static readonly Error AmountTooSmall = Error.Validation("invoicing.credit_amount_too_small", "El importe a acreditar es demasiado pequeño para facturarlo.");
}

/// <summary>
/// Cálculo de una nota de crédito sobre una factura autorizada. La ficha del SRI manda que la tarifa del IVA sea la <b>del documento que se modifica</b>: por eso cada línea hereda la tarifa y el
/// código de la línea de la factura, nunca la vigente hoy. Se acredita por línea, con el importe <b>con IVA incluido</b> (así vende el gimnasio): si se acredita todo lo que queda de la línea se
/// usa su base exacta; si es una parte, se desglosa con la misma regla de redondeo de la factura. El IVA de cada línea es siempre la tarifa aplicada a su base.
/// </summary>
public static class CreditNoteCalculator
{
    /// <summary>Las líneas de la factura con lo que queda de cada una, dado lo ya acreditado por línea.</summary>
    public static IReadOnlyList<CreditableLine> Creditable(Invoice invoice, IReadOnlyDictionary<Guid, CreditedAmount> credited) =>
        [.. invoice.Lines.OrderBy(l => l.LineNumber).Select(l => new CreditableLine(l, credited.GetValueOrDefault(l.Id, CreditedAmount.None)))];

    /// <param name="requested">Las líneas a acreditar. Vacío o nulo: todo lo que quede de cada línea.</param>
    public static Result<CreditNoteCalculation> Calculate(IReadOnlyList<CreditableLine> creditable, IReadOnlyList<CreditRequestLine>? requested)
    {
        var picks = new List<(CreditableLine Line, decimal? Amount)>();
        if (requested is null || requested.Count == 0)
        {
            picks.AddRange(creditable.Where(c => c.RemainingBase > 0m).Select(c => (c, (decimal?)null)));
            if (picks.Count == 0)
            {
                return CreditNoteErrors.FullyCredited;
            }
        }
        else
        {
            if (requested.Select(r => r.LineId).Distinct().Count() != requested.Count)
            {
                return CreditNoteErrors.LineRepeated;
            }

            foreach (var r in requested)
            {
                var line = creditable.FirstOrDefault(c => c.Line.Id == r.LineId);
                if (line is null)
                {
                    return CreditNoteErrors.LineNotFound;
                }

                picks.Add((line, r.Amount));
            }
        }

        var lines = new List<CreditNoteLine>();
        foreach (var (line, amount) in picks.OrderBy(p => p.Line.Line.LineNumber))
        {
            if (line.RemainingBase <= 0m)
            {
                return CreditNoteErrors.LineExhausted;
            }

            decimal credit;
            if (amount is null || Money.Round(amount.Value) == Money.Round(line.RemainingGross))
            {
                // Todo lo que queda de la línea: la base exacta, para no dejar ni pasarse de un centavo.
                credit = line.RemainingBase;
            }
            else
            {
                var value = amount.Value;
                if (value <= 0m || Money.Round(value) != value)
                {
                    return CreditNoteErrors.AmountInvalid;
                }

                if (value > line.RemainingGross)
                {
                    return CreditNoteErrors.AmountExceeds;
                }

                credit = Math.Min(InvoiceCalculator.SplitGross(value, line.Line.TaxPercent).Base, line.RemainingBase);
                if (credit <= 0m)
                {
                    return CreditNoteErrors.AmountTooSmall;
                }
            }

            // El IVA es la tarifa de la factura aplicada a la base; solo en el caso extremo de que eso pase de lo que quedaba, se usa lo que quedaba.
            var tax = Math.Min(Money.Round(credit * line.Line.TaxPercent / 100m), Math.Max(line.RemainingTax, 0m));
            lines.Add(new CreditNoteLine(
                lines.Count + 1, line.Line.Id, line.Line.MainCode, line.Line.Description, credit, line.Line.TaxKind, line.Line.TaxCode, line.Line.TaxRateCode, line.Line.TaxPercent, tax));
        }

        var taxes = lines.GroupBy(l => (l.TaxCode, l.RateCode, l.TaxPercent))
            .Select(g =>
            {
                var taxable = g.Sum(l => l.Base);
                return new CalculatedTax(g.Key.TaxCode, g.Key.RateCode, g.Key.TaxPercent, taxable, Money.Round(taxable * g.Key.TaxPercent / 100m));
            })
            .OrderBy(t => t.RateCode, StringComparer.Ordinal)
            .ToList();
        var subtotal = lines.Sum(l => l.Base);
        var totalTax = taxes.Sum(t => t.Amount);
        return new CreditNoteCalculation(lines, taxes, subtotal, totalTax, subtotal + totalTax);
    }
}
