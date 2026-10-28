using FitCore.Application.Core;
using FitCore.Classes.Domain;
using FluentValidation;

namespace FitCore.Classes.Application.Attendance;

/// <param name="ReservationId">La reserva de la que se registra o se corrige la asistencia.</param>
/// <param name="Notes">Opcional (≤ 300 caracteres): por ejemplo, llegó tarde o se retiró antes.</param>
public sealed record AttendanceEntryRequest(Guid ReservationId, AttendanceOutcome Outcome, string? Notes);

/// <summary>Registra la asistencia de varios participantes a la vez. Repetirlo no duplica: lo ya registrado se conserva o se corrige.</summary>
public sealed record RecordAttendanceRequest(IReadOnlyList<AttendanceEntryRequest> Entries);

/// <param name="MarkPendingAsNoShow">Los confirmados sin resultado se registran como «no asistió». Sin esto quedan sin resultado.</param>
public sealed record CompleteSessionRequest(bool MarkPendingAsNoShow = false);

public sealed record RosterEntryDto(
    Guid ReservationId,
    Guid MemberId,
    string MemberName,
    string MemberCode,
    string ReservationStatus,
    int? WaitlistPosition,
    string? Outcome,
    string? Notes,
    DateTimeOffset? RecordedAt,
    bool HasGymEntry,
    DateTimeOffset BookedAt,
    DateTimeOffset? OfferExpiresAt);

/// <summary>Quién tiene cupo, quién tiene una invitación pendiente y quién espera, con la asistencia ya registrada.</summary>
public sealed record RosterDto(
    Guid SessionId,
    Guid ClassId,
    string ClassName,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string SessionStatus,
    int Capacity,
    int Confirmed,
    int Attended,
    int NoShow,
    int Pending,
    IReadOnlyList<RosterEntryDto> Participants,
    IReadOnlyList<RosterEntryDto> Offers,
    IReadOnlyList<RosterEntryDto> Waitlist);

public sealed record ClassAttendanceDto(
    Guid Id,
    Guid ReservationId,
    Guid SessionId,
    Guid ClassId,
    string ClassName,
    string Activity,
    Guid BranchId,
    string BranchName,
    string InstructorName,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    Guid MemberId,
    string MemberName,
    string MemberCode,
    string Outcome,
    string? Notes,
    bool HasGymEntry,
    DateTimeOffset RecordedAt,
    string RecordedByName,
    DateTimeOffset? CorrectedAt);

public sealed record GetRosterQuery(Guid SessionId) : IQuery<RosterDto>;

public sealed record RecordAttendanceCommand(Guid SessionId, RecordAttendanceRequest Request) : ICommand<RosterDto>;

public sealed record CompleteSessionCommand(Guid SessionId, CompleteSessionRequest Request) : ICommand<RosterDto>;

public sealed record ListClassAttendanceQuery(
    PageRequest Page,
    Guid? SessionId,
    Guid? MemberId,
    Guid? BranchId,
    AttendanceOutcome? Outcome,
    DateTimeOffset? From,
    DateTimeOffset? To) : IQuery<PagedResult<ClassAttendanceDto>>;

public sealed record ListMyClassAttendanceQuery(PageRequest Page, AttendanceOutcome? Outcome) : IQuery<PagedResult<ClassAttendanceDto>>;

public sealed class RecordAttendanceRequestValidator : AbstractValidator<RecordAttendanceRequest>
{
    public const int MaxEntries = 200;

    public RecordAttendanceRequestValidator()
    {
        RuleFor(x => x.Entries).NotNull().NotEmpty().Must(e => e.Count <= MaxEntries).WithMessage($"Se admiten hasta {MaxEntries} registros por solicitud.");
        RuleForEach(x => x.Entries).ChildRules(entry =>
        {
            entry.RuleFor(e => e.ReservationId).NotEmpty();
            entry.RuleFor(e => e.Outcome).IsInEnum();
            entry.RuleFor(e => e.Notes).MaximumLength(ClassAttendance.MaxNotesLength);
        });
    }
}
