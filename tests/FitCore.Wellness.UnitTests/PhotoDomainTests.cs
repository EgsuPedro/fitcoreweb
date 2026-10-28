using System.Text;
using System.Text.RegularExpressions;
using FitCore.Wellness.Domain;

namespace FitCore.Wellness.UnitTests;

public class PhotoDomainTests
{
    private static readonly byte[] Secret = Encoding.ASCII.GetBytes("GPS-0.123,-78.456-SECRETO");

    private static byte[] Segment(byte marker, byte[] payload) =>
        [0xFF, marker, (byte)((payload.Length + 2) >> 8), (byte)((payload.Length + 2) & 0xFF), .. payload];

    private static readonly byte[] Scan = [0x01, 0x02, 0x03, 0x00, 0x11, 0x22];

    /// <summary>JPEG mínimo: JFIF, EXIF con un secreto, ICC, comentario, un barrido de datos y, tras el final, un archivo añadido.</summary>
    private static byte[] Jpeg(bool withExif = true, bool withTrailer = true, bool progressive = false)
    {
        var bytes = new List<byte> { 0xFF, 0xD8 };
        bytes.AddRange(Segment(0xE0, "JFIF\0"u8.ToArray()));
        if (withExif)
        {
            bytes.AddRange(Segment(0xE1, [.. "Exif\0\0"u8, .. Secret]));
            bytes.AddRange(Segment(0xED, [.. "Photoshop 3.0\0"u8, .. Secret]));
            bytes.AddRange(Segment(0xFE, Secret));
            bytes.AddRange(Segment(0xE2, [.. "MPF\0"u8, .. Secret]));
        }

        bytes.AddRange(Segment(0xE2, [.. "ICC_PROFILE\0"u8, 0x01, 0x01]));
        bytes.AddRange(Segment(0xC0, [0x08, 0x00, 0x10, 0x00, 0x10, 0x01, 0x01, 0x11, 0x00]));
        bytes.AddRange(Segment(0xDA, [0x01, 0x01, 0x00, 0x00, 0x3F, 0x00]));
        bytes.AddRange(Scan);
        bytes.AddRange([0xFF, 0x00, 0x7A]); // FF 00 dentro de los datos comprimidos no es un marcador
        bytes.AddRange([0xFF, 0xD3, 0x55]); // reinicio RSTn tampoco
        if (progressive)
        {
            bytes.AddRange(Segment(0xC4, [0x00, 0x01]));
            bytes.AddRange(Segment(0xDA, [0x01, 0x01, 0x00, 0x00, 0x3F, 0x00]));
            bytes.AddRange([0x09, 0x08]);
        }

        bytes.AddRange([0xFF, 0xD9]);
        if (withTrailer)
        {
            bytes.AddRange(Secret);
        }

        return [.. bytes];
    }

    private static byte[] PngChunk(string type, byte[] data) =>
        [(byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length, .. Encoding.ASCII.GetBytes(type), .. data, 0, 0, 0, 0];

    private static byte[] Png(bool withMetadata = true, bool withTrailer = true)
    {
        var bytes = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        bytes.AddRange(PngChunk("IHDR", new byte[13]));
        bytes.AddRange(PngChunk("sRGB", [0]));
        if (withMetadata)
        {
            bytes.AddRange(PngChunk("tEXt", [.. "Comment\0"u8, .. Secret]));
            bytes.AddRange(PngChunk("eXIf", Secret));
            bytes.AddRange(PngChunk("tIME", new byte[7]));
            bytes.AddRange(PngChunk("iTXt", Secret));
        }

        bytes.AddRange(PngChunk("IDAT", [1, 2, 3, 4]));
        bytes.AddRange(PngChunk("IEND", []));
        if (withTrailer)
        {
            bytes.AddRange(Secret);
        }

        return [.. bytes];
    }

    private static bool Contains(byte[] data, byte[] needle) => data.AsSpan().IndexOf(needle) >= 0;

    // ---- Tipo de archivo ----

    [Fact]
    public void El_tipo_se_decide_por_los_primeros_bytes()
    {
        Assert.Equal(PhotoRules.Jpeg, PhotoRules.DetectContentType(Jpeg()));
        Assert.Equal(PhotoRules.Png, PhotoRules.DetectContentType(Png()));
        Assert.Null(PhotoRules.DetectContentType("GIF89a......"u8));
        Assert.Null(PhotoRules.DetectContentType("RIFF....WEBPVP8 "u8));
        Assert.Null(PhotoRules.DetectContentType("<html><script>alert(1)</script>"u8));
        Assert.Null(PhotoRules.DetectContentType("%PDF-1.7"u8));
        Assert.Null(PhotoRules.DetectContentType([0xFF, 0xD8]));
        Assert.Null(PhotoRules.DetectContentType([]));
    }

    // ---- JPEG ----

    [Fact]
    public void En_un_jpeg_se_quitan_exif_comentarios_y_lo_añadido_al_final_y_la_imagen_queda_igual()
    {
        var clean = PhotoRules.StripMetadata(Jpeg(), PhotoRules.Jpeg);

        Assert.NotNull(clean);
        Assert.False(Contains(clean, Secret));
        Assert.False(Contains(clean, "Exif"u8.ToArray()));
        Assert.False(Contains(clean, "MPF"u8.ToArray()));
        // Lo que dibuja la imagen se conserva: JFIF, perfil de color, datos comprimidos (con FF 00 y RSTn) y el final.
        Assert.True(Contains(clean, "JFIF"u8.ToArray()));
        Assert.True(Contains(clean, "ICC_PROFILE"u8.ToArray()));
        Assert.True(Contains(clean, [.. Scan, 0xFF, 0x00, 0x7A, 0xFF, 0xD3, 0x55, 0xFF, 0xD9]));
        Assert.Equal([0xFF, 0xD9], clean[^2..]);
        Assert.Equal(PhotoRules.Jpeg, PhotoRules.DetectContentType(clean));
    }

    [Fact]
    public void Un_jpeg_sin_metadatos_queda_igual()
    {
        var original = Jpeg(withExif: false, withTrailer: false);

        Assert.Equal(original, PhotoRules.StripMetadata(original, PhotoRules.Jpeg));
    }

    [Fact]
    public void Un_jpeg_progresivo_con_varios_barridos_se_procesa_completo()
    {
        var clean = PhotoRules.StripMetadata(Jpeg(progressive: true), PhotoRules.Jpeg);

        Assert.NotNull(clean);
        Assert.True(Contains(clean, [0x09, 0x08, 0xFF, 0xD9]));
        Assert.False(Contains(clean, Secret));
    }

    [Fact]
    public void Un_jpeg_cortado_mal_formado_o_sin_barrido_se_rechaza()
    {
        var full = Jpeg();

        Assert.Null(PhotoRules.StripMetadata(full[..40], PhotoRules.Jpeg));
        Assert.Null(PhotoRules.StripMetadata([0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x02, 0xFF, 0xD9], PhotoRules.Jpeg)); // sin barrido de datos
        Assert.Null(PhotoRules.StripMetadata([0xFF, 0xD8, 0xFF, 0xE1, 0xFF, 0xFF, 0x00], PhotoRules.Jpeg)); // longitud fuera del archivo
        Assert.Null(PhotoRules.StripMetadata([0xFF, 0xD8, 0x00, 0x01], PhotoRules.Jpeg));
        Assert.Null(PhotoRules.StripMetadata(Jpeg(withTrailer: false)[..^2], PhotoRules.Jpeg)); // sin marcador de fin tras los datos
    }

    // ---- PNG ----

    [Fact]
    public void En_un_png_se_quitan_texto_exif_fecha_y_lo_añadido_al_final()
    {
        var clean = PhotoRules.StripMetadata(Png(), PhotoRules.Png);

        Assert.NotNull(clean);
        Assert.False(Contains(clean, Secret));
        foreach (var type in new[] { "tEXt", "eXIf", "tIME", "iTXt" })
        {
            Assert.False(Contains(clean, Encoding.ASCII.GetBytes(type)), type);
        }

        foreach (var type in new[] { "IHDR", "sRGB", "IDAT", "IEND" })
        {
            Assert.True(Contains(clean, Encoding.ASCII.GetBytes(type)), type);
        }

        Assert.Equal(PngChunk("IEND", []), clean[^12..]);
        Assert.Equal(Png(withMetadata: false, withTrailer: false), clean);
    }

    [Fact]
    public void Un_png_cortado_o_que_no_empieza_por_ihdr_se_rechaza()
    {
        var full = Png();

        Assert.Null(PhotoRules.StripMetadata(full[..30], PhotoRules.Png));
        Assert.Null(PhotoRules.StripMetadata([.. full[..8], .. PngChunk("IDAT", [1, 2])], PhotoRules.Png));
        Assert.Null(PhotoRules.StripMetadata([.. full[..8], .. PngChunk("IHDR", [1, 2])], PhotoRules.Png)); // IHDR de longitud errónea
        Assert.Null(PhotoRules.StripMetadata(Png(withMetadata: false, withTrailer: false)[..^12], PhotoRules.Png)); // sin IEND
    }

    [Fact]
    public void Un_tipo_no_admitido_no_se_procesa()
    {
        Assert.Null(PhotoRules.StripMetadata(Jpeg(), "image/webp"));
    }

    // ---- Reglas ----

    [Fact]
    public void La_fecha_de_la_foto_no_es_futura_ni_de_hace_mas_de_diez_anos()
    {
        var today = new DateOnly(2026, 6, 15);

        Assert.True(PhotoRules.IsDateValid(today, today));
        Assert.True(PhotoRules.IsDateValid(today.AddYears(-10), today));
        Assert.False(PhotoRules.IsDateValid(today.AddDays(1), today));
        Assert.False(PhotoRules.IsDateValid(today.AddYears(-10).AddDays(-1), today));
    }

    [Theory]
    [InlineData(17, true)]
    [InlineData(0, true)]
    [InlineData(18, false)]
    [InlineData(40, false)]
    [InlineData(null, false)]
    public void Es_menor_solo_si_se_conoce_la_edad_y_es_menor_de_18(int? age, bool minor) =>
        Assert.Equal(minor, PhotoRules.IsMinor(age));

    [Fact]
    public void La_clave_de_almacenamiento_es_aleatoria_y_no_contiene_datos_de_la_persona()
    {
        var tenant = Guid.NewGuid();
        var keys = Enumerable.Range(0, 50).Select(_ => PhotoRules.NewStorageKey(tenant)).ToList();

        Assert.Equal(50, keys.Distinct().Count());
        Assert.All(keys, key => Assert.Matches(new Regex($"^progress-photos/{tenant:N}/[0-9a-f]{{32}}$"), key));
    }

    [Fact]
    public void El_consentimiento_limpia_el_nombre_del_representante_y_un_retiro_no_lo_lleva()
    {
        var tenant = Guid.NewGuid();
        var member = Guid.NewGuid();
        var user = Guid.NewGuid();

        var grant = PhotoConsent.Grant(tenant, member, PhotoConsentSource.Staff, "  Rosa Pérez  ", user, DateTimeOffset.UnixEpoch);
        var blank = PhotoConsent.Grant(tenant, member, PhotoConsentSource.Member, "   ", user, DateTimeOffset.UnixEpoch);
        var revoke = PhotoConsent.Revoke(tenant, member, PhotoConsentSource.Member, user, DateTimeOffset.UnixEpoch);

        Assert.Equal(("Rosa Pérez", PhotoConsentAction.Granted, PhotoConsent.CurrentNoticeVersion), (grant.GuardianName, grant.Action, grant.NoticeVersion));
        Assert.Null(blank.GuardianName);
        Assert.Equal(PhotoConsentAction.Revoked, revoke.Action);
        Assert.Null(revoke.GuardianName);
    }
}
