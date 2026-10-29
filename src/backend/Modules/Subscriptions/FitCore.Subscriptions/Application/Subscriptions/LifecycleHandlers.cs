using FitCore.Application.Core;
using FitCore.SharedKernel;
using FitCore.Subscriptions.Application.Billing;
using FitCore.Subscriptions.Application.Organizations;
using FitCore.Subscriptions.Application.Portal;
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
            if (change.IsFailure)
            {
                return Result.Failure(change.Error);
            }

            // La factura abierta tenía el importe del plan anterior: se anula y se emite de nuevo con el precio que corresponde.
            await BillingStore.VoidOpenAsync(Db, s, "Cambio de plan: la factura debe emitirse de nuevo con las condiciones nuevas", currentUser.UserId, clock.UtcNow, cancellationToken);
            return Result.Success();
        }, cancellationToken);
}

internal sealed class CancelSubscriptionHandler(SubscriptionsDbContext db, IClock clock, ICurrentUser currentUser)
    : SubscriptionTransitionHandler(db), ICommandHandler<CancelSubscriptionCommand, SubscriptionDto>
{
    public Task<Result<SubscriptionDto>> HandleAsync(CancelSubscriptionCommand command, CancellationToken cancellationToken) =>
        ApplyAsync(command.TenantId, async (s, _) =>
        {
            var result = command.Request.AtPeriodEnd
                ? s.ScheduleCancellation(command.Request.Reason, currentUser.UserId, clock.UtcNow)
                : s.Cancel(command.Request.Reason, currentUser.UserId, clock.UtcNow);
            if (result.IsSuccess)
            {
                // Una suscripción que se va a cancelar no se cobra: las facturas abiertas se anulan.
                await BillingStore.VoidOpenAsync(Db, s, "Cancelación de la suscripción", currentUser.UserId, clock.UtcNow, cancellationToken);
            }

            return result;
        }, cancellationToken);
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
    IServiceScopeFactory scopes, IClock clock, IOptions<SubscriptionOptions> options, UsageMeter usageMeter, ILogger<SubscriptionLifecycleRunner> logger)
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
            var lead = Math.Max(0, options.Value.InvoiceLeadDays);
            candidates = await db.Subscriptions.IgnoreQueryFilters().AsNoTracking()
                .Where(s => ((s.Status == SubscriptionStatus.Trialing || s.Status == SubscriptionStatus.Active) && s.CurrentPeriodEnd <= limit.AddDays(lead))
                            || (s.Status == SubscriptionStatus.PastDue && s.Price > 0))
                .OrderBy(s => s.CurrentPeriodEnd)
                .Select(s => s.Id)
                .ToListAsync(cancellationToken);
        }

        var summary = LifecycleRunSummary.Empty with { Examined = candidates.Count };
        foreach (var id in candidates)
        {
            try
            {
                var (action, invoiced) = await ProcessAsync(platformId, id, cancellationToken);
                summary = summary with
                {
                    InvoicesIssued = summary.InvoicesIssued + (invoiced ? 1 : 0),
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

        // Los avisos van después de procesar: así reflejan los cambios de estado de esta misma pasada.
        var notices = await GenerateNoticesAsync(platformId, cancellationToken);
        return summary with { UsageRefreshed = await RefreshUsageAsync(platformId, cancellationToken), NoticesCreated = notices };
    }

    /// <summary>Crea los avisos comerciales internos que falten (idempotente). Un fallo aquí nunca detiene el proceso de vencimientos.</summary>
    private async Task<int> GenerateNoticesAsync(Guid platformId, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(platformId);
            var db = scope.ServiceProvider.GetRequiredService<SubscriptionsDbContext>();
            return await NoticeGenerator.GenerateAsync(db, clock.UtcNow, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Falló la generación de avisos comerciales");
            return 0;
        }
    }

    /// <summary>Renueva la foto de consumo de las organizaciones que la tienen vieja (o ninguna), unas pocas por pasada, para no cargar la base.</summary>
    private async Task<int> RefreshUsageAsync(Guid platformId, CancellationToken cancellationToken)
    {
        var refreshed = 0;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(platformId);
            var db = scope.ServiceProvider.GetRequiredService<SubscriptionsDbContext>();
            var cutoff = clock.UtcNow.AddMinutes(-Math.Max(1, options.Value.UsageSnapshotMinutes));
            var stale = await (from t in db.Tenants.AsNoTracking()
                               where t.Slug != SubscriptionStore.PlatformSlug
                               join u in db.UsageSnapshots.AsNoTracking() on t.Id equals u.TenantId into usage
                               from u in usage.DefaultIfEmpty()
                               where u == null || u.MeasuredAt < cutoff
                               orderby u == null ? DateTimeOffset.MinValue : u.MeasuredAt
                               select t.Id).Take(100).ToListAsync(cancellationToken);
            foreach (var tenantId in stale)
            {
                try
                {
                    await usageMeter.RefreshAsync(db, clock, tenantId, cancellationToken);
                    refreshed++;
                    db.ChangeTracker.Clear();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    db.ChangeTracker.Clear();
                    logger.LogWarning(ex, "No se pudo medir el consumo del gimnasio {TenantId}", tenantId);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Falló la actualización de las fotos de consumo");
        }

        return refreshed;
    }

    private async Task<(LifecycleAction Action, bool Invoiced)> ProcessAsync(Guid platformId, Guid subscriptionId, CancellationToken cancellationToken)
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
            return (LifecycleAction.None, false);
        }

        var subscription = await db.Subscriptions.IgnoreQueryFilters().Include(s => s.PlanChanges).FirstAsync(s => s.Id == subscriptionId, cancellationToken);
        var tenant = await SubscriptionStore.TenantAsync(db, subscription.TenantId, cancellationToken);
        if (tenant is null)
        {
            return (LifecycleAction.None, false);
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
            return (LifecycleAction.None, false);
        }

        // Facturación anticipada: una suscripción de pago que se acerca a su vencimiento (o ya está en mora) recibe su factura para poder pagarla.
        // Si quedó cancelada, vencida o no es de pago, no se emite nada. Una sola abierta por suscripción (lo garantiza un índice único).
        var invoiced = false;
        var lead = Math.Max(0, options.Value.InvoiceLeadDays);
        var billable = subscription.Price > 0m && !subscription.CancelAtPeriodEnd
            && (subscription.Status == SubscriptionStatus.PastDue
                || (subscription.Status is SubscriptionStatus.Active or SubscriptionStatus.Trialing && subscription.CurrentPeriodEnd <= today.AddDays(lead)));
        if (billable)
        {
            var issued = await BillingStore.IssueAsync(db, subscription, today, null, now, cancellationToken);
            invoiced = issued.IsSuccess;
        }

        if (action == LifecycleAction.None && !invoiced)
        {
            return (LifecycleAction.None, false);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            // Otra solicitud emitió la misma factura a la vez: no hay nada más que hacer.
            return (LifecycleAction.None, false);
        }

        return (action, invoiced);
    }
}
