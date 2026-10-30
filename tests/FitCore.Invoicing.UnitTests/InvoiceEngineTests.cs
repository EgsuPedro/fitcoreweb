using FitCore.Application.Core;
using FitCore.Invoicing.Domain;
using FitCore.SharedKernel;

namespace FitCore.Invoicing.UnitTests;

public class AccessKeyTests
{
    [Fact]
    public void The_official_example_of_the_SRI_technical_sheet_gives_6()
    {
        // Ficha técnica 5.2, ejemplo de verificación con módulo 11: cadena 41261533 → suma 104 → 104 mod 11 = 5 → 11 − 5 = 6.
        Assert.Equal(6, AccessKey.CheckDigit("41261533"));
    }

    [Theory]
    [InlineData("0", 0)] // suma 0 → 11 − 0 = 11 → dígito 0
    [InlineData("5", 1)] // 5×2 = 10 → 10 mod 11 = 10 → 11 − 10 = 1
    [InlineData("1", 9)] // 1×2 = 2 → 11 − 2 = 9
    public void Digits_eleven_and_ten_become_zero_and_one_as_the_sheet_says(string digits, int expected) =>
        Assert.Equal(expected, AccessKey.CheckDigit(digits));

    [Fact]
    public void A_key_is_49_digits_with_every_field_in_its_place()
    {
        var key = AccessKey.Build(new DateOnly(2026, 3, 9), "01", "1790000028001", SriEnvironment.Test, "001", "002", 123, "12345678");

        Assert.Equal(49, key.Length);
        Assert.Equal("09032026", key[..8]);
        Assert.Equal("01", key.Substring(8, 2));
        Assert.Equal("1790000028001", key.Substring(10, 13));
        Assert.Equal("1", key.Substring(23, 1)); // pruebas
        Assert.Equal("001002", key.Substring(24, 6));
        Assert.Equal("000000123", key.Substring(30, 9));
        Assert.Equal("12345678", key.Substring(39, 8));
        Assert.Equal("1", key.Substring(47, 1)); // emisión normal
        Assert.True(AccessKey.IsValid(key));
    }

    [Fact]
    public void Production_uses_environment_code_2()
    {
        var key = AccessKey.Build(new DateOnly(2026, 3, 9), "01", "1790000028001", SriEnvironment.Production, "001", "001", 1, "00000001");

        Assert.Equal("2", key.Substring(23, 1));
    }

    [Fact]
    public void Changing_any_digit_of_a_valid_key_invalidates_it()
    {
        var key = AccessKey.Build(new DateOnly(2026, 3, 9), "01", "1790000028001", SriEnvironment.Test, "001", "001", 1, "87654321");

        Assert.True(AccessKey.IsValid(key));
        for (var i = 0; i < 48; i++)
        {
            var changed = key.ToCharArray();
            changed[i] = changed[i] == '9' ? '0' : (char)(changed[i] + 1);
            // Un cambio de un solo dígito siempre altera la suma módulo 11: nunca puede seguir siendo válida.
            Assert.False(AccessKey.IsValid(new string(changed)), $"posición {i}");
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("123")]
    [InlineData("1234567890123456789012345678901234567890123456789")]
    [InlineData("abcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvw")]
    public void Malformed_keys_are_not_valid(string? key) => Assert.False(AccessKey.IsValid(key));

    [Fact]
    public void Fields_out_of_range_are_refused()
    {
        var date = new DateOnly(2026, 3, 9);
        Assert.Throws<ArgumentOutOfRangeException>(() => AccessKey.Build(date, "01", "1790000028001", SriEnvironment.Test, "001", "001", 0, "12345678"));
        Assert.Throws<ArgumentOutOfRangeException>(() => AccessKey.Build(date, "01", "1790000028001", SriEnvironment.Test, "001", "001", 1_000_000_000, "12345678"));
        Assert.Throws<ArgumentException>(() => AccessKey.Build(date, "01", "179000002800", SriEnvironment.Test, "001", "001", 1, "12345678"));
        Assert.Throws<ArgumentException>(() => AccessKey.Build(date, "01", "1790000028001", SriEnvironment.Test, "01", "001", 1, "12345678"));
        Assert.Throws<ArgumentException>(() => AccessKey.Build(date, "01", "1790000028001", SriEnvironment.Test, "001", "001", 1, "1234567"));
    }

    [Fact]
    public void Numeric_codes_are_eight_digits_and_vary()
    {
        var codes = Enumerable.Range(0, 50).Select(_ => AccessKey.NewNumericCode()).ToList();

        Assert.All(codes, c => Assert.Matches("^[0-9]{8}$", c));
        Assert.True(codes.Distinct().Count() > 40);
    }
}

public class InvoiceCalculatorTests
{
    private static InvoiceSource Source(decimal gross, decimal discount, decimal payable, string taxKind = "General", decimal percent = 15m, params (string Method, decimal Amount)[] payments) => new(
        InvoiceSourceTypes.Obligation, Guid.NewGuid(), "OB-000001", Guid.NewGuid(), new DateOnly(2026, 3, 9), "USD", InvoiceSourceStatus.Paid,
        new InvoiceSourceBuyer(null, "Ana", "Pérez", SourceIdKind.Cedula, "1712345675", null, null, null),
        [new InvoiceSourceLine("Membresía", 1m, gross, discount, payable, 0m, 0m, taxKind, percent)],
        [.. (payments.Length == 0 ? [("Cash", payable)] : payments).Select(p => new InvoiceSourcePayment(p.Method, p.Amount, DateTimeOffset.UtcNow, null))]);

    [Fact]
    public void Every_price_from_one_cent_to_two_thousand_dollars_satisfies_the_SRI_checks_within_one_cent()
    {
        var exact = 0;
        var oneCentOff = 0;
        for (var cents = 1; cents <= 200_000; cents++)
        {
            var gross = cents / 100m;
            var (b, t) = InvoiceCalculator.SplitGross(gross, 15m);

            // El IVA siempre es la tarifa aplicada a la base, redondeada: lo que el SRI recalcula.
            Assert.Equal(Money.Round(b * 15m / 100m), t);
            Assert.True(b >= 0m && t >= 0m);
            var difference = gross - (b + t);
            if (difference == 0m)
            {
                exact++;
            }
            else
            {
                // Cuando no existe una base exacta, queda un centavo de diferencia y siempre en contra de la venta (nunca se declara de más).
                Assert.Equal(0.01m, difference);
                oneCentOff++;
            }
        }

        // Medido: con 15 % algo más del 13 % de los precios (26087 de 200000) no admite una base de centavos cuyo IVA redondeado dé exactamente lo cobrado, porque al subir la base un
        // centavo el total sube uno o dos. En esos casos el comprobante queda un centavo por debajo de lo cobrado (y así se registra). Si esta proporción cambia, cambió la regla.
        Assert.Equal(26_087, oneCentOff);
        Assert.Equal(173_913, exact);
    }

    [Theory]
    [InlineData(30.00, 26.09, 3.91)] // 26.09 × 15 % = 3.9135 → 3.91
    [InlineData(115.00, 100.00, 15.00)]
    [InlineData(11.50, 10.00, 1.50)]
    [InlineData(0.01, 0.01, 0.00)]
    [InlineData(1.15, 1.00, 0.15)]
    public void Typical_prices_split_exactly(double gross, double expectedBase, double expectedTax)
    {
        var (b, t) = InvoiceCalculator.SplitGross((decimal)gross, 15m);

        Assert.Equal((decimal)expectedBase, b);
        Assert.Equal((decimal)expectedTax, t);
    }

    [Fact]
    public void A_price_without_an_exact_split_never_overstates_the_sale()
    {
        // 0.11 con 15 %: base 0.10 → IVA 0.015 → 0.02 (total 0.12); base 0.09 → IVA 0.0135 → 0.01 (total 0.10). Ninguna da 0.11: gana el menor.
        var (b, t) = InvoiceCalculator.SplitGross(0.11m, 15m);

        Assert.Equal(0.10m, b + t);
    }

    [Fact]
    public void Zero_percent_keeps_the_whole_amount_as_base()
    {
        Assert.Equal((25m, 0m), InvoiceCalculator.SplitGross(25m, 0m));
    }

    [Fact]
    public void A_line_keeps_the_identity_unit_price_times_quantity_minus_discount_equals_subtotal()
    {
        // 50 de lista, 20 de descuento → se cobra 30.
        var result = InvoiceCalculator.Calculate(Source(50m, 20m, 30m), CardPaymentMethod.Credit);

        Assert.True(result.IsSuccess);
        var line = Assert.Single(result.Value.Lines);
        Assert.Equal(26.09m, line.Subtotal);
        Assert.Equal(3.91m, line.TaxAmount);
        Assert.Equal(line.Subtotal, (line.UnitPrice * line.Quantity) - line.Discount);
        Assert.Equal(43.48m, line.UnitPrice); // 50 / 1.15 = 43.478…
        Assert.Equal(17.39m, line.Discount); // 43.48 − 26.09
        Assert.Equal("4", line.RateCode);
    }

    [Fact]
    public void Totals_add_up_and_the_tax_total_is_the_rate_applied_to_the_base()
    {
        var result = InvoiceCalculator.Calculate(Source(30m, 0m, 30m), CardPaymentMethod.Credit).Value;

        Assert.Equal(26.09m, result.SubtotalWithoutTaxes);
        Assert.Equal(3.91m, result.TotalTax);
        Assert.Equal(30m, result.Total);
        Assert.Equal(0m, result.RoundingDifference);
        Assert.Equal(0m, result.TotalDiscount);
        var tax = Assert.Single(result.Taxes);
        Assert.Equal(("2", "4", 15m, 26.09m, 3.91m), (tax.TaxCode, tax.RateCode, tax.Percent, tax.TaxableBase, tax.Amount));
    }

    [Fact]
    public void A_one_cent_rounding_difference_is_recorded_and_the_payments_still_add_up_to_the_invoice_total()
    {
        // 0.11 no admite desglose exacto: el comprobante suma 0.10 y se cobraron 0.11.
        var result = InvoiceCalculator.Calculate(Source(0.11m, 0m, 0.11m, payments: ("Cash", 0.11m)), CardPaymentMethod.Credit).Value;

        Assert.Equal(0.10m, result.Total);
        Assert.Equal(0.01m, result.RoundingDifference);
        Assert.Equal(result.Total, result.Payments.Sum(p => p.Amount));
    }

    [Fact]
    public void The_rounding_difference_is_applied_to_the_largest_payment_so_the_payments_equal_the_total()
    {
        var result = InvoiceCalculator.Calculate(Source(0.11m, 0m, 0.11m, payments: [("Cash", 0.05m), ("Card", 0.06m)]), CardPaymentMethod.Debit).Value;

        Assert.Equal(result.Total, result.Payments.Sum(p => p.Amount));
        Assert.Equal(0.05m, result.Payments.Single(p => p.Method == "Cash").Amount);
        Assert.Equal(0.05m, result.Payments.Single(p => p.Method == "Card").Amount);
    }

    [Theory]
    [InlineData("Zero", 0, "0")]
    [InlineData("NotSubject", 0, "6")]
    [InlineData("Exempt", 0, "7")]
    [InlineData("General", 12, "2")]
    [InlineData("General", 14, "3")]
    [InlineData("General", 15, "4")]
    [InlineData("General", 5, "5")]
    [InlineData("General", 13, "10")]
    public void Tax_kinds_map_to_the_SRI_rate_codes_and_no_tax_is_charged_when_free(string kind, double percent, string code)
    {
        var result = InvoiceCalculator.Calculate(Source(40m, 0m, 40m, kind, (decimal)percent), CardPaymentMethod.Credit).Value;

        Assert.Equal(code, result.Lines[0].RateCode);
        Assert.Equal(code, result.Taxes[0].RateCode);
        if (percent == 0)
        {
            Assert.Equal(0m, result.TotalTax);
            Assert.Equal(40m, result.SubtotalWithoutTaxes);
        }
    }

    [Theory]
    [InlineData("General", 8)] // tarifa que el sistema no conoce: nunca se adivina un código
    [InlineData("General", 0)]
    [InlineData("Other", 15)]
    public void An_unknown_tax_rate_is_refused_instead_of_guessed(string kind, double percent)
    {
        var result = InvoiceCalculator.Calculate(Source(40m, 0m, 40m, kind, (decimal)percent), CardPaymentMethod.Credit);

        Assert.True(result.IsFailure);
        Assert.Equal("invoicing.tax_rate_unsupported", result.Error.Code);
    }

    [Fact]
    public void Quantities_other_than_one_and_invalid_amounts_are_refused()
    {
        var withQuantity = Source(30m, 0m, 30m) with { Lines = [new InvoiceSourceLine("X", 2m, 30m, 0m, 30m, 0m, 0m, "General", 15m)] };
        Assert.Equal("invoicing.quantity_unsupported", InvoiceCalculator.Calculate(withQuantity, CardPaymentMethod.Credit).Error.Code);

        Assert.Equal("invoicing.line_amounts_invalid", InvoiceCalculator.Calculate(Source(30m, 40m, -10m), CardPaymentMethod.Credit).Error.Code);
        Assert.Equal("invoicing.line_amounts_invalid", InvoiceCalculator.Calculate(Source(30m, 0m, 0m), CardPaymentMethod.Credit).Error.Code);
    }

    [Theory]
    [InlineData("Cash", CardPaymentMethod.Credit, "01")]
    [InlineData("Card", CardPaymentMethod.Credit, "19")]
    [InlineData("Card", CardPaymentMethod.Debit, "16")]
    [InlineData("BankTransfer", CardPaymentMethod.Credit, "20")]
    [InlineData("Other", CardPaymentMethod.Debit, "20")]
    public void Payment_methods_map_to_the_SRI_codes(string method, CardPaymentMethod card, string code) =>
        Assert.Equal(code, SriCatalogs.PaymentMethodCode(method, card));

    [Fact]
    public void Environment_codes_follow_table_4()
    {
        Assert.Equal("1", SriCatalogs.EnvironmentCode(SriEnvironment.Test));
        Assert.Equal("2", SriCatalogs.EnvironmentCode(SriEnvironment.Production));
    }
}
