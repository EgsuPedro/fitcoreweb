using FitCore.SharedKernel;

namespace FitCore.Commercial.Domain;

public enum IdentificationType
{
    Cedula = 1,
    Ruc = 2,
    Passport = 3,
    Other = 4
}

/// <summary>
/// Valida documentos según las reglas de Ecuador: cédula (módulo 10), RUC de persona natural, sociedad privada y
/// entidad pública (módulos 10 y 11) y pasaporte (alfanumérico). "Otro" solo exige un formato razonable.
/// </summary>
public static class IdentificationValidator
{
    public static string Normalize(string number) => EcuadorIds.Normalize(number);

    public static bool IsValid(IdentificationType type, string number)
    {
        var value = Normalize(number);
        return type switch
        {
            IdentificationType.Cedula => IsValidCedula(value),
            IdentificationType.Ruc => IsValidRuc(value),
            IdentificationType.Passport => value.Length is >= 5 and <= 20 && value.All(char.IsAsciiLetterOrDigit),
            IdentificationType.Other => value.Length is >= 3 and <= 20 && value.All(char.IsAsciiLetterOrDigit),
            _ => false
        };
    }

    public static bool IsValidCedula(string value) => EcuadorIds.IsValidCedula(value);

    public static bool IsValidRuc(string value) => EcuadorIds.IsValidRuc(value);
}
