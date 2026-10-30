using System.Security.Cryptography;
using FitCore.SharedKernel;
using Microsoft.Extensions.Options;

namespace FitCore.Invoicing.Application.Security;

/// <summary>
/// Configuración de la clave maestra que protege los certificados y sus contraseñas (sección <c>Invoicing</c>). La clave son 32 bytes aleatorios en base64
/// (<c>openssl rand -base64 32</c>) y vive <b>fuera de la base de datos y de git</b> (variable de entorno o gestor de secretos). Sin ella no se pueden cargar certificados.
/// </summary>
public sealed class InvoicingOptions
{
    public const string SectionName = "Invoicing";

    public string MasterKey { get; set; } = string.Empty;

    /// <summary>Versión de la clave maestra; se guarda con cada secreto para poder rotarla sin perder los anteriores.</summary>
    public int MasterKeyVersion { get; set; } = 1;

    /// <summary>
    /// Monto máximo (USD, con impuestos) de una venta a consumidor final. Lo fija la normativa tributaria y puede cambiar: por eso es configuración y no una constante.
    /// Por encima de este valor hay que identificar al comprador.
    /// </summary>
    public decimal FinalConsumerMaxAmount { get; set; } = 50m;
}

/// <summary>Cifra y descifra secretos (certificado y contraseña). Todo secreto queda atado a su contexto (gimnasio y propósito): copiarlo a otra fila no lo descifra.</summary>
public interface ISecretProtector
{
    bool IsConfigured { get; }

    int KeyVersion { get; }

    byte[] Protect(ReadOnlySpan<byte> plaintext, string context);

    byte[] Unprotect(ReadOnlySpan<byte> package, string context);
}

/// <summary>
/// Cifrado por sobre (envelope): cada secreto lleva su propia clave de datos aleatoria (AES-256-GCM), y esa clave viaja cifrada con la clave maestra (AES-256-GCM).
/// Formato: <c>[versión de formato 1][versión de clave maestra 4][nonce 12][etiqueta 16][clave de datos cifrada 32][nonce 12][etiqueta 16][secreto cifrado …]</c>.
/// El contexto se autentica (datos asociados): un paquete alterado o llevado a otro gimnasio falla al descifrar.
/// </summary>
public sealed class AesGcmSecretProtector : ISecretProtector
{
    private const byte Format = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;

    private readonly byte[]? _masterKey;

    public AesGcmSecretProtector(IOptions<InvoicingOptions> options)
    {
        KeyVersion = options.Value.MasterKeyVersion;
        var text = options.Value.MasterKey.Trim();
        if (text.Length == 0)
        {
            return;
        }

        try
        {
            var key = Convert.FromBase64String(text);
            _masterKey = key.Length == KeySize ? key : null;
        }
        catch (FormatException)
        {
            _masterKey = null;
        }
    }

    public bool IsConfigured => _masterKey is not null;

    public int KeyVersion { get; }

    public byte[] Protect(ReadOnlySpan<byte> plaintext, string context)
    {
        var master = RequireKey();
        var dataKey = RandomNumberGenerator.GetBytes(KeySize);
        var aad = System.Text.Encoding.UTF8.GetBytes(context);

        var keyNonce = RandomNumberGenerator.GetBytes(NonceSize);
        var wrapped = new byte[KeySize];
        var keyTag = new byte[TagSize];
        using (var wrap = new AesGcm(master, TagSize))
        {
            wrap.Encrypt(keyNonce, dataKey, wrapped, keyTag, aad);
        }

        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        using (var data = new AesGcm(dataKey, TagSize))
        {
            data.Encrypt(nonce, plaintext, cipher, tag, aad);
        }

        CryptographicOperations.ZeroMemory(dataKey);

        var package = new byte[1 + 4 + NonceSize + TagSize + KeySize + NonceSize + TagSize + cipher.Length];
        var offset = 0;
        package[offset++] = Format;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(package.AsSpan(offset, 4), KeyVersion);
        offset += 4;
        foreach (var part in new[] { keyNonce, keyTag, wrapped, nonce, tag, cipher })
        {
            part.CopyTo(package, offset);
            offset += part.Length;
        }

        return package;
    }

    public byte[] Unprotect(ReadOnlySpan<byte> package, string context)
    {
        var master = RequireKey();
        var header = 1 + 4 + NonceSize + TagSize + KeySize + NonceSize + TagSize;
        if (package.Length < header || package[0] != Format)
        {
            throw new CryptographicException("El secreto cifrado no tiene un formato válido.");
        }

        var offset = 5;
        var keyNonce = package.Slice(offset, NonceSize);
        offset += NonceSize;
        var keyTag = package.Slice(offset, TagSize);
        offset += TagSize;
        var wrapped = package.Slice(offset, KeySize);
        offset += KeySize;
        var nonce = package.Slice(offset, NonceSize);
        offset += NonceSize;
        var tag = package.Slice(offset, TagSize);
        offset += TagSize;
        var cipher = package[offset..];
        var aad = System.Text.Encoding.UTF8.GetBytes(context);

        var dataKey = new byte[KeySize];
        using (var wrap = new AesGcm(master, TagSize))
        {
            wrap.Decrypt(keyNonce, wrapped, keyTag, dataKey, aad);
        }

        var plaintext = new byte[cipher.Length];
        try
        {
            using var data = new AesGcm(dataKey, TagSize);
            data.Decrypt(nonce, cipher, tag, plaintext, aad);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }

        return plaintext;
    }

    private byte[] RequireKey() =>
        _masterKey ?? throw new InvalidOperationException("La clave maestra de facturación (Invoicing:MasterKey) no está configurada o no son 32 bytes en base64.");
}
