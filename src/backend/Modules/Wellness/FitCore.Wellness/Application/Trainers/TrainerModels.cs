using FitCore.Application.Core;
using FitCore.SharedKernel;
using FitCore.Wellness.Domain;
using FluentValidation;

namespace FitCore.Wellness.Application.Trainers;

public sealed record TrainerDto(
    Guid Id,
    Guid UserId,
    string FullName,
    string? Email,
    IReadOnlyList<string> Specialties,
    string? Certifications,
    string? Bio,
    string Status,
    DateOnly JoinedOn,
    IReadOnlyList<Guid> BranchIds,
    int ActiveMembers,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);

public sealed record AvailabilityDto(Guid Id, string Day, string Start, string End, Guid? BranchId);

public sealed record AssignmentDto(
    Guid Id,
    Guid TrainerId,
    string TrainerName,
    Guid MemberId,
    string MemberCode,
    string MemberName,
    Guid BranchId,
    DateOnly AssignedOn,
    DateOnly? EndedOn,
    string? EndReason,
    bool IsActive);

public sealed record TrainerNoteDto(Guid Id, Guid MemberId, Guid TrainerId, string TrainerName, string Text, DateTimeOffset CreatedAt);

public sealed record CreateTrainerRequest(Guid UserId, IReadOnlyList<string>? Specialties, string? Certifications, string? Bio, DateOnly? JoinedOn);

public sealed record UpdateTrainerRequest(IReadOnlyList<string>? Specialties, string? Certifications, string? Bio, DateOnly JoinedOn);

public sealed record SetTrainerStatusRequest(bool IsActive);

public sealed record AvailabilitySlotRequest(DayOfWeek Day, string Start, string End, Guid? BranchId);

public sealed record SetAvailabilityRequest(IReadOnlyList<AvailabilitySlotRequest>? Slots);

public sealed record AssignMemberRequest(Guid MemberId, string? Reason);

public sealed record UnassignMemberRequest(string Reason);

public sealed record CreateTrainerNoteRequest(string Text);

public sealed record ListTrainersQuery(PageRequest Page, string? Search, TrainerStatus? Status, Guid? BranchId) : IQuery<PagedResult<TrainerDto>>;

public sealed record GetTrainerQuery(Guid Id) : IQuery<TrainerDto>;

public sealed record GetMyTrainerQuery : IQuery<TrainerDto>;

public sealed record CreateTrainerCommand(CreateTrainerRequest Request) : ICommand<TrainerDto>;

public sealed record UpdateTrainerCommand(Guid Id, UpdateTrainerRequest Request) : ICommand<TrainerDto>;

public sealed record SetTrainerStatusCommand(Guid Id, SetTrainerStatusRequest Request) : ICommand<TrainerDto>;

public sealed record GetAvailabilityQuery(Guid TrainerId) : IQuery<IReadOnlyList<AvailabilityDto>>;

public sealed record SetAvailabilityCommand(Guid TrainerId, SetAvailabilityRequest Request) : ICommand<IReadOnlyList<AvailabilityDto>>;

public sealed record AssignMemberCommand(Guid TrainerId, AssignMemberRequest Request) : ICommand<AssignmentDto>;

public sealed record UnassignMemberCommand(Guid TrainerId, Guid MemberId, UnassignMemberRequest Request) : ICommand<AssignmentDto>;

public sealed record ListTrainerMembersQuery(Guid TrainerId, bool ActiveOnly, PageRequest Page) : IQuery<PagedResult<AssignmentDto>>;

public sealed record ListMemberAssignmentsQuery(Guid MemberId) : IQuery<IReadOnlyList<AssignmentDto>>;

public sealed record CreateTrainerNoteCommand(Guid MemberId, CreateTrainerNoteRequest Request) : ICommand<TrainerNoteDto>;

public sealed record ListTrainerNotesQuery(Guid MemberId, PageRequest Page) : IQuery<PagedResult<TrainerNoteDto>>;

public static class TrainerRules
{
    public static TimeOnly ParseTime(string value) =>
        TimeOnly.ParseExact(value, ["HH:mm", "HH:mm:ss"], System.Globalization.CultureInfo.InvariantCulture);

    public static bool TryParseTime(string? value, out TimeOnly time) =>
        TimeOnly.TryParseExact(value, ["HH:mm", "HH:mm:ss"], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out time);
}

public sealed class CreateTrainerRequestValidator : AbstractValidator<CreateTrainerRequest>
{
    public CreateTrainerRequestValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        TrainerProfileRules.Apply(this, x => x.Specialties, x => x.Certifications, x => x.Bio);
    }
}

public sealed class UpdateTrainerRequestValidator : AbstractValidator<UpdateTrainerRequest>
{
    public UpdateTrainerRequestValidator()
    {
        RuleFor(x => x.JoinedOn).NotEmpty();
        TrainerProfileRules.Apply(this, x => x.Specialties, x => x.Certifications, x => x.Bio);
    }
}

internal static class TrainerProfileRules
{
    public static void Apply<T>(
        AbstractValidator<T> validator,
        System.Linq.Expressions.Expression<Func<T, IReadOnlyList<string>?>> specialties,
        System.Linq.Expressions.Expression<Func<T, string?>> certifications,
        System.Linq.Expressions.Expression<Func<T, string?>> bio)
    {
        validator.RuleFor(specialties)
            .Must(list => list is null || TrainerProfile.NormalizeSpecialties(list).Length <= TrainerProfile.MaxSpecialties)
            .WithMessage($"Máximo {TrainerProfile.MaxSpecialties} especialidades.")
            .Must(list => list is null || list.All(s => s.Trim().Length <= TrainerProfile.MaxSpecialtyLength))
            .WithMessage($"Cada especialidad admite hasta {TrainerProfile.MaxSpecialtyLength} caracteres.");
        validator.RuleFor(certifications).MaximumLength(TrainerProfile.MaxCertificationsLength);
        validator.RuleFor(bio).MaximumLength(TrainerProfile.MaxBioLength);
    }
}

public sealed class SetAvailabilityRequestValidator : AbstractValidator<SetAvailabilityRequest>
{
    public SetAvailabilityRequestValidator()
    {
        RuleFor(x => x.Slots).NotNull();
        RuleForEach(x => x.Slots).ChildRules(slot =>
        {
            slot.RuleFor(s => s.Day).IsInEnum();
            slot.RuleFor(s => s.Start).Must(v => TrainerRules.TryParseTime(v, out _)).WithMessage("La hora de inicio debe tener el formato HH:mm.");
            slot.RuleFor(s => s.End).Must(v => TrainerRules.TryParseTime(v, out _)).WithMessage("La hora de fin debe tener el formato HH:mm.");
        });
    }
}

public sealed class AssignMemberRequestValidator : AbstractValidator<AssignMemberRequest>
{
    public AssignMemberRequestValidator()
    {
        RuleFor(x => x.MemberId).NotEmpty();
        RuleFor(x => x.Reason).MaximumLength(TrainerAssignment.MaxReasonLength);
    }
}

public sealed class UnassignMemberRequestValidator : AbstractValidator<UnassignMemberRequest>
{
    public UnassignMemberRequestValidator()
    {
        RuleFor(x => x.Reason).NotEmpty().MinimumLength(3).MaximumLength(TrainerAssignment.MaxReasonLength);
    }
}

public sealed class CreateTrainerNoteRequestValidator : AbstractValidator<CreateTrainerNoteRequest>
{
    public CreateTrainerNoteRequestValidator()
    {
        RuleFor(x => x.Text).NotEmpty().MaximumLength(TrainerNote.MaxLength);
    }
}

public static class TrainerErrors
{
    public static readonly Error NotFound = Error.NotFound("trainers.not_found", "Entrenador no encontrado.");
    public static readonly Error NoProfile = Error.NotFound("trainers.no_profile", "Su usuario no tiene perfil de entrenador.");
    public static readonly Error AlreadyExists = Error.Conflict("trainers.already_exists", "Ese usuario ya es entrenador.");
    public static readonly Error UserNotFound = Error.Validation("trainers.user_not_found", "El usuario no existe o no está a su alcance.");
    public static readonly Error UserInactive = Error.Validation("trainers.user_inactive", "El usuario está inactivo.");
    public static readonly Error Inactive = Error.Conflict("trainers.inactive", "El entrenador está inactivo.");
    public static readonly Error HasActiveMembers = Error.Conflict("trainers.has_active_members", "El entrenador tiene alumnos asignados: reasígnelos antes de desactivarlo.");
    public static readonly Error MemberNotFound = Error.NotFound("trainers.member_not_found", "Alumno no encontrado.");
    public static readonly Error MemberInactive = Error.Conflict("trainers.member_inactive", "El alumno está inactivo.");
    public static readonly Error AlreadyAssigned = Error.Conflict("trainers.already_assigned", "El alumno ya está asignado a este entrenador.");
    public static readonly Error NoActiveAssignment = Error.Conflict("trainers.no_active_assignment", "El alumno no está asignado a este entrenador.");
    public static readonly Error BranchMismatch = Error.Conflict("trainers.branch_mismatch", "El entrenador no trabaja en la sucursal del alumno.");
    public static readonly Error AssignmentConflict = Error.Conflict("trainers.assignment_conflict", "El alumno cambió de entrenador al mismo tiempo; intente de nuevo.");
    public static readonly Error NotYours = Error.Forbidden("trainers.not_yours", "Solo puede consultar o modificar sus propios datos de entrenador.");
    public static readonly Error NotAssigned = Error.Forbidden("trainers.not_assigned", "Solo el entrenador asignado al alumno puede ver o escribir sus observaciones.");
    public static readonly Error BranchInvalid = Error.Validation("trainers.availability_branch_invalid", "Alguna sucursal de las franjas no existe, está inactiva o no está a su alcance.");
}
