using System.Text;
using FitCore.Analytics.Domain;

namespace FitCore.Analytics.UnitTests;

public class CsvWriterTests
{
    [Theory]
    [InlineData("=1+1", "\"'=1+1\"")]
    [InlineData("+cmd|' /C calc'!A0", "\"'+cmd|' /C calc'!A0\"")]
    [InlineData("-2+3", "\"'-2+3\"")]
    [InlineData("@SUM(A1:A9)", "\"'@SUM(A1:A9)\"")]
    [InlineData("\t=HYPERLINK(\"http://x\")", "\"'\t=HYPERLINK(\"\"http://x\"\")\"")]
    [InlineData("\r=1", "\"'\r=1\"")]
    [InlineData("   =1+1", "\"'   =1+1\"")]
    public void El_texto_que_podria_ejecutarse_como_formula_se_neutraliza(string input, string expected) =>
        Assert.Equal(expected, CsvWriter.Text(input));

    [Theory]
    [InlineData("Camila Pérez", "\"Camila Pérez\"")]
    [InlineData("Plan 3-meses", "\"Plan 3-meses\"")]
    [InlineData("a, b", "\"a, b\"")]
    [InlineData("dijo \"hola\"", "\"dijo \"\"hola\"\"\"")]
    [InlineData("línea 1\nlínea 2", "\"línea 1\nlínea 2\"")]
    [InlineData("", "\"\"")]
    [InlineData(null, "\"\"")]
    public void El_texto_normal_se_cita_y_las_comillas_se_duplican(string? input, string expected) =>
        Assert.Equal(expected, CsvWriter.Text(input));

    [Fact]
    public void Los_numeros_no_se_neutralizan_ni_se_citan_y_usan_punto_decimal()
    {
        Assert.Equal("-5.00", CsvWriter.Number(-5m));
        Assert.Equal("1234.57", CsvWriter.Number(1234.567m));
        Assert.Equal("0.63", CsvWriter.Number(0.63m));
        Assert.Equal(string.Empty, CsvWriter.Number(null));
        Assert.Equal("66.7", CsvWriter.Percent(66.7m));
        Assert.Equal("-50.0", CsvWriter.Percent(-50m));
        Assert.Equal("12", CsvWriter.Count(12));
        Assert.Equal(string.Empty, CsvWriter.Count(null));
    }

    [Fact]
    public void Las_fechas_se_escriben_en_la_hora_local_y_no_en_UTC()
    {
        // 02:30 UTC del 1 de marzo son las 21:30 del 28 de febrero en Quito (UTC−5).
        var instant = DateTimeOffset.Parse("2026-03-01T02:30:00Z");
        Assert.Equal("2026-02-28 21:30", CsvWriter.LocalDateTime(instant, "America/Guayaquil"));
        Assert.Equal("2026-02-28 20:30", CsvWriter.LocalDateTime(instant, "Pacific/Galapagos"));
        Assert.Equal(string.Empty, CsvWriter.LocalDateTime(null, "America/Guayaquil"));
        Assert.Equal("2026-03-01", CsvWriter.Date(new DateOnly(2026, 3, 1)));
    }

    [Fact]
    public void El_archivo_es_UTF8_con_marca_de_orden_y_fin_de_linea_CRLF()
    {
        var bytes = CsvWriter.Build(["Alumno", "Importe (USD)"], [[CsvWriter.Text("María =x"), CsvWriter.Number(-5m)], [CsvWriter.Text("Luis"), CsvWriter.Number(30m)]]);

        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        var text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        Assert.Equal("\"Alumno\",\"Importe (USD)\"\r\n\"María =x\",-5.00\r\n\"Luis\",30.00\r\n", text);
        Assert.Equal(3, text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void Un_nombre_malicioso_en_un_archivo_completo_queda_inerte()
    {
        var bytes = CsvWriter.Build(["Alumno"], [[CsvWriter.Text("=HYPERLINK(\"http://malo\",\"clic\")")]]);
        var text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);

        Assert.Contains("\"'=HYPERLINK(", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\n\"=", text, StringComparison.Ordinal);
    }
}
