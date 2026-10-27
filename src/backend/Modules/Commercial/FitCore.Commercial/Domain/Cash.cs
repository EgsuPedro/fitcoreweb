using FitCore.SharedKernel;

namespace FitCore.Commercial.Domain;

public enum CashSessionStatus
{
    Open = 1,
    Closed = 2
}

public enum CashMovementType
{
    /// <summary>Cobro en efectivo de un pago. Entra dinero a la caja.</summary>
    CashPayment = 1,

    /// <summary>Reverso de un cobro en efectivo. Sale dinero de la caja.</summary>
    PaymentReversal = 2,

    /// <summary>Ingreso autorizado que no es un cobro (por ejemplo, un fondo adicional).</summary>
    ManualIn = 3,

    /// <summary>Egreso autorizado (por ejemplo, pago de un proveedor o retiro de efectivo).</summary>
    ManualOut = 4
}

public enum CashDirection
{
    In = 1,
    Out = 2
}

/// <summary>Caja física de una sucursal. No guarda dinero: lo guardan sus sesiones y movimientos.</summary>
public sealed class CashRegister : Entity<Guid>, ITenantOwned, IAuditable
{
    private CashRegister(Guid id)
        : base(id)
    {
    }

    public Guid TenantId { get; private set; }
    public Guid BranchId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public bool IsActive { get; private set; } = true;

    /// <summary>Si es falso (lo normal), la caja admite una sola sesión abierta a la vez.</summary>
    public bool AllowMultipleOpenSessions { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public Guid? CreatedBy { get; private set; }
    public DateTimeOffset? UpdatedAt { get; private set; }
    public Guid? UpdatedBy { get; private set; }

    public static CashRegister Create(Guid tenantId, Guid branchId, string name, bool allowMultipleOpenSessions, Guid? createdBy, DateTimeOffset now) =>
        new(Guid.CreateVersion7())
        {
            TenantId = tenantId,
            BranchId = branchId,
            Name = name.Trim(),
            AllowMultipleOpenSessions = allowMultipleOpenSessions,
            CreatedAt = now,
            CreatedBy = createdBy
        };

    public void Update(string name, bool isActive, bool allowMultipleOpenSessions, Guid? updatedBy, DateTimeOffset now)
    {
        Name = name.Trim();
        IsActive = isActive;
        AllowMultipleOpenSessions = allowMultipleOpenSessions;
        UpdatedAt = now;
        UpdatedBy = updatedBy;
    }
}

/// <summary>
/// Turno de caja de un cajero: se abre con un saldo inicial y se cierra con un arqueo. El saldo esperado nunca se envía
/// ni se edita: sale de los movimientos registrados (ver <see cref="CashMath"/>).
/// </summary>
public sealed class CashSession : Entity<Guid>, ITenantOwned, IAuditable
{
    private CashSession(Guid id)
        : base(id)
    {
    }

    public Guid TenantId { get; private set; }
    public Guid BranchId { get; private set; }
    public Guid RegisterId { get; private set; }

    /// <summary>Copia de la configuración de la caja al abrir: si es verdadero, ninguna otra sesión de esa caja puede estar abierta.</summary>
    public bool Exclusive { get; private set; }

    public Guid OpenedBy { get; private set; }
    public DateTimeOffset OpenedAt { get; private set; }
    public decimal OpeningAmount { get; private set; }
    public string? OpenNotes { get; private set; }
    public CashSessionStatus Status { get; private set; } = CashSessionStatus.Open;
    public Guid? ClosedBy { get; private set; }
    public DateTimeOffset? ClosedAt { get; private set; }
    public decimal? CountedAmount { get; private set; }

    /// <summary>Saldo esperado calculado al cerrar, guardado para el historial de cierres.</summary>
    public decimal? ExpectedAtClose { get; private set; }

    /// <summary>Contado menos esperado: negativo es faltante, positivo es sobrante.</summary>
    public decimal? Difference { get; private set; }

    public string? CloseNotes { get; private set; }

    public static CashSession Open(Guid tenantId, CashRegister register, decimal openingAmount, string? notes, Guid openedBy, DateTimeOffset now) =>
        new(Guid.CreateVersion7())
        {
            TenantId = tenantId,
            BranchId = register.BranchId,
            RegisterId = register.Id,
            Exclusive = !register.AllowMultipleOpenSessions,
            OpenedBy = openedBy,
            OpenedAt = now,
            OpeningAmount = Money.Round(openingAmount),
            OpenNotes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim()
        };

    public Result Close(decimal countedAmount, decimal expectedAmount, string? notes, Guid closedBy, DateTimeOffset now)
    {
        if (Status == CashSessionStatus.Closed)
        {
            return Result.Failure(CashErrors.SessionClosed);
        }

        var counted = Money.Round(countedAmount);
        var difference = counted - expectedAmount;
        if (difference != 0m && string.IsNullOrWhiteSpace(notes))
        {
            return Result.Failure(CashErrors.DifferenceRequiresNotes);
        }

        Status = CashSessionStatus.Closed;
        ClosedBy = closedBy;
        ClosedAt = now;
        CountedAmount = counted;
        ExpectedAtClose = expectedAmount;
        Difference = difference;
        CloseNotes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        return Result.Success();
    }
}

/// <summary>Entrada o salida de dinero de una sesión. Solo se agregan: un error se corrige con un movimiento contrario.</summary>
public sealed class CashMovement : ITenantOwned
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid BranchId { get; init; }
    public Guid SessionId { get; init; }
    public CashMovementType Type { get; init; }
    public CashDirection Direction { get; init; }
    public decimal Amount { get; init; }
    public string? Reason { get; init; }
    public Guid? PaymentId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public Guid? CreatedBy { get; init; }

    public static CashDirection DirectionOf(CashMovementType type) =>
        type is CashMovementType.CashPayment or CashMovementType.ManualIn ? CashDirection.In : CashDirection.Out;

    public static CashMovement Create(
        Guid tenantId,
        CashSession session,
        CashMovementType type,
        decimal amount,
        string? reason,
        Guid? paymentId,
        Guid? createdBy,
        DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            BranchId = session.BranchId,
            SessionId = session.Id,
            Type = type,
            Direction = DirectionOf(type),
            Amount = Money.Round(amount),
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
            PaymentId = paymentId,
            CreatedAt = now,
            CreatedBy = createdBy
        };
}

public static class CashMath
{
    /// <summary>Saldo esperado en caja: apertura + ingresos − egresos. Es la única fórmula; nadie la escribe a mano.</summary>
    public static decimal Expected(decimal opening, decimal totalIn, decimal totalOut) => opening + totalIn - totalOut;
}

public static class CashErrors
{
    public static readonly Error SessionClosed = Error.Conflict("cash.session_closed", "La sesión de caja ya está cerrada.");
    public static readonly Error DifferenceRequiresNotes = Error.Validation("cash.difference_requires_notes", "Hay una diferencia entre lo contado y lo esperado: indique una observación.");
}
