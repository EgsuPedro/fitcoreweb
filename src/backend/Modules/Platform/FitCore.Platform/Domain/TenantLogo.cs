using System.Security.Cryptography;
using FitCore.SharedKernel;

namespace FitCore.Platform.Domain;

/// <summary>
/// Logo de un gimnasio, para sus reportes, exportaciones y —más adelante— sus comprobantes. Es la <b>constancia</b> del archivo guardado en el almacén privado
/// (el archivo no vive en la base). Uno por gimnasio; cambiarlo guarda un archivo nuevo con otra clave y borra el anterior.
/// </summary>
public sealed class TenantLogo : ITenantOwned
{
    public Guid TenantId { get; init; }
    public string StorageKey { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public int SizeBytes { get; set; }

    /// <summary>SHA-256 del archivo ya limpiado: sirve de versión (ETag) para que el navegador lo reutilice sin pedirlo de nuevo.</summary>
    public string Sha256 { get; set; } = string.Empty;

    public int Width { get; set; }
    public int Height { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }
}

/// <summary>Reglas del archivo de logo: solo PNG o JPEG (nunca SVG: puede llevar scripts), tamaño y dimensiones acotados y sin metadatos.</summary>
public static class LogoImage
{
    public const int MaxBytes = 1_048_576;
    public const int MinSide = 32;
    public const int MaxSide = 2000;
    public const string Png = "image/png";
    public const string Jpeg = "image/jpeg";

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    // Chunks PNG que afectan cómo se dibuja la imagen; el resto (texto, fecha, EXIF) se descarta.
    private static readonly HashSet<string> PngKeep = ["IHDR", "PLTE", "IDAT", "IEND", "tRNS", "gAMA", "cHRM", "sRGB", "iCCP", "sBIT", "pHYs", "bKGD"];

    public static readonly Error Required = Error.Validation("branding.logo_required", "Elija el archivo del logo.");
    public static readonly Error TooLarge = Error.Validation("branding.logo_too_large", "El logo no puede pesar más de 1 MB.");
    public static readonly Error InvalidType = Error.Validation("branding.logo_invalid_type", "El logo debe ser una imagen PNG o JPG válida.");
    public static readonly Error InvalidSize = Error.Validation("branding.logo_invalid_size", $"El logo debe medir entre {MinSide} y {MaxSide} píxeles por lado.");
    public static readonly Error NotFound = Error.NotFound("branding.logo_not_found", "El gimnasio no tiene logo.");

    public sealed record Prepared(byte[] Content, string ContentType, int Width, int Height, string Sha256);

    /// <summary>La clave aleatoria e impredecible del archivo en el almacén privado.</summary>
    public static string NewStorageKey(Guid tenantId) => $"branding/{tenantId:N}/{RandomNumberGenerator.GetHexString(32, lowercase: true)}";

    /// <summary>
    /// Valida y limpia el archivo recibido: el tipo se decide por sus primeros bytes (nunca por lo que declare el cliente), se quitan los metadatos
    /// (ubicación GPS, fecha y equipo, texto) sin volver a codificar los píxeles y se leen sus dimensiones.
    /// </summary>
    public static Result<Prepared> Prepare(byte[] data)
    {
        if (data.Length == 0)
        {
            return Required;
        }

        if (data.Length > MaxBytes)
        {
            return TooLarge;
        }

        var type = DetectContentType(data);
        var clean = type switch
        {
            Jpeg => StripJpeg(data),
            Png => StripPng(data),
            _ => null
        };
        if (type is null || clean is null)
        {
            return InvalidType;
        }

        var size = type == Png ? PngSize(clean) : JpegSize(clean);
        if (size is not { } wh || wh.Width < MinSide || wh.Height < MinSide || wh.Width > MaxSide || wh.Height > MaxSide)
        {
            return size is null ? InvalidType : InvalidSize;
        }

        return new Prepared(clean, type, wh.Width, wh.Height, Convert.ToHexString(SHA256.HashData(clean)).ToLowerInvariant());
    }

    public static string? DetectContentType(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            return Jpeg;
        }

        return data.Length >= PngSignature.Length && data[..PngSignature.Length].SequenceEqual(PngSignature) ? Png : null;
    }

    private static (int Width, int Height)? PngSize(byte[] data) =>
        data.Length >= 24
            ? ((data[16] << 24) | (data[17] << 16) | (data[18] << 8) | data[19], (data[20] << 24) | (data[21] << 16) | (data[22] << 8) | data[23])
            : null;

    private static (int Width, int Height)? JpegSize(byte[] data)
    {
        var pos = 2;
        while (pos + 9 < data.Length)
        {
            if (data[pos] != 0xFF)
            {
                return null;
            }

            var marker = data[pos + 1];
            if (marker is 0x01 or (>= 0xD0 and <= 0xD8) or 0xFF)
            {
                pos += marker == 0xFF ? 1 : 2;
                continue;
            }

            var length = (data[pos + 2] << 8) | data[pos + 3];
            if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC))
            {
                return ((data[pos + 7] << 8) | data[pos + 8], (data[pos + 5] << 8) | data[pos + 6]);
            }

            pos += 2 + length;
        }

        return null;
    }

    private static byte[]? StripJpeg(byte[] data)
    {
        using var output = new MemoryStream(data.Length);
        output.Write(data, 0, 2); // FF D8
        var pos = 2;
        var sawScan = false;
        while (pos < data.Length)
        {
            if (data[pos] != 0xFF)
            {
                return null;
            }

            while (pos < data.Length && data[pos] == 0xFF)
            {
                pos++;
            }

            if (pos >= data.Length)
            {
                return null;
            }

            var marker = data[pos++];
            if (marker == 0xD9)
            {
                output.Write([0xFF, 0xD9]);
                return sawScan ? output.ToArray() : null;
            }

            if (marker is 0x01 or (>= 0xD0 and <= 0xD8))
            {
                output.Write([0xFF, marker]);
                continue;
            }

            if (pos + 2 > data.Length)
            {
                return null;
            }

            var length = (data[pos] << 8) | data[pos + 1];
            if (length < 2 || pos + length > data.Length)
            {
                return null;
            }

            if (KeepJpegSegment(marker, data.AsSpan(pos + 2, length - 2)))
            {
                output.Write([0xFF, marker]);
                output.Write(data, pos, length);
            }

            pos += length;
            if (marker != 0xDA)
            {
                continue;
            }

            sawScan = true;
            var start = pos;
            while (pos + 1 < data.Length && !(data[pos] == 0xFF && data[pos + 1] != 0x00 && data[pos + 1] != 0xFF && !(data[pos + 1] is >= 0xD0 and <= 0xD7)))
            {
                pos++;
            }

            if (pos + 1 >= data.Length)
            {
                return null;
            }

            output.Write(data, start, pos - start);
        }

        return null;
    }

    /// <summary>Se conservan los segmentos que la imagen necesita para dibujarse (JFIF, perfil de color ICC, Adobe) y se descartan EXIF/XMP, IPTC y comentarios.</summary>
    private static bool KeepJpegSegment(byte marker, ReadOnlySpan<byte> payload) =>
        marker == 0xE2 ? payload.StartsWith("ICC_PROFILE\0"u8)
        : marker == 0xEE || marker is not ((>= 0xE1 and <= 0xEF) or 0xFE);

    private static byte[]? StripPng(byte[] data)
    {
        using var output = new MemoryStream(data.Length);
        output.Write(data, 0, PngSignature.Length);
        var pos = PngSignature.Length;
        var first = true;
        while (pos + 12 <= data.Length)
        {
            var length = (data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3];
            if (length < 0 || pos + 12L + length > data.Length)
            {
                return null;
            }

            var type = System.Text.Encoding.ASCII.GetString(data, pos + 4, 4);
            if (first && (type != "IHDR" || length != 13))
            {
                return null;
            }

            first = false;
            if (PngKeep.Contains(type))
            {
                output.Write(data, pos, 12 + length);
            }

            pos += 12 + length;
            if (type == "IEND")
            {
                return output.ToArray();
            }
        }

        return null;
    }
}
