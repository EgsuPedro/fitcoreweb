using FitCore.Application.Core;
using FitCore.Commercial.Application.Billing;
using FitCore.Commercial.Application.Settings;
using FitCore.Commercial.Domain;
using FitCore.Commercial.Persistence;
using FitCore.Platform.Application;
using FitCore.Platform.Domain;
using FitCore.Platform.Persistence;
using FitCore.Platform.Security;
using FitCore.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FitCore.Commercial.Application.Cash;

// ---- Consultas -----------------------------------------------------------------------------------------------------

internal sealed class ListCashRegistersHandler(CommercialDbContext db, PlatformDbContext platformDb, IAccessContext access)
    : IQueryHandler<ListCashRegistersQuery, PagedResult<CashRegisterDto>>
{
    public async Task<Result<PagedResult<CashRegisterDto>>> HandleAsync(ListCashRegistersQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        if (query.BranchId is { } requested && !actor.Value.CanAccessBranch(requested))
        {
            return PlatformErrors.BranchNotAllowed;
        }

        var registers = db.CashRegisters.AsNoTracking().VisibleTo(actor.Value);
        if (query.BranchId is { } branchId)
        {
            registers = registers.Where(r => r.BranchId == branchId);
        }

        if (query.IsActive is { } isActive)
        {
            registers = registers.Where(r => r.IsActive == isActive);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            registers = registers.Where(r => EF.Functions.ILike(r.Name, LikePattern.Contains(query.Search)));
        }

        var paged = await registers.OrderBy(r => r.BranchId).ThenBy(r => r.Name).ToPagedAsync(query.Page, cancellationToken);
        var registerIds = paged.Items.Select(r => r.Id).ToList();
        var openSessions = await db.CashSessions.AsNoTracking().Where(s => registerIds.Contains(s.RegisterId) && s.Status == CashSessionStatus.Open)
            .OrderBy(s => s.OpenedAt).ToListAsync(cancellationToken);
        var sessionDtos = (await CashQueries.ToDtosAsync(db, platformDb, openSessions, cancellationToken)).ToLookup(s => s.RegisterId);
        var branchIds = paged.Items.Select(r => r.BranchId).Distinct().ToList();
        var branches = await db.BranchRefs.AsNoTracking().Where(b => branchIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, cancellationToken);

        return new PagedResult<CashRegisterDto>(
            [.. paged.Items.Select(r => ToDto(r, branches[r.BranchId], [.. sessionDtos[r.Id]]))],
            paged.Page,
            paged.PageSize,
            paged.TotalCount);
    }

    internal static CashRegisterDto ToDto(CashRegister r, BranchRef branch, IReadOnlyList<CashSessionDto> openSessions) =>
        new(r.Id, r.BranchId, branch.Code, branch.Name, r.Name, r.IsActive, r.AllowMultipleOpenSessions, openSessions, r.CreatedAt, r.CreatedBy, r.UpdatedAt);
}

internal sealed class ListCashSessionsHandler(CommercialDbContext db, PlatformDbContext platformDb, IAccessContext access)
    : IQueryHandler<ListCashSessionsQuery, PagedResult<CashSessionDto>>
{
    private static readonly TimeZoneInfo Ecuador = TimeZoneInfo.FindSystemTimeZoneById(Tenant.DefaultTimeZone);

    public async Task<Result<PagedResult<CashSessionDto>>> HandleAsync(ListCashSessionsQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        if (!await db.CashRegisters.AsNoTracking().VisibleTo(actor.Value).AnyAsync(r => r.Id == query.RegisterId, cancellationToken))
        {
            return CashErrorsApp.RegisterNotFound;
        }

        var sessions = db.CashSessions.AsNoTracking().VisibleTo(actor.Value).Where(s => s.RegisterId == query.RegisterId);
        if (query.Status is { } status)
        {
            sessions = sessions.Where(s => s.Status == status);
        }

        if (query.From is { } from)
        {
            var start = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), Ecuador.GetUtcOffset(from.ToDateTime(TimeOnly.MinValue))).ToUniversalTime();
            sessions = sessions.Where(s => s.OpenedAt >= start);
        }

        if (query.To is { } to)
        {
            var end = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), Ecuador.GetUtcOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue))).ToUniversalTime();
            sessions = sessions.Where(s => s.OpenedAt < end);
        }

        var paged = await sessions.OrderByDescending(s => s.OpenedAt).ToPagedAsync(query.Page, cancellationToken);
        return new PagedResult<CashSessionDto>(await CashQueries.ToDtosAsync(db, platformDb, paged.Items, cancellationToken), paged.Page, paged.PageSize, paged.TotalCount);
    }
}

internal sealed class GetCashSessionHandler(CommercialDbContext db, PlatformDbContext platformDb, IAccessContext access)
    : IQueryHandler<GetCashSessionQuery, CashSessionDto>
{
    public async Task<Result<CashSessionDto>> HandleAsync(GetCashSessionQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var session = await db.CashSessions.AsNoTracking().VisibleTo(actor.Value)
            .FirstOrDefaultAsync(s => s.Id == query.SessionId && s.RegisterId == query.RegisterId, cancellationToken);
        return session is null ? CashErrorsApp.SessionNotFound : await CashQueries.ToDtoAsync(db, platformDb, session, cancellationToken);
    }
}

internal sealed class ListCashMovementsHandler(CommercialDbContext db, PlatformDbContext platformDb, IAccessContext access)
    : IQueryHandler<ListCashMovementsQuery, PagedResult<CashMovementDto>>
{
    public async Task<Result<PagedResult<CashMovementDto>>> HandleAsync(ListCashMovementsQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        if (!await db.CashRegisters.AsNoTracking().VisibleTo(actor.Value).AnyAsync(r => r.Id == query.RegisterId, cancellationToken))
        {
            return CashErrorsApp.RegisterNotFound;
        }

        var sessionIds = db.CashSessions.AsNoTracking().VisibleTo(actor.Value).Where(s => s.RegisterId == query.RegisterId).Select(s => s.Id);
        var movements = db.CashMovements.AsNoTracking().Where(m => sessionIds.Contains(m.SessionId));
        if (query.SessionId is { } sessionId)
        {
            movements = movements.Where(m => m.SessionId == sessionId);
        }

        if (query.Type is { } type)
        {
            movements = movements.Where(m => m.Type == type);
        }

        var paged = await movements.OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id).ToPagedAsync(query.Page, cancellationToken);
        return new PagedResult<CashMovementDto>(await ToDtosAsync(db, platformDb, paged.Items, cancellationToken), paged.Page, paged.PageSize, paged.TotalCount);
    }

    internal static async Task<IReadOnlyList<CashMovementDto>> ToDtosAsync(CommercialDbContext db, PlatformDbContext platformDb, IReadOnlyList<CashMovement> movements, CancellationToken cancellationToken)
    {
        var paymentIds = movements.Where(m => m.PaymentId != null).Select(m => m.PaymentId!.Value).Distinct().ToList();
        var receipts = await db.Payments.AsNoTracking().Where(p => paymentIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.ReceiptNumber, cancellationToken);
        var names = await CashQueries.UserNamesAsync(platformDb, movements.Select(m => m.CreatedBy), cancellationToken);
        return
        [
            .. movements.Select(m => new CashMovementDto(
                m.Id,
                m.SessionId,
                m.Type.ToString(),
                m.Direction.ToString(),
                m.Amount,
                m.Reason,
                m.PaymentId,
                m.PaymentId is { } pid ? receipts.GetValueOrDefault(pid) : null,
                m.CreatedAt,
                m.CreatedBy,
                m.CreatedBy is { } uid ? names.GetValueOrDefault(uid) : null))
        ];
    }
}

// ---- Configuración de cajas ----------------------------------------------------------------------------------------

internal sealed class CreateCashRegisterHandler(CommercialDbContext db, IAccessContext access, IClock clock, ICurrentUser currentUser)
    : ICommandHandler<CreateCashRegisterCommand, CashRegisterDto>
{
    public async Task<Result<CashRegisterDto>> HandleAsync(CreateCashRegisterCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var request = command.Request;
        var branch = await db.ResolveBranchAsync(actor.Value, request.BranchId, cancellationToken);
        if (branch.IsFailure)
        {
            return branch.Error;
        }

        if (await db.CashRegisters.AnyAsync(r => r.BranchId == branch.Value.Id && EF.Functions.ILike(r.Name, LikePattern.Exact(request.Name)), cancellationToken))
        {
            return CashErrorsApp.NameTaken;
        }

        var register = CashRegister.Create(actor.Value.TenantId, branch.Value.Id, request.Name, request.AllowMultipleOpenSessions, currentUser.UserId, clock.UtcNow);
        db.CashRegisters.Add(register);
        await db.SaveChangesAsync(cancellationToken);
        return ListCashRegistersHandler.ToDto(register, branch.Value, []);
    }
}

internal sealed class UpdateCashRegisterHandler(CommercialDbContext db, IAccessContext access, IClock clock, ICurrentUser currentUser)
    : ICommandHandler<UpdateCashRegisterCommand, CashRegisterDto>
{
    public async Task<Result<CashRegisterDto>> HandleAsync(UpdateCashRegisterCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var register = await db.CashRegisters.VisibleTo(actor.Value).FirstOrDefaultAsync(r => r.Id == command.Id, cancellationToken);
        if (register is null)
        {
            return CashErrorsApp.RegisterNotFound;
        }

        var request = command.Request;
        if (await db.CashRegisters.AnyAsync(r => r.Id != register.Id && r.BranchId == register.BranchId && EF.Functions.ILike(r.Name, LikePattern.Exact(request.Name)), cancellationToken))
        {
            return CashErrorsApp.NameTaken;
        }

        // Desactivar la caja o volverla exclusiva con sesiones abiertas dejaría turnos huérfanos o incompatibles.
        var openCount = await db.CashSessions.CountAsync(s => s.RegisterId == register.Id && s.Status == CashSessionStatus.Open, cancellationToken);
        if (openCount > 0 && (!request.IsActive || (!request.AllowMultipleOpenSessions && openCount > 1)))
        {
            return CashErrorsApp.HasOpenSessions;
        }

        register.Update(request.Name, request.IsActive, request.AllowMultipleOpenSessions, currentUser.UserId, clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        var branch = await db.BranchRefs.AsNoTracking().FirstAsync(b => b.Id == register.BranchId, cancellationToken);
        return ListCashRegistersHandler.ToDto(register, branch, []);
    }
}

// ---- Apertura, movimientos y cierre --------------------------------------------------------------------------------

internal sealed class OpenCashHandler(CommercialDbContext db, PlatformDbContext platformDb, IAccessContext access, IClock clock, ICurrentUser currentUser)
    : ICommandHandler<OpenCashCommand, CashSessionDto>
{
    private const string Scope = "cash.open";

    public async Task<Result<CashSessionDto>> HandleAsync(OpenCashCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        if (Idempotency.Validate(command.IdempotencyKey) is { } keyError)
        {
            return keyError;
        }

        var userId = currentUser.UserId!.Value;
        var key = command.IdempotencyKey!.Trim();
        var register = await db.CashRegisters.AsNoTracking().VisibleTo(actor.Value).FirstOrDefaultAsync(r => r.Id == command.RegisterId, cancellationToken);
        if (register is null)
        {
            return CashErrorsApp.RegisterNotFound;
        }

        if (!register.IsActive)
        {
            return CashErrorsApp.RegisterInactive;
        }

        var now = clock.UtcNow;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var begin = await Idempotency.BeginAsync<CashSessionDto>(db, actor.Value.TenantId, key, Scope, Idempotency.Hash(new { command.RegisterId, command.Request }, userId), userId, now, cancellationToken);
        if (begin.IsFailure)
        {
            return begin.Error;
        }

        if (begin.Value.IsReplay)
        {
            return begin.Value.Response!;
        }

        if (await db.CashSessions.AnyAsync(s => s.OpenedBy == userId && s.Status == CashSessionStatus.Open, cancellationToken))
        {
            return CashErrorsApp.UserHasOpenSession;
        }

        if (!register.AllowMultipleOpenSessions && await db.CashSessions.AnyAsync(s => s.RegisterId == register.Id && s.Status == CashSessionStatus.Open, cancellationToken))
        {
            return CashErrorsApp.RegisterAlreadyOpen;
        }

        var session = CashSession.Open(actor.Value.TenantId, register, command.Request.OpeningAmount, command.Request.Notes, userId, now);
        db.CashSessions.Add(session);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg)
        {
            // Dos aperturas simultáneas: las restricciones únicas de la base deciden y se traducen a un error claro.
            return pg.ConstraintName == "ux_cash_sessions_open_per_user" ? CashErrorsApp.UserHasOpenSession : CashErrorsApp.RegisterAlreadyOpen;
        }

        var dto = await CashQueries.ToDtoAsync(db, platformDb, session, cancellationToken);
        await Idempotency.CompleteAsync(db, actor.Value.TenantId, key, 200, dto, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return dto;
    }
}

internal sealed class AddCashMovementHandler(CommercialDbContext db, PlatformDbContext platformDb, IAccessContext access, IClock clock, ICurrentUser currentUser)
    : ICommandHandler<AddCashMovementCommand, CashMovementDto>
{
    private const string Scope = "cash.movement";

    public async Task<Result<CashMovementDto>> HandleAsync(AddCashMovementCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        if (Idempotency.Validate(command.IdempotencyKey) is { } keyError)
        {
            return keyError;
        }

        var userId = currentUser.UserId!.Value;
        var key = command.IdempotencyKey!.Trim();
        if (!await db.CashRegisters.AsNoTracking().VisibleTo(actor.Value).AnyAsync(r => r.Id == command.RegisterId, cancellationToken))
        {
            return CashErrorsApp.RegisterNotFound;
        }

        var now = clock.UtcNow;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var begin = await Idempotency.BeginAsync<CashMovementDto>(db, actor.Value.TenantId, key, Scope, Idempotency.Hash(new { command.RegisterId, command.Request }, userId), userId, now, cancellationToken);
        if (begin.IsFailure)
        {
            return begin.Error;
        }

        if (begin.Value.IsReplay)
        {
            return begin.Value.Response!;
        }

        var session = await CashLedger.RequireOpenSessionAsync(db, userId, null, command.RegisterId, cancellationToken);
        if (session.IsFailure)
        {
            return session.Error;
        }

        var request = command.Request;
        if (request.Type == CashMovementType.ManualOut && await CashLedger.ExpectedAsync(db, session.Value, cancellationToken) < Money.Round(request.Amount))
        {
            return CashErrorsApp.InsufficientFunds;
        }

        var movement = CashMovement.Create(actor.Value.TenantId, session.Value, request.Type, request.Amount, request.Reason, null, userId, now);
        db.CashMovements.Add(movement);
        await db.SaveChangesAsync(cancellationToken);

        var dto = (await ListCashMovementsHandler.ToDtosAsync(db, platformDb, [movement], cancellationToken))[0];
        await Idempotency.CompleteAsync(db, actor.Value.TenantId, key, 201, dto, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return dto;
    }
}

internal sealed class CloseCashHandler(CommercialDbContext db, PlatformDbContext platformDb, IAccessContext access, IClock clock, ICurrentUser currentUser)
    : ICommandHandler<CloseCashCommand, CashSessionDto>
{
    private const string Scope = "cash.close";

    public async Task<Result<CashSessionDto>> HandleAsync(CloseCashCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        if (Idempotency.Validate(command.IdempotencyKey) is { } keyError)
        {
            return keyError;
        }

        var userId = currentUser.UserId!.Value;
        var key = command.IdempotencyKey!.Trim();
        if (!await db.CashRegisters.AsNoTracking().VisibleTo(actor.Value).AnyAsync(r => r.Id == command.RegisterId, cancellationToken))
        {
            return CashErrorsApp.RegisterNotFound;
        }

        var now = clock.UtcNow;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var begin = await Idempotency.BeginAsync<CashSessionDto>(db, actor.Value.TenantId, key, Scope, Idempotency.Hash(new { command.RegisterId, command.Request }, userId), userId, now, cancellationToken);
        if (begin.IsFailure)
        {
            return begin.Error;
        }

        if (begin.Value.IsReplay)
        {
            return begin.Value.Response!;
        }

        // Qué sesión se cierra: la indicada (supervisor) o la propia del usuario en esa caja.
        Guid sessionId;
        if (command.Request.SessionId is { } requested)
        {
            var exists = await db.CashSessions.AsNoTracking().VisibleTo(actor.Value)
                .AnyAsync(s => s.Id == requested && s.RegisterId == command.RegisterId, cancellationToken);
            if (!exists)
            {
                return CashErrorsApp.SessionNotFound;
            }

            sessionId = requested;
        }
        else
        {
            var own = await db.CashSessions.AsNoTracking()
                .Where(s => s.RegisterId == command.RegisterId && s.OpenedBy == userId && s.Status == CashSessionStatus.Open)
                .Select(s => (Guid?)s.Id).FirstOrDefaultAsync(cancellationToken);
            if (own is null)
            {
                return CashErrorsApp.NoOpenSession;
            }

            sessionId = own.Value;
        }

        // Con el bloqueo ya no entran cobros ni movimientos: el saldo esperado queda fijo durante el arqueo.
        await CashLedger.LockSessionAsync(db, sessionId, cancellationToken);
        var session = await db.CashSessions.FirstAsync(s => s.Id == sessionId, cancellationToken);
        if (session.Status == CashSessionStatus.Closed)
        {
            return CashErrors.SessionClosed;
        }

        if (session.OpenedBy != userId && !actor.Value.Has(Permissions.CashManage))
        {
            return CashErrorsApp.CloseNotAllowed;
        }

        var expected = await CashLedger.ExpectedAsync(db, session, cancellationToken);
        var closed = session.Close(command.Request.CountedAmount, expected, command.Request.Notes, userId, now);
        if (closed.IsFailure)
        {
            return closed.Error;
        }

        await db.SaveChangesAsync(cancellationToken);
        var dto = await CashQueries.ToDtoAsync(db, platformDb, session, cancellationToken);
        await Idempotency.CompleteAsync(db, actor.Value.TenantId, key, 200, dto, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return dto;
    }
}
