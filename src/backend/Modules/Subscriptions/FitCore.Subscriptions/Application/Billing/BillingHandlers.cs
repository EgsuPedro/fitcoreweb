using FitCore.Application.Core;
using FitCore.SharedKernel;
using FitCore.Subscriptions.Application.Subscriptions;
using FitCore.Subscriptions.Domain;
using FitCore.Subscriptions.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FitCore.Subscriptions.Application.Billing;

// La autorización de plataforma (platform.billing.read / .manage) se exige en el endpoint. Estas operaciones cruzan gimnasios a propósito
// (IgnoreQueryFilters); el aislamiento real lo da la política RLS de la base: solo el contexto «platform» escribe, y cada gimnasio lee lo suyo.

internal static class BillingStore
{
    public static async Task<string> NextNumberAsync(SubscriptionsDbContext db, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var n = (await db.Database.SqlQuery<long>($"SELECT nextval('subscriptions.invoice_seq') AS \"Value\"").ToListAsync(cancellationToken))[0];
        return $"FC-{now.UtcDateTime.Year}-{n:D6}";
    }

    /// <summary>
    /// Emite la factura del período siguiente de una suscripción: activación (en prueba o vencida) o renovación. El importe sale de la suscripción, o del
    /// plan al que pasará si hay una baja programada. Una factura abierta por suscripción.
    /// </summary>
    public static async Task<Result<SubscriptionInvoice>> IssueAsync(
        SubscriptionsDbContext db, Subscription subscription, DateOnly today, Guid? actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (subscription.Status is SubscriptionStatus.Suspended or SubscriptionStatus.Cancelled)
        {
            return BillingErrors.SubscriptionNotBillable;
        }

        if (subscription.CancelAtPeriodEnd)
        {
            return BillingErrors.CancellationScheduled;
        }

        if (await db.Invoices.IgnoreQueryFilters().AnyAsync(i => i.SubscriptionId == subscription.Id && (i.Status == InvoiceStatus.Pending || i.Status == InvoiceStatus.Failed), cancellationToken))
        {
            return BillingErrors.OpenInvoiceExists;
        }

        var billed = await PlanSnapshots.PendingAsync(db, subscription, cancellationToken) ?? subscription.ToSnapshot();
        var activation = subscription.Status is SubscriptionStatus.Trialing or SubscriptionStatus.Expired;
        var start = activation || subscription.CurrentPeriodEnd < today ? today : subscription.CurrentPeriodEnd;
        var due = subscription.Status switch
        {
            SubscriptionStatus.Trialing => subscription.TrialEndsOn ?? subscription.CurrentPeriodEnd,
            SubscriptionStatus.Expired => today,
            _ => subscription.CurrentPeriodEnd
        };

        var number = await NextNumberAsync(db, now, cancellationToken);
        var invoice = SubscriptionInvoice.Issue(number, subscription, billed, activation ? InvoiceKind.Activation : InvoiceKind.Renewal, start, due, actor, now);
        if (invoice.IsFailure)
        {
            return invoice.Error;
        }

        db.Invoices.Add(invoice.Value);
        subscription.RecordBilling(SubscriptionEventType.InvoiceIssued, actor, now, $"{invoice.Value.Number} · {invoice.Value.Amount:0.00} {invoice.Value.Currency} · vence {due:yyyy-MM-dd}");
        return invoice.Value;
    }

    /// <summary>Anula las facturas abiertas de una suscripción (cambio de plan, cancelación): su importe o su vigencia ya no corresponden.</summary>
    public static async Task VoidOpenAsync(
        SubscriptionsDbContext db, Subscription subscription, string reason, Guid? actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var open = await db.Invoices.IgnoreQueryFilters()
            .Where(i => i.SubscriptionId == subscription.Id && (i.Status == InvoiceStatus.Pending || i.Status == InvoiceStatus.Failed))
            .ToListAsync(cancellationToken);
        foreach (var invoice in open)
        {
            if (invoice.Void(reason, actor, now).IsSuccess)
            {
                subscription.RecordBilling(SubscriptionEventType.InvoiceVoided, actor, now, $"{invoice.Number} · {reason}");
            }
        }
    }

    public static async Task<Result<InvoiceDto>> ToDtoAsync(SubscriptionsDbContext db, Guid invoiceId, DateOnly today, CancellationToken cancellationToken)
    {
        var row = await (from i in db.Invoices.IgnoreQueryFilters().AsNoTracking()
                         join t in db.Tenants.AsNoTracking() on i.TenantId equals t.Id
                         where i.Id == invoiceId
                         select new { i, t }).FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return BillingErrors.InvoiceNotFound;
        }

        var payment = await db.Payments.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(p => p.InvoiceId == invoiceId, cancellationToken);
        var attempts = await db.PaymentAttempts.IgnoreQueryFilters().AsNoTracking().Where(a => a.InvoiceId == invoiceId).OrderByDescending(a => a.AttemptedAt).ToListAsync(cancellationToken);
        var inv = row.i;
        return new InvoiceDto(
            inv.Id, inv.Number, inv.TenantId, row.t.Slug, row.t.Name, inv.SubscriptionId, inv.Kind.ToString(), inv.Status.ToString(), inv.Amount, inv.Currency,
            inv.PlanCode, inv.PlanName, inv.PlanVersion, inv.PeriodStart, inv.PeriodEnd, inv.IssuedAt, inv.DueOn, inv.PaidAt, inv.VoidedAt, inv.VoidReason,
            inv.RefundedAmount, inv.AttemptCount, inv.IsOpen, SubscriptionInvoice.Notice,
            payment is null ? null : PaymentDto(payment, inv.Number, row.t.Slug, row.t.Name),
            [.. attempts.Select(a => new PaymentAttemptDto(a.Id, a.Method.ToString(), a.Reference, a.Reason, a.AttemptedAt))]);
    }

    public static SubscriptionPaymentDto PaymentDto(SubscriptionPayment p, string invoiceNumber, string tenantSlug, string tenantName) => new(
        p.Id, p.InvoiceId, invoiceNumber, p.TenantId, tenantSlug, tenantName, p.Amount, p.Currency, p.Method.ToString(), p.Reference, p.Provider,
        p.PaidAt, p.ConfirmedAt, p.RefundedAmount, p.Notes);

    public static Task<Subscription?> SubscriptionOfAsync(SubscriptionsDbContext db, Guid subscriptionId, CancellationToken cancellationToken) =>
        db.Subscriptions.IgnoreQueryFilters()
            .Include(s => s.PlanChanges.Where(c => c.Status == PlanChangeStatus.Scheduled))
            .FirstOrDefaultAsync(s => s.Id == subscriptionId, cancellationToken);
}

internal sealed class IssueInvoiceHandler(SubscriptionsDbContext db, IClock clock, ICurrentUser currentUser) : ICommandHandler<IssueInvoiceCommand, InvoiceDto>
{
    public async Task<Result<InvoiceDto>> HandleAsync(IssueInvoiceCommand command, CancellationToken cancellationToken)
    {
        var tenant = await SubscriptionStore.TenantAsync(db, command.TenantId, cancellationToken);
        if (tenant is null)
        {
            return SubscriptionAppErrors.TenantNotFound;
        }

        var subscription = await SubscriptionStore.CurrentAsync(db, command.TenantId, cancellationToken);
        if (subscription is null)
        {
            return SubscriptionAppErrors.NotFound;
        }

        var today = SubscriptionStore.Today(clock, tenant.TimeZone);
        var invoice = await BillingStore.IssueAsync(db, subscription, today, currentUser.UserId, clock.UtcNow, cancellationToken);
        if (invoice.IsFailure)
        {
            return invoice.Error;
        }

        await db.SaveChangesAsync(cancellationToken);
        return await BillingStore.ToDtoAsync(db, invoice.Value.Id, today, cancellationToken);
    }
}

internal sealed class PayInvoiceHandler(SubscriptionsDbContext db, IClock clock, ICurrentUser currentUser)
    : ICommandHandler<PayInvoiceCommand, InvoiceDto>
{
    public async Task<Result<InvoiceDto>> HandleAsync(PayInvoiceCommand command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var key = command.IdempotencyKey?.Trim();
        if (string.IsNullOrEmpty(key) || key.Length > 100)
        {
            return BillingErrors.KeyRequired;
        }

        var invoice = await db.Invoices.IgnoreQueryFilters().FirstOrDefaultAsync(i => i.Id == command.InvoiceId, cancellationToken);
        if (invoice is null)
        {
            return BillingErrors.InvoiceNotFound;
        }

        var tenant = await SubscriptionStore.TenantAsync(db, invoice.TenantId, cancellationToken);
        if (tenant is null)
        {
            return SubscriptionAppErrors.TenantNotFound;
        }

        var today = SubscriptionStore.Today(clock, tenant.TimeZone);

        // Repetir la misma solicitud (doble clic, reintento de red) devuelve el mismo resultado sin volver a cobrar.
        var replay = await db.Payments.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(p => p.IdempotencyKey == key, cancellationToken);
        if (replay is not null)
        {
            return replay.InvoiceId == invoice.Id ? await BillingStore.ToDtoAsync(db, invoice.Id, today, cancellationToken) : BillingErrors.KeyReused;
        }

        if (request.Method == SubscriptionPaymentMethod.Card)
        {
            return BillingErrors.CardNotManual;
        }

        // El importe y la moneda recibidos deben ser exactamente los de la factura: un pago que no coincide no confirma nada.
        if (request.Amount != invoice.Amount || !string.Equals(request.Currency, invoice.Currency, StringComparison.Ordinal))
        {
            return BillingErrors.AmountMismatch;
        }

        var now = clock.UtcNow;
        var paidAt = request.PaidAt ?? now;
        if (paidAt > now.AddMinutes(5))
        {
            return BillingErrors.FutureDate;
        }

        if (!string.IsNullOrWhiteSpace(request.Reference) && request.Method != SubscriptionPaymentMethod.Cash
            && await db.Payments.IgnoreQueryFilters().AnyAsync(p => p.Method == request.Method && p.Reference != null && EF.Functions.ILike(p.Reference, LikePattern.Exact(request.Reference), "\\"), cancellationToken))
        {
            return Error.Conflict("billing.reference_duplicated", "Esa referencia ya está registrada en otro pago.");
        }

        var subscription = await BillingStore.SubscriptionOfAsync(db, invoice.SubscriptionId, cancellationToken);
        if (subscription is null)
        {
            return SubscriptionAppErrors.NotFound;
        }

        if (subscription.Status == SubscriptionStatus.Suspended)
        {
            return BillingErrors.SubscriptionSuspended;
        }

        // El pago se aplica a la suscripción en la misma transacción: si la suscripción no lo admite (cancelación programada, estado inválido),
        // no queda registrado ningún pago a medias.
        var note = $"Pago {invoice.Number}";
        var applied = subscription.Status switch
        {
            SubscriptionStatus.Trialing => subscription.Activate(today, currentUser.UserId, now, note),
            SubscriptionStatus.Active or SubscriptionStatus.PastDue or SubscriptionStatus.Expired => subscription.Renew(
                await PlanSnapshots.PendingAsync(db, subscription, cancellationToken), today, 3650, currentUser.UserId, now, note),
            _ => Result.Failure(BillingErrors.SubscriptionNotBillable)
        };
        if (applied.IsFailure)
        {
            return applied.Error;
        }

        var marked = invoice.MarkPaid(subscription.CurrentPeriodStart, subscription.CurrentPeriodEnd, paidAt, currentUser.UserId, now);
        if (marked.IsFailure)
        {
            return marked.Error;
        }

        db.Payments.Add(SubscriptionPayment.Confirm(invoice, request.Method, request.Reference, key, paidAt, currentUser.UserId, request.Notes, now));
        subscription.RecordBilling(SubscriptionEventType.PaymentConfirmed, currentUser.UserId, now, $"{invoice.Number} · {invoice.Amount:0.00} {invoice.Currency} · {request.Method}");
        await db.SaveChangesAsync(cancellationToken);
        return await BillingStore.ToDtoAsync(db, invoice.Id, today, cancellationToken);
    }
}

internal sealed class RecordFailedAttemptHandler(SubscriptionsDbContext db, IClock clock, ICurrentUser currentUser) : ICommandHandler<RecordFailedAttemptCommand, InvoiceDto>
{
    public async Task<Result<InvoiceDto>> HandleAsync(RecordFailedAttemptCommand command, CancellationToken cancellationToken)
    {
        var invoice = await db.Invoices.IgnoreQueryFilters().FirstOrDefaultAsync(i => i.Id == command.InvoiceId, cancellationToken);
        if (invoice is null)
        {
            return BillingErrors.InvoiceNotFound;
        }

        var now = clock.UtcNow;
        var failed = invoice.RecordFailedAttempt(currentUser.UserId, now);
        if (failed.IsFailure)
        {
            return failed.Error;
        }

        var subscription = await BillingStore.SubscriptionOfAsync(db, invoice.SubscriptionId, cancellationToken);
        db.PaymentAttempts.Add(new SubscriptionPaymentAttempt
        {
            Id = Guid.CreateVersion7(),
            TenantId = invoice.TenantId,
            InvoiceId = invoice.Id,
            Method = command.Request.Method,
            Reference = string.IsNullOrWhiteSpace(command.Request.Reference) ? null : command.Request.Reference.Trim(),
            Reason = command.Request.Reason.Trim(),
            AttemptedAt = now,
            RecordedBy = currentUser.UserId
        });

        // Un cobro fallido NO activa ni renueva nada: la suscripción queda como estaba, solo se deja constancia.
        subscription?.RecordBilling(SubscriptionEventType.PaymentFailed, currentUser.UserId, now, $"{invoice.Number} · intento {invoice.AttemptCount} · {command.Request.Reason.Trim()}");
        await db.SaveChangesAsync(cancellationToken);

        var tenant = await SubscriptionStore.TenantAsync(db, invoice.TenantId, cancellationToken);
        return await BillingStore.ToDtoAsync(db, invoice.Id, SubscriptionStore.Today(clock, tenant?.TimeZone ?? "America/Guayaquil"), cancellationToken);
    }
}

internal sealed class VoidInvoiceHandler(SubscriptionsDbContext db, IClock clock, ICurrentUser currentUser) : ICommandHandler<VoidInvoiceCommand, InvoiceDto>
{
    public async Task<Result<InvoiceDto>> HandleAsync(VoidInvoiceCommand command, CancellationToken cancellationToken)
    {
        var invoice = await db.Invoices.IgnoreQueryFilters().FirstOrDefaultAsync(i => i.Id == command.InvoiceId, cancellationToken);
        if (invoice is null)
        {
            return BillingErrors.InvoiceNotFound;
        }

        var now = clock.UtcNow;
        var voided = invoice.Void(command.Request.Reason, currentUser.UserId, now);
        if (voided.IsFailure)
        {
            return voided.Error;
        }

        var subscription = await BillingStore.SubscriptionOfAsync(db, invoice.SubscriptionId, cancellationToken);
        subscription?.RecordBilling(SubscriptionEventType.InvoiceVoided, currentUser.UserId, now, $"{invoice.Number} · {command.Request.Reason.Trim()}");
        await db.SaveChangesAsync(cancellationToken);

        var tenant = await SubscriptionStore.TenantAsync(db, invoice.TenantId, cancellationToken);
        return await BillingStore.ToDtoAsync(db, invoice.Id, SubscriptionStore.Today(clock, tenant?.TimeZone ?? "America/Guayaquil"), cancellationToken);
    }
}

internal sealed class RefundPaymentHandler(SubscriptionsDbContext db, IClock clock, ICurrentUser currentUser) : ICommandHandler<RefundPaymentCommand, InvoiceDto>
{
    public async Task<Result<InvoiceDto>> HandleAsync(RefundPaymentCommand command, CancellationToken cancellationToken)
    {
        var key = command.IdempotencyKey?.Trim();
        if (string.IsNullOrEmpty(key) || key.Length > 100)
        {
            return BillingErrors.KeyRequired;
        }

        var payment = await db.Payments.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.Id == command.PaymentId, cancellationToken);
        if (payment is null)
        {
            return BillingErrors.PaymentNotFound;
        }

        var tenant = await SubscriptionStore.TenantAsync(db, payment.TenantId, cancellationToken);
        var today = SubscriptionStore.Today(clock, tenant?.TimeZone ?? "America/Guayaquil");

        var replay = await db.PaymentRefunds.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(r => r.IdempotencyKey == key, cancellationToken);
        if (replay is not null)
        {
            return replay.PaymentId == payment.Id ? await BillingStore.ToDtoAsync(db, payment.InvoiceId, today, cancellationToken) : BillingErrors.KeyReused;
        }

        var invoice = await db.Invoices.IgnoreQueryFilters().FirstAsync(i => i.Id == payment.InvoiceId, cancellationToken);
        var now = clock.UtcNow;
        var refunded = invoice.ApplyRefund(command.Request.Amount, currentUser.UserId, now);
        if (refunded.IsFailure)
        {
            return refunded.Error;
        }

        payment.AddRefund(command.Request.Amount);
        db.PaymentRefunds.Add(new SubscriptionPaymentRefund
        {
            Id = Guid.CreateVersion7(),
            TenantId = payment.TenantId,
            PaymentId = payment.Id,
            InvoiceId = invoice.Id,
            Amount = command.Request.Amount,
            Reason = command.Request.Reason.Trim(),
            IdempotencyKey = key,
            RefundedAt = now,
            RecordedBy = currentUser.UserId
        });

        // Reembolsar registra la devolución; no cambia el estado de la suscripción (cancelarla o suspenderla es otra decisión, con su motivo).
        var subscription = await BillingStore.SubscriptionOfAsync(db, invoice.SubscriptionId, cancellationToken);
        subscription?.RecordBilling(SubscriptionEventType.PaymentRefunded, currentUser.UserId, now, $"{invoice.Number} · {command.Request.Amount:0.00} {invoice.Currency} · {command.Request.Reason.Trim()}");
        await db.SaveChangesAsync(cancellationToken);
        return await BillingStore.ToDtoAsync(db, invoice.Id, today, cancellationToken);
    }
}

internal sealed class GetInvoiceHandler(SubscriptionsDbContext db, IClock clock) : IQueryHandler<GetInvoiceQuery, InvoiceDto>
{
    public Task<Result<InvoiceDto>> HandleAsync(GetInvoiceQuery query, CancellationToken cancellationToken) =>
        BillingStore.ToDtoAsync(db, query.Id, DateOnly.FromDateTime(clock.UtcNow.UtcDateTime), cancellationToken);
}

internal sealed class ListInvoicesHandler(SubscriptionsDbContext db, IClock clock) : IQueryHandler<ListInvoicesQuery, PagedResult<InvoiceSummaryDto>>
{
    public async Task<Result<PagedResult<InvoiceSummaryDto>>> HandleAsync(ListInvoicesQuery query, CancellationToken cancellationToken)
    {
        var rows = from i in db.Invoices.IgnoreQueryFilters().AsNoTracking()
                   join t in db.Tenants.AsNoTracking() on i.TenantId equals t.Id
                   select new { i, t };

        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            if (!Enum.TryParse<InvoiceStatus>(query.Status, ignoreCase: true, out var status))
            {
                return Error.Validation("billing.status_invalid", "El estado no es válido.");
            }

            rows = rows.Where(x => x.i.Status == status);
        }

        if (query.TenantId is { } tenantId)
        {
            rows = rows.Where(x => x.i.TenantId == tenantId);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var pattern = LikePattern.Contains(query.Search);
            rows = rows.Where(x => EF.Functions.ILike(x.i.Number, pattern, "\\") || EF.Functions.ILike(x.t.Name, pattern, "\\") || EF.Functions.ILike(x.t.Slug, pattern, "\\"));
        }

        if (query.From is { } from)
        {
            var start = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            rows = rows.Where(x => x.i.IssuedAt >= start);
        }

        if (query.To is { } to)
        {
            var end = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            rows = rows.Where(x => x.i.IssuedAt < end);
        }

        if (query.OverdueOnly)
        {
            var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
            rows = rows.Where(x => (x.i.Status == InvoiceStatus.Pending || x.i.Status == InvoiceStatus.Failed) && x.i.DueOn < today);
        }

        var paged = await rows.OrderByDescending(x => x.i.IssuedAt).ThenByDescending(x => x.i.Number).ToPagedAsync(query.Page, cancellationToken);
        return new PagedResult<InvoiceSummaryDto>(
            [.. paged.Items.Select(x => new InvoiceSummaryDto(
                x.i.Id, x.i.Number, x.i.TenantId, x.t.Slug, x.t.Name, x.i.Kind.ToString(), x.i.Status.ToString(), x.i.Amount, x.i.Currency, x.i.PlanCode,
                x.i.PeriodStart, x.i.PeriodEnd, x.i.IssuedAt, x.i.DueOn, x.i.PaidAt, x.i.RefundedAmount, x.i.AttemptCount))],
            paged.Page, paged.PageSize, paged.TotalCount);
    }
}

internal sealed class ListSubscriptionPaymentsHandler(SubscriptionsDbContext db) : IQueryHandler<ListSubscriptionPaymentsQuery, PagedResult<SubscriptionPaymentDto>>
{
    public async Task<Result<PagedResult<SubscriptionPaymentDto>>> HandleAsync(ListSubscriptionPaymentsQuery query, CancellationToken cancellationToken)
    {
        var rows = from p in db.Payments.IgnoreQueryFilters().AsNoTracking()
                   join i in db.Invoices.IgnoreQueryFilters().AsNoTracking() on p.InvoiceId equals i.Id
                   join t in db.Tenants.AsNoTracking() on p.TenantId equals t.Id
                   select new { p, i.Number, t };

        if (query.TenantId is { } tenantId)
        {
            rows = rows.Where(x => x.p.TenantId == tenantId);
        }

        if (query.From is { } from)
        {
            var start = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            rows = rows.Where(x => x.p.PaidAt >= start);
        }

        if (query.To is { } to)
        {
            var end = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            rows = rows.Where(x => x.p.PaidAt < end);
        }

        var paged = await rows.OrderByDescending(x => x.p.PaidAt).ToPagedAsync(query.Page, cancellationToken);
        return new PagedResult<SubscriptionPaymentDto>(
            [.. paged.Items.Select(x => BillingStore.PaymentDto(x.p, x.Number, x.t.Slug, x.t.Name))], paged.Page, paged.PageSize, paged.TotalCount);
    }
}

internal sealed class GetBillingSummaryHandler(SubscriptionsDbContext db, IClock clock) : IQueryHandler<GetBillingSummaryQuery, BillingSummaryDto>
{
    public async Task<Result<BillingSummaryDto>> HandleAsync(GetBillingSummaryQuery query, CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        var to = query.To ?? today;
        var from = query.From ?? to.AddDays(-29);
        if (from > to)
        {
            return Error.Validation("billing.period_invalid", "La fecha inicial no puede ser posterior a la final.");
        }

        if (to.DayNumber - from.DayNumber > 730)
        {
            return Error.Validation("billing.period_too_long", "El período no puede superar 2 años.");
        }

        var start = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var end = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        var invoices = db.Invoices.IgnoreQueryFilters().AsNoTracking();
        var issued = invoices.Where(i => i.IssuedAt >= start && i.IssuedAt < end && i.Status != InvoiceStatus.Void);
        var payments = db.Payments.IgnoreQueryFilters().AsNoTracking().Where(p => p.PaidAt >= start && p.PaidAt < end);
        var refunds = db.PaymentRefunds.IgnoreQueryFilters().AsNoTracking().Where(r => r.RefundedAt >= start && r.RefundedAt < end);
        var open = invoices.Where(i => i.Status == InvoiceStatus.Pending || i.Status == InvoiceStatus.Failed);

        var issuedAmount = await issued.SumAsync(i => (decimal?)i.Amount, cancellationToken) ?? 0m;
        var collected = await payments.SumAsync(p => (decimal?)p.Amount, cancellationToken) ?? 0m;
        var refunded = await refunds.SumAsync(r => (decimal?)r.Amount, cancellationToken) ?? 0m;
        var pending = await open.SumAsync(i => (decimal?)i.Amount, cancellationToken) ?? 0m;
        var overdue = open.Where(i => i.DueOn < today);

        return new BillingSummaryDto(
            from, to, "USD",
            issuedAmount, await issued.CountAsync(cancellationToken),
            collected, await payments.CountAsync(cancellationToken),
            refunded, collected - refunded,
            pending, await open.CountAsync(cancellationToken),
            await overdue.SumAsync(i => (decimal?)i.Amount, cancellationToken) ?? 0m, await overdue.CountAsync(cancellationToken),
            await invoices.CountAsync(i => i.Status == InvoiceStatus.Failed, cancellationToken));
    }
}
