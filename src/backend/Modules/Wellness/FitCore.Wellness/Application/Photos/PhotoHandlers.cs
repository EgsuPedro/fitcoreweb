using FitCore.Application.Core;
using FitCore.Platform.Application;
using FitCore.Platform.Security;
using FitCore.SharedKernel;
using FitCore.Wellness.Application.Training;
using FitCore.Wellness.Domain;
using FitCore.Wellness.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace FitCore.Wellness.Application.Photos;

internal static class PhotoState
{
    /// <summary>Estado vigente del consentimiento = el último registro (orden estable: fecha y luego identificador v7).</summary>
    public static Task<PhotoConsent?> LatestConsentAsync(WellnessDbContext db, Guid memberId, CancellationToken cancellationToken) =>
        db.PhotoConsents.AsNoTracking().Where(c => c.MemberId == memberId)
            .OrderByDescending(c => c.RecordedAt).ThenByDescending(c => c.Id).FirstOrDefaultAsync(cancellationToken);

    public static async Task<PhotoConsentDto> ConsentDtoAsync(WellnessDbContext db, Guid memberId, CancellationToken cancellationToken)
    {
        var history = await db.PhotoConsents.AsNoTracking().Where(c => c.MemberId == memberId)
            .OrderByDescending(c => c.RecordedAt).ThenByDescending(c => c.Id).Take(50).ToListAsync(cancellationToken);
        var photoCount = await db.ProgressPhotos.AsNoTracking().CountAsync(p => p.MemberId == memberId, cancellationToken);
        var latest = history.FirstOrDefault();
        return new PhotoConsentDto(
            latest is null ? "None" : latest.Action.ToString(),
            latest?.RecordedAt,
            latest?.Source.ToString(),
            latest?.NoticeVersion,
            latest?.Action == PhotoConsentAction.Granted ? latest.GuardianName : null,
            PhotoConsent.CurrentNoticeVersion,
            photoCount,
            [.. history.Select(c => new PhotoConsentEventDto(c.Id, c.Action.ToString(), c.Source.ToString(), c.NoticeVersion, c.GuardianName, c.RecordedAt))]);
    }

    public static ProgressPhotoDto ToDto(ProgressPhoto p) =>
        new(p.Id, p.MemberId, p.TakenOn, p.Angle.ToString(), p.ContentType, p.SizeBytes, p.CreatedAt);

    public static async Task DeleteFilesAsync(IFileStore files, ILogger logger, IEnumerable<string> keys)
    {
        // Después de confirmar en la base: aunque la persona cierre la conexión, los archivos se borran igual.
        foreach (var key in keys)
        {
            try
            {
                await files.DeleteAsync(key, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "No se pudo borrar un archivo de foto ya dado de baja ({StorageKey}); requiere limpieza manual.", key);
            }
        }
    }
}

internal sealed class GetPhotoConsentHandler(WellnessDbContext db, IAccessContext access, SecurityAudit audit)
    : IQueryHandler<GetPhotoConsentQuery, PhotoConsentDto>
{
    public async Task<Result<PhotoConsentDto>> HandleAsync(GetPhotoConsentQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var scope = await MemberScope.ResolveAsync(db, actor.Value, query.MemberId, cancellationToken);
        if (scope.IsFailure)
        {
            return PhotoErrors.MemberNotFound;
        }

        if (!scope.Value.IsSelf)
        {
            await audit.RecordAccessAsync("Wellness.PhotoConsentRead", "Member", query.MemberId.ToString(), cancellationToken);
        }

        return await PhotoState.ConsentDtoAsync(db, query.MemberId, cancellationToken);
    }
}

internal sealed class GrantPhotoConsentHandler(WellnessDbContext db, IAccessContext access, IClock clock, SecurityAudit audit)
    : ICommandHandler<GrantPhotoConsentCommand, PhotoConsentDto>
{
    public async Task<Result<PhotoConsentDto>> HandleAsync(GrantPhotoConsentCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var scope = await MemberScope.ResolveAsync(db, actor.Value, command.MemberId, cancellationToken);
        if (scope.IsFailure)
        {
            return PhotoErrors.MemberNotFound;
        }

        var member = scope.Value.Member;
        if (!member.IsActive)
        {
            return PhotoErrors.MemberInactive;
        }

        if ((await PhotoState.LatestConsentAsync(db, member.Id, cancellationToken))?.Action == PhotoConsentAction.Granted)
        {
            return PhotoErrors.ConsentAlreadyGranted;
        }

        // Un menor no consiente por sí mismo: lo registra el personal con el nombre de su representante.
        // Si no hay fecha de nacimiento no hay base para juzgar la edad y se trata como adulto.
        var today = await LocalDates.TodayAtBranchAsync(db, clock, member.BranchId, cancellationToken);
        string? guardian = null;
        if (PhotoRules.IsMinor(AssessmentRules.AgeOn(member.BirthDate, today)))
        {
            if (scope.Value.IsSelf)
            {
                return PhotoErrors.GuardianConsentByStaff;
            }

            guardian = command.Request.GuardianName?.Trim();
            if (string.IsNullOrEmpty(guardian) || guardian.Length < 3)
            {
                return PhotoErrors.GuardianRequired;
            }
        }

        var source = scope.Value.IsSelf ? PhotoConsentSource.Member : PhotoConsentSource.Staff;
        db.PhotoConsents.Add(PhotoConsent.Grant(actor.Value.TenantId, member.Id, source, guardian, actor.Value.UserId, clock.UtcNow));
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAccessAsync("Wellness.PhotoConsentGranted", "Member", member.Id.ToString(), cancellationToken);
        return await PhotoState.ConsentDtoAsync(db, member.Id, cancellationToken);
    }
}

internal sealed class RevokePhotoConsentHandler(
    WellnessDbContext db,
    IAccessContext access,
    IClock clock,
    IFileStore files,
    SecurityAudit audit,
    ILogger<RevokePhotoConsentHandler> logger)
    : ICommandHandler<RevokePhotoConsentCommand, RevokePhotoConsentResult>
{
    public async Task<Result<RevokePhotoConsentResult>> HandleAsync(RevokePhotoConsentCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var scope = await MemberScope.ResolveAsync(db, actor.Value, command.MemberId, cancellationToken);
        if (scope.IsFailure)
        {
            return PhotoErrors.MemberNotFound;
        }

        var member = scope.Value.Member;
        if ((await PhotoState.LatestConsentAsync(db, member.Id, cancellationToken))?.Action != PhotoConsentAction.Granted)
        {
            return PhotoErrors.ConsentNotGranted;
        }

        var source = scope.Value.IsSelf ? PhotoConsentSource.Member : PhotoConsentSource.Staff;
        List<string> keys;
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            // Primero el registro de retiro: su disparador toma un candado por alumno que dura hasta confirmar, así una subida
            // simultánea espera y luego ve el consentimiento ya retirado. Solo después se listan y borran las fotos.
            db.PhotoConsents.Add(PhotoConsent.Revoke(actor.Value.TenantId, member.Id, source, actor.Value.UserId, clock.UtcNow));
            await db.SaveChangesAsync(cancellationToken);
            keys = await db.ProgressPhotos.Where(p => p.MemberId == member.Id).Select(p => p.StorageKey).ToListAsync(cancellationToken);
            await db.ProgressPhotos.Where(p => p.MemberId == member.Id).ExecuteDeleteAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        await PhotoState.DeleteFilesAsync(files, logger, keys);
        await audit.RecordAccessAsync("Wellness.PhotoConsentRevoked", "Member", member.Id.ToString(), cancellationToken);
        return new RevokePhotoConsentResult(keys.Count, await PhotoState.ConsentDtoAsync(db, member.Id, cancellationToken));
    }
}

internal sealed class UploadProgressPhotoHandler(WellnessDbContext db, IAccessContext access, IClock clock, IFileStore files, SecurityAudit audit)
    : ICommandHandler<UploadProgressPhotoCommand, ProgressPhotoDto>
{
    public async Task<Result<ProgressPhotoDto>> HandleAsync(UploadProgressPhotoCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var scope = await MemberScope.ResolveAsync(db, actor.Value, command.MemberId, cancellationToken);
        if (scope.IsFailure)
        {
            return PhotoErrors.MemberNotFound;
        }

        var member = scope.Value.Member;
        if (!member.IsActive)
        {
            return PhotoErrors.MemberInactive;
        }

        if (!Enum.IsDefined(command.Angle))
        {
            return PhotoErrors.AngleInvalid;
        }

        var consent = await PhotoState.LatestConsentAsync(db, member.Id, cancellationToken);
        if (consent?.Action != PhotoConsentAction.Granted)
        {
            return PhotoErrors.ConsentRequired;
        }

        var today = await LocalDates.TodayAtBranchAsync(db, clock, member.BranchId, cancellationToken);
        if (!PhotoRules.IsDateValid(command.TakenOn, today))
        {
            return PhotoErrors.DateInvalid;
        }

        if (await db.ProgressPhotos.CountAsync(p => p.MemberId == member.Id, cancellationToken) >= ProgressPhoto.MaxPerMember)
        {
            return PhotoErrors.LimitReached;
        }

        var raw = await ReadLimitedAsync(command.Content, cancellationToken);
        if (raw is null)
        {
            return PhotoErrors.FileTooLarge;
        }

        if (raw.Length == 0)
        {
            return PhotoErrors.FileRequired;
        }

        // Tipo por los bytes del archivo (no por el nombre ni la cabecera), y sin metadatos: EXIF puede traer la ubicación del domicilio.
        var contentType = PhotoRules.DetectContentType(raw);
        var clean = contentType is null ? null : PhotoRules.StripMetadata(raw, contentType);
        if (contentType is null || clean is null)
        {
            return PhotoErrors.FileInvalid;
        }

        var key = PhotoRules.NewStorageKey(actor.Value.TenantId);
        await using (var buffer = new MemoryStream(clean, writable: false))
        {
            await files.SaveAsync(key, buffer, cancellationToken);
        }

        try
        {
            var photo = ProgressPhoto.Create(actor.Value.TenantId, member.Id, command.TakenOn, command.Angle, key, contentType, clean.Length, consent.Id, actor.Value.UserId, clock.UtcNow);
            db.ProgressPhotos.Add(photo);
            await db.SaveChangesAsync(cancellationToken);
            await audit.RecordAccessAsync("Wellness.PhotoUploaded", "Member", member.Id.ToString(), cancellationToken);
            return PhotoState.ToDto(photo);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.CheckViolation })
        {
            // El consentimiento se retiró mientras se subía la foto: la base la rechaza y el archivo no debe quedar huérfano.
            await files.DeleteAsync(key, CancellationToken.None);
            return PhotoErrors.ConsentRequired;
        }
        catch
        {
            await files.DeleteAsync(key, CancellationToken.None);
            throw;
        }
    }

    /// <summary>Lee el contenido sin pasar del máximo; nulo si lo excede (no se confía en la longitud que declare el cliente).</summary>
    private static async Task<byte[]?> ReadLimitedAsync(Stream content, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > PhotoRules.MaxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}

internal sealed class ListProgressPhotosHandler(WellnessDbContext db, IAccessContext access, SecurityAudit audit)
    : IQueryHandler<ListProgressPhotosQuery, IReadOnlyList<ProgressPhotoDto>>
{
    public async Task<Result<IReadOnlyList<ProgressPhotoDto>>> HandleAsync(ListProgressPhotosQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var scope = await MemberScope.ResolveAsync(db, actor.Value, query.MemberId, cancellationToken);
        if (scope.IsFailure)
        {
            return PhotoErrors.MemberNotFound;
        }

        var photos = await db.ProgressPhotos.AsNoTracking().Where(p => p.MemberId == query.MemberId)
            .OrderByDescending(p => p.TakenOn).ThenByDescending(p => p.CreatedAt).ToListAsync(cancellationToken);
        if (!scope.Value.IsSelf)
        {
            await audit.RecordAccessAsync("Wellness.PhotosListed", "Member", query.MemberId.ToString(), cancellationToken);
        }

        return photos.Select(PhotoState.ToDto).ToList();
    }
}

internal sealed class GetProgressPhotoContentHandler(WellnessDbContext db, IAccessContext access, IFileStore files, SecurityAudit audit, ILogger<GetProgressPhotoContentHandler> logger)
    : IQueryHandler<GetProgressPhotoContentQuery, PhotoContent>
{
    public async Task<Result<PhotoContent>> HandleAsync(GetProgressPhotoContentQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var photo = await db.ProgressPhotos.AsNoTracking().FirstOrDefaultAsync(p => p.Id == query.Id, cancellationToken);
        if (photo is null)
        {
            return PhotoErrors.NotFound;
        }

        var scope = await MemberScope.ResolveAsync(db, actor.Value, photo.MemberId, cancellationToken);
        if (scope.IsFailure)
        {
            return PhotoErrors.NotFound;
        }

        // Defensa en profundidad: sin consentimiento vigente no se entrega ni una foto que por error siguiera guardada.
        if ((await PhotoState.LatestConsentAsync(db, photo.MemberId, cancellationToken))?.Action != PhotoConsentAction.Granted)
        {
            return PhotoErrors.ConsentRequired;
        }

        var stream = await files.OpenReadAsync(photo.StorageKey, cancellationToken);
        if (stream is null)
        {
            logger.LogError("Falta el archivo de la foto {PhotoId} en el almacén.", photo.Id);
            return PhotoErrors.NotFound;
        }

        if (!scope.Value.IsSelf)
        {
            await audit.RecordAccessAsync("Wellness.PhotoViewed", "Member", photo.MemberId.ToString(), cancellationToken);
        }

        return new PhotoContent(stream, photo.ContentType);
    }
}

internal sealed class DeleteProgressPhotoHandler(WellnessDbContext db, IAccessContext access, IFileStore files, SecurityAudit audit, ILogger<DeleteProgressPhotoHandler> logger)
    : ICommandHandler<DeleteProgressPhotoCommand, Unit>
{
    public async Task<Result<Unit>> HandleAsync(DeleteProgressPhotoCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var photo = await db.ProgressPhotos.FirstOrDefaultAsync(p => p.Id == command.Id, cancellationToken);
        if (photo is null)
        {
            return PhotoErrors.NotFound;
        }

        var scope = await MemberScope.ResolveAsync(db, actor.Value, photo.MemberId, cancellationToken);
        if (scope.IsFailure)
        {
            return PhotoErrors.NotFound;
        }

        db.ProgressPhotos.Remove(photo);
        await db.SaveChangesAsync(cancellationToken);
        await PhotoState.DeleteFilesAsync(files, logger, [photo.StorageKey]);
        await audit.RecordAccessAsync("Wellness.PhotoDeleted", "Member", photo.MemberId.ToString(), cancellationToken);
        return default(Unit);
    }
}
