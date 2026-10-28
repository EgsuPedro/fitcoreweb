using FitCore.Application.Core;
using FitCore.Commercial.Application.Memberships;
using FitCore.Commercial.Application.Settings;
using FitCore.Commercial.Domain;
using FitCore.Commercial.Persistence;
using FitCore.Platform.Security;
using FitCore.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace FitCore.Commercial.Application.Portal;

// Portal del alumno: todo se resuelve desde la sesión (el usuario vinculado a la ficha). Ningún identificador que llegue del
// cliente decide de quién son los datos: aquí no hay ninguno.

public sealed record MyMemberDto(
    Guid Id,
    string Code,
    string FirstName,
    string LastName,
    Guid BranchId,
    string BranchName,
    string BranchTimeZone,
    string? Email,
    string? Phone,
    string Status,
    bool WhatsAppConsent,
    bool HasQrToken);

public sealed record MyMembershipDto(
    Guid Id,
    string PlanName,
    string? AllowedAccess,
    DateOnly StartDate,
    DateOnly EndDate,
    string Status,
    decimal AgreedPrice,
    decimal? Balance,
    DateOnly? DueDate,
    string? ObligationStatus,
    int RemainingFreezeDays);

public sealed record MyPaymentDto(Guid Id, string ReceiptNumber, DateTimeOffset PaidAt, string Method, decimal Amount, string Status);

public sealed record MyAttendanceDto(Guid Id, string BranchName, DateTimeOffset CheckInAt, DateTimeOffset? CheckOutAt, int? DurationMinutes);

public sealed record GetMyMemberQuery : IQuery<MyMemberDto>;

public sealed record ListMyMembershipsQuery(PageRequest Page) : IQuery<PagedResult<MyMembershipDto>>;

public sealed record ListMyPaymentsQuery(PageRequest Page) : IQuery<PagedResult<MyPaymentDto>>;

public sealed record ListMyAttendanceQuery(PageRequest Page) : IQuery<PagedResult<MyAttendanceDto>>;

public static class PortalErrors
{
    public static readonly Error NoMemberLinked = Error.NotFound("portal.no_member_linked", "Su usuario no está vinculado a ninguna ficha de alumno.");
}

internal static class PortalMember
{
    /// <summary>La ficha del usuario autenticado, o un error si su cuenta no está vinculada a un alumno.</summary>
    public static async Task<Result<Member>> ResolveAsync(CommercialDbContext db, IAccessContext access, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var member = await db.Members.AsNoTracking().FirstOrDefaultAsync(m => m.UserId == actor.Value.UserId, cancellationToken);
        return member is null ? PortalErrors.NoMemberLinked : member;
    }
}

internal sealed class GetMyMemberHandler(CommercialDbContext db, IAccessContext access) : IQueryHandler<GetMyMemberQuery, MyMemberDto>
{
    public async Task<Result<MyMemberDto>> HandleAsync(GetMyMemberQuery query, CancellationToken cancellationToken)
    {
        var member = await PortalMember.ResolveAsync(db, access, cancellationToken);
        if (member.IsFailure)
        {
            return member.Error;
        }

        var m = member.Value;
        var branch = await db.BranchRefs.AsNoTracking().Where(b => b.Id == m.BranchId).Select(b => new { b.Name, b.TimeZone }).FirstOrDefaultAsync(cancellationToken);
        return new MyMemberDto(m.Id, m.Code, m.FirstName, m.LastName, m.BranchId, branch?.Name ?? string.Empty, branch?.TimeZone ?? string.Empty, m.Email, m.Phone, m.Status.ToString(), m.WhatsAppConsent, m.QrTokenHash != null);
    }
}

internal sealed class ListMyMembershipsHandler(CommercialDbContext db, IAccessContext access, IClock clock)
    : IQueryHandler<ListMyMembershipsQuery, PagedResult<MyMembershipDto>>
{
    public async Task<Result<PagedResult<MyMembershipDto>>> HandleAsync(ListMyMembershipsQuery query, CancellationToken cancellationToken)
    {
        var member = await PortalMember.ResolveAsync(db, access, cancellationToken);
        if (member.IsFailure)
        {
            return member.Error;
        }

        var today = LocalDates.Today(clock);
        var paged = await db.Memberships.AsNoTracking()
            .Where(m => m.MemberId == member.Value.Id)
            .OrderByDescending(m => m.StartDate).ThenByDescending(m => m.CreatedAt)
            .ToPagedAsync(query.Page, cancellationToken);

        // Mismo cálculo (estado derivado, saldo) que la vista del personal; aquí se recorta a lo que le sirve al alumno.
        var dtos = await MembershipQueries.ToDtosAsync(db, paged.Items, today, cancellationToken);
        var items = dtos.Select(d => new MyMembershipDto(
            d.Id, d.PlanName, d.AllowedAccess, d.StartDate, d.EndDate, d.Status, d.AgreedPrice,
            d.Obligation?.Balance, d.Obligation?.DueDate, d.Obligation?.Status, d.RemainingFreezeDays));
        return new PagedResult<MyMembershipDto>([.. items], paged.Page, paged.PageSize, paged.TotalCount);
    }
}

internal sealed class ListMyPaymentsHandler(CommercialDbContext db, IAccessContext access)
    : IQueryHandler<ListMyPaymentsQuery, PagedResult<MyPaymentDto>>
{
    public async Task<Result<PagedResult<MyPaymentDto>>> HandleAsync(ListMyPaymentsQuery query, CancellationToken cancellationToken)
    {
        var member = await PortalMember.ResolveAsync(db, access, cancellationToken);
        if (member.IsFailure)
        {
            return member.Error;
        }

        var paged = await db.Payments.AsNoTracking()
            .Where(p => p.MemberId == member.Value.Id)
            .OrderByDescending(p => p.PaidAt).ThenBy(p => p.Id)
            .ToPagedAsync(query.Page, cancellationToken);
        var items = paged.Items.Select(p => new MyPaymentDto(p.Id, p.ReceiptNumber, p.PaidAt, p.Method.ToString(), p.Amount, p.Status.ToString()));
        return new PagedResult<MyPaymentDto>([.. items], paged.Page, paged.PageSize, paged.TotalCount);
    }
}

internal sealed class ListMyAttendanceHandler(CommercialDbContext db, IAccessContext access)
    : IQueryHandler<ListMyAttendanceQuery, PagedResult<MyAttendanceDto>>
{
    public async Task<Result<PagedResult<MyAttendanceDto>>> HandleAsync(ListMyAttendanceQuery query, CancellationToken cancellationToken)
    {
        var member = await PortalMember.ResolveAsync(db, access, cancellationToken);
        if (member.IsFailure)
        {
            return member.Error;
        }

        var paged = await db.Attendance.AsNoTracking()
            .Where(a => a.MemberId == member.Value.Id)
            .OrderByDescending(a => a.CheckInAt).ThenBy(a => a.Id)
            .ToPagedAsync(query.Page, cancellationToken);
        var branchIds = paged.Items.Select(a => a.BranchId).Distinct().ToList();
        var branches = await db.BranchRefs.AsNoTracking().Where(b => branchIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, b => b.Name, cancellationToken);
        var items = paged.Items.Select(a => new MyAttendanceDto(
            a.Id,
            branches.GetValueOrDefault(a.BranchId, string.Empty),
            a.CheckInAt,
            a.CheckOutAt,
            a.CheckOutAt is { } end ? (int)Math.Max(0, (end - a.CheckInAt).TotalMinutes) : null));
        return new PagedResult<MyAttendanceDto>([.. items], paged.Page, paged.PageSize, paged.TotalCount);
    }
}
