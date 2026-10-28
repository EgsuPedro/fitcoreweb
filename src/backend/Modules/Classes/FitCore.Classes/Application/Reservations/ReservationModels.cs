using FitCore.Application.Core;
using FitCore.Classes.Domain;
using FluentValidation;

namespace FitCore.Classes.Application.Reservations;

public sealed record ReservationDto(
    Guid Id,
    Guid SessionId,
    Guid ClassId,
    string ClassName,
    string Activity,
    Guid BranchId,
    string BranchName,
    string InstructorName,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string SessionStatus,
    Guid MemberId,
    string MemberName,
    string MemberCode,
    string Status,
    string Source,
    DateTimeOffset BookedAt,
    DateTimeOffset? CancelledAt,
    string? CancelReason,
    bool LateCancellation);

/// <param name="MemberId">El alumno por quien se reserva: el personal debe poder administrar la sucursal de la sesión.</param>
public sealed record CreateReservationRequest(Guid SessionId, Guid MemberId);

/// <summary>El alumno que reserva por sí mismo es siempre el de su sesión: nunca se envía su identificador.</summary>
public sealed record CreateMyReservationRequest(Guid SessionId);

/// <param name="Reason">Opcional dentro del plazo. Obligatorio (≥ 5 caracteres) si el personal cancela fuera de plazo: queda marcado como excepción.</param>
public sealed record CancelReservationRequest(string? Reason);

public sealed record ListReservationsQuery(
    PageRequest Page,
    Guid? SessionId,
    Guid? MemberId,
    Guid? BranchId,
    ReservationStatus? Status,
    DateTimeOffset? From,
    DateTimeOffset? To) : IQuery<PagedResult<ReservationDto>>;

public sealed record GetReservationQuery(Guid Id) : IQuery<ReservationDto>;

public sealed record CreateReservationCommand(CreateReservationRequest Request, string? IdempotencyKey) : ICommand<ReservationDto>;

public sealed record CancelReservationCommand(Guid Id, CancelReservationRequest Request) : ICommand<ReservationDto>;

public sealed record ListMyReservationsQuery(PageRequest Page, ReservationStatus? Status, bool UpcomingOnly) : IQuery<PagedResult<ReservationDto>>;

public sealed record CreateMyReservationCommand(CreateMyReservationRequest Request, string? IdempotencyKey) : ICommand<ReservationDto>;

public sealed record CancelMyReservationCommand(Guid Id, CancelReservationRequest Request) : ICommand<ReservationDto>;

public sealed class CreateReservationRequestValidator : AbstractValidator<CreateReservationRequest>
{
    public CreateReservationRequestValidator()
    {
        RuleFor(x => x.SessionId).NotEmpty();
        RuleFor(x => x.MemberId).NotEmpty();
    }
}

public sealed class CreateMyReservationRequestValidator : AbstractValidator<CreateMyReservationRequest>
{
    public CreateMyReservationRequestValidator()
    {
        RuleFor(x => x.SessionId).NotEmpty();
    }
}

public sealed class CancelReservationRequestValidator : AbstractValidator<CancelReservationRequest>
{
    public CancelReservationRequestValidator()
    {
        RuleFor(x => x.Reason).MaximumLength(ClassReservation.MaxReasonLength);
    }
}
