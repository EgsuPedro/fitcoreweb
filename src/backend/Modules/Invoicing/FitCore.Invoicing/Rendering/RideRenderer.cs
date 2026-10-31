using System.Globalization;
using PdfSharp;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using QRCoder;

namespace FitCore.Invoicing.Rendering;

/// <summary>
/// Tipografías incrustadas (DejaVu Sans Condensed, licencia libre): PDFsharp no usa las del sistema, que en los contenedores no existen, y así el PDF sale igual en cualquier servidor.
/// </summary>
internal sealed class EmbeddedFontResolver : IFontResolver
{
    public const string Family = "DejaVu Sans Condensed";
    private const string Regular = "DejaVuSansCondensed.ttf";
    private const string Bold = "DejaVuSansCondensed-Bold.ttf";

    private static readonly Dictionary<string, byte[]> Cache = [];
    private static readonly object Gate = new();

    public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic) => new(isBold ? Bold : Regular);

    public byte[]? GetFont(string faceName)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(faceName, out var cached))
            {
                return cached;
            }

            var assembly = typeof(EmbeddedFontResolver).Assembly;
            var resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith(faceName, StringComparison.Ordinal));
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return Cache[faceName] = memory.ToArray();
        }
    }
}

/// <summary>
/// Dibuja el RIDE (representación impresa) de una factura autorizada en PDF A4, con el modelo oficial del Anexo 2 de la ficha técnica del SRI. Lleva un código QR con la clave de acceso (la ficha
/// permite «otro código opcional de información»; el de barras es opcional) y el logo del gimnasio si lo tiene. El detalle se reparte en varias páginas cuando no cabe.
/// </summary>
public static class RideRenderer
{
    private const double Margin = 28;
    private const double PageWidth = 595;
    private const double PageHeight = 842;
    private const double ContentWidth = PageWidth - (2 * Margin);
    private const double Pad = 5;
    private const double Line = 11;

    private static readonly object FontGate = new();
    private static bool _fontsReady;

    private static readonly XPen Border = new(XColor.FromArgb(70, 70, 70), 0.8);
    private static readonly XBrush Ink = XBrushes.Black;
    private static readonly XBrush Muted = new XSolidBrush(XColor.FromArgb(95, 95, 95));
    private static readonly XBrush HeaderFill = new XSolidBrush(XColor.FromArgb(232, 232, 232));

    public static byte[] Render(RideModel model)
    {
        EnsureFonts();
        using var document = new PdfDocument();
        document.Info.Title = $"{model.DocumentName} {model.DocumentNumber}";
        document.Info.Author = model.IssuerLegalName;
        document.Info.Creator = "FIT360";
        document.Info.Subject = $"Representación impresa de {(model.DocumentName == "Factura" ? "la factura" : "la nota de crédito")} electrónica {model.DocumentNumber} (clave de acceso {model.AccessKey})";

        var painter = new Painter(document, model);
        painter.Draw();

        using var output = new MemoryStream();
        document.Save(output, closeStream: false);
        return output.ToArray();
    }

    private static void EnsureFonts()
    {
        lock (FontGate)
        {
            if (_fontsReady)
            {
                return;
            }

            GlobalFontSettings.FontResolver ??= new EmbeddedFontResolver();
            _fontsReady = true;
        }
    }

    private sealed class Painter(PdfDocument document, RideModel model)
    {
        private readonly XFont _normal = new(EmbeddedFontResolver.Family, 8.5, XFontStyleEx.Regular);
        private readonly XFont _small = new(EmbeddedFontResolver.Family, 7.5, XFontStyleEx.Regular);
        private readonly XFont _bold = new(EmbeddedFontResolver.Family, 8.5, XFontStyleEx.Bold);
        private readonly XFont _title = new(EmbeddedFontResolver.Family, 12, XFontStyleEx.Bold);

        private PdfPage _page = null!;
        private XGraphics _gfx = null!;
        private double _y;

        public void Draw()
        {
            NewPage();
            DrawHeader();
            DrawBuyer();
            DrawDetail();
            DrawFooterBlocks();
            _gfx.Dispose();
            DrawPageNumbers();
        }

        private void NewPage()
        {
            _gfx?.Dispose();
            _page = document.AddPage();
            _page.Size = PageSize.A4;
            _gfx = XGraphics.FromPdfPage(_page);
            _y = Margin;
        }

        // ---- cabecera: emisor y datos del SRI ---------------------------------------------------------------------------------

        private void DrawHeader()
        {
            const double gap = 12;
            const double leftWidth = 285;
            var rightX = Margin + leftWidth + gap;
            var rightWidth = ContentWidth - leftWidth - gap;
            var top = _y;

            // Izquierda: logo y recuadro del emisor.
            var logoHeight = 0.0;
            if (model.Logo is { } logo)
            {
                using var image = XImage.FromStream(new MemoryStream(logo.Content));
                var scale = Math.Min(150.0 / logo.Width, 62.0 / logo.Height);
                var width = logo.Width * scale;
                logoHeight = logo.Height * scale;
                _gfx.DrawImage(image, Margin, top, width, logoHeight);
                logoHeight += 8;
            }

            var issuerTop = top + logoHeight;
            var issuerLines = new List<(string Text, XFont Font)> { (model.IssuerLegalName, _bold) };
            if (!string.IsNullOrWhiteSpace(model.IssuerTradeName) && !string.Equals(model.IssuerTradeName, model.IssuerLegalName, StringComparison.OrdinalIgnoreCase))
            {
                issuerLines.Add((model.IssuerTradeName, _normal));
            }

            issuerLines.Add(($"Dirección Matriz: {model.HeadOfficeAddress}", _normal));
            issuerLines.Add(($"Dirección Sucursal: {model.EstablishmentAddress}", _normal));
            if (!string.IsNullOrWhiteSpace(model.SpecialTaxpayerNumber))
            {
                issuerLines.Add(($"Contribuyente Especial Nro: {model.SpecialTaxpayerNumber}", _normal));
            }

            issuerLines.Add(($"OBLIGADO A LLEVAR CONTABILIDAD: {(model.AccountingRequired ? "SI" : "NO")}", _bold));
            if (!string.IsNullOrWhiteSpace(model.WithholdingAgentNumber))
            {
                issuerLines.Add(($"Agente de Retención Resolución No. {model.WithholdingAgentNumber}", _normal));
            }

            if (model.RegimeLegend is not null)
            {
                issuerLines.Add((model.RegimeLegend, _bold));
            }

            var issuerHeight = (2 * Pad) + issuerLines.Sum(l => Wrap(l.Text, l.Font, leftWidth - (2 * Pad)).Count * Line) + (issuerLines.Count * 2);

            // Derecha: datos del SRI.
            var qrSize = 78.0;
            var keyLines = Wrap(model.AccessKey, _small, rightWidth - (2 * Pad));
            var authLines = Wrap(model.AuthorizationNumber, _small, rightWidth - (2 * Pad));
            var rightHeight = (2 * Pad) + 16 + 16 + 14 + 12 + (authLines.Count * 10) + 8 + 24 + 14 + 14 + 12 + qrSize + (keyLines.Count * 10) + 14;

            var height = Math.Max(issuerTop - top + issuerHeight, rightHeight);

            _gfx.DrawRectangle(Border, rightX, top, rightWidth, height);
            _gfx.DrawRectangle(Border, Margin, issuerTop, leftWidth, height - (issuerTop - top));

            var y = issuerTop + Pad;
            foreach (var (text, font) in issuerLines)
            {
                foreach (var wrapped in Wrap(text, font, leftWidth - (2 * Pad)))
                {
                    _gfx.DrawString(wrapped, font, Ink, new XPoint(Margin + Pad, y + (font.Size * 0.85)));
                    y += Line;
                }

                y += 2;
            }

            var x = rightX + Pad;
            var ry = top + Pad;
            Text($"R.U.C.: {model.IssuerRuc}", _title, x, ref ry, 16);
            Text(model.DocumentTitle, _title, x, ref ry, 16);
            Text($"No. {model.DocumentNumber}", _bold, x, ref ry, 14);
            Text("NÚMERO DE AUTORIZACIÓN", _bold, x, ref ry, 12);
            foreach (var l in authLines)
            {
                Text(l, _small, x, ref ry, 10);
            }

            ry += 8;
            Text("FECHA Y HORA DE AUTORIZACIÓN:", _bold, x, ref ry, 12);
            Text(model.AuthorizedAtLocal, _normal, x, ref ry, 12);
            Text($"AMBIENTE: {model.EnvironmentName}", _bold, x, ref ry, 14);
            Text($"EMISIÓN: {model.EmissionName}", _bold, x, ref ry, 14);
            Text("CLAVE DE ACCESO", _bold, x, ref ry, 12);

            DrawQr(model.AccessKey, x, ry, qrSize);

            ry += qrSize + 2;
            foreach (var l in keyLines)
            {
                Text(l, _small, x, ref ry, 10);
            }

            _y = top + height + 10;
        }

        /// <summary>El QR se dibuja como vectores (un cuadrado por módulo): queda nítido a cualquier tamaño y no depende de decodificar imágenes.</summary>
        private void DrawQr(string content, double x, double y, double size)
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.M);
            var matrix = data.ModuleMatrix;
            var cell = size / matrix.Count;
            for (var row = 0; row < matrix.Count; row++)
            {
                for (var col = 0; col < matrix[row].Count; col++)
                {
                    if (matrix[row][col])
                    {
                        _gfx.DrawRectangle(Ink, x + (col * cell), y + (row * cell), cell + 0.1, cell + 0.1);
                    }
                }
            }
        }

        private void Text(string text, XFont font, double x, ref double y, double advance)
        {
            _gfx.DrawString(text, font, Ink, new XPoint(x, y + (font.Size * 0.85)));
            y += advance;
        }

        // ---- comprador -------------------------------------------------------------------------------------------------------------

        private void DrawBuyer()
        {
            var rows = new List<(string Label, string Value)>
            {
                ("Razón Social / Nombres y Apellidos:", model.BuyerName),
                ("Identificación:", model.BuyerIdNumber),
                ("Fecha Emisión:", model.IssueDate)
            };
            if (!string.IsNullOrWhiteSpace(model.BuyerAddress))
            {
                rows.Add(("Dirección:", model.BuyerAddress));
            }

            foreach (var field in model.ModifiedDocument ?? [])
            {
                rows.Add((field.Label, field.Value));
            }

            const double labelWidth = 175;
            var height = (2 * Pad) + rows.Sum(r => Math.Max(1, Wrap(r.Value, _normal, ContentWidth - labelWidth - (2 * Pad)).Count) * Line);
            EnsureSpace(height);
            _gfx.DrawRectangle(Border, Margin, _y, ContentWidth, height);
            var y = _y + Pad;
            foreach (var (label, value) in rows)
            {
                _gfx.DrawString(label, _bold, Ink, new XPoint(Margin + Pad, y + 7));
                foreach (var line in Wrap(value, _normal, ContentWidth - labelWidth - (2 * Pad)))
                {
                    _gfx.DrawString(line, _normal, Ink, new XPoint(Margin + labelWidth, y + 7));
                    y += Line;
                }

                if (string.IsNullOrEmpty(value))
                {
                    y += Line;
                }
            }

            _y += height + 8;
        }

        // ---- detalle ---------------------------------------------------------------------------------------------------------------

        private static readonly (string Title, double Width, bool Right)[] Columns =
        [
            ("Cod. Principal", 78, false), ("Cant.", 42, true), ("Descripción", 185, false), ("Precio Unitario", 78, true), ("Descuento", 66, true), ("Precio Total", 90, true)
        ];

        private void DrawDetail()
        {
            DrawTableHeader();
            foreach (var line in model.Lines)
            {
                var cells = new[]
                {
                    line.MainCode, line.Quantity.ToString("0.##", CultureInfo.InvariantCulture).Replace('.', ','), line.Description,
                    RideModel.Money(line.UnitPrice), RideModel.Money(line.Discount), RideModel.Money(line.Total)
                };
                var lines = cells.Select((c, i) => Wrap(c, _normal, Columns[i].Width - (2 * Pad))).ToList();
                var height = (2 * Pad) + (lines.Max(l => l.Count) * Line);
                if (_y + height > PageHeight - Margin - 30)
                {
                    NewPage();
                    _gfx.DrawString($"{model.DocumentName} {model.DocumentNumber} (continuación)", _bold, Ink, new XPoint(Margin, _y + 8));
                    _y += 16;
                    DrawTableHeader();
                }

                var x = Margin;
                for (var i = 0; i < Columns.Length; i++)
                {
                    _gfx.DrawRectangle(Border, x, _y, Columns[i].Width, height);
                    var ly = _y + Pad;
                    foreach (var text in lines[i])
                    {
                        DrawCell(text, _normal, x, ly, Columns[i].Width, Columns[i].Right);
                        ly += Line;
                    }

                    x += Columns[i].Width;
                }

                _y += height;
            }

            _y += 10;
        }

        private void DrawTableHeader()
        {
            const double height = 24;
            EnsureSpace(height + 30);
            var x = Margin;
            foreach (var (title, width, _) in Columns)
            {
                _gfx.DrawRectangle(Border, HeaderFill, x, _y, width, height);
                var lines = Wrap(title, _bold, width - (2 * Pad));
                var ly = _y + ((height - (lines.Count * 10)) / 2);
                foreach (var l in lines)
                {
                    var w = _gfx.MeasureString(l, _bold).Width;
                    _gfx.DrawString(l, _bold, Ink, new XPoint(x + ((width - w) / 2), ly + 7));
                    ly += 10;
                }

                x += width;
            }

            _y += height;
        }

        private void DrawCell(string text, XFont font, double x, double y, double width, bool right)
        {
            var w = _gfx.MeasureString(text, font).Width;
            var px = right ? x + width - Pad - w : x + Pad;
            _gfx.DrawString(text, font, Ink, new XPoint(px, y + 7));
        }

        // ---- información adicional, forma de pago y totales -----------------------------------------------------------------------

        private void DrawFooterBlocks()
        {
            const double leftWidth = 300;
            const double gap = 12;
            var rightX = Margin + leftWidth + gap;
            var rightWidth = ContentWidth - leftWidth - gap;

            var additionalHeight = model.AdditionalInfo.Count == 0
                ? 0
                : 16 + (2 * Pad) + model.AdditionalInfo.Sum(f => Wrap($"{f.Label}: {f.Value}", _small, leftWidth - (2 * Pad)).Count * 10);
            var paymentHeight = model.Payments.Count == 0 ? 0 : 16 + (model.Payments.Count * 15) + 4;
            var leftHeight = additionalHeight + (additionalHeight > 0 ? 8 : 0) + paymentHeight;
            var rightHeight = model.Totals.Count * 15;
            EnsureSpace(Math.Max(leftHeight, rightHeight) + 4);
            var top = _y;

            // Izquierda.
            var y = top;
            if (additionalHeight > 0)
            {
                _gfx.DrawRectangle(Border, Margin, y, leftWidth, additionalHeight);
                _gfx.DrawRectangle(Border, HeaderFill, Margin, y, leftWidth, 16);
                var tw = _gfx.MeasureString("Información Adicional", _bold).Width;
                _gfx.DrawString("Información Adicional", _bold, Ink, new XPoint(Margin + ((leftWidth - tw) / 2), y + 11));
                var iy = y + 16 + Pad;
                foreach (var field in model.AdditionalInfo)
                {
                    foreach (var line in Wrap($"{field.Label}: {field.Value}", _small, leftWidth - (2 * Pad)))
                    {
                        _gfx.DrawString(line, _small, Ink, new XPoint(Margin + Pad, iy + 6));
                        iy += 10;
                    }
                }

                y += additionalHeight + 8;
            }

            const double valueWidth = 90;
            if (model.Payments.Count > 0)
            {
                _gfx.DrawRectangle(Border, HeaderFill, Margin, y, leftWidth - valueWidth, 16);
                _gfx.DrawRectangle(Border, HeaderFill, Margin + leftWidth - valueWidth, y, valueWidth, 16);
                _gfx.DrawString("Forma de pago", _bold, Ink, new XPoint(Margin + Pad, y + 11));
                _gfx.DrawString("Valor", _bold, Ink, new XPoint(Margin + leftWidth - valueWidth + Pad, y + 11));
                y += 16;
                foreach (var payment in model.Payments)
                {
                    _gfx.DrawRectangle(Border, Margin, y, leftWidth - valueWidth, 15);
                    _gfx.DrawRectangle(Border, Margin + leftWidth - valueWidth, y, valueWidth, 15);
                    _gfx.DrawString(Fit(payment.Label, _small, leftWidth - valueWidth - (2 * Pad)), _small, Ink, new XPoint(Margin + Pad, y + 10));
                    var w = _gfx.MeasureString(payment.Value, _small).Width;
                    _gfx.DrawString(payment.Value, _small, Ink, new XPoint(Margin + leftWidth - Pad - w, y + 10));
                    y += 15;
                }
            }

            // Derecha: totales por tarifa.
            var ty = top;
            const double amountWidth = 90;
            foreach (var total in model.Totals)
            {
                var font = total.Emphasis ? _bold : _small;
                if (total.Emphasis)
                {
                    _gfx.DrawRectangle(Border, HeaderFill, rightX, ty, rightWidth - amountWidth, 15);
                    _gfx.DrawRectangle(Border, HeaderFill, rightX + rightWidth - amountWidth, ty, amountWidth, 15);
                }
                else
                {
                    _gfx.DrawRectangle(Border, rightX, ty, rightWidth - amountWidth, 15);
                    _gfx.DrawRectangle(Border, rightX + rightWidth - amountWidth, ty, amountWidth, 15);
                }

                _gfx.DrawString(total.Label, font, Ink, new XPoint(rightX + Pad, ty + 10));
                var amount = RideModel.Money(total.Amount);
                var aw = _gfx.MeasureString(amount, font).Width;
                _gfx.DrawString(amount, font, Ink, new XPoint(rightX + rightWidth - Pad - aw, ty + 10));
                ty += 15;
            }

            _y = Math.Max(y, ty) + 8;
        }

        private void DrawPageNumbers()
        {
            var total = document.PageCount;
            for (var i = 0; i < total; i++)
            {
                using var gfx = XGraphics.FromPdfPage(document.Pages[i], XGraphicsPdfPageOptions.Append);
                var text = $"{model.DocumentName} {model.DocumentNumber} · Representación impresa (RIDE) generada por FIT360 · Página {i + 1} de {total}";
                var w = gfx.MeasureString(text, _small).Width;
                gfx.DrawString(text, _small, Muted, new XPoint((PageWidth - w) / 2, PageHeight - 16));
            }
        }

        // ---- utilidades ------------------------------------------------------------------------------------------------------------

        private void EnsureSpace(double height)
        {
            if (_y + height > PageHeight - Margin - 20)
            {
                NewPage();
            }
        }

        /// <summary>Parte el texto en líneas que caben en el ancho; una «palabra» sin espacios más larga que el ancho (como una clave de acceso) se corta.</summary>
        private List<string> Wrap(string text, XFont font, double width)
        {
            var lines = new List<string>();
            if (string.IsNullOrEmpty(text))
            {
                return [string.Empty];
            }

            var current = string.Empty;
            foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = current.Length == 0 ? word : $"{current} {word}";
                if (_gfx.MeasureString(candidate, font).Width <= width)
                {
                    current = candidate;
                    continue;
                }

                if (current.Length > 0)
                {
                    lines.Add(current);
                    current = string.Empty;
                }

                var rest = word;
                while (_gfx.MeasureString(rest, font).Width > width && rest.Length > 1)
                {
                    var cut = rest.Length - 1;
                    while (cut > 1 && _gfx.MeasureString(rest[..cut], font).Width > width)
                    {
                        cut--;
                    }

                    lines.Add(rest[..cut]);
                    rest = rest[cut..];
                }

                current = rest;
            }

            if (current.Length > 0)
            {
                lines.Add(current);
            }

            return lines.Count == 0 ? [string.Empty] : lines;
        }

        private string Fit(string text, XFont font, double width)
        {
            if (_gfx.MeasureString(text, font).Width <= width)
            {
                return text;
            }

            var cut = text.Length;
            while (cut > 1 && _gfx.MeasureString(text[..cut] + "…", font).Width > width)
            {
                cut--;
            }

            return text[..cut] + "…";
        }
    }

}
