using FitCore.Application.Core;
using FitCore.Commercial.Domain;
using FitCore.Commercial.Persistence;
using FitCore.Platform.Persistence;
using FitCore.Platform.Security;
using FitCore.SharedKernel;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace FitCore.Commercial.Application.Cash;

public sealed record CashSessionDto(
    Guid Id,
    Guid RegisterId,
    string RegisterName,
    Guid BranchId,
    string Status,
    Guid OpenedBy,
    string? OpenedByName,
    DateTimeOffset OpenedAt,
    decimal OpeningAmount,
    decimal TotalIn,
    decimal TotalOut,
    decimal ExpectedAmount,
    decimal? CountedAmount,
    decimal? Difference,
    Guid? ClosedBy,
    string? ClosedByName,
    DateTimeOffset? ClosedAt,
    string? OpenNotes,
    string? CloseNotes,
    int MovementCount);

public sealed record CashRegisterDto(
    Guid Id,
    Guid BranchId,
    string BranchCode,
    string BranchName,
    string Name,
    bool IsActive,
    bool AllowMultipleOpenSessions,
    IReadOnlyList<CashSessionDto> OpenSessions,
    DateTimeOffset CreatedAt,
    Guid? CreatedBy,
    DateTimeOffset? UpdatedAt);

public sealed record CashMovementDto(
    Guid Id,
    Guid SessionId,
    string Type,
    string Direction,
    decimal Amount,
    string? Reason,
    Guid? PaymentId,
    string? ReceiptNumber,
    DateTimeOffset CreatedAt,
    Guid? CreatedBy,
    string? CreatedByName);

public sealed record CreateCashRegisterRequest(Guid BranchId, string Name, bool AllowMultipleOpenSessions);

public sealed record UpdateCashRegisterRequest(string Name, bool IsActive, bool AllowMultipleOpenSessions);

public sealed record OpenCashRequest(decimal OpeningAmount, string? Notes);

/// <summary>Cerrar caja: solo el saldo contado. El esperado lo calcula el sistema a partir de los movimientos.</summary>
public sealed record CloseCashRequest(Guid? SessionId, decimal CountedAmount, string? Notes);

/// <summary>Ingreso o egreso autorizado distinto de un cobro. Los cobros y reversos de efectivo los genera el sistema.</summary>
public sealed record CashMovementRequest(CashMovementType Type, decimal Amount, string Reason);

public sealed record ListCashRegistersQuery(PageRequest Page, Guid? BranchId, bool? IsActive, string? Search) : IQuery<PagedResult<CashRegisterDto>>;

public sealed record ListCashSessionsQuery(Guid RegisterId, PageRequest Page, CashSessionStatus? Status, DateOnly? From, DateOnly? To) : IQuery<PagedResult<CashSessionDto>>;

public sealed record GetCashSessionQuery(Guid RegisterId, Guid SessionId) : IQuery<CashSessionDto>;

public sealed record ListCashMovementsQuery(Guid RegisterId, Guid? SessionId, CashMovementType? Type, PageRequest Page) : IQuery<PagedResult<CashMovementDto>>;

public sealed record CreateCashRegisterCommand(CreateCashRegisterRequest Request) : ICommand<CashRegisterDto>;

public sealed record UpdateCashRegisterCommand(Guid Id, UpdateCashRegisterRequest Request) : ICommand<CashRegisterDto>;

public sealed record OpenCashCommand(Guid RegisterId, OpenCashRequest Request, string? IdempotencyKey) : ICommand<CashSessionDto>;

public sealed record CloseCashCommand(Guid RegisterId, CloseCashRequest Request, string? IdempotencyKey) : ICommand<CashSessionDto>;

public sealed record AddCashMovementCommand(Guid RegisterId, CashMovementRequest Request, string? IdempotencyKey) : ICommand<CashMovementDto>;

public sealed class CreateCashRegisterRequestValidator : AbstractValidator<CreateCashRegisterRequest>
{
    public CreateCashRegisterRequestValidator()
    {
        RuleFor(x => x.BranchId).NotEmpty();
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
    }
}

public sealed class UpdateCashRegisterRequestValidator : AbstractValidator<UpdateCashRegisterRequest>
{
    public UpdateCashRegisterRequestValidator() => RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
}

public sealed class OpenCashRequestValidator : AbstractValidator<OpenCashRequest>
{
    public OpenCashRequestValidator()
    {
        RuleFor(x => x.OpeningAmount).GreaterThanOrEqualTo(0m).LessThanOrEqualTo(100000m).PrecisionScale(12, 2, ignoreTrailingZeros: true);
        RuleFor(x => x.Notes).MaximumLength(500);
    }
}

public sealed class CloseCashRequestValidator : AbstractValidator<CloseCashRequest>
{
    public CloseCashRequestValidator()
    {
        RuleFor(x => x.CountedAmount).GreaterThanOrEqualTo(0m).LessThanOrEqualTo(1000000m).PrecisionScale(12, 2, ignoreTrailingZeros: true);
        RuleFor(x => x.Notes).MaximumLength(500);
    }
}

public sealed class CashMovementRequestValidator : AbstractValidator<CashMovementRequest>
{
    public CashMovementRequestValidator()
    {
        RuleFor(x => x.Type).Must(t => t is CashMovementType.ManualIn or CashMovementType.ManualOut)
            .WithMessage("Solo se registran ingresos o egresos manuales: los cobros y reversos de efectivo los genera el sistema.");
        RuleFor(x => x.Amount).GreaterThan(0m).LessThanOrEqualTo(100000m).PrecisionScale(12, 2, ignoreTrailingZeros: true);
        RuleFor(x => x.Reason).NotEmpty().MinimumLength(5).MaximumLength(300);
    }
}

internal static class CashErrorsApp
{
    public static readonly Error RegisterNotFound = Error.NotFound("cash.register_not_found", "Caja no encontrada.");
    public static readonly Error SessionNotFound = Error.NotFound("cash.session_not_found", "Sesión de caja no encontrada.");
    public static readonly Error NameTaken = Error.Conflict("cash.register_name_taken", "Ya existe una caja con ese nombre en la sucursal.");
    public static readonly Error RegisterInactive = Error.Validation("cash.register_inactive", "La caja está inactiva.");
    public static readonly Error RegisterAlreadyOpen = Error.Conflict("cash.register_already_open", "La caja ya tiene una sesión abierta.");
    public static readonly Error UserHasOpenSession = Error.Conflict("cash.user_has_open_session", "Ya tiene una sesión de caja abierta: ciérrela antes de abrir otra.");
    public static readonly Error NoOpenSession = Error.Validation("cash.no_open_session", "No tiene una sesión de caja abierta en esta sucursal.");
    public static readonly Error InsufficientFunds = Error.Validation("cash.insufficient_funds", "La caja no tiene efectivo suficiente para ese egreso.");
    public static readonly Error CloseNotAllowed = Error.Forbidden("cash.close_not_allowed", "Solo quien abrió la caja o un supervisor puede cerrarla.");
    public static readonly Error HasOpenSessions = Error.Conflict("cash.has_open_sessions", "La caja tiene sesiones abiertas: ciérrelas antes de desactivarla o cambiar su configuración.");
}

internal static class CashQueries
{
    public static IQueryable<CashRegister> VisibleTo(this IQueryable<CashRegister> registers, UserAccess actor) =>
        registers.InScope(actor, r => r.BranchId);

    public static IQueryable<CashSession> VisibleTo(this IQueryable<CashSession> sessions, UserAccess actor) =>
        sessions.InScope(actor, s => s.BranchId);

    public static async Task<Dictionary<Guid, string>> UserNamesAsync(PlatformDbContext platformDb, IEnumerable<Guid?> ids, CancellationToken cancellationToken)
    {
        var wanted = ids.Where(i => i is not null).Select(i => i!.Value).Distinct().ToList();
        return wanted.Count == 0
            ? []
            : await platformDb.Users.AsNoTracking().Where(u => wanted.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, cancellationToken);
    }

    /// <summary>Respuestas de sesiones con sus totales (derivados de los movimientos) y los nombres de quienes las abrieron y cerraron.</summary>
    public static async Task<IReadOnlyList<CashSessionDto>> ToDtosAsync(
        CommercialDbContext db,
        PlatformDbContext platformDb,
        IReadOnlyList<CashSession> sessions,
        CancellationToken cancellationToken)
    {
        var sessionIds = sessions.Select(s => s.Id).ToList();
        var registerIds = sessions.Select(s => s.RegisterId).Distinct().ToList();
        var registers = await db.CashRegisters.AsNoTracking().Where(r => registerIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Name, cancellationToken);
        var totals = await CashLedger.TotalsAsync(db, sessionIds, cancellationToken);
        var names = await UserNamesAsync(platformDb, sessions.SelectMany(s => new Guid?[] { s.OpenedBy, s.ClosedBy }), cancellationToken);

        return
        [
            .. sessions.Select(s =>
            {
                var total = totals.GetValueOrDefault(s.Id);
                var expected = s.Status == CashSessionStatus.Closed
                    ? s.ExpectedAtClose ?? CashMath.Expected(s.OpeningAmount, total.In, total.Out)
                    : CashMath.Expected(s.OpeningAmount, total.In, total.Out);
                return new CashSessionDto(
                    s.Id,
                    s.RegisterId,
                    registers.GetValueOrDefault(s.RegisterId, string.Empty),
                    s.BranchId,
                    s.Status.ToString(),
                    s.OpenedBy,
                    names.GetValueOrDefault(s.OpenedBy),
                    s.OpenedAt,
                    s.OpeningAmount,
                    total.In,
                    total.Out,
                    expected,
                    s.CountedAmount,
                    s.Difference,
                    s.ClosedBy,
                    s.ClosedBy is { } closer ? names.GetValueOrDefault(closer) : null,
                    s.ClosedAt,
                    s.OpenNotes,
                    s.CloseNotes,
                    total.Count);
            })
        ];
    }

    public static async Task<CashSessionDto> ToDtoAsync(CommercialDbContext db, PlatformDbContext platformDb, CashSession session, CancellationToken cancellationToken) =>
        (await ToDtosAsync(db, platformDb, [session], cancellationToken))[0];
}

/// <summary>
/// Libro de caja: los totales de una sesión salen siempre de sus movimientos. Cobros y reversos de efectivo, e ingresos y
/// egresos manuales, son las únicas cosas que mueven el saldo esperado.
/// </summary>
internal static class CashLedger
{
    public readonly record struct Totals(decimal In, decimal Out, int Count);

    public static async Task<Dictionary<Guid, Totals>> TotalsAsync(CommercialDbContext db, IReadOnlyCollection<Guid> sessionIds, CancellationToken cancellationToken)
    {
        if (sessionIds.Count == 0)
        {
            return [];
        }

        var rows = await db.CashMovements.AsNoTracking().Where(m => sessionIds.Contains(m.SessionId))
            .Select(m => new { m.SessionId, m.Direction, m.Amount }).ToListAsync(cancellationToken);
        return rows.GroupBy(r => r.SessionId).ToDictionary(
            g => g.Key,
            g => new Totals(
                g.Where(r => r.Direction == CashDirection.In).Sum(r => r.Amount),
                g.Where(r => r.Direction == CashDirection.Out).Sum(r => r.Amount),
                g.Count()));
    }

    public static async Task<decimal> ExpectedAsync(CommercialDbContext db, CashSession session, CancellationToken cancellationToken)
    {
        var total = (await TotalsAsync(db, [session.Id], cancellationToken)).GetValueOrDefault(session.Id);
        return CashMath.Expected(session.OpeningAmount, total.In, total.Out);
    }

    /// <summary>
    /// Bloquea la fila de la sesión. Los cobros en efectivo, los reversos, los movimientos manuales y el cierre pasan todos
    /// por aquí: así un cobro no puede entrar mientras se cierra la caja y el saldo esperado no cambia durante el arqueo.
    /// </summary>
    public static async Task LockSessionAsync(CommercialDbContext db, Guid sessionId, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM cash.cash_sessions WHERE id = {sessionId} FOR UPDATE", cancellationToken);

    /// <summary>La sesión abierta del usuario en esa sucursal (o en esa caja), bloqueada y comprobada otra vez ya con el bloqueo.</summary>
    public static async Task<Result<CashSession>> RequireOpenSessionAsync(
        CommercialDbContext db,
        Guid userId,
        Guid? branchId,
        Guid? registerId,
        CancellationToken cancellationToken)
    {
        var id = await db.CashSessions.AsNoTracking()
            .Where(s => s.OpenedBy == userId && s.Status == CashSessionStatus.Open
                && (branchId == null || s.BranchId == branchId) && (registerId == null || s.RegisterId == registerId))
            .OrderBy(s => s.OpenedAt)
            .Select(s => (Guid?)s.Id).FirstOrDefaultAsync(cancellationToken);
        if (id is null)
        {
            return CashErrorsApp.NoOpenSession;
        }

        await LockSessionAsync(db, id.Value, cancellationToken);
        var session = await db.CashSessions.FirstOrDefaultAsync(s => s.Id == id.Value && s.Status == CashSessionStatus.Open, cancellationToken);
        return session is null ? CashErrorsApp.NoOpenSession : session;
    }
}
