using FitCore.Application.Core;
using FitCore.SharedKernel;
using FitCore.Subscriptions.Application.Billing;
using FitCore.Subscriptions.Application.Subscriptions;
using FitCore.Subscriptions.Domain;
using FitCore.Subscriptions.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FitCore.Subscriptions.Application.Providers;

/// <summary>
/// Aplica un evento ya verificado del proveedor. La autenticidad (firma y marca de tiempo) se comprobó antes, en <see cref="IPaymentProviderGateway"/>; aquí se
/// comprueba que el <b>contenido</b> concuerde con lo que FitCore 360 emitió: la factura existe, el importe y la moneda son exactamente los suyos, sigue abierta
/// y la suscripción admite el pago. Si algo no concuerda no cambia nada: se deja constancia del evento como <c>Rejected</c> para revisarlo a mano. Es idempotente:
/// el par (proveedor, id de evento) es único, así que un reenvío —o dos entregas simultáneas— no aplica nada dos veces. Actúa con el contexto de plataforma.
/// </summary>
internal sealed class ProcessProviderEventHandler(
    SubscriptionsDbContext db, ITenantContext tenantContext, IPaymentProviderGateway gateway, IClock clock, ILogger<ProcessProviderEventHandler> logger)
    : ICommandHandler<ProcessProviderEventCommand, WebhookReceiptDto>
{
    private static readonly WebhookReceiptDto DuplicateReceipt = new(true, "Duplicate", true);

    public async Task<Result<WebhookReceiptDto>> HandleAsync(ProcessProviderEventCommand command, CancellationToken cancellationToken)
    {
        var platformId = await db.Tenants.AsNoTracking().Where(t => t.Slug == SubscriptionStore.PlatformSlug).Select(t => t.Id).FirstOrDefaultAsync(cancellationToken);
        if (platformId == Guid.Empty)
        {
            return Error.Failure("payments.platform_missing", "No existe el gimnasio de plataforma.");
        }

        tenantContext.Set(platformId);
        var n = command.Notification;
        var provider = gateway.Code;

        if (await db.ProviderEvents.AnyAsync(e => e.Provider == provider && e.ExternalEventId == n.ExternalEventId, cancellationToken))
        {
            return DuplicateReceipt;
        }

        try
        {
            return n.Type switch
            {
                ProviderEventType.PaymentSucceeded => await SucceededAsync(provider, n, cancellationToken),
                ProviderEventType.PaymentFailed => await FailedAsync(provider, n, cancellationToken),
                _ => await RefundedAsync(provider, n, cancellationToken)
            };
        }
        catch (DbUpdateException)
        {
            // Si otra entrega del mismo evento ganó la carrera (índice único), lo que quedó es lo correcto y esta es un duplicado; si no, es un error real.
            if (await AlreadyRecordedAsync(provider, n.ExternalEventId, cancellationToken))
            {
                return DuplicateReceipt;
            }

            throw;
        }
    }

    private async Task<bool> AlreadyRecordedAsync(string provider, string eventId, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        return await db.ProviderEvents.AsNoTracking().AnyAsync(e => e.Provider == provider && e.ExternalEventId == eventId, cancellationToken);
    }

    private async Task<Result<WebhookReceiptDto>> SucceededAsync(string provider, ProviderNotification n, CancellationToken cancellationToken)
    {
        var invoice = await db.Invoices.IgnoreQueryFilters().FirstOrDefaultAsync(i => i.Number == n.InvoiceNumber, cancellationToken);
        if (invoice is null)
        {
            return await ResolveAsync(provider, n, null, ProviderEventOutcome.Rejected, BillingErrors.InvoiceNotFound.Code, cancellationToken);
        }

        if (n.Amount != invoice.Amount || !string.Equals(n.Currency, invoice.Currency, StringComparison.Ordinal))
        {
            return await ResolveAsync(provider, n, invoice, ProviderEventOutcome.Rejected, BillingErrors.AmountMismatch.Code, cancellationToken);
        }

        // La misma referencia del proveedor ya confirmó un pago: es el mismo cobro llegando con otro id de evento.
        if (await db.Payments.IgnoreQueryFilters().AnyAsync(p => p.Provider == provider && p.ProviderReference == n.ProviderReference, cancellationToken))
        {
            return await ResolveAsync(provider, n, invoice, ProviderEventOutcome.Ignored, "billing.already_paid", cancellationToken);
        }

        if (!invoice.IsOpen)
        {
            return await ResolveAsync(provider, n, invoice, ProviderEventOutcome.Rejected, "billing.invoice_not_open", cancellationToken);
        }

        var subscription = await BillingStore.SubscriptionOfAsync(db, invoice.SubscriptionId, cancellationToken);
        var tenant = await SubscriptionStore.TenantAsync(db, invoice.TenantId, cancellationToken);
        if (subscription is null || tenant is null)
        {
            return await ResolveAsync(provider, n, invoice, ProviderEventOutcome.Rejected, SubscriptionAppErrors.NotFound.Code, cancellationToken);
        }

        var now = clock.UtcNow;
        var paidAt = n.OccurredAt > now ? now : n.OccurredAt;
        var applied = await BillingStore.ApplyPaymentAsync(db, invoice, subscription, SubscriptionStore.Today(clock, tenant.TimeZone), paidAt, null, now, cancellationToken);
        if (applied.IsFailure)
        {
            return await ResolveAsync(provider, n, invoice, ProviderEventOutcome.Rejected, applied.Error.Code, cancellationToken);
        }

        db.Payments.Add(SubscriptionPayment.ConfirmFromProvider(invoice, provider, n.ProviderReference!, n.ExternalEventId, paidAt, now));
        subscription.RecordBilling(SubscriptionEventType.PaymentConfirmed, null, now, $"{invoice.Number} · {invoice.Amount:0.00} {invoice.Currency} · {provider}");
        return await ResolveAsync(provider, n, invoice, ProviderEventOutcome.Applied, null, cancellationToken, keepChanges: true);
    }

    private async Task<Result<WebhookReceiptDto>> FailedAsync(string provider, ProviderNotification n, CancellationToken cancellationToken)
    {
        var invoice = await db.Invoices.IgnoreQueryFilters().FirstOrDefaultAsync(i => i.Number == n.InvoiceNumber, cancellationToken);
        if (invoice is null)
        {
            return await ResolveAsync(provider, n, null, ProviderEventOutcome.Rejected, BillingErrors.InvoiceNotFound.Code, cancellationToken);
        }

        if (n.Amount != invoice.Amount || !string.Equals(n.Currency, invoice.Currency, StringComparison.Ordinal))
        {
            return await ResolveAsync(provider, n, invoice, ProviderEventOutcome.Rejected, BillingErrors.AmountMismatch.Code, cancellationToken);
        }

        var now = clock.UtcNow;
        var failed = invoice.RecordFailedAttempt(null, now);
        if (failed.IsFailure)
        {
            return await ResolveAsync(provider, n, invoice, ProviderEventOutcome.Rejected, failed.Error.Code, cancellationToken);
        }

        var reason = n.FailureReason ?? "Rechazado por el proveedor";
        db.PaymentAttempts.Add(new SubscriptionPaymentAttempt
        {
            Id = Guid.CreateVersion7(),
            TenantId = invoice.TenantId,
            InvoiceId = invoice.Id,
            Method = SubscriptionPaymentMethod.Card,
            Reference = n.ProviderReference,
            Reason = reason,
            AttemptedAt = now
        });

        // Un cobro fallido NO activa ni renueva nada: la suscripción queda como estaba, solo se deja constancia.
        var subscription = await BillingStore.SubscriptionOfAsync(db, invoice.SubscriptionId, cancellationToken);
        subscription?.RecordBilling(SubscriptionEventType.PaymentFailed, null, now, $"{invoice.Number} · intento {invoice.AttemptCount} · {provider}: {reason}");
        return await ResolveAsync(provider, n, invoice, ProviderEventOutcome.Applied, null, cancellationToken, keepChanges: true);
    }

    private async Task<Result<WebhookReceiptDto>> RefundedAsync(string provider, ProviderNotification n, CancellationToken cancellationToken)
    {
        var payment = await db.Payments.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.Provider == provider && p.ProviderReference == n.ProviderReference, cancellationToken);
        var invoice = payment is null ? null : await db.Invoices.IgnoreQueryFilters().FirstOrDefaultAsync(i => i.Id == payment.InvoiceId, cancellationToken);
        if (payment is null || invoice is null)
        {
            return await ResolveAsync(provider, n, null, ProviderEventOutcome.Rejected, BillingErrors.PaymentNotFound.Code, cancellationToken);
        }

        if (invoice.Number != n.InvoiceNumber || !string.Equals(n.Currency, payment.Currency, StringComparison.Ordinal))
        {
            return await ResolveAsync(provider, n, invoice, ProviderEventOutcome.Rejected, BillingErrors.AmountMismatch.Code, cancellationToken);
        }

        var now = clock.UtcNow;
        var refunded = invoice.ApplyRefund(n.Amount, null, now);
        if (refunded.IsFailure)
        {
            return await ResolveAsync(provider, n, invoice, ProviderEventOutcome.Rejected, refunded.Error.Code, cancellationToken);
        }

        payment.AddRefund(n.Amount);
        db.PaymentRefunds.Add(new SubscriptionPaymentRefund
        {
            Id = Guid.CreateVersion7(),
            TenantId = payment.TenantId,
            PaymentId = payment.Id,
            InvoiceId = invoice.Id,
            Amount = n.Amount,
            Reason = $"Reembolso informado por {provider}",
            IdempotencyKey = $"{provider}:{n.ExternalEventId}",
            RefundedAt = now
        });

        // Como el reembolso manual: registra la devolución y no cambia el estado de la suscripción.
        var subscription = await BillingStore.SubscriptionOfAsync(db, invoice.SubscriptionId, cancellationToken);
        subscription?.RecordBilling(SubscriptionEventType.PaymentRefunded, null, now, $"{invoice.Number} · {n.Amount:0.00} {invoice.Currency} · {provider}");
        return await ResolveAsync(provider, n, invoice, ProviderEventOutcome.Applied, null, cancellationToken, keepChanges: true);
    }

    /// <summary>
    /// Deja constancia del evento y guarda. Cuando no se aplicó, antes se <b>descarta todo lo pendiente</b> en memoria: así un pago que falló a medias nunca
    /// deja una suscripción activada sin su pago.
    /// </summary>
    private async Task<Result<WebhookReceiptDto>> ResolveAsync(
        string provider, ProviderNotification n, SubscriptionInvoice? invoice, ProviderEventOutcome outcome, string? code, CancellationToken cancellationToken,
        bool keepChanges = false)
    {
        var tenantId = invoice?.TenantId;
        var invoiceId = invoice?.Id;
        if (!keepChanges)
        {
            db.ChangeTracker.Clear();
            logger.LogWarning("Evento {EventId} del proveedor {Provider} no aplicado: {Outcome} {Code}", n.ExternalEventId, provider, outcome, code);
        }

        db.ProviderEvents.Add(new ProviderEventRecord
        {
            Id = Guid.CreateVersion7(),
            Provider = provider,
            ExternalEventId = n.ExternalEventId,
            Type = n.Type.ToString(),
            TenantId = tenantId,
            InvoiceId = invoiceId,
            InvoiceNumber = n.InvoiceNumber,
            ProviderReference = n.ProviderReference,
            Amount = n.Amount,
            Currency = n.Currency,
            OccurredAt = n.OccurredAt,
            ReceivedAt = clock.UtcNow,
            Outcome = outcome,
            OutcomeCode = code
        });
        await db.SaveChangesAsync(cancellationToken);
        return new WebhookReceiptDto(true, outcome.ToString(), false);
    }
}

internal sealed class GetProviderStatusHandler(IPaymentProviderGateway gateway, IOptions<PaymentProviderOptions> options) : IQueryHandler<GetProviderStatusQuery, ProviderStatusDto>
{
    public Task<Result<ProviderStatusDto>> HandleAsync(GetProviderStatusQuery query, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success(new ProviderStatusDto(
            gateway.IsConfigured, gateway.Code, SignedWebhookGateway.SignatureHeader, "/api/v1/webhooks/payments", options.Value.ToleranceSeconds,
            gateway.IsConfigured && !string.IsNullOrWhiteSpace(options.Value.PreviousWebhookSecret))));
}

internal sealed class ListProviderEventsHandler(SubscriptionsDbContext db) : IQueryHandler<ListProviderEventsQuery, PagedResult<ProviderEventDto>>
{
    public async Task<Result<PagedResult<ProviderEventDto>>> HandleAsync(ListProviderEventsQuery query, CancellationToken cancellationToken)
    {
        var rows = from e in db.ProviderEvents.AsNoTracking()
                   join t in db.Tenants.AsNoTracking() on e.TenantId equals t.Id into tenants
                   from t in tenants.DefaultIfEmpty()
                   select new { e, TenantName = t == null ? null : t.Name };

        if (!string.IsNullOrWhiteSpace(query.Outcome))
        {
            if (!Enum.TryParse<ProviderEventOutcome>(query.Outcome, ignoreCase: true, out var outcome))
            {
                return Error.Validation("payments.outcome_invalid", "El resultado no es válido.");
            }

            rows = rows.Where(x => x.e.Outcome == outcome);
        }

        if (query.TenantId is { } tenantId)
        {
            rows = rows.Where(x => x.e.TenantId == tenantId);
        }

        var paged = await rows.OrderByDescending(x => x.e.ReceivedAt).ThenByDescending(x => x.e.Id).ToPagedAsync(query.Page, cancellationToken);
        return new PagedResult<ProviderEventDto>(
            [.. paged.Items.Select(x => new ProviderEventDto(
                x.e.Id, x.e.Provider, x.e.ExternalEventId, x.e.Type, x.e.TenantId, x.TenantName, x.e.InvoiceId, x.e.InvoiceNumber, x.e.ProviderReference,
                x.e.Amount, x.e.Currency, x.e.OccurredAt, x.e.ReceivedAt, x.e.Outcome.ToString(), x.e.OutcomeCode))],
            paged.Page, paged.PageSize, paged.TotalCount);
    }
}
