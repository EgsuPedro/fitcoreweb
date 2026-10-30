namespace FitCore.SharedKernel;

/// <summary>
/// Reglas de los documentos de identidad de Ecuador que comparten varios módulos (alumnos, emisor de comprobantes): cédula (módulo 10) y RUC de
/// persona natural, sociedad privada y entidad pública (módulos 10 y 11). Es la única implementación: nadie la copia.
/// </summary>
public static class EcuadorIds
{
    public static string Normalize(string number) =>
        number.Trim().Replace(" ", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal).ToUpperInvariant();

    public static bool IsValidCedula(string value)
    {
        if (value.Length != 10 || !value.All(char.IsAsciiDigit))
        {
            return false;
        }

        return IsValidNaturalPersonPrefix(value);
    }

    public static bool IsValidRuc(string value)
    {
        if (value.Length != 13 || !value.All(char.IsAsciiDigit))
        {
            return false;
        }

        if (!ProvinceIsValid(value))
        {
            return false;
        }

        var third = value[2] - '0';
        return third switch
        {
            < 6 => IsValidNaturalPersonPrefix(value[..10]) && value[10..] != "000",
            6 => Modulo11(value[..8], [3, 2, 7, 6, 5, 4, 3, 2], value[8] - '0') && value[9..] != "0000",
            9 => Modulo11(value[..9], [4, 3, 2, 7, 6, 5, 4, 3, 2], value[9] - '0') && value[10..] != "000",
            _ => false
        };
    }

    private static bool ProvinceIsValid(string value)
    {
        var province = int.Parse(value[..2], System.Globalization.CultureInfo.InvariantCulture);
        return province is (>= 1 and <= 24) or 30;
    }

    private static bool IsValidNaturalPersonPrefix(string first10)
    {
        if (!ProvinceIsValid(first10) || first10[2] - '0' >= 6)
        {
            return false;
        }

        var sum = 0;
        for (var i = 0; i < 9; i++)
        {
            var product = (first10[i] - '0') * (i % 2 == 0 ? 2 : 1);
            sum += product > 9 ? product - 9 : product;
        }

        var check = (10 - (sum % 10)) % 10;
        return check == first10[9] - '0';
    }

    private static bool Modulo11(string digits, int[] coefficients, int checkDigit)
    {
        var sum = 0;
        for (var i = 0; i < coefficients.Length; i++)
        {
            sum += (digits[i] - '0') * coefficients[i];
        }

        var residue = sum % 11;
        var expected = residue == 0 ? 0 : 11 - residue;
        return expected != 10 && expected == checkDigit;
    }
}
