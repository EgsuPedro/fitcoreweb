using System.Security.Cryptography;
using System.Text;
using FitCore.SharedKernel;

namespace FitCore.Commercial.Domain;

public enum AttendanceMethod
{
    /// <summary>El personal identifica al alumno (por su código o identificador).</summary>
    Manual = 1,

    /// <summary>El personal escanea el código QR del alumno.</summary>
    Qr = 2,

    /// <summary>Reservado para integraciones con dispositivos físicos (torniquetes, lectores): aún no disponible.</summary>
    Device = 3
}

/// <summary>
/// Una visita de un alumno a una sucursal. La hora de entrada y de salida es siempre la del servidor: ni el cliente ni el
/// alumno pueden fijarla. Una vez registrada la salida, la fila es definitiva.
/// </summary>
public sealed class AttendanceEntry : Entity<Guid>, ITenantOwned, IAuditable
{
    private AttendanceEntry(Guid id)
        : base(id)
    {
    }

    public Guid TenantId { get; private set; }
    public Guid BranchId { get; private set; }
    public Guid MemberId { get; private set; }

    /// <summary>Membresía que dio acceso. Es nula solo en un acceso por excepción sin membresía aplicable.</summary>
    public Guid? MembershipId { get; private set; }

    public DateTimeOffset CheckInAt { get; private set; }
    public DateTimeOffset? CheckOutAt { get; private set; }
    public AttendanceMethod Method { get; private set; }
    public AttendanceMethod? CheckOutMethod { get; private set; }

    /// <summary>Verdadero si el acceso se autorizó como excepción a una regla (deuda, membresía vencida…).</summary>
    public bool IsException { get; private set; }

    public string? ExceptionReason { get; private set; }
    public Guid RegisteredBy { get; private set; }
    public Guid? CheckOutBy { get; private set; }

    public bool IsOpen => CheckOutAt is null;

    public static AttendanceEntry CheckIn(
        Guid tenantId,
        Guid branchId,
        Guid memberId,
        Guid? membershipId,
        AttendanceMethod method,
        string? exceptionReason,
        Guid registeredBy,
        DateTimeOffset now) =>
        new(Guid.CreateVersion7())
        {
            TenantId = tenantId,
            BranchId = branchId,
            MemberId = memberId,
            MembershipId = membershipId,
            CheckInAt = now,
            Method = method,
            IsException = !string.IsNullOrWhiteSpace(exceptionReason),
            ExceptionReason = string.IsNullOrWhiteSpace(exceptionReason) ? null : exceptionReason.Trim(),
            RegisteredBy = registeredBy
        };

    public Result CheckOut(AttendanceMethod method, Guid actor, DateTimeOffset now)
    {
        if (CheckOutAt is not null)
        {
            return Result.Failure(AttendanceErrors.NoOpenEntry);
        }

        CheckOutAt = now;
        CheckOutMethod = method;
        CheckOutBy = actor;
        return Result.Success();
    }
}

public static class AttendanceErrors
{
    public static readonly Error NoOpenEntry = Error.Validation("attendance.no_open_entry", "El alumno no tiene una entrada abierta.");
}

/// <summary>Estado de una obligación de la membresía, para evaluar el acceso con deuda.</summary>
public readonly record struct ObligationStanding(decimal Payable, decimal Paid, DateOnly DueDate)
{
    public decimal Owed => Math.Max(0m, Payable - Paid);
}

/// <summary>
/// Regla de acceso con deuda (política del gimnasio, ADR 0008). Se evalúa en cada entrada con los saldos vigentes: un reverso de
/// pago posterior corta el acceso sin tocar la membresía.
/// </summary>
public static class AccessRules
{
    public const string DebtPending = "attendance.debt_pending";
    public const string DebtOverdue = "attendance.debt_overdue";
    public const string MinimumPaymentNotMet = "attendance.minimum_payment_not_met";

    /// <summary>Código del motivo de rechazo por deuda, o nulo si la deuda no impide el acceso.</summary>
    public static string? DebtDenial(AccessPolicy policy, int minPaymentPercent, DateOnly today, IReadOnlyCollection<ObligationStanding> obligations)
    {
        switch (policy)
        {
            case AccessPolicy.RequireFullPayment:
                return obligations.Any(o => o.Owed > 0m) ? DebtPending : null;

            case AccessPolicy.AllowWithMinimumPayment:
                var payable = obligations.Sum(o => o.Payable);
                var paid = obligations.Sum(o => Math.Min(o.Paid, o.Payable));
                return payable > 0m && paid * 100m < payable * minPaymentPercent ? MinimumPaymentNotMet : null;

            default:
                return obligations.Any(o => o.Owed > 0m && o.DueDate < today) ? DebtOverdue : null;
        }
    }
}

/// <summary>
/// Código QR del alumno: un token aleatorio y opaco, sin datos personales ni saldos. En la base solo se guarda su huella
/// (SHA-256): quien lea la base no puede fabricar un QR. Rotarlo invalida el anterior.
/// </summary>
public static class QrTokens
{
    public const string Prefix = "fc1_";

    public static string Generate() => Prefix + Base64Url(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token.Trim()))).ToLowerInvariant();

    public static bool LooksValid(string token) => token.StartsWith(Prefix, StringComparison.Ordinal) && token.Length == Prefix.Length + 43;

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
