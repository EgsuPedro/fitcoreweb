using System.Text.RegularExpressions;
using FitCore.Subscriptions.Domain;
using FitCore.Subscriptions.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FitCore.Subscriptions.Application.Portal;

/// <summary>
/// Genera los avisos comerciales internos. Es idempotente: cada aviso lleva una clave única por gimnasio (<see cref="SubscriptionNotice.DedupeKey"/>), de modo
/// que correr el proceso muchas veces, o dos instancias a la vez, no duplica nada. Debe ejecutarse con el contexto de plataforma (la base solo deja
/// escribir avisos a la plataforma). No envía nada fuera del portal: no hay canales externos.
/// </summary>
internal static partial class NoticeGenerator
{
    /// <summary>Hechos del historial más viejos que esto ya no generan aviso (evita inundar un gimnasio con historia vieja).</summary>
    private const int EventWindowDays = 14;
    private const int TrialEndingDays = 3;
    private const int DueSoonDays = 7;
    private const int GraceEndingDays = 2;

    [GeneratedRegex(@"FC-\d{4}-\d{6}")]
    private static partial Regex InvoiceNumber();

    public static async Task<int> GenerateAsync(SubscriptionsDbContext db, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var candidates = new List<SubscriptionNotice>();

        var since = now.AddDays(-EventWindowDays);
        var events = await (from e in db.SubscriptionEvents.IgnoreQueryFilters().AsNoTracking()
                            join s in db.Subscriptions.IgnoreQueryFilters().AsNoTracking() on e.SubscriptionId equals s.Id
                            where e.OccurredAt >= since
                            select new { e.Id, e.TenantId, e.SubscriptionId, e.Type, e.OccurredAt, e.Detail, s.PlanName, s.CurrentPeriodEnd, s.GraceEndsOn })
            .ToListAsync(cancellationToken);
        foreach (var e in events)
        {
            if (NoticeTypes.For(e.Type) is not { } notice)
            {
                continue;
            }

            var date = e.Type switch
            {
                SubscriptionEventType.PastDue => e.GraceEndsOn,
                SubscriptionEventType.Activated or SubscriptionEventType.Renewed or SubscriptionEventType.CancellationScheduled or SubscriptionEventType.PlanChangeScheduled
                    or SubscriptionEventType.Extended => e.CurrentPeriodEnd,
                _ => (DateOnly?)null
            };
            var reference = e.Detail is null ? null : InvoiceNumber().Match(e.Detail) is { Success: true } m ? m.Value : null;
            candidates.Add(New(e.TenantId, e.SubscriptionId, notice.Type, notice.Severity, $"event:{e.Id}", e.PlanName, date, reference, e.OccurredAt));
        }

        // Avisos de calendario: se generan mientras la situación esté cerca, una sola vez por fecha de vencimiento.
        var current = await db.Subscriptions.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.Status == SubscriptionStatus.Trialing || s.Status == SubscriptionStatus.Active || s.Status == SubscriptionStatus.PastDue)
            .ToListAsync(cancellationToken);
        foreach (var s in current)
        {
            if (s is { Status: SubscriptionStatus.Trialing, TrialEndsOn: { } trialEnds } && trialEnds.DayNumber - today.DayNumber is >= 0 and <= TrialEndingDays)
            {
                candidates.Add(New(s.TenantId, s.Id, NoticeTypes.TrialEnding, NoticeSeverity.Warning, $"trial_ending:{s.Id}:{trialEnds:yyyyMMdd}", s.PlanName, trialEnds, null, now));
            }

            if (s is { Status: SubscriptionStatus.Active, Price: > 0, CancelAtPeriodEnd: false } && s.NextDueOn.DayNumber - today.DayNumber is >= 0 and <= DueSoonDays)
            {
                candidates.Add(New(s.TenantId, s.Id, NoticeTypes.DueSoon, NoticeSeverity.Info, $"due_soon:{s.Id}:{s.NextDueOn:yyyyMMdd}", s.PlanName, s.NextDueOn, null, now));
            }

            if (s is { Status: SubscriptionStatus.PastDue, GraceEndsOn: { } graceEnds } && graceEnds.DayNumber - today.DayNumber is >= 0 and <= GraceEndingDays)
            {
                candidates.Add(New(s.TenantId, s.Id, NoticeTypes.GraceEnding, NoticeSeverity.Critical, $"grace_ending:{s.Id}:{graceEnds:yyyyMMdd}", s.PlanName, graceEnds, null, now));
            }
        }

        if (candidates.Count == 0)
        {
            return 0;
        }

        var keys = candidates.Select(c => c.DedupeKey).ToList();
        var existing = (await db.Notices.IgnoreQueryFilters().AsNoTracking().Where(n => keys.Contains(n.DedupeKey)).Select(n => n.DedupeKey).ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        var fresh = candidates.Where(c => !existing.Contains(c.DedupeKey)).ToList();
        if (fresh.Count == 0)
        {
            return 0;
        }

        db.Notices.AddRange(fresh);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return fresh.Count;
        }
        catch (DbUpdateException)
        {
            // Otra instancia generó alguno al mismo tiempo (índice único): se descarta lo pendiente y la próxima pasada completa lo que falte.
            db.ChangeTracker.Clear();
            return 0;
        }
    }

    private static SubscriptionNotice New(
        Guid tenantId, Guid subscriptionId, string type, NoticeSeverity severity, string key, string planName, DateOnly? date, string? reference, DateTimeOffset at) =>
        new()
        {
            Id = Guid.CreateVersion7(), TenantId = tenantId, SubscriptionId = subscriptionId, Type = type, Severity = severity, DedupeKey = key,
            PlanName = planName, RelatedDate = date, Reference = reference, CreatedAt = at
        };
}
