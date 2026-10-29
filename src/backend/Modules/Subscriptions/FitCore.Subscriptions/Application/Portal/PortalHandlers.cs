using FitCore.Application.Core;
using FitCore.SharedKernel;
using FitCore.Subscriptions.Application.Billing;
using FitCore.Subscriptions.Application.Plans;
using FitCore.Subscriptions.Application.Subscriptions;
using FitCore.Subscriptions.Domain;
using FitCore.Subscriptions.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FitCore.Subscriptions.Application.Portal;

internal static class PortalStore
{
    /// <summary>Plan heredado: lo reciben los gimnasios anteriores a los planes comerciales y no se vende ni se pide.</summary>
    public const string LegacyPlanCode = "legacy";

    public static bool IsSellable(Plan plan) => plan.IsActive && plan.Code != LegacyPlanCode;

    /// <summary>La suscripción más reciente del gimnasio (la vigente o, si no hay, la última), sin seguimiento. El filtro y RLS la limitan al gimnasio del token.</summary>
    public static Task<Subscription?> LatestAsync(SubscriptionsDbContext db, CancellationToken cancellationToken) =>
        db.Subscriptions.AsNoTracking()
            .OrderBy(s => s.Status == SubscriptionStatus.Cancelled).ThenByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

    public static async Task<Dictionary<Guid, string>> PlanNamesAsync(SubscriptionsDbContext db, IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        var wanted = ids.Distinct().ToList();
        return wanted.Count == 0
            ? []
            : await db.Plans.AsNoTracking().Where(p => wanted.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, cancellationToken);
    }
}

internal sealed class GetMySubscriptionHandler(SubscriptionsDbContext db, ITenantContext tenantContext, IClock clock) : IQueryHandler<GetMySubscriptionQuery, MySubscriptionDto>
{
    private const int TrialWarningDays = 7;
    private const int DueWarningDays = 7;

    public async Task<Result<MySubscriptionDto>> HandleAsync(GetMySubscriptionQuery query, CancellationToken cancellationToken)
    {
        if (tenantContext.TenantId is not { } tenantId || await PortalStore.LatestAsync(db, cancellationToken) is not { } subscription)
        {
            return CapabilityErrors.None;
        }

        var tenant = await SubscriptionStore.TenantAsync(db, tenantId, cancellationToken);
        var today = SubscriptionStore.Today(clock, tenant?.TimeZone ?? "UTC");
        var accessMode = await db.Entitlements.AsNoTracking().Where(e => e.TenantId == tenantId).Select(e => e.AccessMode).FirstOrDefaultAsync(cancellationToken) ?? "Restricted";

        var pendingPlanName = subscription.PendingPlanId is { } pendingId
            ? (await PortalStore.PlanNamesAsync(db, [pendingId], cancellationToken)).GetValueOrDefault(pendingId)
            : null;

        var open = await db.Invoices.AsNoTracking()
            .Where(i => i.SubscriptionId == subscription.Id && (i.Status == InvoiceStatus.Pending || i.Status == InvoiceStatus.Failed))
            .OrderByDescending(i => i.IssuedAt).FirstOrDefaultAsync(cancellationToken);

        var requests = await db.Requests.AsNoTracking().Where(r => r.Status == SubscriptionRequestStatus.Pending).OrderByDescending(r => r.CreatedAt).ToListAsync(cancellationToken);
        var names = await PortalStore.PlanNamesAsync(db, requests.Where(r => r.RequestedPlanId is not null).Select(r => r.RequestedPlanId!.Value), cancellationToken);
        var unread = await db.Notices.AsNoTracking().CountAsync(n => n.ReadAt == null, cancellationToken);

        return new MySubscriptionDto(
            subscription.Id, subscription.PlanCode, subscription.PlanName, subscription.PlanVersionNumber, subscription.Price, subscription.Currency,
            subscription.Period.ToString(), subscription.Status.ToString(), accessMode, subscription.StartedAt, subscription.CurrentPeriodStart,
            subscription.CurrentPeriodEnd, subscription.TrialEndsOn, subscription.NextDueOn, subscription.GraceEndsOn, subscription.CancelAtPeriodEnd,
            pendingPlanName,
            open is null ? null : new OpenInvoiceDto(open.Id, open.Number, open.Status.ToString(), open.Amount, open.Currency, open.PeriodStart, open.PeriodEnd, open.DueOn, open.DueOn < today),
            Alerts(subscription, accessMode, today),
            [.. requests.Select(r => SubscriptionRequestDto.From(r, r.RequestedPlanId is { } id ? names.GetValueOrDefault(id) : null))],
            unread, subscription.Price == 0);
    }

    /// <summary>Lo que el gimnasio debe saber hoy, de lo más grave a lo menos. Se calcula con el estado real, no con avisos ya generados.</summary>
    internal static IReadOnlyList<SubscriptionAlertDto> Alerts(Subscription s, string accessMode, DateOnly today)
    {
        var alerts = new List<SubscriptionAlertDto>();
        switch (s.Status)
        {
            case SubscriptionStatus.Suspended:
                alerts.Add(new SubscriptionAlertDto("suspended", nameof(NoticeSeverity.Critical), null));
                break;
            case SubscriptionStatus.Expired:
                alerts.Add(new SubscriptionAlertDto("expired", nameof(NoticeSeverity.Critical), null));
                break;
            case SubscriptionStatus.Cancelled:
                alerts.Add(new SubscriptionAlertDto("cancelled", nameof(NoticeSeverity.Critical), null));
                break;
            case SubscriptionStatus.PastDue:
                alerts.Add(new SubscriptionAlertDto("past_due", nameof(NoticeSeverity.Warning), s.GraceEndsOn));
                break;
            case SubscriptionStatus.Trialing when s.TrialEndsOn is { } ends && ends.DayNumber - today.DayNumber <= TrialWarningDays:
                alerts.Add(new SubscriptionAlertDto("trial_ending", nameof(NoticeSeverity.Warning), ends));
                break;
            case SubscriptionStatus.Active when s.Price > 0 && !s.CancelAtPeriodEnd && s.NextDueOn.DayNumber - today.DayNumber is <= DueWarningDays:
                alerts.Add(new SubscriptionAlertDto("due_soon", nameof(NoticeSeverity.Info), s.NextDueOn));
                break;
        }

        if (s.CancelAtPeriodEnd && s.Status is not (SubscriptionStatus.Cancelled or SubscriptionStatus.Expired))
        {
            alerts.Add(new SubscriptionAlertDto("cancellation_scheduled", nameof(NoticeSeverity.Info), s.CurrentPeriodEnd));
        }

        if (accessMode == "Restricted" && alerts.All(a => a.Code != "restricted"))
        {
            alerts.Add(new SubscriptionAlertDto("restricted", nameof(NoticeSeverity.Critical), null));
        }

        return alerts;
    }
}

internal sealed class ListMyInvoicesHandler(SubscriptionsDbContext db, ITenantContext tenantContext) : IQueryHandler<ListMyInvoicesQuery, PagedResult<InvoiceSummaryDto>>
{
    public async Task<Result<PagedResult<InvoiceSummaryDto>>> HandleAsync(ListMyInvoicesQuery query, CancellationToken cancellationToken)
    {
        if (tenantContext.TenantId is not { } tenantId)
        {
            return CapabilityErrors.None;
        }

        // El filtro por gimnasio y RLS lo limitan al propio; se muestran los documentos internos sin notas ni motivos del equipo.
        var rows = from i in db.Invoices.AsNoTracking()
                   join t in db.Tenants.AsNoTracking() on i.TenantId equals t.Id
                   where i.TenantId == tenantId
                   orderby i.IssuedAt descending, i.Number descending
                   select new { i, t };
        var paged = await rows.ToPagedAsync(query.Page, cancellationToken);
        return new PagedResult<InvoiceSummaryDto>(
            [.. paged.Items.Select(x => new InvoiceSummaryDto(
                x.i.Id, x.i.Number, x.i.TenantId, x.t.Slug, x.t.Name, x.i.Kind.ToString(), x.i.Status.ToString(), x.i.Amount, x.i.Currency, x.i.PlanCode,
                x.i.PeriodStart, x.i.PeriodEnd, x.i.IssuedAt, x.i.DueOn, x.i.PaidAt, x.i.RefundedAmount, x.i.AttemptCount))],
            paged.Page, paged.PageSize, paged.TotalCount);
    }
}

internal sealed class ListMyNoticesHandler(SubscriptionsDbContext db) : IQueryHandler<ListMyNoticesQuery, PagedResult<NoticeDto>>
{
    public async Task<Result<PagedResult<NoticeDto>>> HandleAsync(ListMyNoticesQuery query, CancellationToken cancellationToken)
    {
        var notices = db.Notices.AsNoTracking().AsQueryable();
        if (query.UnreadOnly)
        {
            notices = notices.Where(n => n.ReadAt == null);
        }

        var paged = await notices.OrderByDescending(n => n.CreatedAt).ThenByDescending(n => n.Id).ToPagedAsync(query.Page, cancellationToken);
        return new PagedResult<NoticeDto>([.. paged.Items.Select(NoticeDto.From)], paged.Page, paged.PageSize, paged.TotalCount);
    }
}

internal sealed class MarkNoticeReadHandler(SubscriptionsDbContext db, IClock clock) : ICommandHandler<MarkNoticeReadCommand, int>
{
    public async Task<Result<int>> HandleAsync(MarkNoticeReadCommand command, CancellationToken cancellationToken)
    {
        var unread = db.Notices.Where(n => n.ReadAt == null);
        if (command.NoticeId is { } id)
        {
            if (!await db.Notices.AnyAsync(n => n.Id == id, cancellationToken))
            {
                return PortalErrors.NoticeNotFound;
            }

            unread = unread.Where(n => n.Id == id);
        }

        var notices = await unread.ToListAsync(cancellationToken);
        foreach (var notice in notices)
        {
            notice.MarkRead(clock.UtcNow);
        }

        await db.SaveChangesAsync(cancellationToken);
        return notices.Count;
    }
}

internal sealed class ListMyRequestsHandler(SubscriptionsDbContext db) : IQueryHandler<ListMyRequestsQuery, PagedResult<SubscriptionRequestDto>>
{
    public async Task<Result<PagedResult<SubscriptionRequestDto>>> HandleAsync(ListMyRequestsQuery query, CancellationToken cancellationToken)
    {
        var paged = await db.Requests.AsNoTracking().OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id).ToPagedAsync(query.Page, cancellationToken);
        var names = await PortalStore.PlanNamesAsync(db, paged.Items.Where(r => r.RequestedPlanId is not null).Select(r => r.RequestedPlanId!.Value), cancellationToken);
        return new PagedResult<SubscriptionRequestDto>(
            [.. paged.Items.Select(r => SubscriptionRequestDto.From(r, r.RequestedPlanId is { } id ? names.GetValueOrDefault(id) : null))],
            paged.Page, paged.PageSize, paged.TotalCount);
    }
}

internal sealed class ListMyPlansHandler(SubscriptionsDbContext db) : IQueryHandler<ListMyPlansQuery, IReadOnlyList<AvailablePlanDto>>
{
    public async Task<Result<IReadOnlyList<AvailablePlanDto>>> HandleAsync(ListMyPlansQuery query, CancellationToken cancellationToken)
    {
        var current = await PortalStore.LatestAsync(db, cancellationToken);
        if (current is null)
        {
            return CapabilityErrors.None;
        }

        var plans = await db.Plans.AsNoTracking().Where(p => p.IsActive && p.Code != PortalStore.LegacyPlanCode).ToListAsync(cancellationToken);
        var currentMonthly = Subscription.MonthlyPrice(current.Price, current.Period);
        IReadOnlyList<AvailablePlanDto> result =
        [
            .. plans
                .OrderBy(p => Subscription.MonthlyPrice(p.Current.Price, p.Current.Period))
                .ThenBy(p => p.Name, StringComparer.Ordinal)
                .Select(p => new AvailablePlanDto(
                    PlanSummaryDto.From(p), p.Id == current.PlanId,
                    Subscription.Classify(currentMonthly, Subscription.MonthlyPrice(p.Current.Price, p.Current.Period)).ToString()))
        ];
        return Result<IReadOnlyList<AvailablePlanDto>>.Success(result);
    }
}

internal sealed class CreateRequestHandler(SubscriptionsDbContext db, ITenantContext tenantContext, IClock clock, ICurrentUser currentUser)
    : ICommandHandler<CreateRequestCommand, SubscriptionRequestDto>
{
    public async Task<Result<SubscriptionRequestDto>> HandleAsync(CreateRequestCommand command, CancellationToken cancellationToken)
    {
        if (tenantContext.TenantId is not { } tenantId || await PortalStore.LatestAsync(db, cancellationToken) is not { } subscription)
        {
            return CapabilityErrors.None;
        }

        var request = command.Request;
        string? planName = null;
        if (request.Kind == SubscriptionRequestKind.ChangePlan && request.PlanId is { } planId)
        {
            var plan = await db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == planId, cancellationToken);
            if (plan is null || !PortalStore.IsSellable(plan))
            {
                return PortalErrors.PlanNotAvailable;
            }

            if (plan.Id == subscription.PlanId)
            {
                return PortalErrors.SamePlan;
            }

            planName = plan.Name;
        }

        var allowed = request.Kind switch
        {
            SubscriptionRequestKind.ChangePlan => subscription.Status is SubscriptionStatus.Trialing or SubscriptionStatus.Active or SubscriptionStatus.PastDue,
            SubscriptionRequestKind.Cancellation => subscription.Status is SubscriptionStatus.Trialing or SubscriptionStatus.Active or SubscriptionStatus.PastDue && !subscription.CancelAtPeriodEnd,
            _ => subscription.Status is SubscriptionStatus.Suspended or SubscriptionStatus.Expired or SubscriptionStatus.Cancelled || subscription.CancelAtPeriodEnd
        };
        if (!allowed)
        {
            return PortalErrors.InvalidState;
        }

        if (await db.Requests.AnyAsync(r => r.SubscriptionId == subscription.Id && r.Kind == request.Kind && r.Status == SubscriptionRequestStatus.Pending, cancellationToken))
        {
            return PortalErrors.AlreadyPending;
        }

        var created = SubscriptionRequest.Create(tenantId, subscription.Id, request.Kind, request.PlanId, request.Message, currentUser.UserId, clock.UtcNow);
        if (created.IsFailure)
        {
            return created.Error;
        }

        db.Requests.Add(created.Value);
        await db.SaveChangesAsync(cancellationToken);
        return SubscriptionRequestDto.From(created.Value, planName);
    }
}

internal sealed class WithdrawRequestHandler(SubscriptionsDbContext db, IClock clock, ICurrentUser currentUser) : ICommandHandler<WithdrawRequestCommand, SubscriptionRequestDto>
{
    public async Task<Result<SubscriptionRequestDto>> HandleAsync(WithdrawRequestCommand command, CancellationToken cancellationToken)
    {
        var request = await db.Requests.FirstOrDefaultAsync(r => r.Id == command.Id, cancellationToken);
        if (request is null)
        {
            return PortalErrors.RequestNotFound;
        }

        var withdrawn = request.Withdraw(currentUser.UserId, clock.UtcNow);
        if (withdrawn.IsFailure)
        {
            return withdrawn.Error;
        }

        await db.SaveChangesAsync(cancellationToken);
        var names = await PortalStore.PlanNamesAsync(db, request.RequestedPlanId is { } id ? [id] : [], cancellationToken);
        return SubscriptionRequestDto.From(request, request.RequestedPlanId is { } pid ? names.GetValueOrDefault(pid) : null);
    }
}

// Del lado de la plataforma: solicitudes de todos los gimnasios. La autorización se exige en el endpoint y el aislamiento lo da RLS.

internal static class PlatformRequests
{
    public static async Task<PlatformRequestDto?> ToDtoAsync(SubscriptionsDbContext db, Guid id, CancellationToken cancellationToken)
    {
        var row = await (from r in db.Requests.IgnoreQueryFilters().AsNoTracking()
                         join t in db.Tenants.AsNoTracking() on r.TenantId equals t.Id
                         join s in db.Subscriptions.IgnoreQueryFilters().AsNoTracking() on r.SubscriptionId equals s.Id
                         where r.Id == id
                         select new { r, t, s }).FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }

        var names = await PortalStore.PlanNamesAsync(db, row.r.RequestedPlanId is { } pid ? [pid] : [], cancellationToken);
        return Map(row.r, row.t, row.s, row.r.RequestedPlanId is { } p ? names.GetValueOrDefault(p) : null);
    }

    public static async Task<Result<PlatformRequestDto>> ResultAsync(SubscriptionsDbContext db, Guid id, CancellationToken cancellationToken) =>
        await ToDtoAsync(db, id, cancellationToken) is { } dto ? dto : PortalErrors.RequestNotFound;

    public static PlatformRequestDto Map(SubscriptionRequest r, TenantRef t, Subscription s, string? planName) => new(
        r.Id, r.TenantId, t.Slug, t.Name, r.Kind.ToString(), r.Status.ToString(), r.RequestedPlanId, planName, r.Message, r.RequestedBy, r.CreatedAt,
        r.ResolvedAt, r.ResolvedBy, r.ResolutionNote, s.PlanName, s.Status.ToString());
}

internal sealed class ListPlatformRequestsHandler(SubscriptionsDbContext db) : IQueryHandler<ListPlatformRequestsQuery, PagedResult<PlatformRequestDto>>
{
    public async Task<Result<PagedResult<PlatformRequestDto>>> HandleAsync(ListPlatformRequestsQuery query, CancellationToken cancellationToken)
    {
        var rows = from r in db.Requests.IgnoreQueryFilters().AsNoTracking()
                   join t in db.Tenants.AsNoTracking() on r.TenantId equals t.Id
                   join s in db.Subscriptions.IgnoreQueryFilters().AsNoTracking() on r.SubscriptionId equals s.Id
                   select new { r, t, s };

        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            if (!Enum.TryParse<SubscriptionRequestStatus>(query.Status, ignoreCase: true, out var status))
            {
                return PortalErrors.StatusInvalid;
            }

            rows = rows.Where(x => x.r.Status == status);
        }

        if (query.TenantId is { } tenantId)
        {
            rows = rows.Where(x => x.r.TenantId == tenantId);
        }

        // Las pendientes primero y, dentro de cada grupo, las más antiguas esperando arriba.
        var paged = await rows.OrderBy(x => x.r.Status == SubscriptionRequestStatus.Pending ? 0 : 1)
            .ThenBy(x => x.r.Status == SubscriptionRequestStatus.Pending ? x.r.CreatedAt : DateTimeOffset.MaxValue)
            .ThenByDescending(x => x.r.CreatedAt)
            .ToPagedAsync(query.Page, cancellationToken);
        var names = await PortalStore.PlanNamesAsync(db, paged.Items.Where(x => x.r.RequestedPlanId is not null).Select(x => x.r.RequestedPlanId!.Value), cancellationToken);
        return new PagedResult<PlatformRequestDto>(
            [.. paged.Items.Select(x => PlatformRequests.Map(x.r, x.t, x.s, x.r.RequestedPlanId is { } id ? names.GetValueOrDefault(id) : null))],
            paged.Page, paged.PageSize, paged.TotalCount);
    }
}

/// <summary>
/// Aprobar ejecuta la acción pedida con las reglas de siempre (cambio de plan, cancelación al fin del período, reactivación) y marca la solicitud
/// <b>en la misma transacción</b>: si la acción no se puede aplicar, la solicitud sigue pendiente y se explica por qué.
/// </summary>
internal sealed class ApproveRequestHandler(SubscriptionsDbContext db, IServiceProvider services, IClock clock, ICurrentUser currentUser)
    : ICommandHandler<ApproveRequestCommand, PlatformRequestDto>
{
    public async Task<Result<PlatformRequestDto>> HandleAsync(ApproveRequestCommand command, CancellationToken cancellationToken)
    {
        var request = await db.Requests.IgnoreQueryFilters().FirstOrDefaultAsync(r => r.Id == command.Id, cancellationToken);
        if (request is null)
        {
            return PortalErrors.RequestNotFound;
        }

        var approved = request.Approve(command.Request.Note, currentUser.UserId, clock.UtcNow);
        if (approved.IsFailure)
        {
            return approved.Error;
        }

        var subscription = await db.Subscriptions.IgnoreQueryFilters().AsNoTracking().FirstAsync(s => s.Id == request.SubscriptionId, cancellationToken);
        var note = "Solicitud del gimnasio aprobada";
        Result<SubscriptionDto> applied;
        switch (request.Kind)
        {
            case SubscriptionRequestKind.ChangePlan:
                applied = await services.GetRequiredService<ICommandHandler<ChangeSubscriptionPlanCommand, SubscriptionDto>>()
                    .HandleAsync(new ChangeSubscriptionPlanCommand(request.TenantId, new ChangeSubscriptionPlanRequest(request.RequestedPlanId!.Value, note)), cancellationToken);
                break;
            case SubscriptionRequestKind.Cancellation:
                applied = await services.GetRequiredService<ICommandHandler<CancelSubscriptionCommand, SubscriptionDto>>()
                    .HandleAsync(new CancelSubscriptionCommand(request.TenantId, new CancelSubscriptionRequest(request.Message ?? note, AtPeriodEnd: true)), cancellationToken);
                break;
            default:
                if (subscription.Status == SubscriptionStatus.Suspended)
                {
                    applied = await services.GetRequiredService<ICommandHandler<ReactivateSubscriptionCommand, SubscriptionDto>>()
                        .HandleAsync(new ReactivateSubscriptionCommand(request.TenantId, new ReactivateSubscriptionRequest(note)), cancellationToken);
                }
                else if (subscription.CancelAtPeriodEnd)
                {
                    applied = await services.GetRequiredService<ICommandHandler<ResumeSubscriptionCommand, SubscriptionDto>>()
                        .HandleAsync(new ResumeSubscriptionCommand(request.TenantId, new ResumeSubscriptionRequest(note)), cancellationToken);
                }
                else
                {
                    return PortalErrors.NotApplicable;
                }

                break;
        }

        if (applied.IsFailure)
        {
            return applied.Error;
        }

        // La acción guardó los cambios (incluida la solicitud, que este mismo contexto tenía en seguimiento).
        return await PlatformRequests.ResultAsync(db, request.Id, cancellationToken);
    }
}

internal sealed class RejectRequestHandler(SubscriptionsDbContext db, IClock clock, ICurrentUser currentUser) : ICommandHandler<RejectRequestCommand, PlatformRequestDto>
{
    public async Task<Result<PlatformRequestDto>> HandleAsync(RejectRequestCommand command, CancellationToken cancellationToken)
    {
        var request = await db.Requests.IgnoreQueryFilters().FirstOrDefaultAsync(r => r.Id == command.Id, cancellationToken);
        if (request is null)
        {
            return PortalErrors.RequestNotFound;
        }

        var rejected = request.Reject(command.Request.Note, currentUser.UserId, clock.UtcNow);
        if (rejected.IsFailure)
        {
            return rejected.Error;
        }

        await db.SaveChangesAsync(cancellationToken);
        return await PlatformRequests.ResultAsync(db, request.Id, cancellationToken);
    }
}
