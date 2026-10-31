using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using FitCore.Invoicing.Domain;
using FitCore.Invoicing.Rendering;
using UglyToad.PdfPig;

namespace FitCore.Invoicing.UnitTests;

internal static class RideSamples
{
    public static readonly DateTimeOffset AuthorizedAt = new(2026, 10, 31, 12, 44, 24, TimeSpan.Zero); // 07:44:24 en Ecuador

    public static Invoice Authorized(decimal payable = 20m, string taxKind = "General", decimal percent = 15m, TaxRegime regime = TaxRegime.General, string? special = null, string? withholding = null)
    {
        var invoice = SampleInvoices.Create(payable, payable, taxKind, percent, regime, special: special, withholding: withholding);
        invoice.AuthorizationNumber = invoice.AccessKey;
        invoice.AuthorizedAt = AuthorizedAt;
        invoice.Status = InvoiceStatus.Authorized;
        return invoice;
    }

    /// <summary>PNG de un color, armado a mano (sin bibliotecas de imágenes).</summary>
    public static byte[] Png(int width, int height)
    {
        static uint Crc(byte[] data)
        {
            var crc = 0xffffffffu;
            foreach (var b in data)
            {
                crc ^= b;
                for (var k = 0; k < 8; k++)
                {
                    crc = (crc & 1) != 0 ? 0xedb88320u ^ (crc >> 1) : crc >> 1;
                }
            }

            return ~crc;
        }

        static byte[] BigEndian(uint value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

        static byte[] Chunk(string type, byte[] data)
        {
            var body = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
            return BigEndian((uint)data.Length).Concat(body).Concat(BigEndian(Crc(body))).ToArray();
        }

        var header = BigEndian((uint)width).Concat(BigEndian((uint)height)).Concat(new byte[] { 8, 2, 0, 0, 0 }).ToArray();
        var row = new byte[1 + (width * 3)];
        for (var i = 1; i < row.Length; i += 3)
        {
            row[i] = 0x1f;
            row[i + 1] = 0x7a;
            row[i + 2] = 0x6b;
        }

        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            for (var y = 0; y < height; y++)
            {
                z.Write(row);
            }
        }

        return new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }.Concat(Chunk("IHDR", header)).Concat(Chunk("IDAT", compressed.ToArray())).Concat(Chunk("IEND", [])).ToArray();
    }

    public static string TextOf(byte[] pdf)
    {
        using var document = PdfDocument.Open(pdf);
        return string.Join("\n", document.GetPages().Select(p => p.Text));
    }
}

public class RideModelTests
{
    private static RideModel Model(Invoice invoice, RideLogo? logo = null) => RideModel.From(invoice, RideSamples.AuthorizedAt, logo);

    [Fact]
    public void An_invoice_that_is_not_authorized_has_no_ride()
    {
        var invoice = SampleInvoices.Create();

        Assert.Throws<InvalidOperationException>(() => RideModel.From(invoice, DateTimeOffset.UtcNow, null));
    }

    [Fact]
    public void The_authorization_time_is_shown_in_ecuador_time_and_the_environment_by_name()
    {
        var model = Model(RideSamples.Authorized());

        Assert.Equal("31/10/2026 07:44:24", model.AuthorizedAtLocal);
        Assert.Equal("PRUEBAS", model.EnvironmentName);
        Assert.Equal("NORMAL", model.EmissionName);
        Assert.Equal(model.AccessKey, model.AuthorizationNumber);
    }

    [Fact]
    public void Totals_follow_the_official_model_with_only_the_subtotals_that_have_value()
    {
        var model = Model(RideSamples.Authorized(30m));

        Assert.Equal(["SUBTOTAL 15%", "SUBTOTAL SIN IMPUESTOS", "TOTAL DESCUENTO", "IVA 15%", "VALOR TOTAL"], model.Totals.Select(t => t.Label));
        Assert.Equal([26.09m, 26.09m, 0m, 3.91m, 30m], model.Totals.Select(t => t.Amount));
        Assert.True(model.Totals[^1].Emphasis);
    }

    [Theory]
    [InlineData("Zero", 0, "SUBTOTAL 0%")]
    [InlineData("NotSubject", 0, "SUBTOTAL NO OBJETO DE IVA")]
    [InlineData("Exempt", 0, "SUBTOTAL EXENTO DE IVA")]
    public void Tax_free_sales_show_their_own_subtotal_and_no_iva_row(string kind, double percent, string label)
    {
        var model = Model(RideSamples.Authorized(40m, kind, (decimal)percent));

        Assert.Equal([label, "SUBTOTAL SIN IMPUESTOS", "TOTAL DESCUENTO", "VALOR TOTAL"], model.Totals.Select(t => t.Label));
    }

    [Fact]
    public void Payments_use_the_names_of_table_24()
    {
        var model = Model(RideSamples.Authorized());

        Assert.Equal("19 - TARJETA DE CRÉDITO", model.Payments.Single().Label);
        Assert.Equal("20,00", model.Payments.Single().Value);
    }

    [Theory]
    [InlineData(0, "0,00")]
    [InlineData(20, "20,00")]
    [InlineData(1234.5, "1.234,50")]
    [InlineData(1234567.891, "1.234.567,89")]
    public void Money_uses_decimal_comma_and_thousands_dot_like_the_official_model(double value, string expected) =>
        Assert.Equal(expected, RideModel.Money((decimal)value));

    [Fact]
    public void The_rimpe_legend_withholding_agent_and_special_taxpayer_appear_only_when_they_apply()
    {
        var plain = Model(RideSamples.Authorized());
        var rimpe = Model(RideSamples.Authorized(regime: TaxRegime.RimpeEmprendedor, special: "5368", withholding: "1"));

        Assert.Null(plain.RegimeLegend);
        Assert.Equal("CONTRIBUYENTE RÉGIMEN RIMPE", rimpe.RegimeLegend);
        Assert.Equal("5368", rimpe.SpecialTaxpayerNumber);
        Assert.Equal("1", rimpe.WithholdingAgentNumber);
    }
}

public class RideRendererTests
{
    private static byte[] Render(Invoice? invoice = null, RideLogo? logo = null) =>
        RideRenderer.Render(RideModel.From(invoice ?? RideSamples.Authorized(), RideSamples.AuthorizedAt, logo));

    [Fact]
    public void It_produces_a_valid_single_page_pdf_for_a_standard_invoice()
    {
        var pdf = Render();

        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(pdf, 0, 5), StringComparison.Ordinal);
        using var document = PdfDocument.Open(pdf);
        Assert.Equal(1, document.NumberOfPages);
        Assert.Equal("Factura 001-001-000000001", document.Information.Title);
    }

    [Fact]
    public void The_pdf_carries_every_field_the_official_model_requires()
    {
        var invoice = RideSamples.Authorized(30m, special: "5368", withholding: "1");
        var text = RideSamples.TextOf(Render(invoice));

        foreach (var expected in new[]
        {
            "Fitness Quito S.A.", "R.U.C.: 1790000028001", "FACTURA", "No. 001-001-000000001", "NÚMERO DE AUTORIZACIÓN", invoice.AccessKey[..20],
            "FECHA Y HORA DE AUTORIZACIÓN", "31/10/2026 07:44:24", "AMBIENTE: PRUEBAS", "EMISIÓN: NORMAL", "CLAVE DE ACCESO",
            "Dirección Matriz", "Dirección Sucursal", "Contribuyente Especial Nro: 5368", "OBLIGADO A LLEVAR CONTABILIDAD: SI", "Agente de Retención Resolución No. 1",
            "Razón Social / Nombres y Apellidos", "Ana Pérez", "1712345675", "09/03/2026", "Cod. Principal", "Descripción", "Precio Unitario", "Membresía mensual",
            "Información Adicional", "ana@correo.test", "Forma de pago", "19 - TARJETA DE CRÉDITO", "SUBTOTAL 15%", "26,09", "IVA 15%", "3,91", "VALOR TOTAL", "30,00"
        })
        {
            Assert.Contains(expected, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Accents_and_the_letter_enye_survive_in_the_pdf()
    {
        var invoice = SampleInvoices.Create(buyerName: "Peña Núñez María José", address: "Av. 9 de Octubre y Whymper — Quito");
        invoice.AuthorizationNumber = invoice.AccessKey;
        invoice.AuthorizedAt = RideSamples.AuthorizedAt;

        var text = RideSamples.TextOf(Render(invoice));

        Assert.Contains("Peña Núñez María José", text, StringComparison.Ordinal);
        Assert.Contains("Whymper", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_logo_is_embedded_when_the_gym_has_one()
    {
        var without = Render();
        var with = Render(logo: new RideLogo(RideSamples.Png(240, 90), 240, 90));

        Assert.True(with.Length > without.Length);
        using var document = PdfDocument.Open(with);
        Assert.Single(document.GetPage(1).GetImages()); // el QR es vectorial: la única imagen es el logo
        using var plain = PdfDocument.Open(without);
        Assert.Empty(plain.GetPage(1).GetImages());
    }

    [Fact]
    public void Many_lines_continue_on_the_next_page_and_every_page_is_numbered()
    {
        var invoice = RideSamples.Authorized();
        var template = invoice.Lines.Single();
        invoice.Lines.Clear();
        for (var i = 1; i <= 70; i++)
        {
            invoice.Lines.Add(new InvoiceLine
            {
                Id = Guid.NewGuid(), InvoiceId = invoice.Id, LineNumber = i, MainCode = $"M{i:000}", Description = $"Concepto número {i} con una descripción un poco larga para que ocupe más de una línea del detalle",
                Quantity = 1, UnitPrice = 1m, Discount = 0m, Subtotal = 1m, TaxKind = "General", TaxCode = "2", TaxRateCode = "4", TaxPercent = 15m, TaxAmount = 0.15m, TenantId = template.TenantId
            });
        }

        var pdf = Render(invoice);
        var text = RideSamples.TextOf(pdf);

        using var document = PdfDocument.Open(pdf);
        Assert.True(document.NumberOfPages >= 2, $"páginas: {document.NumberOfPages}");
        Assert.Contains("(continuación)", text, StringComparison.Ordinal);
        Assert.Contains("M001", text, StringComparison.Ordinal);
        Assert.Contains("M070", text, StringComparison.Ordinal);
        Assert.Contains($"Página {document.NumberOfPages} de {document.NumberOfPages}", text, StringComparison.Ordinal);
        // Los totales aparecen una sola vez, al final.
        Assert.Equal(1, Regex.Matches(text, "VALOR TOTAL").Count);
    }

    [Fact]
    public void Very_long_texts_wrap_inside_their_cells_without_failing()
    {
        var invoice = RideSamples.Authorized();
        typeof(InvoiceLine).GetProperty(nameof(InvoiceLine.Description))!.SetValue(invoice.Lines[0], new string('Z', 300));

        var pdf = Render(invoice);

        using var document = PdfDocument.Open(pdf);
        Assert.True(document.NumberOfPages >= 1);
    }

    [Fact]
    public void The_same_invoice_always_renders_the_same_content()
    {
        var invoice = RideSamples.Authorized();

        Assert.Equal(RideSamples.TextOf(Render(invoice)), RideSamples.TextOf(Render(invoice)));
    }

    [Fact]
    public void A_sample_can_be_written_to_disk_for_a_visual_check_when_requested()
    {
        var path = Environment.GetEnvironmentVariable("FITCORE_RIDE_OUT");
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        var invoice = RideSamples.Authorized(30m, special: "5368", withholding: "1");
        File.WriteAllBytes(path, Render(invoice, new RideLogo(RideSamples.Png(240, 90), 240, 90)));
    }
}
