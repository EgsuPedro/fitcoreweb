using System.Security.Cryptography;
using FitCore.SharedKernel;

namespace FitCore.Wellness.Domain;

public enum PhotoAngle
{
    Front = 1,
    Side = 2,
    Back = 3,
    Other = 4
}

public enum PhotoConsentAction
{
    Granted = 1,
    Revoked = 2
}

/// <summary>Quién registró el consentimiento: la propia persona desde su sesión o el personal en su nombre (por ejemplo, firmado en papel).</summary>
public enum PhotoConsentSource
{
    Member = 1,
    Staff = 2
}

/// <summary>
/// Registro de consentimiento para guardar fotos de progreso. Es solo-agregar: dar y retirar el consentimiento son filas nuevas, nunca
/// se edita ni se borra una, y el estado vigente es la última. Guarda la versión del aviso que se mostró, y el nombre del
/// representante cuando la persona es menor de edad. No es <see cref="IAuditable"/> a propósito: es un dato personal y la auditoría
/// automática copiaría su contenido.
/// </summary>
public sealed class PhotoConsent : Entity<Guid>, ITenantOwned
{
    /// <summary>Versión vigente del texto del aviso de privacidad de las fotos; sube cuando el texto cambia de fondo.</summary>
    public const int CurrentNoticeVersion = 1;

    public const int MaxGuardianNameLength = 120;

    private PhotoConsent(Guid id)
        : base(id)
    {
    }

    public Guid TenantId { get; private set; }
    public Guid MemberId { get; private set; }
    public PhotoConsentAction Action { get; private set; }
    public PhotoConsentSource Source { get; private set; }
    public int NoticeVersion { get; private set; }
    public string? GuardianName { get; private set; }
    public Guid RecordedBy { get; private set; }
    public DateTimeOffset RecordedAt { get; private set; }

    public static PhotoConsent Grant(Guid tenantId, Guid memberId, PhotoConsentSource source, string? guardianName, Guid recordedBy, DateTimeOffset now) =>
        new(Guid.CreateVersion7())
        {
            TenantId = tenantId,
            MemberId = memberId,
            Action = PhotoConsentAction.Granted,
            Source = source,
            NoticeVersion = CurrentNoticeVersion,
            GuardianName = string.IsNullOrWhiteSpace(guardianName) ? null : guardianName.Trim(),
            RecordedBy = recordedBy,
            RecordedAt = now
        };

    public static PhotoConsent Revoke(Guid tenantId, Guid memberId, PhotoConsentSource source, Guid recordedBy, DateTimeOffset now) =>
        new(Guid.CreateVersion7())
        {
            TenantId = tenantId,
            MemberId = memberId,
            Action = PhotoConsentAction.Revoked,
            Source = source,
            NoticeVersion = CurrentNoticeVersion,
            RecordedBy = recordedBy,
            RecordedAt = now
        };
}

/// <summary>
/// Foto de progreso. El archivo vive en el almacén privado bajo una clave aleatoria (<see cref="StorageKey"/>); aquí solo se guardan
/// sus datos. No se edita: se sube o se borra. Siempre referencia el consentimiento que la cubría al subirla.
/// </summary>
public sealed class ProgressPhoto : Entity<Guid>, ITenantOwned
{
    public const int MaxPerMember = 100;
    public const int MaxStorageKeyLength = 120;

    private ProgressPhoto(Guid id)
        : base(id)
    {
    }

    public Guid TenantId { get; private set; }
    public Guid MemberId { get; private set; }
    public DateOnly TakenOn { get; private set; }
    public PhotoAngle Angle { get; private set; }
    public string StorageKey { get; private set; } = string.Empty;
    public string ContentType { get; private set; } = string.Empty;
    public long SizeBytes { get; private set; }
    public Guid ConsentId { get; private set; }
    public Guid UploadedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static ProgressPhoto Create(
        Guid tenantId,
        Guid memberId,
        DateOnly takenOn,
        PhotoAngle angle,
        string storageKey,
        string contentType,
        long sizeBytes,
        Guid consentId,
        Guid uploadedBy,
        DateTimeOffset now) =>
        new(Guid.CreateVersion7())
        {
            TenantId = tenantId,
            MemberId = memberId,
            TakenOn = takenOn,
            Angle = angle,
            StorageKey = storageKey,
            ContentType = contentType,
            SizeBytes = sizeBytes,
            ConsentId = consentId,
            UploadedBy = uploadedBy,
            CreatedAt = now
        };
}

public static class PhotoRules
{
    public const long MaxBytes = 8L * 1024 * 1024;
    public const string Jpeg = "image/jpeg";
    public const string Png = "image/png";
    public const int AdultAge = 18;
    public const int MaxAgeYears = 10;

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    // Chunks PNG que afectan cómo se dibuja la imagen; el resto (texto, fecha, EXIF) se descarta.
    private static readonly HashSet<string> PngKeep = ["IHDR", "PLTE", "IDAT", "IEND", "tRNS", "gAMA", "cHRM", "sRGB", "iCCP", "sBIT", "pHYs", "bKGD"];

    public static bool IsMinor(int? age) => age is < AdultAge;

    /// <summary>Fecha de la foto: no futura (en el día de la sucursal) ni de hace más de diez años.</summary>
    public static bool IsDateValid(DateOnly takenOn, DateOnly today) => takenOn <= today && takenOn >= today.AddYears(-MaxAgeYears);

    /// <summary>Clave aleatoria e impredecible: no contiene nombres, fechas ni identificadores de la persona.</summary>
    public static string NewStorageKey(Guid tenantId) =>
        $"progress-photos/{tenantId:N}/{RandomNumberGenerator.GetHexString(32, lowercase: true)}";

    /// <summary>El tipo se decide por los primeros bytes del archivo, nunca por lo que declare el cliente (nombre o cabecera).</summary>
    public static string? DetectContentType(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            return Jpeg;
        }

        return data.Length >= PngSignature.Length && data[..PngSignature.Length].SequenceEqual(PngSignature) ? Png : null;
    }

    /// <summary>
    /// Quita los metadatos de la imagen (EXIF con ubicación GPS y fecha del equipo, texto, miniaturas, comentarios) y todo lo que
    /// siga al final de la imagen. Devuelve nulo si el archivo no es una imagen bien formada de ese tipo. No vuelve a codificar los
    /// píxeles: la imagen queda idéntica.
    /// </summary>
    public static byte[]? StripMetadata(byte[] data, string contentType) => contentType switch
    {
        Jpeg => StripJpeg(data),
        Png => StripPng(data),
        _ => null
    };

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
                pos++; // bytes de relleno antes del marcador
            }

            if (pos >= data.Length)
            {
                return null;
            }

            var marker = data[pos++];
            if (marker == 0xD9)
            {
                // Fin de la imagen: lo que haya después (segundas imágenes, datos añadidos) no se conserva.
                output.Write([0xFF, 0xD9]);
                return sawScan ? output.ToArray() : null;
            }

            if (marker is 0x01 or (>= 0xD0 and <= 0xD8))
            {
                output.Write([0xFF, marker]); // marcadores sin longitud
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

            // Datos comprimidos del barrido: se copian tal cual hasta el siguiente marcador real (FF seguido de algo distinto de 00, FF o RSTn).
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

    /// <summary>Se conservan los segmentos que la imagen necesita para dibujarse (JFIF, perfil de color ICC, Adobe) y se descartan EXIF/XMP, IPTC, MPF y comentarios.</summary>
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
