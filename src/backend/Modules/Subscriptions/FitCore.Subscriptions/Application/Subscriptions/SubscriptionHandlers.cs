using FitCore.Application.Core;
using FitCore.SharedKernel;
using FitCore.Subscriptions.Domain;
using FitCore.Subscriptions.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FitCore.Subscriptions.Application.Subscriptions;

// La autorización de plataforma (platform.subscriptions.read / .manage) se exige en el endpoint. Estas operaciones cruzan gimnasios a propósito
// (IgnoreQueryFilters): lo único que impide que un gimnasio vea o escriba suscripciones ajenas es la política RLS de la base de datos,
// que solo concede ese alcance al gimnasio «platform».

internal static class SubscriptionStore
{
    public const string PlatformSlug = "platform";

    public static Task<TenantRef?> TenantAsync(SubscriptionsDbContext db, Guid tenantId, CancellationToken cancellationToken) =>
        db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken);

    /// <summary>La suscripción vigente (no cancelada) del gimnasio, con seguimiento para modificarla.</summary>
    public static Task<Subscription?> CurrentAsync(SubscriptionsDbContext db, Guid tenantId, CancellationToken cancellationToken) =>
        db.Subscriptions.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.TenantId == tenantId && s.Status != SubscriptionStatus.Cancelled, cancellationToken);

    /// <summary>Hoy en la zona horaria del gimnasio: los períodos de una suscripción son fechas locales.</summary>
    public static DateOnly Today(IClock clock, string timeZone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(timeZone)).DateTime);

    public static PlanSnapshot Snapshot(Plan plan)
    {
        var v = plan.Current;
        return new PlanSnapshot(plan.Id, v.Id, v.Number, plan.Code, plan.Name, v.Currency, v.Period, v.Price, v.TrialDays, v.GraceDays);
    }

    /// <summary>Días de gracia de la suscripción: los de la versión contratada del plan o, si no define, el valor de plataforma.</summary>
    public static async Task<int> GraceDaysAsync(SubscriptionsDbContext db, SubscriptionOptions options, Subscription subscription, CancellationToken cancellationToken)
    {
        var plan = await db.PlanVersions.AsNoTracking().Where(v => v.Id == subscription.PlanVersionId).Select(v => v.GraceDays).FirstOrDefaultAsync(cancellationToken);
        return plan ?? options.GraceDays;
    }
}

internal sealed class GetTenantSubscriptionHandler(SubscriptionsDbContext db) : IQueryHandler<GetTenantSubscriptionQuery, SubscriptionDto>
{
    public async Task<Result<SubscriptionDto>> HandleAsync(GetTenantSubscriptionQuery query, CancellationToken cancellationToken)
    {
        var tenant = await SubscriptionStore.TenantAsync(db, query.TenantId, cancellationToken);
        if (tenant is null)
        {
            return SubscriptionAppErrors.TenantNotFound;
        }

        var subscription = await db.Subscriptions.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.TenantId == query.TenantId)
            .OrderBy(s => s.Status == SubscriptionStatus.Cancelled).ThenByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (subscription is null)
        {
            return SubscriptionAppErrors.NotFound;
        }

        var events = await db.SubscriptionEvents.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.SubscriptionId == subscription.Id).OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Id).Take(20)
            .ToListAsync(cancellationToken);
        return SubscriptionDto.From(subscription, tenant.Slug, tenant.Name, events);
    }
}

internal sealed class GetSubscriptionHistoryHandler(SubscriptionsDbContext db) : IQueryHandler<GetSubscriptionHistoryQuery, PagedResult<SubscriptionHistoryItemDto>>
{
    public async Task<Result<PagedResult<SubscriptionHistoryItemDto>>> HandleAsync(GetSubscriptionHistoryQuery query, CancellationToken cancellationToken)
    {
        if (await SubscriptionStore.TenantAsync(db, query.TenantId, cancellationToken) is null)
        {
            return SubscriptionAppErrors.TenantNotFound;
        }

        var events = from e in db.SubscriptionEvents.IgnoreQueryFilters().AsNoTracking()
                     join s in db.Subscriptions.IgnoreQueryFilters().AsNoTracking() on e.SubscriptionId equals s.Id
                     where e.TenantId == query.TenantId
                     orderby e.OccurredAt descending, e.Id descending
                     select new { e, s.PlanCode };
        var paged = await events.ToPagedAsync(query.Page, cancellationToken);
        return new PagedResult<SubscriptionHistoryItemDto>(
            [.. paged.Items.Select(x => new SubscriptionHistoryItemDto(x.e.Id, x.e.SubscriptionId, x.PlanCode, x.e.Type.ToString(), x.e.OccurredAt, x.e.ActorId, x.e.FromStatus?.ToString(), x.e.ToStatus?.ToString(), x.e.Detail))],
            paged.Page, paged.PageSize, paged.TotalCount);
    }
}

internal sealed class ListSubscriptionsHandler(SubscriptionsDbContext db, IClock clock) : IQueryHandler<ListSubscriptionsQuery, PagedResult<SubscriptionSummaryDto>>
{
    public async Task<Result<PagedResult<SubscriptionSummaryDto>>> HandleAsync(ListSubscriptionsQuery query, CancellationToken cancellationToken)
    {
        // Solo la suscripción vigente de cada gimnasio (las canceladas son historial).
        var rows = from s in db.Subscriptions.IgnoreQueryFilters().AsNoTracking()
                   join t in db.Tenants.AsNoTracking() on s.TenantId equals t.Id
                   where s.Status != SubscriptionStatus.Cancelled
                   select new { s, t };

        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            if (!Enum.TryParse<SubscriptionStatus>(query.Status, ignoreCase: true, out var status))
            {
                return Error.Validation("subscriptions.status_invalid", "El estado no es válido.");
            }

            rows = rows.Where(x => x.s.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(query.PlanCode))
        {
            var code = query.PlanCode.Trim().ToLowerInvariant();
            rows = rows.Where(x => x.s.PlanCode == code);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var pattern = LikePattern.Contains(query.Search);
            rows = rows.Where(x => EF.Functions.ILike(x.t.Name, pattern, "\\") || EF.Functions.ILike(x.t.Slug, pattern, "\\"));
        }

        if (query.DueWithinDays is { } days)
        {
            if (days is < 0 or > 365)
            {
                return Error.Validation("subscriptions.due_days_invalid", "Los días de vencimiento van de 0 a 365.");
            }

            // La fecha límite se calcula con el día UTC como cota amplia: la diferencia con la fecha local es de a lo sumo un día.
            var limit = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime).AddDays(days + 1);
            rows = rows.Where(x => x.s.NextDueOn <= limit);
        }

        var paged = await rows.OrderBy(x => x.s.NextDueOn).ThenBy(x => x.t.Name).ToPagedAsync(query.Page, cancellationToken);
        return new PagedResult<SubscriptionSummaryDto>(
            [.. paged.Items.Select(x => new SubscriptionSummaryDto(
                x.s.Id, x.s.TenantId, x.t.Slug, x.t.Name, x.t.Status, x.s.PlanCode, x.s.PlanName, x.s.Status.ToString(), x.s.Price, x.s.Currency,
                x.s.Period.ToString(), x.s.CurrentPeriodEnd, x.s.NextDueOn, x.s.TrialEndsOn, x.s.GraceEndsOn))],
            paged.Page, paged.PageSize, paged.TotalCount);
    }
}

internal sealed class CreateSubscriptionHandler(SubscriptionsDbContext db, IClock clock, ICurrentUser currentUser)
    : ICommandHandler<CreateSubscriptionCommand, SubscriptionDto>
{
    public async Task<Result<SubscriptionDto>> HandleAsync(CreateSubscriptionCommand command, CancellationToken cancellationToken)
    {
        var tenant = await SubscriptionStore.TenantAsync(db, command.TenantId, cancellationToken);
        if (tenant is null)
        {
            return SubscriptionAppErrors.TenantNotFound;
        }

        if (tenant.Slug == SubscriptionStore.PlatformSlug)
        {
            return SubscriptionAppErrors.PlatformProtected;
        }

        var plan = await db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == command.Request.PlanId, cancellationToken);
        if (plan is null)
        {
            return SubscriptionAppErrors.PlanNotFound;
        }

        if (!plan.IsActive)
        {
            return SubscriptionAppErrors.PlanInactive;
        }

        if (await db.Subscriptions.IgnoreQueryFilters().AnyAsync(s => s.TenantId == tenant.Id && s.Status != SubscriptionStatus.Cancelled, cancellationToken))
        {
            return SubscriptionAppErrors.AlreadyExists;
        }

        var started = Subscription.Start(
            tenant.Id, SubscriptionStore.Snapshot(plan), command.Request.StartTrial, SubscriptionStore.Today(clock, tenant.TimeZone),
            currentUser.UserId, clock.UtcNow, command.Request.Note);
        if (started.IsFailure)
        {
            return started.Error;
        }

        db.Subscriptions.Add(started.Value);
        await db.SaveChangesAsync(cancellationToken);
        return SubscriptionDto.From(started.Value, tenant.Slug, tenant.Name, started.Value.Events);
    }
}

internal abstract class SubscriptionTransitionHandler(SubscriptionsDbContext database)
{
    protected SubscriptionsDbContext Db { get; } = database;

    protected async Task<Result<SubscriptionDto>> ApplyAsync(
        Guid tenantId, Func<Subscription, TenantRef, Task<Result>> transition, CancellationToken cancellationToken)
    {
        var tenant = await SubscriptionStore.TenantAsync(Db, tenantId, cancellationToken);
        if (tenant is null)
        {
            return SubscriptionAppErrors.TenantNotFound;
        }

        var subscription = await SubscriptionStore.CurrentAsync(Db, tenantId, cancellationToken);
        if (subscription is null)
        {
            return SubscriptionAppErrors.NotFound;
        }

        var result = await transition(subscription, tenant);
        if (result.IsFailure)
        {
            return result.Error;
        }

        await Db.SaveChangesAsync(cancellationToken);
        var recent = await Db.SubscriptionEvents.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.SubscriptionId == subscription.Id).OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Id).Take(20).ToListAsync(cancellationToken);
        return SubscriptionDto.From(subscription, tenant.Slug, tenant.Name, recent);
    }
}

internal sealed class ActivateSubscriptionHandler(SubscriptionsDbContext db, IClock clock, ICurrentUser currentUser)
    : SubscriptionTransitionHandler(db), ICommandHandler<ActivateSubscriptionCommand, SubscriptionDto>
{
    public Task<Result<SubscriptionDto>> HandleAsync(ActivateSubscriptionCommand command, CancellationToken cancellationToken) =>
        ApplyAsync(command.TenantId, (s, t) =>
            Task.FromResult(s.Activate(SubscriptionStore.Today(clock, t.TimeZone), currentUser.UserId, clock.UtcNow, command.Request.Note)), cancellationToken);
}

internal sealed class SuspendSubscriptionHandler(SubscriptionsDbContext db, IClock clock, ICurrentUser currentUser)
    : SubscriptionTransitionHandler(db), ICommandHandler<SuspendSubscriptionCommand, SubscriptionDto>
{
    public Task<Result<SubscriptionDto>> HandleAsync(SuspendSubscriptionCommand command, CancellationToken cancellationToken) =>
        ApplyAsync(command.TenantId, (s, _) => Task.FromResult(s.Suspend(command.Request.Reason, currentUser.UserId, clock.UtcNow)), cancellationToken);
}

internal sealed class ReactivateSubscriptionHandler(SubscriptionsDbContext db, IClock clock, ICurrentUser currentUser, IOptions<SubscriptionOptions> options)
    : SubscriptionTransitionHandler(db), ICommandHandler<ReactivateSubscriptionCommand, SubscriptionDto>
{
    public Task<Result<SubscriptionDto>> HandleAsync(ReactivateSubscriptionCommand command, CancellationToken cancellationToken) =>
        ApplyAsync(command.TenantId, async (s, t) =>
        {
            var grace = await SubscriptionStore.GraceDaysAsync(Db, options.Value, s, cancellationToken);
            return s.Reactivate(SubscriptionStore.Today(clock, t.TimeZone), grace, currentUser.UserId, clock.UtcNow, command.Request.Note);
        }, cancellationToken);
}

/// <summary>
/// Gancho del alta de gimnasios: valida el plan elegido antes de crear nada y, con el gimnasio ya creado, le abre su suscripción
/// (el plan indicado, en prueba o activa; sin indicar, el plan por defecto de la plataforma).
/// </summary>
internal sealed class SubscriptionProvisioningHook(
    SubscriptionsDbContext db, IClock clock, ICurrentUser currentUser, IOptions<SubscriptionOptions> options) : ITenantProvisioningHook
{
    public async Task<Result> ValidateAsync(TenantPlanSelection selection, CancellationToken cancellationToken)
    {
        var plan = await FindAsync(selection, cancellationToken);
        if (plan.IsFailure)
        {
            return Result.Failure(plan.Error);
        }

        return selection.StartTrial && plan.Value.Current.TrialDays <= 0 ? Result.Failure(SubscriptionErrors.PlanWithoutTrial) : Result.Success();
    }

    public async Task<Result> OnProvisionedAsync(Guid tenantId, TenantPlanSelection selection, CancellationToken cancellationToken)
    {
        var plan = await FindAsync(selection, cancellationToken);
        if (plan.IsFailure)
        {
            return Result.Failure(plan.Error);
        }

        var tenant = await SubscriptionStore.TenantAsync(db, tenantId, cancellationToken);
        if (tenant is null)
        {
            return Result.Failure(SubscriptionAppErrors.TenantNotFound);
        }

        var started = Subscription.Start(
            tenantId, SubscriptionStore.Snapshot(plan.Value), selection.StartTrial, SubscriptionStore.Today(clock, tenant.TimeZone),
            currentUser.UserId, clock.UtcNow, "Alta del gimnasio");
        if (started.IsFailure)
        {
            return Result.Failure(started.Error);
        }

        db.Subscriptions.Add(started.Value);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    private async Task<Result<Plan>> FindAsync(TenantPlanSelection selection, CancellationToken cancellationToken)
    {
        var code = (string.IsNullOrWhiteSpace(selection.PlanCode) ? options.Value.DefaultPlanCode : selection.PlanCode).Trim().ToLowerInvariant();
        var plan = await db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Code == code, cancellationToken);
        if (plan is null)
        {
            return SubscriptionAppErrors.PlanNotFound;
        }

        // El plan por defecto (legacy) nace inactivo: no se vende, pero sí se asigna cuando el alta no elige otro.
        var isDefault = string.IsNullOrWhiteSpace(selection.PlanCode);
        if (!plan.IsActive && !isDefault)
        {
            return SubscriptionAppErrors.PlanInactive;
        }

        return plan;
    }
}
