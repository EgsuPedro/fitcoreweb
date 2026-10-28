using FitCore.Application.Core;
using FitCore.Classes.Domain;
using FluentValidation;

namespace FitCore.Classes.Application.GroupClasses;

public sealed record GroupClassDto(
    Guid Id,
    string Name,
    string? Description,
    string Activity,
    Guid InstructorId,
    string InstructorName,
    Guid BranchId,
    string BranchName,
    int DurationMinutes,
    int Capacity,
    string Level,
    string? Requirements,
    int MinBookingNoticeMinutes,
    int MaxBookingAdvanceDays,
    int CancellationDeadlineMinutes,
    bool WaitlistEnabled,
    int WaitlistCapacity,
    int OfferWindowMinutes,
    bool RequireActiveMembership,
    bool ApplyPaymentPolicy,
    int? MinAge,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt)
{
    public static GroupClassDto From(GroupClass c, string instructorName, string branchName) =>
        new(
            c.Id,
            c.Name,
            c.Description,
            c.Activity.ToString(),
            c.InstructorId,
            instructorName,
            c.BranchId,
            branchName,
            c.DurationMinutes,
            c.Capacity,
            c.Level.ToString(),
            c.Requirements,
            c.MinBookingNoticeMinutes,
            c.MaxBookingAdvanceDays,
            c.CancellationDeadlineMinutes,
            c.WaitlistEnabled,
            c.WaitlistCapacity,
            c.OfferWindowMinutes,
            c.RequireActiveMembership,
            c.ApplyPaymentPolicy,
            c.MinAge,
            c.IsActive,
            c.CreatedAt,
            c.UpdatedAt);
}

/// <param name="BranchId">Obligatoria al crear; al editar solo se admite la misma (la sucursal de una clase no cambia).</param>
/// <param name="InstructorId">Id del perfil de entrenador (<c>GET /trainers</c>).</param>
/// <param name="MinBookingNoticeMinutes">Minutos antes del inicio en que se cierra la reserva (0 = hasta el inicio).</param>
/// <param name="MaxBookingAdvanceDays">Con cuántos días de anticipación se abre la reserva.</param>
/// <param name="CancellationDeadlineMinutes">Mientras falten al menos estos minutos se puede cancelar (0 = hasta el inicio).</param>
/// <param name="WaitlistCapacity">Alumnos que pueden esperar por sesión; 0 si no hay lista de espera.</param>
/// <param name="OfferWindowMinutes">Plazo para aceptar un cupo ofrecido desde la lista de espera.</param>
public sealed record GroupClassRequest(
    Guid? BranchId,
    string Name,
    string? Description,
    ActivityType Activity,
    Guid InstructorId,
    int DurationMinutes,
    int Capacity,
    ClassLevel Level,
    string? Requirements,
    int MinBookingNoticeMinutes,
    int MaxBookingAdvanceDays,
    int CancellationDeadlineMinutes,
    bool WaitlistEnabled,
    int WaitlistCapacity,
    int OfferWindowMinutes,
    bool RequireActiveMembership,
    bool ApplyPaymentPolicy,
    int? MinAge)
{
    public GroupClassDetails ToDetails() =>
        new(
            Name,
            Description,
            Activity,
            InstructorId,
            DurationMinutes,
            Capacity,
            Level,
            Requirements,
            MinBookingNoticeMinutes,
            MaxBookingAdvanceDays,
            CancellationDeadlineMinutes,
            WaitlistEnabled,
            WaitlistCapacity,
            OfferWindowMinutes,
            RequireActiveMembership,
            ApplyPaymentPolicy,
            MinAge);
}

public sealed record SetGroupClassStatusRequest(bool IsActive);

public sealed record ListGroupClassesQuery(
    PageRequest Page,
    string? Search,
    ActivityType? Activity,
    ClassLevel? Level,
    Guid? BranchId,
    Guid? InstructorId,
    bool? IsActive) : IQuery<PagedResult<GroupClassDto>>;

public sealed record GetGroupClassQuery(Guid Id) : IQuery<GroupClassDto>;

public sealed record CreateGroupClassCommand(GroupClassRequest Request) : ICommand<GroupClassDto>;

public sealed record UpdateGroupClassCommand(Guid Id, GroupClassRequest Request) : ICommand<GroupClassDto>;

public sealed record SetGroupClassStatusCommand(Guid Id, SetGroupClassStatusRequest Request) : ICommand<GroupClassDto>;

public sealed class GroupClassRequestValidator : AbstractValidator<GroupClassRequest>
{
    public GroupClassRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MinimumLength(GroupClass.MinNameLength).MaximumLength(GroupClass.MaxNameLength);
        RuleFor(x => x.Description).MaximumLength(GroupClass.MaxDescriptionLength);
        RuleFor(x => x.Requirements).MaximumLength(GroupClass.MaxRequirementsLength);
        RuleFor(x => x.Activity).IsInEnum();
        RuleFor(x => x.Level).IsInEnum();
        RuleFor(x => x.InstructorId).NotEmpty();
        RuleFor(x => x.DurationMinutes).InclusiveBetween(GroupClass.MinDurationMinutes, GroupClass.MaxDurationMinutes);
        RuleFor(x => x.Capacity).InclusiveBetween(1, GroupClass.MaxCapacity);
        RuleFor(x => x.MinBookingNoticeMinutes).InclusiveBetween(0, GroupClass.MaxNoticeMinutes);
        RuleFor(x => x.MaxBookingAdvanceDays).InclusiveBetween(1, GroupClass.MaxAdvanceDays);
        RuleFor(x => x.CancellationDeadlineMinutes).InclusiveBetween(0, GroupClass.MaxCancellationMinutes);
        RuleFor(x => x.WaitlistCapacity).InclusiveBetween(0, GroupClass.MaxWaitlistCapacity);
        RuleFor(x => x.OfferWindowMinutes).InclusiveBetween(GroupClass.MinOfferWindowMinutes, GroupClass.MaxOfferWindowMinutes);
        RuleFor(x => x.MinAge!.Value).InclusiveBetween(0, GroupClass.MaxMinAge).When(x => x.MinAge is not null);
    }
}
