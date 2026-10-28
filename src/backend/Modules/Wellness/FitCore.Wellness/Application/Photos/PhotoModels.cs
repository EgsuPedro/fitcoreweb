using FitCore.Application.Core;
using FitCore.SharedKernel;
using FitCore.Wellness.Domain;
using FluentValidation;

namespace FitCore.Wellness.Application.Photos;

/// <param name="GuardianName">Obligatorio si la persona es menor de edad (lo registra el personal con su representante); se ignora en adultos.</param>
public sealed record GrantPhotoConsentRequest(string? GuardianName);

public sealed record PhotoConsentEventDto(Guid Id, string Action, string Source, int NoticeVersion, string? GuardianName, DateTimeOffset RecordedAt);

/// <param name="Status"><c>None</c> (nunca se registró), <c>Granted</c> o <c>Revoked</c>.</param>
/// <param name="CurrentNoticeVersion">Versión vigente del aviso de privacidad que debe mostrarse al pedir el consentimiento.</param>
public sealed record PhotoConsentDto(
    string Status,
    DateTimeOffset? Since,
    string? Source,
    int? NoticeVersion,
    string? GuardianName,
    int CurrentNoticeVersion,
    int PhotoCount,
    IReadOnlyList<PhotoConsentEventDto> History);

/// <param name="DeletedPhotos">Fotos y archivos que se borraron al retirar el consentimiento.</param>
public sealed record RevokePhotoConsentResult(int DeletedPhotos, PhotoConsentDto Consent);

public sealed record ProgressPhotoDto(Guid Id, Guid MemberId, DateOnly TakenOn, string Angle, string ContentType, long SizeBytes, DateTimeOffset CreatedAt);

/// <summary>Contenido de una foto: lo abre y lo libera quien lo recibe (el endpoint).</summary>
public sealed record PhotoContent(Stream Content, string ContentType);

public sealed record GetPhotoConsentQuery(Guid MemberId) : IQuery<PhotoConsentDto>;

public sealed record GrantPhotoConsentCommand(Guid MemberId, GrantPhotoConsentRequest Request) : ICommand<PhotoConsentDto>;

public sealed record RevokePhotoConsentCommand(Guid MemberId) : ICommand<RevokePhotoConsentResult>;

public sealed record UploadProgressPhotoCommand(Guid MemberId, DateOnly TakenOn, PhotoAngle Angle, Stream Content) : ICommand<ProgressPhotoDto>;

public sealed record ListProgressPhotosQuery(Guid MemberId) : IQuery<IReadOnlyList<ProgressPhotoDto>>;

public sealed record GetProgressPhotoContentQuery(Guid Id) : IQuery<PhotoContent>;

public sealed record DeleteProgressPhotoCommand(Guid Id) : ICommand<Unit>;

public sealed class GrantPhotoConsentRequestValidator : AbstractValidator<GrantPhotoConsentRequest>
{
    public GrantPhotoConsentRequestValidator() =>
        RuleFor(x => x.GuardianName).MaximumLength(PhotoConsent.MaxGuardianNameLength);
}

public static class PhotoErrors
{
    public static readonly Error MemberNotFound = Error.NotFound("photos.member_not_found", "Alumno no encontrado.");
    public static readonly Error MemberInactive = Error.Conflict("photos.member_inactive", "El alumno está inactivo.");
    public static readonly Error NotFound = Error.NotFound("photos.not_found", "Foto no encontrada.");
    public static readonly Error ConsentRequired = Error.Conflict("photos.consent_required", "El alumno debe dar su consentimiento antes de guardar fotos de progreso.");
    public static readonly Error ConsentAlreadyGranted = Error.Conflict("photos.consent_already_granted", "El alumno ya dio su consentimiento.");
    public static readonly Error ConsentNotGranted = Error.Conflict("photos.consent_not_granted", "El alumno no tiene un consentimiento vigente que retirar.");
    public static readonly Error GuardianRequired = Error.Validation("photos.guardian_required", "Para un menor de edad indique el nombre de su representante.");
    public static readonly Error GuardianConsentByStaff = Error.Validation("photos.guardian_consent_by_staff", "El consentimiento de un menor de edad lo registra el personal con su representante.");
    public static readonly Error FileRequired = Error.Validation("photos.file_required", "Adjunte el archivo de la foto.");
    public static readonly Error FileTooLarge = Error.Validation("photos.file_too_large", "La foto pesa más de 8 MB.");
    public static readonly Error FileInvalid = Error.Validation("photos.file_invalid", "El archivo debe ser una imagen JPEG o PNG válida.");
    public static readonly Error DateInvalid = Error.Validation("photos.date_invalid", "La fecha de la foto no puede ser futura ni de hace más de diez años.");
    public static readonly Error AngleInvalid = Error.Validation("photos.angle_invalid", "El ángulo de la foto no es válido (Front, Side, Back u Other).");
    public static readonly Error LimitReached = Error.Conflict("photos.limit_reached", "El alumno ya tiene el máximo de fotos (100): borre alguna antes de subir otra.");
}
