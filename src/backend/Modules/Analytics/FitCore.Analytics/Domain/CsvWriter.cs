using System.Globalization;
using System.Text;

namespace FitCore.Analytics.Domain;

/// <summary>
/// Escribe CSV seguro. El texto que escribió un usuario (nombres, conceptos, motivos, clases) puede empezar con «=», «+», «-» o «@» y una hoja de
/// cálculo lo ejecutaría como fórmula: aquí se neutraliza con un apóstrofo delante. Los números no se tocan (un faltante de −5,00 sigue siendo un número).
/// </summary>
public static class CsvWriter
{
    private static readonly char[] FormulaStarts = ['=', '+', '-', '@', '\t', '\r', '\n'];

    /// <summary>Texto citado y sin riesgo de fórmula. Las comillas dobles se duplican; los saltos de línea quedan dentro de las comillas.</summary>
    public static string Text(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "\"\"";
        }

        var probe = value.TrimStart(' ');
        var safe = probe.Length > 0 && Array.IndexOf(FormulaStarts, probe[0]) >= 0 ? "'" + value : value;
        return "\"" + safe.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    public static string Number(decimal? value) => value?.ToString("0.00", CultureInfo.InvariantCulture) ?? string.Empty;

    public static string Percent(decimal? value) => value?.ToString("0.0", CultureInfo.InvariantCulture) ?? string.Empty;

    public static string Count(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    public static string Bool(bool value) => value ? "Sí" : "No";

    /// <summary>Fecha y hora en la zona local del reporte (no en UTC), sin segundos.</summary>
    public static string LocalDateTime(DateTimeOffset? instant, string timeZoneId) =>
        instant is { } value
            ? TimeZoneInfo.ConvertTime(value, TimeZoneInfo.FindSystemTimeZoneById(timeZoneId)).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            : string.Empty;

    public static string Date(DateOnly? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>El archivo completo en UTF-8 con marca de orden de bytes (para que Excel muestre bien los acentos) y fin de línea CRLF.</summary>
    public static byte[] Build(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows)
    {
        var builder = new StringBuilder();
        builder.Append(string.Join(',', headers.Select(Text))).Append("\r\n");
        foreach (var row in rows)
        {
            builder.Append(string.Join(',', row)).Append("\r\n");
        }

        var preamble = Encoding.UTF8.GetPreamble();
        var body = Encoding.UTF8.GetBytes(builder.ToString());
        var bytes = new byte[preamble.Length + body.Length];
        preamble.CopyTo(bytes, 0);
        body.CopyTo(bytes, preamble.Length);
        return bytes;
    }
}
