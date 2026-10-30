using System.Net.Mail;
using FitCore.Application.Core;
using FitCore.SharedKernel;

namespace FitCore.Invoicing.Domain;

/// <summary>
/// Tipos de identificación del comprador y su código (Tabla 6 de la ficha técnica del SRI, esquema offline, versión 2.20 leída en el documento oficial:
/// 04 RUC, 05 cédula, 06 pasaporte, 07 venta a consumidor final, 08 identificación del exterior). Debe contrastarse con la versión vigente (2.34) antes del bloque 7.
/// </summary>
public enum SriBuyerIdType
{
    Ruc = 4,
    Cedula = 5,
    Passport = 6,
    FinalConsumer = 7,
    Foreign = 8
}

public static class SriBuyerIdTypes
{
    public static string Code(this SriBuyerIdType type) => ((int)type).ToString("00", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Tipo con el que se identifica por defecto el documento que el gimnasio registró. «Otro» no tiene equivalente: hay que elegirlo al emitir.</summary>
    public static SriBuyerIdType? DefaultFor(SourceIdKind kind) => kind switch
    {
        SourceIdKind.Ruc => SriBuyerIdType.Ruc,
        SourceIdKind.Cedula => SriBuyerIdType.Cedula,
        SourceIdKind.Passport => SriBuyerIdType.Passport,
        _ => null
    };
}

/// <summary>Observación sobre el comprador: <c>Blocking</c> impide emitir; el resto solo avisa (por ejemplo, sin correo no se podrá enviar el RIDE).</summary>
public sealed record BuyerIssue(string Code, bool Blocking);

/// <summary>Copia del comprador que se congela en el comprobante: aunque el alumno cambie sus datos después, el comprobante no cambia.</summary>
public sealed record InvoiceBuyerSnapshot(
    SriBuyerIdType IdType,
    string IdNumber,
    string LegalName,
    string? Address,
    string? Email,
    string? Phone,
    Guid? PersonId)
{
    public string IdTypeCode => IdType.Code();

    public bool IsFinalConsumer => IdType == SriBuyerIdType.FinalConsumer;
}

public sealed record BuyerResolution(InvoiceBuyerSnapshot? Buyer, IReadOnlyList<BuyerIssue> Issues)
{
    public bool IsValid => Buyer is not null && Issues.All(i => !i.Blocking);
}

/// <summary>
/// Reglas del comprador. El SRI exige identificar al comprador con el tipo y número que corresponden; la venta a consumidor final se identifica con trece nueves y
/// la razón social «CONSUMIDOR FINAL», y solo puede usarse hasta un monto máximo que fija la normativa tributaria (se recibe por parámetro, no se codifica).
/// </summary>
public static class BuyerRules
{
    public const string FinalConsumerId = "9999999999999";
    public const string FinalConsumerName = "CONSUMIDOR FINAL";
    public const int MaxNameLength = 300;
    public const int MaxAddressLength = 300;

    /// <summary>
    /// Arma el comprador del comprobante. <paramref name="chosen"/> permite al usuario cambiar el tipo (por ejemplo, identificar con pasaporte un documento
    /// registrado como «Otro», o vender a consumidor final); sin elección se usa el tipo equivalente al documento registrado.
    /// </summary>
    public static BuyerResolution Resolve(InvoiceSourceBuyer source, SriBuyerIdType? chosen, decimal total, decimal finalConsumerMaxAmount)
    {
        var issues = new List<BuyerIssue>();

        if (chosen == SriBuyerIdType.FinalConsumer)
        {
            if (total > finalConsumerMaxAmount)
            {
                issues.Add(new BuyerIssue("final_consumer_over_limit", true));
            }

            var finalConsumer = new InvoiceBuyerSnapshot(SriBuyerIdType.FinalConsumer, FinalConsumerId, FinalConsumerName, null, null, null, null);
            return new BuyerResolution(finalConsumer, issues);
        }

        var type = chosen ?? SriBuyerIdTypes.DefaultFor(source.IdKind);
        if (type is null)
        {
            issues.Add(new BuyerIssue("id_type_required", true));
            return new BuyerResolution(null, issues);
        }

        var number = EcuadorIds.Normalize(source.IdNumber);
        if (!IsValidNumber(type.Value, number))
        {
            issues.Add(new BuyerIssue(type.Value switch
            {
                SriBuyerIdType.Ruc => "ruc_invalid",
                SriBuyerIdType.Cedula => "cedula_invalid",
                SriBuyerIdType.Passport => "passport_invalid",
                _ => "foreign_id_invalid"
            }, true));
        }

        var name = Collapse($"{source.FirstName} {source.LastName}");
        if (name.Length == 0)
        {
            issues.Add(new BuyerIssue("name_required", true));
        }
        else if (name.Length > MaxNameLength)
        {
            issues.Add(new BuyerIssue("name_too_long", true));
        }

        var address = Collapse(source.Address);
        if (address.Length == 0)
        {
            issues.Add(new BuyerIssue("address_missing", false));
        }
        else if (address.Length > MaxAddressLength)
        {
            issues.Add(new BuyerIssue("address_too_long", true));
        }

        var email = string.IsNullOrWhiteSpace(source.Email) ? null : source.Email.Trim().ToLowerInvariant();
        if (email is null)
        {
            issues.Add(new BuyerIssue("email_missing", false));
        }
        else if (!IsValidEmail(email))
        {
            issues.Add(new BuyerIssue("email_invalid", false));
        }

        var snapshot = new InvoiceBuyerSnapshot(
            type.Value, number, name, address.Length == 0 ? null : address, email is not null && IsValidEmail(email) ? email : null,
            string.IsNullOrWhiteSpace(source.Phone) ? null : source.Phone.Trim(), source.PersonId);
        return new BuyerResolution(snapshot, issues);
    }

    /// <summary>RUC y cédula con los módulos oficiales; pasaporte y documento del exterior solo alfanuméricos de largo razonable (el SRI no define otro formato).</summary>
    public static bool IsValidNumber(SriBuyerIdType type, string normalized) => type switch
    {
        SriBuyerIdType.Ruc => EcuadorIds.IsValidRuc(normalized),
        SriBuyerIdType.Cedula => EcuadorIds.IsValidCedula(normalized),
        SriBuyerIdType.Passport => normalized.Length is >= 5 and <= 20 && normalized.All(char.IsAsciiLetterOrDigit),
        SriBuyerIdType.Foreign => normalized.Length is >= 3 and <= 20 && normalized.All(char.IsAsciiLetterOrDigit),
        _ => normalized == FinalConsumerId
    };

    private static string Collapse(string? text) =>
        string.IsNullOrWhiteSpace(text) ? string.Empty : string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static bool IsValidEmail(string email)
    {
        if (email.Length > 254 || !MailAddress.TryCreate(email, out var parsed))
        {
            return false;
        }

        return parsed.Address == email && email.Contains('.', StringComparison.Ordinal);
    }
}
