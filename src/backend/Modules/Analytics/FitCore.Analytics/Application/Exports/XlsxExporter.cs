using System.Globalization;
using ClosedXML.Excel;
using ClosedXML.Excel.Drawings;
using FitCore.Analytics.Domain;
using FitCore.Application.Core;

namespace FitCore.Analytics.Application.Exports;

/// <summary>
/// Arma el libro de Excel de un reporte: logo y nombre del gimnasio, título, período y fecha de generación; tabla con encabezado de la marca, filtros,
/// primera fila fija, anchos legibles y números, fechas y textos con su tipo real (se pueden ordenar, filtrar y sumar). Los textos escritos por usuarios
/// se guardan siempre como <b>texto</b>, nunca como fórmula: no hay que neutralizarlos como en CSV.
/// </summary>
internal static class XlsxExporter
{
    private const string Teal = "#0F766E";
    private const string TealSoft = "#E2F4F1";
    private const string Border = "#D5DEE5";
    private const int MaxColumnWidth = 48;
    private const int LogoHeightPixels = 64;
    private const int LogoMaxWidthPixels = 240;

    public static byte[] Build(CsvTable table, TenantBrand brand, string title, string periodText, DateTimeOffset generatedAt)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add(SheetName(title));
        sheet.ShowGridLines = false;

        var row = 1;
        if (brand.Logo is { } logo)
        {
            AddLogo(sheet, logo);
            row = 5; // el logo ocupa las filas 1 a 4
        }

        sheet.Cell(row, 1).Value = AsText(brand.Name);
        sheet.Cell(row, 1).Style.Font.SetBold().Font.SetFontSize(15).Font.SetFontColor(XLColor.FromHtml(Teal));
        row++;
        sheet.Cell(row, 1).Value = AsText(title);
        sheet.Cell(row, 1).Style.Font.SetBold().Font.SetFontSize(12);
        row++;
        var local = TimeZoneInfo.ConvertTime(generatedAt, ResolveZone(brand.TimeZone));
        sheet.Cell(row, 1).Value = AsText($"Período: {periodText}  ·  Generado: {local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} ({brand.TimeZone})");
        sheet.Cell(row, 1).Style.Font.SetFontColor(XLColor.FromHtml("#5D6B7A"));
        row += 2;

        var headerRow = row;
        var columns = table.Headers.Count;
        for (var c = 0; c < columns; c++)
        {
            var cell = sheet.Cell(headerRow, c + 1);
            cell.Value = AsText(table.Headers[c]);
            cell.Style.Font.SetBold().Font.SetFontColor(XLColor.White);
            cell.Style.Fill.SetBackgroundColor(XLColor.FromHtml(Teal));
            cell.Style.Alignment.SetVertical(XLAlignmentVerticalValues.Center).Alignment.SetWrapText(true);
        }

        sheet.Row(headerRow).Height = 26;
        var widths = table.Headers.Select(h => h.Length + 2).ToArray();

        for (var r = 0; r < table.Rows.Count; r++)
        {
            var source = table.Rows[r];
            var excelRow = headerRow + 1 + r;
            for (var c = 0; c < columns && c < source.Count; c++)
            {
                var cell = sheet.Cell(excelRow, c + 1);
                var width = Fill(cell, source[c]);
                widths[c] = Math.Max(widths[c], width);
            }

            if (r % 2 == 1)
            {
                sheet.Range(excelRow, 1, excelRow, columns).Style.Fill.SetBackgroundColor(XLColor.FromHtml("#F8FAFC"));
            }
        }

        if (columns > 0)
        {
            var lastRow = headerRow + Math.Max(1, table.Rows.Count);
            var body = sheet.Range(headerRow, 1, lastRow, columns);
            body.Style.Border.SetInsideBorder(XLBorderStyleValues.Thin).Border.SetInsideBorderColor(XLColor.FromHtml(Border));
            body.Style.Border.SetOutsideBorder(XLBorderStyleValues.Thin).Border.SetOutsideBorderColor(XLColor.FromHtml(Border));
            sheet.Range(headerRow, 1, lastRow, columns).SetAutoFilter();
            for (var c = 0; c < columns; c++)
            {
                // Anchos por el largo del contenido (no se miden fuentes del sistema, que en un contenedor pueden no existir).
                sheet.Column(c + 1).Width = Math.Clamp(widths[c] * 1.1 + 2, 10, MaxColumnWidth);
            }
        }

        if (table.Rows.Count == 0 && columns > 0)
        {
            sheet.Cell(headerRow + 1, 1).Value = AsText("Sin datos para los filtros elegidos.");
            sheet.Cell(headerRow + 1, 1).Style.Font.SetItalic().Font.SetFontColor(XLColor.FromHtml("#5D6B7A"));
        }

        sheet.SheetView.FreezeRows(headerRow);
        sheet.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        sheet.PageSetup.FitToPages(1, 0);
        sheet.PageSetup.SetRowsToRepeatAtTop(headerRow, headerRow);
        sheet.PageSetup.Footer.Center.AddText("Página &P de &N");

        workbook.Properties.Title = title;
        workbook.Properties.Author = brand.Name;
        workbook.Properties.Company = brand.Name;

        using var output = new MemoryStream();
        workbook.SaveAs(output);
        return output.ToArray();
    }

    /// <summary>Siempre texto: una cadena como «=1+1» o «007» se guarda tal cual, sin interpretarse como fórmula, número ni fecha.</summary>
    private static XLCellValue AsText(string text) => text;

    private static void AddLogo(IXLWorksheet sheet, TenantLogoFile logo)
    {
        var height = LogoHeightPixels;
        var width = Math.Max(1, (int)Math.Round(height * (double)logo.Width / logo.Height));
        if (width > LogoMaxWidthPixels)
        {
            width = LogoMaxWidthPixels;
            height = Math.Max(1, (int)Math.Round(width * (double)logo.Height / logo.Width));
        }

        using var stream = new MemoryStream(logo.Content);
        var format = logo.ContentType == "image/png" ? XLPictureFormat.Png : XLPictureFormat.Jpeg;
        sheet.AddPicture(stream, format, "Logo").MoveTo(sheet.Cell(1, 1), 4, 4).WithSize(width, height);
        for (var r = 1; r <= 4; r++)
        {
            sheet.Row(r).Height = 18;
        }
    }

    /// <summary>
    /// Escribe la celda con su tipo real a partir del texto ya formateado para el CSV: «"…"» es texto, «aaaa-mm-dd[ hh:mm]» entre comillas es fecha,
    /// «12.50» o «3» sin comillas es número y vacío es vacío. Devuelve el largo aproximado para el ancho de la columna.
    /// </summary>
    internal static int Fill(IXLCell cell, string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return 0;
        }

        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
        {
            var text = raw[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal);
            // Quita el apóstrofo que el CSV antepone para neutralizar fórmulas: en Excel el texto ya es texto.
            if (text.Length > 1 && text[0] == '\'' && "=+-@\t\r\n".Contains(text.TrimStart('\'').FirstOrDefault()))
            {
                text = text[1..];
            }

            if (TryDate(text, out var date, out var hasTime))
            {
                cell.Value = date;
                cell.Style.DateFormat.Format = hasTime ? "yyyy-mm-dd hh:mm" : "yyyy-mm-dd";
                cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
                return hasTime ? 16 : 10;
            }

            cell.Value = AsText(text);
            if (text.Contains('\n', StringComparison.Ordinal))
            {
                cell.Style.Alignment.WrapText = true;
            }

            return text.Split('\n').Max(l => l.Length);
        }

        if (decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
        {
            cell.Value = number;
            cell.Style.NumberFormat.Format = raw.Contains('.', StringComparison.Ordinal) ? "#,##0.00" : "0";
            return raw.Length + 2;
        }

        cell.Value = AsText(raw);
        return raw.Length;
    }

    private static bool TryDate(string text, out DateTime date, out bool hasTime)
    {
        hasTime = text.Length == 16;
        if (text.Length is 10 or 16
            && DateTime.TryParseExact(text, hasTime ? "yyyy-MM-dd HH:mm" : "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
        {
            return true;
        }

        date = default;
        return false;
    }

    private static TimeZoneInfo ResolveZone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    /// <summary>Excel limita el nombre de la hoja a 31 caracteres y prohíbe : \ / ? * [ ].</summary>
    private static string SheetName(string title)
    {
        var clean = new string(title.Where(c => !":\\/?*[]".Contains(c)).ToArray()).Trim();
        return clean.Length == 0 ? "Reporte" : clean[..Math.Min(31, clean.Length)];
    }
}
