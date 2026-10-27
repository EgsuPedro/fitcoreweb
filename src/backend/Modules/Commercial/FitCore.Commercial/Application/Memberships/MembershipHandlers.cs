using FitCore.Application.Core;
using FitCore.Commercial.Application.Members;
using FitCore.Commercial.Application.Plans;
using FitCore.Commercial.Application.Settings;
using FitCore.Commercial.Domain;
using FitCore.Commercial.Persistence;
using FitCore.Platform.Application;
using FitCore.Platform.Security;
using FitCore.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace FitCore.Commercial.Application.Memberships;

// ---- Consultas -----------------------------------------------------------------------------------------------------

internal sealed class ListMembershipsHandler(CommercialDbContext db, IAccessContext access, IClock clock)
    : IQueryHandler<ListMembershipsQuery, PagedResult<MembershipDto>>
{
    public async Task<Result<PagedResult<MembershipDto>>> HandleAsync(ListMembershipsQuery query, CancellationToken cancellationToken)
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

        var today = LocalDates.Today(clock);
        var memberships = db.Memberships.AsNoTracking().VisibleTo(actor.Value);
        if (query.MemberId is { } memberId)
        {
            memberships = memberships.Where(m => m.MemberId == memberId);
        }

        if (query.BranchId is { } branchId)
        {
            memberships = memberships.Where(m => m.BranchId == branchId);
        }

        if (query.PlanId is { } planId)
        {
            memberships = memberships.Where(m => m.PlanId == planId);
        }

        if (query.Status is { } status)
        {
            memberships = memberships.WithEffectiveStatus(status, today);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var pattern = LikePattern.Contains(query.Search);
            memberships = memberships.Where(m => db.Members.Any(x =>
                x.Id == m.MemberId
                && (EF.Functions.ILike(x.FirstName + " " + x.LastName, pattern) || EF.Functions.ILike(x.Code, pattern) || EF.Functions.ILike(x.IdNumber, pattern))));
        }

        var paged = await memberships.OrderByDescending(m => m.StartDate).ThenByDescending(m => m.CreatedAt).ToPagedAsync(query.Page, cancellationToken);
        return new PagedResult<MembershipDto>(await MembershipQueries.ToDtosAsync(db, paged.Items, today, cancellationToken), paged.Page, paged.PageSize, paged.TotalCount);
    }
}

internal sealed class ExpiringMembershipsHandler(CommercialDbContext db, IAccessContext access, IClock clock)
    : IQueryHandler<ExpiringMembershipsQuery, PagedResult<MembershipDto>>
{
    public async Task<Result<PagedResult<MembershipDto>>> HandleAsync(ExpiringMembershipsQuery query, CancellationToken cancellationToken)
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

        var today = LocalDates.Today(clock);
        var limit = today.AddDays(Math.Clamp(query.Days, 0, 90));
        var memberships = db.Memberships.AsNoTracking().VisibleTo(actor.Value)
            .Where(m => m.Status == MembershipStatus.Active && m.EndDate >= today && m.EndDate <= limit);
        if (query.BranchId is { } branchId)
        {
            memberships = memberships.Where(m => m.BranchId == branchId);
        }

        var paged = await memberships.OrderBy(m => m.EndDate).ThenBy(m => m.CreatedAt).ToPagedAsync(query.Page, cancellationToken);
        return new PagedResult<MembershipDto>(await MembershipQueries.ToDtosAsync(db, paged.Items, today, cancellationToken), paged.Page, paged.PageSize, paged.TotalCount);
    }
}

internal sealed class GetMembershipHandler(CommercialDbContext db, IAccessContext access, IClock clock) : IQueryHandler<GetMembershipQuery, MembershipDto>
{
    public async Task<Result<MembershipDto>> HandleAsync(GetMembershipQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var membership = await db.Memberships.AsNoTracking().VisibleTo(actor.Value).FirstOrDefaultAsync(m => m.Id == query.Id, cancellationToken);
        return membership is null
            ? MembershipErrorsApp.NotFound
            : await MembershipQueries.ToDtoAsync(db, membership, LocalDates.Today(clock), cancellationToken);
    }
}

internal sealed class ListMembershipEventsHandler(CommercialDbContext db, IAccessContext access)
    : IQueryHandler<ListMembershipEventsQuery, IReadOnlyList<MembershipEventDto>>
{
    public async Task<Result<IReadOnlyList<MembershipEventDto>>> HandleAsync(ListMembershipEventsQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        if (!await db.Memberships.AsNoTracking().VisibleTo(actor.Value).AnyAsync(m => m.Id == query.Id, cancellationToken))
        {
            return MembershipErrorsApp.NotFound;
        }

        var events = await db.MembershipEvents.AsNoTracking().Where(e => e.MembershipId == query.Id)
            .OrderBy(e => e.OccurredAt).ThenBy(e => e.Id).ToListAsync(cancellationToken);
        return Result.Success<IReadOnlyList<MembershipEventDto>>([.. events.Select(MembershipEventDto.From)]);
    }
}

// ---- Inscripción y renovación --------------------------------------------------------------------------------------

internal sealed class EnrollHandler(CommercialDbContext db, IAccessContext access, IClock clock, ICurrentUser currentUser)
    : ICommandHandler<EnrollCommand, MembershipDto>
{
    public async Task<Result<MembershipDto>> HandleAsync(EnrollCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var request = command.Request;
        var member = await db.Members.VisibleTo(actor.Value).FirstOrDefaultAsync(m => m.Id == request.MemberId, cancellationToken);
        if (member is null)
        {
            return MembershipErrorsApp.MemberNotFound;
        }

        var plan = await db.Plans.Include(p => p.Branches).VisibleTo(actor.Value).FirstOrDefaultAsync(p => p.Id == request.PlanId, cancellationToken);
        if (plan is null)
        {
            return MembershipErrorsApp.PlanNotFound;
        }

        var created = await EnrollmentWorkflow.RunAsync(
            db,
            clock,
            currentUser,
            actor.Value,
            new EnrollmentInput(member, plan, request.StartDate, request.PromotionId, request.PromotionCode, request.DueDate, null),
            cancellationToken);
        if (created.IsFailure)
        {
            return created.Error;
        }

        return await MembershipQueries.ToDtoAsync(db, created.Value, LocalDates.Today(clock), cancellationToken);
    }
}

internal sealed class RenewHandler(CommercialDbContext db, IAccessContext access, IClock clock, ICurrentUser currentUser)
    : ICommandHandler<RenewCommand, MembershipDto>
{
    public async Task<Result<MembershipDto>> HandleAsync(RenewCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var source = await db.Memberships.VisibleTo(actor.Value).FirstOrDefaultAsync(m => m.Id == command.Id, cancellationToken);
        if (source is null)
        {
            return MembershipErrorsApp.NotFound;
        }

        var timeZone = await MembershipQueries.BranchTimeZoneAsync(db, source.BranchId, cancellationToken);
        var today = LocalDates.Today(clock, timeZone);
        var effective = source.EffectiveStatus(today);
        if (effective == MembershipStatus.Cancelled)
        {
            return MembershipErrorsApp.CannotRenew;
        }

        if (effective == MembershipStatus.Frozen)
        {
            return MembershipErrorsApp.RenewFrozen;
        }

        if (effective == MembershipStatus.PendingPayment)
        {
            return MembershipErrorsApp.RenewPending;
        }

        if (await db.Memberships.AnyAsync(m => m.RenewedFromId == source.Id && m.Status != MembershipStatus.Cancelled, cancellationToken))
        {
            return MembershipErrorsApp.AlreadyRenewed;
        }

        var member = await db.Members.VisibleTo(actor.Value).FirstOrDefaultAsync(m => m.Id == source.MemberId, cancellationToken);
        if (member is null)
        {
            return MembershipErrorsApp.MemberNotFound;
        }

        var request = command.Request;
        var planId = request.PlanId ?? source.PlanId;
        var plan = await db.Plans.Include(p => p.Branches).VisibleTo(actor.Value).FirstOrDefaultAsync(p => p.Id == planId, cancellationToken);
        if (plan is null)
        {
            return MembershipErrorsApp.PlanNotFound;
        }

        // La renovación empieza al terminar la actual (o hoy, si ya venció): la membresía existente no cambia de fechas.
        var start = source.EndDate >= today ? source.EndDate.AddDays(1) : today;
        var created = await EnrollmentWorkflow.RunAsync(
            db,
            clock,
            currentUser,
            actor.Value,
            new EnrollmentInput(member, plan, start, request.PromotionId, request.PromotionCode, request.DueDate, source),
            cancellationToken);
        if (created.IsFailure)
        {
            return created.Error;
        }

        return await MembershipQueries.ToDtoAsync(db, created.Value, today, cancellationToken);
    }
}

// ---- Congelar, reactivar y cancelar --------------------------------------------------------------------------------

internal sealed class FreezeHandler(CommercialDbContext db, IAccessContext access, IClock clock, ICurrentUser currentUser)
    : ICommandHandler<FreezeCommand, MembershipDto>
{
    public async Task<Result<MembershipDto>> HandleAsync(FreezeCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var membership = await db.Memberships.VisibleTo(actor.Value).FirstOrDefaultAsync(m => m.Id == command.Id, cancellationToken);
        if (membership is null)
        {
            return MembershipErrorsApp.NotFound;
        }

        var today = LocalDates.Today(clock, await MembershipQueries.BranchTimeZoneAsync(db, membership.BranchId, cancellationToken));

        // Al reactivar, el fin se alarga: si ya hay una renovación programada se solaparían. Hay que resolverla antes.
        if (await db.Memberships.AnyAsync(m => m.MemberId == membership.MemberId && m.Id != membership.Id && m.Status != MembershipStatus.Cancelled && m.StartDate > membership.EndDate, cancellationToken))
        {
            return MembershipErrorsApp.FreezeBlockedByRenewal;
        }

        var frozen = membership.Freeze(today, currentUser.UserId, clock.UtcNow);
        if (frozen.IsFailure)
        {
            return frozen.Error;
        }

        db.MembershipEvents.Add(MembershipQueries.NewEvent(membership, MembershipEventType.Frozen, currentUser.UserId, clock.UtcNow, new
        {
            from = today,
            reason = string.IsNullOrWhiteSpace(command.Request.Reason) ? null : command.Request.Reason.Trim(),
            remainingFreezeDays = membership.RemainingFreezeDays
        }));
        await db.SaveChangesAsync(cancellationToken);
        return await MembershipQueries.ToDtoAsync(db, membership, today, cancellationToken);
    }
}

internal sealed class ReactivateHandler(CommercialDbContext db, IAccessContext access, IClock clock, ICurrentUser currentUser)
    : ICommandHandler<ReactivateCommand, MembershipDto>
{
    public async Task<Result<MembershipDto>> HandleAsync(ReactivateCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var membership = await db.Memberships.VisibleTo(actor.Value).FirstOrDefaultAsync(m => m.Id == command.Id, cancellationToken);
        if (membership is null)
        {
            return MembershipErrorsApp.NotFound;
        }

        var today = LocalDates.Today(clock, await MembershipQueries.BranchTimeZoneAsync(db, membership.BranchId, cancellationToken));
        var oldEnd = membership.EndDate;
        var frozenSince = membership.FreezeStartedOn;
        var granted = membership.Reactivate(today, currentUser.UserId, clock.UtcNow);
        if (granted.IsFailure)
        {
            return granted.Error;
        }

        // El cambio de fecha queda a la vista: fechas anterior y nueva, días congelados y días otorgados.
        db.MembershipEvents.Add(MembershipQueries.NewEvent(membership, MembershipEventType.Reactivated, currentUser.UserId, clock.UtcNow, new
        {
            frozenSince,
            frozenDays = frozenSince is { } since ? Math.Max(0, today.DayNumber - since.DayNumber) : 0,
            grantedDays = granted.Value,
            oldEnd,
            newEnd = membership.EndDate
        }));
        await db.SaveChangesAsync(cancellationToken);
        return await MembershipQueries.ToDtoAsync(db, membership, today, cancellationToken);
    }
}

internal sealed class CancelHandler(CommercialDbContext db, IAccessContext access, IClock clock, ICurrentUser currentUser)
    : ICommandHandler<CancelCommand, MembershipDto>
{
    public async Task<Result<MembershipDto>> HandleAsync(CancelCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var membership = await db.Memberships.VisibleTo(actor.Value).FirstOrDefaultAsync(m => m.Id == command.Id, cancellationToken);
        if (membership is null)
        {
            return MembershipErrorsApp.NotFound;
        }

        var now = clock.UtcNow;
        var today = LocalDates.Today(clock, await MembershipQueries.BranchTimeZoneAsync(db, membership.BranchId, cancellationToken));
        var cancelled = membership.Cancel(command.Request.Reason, today, currentUser.UserId, now);
        if (cancelled.IsFailure)
        {
            return cancelled.Error;
        }

        // Una obligación sin ningún pago se anula junto con la membresía. (Con pagos, el bloque de cobros decide: los
        // pagos se corrigen con reversos, nunca borrándolos.)
        var voided = new List<string>();
        var pending = await db.Obligations.Where(o => o.MembershipId == membership.Id && o.Status == ObligationStatus.Pending).ToListAsync(cancellationToken);
        foreach (var obligation in pending)
        {
            var result = obligation.Void($"Membresía cancelada: {command.Request.Reason.Trim()}", currentUser.UserId, now);
            if (result.IsSuccess)
            {
                voided.Add(obligation.Reference);
            }
        }

        db.MembershipEvents.Add(MembershipQueries.NewEvent(membership, MembershipEventType.Cancelled, currentUser.UserId, now, new
        {
            reason = command.Request.Reason.Trim(),
            voidedObligations = voided
        }));
        await db.SaveChangesAsync(cancellationToken);
        return await MembershipQueries.ToDtoAsync(db, membership, today, cancellationToken);
    }
}
