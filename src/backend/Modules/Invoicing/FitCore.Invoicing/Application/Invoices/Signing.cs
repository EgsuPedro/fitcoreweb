using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using FitCore.Application.Core;
using FitCore.Invoicing.Application.Security;
using FitCore.Invoicing.Application.Settings;
using FitCore.Invoicing.Domain;
using FitCore.Invoicing.Persistence;
using FitCore.Invoicing.Xml;
using FitCore.Platform.Security;
using FitCore.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace FitCore.Invoicing.Application.Invoices;

// ---- Modelos ----------------------------------------------------------------------------------------------------------------------

/// <summary>Un documento generado del comprobante (sin su contenido). El contenido se descarga aparte, con permiso y de forma auditada.</summary>
public sealed record InvoiceDocumentDto(string Kind, string Sha256, int SizeBytes, DateTimeOffset CreatedAt);

/// <summary>Resultado de firmar: el comprobante y si esta llamada lo firmó (falso si ya estaba firmado).</summary>
public sealed record SignInvoiceResult(InvoiceDto Invoice, bool WasSigned);

public sealed record SignInvoiceCommand(Guid Id) : ICommand<SignInvoiceResult>;

/// <summary>Descarga el XML del comprobante. <c>SignedVersion</c>: <c>true</c> (por defecto) el firmado, <c>false</c> el original sin firma.</summary>
public sealed record DownloadInvoiceXmlCommand(Guid Id, bool SignedVersion = true) : ICommand<InvoiceXmlFile>;

public sealed record InvoiceXmlFile(string FileName, string Content);

public static class SigningErrors
{
    public static readonly Error CertificateUnreadable = Error.Unavailable("invoicing.certificate_unreadable", "No se pudo abrir el certificado guardado: revise la clave maestra de facturación o vuelva a cargar el certificado.");
    public static readonly Error XmlInvalid = Error.Failure("invoicing.xml_invalid", "El XML del comprobante no cumple el esquema del SRI; no se firmó. Se registró el detalle para soporte.");
    public static readonly Error SignatureInvalid = Error.Failure("invoicing.signature_invalid", "La firma generada no pasó la verificación local; no se guardó. Se registró el detalle para soporte.");
    public static readonly Error DocumentNotFound = Error.NotFound("invoicing.document_not_found", "El comprobante todavía no tiene ese documento.");
}

// ---- Casos de uso -----------------------------------------------------------------------------------------------------------------

/// <summary>
/// Genera el XML del comprobante, lo valida contra el XSD oficial, lo firma con el certificado del gimnasio (XAdES-BES), verifica la firma antes de guardar y lo deja «firmado». Es idempotente:
/// firmar un comprobante ya firmado no cambia nada. El certificado y su contraseña se descifran solo aquí, en memoria, y se descartan.
/// </summary>
internal sealed class SignInvoiceHandler(IAccessContext access, InvoicingDbContext db, ISecretProtector protector, IClock clock, ILogger<SignInvoiceHandler> logger)
    : ICommandHandler<SignInvoiceCommand, SignInvoiceResult>
{
    public async Task<Result<SignInvoiceResult>> HandleAsync(SignInvoiceCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var invoice = await db.Invoices.VisibleTo(actor.Value).Include(i => i.Lines).Include(i => i.Taxes).Include(i => i.Payments).AsSplitQuery()
            .FirstOrDefaultAsync(i => i.Id == command.Id, cancellationToken);
        if (invoice is null)
        {
            return InvoiceErrors.NotFound;
        }

        if (invoice.Status != InvoiceStatus.PendingSignature)
        {
            return new SignInvoiceResult(await InvoiceReader.ReadAsync(db, invoice.Id, cancellationToken), WasSigned: false);
        }

        var now = clock.UtcNow;
        var stored = await db.Certificates.AsNoTracking().FirstOrDefaultAsync(c => c.IsActive, cancellationToken);
        if (stored is null)
        {
            return InvoicingErrors.NoCertificate;
        }

        if (!protector.IsConfigured)
        {
            return InvoicingErrors.MasterKeyMissing;
        }

        if (now < stored.NotBefore)
        {
            return CertificateErrors.NotYetValid;
        }

        if (now >= stored.NotAfter)
        {
            return CertificateErrors.Expired;
        }

        var xml = InvoiceXmlBuilder.Build(invoice);
        var problems = InvoiceXmlValidator.Validate(xml);
        if (problems.Count > 0)
        {
            logger.LogError("El XML del comprobante {InvoiceId} no cumple el esquema del SRI: {Problems}", invoice.Id, string.Join(" | ", problems));
            return SigningErrors.XmlInvalid;
        }

        string signedXml;
        string thumbprint;
        var p12 = Array.Empty<byte>();
        var passwordBytes = Array.Empty<byte>();
        try
        {
            p12 = protector.Unprotect(stored.EncryptedP12, UploadCertificateHandler.Context(stored.TenantId, stored.Id, "p12"));
            passwordBytes = protector.Unprotect(stored.EncryptedPassword, UploadCertificateHandler.Context(stored.TenantId, stored.Id, "password"));
            using var certificate = X509CertificateLoader.LoadPkcs12(p12, Encoding.UTF8.GetString(passwordBytes), X509KeyStorageFlags.EphemeralKeySet);
            thumbprint = certificate.GetCertHashString(HashAlgorithmName.SHA256).ToLowerInvariant();
            signedXml = XadesBesSigner.Sign(xml, certificate, now);
        }
        catch (CryptographicException ex)
        {
            logger.LogError(ex, "No se pudo abrir el certificado de firma del gimnasio {TenantId}", stored.TenantId);
            return SigningErrors.CertificateUnreadable;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(p12);
            CryptographicOperations.ZeroMemory(passwordBytes);
        }

        // Nada se guarda si la firma no se verifica, o si el documento firmado dejó de cumplir el esquema.
        var check = XadesBesSigner.Verify(signedXml, thumbprint);
        if (!check.IsValid || InvoiceXmlValidator.Validate(signedXml).Count > 0)
        {
            logger.LogError("La firma del comprobante {InvoiceId} no pasó la verificación local: {Problems}", invoice.Id, string.Join(" | ", check.Problems));
            return SigningErrors.SignatureInvalid;
        }

        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            // Se serializa por comprobante: quien llega segundo espera aquí el candado de la fila y, al entrar, ve el estado ya actualizado. Sin esto, dos solicitudes simultáneas insertaban
            // sus dos documentos en órdenes distintos y PostgreSQL abortaba una por interbloqueo (40P01) sobre el índice único.
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM invoicing.invoices WHERE id = {invoice.Id} FOR UPDATE", cancellationToken);
            await db.Entry(invoice).ReloadAsync(cancellationToken);
            if (invoice.Status != InvoiceStatus.PendingSignature)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new SignInvoiceResult(await InvoiceReader.ReadAsync(db, invoice.Id, cancellationToken), WasSigned: false);
            }

            // Primero los documentos y después el cambio de estado: la base exige que exista el XML firmado antes de aceptar el estado «firmado».
            db.InvoiceDocuments.Add(NewDocument(invoice, InvoiceDocumentKind.Xml, xml, now));
            db.InvoiceDocuments.Add(NewDocument(invoice, InvoiceDocumentKind.SignedXml, signedXml, now));
            await db.SaveChangesAsync(cancellationToken);

            invoice.Status = InvoiceStatus.SignedDocument;
            invoice.UpdatedAt = now;
            db.InvoiceEvents.Add(new InvoiceEvent
            {
                Id = Guid.CreateVersion7(), TenantId = invoice.TenantId, InvoiceId = invoice.Id, Type = "Signed", FromStatus = InvoiceStatus.PendingSignature, ToStatus = InvoiceStatus.SignedDocument,
                Detail = $"Firma XAdES-BES con el certificado {thumbprint[..12]}", ActorId = actor.Value.UserId, At = now
            });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Otra solicitud lo firmó al mismo tiempo: su resultado es el válido.
            db.ChangeTracker.Clear();
            return new SignInvoiceResult(await InvoiceReader.ReadAsync(db, command.Id, cancellationToken), WasSigned: false);
        }

        return new SignInvoiceResult(await InvoiceReader.ReadAsync(db, invoice.Id, cancellationToken), WasSigned: true);
    }

    private static InvoiceDocument NewDocument(Invoice invoice, InvoiceDocumentKind kind, string content, DateTimeOffset now)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return new InvoiceDocument
        {
            Id = Guid.CreateVersion7(), TenantId = invoice.TenantId, InvoiceId = invoice.Id, Kind = kind, Content = content,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), SizeBytes = bytes.Length, CreatedAt = now
        };
    }
}

/// <summary>Entrega el XML (firmado o no) con permiso de descarga y deja constancia en el historial del comprobante: contiene datos personales y no hay enlaces públicos.</summary>
internal sealed class DownloadInvoiceXmlHandler(IAccessContext access, InvoicingDbContext db, IClock clock) : ICommandHandler<DownloadInvoiceXmlCommand, InvoiceXmlFile>
{
    public async Task<Result<InvoiceXmlFile>> HandleAsync(DownloadInvoiceXmlCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var invoice = await db.Invoices.AsNoTracking().VisibleTo(actor.Value).FirstOrDefaultAsync(i => i.Id == command.Id, cancellationToken);
        if (invoice is null)
        {
            return InvoiceErrors.NotFound;
        }

        var kind = command.SignedVersion ? InvoiceDocumentKind.SignedXml : InvoiceDocumentKind.Xml;
        var document = await db.InvoiceDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.InvoiceId == invoice.Id && d.Kind == kind, cancellationToken);
        if (document is null)
        {
            return SigningErrors.DocumentNotFound;
        }

        db.InvoiceEvents.Add(new InvoiceEvent
        {
            Id = Guid.CreateVersion7(), TenantId = invoice.TenantId, InvoiceId = invoice.Id, Type = "XmlDownloaded", FromStatus = null, ToStatus = invoice.Status,
            Detail = $"{kind} {document.Sha256[..12]}", ActorId = actor.Value.UserId, At = clock.UtcNow
        });
        await db.SaveChangesAsync(cancellationToken);

        return new InvoiceXmlFile($"{invoice.AccessKey}{(command.SignedVersion ? string.Empty : "-sin-firma")}.xml", document.Content);
    }
}
