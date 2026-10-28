using FitCore.Application.Core;
using FitCore.Classes.Domain;
using FluentValidation;

namespace FitCore.Classes.Application.Sessions;

public sealed record ClassSessionDto(
    Guid Id,
    Guid ClassId,
    string ClassName,
    string Activity,
    Guid BranchId,
    string BranchName,
    Guid? SeriesId,
    Guid InstructorId,
    string InstructorName,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    int Capacity,
    string Status,
    string? Notes,
    string? CancelReason,
    DateTimeOffset? CancelledAt,
    string? AvailabilityOverrideReason,
    bool IsModified,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt)
{
    public static ClassSessionDto From(ClassSession s, string className, string activity, string instructorName, string branchName) =>
        new(
            s.Id,
            s.ClassId,
            className,
            activity,
            s.BranchId,
            branchName,
            s.SeriesId,
            s.InstructorId,
            instructorName,
            s.StartsAt,
            s.EndsAt,
            s.Capacity,
            s.Status.ToString(),
            s.Notes,
            s.CancelReason,
            s.CancelledAt,
            s.AvailabilityOverrideReason,
            s.IsModified,
            s.CreatedAt,
            s.UpdatedAt);
}

public sealed record ClassSeriesDto(
    Guid Id,
    Guid ClassId,
    string ClassName,
    Guid BranchId,
    string BranchName,
    Guid InstructorId,
    string InstructorName,
    int Capacity,
    int DurationMinutes,
    DateOnly StartDate,
    DateOnly EndDate,
    TimeOnly StartTime,
    IReadOnlyList<string> Days,
    string Status,
    string? CancelReason,
    int TotalSessions,
    int ScheduledSessions,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt)
{
    public static ClassSeriesDto From(ClassSeries s, string className, string instructorName, string branchName, int total, int scheduled) =>
        new(
            s.Id,
            s.ClassId,
            className,
            s.BranchId,
            branchName,
            s.InstructorId,
            instructorName,
            s.Capacity,
            s.DurationMinutes,
            s.StartDate,
            s.EndDate,
            s.StartTime,
            [.. s.Days.Select(d => d.ToString())],
            s.Status.ToString(),
            s.CancelReason,
            total,
            scheduled,
            s.CreatedAt,
            s.UpdatedAt);
}

/// <param name="Date">Fecha local de la sucursal que no se pudo programar.</param>
/// <param name="Reason"><c>instructor_overlap</c>, <c>instructor_unavailable</c> o <c>invalid_time</c> (la hora no existe ese día).</param>
public sealed record SkippedOccurrenceDto(DateOnly Date, string Reason);

/// <summary>Resultado de crear o regenerar una serie: nada se omite en silencio, cada fecha no programada se informa con su motivo.</summary>
/// <param name="Created">Sesiones nuevas.</param>
/// <param name="AlreadyExisted">Fechas que ya tenían su sesión (regenerar no duplica).</param>
/// <param name="PastDates">Fechas que ya pasaron y no se programaron.</param>
public sealed record SeriesRunDto(ClassSeriesDto Series, int Created, int AlreadyExisted, int PastDates, IReadOnlyList<SkippedOccurrenceDto> Skipped);

public sealed record CancelSeriesResultDto(ClassSeriesDto Series, int CancelledSessions);

/// <param name="ClassId">La sesión hereda la sucursal y la duración de la clase.</param>
/// <param name="InstructorId">Opcional: por defecto el instructor de la clase.</param>
/// <param name="Capacity">Opcional: por defecto la capacidad de la clase.</param>
/// <param name="AvailabilityOverrideReason">Solo si el horario queda fuera de la disponibilidad del instructor: queda auditado.</param>
public sealed record CreateSessionRequest(
    Guid ClassId,
    DateTimeOffset StartsAt,
    Guid? InstructorId,
    int? Capacity,
    string? Notes,
    string? AvailabilityOverrideReason);

/// <param name="Notes">Vacío borra las notas; omitido las deja como están.</param>
public sealed record UpdateSessionRequest(
    DateTimeOffset? StartsAt,
    Guid? InstructorId,
    int? Capacity,
    string? Notes,
    string? AvailabilityOverrideReason);

public sealed record CancelRequest(string Reason);

/// <param name="Days">Días de la semana en que se repite.</param>
/// <param name="StartTime">Hora local de la sucursal.</param>
public sealed record CreateSeriesRequest(
    Guid ClassId,
    DateOnly StartDate,
    DateOnly EndDate,
    TimeOnly StartTime,
    IReadOnlyList<DayOfWeek> Days,
    Guid? InstructorId,
    int? Capacity);

/// <param name="EndDate">Opcional: extiende la serie hasta esa fecha. Sin ella solo completa las sesiones que falten.</param>
public sealed record GenerateSeriesRequest(DateOnly? EndDate);

public sealed record ListSessionsQuery(
    PageRequest Page,
    DateTimeOffset? From,
    DateTimeOffset? To,
    Guid? ClassId,
    Guid? BranchId,
    Guid? InstructorId,
    Guid? SeriesId,
    SessionStatus? Status) : IQuery<PagedResult<ClassSessionDto>>;

public sealed record GetSessionQuery(Guid Id) : IQuery<ClassSessionDto>;

public sealed record CreateSessionCommand(CreateSessionRequest Request) : ICommand<ClassSessionDto>;

public sealed record UpdateSessionCommand(Guid Id, UpdateSessionRequest Request) : ICommand<ClassSessionDto>;

public sealed record CancelSessionCommand(Guid Id, CancelRequest Request) : ICommand<ClassSessionDto>;

public sealed record ListSeriesQuery(PageRequest Page, Guid? ClassId, Guid? BranchId, SeriesStatus? Status) : IQuery<PagedResult<ClassSeriesDto>>;

public sealed record GetSeriesQuery(Guid Id) : IQuery<ClassSeriesDto>;

public sealed record CreateSeriesCommand(CreateSeriesRequest Request) : ICommand<SeriesRunDto>;

public sealed record GenerateSeriesCommand(Guid Id, GenerateSeriesRequest Request) : ICommand<SeriesRunDto>;

public sealed record CancelSeriesCommand(Guid Id, CancelRequest Request) : ICommand<CancelSeriesResultDto>;

public sealed class CreateSessionRequestValidator : AbstractValidator<CreateSessionRequest>
{
    public CreateSessionRequestValidator()
    {
        RuleFor(x => x.ClassId).NotEmpty();
        RuleFor(x => x.StartsAt).NotEmpty();
        RuleFor(x => x.Capacity!.Value).InclusiveBetween(1, GroupClass.MaxCapacity).When(x => x.Capacity is not null);
        RuleFor(x => x.Notes).MaximumLength(ClassSession.MaxNotesLength);
        RuleFor(x => x.AvailabilityOverrideReason).MaximumLength(ClassSession.MaxOverrideReasonLength);
        RuleFor(x => x.AvailabilityOverrideReason!.Trim().Length)
            .GreaterThanOrEqualTo(ClassSession.MinOverrideReasonLength)
            .When(x => !string.IsNullOrWhiteSpace(x.AvailabilityOverrideReason))
            .WithMessage($"El motivo de la excepción debe tener al menos {ClassSession.MinOverrideReasonLength} caracteres.");
    }
}

public sealed class UpdateSessionRequestValidator : AbstractValidator<UpdateSessionRequest>
{
    public UpdateSessionRequestValidator()
    {
        RuleFor(x => x.Capacity!.Value).InclusiveBetween(1, GroupClass.MaxCapacity).When(x => x.Capacity is not null);
        RuleFor(x => x.Notes).MaximumLength(ClassSession.MaxNotesLength);
        RuleFor(x => x.AvailabilityOverrideReason).MaximumLength(ClassSession.MaxOverrideReasonLength);
        RuleFor(x => x.AvailabilityOverrideReason!.Trim().Length)
            .GreaterThanOrEqualTo(ClassSession.MinOverrideReasonLength)
            .When(x => !string.IsNullOrWhiteSpace(x.AvailabilityOverrideReason))
            .WithMessage($"El motivo de la excepción debe tener al menos {ClassSession.MinOverrideReasonLength} caracteres.");
    }
}

public sealed class CancelRequestValidator : AbstractValidator<CancelRequest>
{
    public CancelRequestValidator()
    {
        RuleFor(x => x.Reason).NotEmpty().MinimumLength(3).MaximumLength(ClassSession.MaxCancelReasonLength);
    }
}

public sealed class CreateSeriesRequestValidator : AbstractValidator<CreateSeriesRequest>
{
    public CreateSeriesRequestValidator()
    {
        RuleFor(x => x.ClassId).NotEmpty();
        RuleFor(x => x.Days).NotEmpty().Must(days => days.All(d => Enum.IsDefined(d))).WithMessage("Indique los días de la semana en que se repite.");
        RuleFor(x => x.Capacity!.Value).InclusiveBetween(1, GroupClass.MaxCapacity).When(x => x.Capacity is not null);
    }
}
