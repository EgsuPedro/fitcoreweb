using FitCore.Application.Core;
using FitCore.Commercial.Application.Members;
using FitCore.Commercial.Application.Memberships;
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

namespace FitCore.Commercial.Application.Attendance;

// ---- Validar acceso (no registra nada) -----------------------------------------------------------------------------

internal sealed class ValidateAccessHandler(CommercialDbContext db, IAccessContext access, IClock clock)
    : IQueryHandler<ValidateAccessQuery, AccessDecisionDto>
{
    public async Task<Result<AccessDecisionDto>> HandleAsync(ValidateAccessQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var request = query.Request;
        var branch = await db.ResolveBranchAsync(actor.Value, request.BranchId, cancellationToken);
        if (branch.IsFailure)
        {
            return branch.Error;
        }

        var member = await MemberIdentity.ResolveAsync(db, request.MemberId, request.MemberCode, request.QrToken, cancellationToken);
        if (member.IsFailure)
        {
            return member.Error;
        }

        var today = LocalDates.Today(clock, branch.Value.TimeZone);
        var evaluation = await AccessEvaluator.EvaluateAsync(db, member.Value, branch.Value.Id, today, cancellationToken);
        var settings = await SettingsStore.GetAsync(db, member.Value.TenantId, cancellationToken);
        var inside = await db.Attendance.AsNoTracking().AnyAsync(a => a.MemberId == member.Value.Id && a.CheckOutAt == null, cancellationToken);

        return new AccessDecisionDto(
            evaluation.Allowed && !inside,
            evaluation.Denial?.Code ?? (inside ? AttendanceErrorsApp.AlreadyCheckedIn.Code : null),
            evaluation.Denial?.Message ?? (inside ? AttendanceErrorsApp.AlreadyCheckedIn.Description : null),
            evaluation.Denial?.Overridable ?? false,
            inside,
            member.Value.Id,
            member.Value.Code,
            member.Value.FullName,
            member.Value.Status.ToString(),
            evaluation.Membership?.Id,
            evaluation.PlanName,
            evaluation.Membership?.EndDate,
            evaluation.Owed,
            settings.AccessPolicy.ToString());
    }
}

// ---- Entrada -------------------------------------------------------------------------------------------------------

internal sealed class CheckInHandler(CommercialDbContext db, PlatformDbContext platformDb, IAccessContext access, IClock clock, ICurrentUser currentUser)
    : ICommandHandler<CheckInCommand, AttendanceDto>
{
    private const string OpenEntryIndex = "ux_attendance_open_member";

    public async Task<Result<AttendanceDto>> HandleAsync(CheckInCommand command, CancellationToken cancellationToken)
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

        var member = await MemberIdentity.ResolveAsync(db, request.MemberId, request.MemberCode, request.QrToken, cancellationToken);
        if (member.IsFailure)
        {
            return member.Error;
        }

        // Una entrada abierta impide otra del mismo alumno (la base lo garantiza también ante solicitudes simultáneas).
        var open = await db.Attendance.AsNoTracking().FirstOrDefaultAsync(a => a.MemberId == member.Value.Id && a.CheckOutAt == null, cancellationToken);
        if (open is not null)
        {
            return open.BranchId == branch.Value.Id ? AttendanceErrorsApp.AlreadyCheckedIn : AttendanceErrorsApp.OpenEntryElsewhere;
        }

        var now = clock.UtcNow; // la hora siempre es la del servidor
        var today = LocalDates.Today(clock, branch.Value.TimeZone);
        var evaluation = await AccessEvaluator.EvaluateAsync(db, member.Value, branch.Value.Id, today, cancellationToken);

        string? exceptionReason = null;
        if (!evaluation.Allowed)
        {
            var denial = evaluation.Denial!;
            if (string.IsNullOrWhiteSpace(request.OverrideReason) || !denial.Overridable)
            {
                return denial.ToError();
            }

            // Solo quien tiene el permiso autoriza una excepción, y deja el motivo (queda auditado).
            if (!actor.Value.Has(Permissions.AttendanceOverride))
            {
                return AttendanceErrorsApp.OverrideNotAllowed;
            }

            exceptionReason = request.OverrideReason;
        }

        var method = string.IsNullOrWhiteSpace(request.QrToken) ? AttendanceMethod.Manual : AttendanceMethod.Qr;
        var entry = AttendanceEntry.CheckIn(
            actor.Value.TenantId,
            branch.Value.Id,
            member.Value.Id,
            evaluation.Membership?.Id,
            method,
            exceptionReason,
            currentUser.UserId!.Value,
            now);
        db.Attendance.Add(entry);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg && pg.ConstraintName == OpenEntryIndex)
        {
            return AttendanceErrorsApp.AlreadyCheckedIn;
        }

        return await AttendanceQueries.ToDtoAsync(db, platformDb, entry, cancellationToken);
    }
}

// ---- Salida --------------------------------------------------------------------------------------------------------

internal sealed class CheckOutHandler(CommercialDbContext db, PlatformDbContext platformDb, IAccessContext access, IClock clock, ICurrentUser currentUser)
    : ICommandHandler<CheckOutCommand, AttendanceDto>
{
    public async Task<Result<AttendanceDto>> HandleAsync(CheckOutCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var request = command.Request;
        var member = await MemberIdentity.ResolveAsync(db, request.MemberId, request.MemberCode, request.QrToken, cancellationToken);
        if (member.IsFailure)
        {
            return member.Error;
        }

        // La salida solo aplica a una entrada abierta de una sucursal a la que el usuario tiene acceso.
        var entry = await db.Attendance.VisibleTo(actor.Value).FirstOrDefaultAsync(a => a.MemberId == member.Value.Id && a.CheckOutAt == null, cancellationToken);
        if (entry is null)
        {
            return AttendanceErrors.NoOpenEntry;
        }

        var method = string.IsNullOrWhiteSpace(request.QrToken) ? AttendanceMethod.Manual : AttendanceMethod.Qr;
        var result = entry.CheckOut(method, currentUser.UserId!.Value, clock.UtcNow);
        if (result.IsFailure)
        {
            return result.Error;
        }

        await db.SaveChangesAsync(cancellationToken);
        return await AttendanceQueries.ToDtoAsync(db, platformDb, entry, cancellationToken);
    }
}

// ---- Consultas -----------------------------------------------------------------------------------------------------

internal sealed class ListAttendanceHandler(CommercialDbContext db, PlatformDbContext platformDb, IAccessContext access)
    : IQueryHandler<ListAttendanceQuery, PagedResult<AttendanceDto>>
{
    private static readonly TimeZoneInfo Ecuador = TimeZoneInfo.FindSystemTimeZoneById(Tenant.DefaultTimeZone);

    public async Task<Result<PagedResult<AttendanceDto>>> HandleAsync(ListAttendanceQuery query, CancellationToken cancellationToken)
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

        var entries = db.Attendance.AsNoTracking().VisibleTo(actor.Value);
        if (query.MemberId is { } memberId)
        {
            entries = entries.Where(a => a.MemberId == memberId);
        }

        if (query.BranchId is { } branchId)
        {
            entries = entries.Where(a => a.BranchId == branchId);
        }

        if (query.Method is { } method)
        {
            entries = entries.Where(a => a.Method == method);
        }

        if (query.Open is { } open)
        {
            entries = open ? entries.Where(a => a.CheckOutAt == null) : entries.Where(a => a.CheckOutAt != null);
        }

        if (query.Exception is { } exception)
        {
            entries = entries.Where(a => a.IsException == exception);
        }

        if (query.From is { } from)
        {
            var start = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), Ecuador.GetUtcOffset(from.ToDateTime(TimeOnly.MinValue))).ToUniversalTime();
            entries = entries.Where(a => a.CheckInAt >= start);
        }

        if (query.To is { } to)
        {
            var end = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), Ecuador.GetUtcOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue))).ToUniversalTime();
            entries = entries.Where(a => a.CheckInAt < end);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var pattern = LikePattern.Contains(query.Search);
            entries = entries.Where(a => db.Members.Any(m =>
                m.Id == a.MemberId && (EF.Functions.ILike(m.FirstName + " " + m.LastName, pattern) || EF.Functions.ILike(m.Code, pattern) || EF.Functions.ILike(m.IdNumber, pattern))));
        }

        var paged = await entries.OrderByDescending(a => a.CheckInAt).ThenByDescending(a => a.Id).ToPagedAsync(query.Page, cancellationToken);
        return new PagedResult<AttendanceDto>(await AttendanceQueries.ToDtosAsync(db, platformDb, paged.Items, cancellationToken), paged.Page, paged.PageSize, paged.TotalCount);
    }
}

internal sealed class GetAttendanceHandler(CommercialDbContext db, PlatformDbContext platformDb, IAccessContext access) : IQueryHandler<GetAttendanceQuery, AttendanceDto>
{
    public async Task<Result<AttendanceDto>> HandleAsync(GetAttendanceQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var entry = await db.Attendance.AsNoTracking().VisibleTo(actor.Value).FirstOrDefaultAsync(a => a.Id == query.Id, cancellationToken);
        return entry is null ? AttendanceErrorsApp.NotFound : await AttendanceQueries.ToDtoAsync(db, platformDb, entry, cancellationToken);
    }
}

// ---- Código QR del alumno ------------------------------------------------------------------------------------------

internal sealed class IssueMemberQrHandler(CommercialDbContext db, IAccessContext access, IClock clock, ICurrentUser currentUser)
    : ICommandHandler<IssueMemberQrCommand, QrTokenDto>
{
    public async Task<Result<QrTokenDto>> HandleAsync(IssueMemberQrCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var member = await db.Members.VisibleTo(actor.Value).FirstOrDefaultAsync(m => m.Id == command.MemberId, cancellationToken);
        if (member is null)
        {
            return MembershipErrorsApp.MemberNotFound;
        }

        // El token se muestra una sola vez: en la base solo queda su huella, y emitir uno nuevo invalida el anterior.
        var token = QrTokens.Generate();
        var now = clock.UtcNow;
        member.IssueQrToken(QrTokens.Hash(token), currentUser.UserId, now);
        await db.SaveChangesAsync(cancellationToken);
        return new QrTokenDto(token, now);
    }
}
