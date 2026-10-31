using System.Text;
using FitCore.Application.Core;
using FitCore.Invoicing.Application.Security;
using FitCore.Invoicing.Domain;
using FitCore.Invoicing.Persistence;
using FitCore.Platform.Application;
using FitCore.Platform.Security;
using FitCore.SharedKernel;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace FitCore.Invoicing.Application.Settings;

// ---- Modelos ----------------------------------------------------------------------------------------------------------------------

public sealed record IssuerDto(
    string Ruc, string LegalName, string? TradeName, string HeadOfficeAddress, string TaxRegime, bool AccountingRequired,
    string? SpecialTaxpayerNumber, string? WithholdingAgentNumber, string Environment, string EmissionType, string? NotificationEmail, string CardPaymentMethod, DateTimeOffset UpdatedAt);

/// <summary><c>LastSequential</c> y <c>LastCreditNoteSequential</c>: último secuencial de factura y de nota de crédito asignado por este punto en el ambiente actual del emisor (0 si todavía no se emitió ninguno). Cada tipo de comprobante lleva su propia numeración; el siguiente lleva el que sigue.</summary>
public sealed record EmissionPointDto(
    Guid Id, Guid? BranchId, string? BranchName, string EstablishmentCode, string PointCode, string EstablishmentAddress, bool IsActive, int LastSequential, int LastCreditNoteSequential = 0);

/// <summary>Datos públicos del certificado activo. Nunca incluye el archivo, la clave privada ni la contraseña.</summary>
public sealed record CertificateDto(
    Guid Id, string Subject, string Issuer, string SerialNumber, string Thumbprint, DateTimeOffset NotBefore, DateTimeOffset NotAfter,
    int DaysToExpire, bool Expired, DateTimeOffset UploadedAt, string? SubjectRuc, bool? MatchesIssuerRuc);

public sealed record ReadinessCheckDto(string Code, bool Ok, bool Blocking);

public sealed record InvoicingSettingsDto(
    IssuerDto? Issuer,
    IReadOnlyList<EmissionPointDto> EmissionPoints,
    CertificateDto? Certificate,
    IReadOnlyList<ReadinessCheckDto> Checks,
    bool CanIssue,
    bool MasterKeyConfigured);

/// <summary>
/// Datos tributarios del emisor. <c>ConfirmProduction</c> debe ser verdadero para pasar al ambiente de producción (en producción los comprobantes tienen validez tributaria).
/// </summary>
public sealed record SaveIssuerRequest(
    string Ruc, string LegalName, string? TradeName, string HeadOfficeAddress, TaxRegime TaxRegime, bool AccountingRequired,
    string? SpecialTaxpayerNumber, string? WithholdingAgentNumber, SriEnvironment Environment, string? NotificationEmail, bool ConfirmProduction = false, CardPaymentMethod CardPaymentMethod = CardPaymentMethod.Credit);

public sealed record SaveEmissionPointRequest(Guid? BranchId, string EstablishmentCode, string PointCode, string EstablishmentAddress);

public sealed record UpdateEmissionPointRequest(Guid? BranchId, string EstablishmentAddress);

public sealed record SetEmissionPointStatusRequest(bool IsActive);

/// <summary>
/// Indica el último secuencial que el SRI ya tiene registrado para este punto de emisión (por ejemplo, de otro sistema o de pruebas anteriores). El siguiente comprobante será el que sigue.
/// Solo puede subir: bajarlo repetiría números ya usados.
/// </summary>
public sealed record SetSequenceRequest(int LastIssued, InvoiceDocumentType DocumentType = InvoiceDocumentType.Invoice);

public sealed record SetEmissionPointSequenceCommand(Guid Id, SetSequenceRequest Request) : ICommand<InvoicingSettingsDto>;

public sealed class SetSequenceRequestValidator : AbstractValidator<SetSequenceRequest>
{
    public SetSequenceRequestValidator() => RuleFor(x => x.LastIssued).InclusiveBetween(0, 999_999_998)
        .WithMessage("El secuencial debe estar entre 0 y 999999998 (el secuencial del SRI tiene 9 dígitos).");
}

public sealed record GetInvoicingSettingsQuery : IQuery<InvoicingSettingsDto>;

public sealed record SaveIssuerCommand(SaveIssuerRequest Request) : ICommand<InvoicingSettingsDto>;

public sealed record CreateEmissionPointCommand(SaveEmissionPointRequest Request) : ICommand<InvoicingSettingsDto>;

public sealed record UpdateEmissionPointCommand(Guid Id, UpdateEmissionPointRequest Request) : ICommand<InvoicingSettingsDto>;

public sealed record SetEmissionPointStatusCommand(Guid Id, bool IsActive) : ICommand<InvoicingSettingsDto>;

/// <summary>El archivo y la contraseña viajan solo en esta orden; se validan, se cifran y no se vuelven a devolver ni a registrar.</summary>
public sealed record UploadCertificateCommand(byte[] File, string Password) : ICommand<InvoicingSettingsDto>;

public sealed record RetireCertificateCommand : ICommand<InvoicingSettingsDto>;

public static class InvoicingErrors
{
    public static readonly Error MasterKeyMissing = Error.Unavailable("invoicing.master_key_missing", "La clave maestra de facturación no está configurada en el servidor: no se pueden guardar certificados.");
    public static readonly Error CertificateRequired = Error.Validation("invoicing.certificate_required", "Seleccione el archivo del certificado (.p12) y escriba su contraseña.");
    public static readonly Error CertificateTooLarge = Error.Validation("invoicing.certificate_too_large", "El archivo del certificado es demasiado grande.");
    public static readonly Error NoCertificate = Error.NotFound("invoicing.certificate_not_found", "No hay un certificado activo.");
    public static readonly Error EmissionPointNotFound = Error.NotFound("invoicing.emission_point_not_found", "Punto de emisión no encontrado.");
    public static readonly Error EmissionPointTaken = Error.Conflict("invoicing.emission_point_taken", "Ya existe un punto de emisión con ese establecimiento y código.");
    public static readonly Error BranchNotFound = Error.Validation("invoicing.branch_not_found", "La sucursal indicada no existe.");
    public static readonly Error EstablishmentMismatch = Error.Validation("invoicing.establishment_code_mismatch", "El código de establecimiento no coincide con el que tiene configurado la sucursal.");
    public static readonly Error SequenceCannotDecrease = Error.Conflict("invoicing.sequence_cannot_decrease", "El secuencial solo puede subir: bajarlo repetiría números que ya se usaron.");
    public static readonly Error IssuerRequired = Error.Validation("invoicing.issuer_required", "Primero guarde los datos del emisor.");
    public const int MaxCertificateBytes = 512 * 1024;
}

public sealed class SaveIssuerRequestValidator : AbstractValidator<SaveIssuerRequest>
{
    public SaveIssuerRequestValidator()
    {
        RuleFor(x => x.Ruc).NotEmpty().Must(r => IssuerReadiness.IsValidIssuerRuc(EcuadorIds.Normalize(r)))
            .WithMessage("El RUC no es válido: debe tener 13 dígitos y terminar en 001 (el XSD del SRI solo admite RUC de emisor terminados en 001).");
        RuleFor(x => x.LegalName).NotEmpty().MaximumLength(300);
        RuleFor(x => x.TradeName).MaximumLength(300);
        RuleFor(x => x.HeadOfficeAddress).NotEmpty().MaximumLength(300);
        RuleFor(x => x.TaxRegime).IsInEnum();
        RuleFor(x => x.Environment).IsInEnum();
        RuleFor(x => x.CardPaymentMethod).IsInEnum();
        RuleFor(x => x.NotificationEmail).EmailAddress().MaximumLength(256).When(x => !string.IsNullOrWhiteSpace(x.NotificationEmail));
        RuleFor(x => x.SpecialTaxpayerNumber).Length(3, 13).Matches("^[0-9A-Za-z]+$").When(x => !string.IsNullOrWhiteSpace(x.SpecialTaxpayerNumber))
            .WithMessage("La resolución de contribuyente especial lleva de 3 a 13 letras o números.");
        RuleFor(x => x.WithholdingAgentNumber).MaximumLength(8).Matches("^[0-9]+$").When(x => !string.IsNullOrWhiteSpace(x.WithholdingAgentNumber))
            .WithMessage("La resolución de agente de retención lleva hasta 8 dígitos.");
        RuleFor(x => x.ConfirmProduction).Equal(true).When(x => x.Environment == SriEnvironment.Production)
            .WithMessage("Para pasar a producción confirme que entiende que los comprobantes tendrán validez tributaria.");
    }
}

public sealed class SaveEmissionPointRequestValidator : AbstractValidator<SaveEmissionPointRequest>
{
    public SaveEmissionPointRequestValidator()
    {
        RuleFor(x => x.EstablishmentCode).Must(EmissionPoint.IsValidCode).WithMessage("El código de establecimiento son 3 dígitos (001 a 999).");
        RuleFor(x => x.PointCode).Must(EmissionPoint.IsValidCode).WithMessage("El punto de emisión son 3 dígitos (001 a 999).");
        RuleFor(x => x.EstablishmentAddress).NotEmpty().MaximumLength(300);
    }
}

public sealed class UpdateEmissionPointRequestValidator : AbstractValidator<UpdateEmissionPointRequest>
{
    public UpdateEmissionPointRequestValidator() => RuleFor(x => x.EstablishmentAddress).NotEmpty().MaximumLength(300);
}

// ---- Casos de uso -----------------------------------------------------------------------------------------------------------------

internal static class AccessGuards
{
    public static async Task<Result<UserAccess>> RequireAccessAsync(this IAccessContext access, CancellationToken cancellationToken)
    {
        var current = await access.GetAsync(cancellationToken);
        return current is null ? PlatformErrors.Unauthenticated : current;
    }

    /// <summary>La configuración tributaria es de todo el gimnasio: solo quien accede a todas las sucursales la cambia.</summary>
    public static async Task<Result<UserAccess>> RequireConfigurationAsync(this IAccessContext access, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor;
        }

        return actor.Value.AllBranches ? actor : PlatformErrors.AllBranchesRequired;
    }
}

/// <summary>Arma la respuesta de configuración: lo guardado, los puntos de emisión, el certificado (solo datos públicos) y las comprobaciones de «listo para emitir».</summary>
internal sealed class SettingsReader(InvoicingDbContext db, ISecretProtector protector, IClock clock)
{
    public async Task<InvoicingSettingsDto> ReadAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var issuer = await db.Issuers.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        var points = await db.EmissionPoints.AsNoTracking().OrderBy(p => p.EstablishmentCode).ThenBy(p => p.PointCode).ToListAsync(cancellationToken);
        var branches = await db.Branches.AsNoTracking().ToDictionaryAsync(b => b.Id, b => b.Name, cancellationToken);
        var certificate = await db.Certificates.AsNoTracking().FirstOrDefaultAsync(c => c.IsActive, cancellationToken);
        var environment = issuer?.Environment ?? SriEnvironment.Test;
        var sequenceRows = await db.EmissionSequences.AsNoTracking().Where(x => x.Environment == environment).ToListAsync(cancellationToken);
        int Last(Guid pointId, InvoiceDocumentType type) => sequenceRows.FirstOrDefault(x => x.EmissionPointId == pointId && x.DocumentType == type)?.LastValue ?? 0;

        bool? matches = certificate?.SubjectRuc is { } subjectRuc && issuer is not null ? subjectRuc == issuer.Ruc : null;
        var checks = IssuerReadiness.Evaluate(issuer, points, certificate, protector.IsConfigured, now, matches);

        return new InvoicingSettingsDto(
            issuer is null ? null : new IssuerDto(
                issuer.Ruc, issuer.LegalName, issuer.TradeName, issuer.HeadOfficeAddress, issuer.TaxRegime.ToString(), issuer.AccountingRequired,
                issuer.SpecialTaxpayerNumber, issuer.WithholdingAgentNumber, issuer.Environment.ToString(), issuer.EmissionType.ToString(), issuer.NotificationEmail, issuer.CardPaymentMethod.ToString(), issuer.UpdatedAt),
            [.. points.Select(p => new EmissionPointDto(
                p.Id, p.BranchId, p.BranchId is { } id ? branches.GetValueOrDefault(id) : null, p.EstablishmentCode, p.PointCode, p.EstablishmentAddress, p.IsActive, Last(p.Id, InvoiceDocumentType.Invoice), Last(p.Id, InvoiceDocumentType.CreditNote)))],
            certificate is null
                ? null
                : new CertificateDto(
                    certificate.Id, certificate.Subject, certificate.Issuer, certificate.SerialNumber, certificate.Thumbprint, certificate.NotBefore, certificate.NotAfter,
                    (int)Math.Floor((certificate.NotAfter - now).TotalDays), now >= certificate.NotAfter, certificate.UploadedAt, certificate.SubjectRuc, matches),
            [.. checks.Select(c => new ReadinessCheckDto(c.Code, c.Ok, c.Blocking))],
            IssuerReadiness.CanIssue(checks),
            protector.IsConfigured);
    }
}

internal sealed class GetInvoicingSettingsHandler(IAccessContext access, SettingsReader reader) : IQueryHandler<GetInvoicingSettingsQuery, InvoicingSettingsDto>
{
    public async Task<Result<InvoicingSettingsDto>> HandleAsync(GetInvoicingSettingsQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        return actor.IsFailure ? actor.Error : await reader.ReadAsync(cancellationToken);
    }
}

internal sealed class SaveIssuerHandler(InvoicingDbContext db, IAccessContext access, SettingsReader reader, IClock clock)
    : ICommandHandler<SaveIssuerCommand, InvoicingSettingsDto>
{
    public async Task<Result<InvoicingSettingsDto>> HandleAsync(SaveIssuerCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireConfigurationAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var r = command.Request;
        var now = clock.UtcNow;
        var issuer = await db.Issuers.FirstOrDefaultAsync(cancellationToken);
        if (issuer is null)
        {
            issuer = new IssuerProfile { TenantId = actor.Value.TenantId, CreatedAt = now };
            db.Issuers.Add(issuer);
        }

        issuer.Ruc = EcuadorIds.Normalize(r.Ruc);
        issuer.LegalName = r.LegalName.Trim();
        issuer.TradeName = Clean(r.TradeName);
        issuer.HeadOfficeAddress = r.HeadOfficeAddress.Trim();
        issuer.TaxRegime = r.TaxRegime;
        issuer.AccountingRequired = r.AccountingRequired;
        issuer.SpecialTaxpayerNumber = Clean(r.SpecialTaxpayerNumber);
        issuer.WithholdingAgentNumber = Clean(r.WithholdingAgentNumber);
        issuer.Environment = r.Environment;
        issuer.EmissionType = EmissionType.Normal;
        issuer.NotificationEmail = Clean(r.NotificationEmail);
        issuer.CardPaymentMethod = r.CardPaymentMethod;
        issuer.UpdatedAt = now;
        issuer.UpdatedBy = actor.Value.UserId;
        await db.SaveChangesAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

internal sealed class CreateEmissionPointHandler(InvoicingDbContext db, IAccessContext access, SettingsReader reader, IClock clock)
    : ICommandHandler<CreateEmissionPointCommand, InvoicingSettingsDto>
{
    public async Task<Result<InvoicingSettingsDto>> HandleAsync(CreateEmissionPointCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireConfigurationAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var r = command.Request;
        if (await db.EmissionPoints.AnyAsync(p => p.EstablishmentCode == r.EstablishmentCode && p.PointCode == r.PointCode, cancellationToken))
        {
            return InvoicingErrors.EmissionPointTaken;
        }

        var branch = await ResolveBranchAsync(db, r.BranchId, r.EstablishmentCode, cancellationToken);
        if (branch.IsFailure)
        {
            return branch.Error;
        }

        db.EmissionPoints.Add(new EmissionPoint
        {
            Id = Guid.CreateVersion7(),
            TenantId = actor.Value.TenantId,
            BranchId = r.BranchId,
            EstablishmentCode = r.EstablishmentCode,
            PointCode = r.PointCode,
            EstablishmentAddress = r.EstablishmentAddress.Trim(),
            IsActive = true,
            CreatedAt = clock.UtcNow
        });
        await db.SaveChangesAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken);
    }

    /// <summary>La sucursal debe existir en el gimnasio y, si ya tiene código de establecimiento, coincidir con el indicado.</summary>
    internal static async Task<Result> ResolveBranchAsync(InvoicingDbContext db, Guid? branchId, string establishmentCode, CancellationToken cancellationToken)
    {
        if (branchId is null)
        {
            return Result.Success();
        }

        var branch = await db.Branches.AsNoTracking().FirstOrDefaultAsync(b => b.Id == branchId, cancellationToken);
        if (branch is null)
        {
            return InvoicingErrors.BranchNotFound;
        }

        return branch.SriEstablishmentCode is { } code && code != establishmentCode ? InvoicingErrors.EstablishmentMismatch : Result.Success();
    }
}

internal sealed class UpdateEmissionPointHandler(InvoicingDbContext db, IAccessContext access, SettingsReader reader)
    : ICommandHandler<UpdateEmissionPointCommand, InvoicingSettingsDto>
{
    public async Task<Result<InvoicingSettingsDto>> HandleAsync(UpdateEmissionPointCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireConfigurationAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var point = await db.EmissionPoints.FirstOrDefaultAsync(p => p.Id == command.Id, cancellationToken);
        if (point is null)
        {
            return InvoicingErrors.EmissionPointNotFound;
        }

        // Los códigos no cambian: los secuenciales y los comprobantes dependen de ellos.
        var branch = await CreateEmissionPointHandler.ResolveBranchAsync(db, command.Request.BranchId, point.EstablishmentCode, cancellationToken);
        if (branch.IsFailure)
        {
            return branch.Error;
        }

        point.BranchId = command.Request.BranchId;
        point.EstablishmentAddress = command.Request.EstablishmentAddress.Trim();
        await db.SaveChangesAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken);
    }
}

/// <summary>
/// Sube el último secuencial usado de un punto de emisión, en el ambiente actual del emisor (pruebas y producción llevan numeración aparte). Sirve cuando el SRI ya tiene números de ese
/// punto (de otro sistema o de pruebas anteriores): un comprobante cuyo secuencial el SRI ya registró se devuelve con el error 45 y su número no se puede cambiar. Es auditado y nunca baja.
/// </summary>
internal sealed class SetEmissionPointSequenceHandler(InvoicingDbContext db, IAccessContext access, SettingsReader reader)
    : ICommandHandler<SetEmissionPointSequenceCommand, InvoicingSettingsDto>
{
    public async Task<Result<InvoicingSettingsDto>> HandleAsync(SetEmissionPointSequenceCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireConfigurationAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var point = await db.EmissionPoints.AsNoTracking().FirstOrDefaultAsync(p => p.Id == command.Id, cancellationToken);
        if (point is null)
        {
            return InvoicingErrors.EmissionPointNotFound;
        }

        var issuer = await db.Issuers.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        if (issuer is null)
        {
            return InvoicingErrors.IssuerRequired;
        }

        var tenantId = actor.Value.TenantId;
        var environment = issuer.Environment.ToString();
        var documentType = command.Request.DocumentType.ToString();
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO invoicing.emission_sequences (tenant_id, emission_point_id, document_type, environment, last_value)
                VALUES ({tenantId}, {point.Id}, {documentType}, {environment}, 0)
                ON CONFLICT DO NOTHING
                """, cancellationToken);

            // El mismo candado que toma la emisión: quien emite a la vez espera, y después usa el número nuevo.
            var rows = await db.EmissionSequences
                .FromSqlInterpolated($"""
                    SELECT * FROM invoicing.emission_sequences
                    WHERE tenant_id = {tenantId} AND emission_point_id = {point.Id} AND document_type = {documentType} AND environment = {environment}
                    FOR UPDATE
                    """)
                .ToListAsync(cancellationToken);
            var sequence = rows.Single();
            if (command.Request.LastIssued < sequence.LastValue)
            {
                return InvoicingErrors.SequenceCannotDecrease;
            }

            sequence.LastValue = command.Request.LastIssued;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        db.ChangeTracker.Clear();
        return await reader.ReadAsync(cancellationToken);
    }
}

internal sealed class SetEmissionPointStatusHandler(InvoicingDbContext db, IAccessContext access, SettingsReader reader)
    : ICommandHandler<SetEmissionPointStatusCommand, InvoicingSettingsDto>
{
    public async Task<Result<InvoicingSettingsDto>> HandleAsync(SetEmissionPointStatusCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireConfigurationAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var point = await db.EmissionPoints.FirstOrDefaultAsync(p => p.Id == command.Id, cancellationToken);
        if (point is null)
        {
            return InvoicingErrors.EmissionPointNotFound;
        }

        point.IsActive = command.IsActive;
        await db.SaveChangesAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken);
    }
}

internal sealed class UploadCertificateHandler(
    InvoicingDbContext db, IAccessContext access, ISecretProtector protector, SettingsReader reader, IClock clock)
    : ICommandHandler<UploadCertificateCommand, InvoicingSettingsDto>
{
    public async Task<Result<InvoicingSettingsDto>> HandleAsync(UploadCertificateCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireConfigurationAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        if (!protector.IsConfigured)
        {
            return InvoicingErrors.MasterKeyMissing;
        }

        if (command.File.Length == 0 || string.IsNullOrEmpty(command.Password))
        {
            return InvoicingErrors.CertificateRequired;
        }

        if (command.File.Length > InvoicingErrors.MaxCertificateBytes)
        {
            return InvoicingErrors.CertificateTooLarge;
        }

        var now = clock.UtcNow;
        var info = CertificateReader.Read(command.File, command.Password, now);
        if (info.IsFailure)
        {
            return info.Error;
        }

        var id = Guid.CreateVersion7();
        var tenantId = actor.Value.TenantId;
        var passwordBytes = Encoding.UTF8.GetBytes(command.Password);
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            // El anterior se retira primero (borrando sus secretos): solo puede haber un certificado activo por gimnasio.
            foreach (var old in await db.Certificates.Where(c => c.IsActive).ToListAsync(cancellationToken))
            {
                Retire(old, now);
            }

            await db.SaveChangesAsync(cancellationToken);

            db.Certificates.Add(new SigningCertificate
            {
                Id = id,
                TenantId = tenantId,
                Subject = info.Value.Subject,
                Issuer = info.Value.Issuer,
                SerialNumber = info.Value.SerialNumber,
                Thumbprint = info.Value.Thumbprint,
                SubjectRuc = info.Value.RucInSubject,
                NotBefore = info.Value.NotBefore,
                NotAfter = info.Value.NotAfter,
                EncryptedP12 = protector.Protect(command.File, Context(tenantId, id, "p12")),
                EncryptedPassword = protector.Protect(passwordBytes, Context(tenantId, id, "password")),
                KeyVersion = protector.KeyVersion,
                IsActive = true,
                UploadedAt = now,
                UploadedBy = actor.Value.UserId
            });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(passwordBytes);
        }

        return await reader.ReadAsync(cancellationToken);
    }

    /// <summary>Contexto que ata el secreto a su gimnasio, a su fila y a su propósito: copiarlo a otra fila o a otro gimnasio no lo descifra.</summary>
    internal static string Context(Guid tenantId, Guid certificateId, string purpose) => $"invoicing:certificate:{tenantId:N}:{certificateId:N}:{purpose}";

    internal static void Retire(SigningCertificate certificate, DateTimeOffset now)
    {
        certificate.IsActive = false;
        certificate.RetiredAt = now;
        certificate.EncryptedP12 = [];
        certificate.EncryptedPassword = [];
    }
}

internal sealed class RetireCertificateHandler(InvoicingDbContext db, IAccessContext access, SettingsReader reader, IClock clock)
    : ICommandHandler<RetireCertificateCommand, InvoicingSettingsDto>
{
    public async Task<Result<InvoicingSettingsDto>> HandleAsync(RetireCertificateCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireConfigurationAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var active = await db.Certificates.FirstOrDefaultAsync(c => c.IsActive, cancellationToken);
        if (active is null)
        {
            return InvoicingErrors.NoCertificate;
        }

        UploadCertificateHandler.Retire(active, clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken);
    }
}
