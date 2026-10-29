using FitCore.Application.Core;
using FitCore.Platform.Persistence;
using FitCore.SharedKernel;
using FitCore.Subscriptions.Application.Billing;
using FitCore.Subscriptions.Application.Subscriptions;
using FitCore.Subscriptions.Domain;
using FitCore.Subscriptions.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FitCore.Subscriptions.Application.Organizations;

// La autorización de plataforma (platform.organizations.* / platform.metrics.read) se exige en el endpoint. El equipo de plataforma ve de cada
// organización su contrato, su cobro y conteos agregados de consumo; nunca los datos de sus alumnos (la base lo impide por RLS).

/// <summary>
/// Mide el consumo agregado de una organización entrando <b>con su propio contexto</b> y contando filas (sin leer ningún dato de personas): es la
/// única forma de ver cuántas sucursales, usuarios y alumnos tiene sin dar a la plataforma acceso a sus tablas.
/// </summary>
public sealed class UsageMeter(IServiceScopeFactory scopes)
{
    public async Task<(int Branches, int InternalUsers, int Members)> MeasureAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<SubscriptionsDbContext>();
        return (
            await db.Branches.CountAsync(b => b.IsActive, cancellationToken),
            await db.Users.CountAsync(u => u.IsActive && u.AccountKind == "Staff", cancellationToken),
            await db.Members.CountAsync(cancellationToken));
    }

    /// <summary>Mide y guarda la foto de consumo de la organización (el llamador ya está en el contexto de plataforma).</summary>
    public async Task<TenantUsageSnapshot> RefreshAsync(SubscriptionsDbContext platformDb, IClock clock, Guid tenantId, CancellationToken cancellationToken)
    {
        var (branches, users, members) = await MeasureAsync(tenantId, cancellationToken);
        var snapshot = await platformDb.UsageSnapshots.FirstOrDefaultAsync(s => s.TenantId == tenantId, cancellationToken);
        if (snapshot is null)
        {
            snapshot = new TenantUsageSnapshot { TenantId = tenantId };
            platformDb.UsageSnapshots.Add(snapshot);
        }

        snapshot.Branches = branches;
        snapshot.InternalUsers = users;
        snapshot.Members = members;
        snapshot.MeasuredAt = clock.UtcNow;
        await platformDb.SaveChangesAsync(cancellationToken);
        return snapshot;
    }
}

internal static class OrganizationMapping
{
    public static OrganizationUsageDto? Usage(TenantUsageSnapshot? u) => u is null ? null : new OrganizationUsageDto(u.Branches, u.InternalUsers, u.Members, u.MeasuredAt);

    public static OrganizationSubscriptionDto? Subscription(Subscription? s) => s is null
        ? null
        : new OrganizationSubscriptionDto(
            s.Id, s.PlanCode, s.PlanName, s.Status.ToString(), s.Price, s.Currency, s.Period.ToString(), s.CurrentPeriodEnd, s.NextDueOn, s.TrialEndsOn, s.GraceEndsOn, s.CancelAtPeriodEnd);
}

internal sealed class ListOrganizationsHandler(SubscriptionsDbContext db, IClock clock) : IQueryHandler<ListOrganizationsQuery, PagedResult<OrganizationSummaryDto>>
{
    public async Task<Result<PagedResult<OrganizationSummaryDto>>> HandleAsync(ListOrganizationsQuery query, CancellationToken cancellationToken)
    {
        var rows = from t in db.Tenants.AsNoTracking()
                   where t.Slug != SubscriptionStore.PlatformSlug
                   join s in db.Subscriptions.IgnoreQueryFilters().AsNoTracking().Where(x => x.Status != SubscriptionStatus.Cancelled) on t.Id equals s.TenantId into subs
                   from s in subs.DefaultIfEmpty()
                   join u in db.UsageSnapshots.AsNoTracking() on t.Id equals u.TenantId into usage
                   from u in usage.DefaultIfEmpty()
                   select new { t, s, u };

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var pattern = LikePattern.Contains(query.Search);
            rows = rows.Where(x => EF.Functions.ILike(x.t.Name, pattern, "\\") || EF.Functions.ILike(x.t.Slug, pattern, "\\"));
        }

        if (!string.IsNullOrWhiteSpace(query.TenantStatus))
        {
            var status = query.TenantStatus.Trim();
            if (status is not ("Active" or "Suspended"))
            {
                return Error.Validation("organizations.status_invalid", "El estado debe ser Active o Suspended.");
            }

            rows = rows.Where(x => x.t.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(query.SubscriptionStatus))
        {
            if (string.Equals(query.SubscriptionStatus, "None", StringComparison.OrdinalIgnoreCase))
            {
                rows = rows.Where(x => x.s == null);
            }
            else if (Enum.TryParse<SubscriptionStatus>(query.SubscriptionStatus, ignoreCase: true, out var subscriptionStatus))
            {
                rows = rows.Where(x => x.s != null && x.s.Status == subscriptionStatus);
            }
            else
            {
                return Error.Validation("organizations.subscription_status_invalid", "El estado de suscripción no es válido (use un estado o None).");
            }
        }

        if (!string.IsNullOrWhiteSpace(query.PlanCode))
        {
            var code = query.PlanCode.Trim().ToLowerInvariant();
            rows = rows.Where(x => x.s != null && x.s.PlanCode == code);
        }

        if (query.DueWithinDays is { } days)
        {
            if (days is < 0 or > 365)
            {
                return Error.Validation("organizations.due_days_invalid", "Los días de vencimiento van de 0 a 365.");
            }

            var limit = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime).AddDays(days + 1);
            rows = rows.Where(x => x.s != null && x.s.NextDueOn <= limit);
        }

        var paged = await rows.OrderBy(x => x.t.Name).ThenBy(x => x.t.Slug).ToPagedAsync(query.Page, cancellationToken);
        return new PagedResult<OrganizationSummaryDto>(
            [.. paged.Items.Select(x => new OrganizationSummaryDto(
                x.t.Id, x.t.Slug, x.t.Name, x.t.Status, x.t.TimeZone, x.t.CreatedAt, OrganizationMapping.Subscription(x.s), OrganizationMapping.Usage(x.u)))],
            paged.Page, paged.PageSize, paged.TotalCount);
    }
}

internal sealed class GetOrganizationHandler(SubscriptionsDbContext db) : IQueryHandler<GetOrganizationQuery, OrganizationDto>
{
    public async Task<Result<OrganizationDto>> HandleAsync(GetOrganizationQuery query, CancellationToken cancellationToken)
    {
        var tenant = await SubscriptionStore.TenantAsync(db, query.Id, cancellationToken);
        if (tenant is null)
        {
            return OrganizationErrors.NotFound;
        }

        if (tenant.Slug == SubscriptionStore.PlatformSlug)
        {
            return OrganizationErrors.PlatformProtected;
        }

        var subscription = await db.Subscriptions.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.TenantId == tenant.Id)
            .OrderBy(s => s.Status == SubscriptionStatus.Cancelled).ThenByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        SubscriptionDto? subscriptionDto = null;
        if (subscription is not null)
        {
            var events = await db.SubscriptionEvents.IgnoreQueryFilters().AsNoTracking().Where(e => e.SubscriptionId == subscription.Id)
                .OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Id).Take(20).ToListAsync(cancellationToken);
            subscriptionDto = SubscriptionDto.From(subscription, tenant.Slug, tenant.Name, events);
        }

        var entitlement = await db.Entitlements.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(e => e.TenantId == tenant.Id, cancellationToken);
        var usage = await db.UsageSnapshots.AsNoTracking().FirstOrDefaultAsync(u => u.TenantId == tenant.Id, cancellationToken);

        var open = db.Invoices.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.TenantId == tenant.Id && (i.Status == InvoiceStatus.Pending || i.Status == InvoiceStatus.Failed));
        var openAmount = await open.SumAsync(i => (decimal?)i.Amount, cancellationToken) ?? 0m;
        var recent = await db.Invoices.IgnoreQueryFilters().AsNoTracking().Where(i => i.TenantId == tenant.Id)
            .OrderByDescending(i => i.IssuedAt).Take(5).ToListAsync(cancellationToken);

        return new OrganizationDto(
            tenant.Id, tenant.Slug, tenant.Name, tenant.Status, tenant.TimeZone, tenant.CreatedAt,
            subscriptionDto,
            entitlement is null ? null : new OrganizationEntitlementDto(
                entitlement.AccessMode, [.. entitlement.Features],
                [.. Capabilities.LimitCatalog.Select(l => new EntitlementLimitDto(l.Code, entitlement.LimitOf(l.Code)))]),
            OrganizationMapping.Usage(usage),
            await open.CountAsync(cancellationToken), openAmount,
            [.. recent.Select(i => new InvoiceSummaryDto(
                i.Id, i.Number, i.TenantId, tenant.Slug, tenant.Name, i.Kind.ToString(), i.Status.ToString(), i.Amount, i.Currency, i.PlanCode,
                i.PeriodStart, i.PeriodEnd, i.IssuedAt, i.DueOn, i.PaidAt, i.RefundedAmount, i.AttemptCount))],
            await db.PlatformNotes.AsNoTracking().CountAsync(n => n.TenantId == tenant.Id, cancellationToken));
    }
}

internal sealed class RefreshUsageHandler(SubscriptionsDbContext db, UsageMeter meter, IClock clock) : ICommandHandler<RefreshUsageCommand, OrganizationUsageDto>
{
    public async Task<Result<OrganizationUsageDto>> HandleAsync(RefreshUsageCommand command, CancellationToken cancellationToken)
    {
        var tenant = await SubscriptionStore.TenantAsync(db, command.TenantId, cancellationToken);
        if (tenant is null)
        {
            return OrganizationErrors.NotFound;
        }

        if (tenant.Slug == SubscriptionStore.PlatformSlug)
        {
            return OrganizationErrors.PlatformProtected;
        }

        var snapshot = await meter.RefreshAsync(db, clock, tenant.Id, cancellationToken);
        return OrganizationMapping.Usage(snapshot)!;
    }
}

internal sealed class ListNotesHandler(SubscriptionsDbContext db) : IQueryHandler<ListNotesQuery, PagedResult<NoteDto>>
{
    public async Task<Result<PagedResult<NoteDto>>> HandleAsync(ListNotesQuery query, CancellationToken cancellationToken)
    {
        if (await SubscriptionStore.TenantAsync(db, query.TenantId, cancellationToken) is null)
        {
            return OrganizationErrors.NotFound;
        }

        var rows = from n in db.PlatformNotes.AsNoTracking()
                   where n.TenantId == query.TenantId
                   join u in db.Users.IgnoreQueryFilters().AsNoTracking() on n.AuthorId equals (Guid?)u.Id into authors
                   from a in authors.DefaultIfEmpty()
                   orderby n.CreatedAt descending
                   select new { n, Name = a != null ? a.FullName : null };
        var paged = await rows.ToPagedAsync(query.Page, cancellationToken);
        return new PagedResult<NoteDto>([.. paged.Items.Select(x => new NoteDto(x.n.Id, x.n.Text, x.n.AuthorId, x.Name, x.n.CreatedAt))], paged.Page, paged.PageSize, paged.TotalCount);
    }
}

internal sealed class AddNoteHandler(SubscriptionsDbContext db, IClock clock, ICurrentUser currentUser) : ICommandHandler<AddNoteCommand, NoteDto>
{
    public async Task<Result<NoteDto>> HandleAsync(AddNoteCommand command, CancellationToken cancellationToken)
    {
        var tenant = await SubscriptionStore.TenantAsync(db, command.TenantId, cancellationToken);
        if (tenant is null)
        {
            return OrganizationErrors.NotFound;
        }

        if (tenant.Slug == SubscriptionStore.PlatformSlug)
        {
            return OrganizationErrors.PlatformProtected;
        }

        var note = PlatformNote.Create(tenant.Id, command.Request.Text, currentUser.UserId, clock.UtcNow);
        if (note.IsFailure)
        {
            return note.Error;
        }

        db.PlatformNotes.Add(note.Value);
        await db.SaveChangesAsync(cancellationToken);
        var name = currentUser.UserId is { } id ? await db.Users.IgnoreQueryFilters().AsNoTracking().Where(u => u.Id == id).Select(u => u.FullName).FirstOrDefaultAsync(cancellationToken) : null;
        return new NoteDto(note.Value.Id, note.Value.Text, note.Value.AuthorId, name, note.Value.CreatedAt);
    }
}

internal sealed class GetActivityHandler(SubscriptionsDbContext db) : IQueryHandler<GetActivityQuery, PagedResult<ActivityEntryDto>>
{
    public async Task<Result<PagedResult<ActivityEntryDto>>> HandleAsync(GetActivityQuery query, CancellationToken cancellationToken)
    {
        if (await SubscriptionStore.TenantAsync(db, query.TenantId, cancellationToken) is null)
        {
            return OrganizationErrors.NotFound;
        }

        // Las acciones del equipo de plataforma se auditan en el gimnasio «platform»; se buscan por los identificadores de lo que tocaron.
        var id = query.TenantId;
        var ids = new List<string> { id.ToString() };
        ids.AddRange((await db.Subscriptions.IgnoreQueryFilters().AsNoTracking().Where(s => s.TenantId == id).Select(s => s.Id).ToListAsync(cancellationToken)).Select(x => x.ToString()));
        ids.AddRange((await db.Invoices.IgnoreQueryFilters().AsNoTracking().Where(i => i.TenantId == id).Select(i => i.Id).ToListAsync(cancellationToken)).Select(x => x.ToString()));
        ids.AddRange((await db.Payments.IgnoreQueryFilters().AsNoTracking().Where(p => p.TenantId == id).Select(p => p.Id).ToListAsync(cancellationToken)).Select(x => x.ToString()));
        ids.AddRange((await db.PlatformNotes.AsNoTracking().Where(n => n.TenantId == id).Select(n => n.Id).ToListAsync(cancellationToken)).Select(x => x.ToString()));
        ids.AddRange((await db.PlanChanges.IgnoreQueryFilters().AsNoTracking().Where(c => c.TenantId == id).Select(c => c.Id).ToListAsync(cancellationToken)).Select(x => x.ToString()));

        var rows = from a in db.Set<AuditLog>().AsNoTracking()
                   where a.EntityId != null && ids.Contains(a.EntityId)
                   join u in db.Users.IgnoreQueryFilters().AsNoTracking() on a.UserId equals (Guid?)u.Id into actors
                   from actor in actors.DefaultIfEmpty()
                   orderby a.OccurredAt descending, a.Id descending
                   select new { a, Name = actor != null ? actor.FullName : null };
        var paged = await rows.ToPagedAsync(query.Page, cancellationToken);
        return new PagedResult<ActivityEntryDto>(
            [.. paged.Items.Select(x => new ActivityEntryDto(
                x.a.Id, x.a.Action, x.a.Entity, x.a.EntityId, x.a.UserId, x.Name, x.a.OccurredAt, x.a.After is { Length: > 500 } after ? after[..500] + "…" : x.a.After))],
            paged.Page, paged.PageSize, paged.TotalCount);
    }
}

internal sealed class GetMetricsHandler(SubscriptionsDbContext db, IClock clock) : IQueryHandler<GetMetricsQuery, MetricsDto>
{
    public const string MrrFormula =
        "MRR = suma, sobre las suscripciones Active y PastDue de pago, del precio contratado ÷ meses de la periodicidad. No incluye pruebas, suspendidas, canceladas ni planes gratuitos; es lo contratado, no lo cobrado. ARR = MRR × 12.";

    public async Task<Result<MetricsDto>> HandleAsync(GetMetricsQuery query, CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        var to = query.To ?? today;
        var from = query.From ?? to.AddDays(-29);
        if (from > to)
        {
            return Error.Validation("metrics.period_invalid", "La fecha inicial no puede ser posterior a la final.");
        }

        if (to.DayNumber - from.DayNumber > 730)
        {
            return Error.Validation("metrics.period_too_long", "El período no puede superar 2 años.");
        }

        var start = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var end = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        var tenants = db.Tenants.AsNoTracking().Where(t => t.Slug != SubscriptionStore.PlatformSlug);
        var current = db.Subscriptions.IgnoreQueryFilters().AsNoTracking().Where(s => s.Status != SubscriptionStatus.Cancelled);
        var organizations = await tenants.CountAsync(cancellationToken);
        var suspendedOrganizations = await tenants.CountAsync(t => t.Status == "Suspended", cancellationToken);
        var withSubscription = await current.CountAsync(cancellationToken);

        var byStatus = (await current.GroupBy(s => s.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(cancellationToken))
            .ToDictionary(x => x.Key.ToString(), x => x.Count);
        foreach (var status in Enum.GetValues<SubscriptionStatus>().Where(s => s != SubscriptionStatus.Cancelled))
        {
            byStatus.TryAdd(status.ToString(), 0);
        }

        // MRR y distribución por plan: la lista es pequeña (una fila por suscripción vigente de pago), se agrega en memoria con la fórmula documentada.
        var recurring = await current
            .Where(s => (s.Status == SubscriptionStatus.Active || s.Status == SubscriptionStatus.PastDue) && s.Price > 0)
            .Select(s => new { s.PlanCode, s.PlanName, s.Price, s.Period })
            .ToListAsync(cancellationToken);
        var plans = await current.GroupBy(s => new { s.PlanCode, s.PlanName }).Select(g => new { g.Key.PlanCode, g.Key.PlanName, Count = g.Count() }).ToListAsync(cancellationToken);
        var byPlan = plans.Select(p => new PlanDistributionDto(
                p.PlanCode, p.PlanName, p.Count,
                Math.Round(recurring.Where(r => r.PlanCode == p.PlanCode).Sum(r => Subscription.MonthlyPrice(r.Price, r.Period)), 2)))
            .OrderByDescending(p => p.Subscriptions).ThenBy(p => p.PlanCode).ToList();
        var mrr = Math.Round(recurring.Sum(r => Subscription.MonthlyPrice(r.Price, r.Period)), 2);

        var payments = db.Payments.IgnoreQueryFilters().AsNoTracking().Where(p => p.PaidAt >= start && p.PaidAt < end);
        var collected = await payments.SumAsync(p => (decimal?)p.Amount, cancellationToken) ?? 0m;
        var refunded = await db.PaymentRefunds.IgnoreQueryFilters().AsNoTracking().Where(r => r.RefundedAt >= start && r.RefundedAt < end)
            .SumAsync(r => (decimal?)r.Amount, cancellationToken) ?? 0m;
        var open = db.Invoices.IgnoreQueryFilters().AsNoTracking().Where(i => i.Status == InvoiceStatus.Pending || i.Status == InvoiceStatus.Failed);
        var overdue = open.Where(i => i.DueOn < today);

        var renewalsPaid = await (from p in payments join i in db.Invoices.IgnoreQueryFilters().AsNoTracking() on p.InvoiceId equals i.Id where i.Kind == InvoiceKind.Renewal select p.Id).CountAsync(cancellationToken);
        var activationsPaid = await (from p in payments join i in db.Invoices.IgnoreQueryFilters().AsNoTracking() on p.InvoiceId equals i.Id where i.Kind == InvoiceKind.Activation select p.Id).CountAsync(cancellationToken);

        var events = db.SubscriptionEvents.IgnoreQueryFilters().AsNoTracking().Where(e => e.OccurredAt >= start && e.OccurredAt < end);
        var freeRenewals = await events.CountAsync(e => e.Type == SubscriptionEventType.Renewed && e.Detail != null && e.Detail.Contains("automática"), cancellationToken);
        var cancellations = await events.CountAsync(e => e.Type == SubscriptionEventType.Cancelled, cancellationToken);

        // Conversión de prueba: de las pruebas que terminaron en el período, cuántas pasaron a pago y cuántas se perdieron (vencieron o se cancelaron).
        var converted = await events.CountAsync(e => e.Type == SubscriptionEventType.Activated && e.FromStatus == SubscriptionStatus.Trialing, cancellationToken);
        var lost = await events.CountAsync(
            e => (e.Type == SubscriptionEventType.Expired || e.Type == SubscriptionEventType.Cancelled) && e.FromStatus == SubscriptionStatus.Trialing, cancellationToken);

        return new MetricsDto(
            from, to, "USD",
            organizations, suspendedOrganizations, Math.Max(0, organizations - withSubscription),
            byStatus, byStatus["Trialing"], byStatus["PastDue"], await overdue.CountAsync(cancellationToken), await overdue.SumAsync(i => (decimal?)i.Amount, cancellationToken) ?? 0m,
            byPlan,
            collected, refunded, collected - refunded, await open.SumAsync(i => (decimal?)i.Amount, cancellationToken) ?? 0m,
            renewalsPaid, activationsPaid, freeRenewals, cancellations,
            converted, lost, converted + lost == 0 ? null : Math.Round(converted * 100m / (converted + lost), 1),
            mrr, mrr * 12, MrrFormula);
    }
}
