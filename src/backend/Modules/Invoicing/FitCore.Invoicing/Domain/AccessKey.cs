using System.Globalization;
using System.Security.Cryptography;

namespace FitCore.Invoicing.Domain;

/// <summary>
/// Clave de acceso de 49 dígitos (Tabla 1 de la ficha técnica del SRI, versión 2.20): fecha de emisión ddmmaaaa (8) · tipo de comprobante (2) · RUC (13) · ambiente (1) ·
/// serie, establecimiento y punto de emisión (6) · secuencial (9) · código numérico (8) · tipo de emisión (1) · dígito verificador módulo 11 (1). En el esquema offline la clave es
/// el número de autorización. Todo campo debe completarse a su longitud: una clave mal formada se rechaza.
/// </summary>
public static class AccessKey
{
    public const int Length = 49;

    public static string Build(
        DateOnly issueDate, string documentCode, string ruc, SriEnvironment environment, string establishmentCode, string pointCode, int sequential, string numericCode)
    {
        Require(documentCode, 2, nameof(documentCode));
        Require(ruc, 13, nameof(ruc));
        Require(establishmentCode, 3, nameof(establishmentCode));
        Require(pointCode, 3, nameof(pointCode));
        Require(numericCode, 8, nameof(numericCode));
        if (sequential is < 1 or > 999_999_999)
        {
            throw new ArgumentOutOfRangeException(nameof(sequential), "El secuencial va de 1 a 999999999.");
        }

        var body = string.Concat(
            issueDate.ToString("ddMMyyyy", CultureInfo.InvariantCulture),
            documentCode,
            ruc,
            SriCatalogs.EnvironmentCode(environment),
            establishmentCode,
            pointCode,
            sequential.ToString("D9", CultureInfo.InvariantCulture),
            numericCode,
            SriCatalogs.NormalEmissionCode);
        return body + CheckDigit(body).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Módulo 11 con factor de chequeo ponderado: los dígitos, de derecha a izquierda, se multiplican por 2, 3, 4, 5, 6, 7 y vuelven a 2; se suman; el dígito es 11 − (suma mod 11);
    /// si da 11 es 0 y si da 10 es 1 (ficha técnica, 5.2). El ejemplo oficial «41261533» da 6.
    /// </summary>
    public static int CheckDigit(string digits)
    {
        var sum = 0;
        var factor = 2;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            sum += (digits[i] - '0') * factor;
            factor = factor == 7 ? 2 : factor + 1;
        }

        var result = 11 - (sum % 11);
        return result switch
        {
            11 => 0,
            10 => 1,
            _ => result
        };
    }

    public static bool IsValid(string? key) =>
        key is { Length: Length } && key.All(char.IsAsciiDigit) && CheckDigit(key[..(Length - 1)]) == key[^1] - '0';

    /// <summary>Código numérico de 8 dígitos: lo define el emisor (ficha 5.2); aquí es aleatorio para que la clave no se pueda adivinar a partir del secuencial.</summary>
    public static string NewNumericCode() => RandomNumberGenerator.GetInt32(0, 100_000_000).ToString("D8", CultureInfo.InvariantCulture);

    private static void Require(string value, int length, string name)
    {
        if (value is null || value.Length != length || !value.All(char.IsAsciiDigit))
        {
            throw new ArgumentException($"El campo debe tener exactamente {length} dígitos.", name);
        }
    }
}
