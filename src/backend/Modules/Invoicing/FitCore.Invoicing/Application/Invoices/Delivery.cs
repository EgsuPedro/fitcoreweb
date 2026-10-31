using System.Net.Mail;
using System.Text;
using FitCore.Application.Core;
using FitCore.Invoicing.Application.Security;
using FitCore.Invoicing.Application.Settings;
using FitCore.Invoicing.Domain;
using FitCore.Invoicing.Persistence;
using FitCore.Invoicing.Rendering;
using FitCore.Platform.Domain;
using FitCore.Platform.Security;
using FitCore.Platform.Persistence;
using FitCore.SharedKernel;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FitCore.Invoicing.Application.Invoices;

// ---- Modelos ----------------------------------------------------------------------------------------------------------------------

/// <summary>Un envío por correo del comprobante. <c>Automatic</c>: lo encoló el sistema al autorizarse; si no, lo pidió una persona (reenvío).</summary>
public sealed record InvoiceEmailDto(Guid Id, string To, string Status, int Attempts, string? LastError, bool Automatic, DateTimeOffset CreatedAt, DateTimeOffset? SentAt);

public sealed record ResendInvoiceEmailRequest(string? To);

/// <summary>Reenvía por correo el XML autorizado y el RIDE. Sin <c>To</c> usa el correo del comprador del comprobante.</summary>
public sealed record ResendInvoiceEmailCommand(Guid Id, string? To) : ICommand<InvoiceDto>;

public sealed record InvoiceRideFile(string FileName, byte[] Content);

/// <summary>Descarga el RIDE (PDF) de un comprobante autorizado. Se genera a partir del comprobante y de los datos autorizados por el SRI: no se guarda ninguna copia.</summary>
public sealed record DownloadInvoiceRideCommand(Guid Id) : ICommand<InvoiceRideFile>;

public static class DeliveryErrors
{
    public static readonly Error NotAuthorized = Error.Conflict("invoicing.invoice_not_authorized", "El comprobante todavía no está autorizado por el SRI: solo entonces tiene RIDE y se puede enviar.");
    public static readonly Error NoBuyerEmail = Error.Validation("invoicing.no_buyer_email", "El comprador no tiene correo registrado: indique a qué dirección enviarlo.");
    public static readonly Error EmailInProgress = Error.Conflict("invoicing.email_in_progress", "Ya hay un envío por correo de este comprobante en curso.");
    public static readonly Error EmailUnavailable = Error.Unavailable("invoicing.email_unavailable", "El envío de correo no está configurado en este servidor.");
}

public sealed class ResendInvoiceEmailRequestValidator : AbstractValidator<ResendInvoiceEmailRequest>
{
    public ResendInvoiceEmailRequestValidator() =>
        RuleFor(x => x.To).Must(InvoiceDelivery.IsValidAddress).When(x => !string.IsNullOrWhiteSpace(x.To)).WithMessage("El correo no es una dirección válida.");
}

// ---- Cola -------------------------------------------------------------------------------------------------------------------------

internal static class InvoiceDelivery
{
    public static readonly TimeSpan[] Backoff = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromHours(1), TimeSpan.FromHours(6)];
    public const int MaxAttempts = 5;

    public static bool IsValidAddress(string? value) =>
        value is { Length: > 0 and <= 256 } && !value.Contains('\r') && !value.Contains('\n')
        && MailAddress.TryCreate(value.Trim(), out var address) && address.Address == value.Trim() && address.Host.Contains('.', StringComparison.Ordinal);

    /// <summary>Deja un envío en la cola. Quien llama guarda los cambios.</summary>
    public static InvoiceEmail Enqueue(InvoicingDbContext db, Invoice invoice, string to, Guid? actorId, DateTimeOffset now)
    {
        var email = new InvoiceEmail
        {
            Id = Guid.CreateVersion7(), TenantId = invoice.TenantId, InvoiceId = invoice.Id, ToAddress = to.Trim(), Status = InvoiceEmailStatus.Pending, NextAttemptAt = now, RequestedBy = actorId, CreatedAt = now
        };
        db.InvoiceEmails.Add(email);
        TransmissionQueue.AddEvent(db, invoice, "EmailQueued", $"En cola para enviarse a {Mask(email.ToAddress)}", actorId, now);
        return email;
    }

    /// <summary>«juan.perez@correo.com» → «j***@correo.com»: el historial es visible para varios roles.</summary>
    public static string Mask(string email)
    {
        var at = email.IndexOf('@', StringComparison.Ordinal);
        return at <= 0 ? "***" : email[0] + "***" + email[at..];
    }
}

internal sealed class ResendInvoiceEmailHandler(IAccessContext access, InvoicingDbContext db, IEmailSender sender, IClock clock) : ICommandHandler<ResendInvoiceEmailCommand, InvoiceDto>
{
    public async Task<Result<InvoiceDto>> HandleAsync(ResendInvoiceEmailCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var invoice = await db.Invoices.VisibleTo(actor.Value).FirstOrDefaultAsync(i => i.Id == command.Id, cancellationToken);
        if (invoice is null)
        {
            return InvoiceErrors.NotFound;
        }

        if (invoice.Status != InvoiceStatus.Authorized)
        {
            return DeliveryErrors.NotAuthorized;
        }

        if (!sender.IsConfigured)
        {
            return DeliveryErrors.EmailUnavailable;
        }

        var to = string.IsNullOrWhiteSpace(command.To) ? invoice.BuyerEmail : command.To;
        if (string.IsNullOrWhiteSpace(to))
        {
            return DeliveryErrors.NoBuyerEmail;
        }

        if (!InvoiceDelivery.IsValidAddress(to))
        {
            return Error.Validation("invoicing.invalid_email", "El correo no es una dirección válida.");
        }

        if (await db.InvoiceEmails.AnyAsync(e => e.InvoiceId == invoice.Id && (e.Status == InvoiceEmailStatus.Pending || e.Status == InvoiceEmailStatus.Sending), cancellationToken))
        {
            return DeliveryErrors.EmailInProgress;
        }

        InvoiceDelivery.Enqueue(db, invoice, to, actor.Value.UserId, clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();
        return await InvoiceReader.ReadAsync(db, command.Id, cancellationToken);
    }
}

// ---- RIDE -------------------------------------------------------------------------------------------------------------------------

/// <summary>Genera el RIDE de un comprobante autorizado con la marca del gimnasio de la solicitud (o del Worker) en curso.</summary>
public sealed class InvoiceRideService(ITenantBranding branding, ILogger<InvoiceRideService> logger)
{
    internal async Task<byte[]> RenderAsync(Invoice invoice, CancellationToken cancellationToken)
    {
        if (invoice.AuthorizedAt is not { } authorizedAt)
        {
            throw new InvalidOperationException("El comprobante no está autorizado.");
        }

        var brand = await branding.GetAsync(cancellationToken);
        var logo = brand.Logo is { } l ? new RideLogo(l.Content, l.Width, l.Height) : null;
        try
        {
            return RideRenderer.Render(RideModel.From(invoice, authorizedAt, logo));
        }
        catch (Exception ex) when (logo is not null && ex is not OperationCanceledException)
        {
            // Un logo que no se pueda dibujar no debe impedir entregar el comprobante: se genera sin él.
            logger.LogWarning(ex, "No se pudo dibujar el logo en el RIDE del comprobante {InvoiceId}; se genera sin logo", invoice.Id);
            return RideRenderer.Render(RideModel.From(invoice, authorizedAt, null));
        }
    }

    public static string FileName(Invoice invoice) => $"Factura-{invoice.Number}.pdf";
}

internal sealed class DownloadInvoiceRideHandler(IAccessContext access, InvoicingDbContext db, InvoiceRideService rides, IClock clock) : ICommandHandler<DownloadInvoiceRideCommand, InvoiceRideFile>
{
    public async Task<Result<InvoiceRideFile>> HandleAsync(DownloadInvoiceRideCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var invoice = await db.Invoices.AsNoTracking().Include(i => i.Lines).Include(i => i.Taxes).Include(i => i.Payments).AsSplitQuery()
            .VisibleTo(actor.Value).FirstOrDefaultAsync(i => i.Id == command.Id, cancellationToken);
        if (invoice is null)
        {
            return InvoiceErrors.NotFound;
        }

        if (invoice.Status != InvoiceStatus.Authorized)
        {
            return DeliveryErrors.NotAuthorized;
        }

        var pdf = await rides.RenderAsync(invoice, cancellationToken);
        db.InvoiceEvents.Add(new InvoiceEvent
        {
            Id = Guid.CreateVersion7(), TenantId = invoice.TenantId, InvoiceId = invoice.Id, Type = "RideDownloaded", FromStatus = null, ToStatus = invoice.Status, Detail = null, ActorId = actor.Value.UserId, At = clock.UtcNow
        });
        await db.SaveChangesAsync(cancellationToken);
        return new InvoiceRideFile(InvoiceRideService.FileName(invoice), pdf);
    }
}

// ---- Envío (un correo) ------------------------------------------------------------------------------------------------------------

/// <summary>Arma y envía un correo de la cola: XML autorizado y RIDE adjuntos. El resultado queda en la cola y en el historial del comprobante.</summary>
internal sealed class InvoiceEmailDispatcher(InvoicingDbContext db, IEmailSender sender, InvoiceRideService rides, IClock clock, ILogger<InvoiceEmailDispatcher> logger)
{
    public async Task SendAsync(Guid emailId, CancellationToken cancellationToken)
    {
        var email = await db.InvoiceEmails.SingleAsync(e => e.Id == emailId, cancellationToken);
        var invoice = await db.Invoices.AsNoTracking().Include(i => i.Lines).Include(i => i.Taxes).Include(i => i.Payments).AsSplitQuery().SingleAsync(i => i.Id == email.InvoiceId, cancellationToken);
        var xml = await db.InvoiceDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.InvoiceId == invoice.Id && d.Kind == InvoiceDocumentKind.AuthorizedXml, cancellationToken);

        Result result;
        if (invoice.Status != InvoiceStatus.Authorized || xml is null)
        {
            result = Error.Validation("invoicing.not_deliverable", "El comprobante no está autorizado.");
        }
        else
        {
            var pdf = await rides.RenderAsync(invoice, cancellationToken);
            result = await sender.SendAsync(Compose(invoice, email.ToAddress, xml.Content, pdf), cancellationToken);
        }

        Complete(email, invoice, result);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static EmailMessage Compose(Invoice invoice, string to, string authorizedXml, byte[] pdf)
    {
        var issuer = string.IsNullOrWhiteSpace(invoice.IssuerTradeName) ? invoice.IssuerLegalName : invoice.IssuerTradeName;
        var body = new StringBuilder()
            .AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"Hola {invoice.BuyerName},")
            .AppendLine()
            .AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"{issuer} le envía su factura electrónica No. {invoice.Number}, por un valor de USD {RideModel.Money(invoice.Total)}, emitida el {invoice.IssueDate:dd/MM/yyyy}.")
            .AppendLine()
            .AppendLine("Adjuntamos el comprobante autorizado por el SRI en dos formatos:")
            .AppendLine("  - La representación impresa (RIDE), en PDF.")
            .AppendLine("  - El archivo XML autorizado, que es el comprobante electrónico con validez tributaria.")
            .AppendLine()
            .AppendLine("Este mensaje se envía automáticamente: no es necesario responderlo.")
            .ToString();
        return new EmailMessage(to, $"Factura {invoice.Number} de {issuer}", body, null,
        [
            new EmailAttachment(InvoiceRideService.FileName(invoice), "application/pdf", pdf),
            new EmailAttachment($"{invoice.AccessKey}.xml", "application/xml", Encoding.UTF8.GetBytes(authorizedXml))
        ]);
    }

    private void Complete(InvoiceEmail email, Invoice invoice, Result result)
    {
        var now = clock.UtcNow;
        email.LockedUntil = null;
        if (result.IsSuccess)
        {
            email.Status = InvoiceEmailStatus.Sent;
            email.SentAt = now;
            email.LastError = null;
            TransmissionQueue.AddEvent(db, invoice, "EmailSent", $"Enviado a {InvoiceDelivery.Mask(email.ToAddress)}", null, now);
            return;
        }

        email.LastError = result.Error.Code.Length <= 60 ? result.Error.Code : result.Error.Code[..60];
        if (email.Attempts >= InvoiceDelivery.MaxAttempts || result.Error.Type == ErrorType.Validation)
        {
            // Un destinatario inválido o un comprobante sin autorizar no mejoran al reintentar.
            email.Status = InvoiceEmailStatus.Failed;
            TransmissionQueue.AddEvent(db, invoice, "EmailFailed", $"No se pudo enviar a {InvoiceDelivery.Mask(email.ToAddress)} ({email.LastError})", null, now);
            logger.LogError("Correo del comprobante {InvoiceId} falló definitivamente: {Error}", invoice.Id, email.LastError);
            return;
        }

        email.Status = InvoiceEmailStatus.Pending;
        email.NextAttemptAt = now + InvoiceDelivery.Backoff[Math.Min(email.Attempts - 1, InvoiceDelivery.Backoff.Length - 1)];
    }
}

// ---- Ejecutor (todos los gimnasios) -----------------------------------------------------------------------------------------------

/// <summary>
/// Trabajo periódico del Worker: por cada gimnasio activo reclama los correos de comprobantes vencidos (concesión con <c>FOR UPDATE SKIP LOCKED</c>, igual que la cola del SRI) y los envía con
/// reintentos espaciados. Sin SMTP configurado no gasta intentos.
/// </summary>
public sealed class InvoiceEmailRunner(IServiceScopeFactory scopes, IClock clock, ILogger<InvoiceEmailRunner> logger)
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);
    private const int BatchSize = 10;

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        List<Guid> tenants;
        await using (var scope = scopes.CreateAsyncScope())
        {
            if (!scope.ServiceProvider.GetRequiredService<IEmailSender>().IsConfigured)
            {
                return 0;
            }

            var platform = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            tenants = await platform.Tenants.AsNoTracking()
                .Where(t => t.Status == TenantStatus.Active && t.Slug != Tenant.PlatformSlug)
                .Select(t => t.Id)
                .ToListAsync(cancellationToken);
        }

        var total = 0;
        foreach (var tenantId in tenants)
        {
            try
            {
                total += await RunForTenantAsync(tenantId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Falló el envío de correos de comprobantes del gimnasio {TenantId}", tenantId);
            }
        }

        return total;
    }

    public async Task<int> RunForTenantAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        List<Guid> claimed;
        await using (var scope = scopes.CreateAsyncScope())
        {
            if (!scope.ServiceProvider.GetRequiredService<IEmailSender>().IsConfigured)
            {
                return 0;
            }

            scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenantId);
            var db = scope.ServiceProvider.GetRequiredService<InvoicingDbContext>();
            var now = clock.UtcNow;
            var leaseUntil = now + Lease;

            // Un envío que quedó a medias (el Worker murió) y ya agotó sus intentos no se reintenta.
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE invoicing.invoice_emails SET status = 'Failed', locked_until = NULL, last_error = 'interrupted'
                WHERE status = 'Sending' AND locked_until < {now} AND attempts >= {InvoiceDelivery.MaxAttempts}
                """, cancellationToken);

            claimed = await db.Database.SqlQuery<Guid>($"""
                UPDATE invoicing.invoice_emails SET status = 'Sending', attempts = attempts + 1, locked_until = {leaseUntil}
                WHERE id IN (
                    SELECT id FROM invoicing.invoice_emails
                    WHERE (status = 'Pending' AND next_attempt_at <= {now}) OR (status = 'Sending' AND locked_until < {now})
                    ORDER BY next_attempt_at LIMIT {BatchSize} FOR UPDATE SKIP LOCKED)
                RETURNING id AS "Value"
                """).ToListAsync(cancellationToken);
        }

        var sent = 0;
        foreach (var id in claimed)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenantId);
                await scope.ServiceProvider.GetRequiredService<InvoiceEmailDispatcher>().SendAsync(id, cancellationToken);
                sent++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // La concesión vence sola y otra pasada lo retoma. Del error solo se registra el tipo: su texto puede traer datos del comprobante.
                logger.LogError(ex, "Falló el envío del correo {EmailId}", id);
            }
        }

        return sent;
    }
}
