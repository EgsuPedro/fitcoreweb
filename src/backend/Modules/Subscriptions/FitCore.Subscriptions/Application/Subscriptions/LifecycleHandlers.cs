using FitCore.Application.Core;
using FitCore.SharedKernel;
using FitCore.Subscriptions.Domain;
using FitCore.Subscriptions.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FitCore.Subscriptions.Application.Subscriptions;

internal static class PlanSnapshots
{
    /// <summary>Condiciones de una versión concreta de un plan (no necesariamente la vigente): lo que una baja programada aplicará al renovar.</summary>
    public static async Task<PlanSnapshot?> OfVersionAsync(SubscriptionsDbContext db, Guid planId, Guid planVersionId, CancellationToken cancellationToken)
    {
        var plan = await db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == planId, cancellationToken);
        var version = plan?.Versions.FirstOrDefault(v => v.Id == planVersionId);
        return plan is null || version is null
            ? null
            : new PlanSnapshot(plan.Id, version.Id, version.Number, plan.Code, plan.Name, version.Currency, version.Period, version.Price, version.TrialDays, version.GraceDays);
    }

    public static Task<PlanSnapshot?> PendingAsync(SubscriptionsDbContext db, Subscription subscription, CancellationToken cancellationToken) =>
        subscription is { PendingPlanId: { } planId, PendingPlanVersionId: { } versionId }
            ? OfVersionAsync(db, planId, versionId, cancellationToken)
            : Task.FromResult<PlanSnapshot?>(null);
}

internal sealed class RenewSubscriptionHandler(SubscriptionsDbContext db, IClock clock, ICurrentUser currentUser, IOptions<SubscriptionOptions> options)
    : SubscriptionTransitionHandler(db), ICommandHandler<RenewSubscriptionCommand, SubscriptionDto>
{
    public Task<Result<SubscriptionDto>> HandleAsync(RenewSubscriptionCommand command, CancellationToken cancellationToken) =>
        ApplyAsync(command.TenantId, async (s, t) =>
        {
            var pending = await PlanSnapshots.PendingAsync(Db, s, cancellationToken);
            return s.Renew(pending, SubscriptionStore.Today(clock, t.TimeZone), options.Value.RenewalWindowDays, currentUser.UserId, clock.UtcNow, command.Request.Note);
        }, cancellationToken);
}

internal sealed class ChangeSubscriptionPlanHandler(SubscriptionsDbContext db, IClock clock, ICurrentUser currentUser)
    : SubscriptionTransitionHandler(db), ICommandHandler<ChangeSubscriptionPlanCommand, SubscriptionDto>
{
    public Task<Result<SubscriptionDto>> HandleAsync(ChangeSubscriptionPlanCommand command, CancellationToken cancellationToken) =>
        ApplyAsync(command.TenantId, async (s, t) =>
        {
            var plan = await Db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == command.Request.PlanId, cancellationToken);
            if (plan is null)
            {
                return Result.Failure(SubscriptionAppErrors.PlanNotFound);
            }

            if (!plan.IsActive)
            {
                return Result.Failure(SubscriptionAppErrors.PlanInactive);
            }

            var change = s.ChangePlan(SubscriptionStore.Snapshot(plan), SubscriptionStore.Today(clock, t.TimeZone), currentUser.UserId, clock.UtcNow, command.Request.Note);
            return change.IsFailure ? Result.Failure(change.Error) : Result.Success();
        }, cancellationToken);
}

internal sealed class CancelSubscriptionHandler(SubscriptionsDbContext db, IClock clock, ICurrentUser currentUser)
    : SubscriptionTransitionHandler(db), ICommandHandler<CancelSubscriptionCommand, SubscriptionDto>
{
    public Task<Result<SubscriptionDto>> HandleAsync(CancelSubscriptionCommand command, CancellationToken cancellationToken) =>
        ApplyAsync(command.TenantId, (s, _) => Task.FromResult(command.Request.AtPeriodEnd
            ? s.ScheduleCancellation(command.Request.Reason, currentUser.UserId, clock.UtcNow)
            : s.Cancel(command.Request.Reason, currentUser.UserId, clock.UtcNow)), cancellationToken);
}

internal sealed class ResumeSubscriptionHandler(SubscriptionsDbContext db, IClock clock, ICurrentUser currentUser)
    : SubscriptionTransitionHandler(db), ICommandHandler<ResumeSubscriptionCommand, SubscriptionDto>
{
    public Task<Result<SubscriptionDto>> HandleAsync(ResumeSubscriptionCommand command, CancellationToken cancellationToken) =>
        ApplyAsync(command.TenantId, (s, _) => Task.FromResult(s.ResumeCancellation(currentUser.UserId, clock.UtcNow, command.Request.Note)), cancellationToken);
}

internal sealed class ExtendSubscriptionHandler(SubscriptionsDbContext db, IClock clock, ICurrentUser currentUser)
    : SubscriptionTransitionHandler(db), ICommandHandler<ExtendSubscriptionCommand, SubscriptionDto>
{
    public Task<Result<SubscriptionDto>> HandleAsync(ExtendSubscriptionCommand command, CancellationToken cancellationToken) =>
        ApplyAsync(command.TenantId, (s, _) => Task.FromResult(s.Extend(command.Request.Days, command.Request.Reason, currentUser.UserId, clock.UtcNow)), cancellationToken);
}

internal sealed class RunLifecycleHandler(SubscriptionLifecycleRunner runner) : ICommandHandler<RunLifecycleCommand, LifecycleRunSummary>
{
    public async Task<Result<LifecycleRunSummary>> HandleAsync(RunLifecycleCommand command, CancellationToken cancellationToken) =>
        await runner.RunAsync(cancellationToken);
}

/// <summary>
/// Proceso de vencimientos (lo ejecuta el Worker y también se puede disparar a mano desde la plataforma). Es idempotente: cada suscripción se
/// evalúa con su día local y solo avanza si corresponde, de modo que repetirlo, o que dos instancias corran a la vez, no duplica nada. Cada una se
/// procesa en su propia transacción con un bloqueo de fila (<c>SKIP LOCKED</c>): si otro proceso la tiene, se salta. Actúa como el contexto de
/// plataforma (es lo que la base exige para escribir suscripciones) y un fallo en una suscripción no frena a las demás.
/// <list type="bullet">
/// <item>Prueba terminada sin pago → <c>Expired</c>.</item>
/// <item>Período terminado con cancelación programada → <c>Cancelled</c>.</item>
/// <item>Período terminado de un plan gratuito → se renueva solo (aplicando una baja de plan programada).</item>
/// <item>Período terminado de un plan de pago → <c>PastDue</c>, con la gracia del plan.</item>
/// <item>Gracia terminada → <c>Expired</c>. No se borra nada: solo cambia el modo de acceso.</item>
/// </list>
/// No hay cobro automático: sin proveedor de pagos, la renovación de un plan de pago se confirma registrando el pago.
/// </summary>
public sealed class SubscriptionLifecycleRunner(
    IServiceScopeFactory scopes, IClock clock, IOptions<SubscriptionOptions> options, ILogger<SubscriptionLifecycleRunner> logger)
{
    public async Task<LifecycleRunSummary> RunAsync(CancellationToken cancellationToken)
    {
        Guid platformId;
        List<Guid> candidates;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SubscriptionsDbContext>();
            platformId = await db.Tenants.AsNoTracking().Where(t => t.Slug == SubscriptionStore.PlatformSlug).Select(t => t.Id).FirstOrDefaultAsync(cancellationToken);
            if (platformId == Guid.Empty)
            {
                return LifecycleRunSummary.Empty;
            }

            scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(platformId);

            // Cota amplia (un día de margen sobre el día UTC): la decisión final usa el día local de cada gimnasio.
            var limit = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime).AddDays(1);
            candidates = await db.Subscriptions.IgnoreQueryFilters().AsNoTracking()
                .Where(s => ((s.Status == SubscriptionStatus.Trialing || s.Status == SubscriptionStatus.Active) && s.CurrentPeriodEnd <= limit)
                            || (s.Status == SubscriptionStatus.PastDue && s.GraceEndsOn != null && s.GraceEndsOn <= limit))
                .OrderBy(s => s.CurrentPeriodEnd)
                .Select(s => s.Id)
                .ToListAsync(cancellationToken);
        }

        var summary = LifecycleRunSummary.Empty with { Examined = candidates.Count };
        foreach (var id in candidates)
        {
            try
            {
                var action = await ProcessAsync(platformId, id, cancellationToken);
                summary = summary with
                {
                    TrialsExpired = summary.TrialsExpired + (action == LifecycleAction.ExpireTrial ? 1 : 0),
                    FreeRenewed = summary.FreeRenewed + (action == LifecycleAction.RenewFree ? 1 : 0),
                    Cancelled = summary.Cancelled + (action == LifecycleAction.CancelAtPeriodEnd ? 1 : 0),
                    MarkedPastDue = summary.MarkedPastDue + (action == LifecycleAction.MarkPastDue ? 1 : 0),
                    GraceExpired = summary.GraceExpired + (action == LifecycleAction.ExpireGrace ? 1 : 0)
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                summary = summary with { Failed = summary.Failed + 1 };
                logger.LogError(ex, "Falló el proceso de vencimientos de la suscripción {SubscriptionId}", id);
            }
        }

        return summary;
    }

    private async Task<LifecycleAction> ProcessAsync(Guid platformId, Guid subscriptionId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(platformId);
        var db = scope.ServiceProvider.GetRequiredService<SubscriptionsDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Si otra instancia tiene la fila bloqueada, se salta: ya la está procesando.
        var locked = await db.Database
            .SqlQuery<Guid>($"SELECT id AS \"Value\" FROM subscriptions.subscriptions WHERE id = {subscriptionId} FOR UPDATE SKIP LOCKED")
            .ToListAsync(cancellationToken);
        if (locked.Count == 0)
        {
            return LifecycleAction.None;
        }

        var subscription = await db.Subscriptions.IgnoreQueryFilters().Include(s => s.PlanChanges).FirstAsync(s => s.Id == subscriptionId, cancellationToken);
        var tenant = await SubscriptionStore.TenantAsync(db, subscription.TenantId, cancellationToken);
        if (tenant is null)
        {
            return LifecycleAction.None;
        }

        var today = SubscriptionStore.Today(clock, tenant.TimeZone);
        var now = clock.UtcNow;
        var action = SubscriptionLifecycle.NextAction(subscription, today);
        var result = action switch
        {
            LifecycleAction.ExpireTrial or LifecycleAction.ExpireGrace => subscription.Expire(null, now),
            LifecycleAction.CancelAtPeriodEnd => subscription.CompleteScheduledCancellation(null, now),
            LifecycleAction.MarkPastDue => subscription.MarkPastDue(await SubscriptionStore.GraceDaysAsync(db, options.Value, subscription, cancellationToken), null, now),
            LifecycleAction.RenewFree => subscription.Renew(
                await PlanSnapshots.PendingAsync(db, subscription, cancellationToken), today, options.Value.RenewalWindowDays, null, now, null, automatic: true),
            _ => Result.Success()
        };

        if (result.IsFailure)
        {
            logger.LogWarning("Suscripción {SubscriptionId}: {Action} no se pudo aplicar ({Code})", subscriptionId, action, result.Error.Code);
            return LifecycleAction.None;
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return action;
    }
}
