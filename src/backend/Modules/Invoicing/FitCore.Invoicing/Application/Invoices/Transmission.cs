using System.Diagnostics;
using System.Text.Json;
using FitCore.Application.Core;
using FitCore.Invoicing.Application.Settings;
using FitCore.Invoicing.Domain;
using FitCore.Invoicing.Persistence;
using FitCore.Invoicing.Sri;
using FitCore.Platform.Domain;
using FitCore.Platform.Persistence;
using FitCore.Platform.Security;
using FitCore.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FitCore.Invoicing.Application.Invoices;

// ---- Modelos ----------------------------------------------------------------------------------------------------------------------

public sealed record SriMessageDto(string? Id, string? Text, string? Detail, string? Type);

/// <summary>Estado del trabajo de transmisión mientras está en curso. <c>Parked</c>: pasó el plazo sin resolverse y requiere que una persona lo reactive.</summary>
public sealed record InvoiceTransmissionDto(bool Parked, int Attempts, int Polls, string? LastError, DateTimeOffset NextAttemptAt, DateTimeOffset FirstAttemptAt, DateTimeOffset? LastAttemptAt);

public sealed record InvoiceAuthorizationDto(string Number, DateTimeOffset AuthorizedAt);

public sealed record InvoiceSriAttemptDto(string Operation, string Outcome, string? SriState, string? ErrorCode, int DurationMs, IReadOnlyList<SriMessageDto> Messages, DateTimeOffset At);

/// <summary>Pone el comprobante en la cola del SRI (firma, envío y consulta de autorización las hace el Worker). Si fue devuelto o está estacionado, lo reactiva. Idempotente.</summary>
public sealed record SubmitInvoiceCommand(Guid Id) : ICommand<InvoiceDto>;

public sealed record TransmissionRunSummary(int Tenants, int Processed, int Failed);

public static class TransmissionErrors
{
    public static readonly Error NotSubmittable = Error.Conflict("invoicing.invoice_not_submittable", "El comprobante ya terminó su trámite con el SRI y no se puede volver a enviar.");
}

// ---- Cola -------------------------------------------------------------------------------------------------------------------------

internal static class TransmissionQueue
{
    /// <summary>
    /// Deja el comprobante en la cola. Quien llama debe tener el candado de la fila del comprobante (para no duplicar la cola) y guarda los cambios. Un comprobante devuelto vuelve a «pendiente de envío»
    /// (el SRI permite reenviarlo sin cambiar clave ni secuencial); uno estacionado se reactiva desde cero.
    /// </summary>
    public static async Task<Result> EnqueueAsync(InvoicingDbContext db, Invoice invoice, Guid? actorId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (invoice.Status is InvoiceStatus.Authorized or InvoiceStatus.NotAuthorized or InvoiceStatus.Voided)
        {
            return TransmissionErrors.NotSubmittable;
        }

        if (invoice.Status == InvoiceStatus.Rejected)
        {
            Transition(db, invoice, InvoiceStatus.PendingSubmission, "Retry", "Reenvío tras una devolución del SRI", actorId, now);
        }

        var queued = await db.Transmissions.FirstOrDefaultAsync(t => t.InvoiceId == invoice.Id, cancellationToken);
        if (queued is null)
        {
            db.Transmissions.Add(new InvoiceTransmission { InvoiceId = invoice.Id, TenantId = invoice.TenantId, NextAttemptAt = now, FirstAttemptAt = now, CreatedAt = now, RequestedBy = actorId });
            AddEvent(db, invoice, "Queued", "En cola para enviarse al SRI", actorId, now);
        }
        else if (queued.Parked)
        {
            queued.Parked = false;
            queued.Attempts = 0;
            queued.Polls = 0;
            queued.LastError = null;
            queued.LockedUntil = null;
            queued.FirstAttemptAt = now;
            queued.NextAttemptAt = now;
            AddEvent(db, invoice, "Retry", "Reactivado tras quedar estacionado", actorId, now);
        }

        return Result.Success();
    }

    public static void Transition(InvoicingDbContext db, Invoice invoice, InvoiceStatus to, string type, string? detail, Guid? actorId, DateTimeOffset now)
    {
        var from = invoice.Status;
        invoice.Status = to;
        invoice.UpdatedAt = now;
        db.InvoiceEvents.Add(new InvoiceEvent
        {
            Id = Guid.CreateVersion7(), TenantId = invoice.TenantId, InvoiceId = invoice.Id, Type = type, FromStatus = from, ToStatus = to, Detail = Clip(detail), ActorId = actorId, At = now
        });
    }

    public static void AddEvent(InvoicingDbContext db, Invoice invoice, string type, string? detail, Guid? actorId, DateTimeOffset now) =>
        db.InvoiceEvents.Add(new InvoiceEvent
        {
            Id = Guid.CreateVersion7(), TenantId = invoice.TenantId, InvoiceId = invoice.Id, Type = type, FromStatus = null, ToStatus = invoice.Status, Detail = Clip(detail), ActorId = actorId, At = now
        });

    private static string? Clip(string? text) => text is null || text.Length <= 500 ? text : text[..499] + "…";
}

internal sealed class SubmitInvoiceHandler(IAccessContext access, InvoicingDbContext db, IClock clock) : ICommandHandler<SubmitInvoiceCommand, InvoiceDto>
{
    public async Task<Result<InvoiceDto>> HandleAsync(SubmitInvoiceCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        if (!await db.Invoices.AsNoTracking().VisibleTo(actor.Value).AnyAsync(i => i.Id == command.Id, cancellationToken))
        {
            return InvoiceErrors.NotFound;
        }

        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            // El mismo candado de fila que usan la firma y el Worker: dos «enviar» simultáneos dejan una sola entrada en la cola.
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM invoicing.invoices WHERE id = {command.Id} FOR UPDATE", cancellationToken);
            var invoice = await db.Invoices.SingleAsync(i => i.Id == command.Id, cancellationToken);
            var queued = await TransmissionQueue.EnqueueAsync(db, invoice, actor.Value.UserId, clock.UtcNow, cancellationToken);
            if (queued.IsFailure)
            {
                return queued.Error;
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        db.ChangeTracker.Clear();
        return await InvoiceReader.ReadAsync(db, command.Id, cancellationToken);
    }
}

// ---- Transmisor (un comprobante) --------------------------------------------------------------------------------------------------

/// <summary>
/// Avanza un comprobante de la cola un paso, según su estado: firma si falta, lo envía al SRI (recepción) o consulta su autorización. Cada intercambio con el SRI deja una constancia inmutable. Un fallo
/// del SRI (red, tiempo, falla SOAP, HTTP 5xx, respuesta ilegible) nunca rechaza el comprobante: se reintenta con espera creciente y, pasado el plazo, se «estaciona». Una devolución se registra con los
/// mensajes del SRI y espera a una persona; un «ya registrada» (43) o «en procesamiento» (70) no se reenvía: se consulta su autorización, como manda la ficha técnica.
/// </summary>
internal sealed class InvoiceTransmitter(
    InvoicingDbContext db, InvoiceSigner signer, ISriGateway gateway, IOptions<SriOptions> options, IClock clock, ILogger<InvoiceTransmitter> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <returns><c>true</c> si el comprobante avanzó o quedó resuelto; <c>false</c> si hubo que reintentar más tarde.</returns>
    public async Task<bool> ProcessAsync(Guid invoiceId, CancellationToken cancellationToken)
    {
        var queued = await db.Transmissions.FirstOrDefaultAsync(t => t.InvoiceId == invoiceId, cancellationToken);
        var invoice = await db.Invoices.FirstOrDefaultAsync(i => i.Id == invoiceId, cancellationToken);
        if (queued is null || invoice is null)
        {
            return true;
        }

        var now = clock.UtcNow;

        // Lo que ya terminó, o espera a una persona, sale de la cola.
        if (invoice.Status is InvoiceStatus.Authorized or InvoiceStatus.NotAuthorized or InvoiceStatus.Voided or InvoiceStatus.Rejected)
        {
            db.Transmissions.Remove(queued);
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }

        if (TransmissionSchedule.ShouldPark(queued.FirstAttemptAt, now, TimeSpan.FromHours(options.Value.MaxAgeHours)))
        {
            queued.Parked = true;
            queued.LockedUntil = null;
            queued.LastError ??= "parked_max_age";
            TransmissionQueue.AddEvent(db, invoice, "Parked", "Pasó el plazo sin resolverse: requiere revisión", null, now);
            await db.SaveChangesAsync(cancellationToken);
            logger.LogWarning("El comprobante {InvoiceId} quedó estacionado tras superar el plazo de la transmisión", invoice.Id);
            return false;
        }

        if (invoice.Status == InvoiceStatus.PendingSignature)
        {
            var signed = await signer.SignAsync(invoice.Id, null, cancellationToken);
            if (signed.IsFailure)
            {
                var code = signed.Error.Code.Replace("invoicing.", string.Empty, StringComparison.Ordinal);
                await FailAsync(invoice, queued, SriOperation.Reception, "SignFailed", code, 0, now, cancellationToken);
                return false;
            }

            // El firmante trabaja con la misma instancia rastreada: el estado ya está al día.
            now = clock.UtcNow;
        }

        if (invoice.Status == InvoiceStatus.SignedDocument)
        {
            TransmissionQueue.Transition(db, invoice, InvoiceStatus.PendingSubmission, "PendingSubmission", "Listo para enviarse al SRI", null, now);
            await db.SaveChangesAsync(cancellationToken);
        }

        return invoice.Status switch
        {
            InvoiceStatus.PendingSubmission => await SendAsync(invoice, queued, cancellationToken),
            InvoiceStatus.Received or InvoiceStatus.Processing => await QueryAsync(invoice, queued, cancellationToken),
            _ => true
        };
    }

    private async Task<bool> SendAsync(Invoice invoice, InvoiceTransmission queued, CancellationToken cancellationToken)
    {
        var document = await db.InvoiceDocuments.AsNoTracking().SingleAsync(d => d.InvoiceId == invoice.Id && d.Kind == InvoiceDocumentKind.SignedXml, cancellationToken);
        var stopwatch = Stopwatch.StartNew();
        SriReception reception;
        try
        {
            reception = await gateway.SendAsync(invoice.Environment, document.Content, cancellationToken);
        }
        catch (SriUnavailableException ex)
        {
            await FailAsync(invoice, queued, SriOperation.Reception, "Unavailable", ex.Code, (int)stopwatch.ElapsedMilliseconds, clock.UtcNow, cancellationToken);
            return false;
        }

        var now = clock.UtcNow;
        var known = SriMessageCodes.MeansAlreadyKnown(reception.Messages);
        AddAttempt(invoice, SriOperation.Reception, reception.State == SriReceptionState.Received ? "Received" : "Returned", reception.RawState, null, (int)stopwatch.ElapsedMilliseconds, reception.Messages, now);

        if (reception.State == SriReceptionState.Received || known)
        {
            TransmissionQueue.Transition(db, invoice, InvoiceStatus.Received, "Received",
                reception.State == SriReceptionState.Received ? "Recibido por el SRI" : "El SRI ya conocía el comprobante: se consulta su autorización", null, now);
            queued.Attempts = 0;
            queued.Polls = 0;
            queued.LastError = null;
            queued.LastAttemptAt = now;
            queued.LockedUntil = null;
            queued.NextAttemptAt = now.AddSeconds(options.Value.AuthorizationDelaySeconds);
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }

        // DEVUELTA: no se reintenta solo. Se conservan los mensajes del SRI y se espera a que una persona corrija y reenvíe.
        TransmissionQueue.Transition(db, invoice, InvoiceStatus.Rejected, "Rejected", SriMessageCodes.Summary(reception.Messages), null, now);
        db.Transmissions.Remove(queued);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task<bool> QueryAsync(Invoice invoice, InvoiceTransmission queued, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        SriAuthorizationResult result;
        try
        {
            result = await gateway.QueryAuthorizationAsync(invoice.Environment, invoice.AccessKey, cancellationToken);
        }
        catch (SriUnavailableException ex)
        {
            await FailAsync(invoice, queued, SriOperation.Authorization, "Unavailable", ex.Code, (int)stopwatch.ElapsedMilliseconds, clock.UtcNow, cancellationToken);
            return false;
        }

        var now = clock.UtcNow;
        var elapsed = (int)stopwatch.ElapsedMilliseconds;

        // Si hubiera varias, vale la más reciente.
        var authorization = result.Authorizations.OrderByDescending(a => a.AuthorizedAt ?? DateTimeOffset.MinValue).FirstOrDefault();
        if (authorization is null)
        {
            AddAttempt(invoice, SriOperation.Authorization, "NotFound", null, null, elapsed, [], now);
            return await StillPendingAsync(queued, now, cancellationToken);
        }

        switch (authorization.State)
        {
            case SriAuthorizationState.Authorized when !string.IsNullOrWhiteSpace(authorization.AuthorizationXml):
                AddAttempt(invoice, SriOperation.Authorization, "Authorized", authorization.RawState, null, elapsed, authorization.Messages, now);
                await AuthorizeAsync(invoice, queued, authorization, now, cancellationToken);
                return true;

            case SriAuthorizationState.NotAuthorized:
                AddAttempt(invoice, SriOperation.Authorization, "NotAuthorized", authorization.RawState, null, elapsed, authorization.Messages, now);
                TransmissionQueue.Transition(db, invoice, InvoiceStatus.NotAuthorized, "NotAuthorized", SriMessageCodes.Summary(authorization.Messages), null, now);
                db.Transmissions.Remove(queued);
                await db.SaveChangesAsync(cancellationToken);
                return true;

            default:
                // Procesando, sin reconocer o «autorizado» sin documento: todavía sin resolver. El texto no reconocido se registra para revisarlo.
                if (authorization.State is SriAuthorizationState.Unknown or SriAuthorizationState.Authorized)
                {
                    logger.LogWarning("Estado de autorización inesperado del SRI para el comprobante {InvoiceId}: «{State}»", invoice.Id, authorization.RawState);
                }

                AddAttempt(invoice, SriOperation.Authorization, authorization.State == SriAuthorizationState.Processing ? "Processing" : "Unknown", authorization.RawState, null, elapsed, authorization.Messages, now);
                if (invoice.Status == InvoiceStatus.Received)
                {
                    TransmissionQueue.Transition(db, invoice, InvoiceStatus.Processing, "Processing", "El SRI está procesando el comprobante", null, now);
                }

                return await StillPendingAsync(queued, now, cancellationToken);
        }
    }

    private async Task AuthorizeAsync(Invoice invoice, InvoiceTransmission queued, SriAuthorization authorization, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var number = authorization.Number is { Length: > 0 and <= 49 } n ? n : invoice.AccessKey;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Primero el XML autorizado: la base exige que exista antes de aceptar el estado «autorizado».
        db.InvoiceDocuments.Add(InvoiceSigner.NewDocument(invoice, InvoiceDocumentKind.AuthorizedXml, authorization.AuthorizationXml!, now));
        await db.SaveChangesAsync(cancellationToken);

        invoice.AuthorizationNumber = number;
        invoice.AuthorizedAt = authorization.AuthorizedAt?.ToUniversalTime() ?? now;
        TransmissionQueue.Transition(db, invoice, InvoiceStatus.Authorized, "Authorized", $"Autorizado por el SRI. Ambiente: {authorization.EnvironmentName}", null, now);
        db.Transmissions.Remove(queued);

        // El comprador recibe el XML autorizado y el RIDE por correo apenas se autoriza (el consumidor final no tiene correo). Si el correo falla, el comprobante sigue autorizado.
        if (invoice.BuyerIdType != SriBuyerIdType.FinalConsumer && InvoiceDelivery.IsValidAddress(invoice.BuyerEmail))
        {
            InvoiceDelivery.Enqueue(db, invoice, invoice.BuyerEmail!, null, now);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<bool> StillPendingAsync(InvoiceTransmission queued, DateTimeOffset now, CancellationToken cancellationToken)
    {
        queued.Polls++;
        queued.LastAttemptAt = now;
        queued.LockedUntil = null;
        queued.LastError = null;
        queued.NextAttemptAt = now + TransmissionSchedule.AfterPending(queued.Polls);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task FailAsync(Invoice invoice, InvoiceTransmission queued, SriOperation operation, string outcome, string errorCode, int durationMs, DateTimeOffset now, CancellationToken cancellationToken)
    {
        AddAttempt(invoice, operation, outcome, null, errorCode, durationMs, [], now);
        queued.Attempts++;
        queued.LastAttemptAt = now;
        queued.LastError = errorCode.Length <= 60 ? errorCode : errorCode[..60];
        queued.LockedUntil = null;
        queued.NextAttemptAt = now + TransmissionSchedule.AfterUnavailable(queued.Attempts);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogWarning("Transmisión del comprobante {InvoiceId} pendiente: {Operation} {Outcome} ({Code}); intento {Attempts}, próximo {Next:O}", invoice.Id, operation, outcome, errorCode, queued.Attempts, queued.NextAttemptAt);
    }

    private void AddAttempt(Invoice invoice, SriOperation operation, string outcome, string? sriState, string? errorCode, int durationMs, IReadOnlyList<SriMessage> messages, DateTimeOffset at) =>
        db.SriAttempts.Add(new InvoiceSriAttempt
        {
            Id = Guid.CreateVersion7(), TenantId = invoice.TenantId, InvoiceId = invoice.Id, Operation = operation, Environment = invoice.Environment, Outcome = outcome,
            SriState = sriState is { Length: > 60 } ? sriState[..60] : sriState, ErrorCode = errorCode, DurationMs = durationMs,
            Messages = JsonSerializer.Serialize(messages.Take(30).Select(m => new SriMessageDto(m.Id, m.Text, m.Detail, m.Type)), Json), At = at
        });
}

// ---- Ejecutor (todos los gimnasios) -----------------------------------------------------------------------------------------------

/// <summary>
/// Trabajo periódico del Worker: por cada gimnasio activo reclama los comprobantes vencidos de su cola y los avanza. El reclamo es una concesión (<c>locked_until</c>) tomada con
/// <c>FOR UPDATE SKIP LOCKED</c>: varias instancias del Worker no procesan el mismo comprobante ni lo envían dos veces, y si una instancia muere la concesión vence y otra continúa. Cada
/// comprobante se procesa en su propio alcance; un fallo en uno no frena a los demás.
/// </summary>
public sealed class InvoiceTransmissionRunner(IServiceScopeFactory scopes, IClock clock, IOptions<SriOptions> options, ILogger<InvoiceTransmissionRunner> logger)
{
    /// <summary>Cuánto dura la concesión de un comprobante reclamado (cubre la firma y las llamadas al SRI).</summary>
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    public async Task<TransmissionRunSummary> RunAsync(CancellationToken cancellationToken)
    {
        List<Guid> tenants;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var platform = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            tenants = await platform.Tenants.AsNoTracking()
                .Where(t => t.Status == TenantStatus.Active && t.Slug != Tenant.PlatformSlug)
                .Select(t => t.Id)
                .ToListAsync(cancellationToken);
        }

        var total = new TransmissionRunSummary(tenants.Count, 0, 0);
        foreach (var tenantId in tenants)
        {
            try
            {
                var summary = await RunForTenantAsync(tenantId, cancellationToken);
                total = total with { Processed = total.Processed + summary.Processed, Failed = total.Failed + summary.Failed };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Falló la transmisión de comprobantes del gimnasio {TenantId}", tenantId);
            }
        }

        return total;
    }

    public async Task<TransmissionRunSummary> RunForTenantAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        List<Guid> claimed;
        await using (var scope = scopes.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenantId);
            var db = scope.ServiceProvider.GetRequiredService<InvoicingDbContext>();
            var now = clock.UtcNow;
            var leaseUntil = now + Lease;
            var batch = Math.Clamp(options.Value.BatchSize, 1, 200);
            claimed = await db.Database.SqlQuery<Guid>($"""
                UPDATE invoicing.invoice_transmissions SET locked_until = {leaseUntil}
                WHERE invoice_id IN (
                    SELECT invoice_id FROM invoicing.invoice_transmissions
                    WHERE NOT parked AND next_attempt_at <= {now} AND (locked_until IS NULL OR locked_until < {now})
                    ORDER BY next_attempt_at LIMIT {batch} FOR UPDATE SKIP LOCKED)
                RETURNING invoice_id AS "Value"
                """).ToListAsync(cancellationToken);
        }

        int processed = 0, failed = 0;
        foreach (var invoiceId in claimed)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenantId);
                var transmitter = scope.ServiceProvider.GetRequiredService<InvoiceTransmitter>();
                if (await transmitter.ProcessAsync(invoiceId, cancellationToken))
                {
                    processed++;
                }
                else
                {
                    failed++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // La concesión vence sola y otra pasada lo retoma. Del error solo se registra el tipo: su texto puede traer datos del comprobante.
                failed++;
                logger.LogError(ex, "Falló la transmisión del comprobante {InvoiceId}", invoiceId);
            }
        }

        return new TransmissionRunSummary(1, processed, failed);
    }
}
