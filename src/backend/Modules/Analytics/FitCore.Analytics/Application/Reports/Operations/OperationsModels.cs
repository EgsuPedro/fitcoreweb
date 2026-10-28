using FitCore.Analytics.Application.Dashboard;
using FitCore.Application.Core;

namespace FitCore.Analytics.Application.Reports.Operations;

public enum AttendanceGrouping
{
    /// <summary>Según el largo del período: día (hasta 31 días), semana (hasta 120) o mes.</summary>
    Auto = 0,
    Day = 1,
    Week = 2,
    Month = 3
}

public enum AttendanceView
{
    Summary = 1,
    Members = 2
}

// ---- Asistencia al gimnasio ---------------------------------------------------------------------------------------------

/// <param name="Start">Inicio (fecha local) del tramo: el día, el lunes de la semana o el primer día del mes.</param>
public sealed record AttendancePointDto(DateOnly Start, int Visits, int Visitors);

public sealed record AttendanceBranchDto(Guid BranchId, string BranchName, int Visits, int Visitors, decimal? Share);

/// <param name="Key">Día de la semana (Monday…Sunday) o la hora local (0–23).</param>
public sealed record AttendanceSlotDto(string Key, int Visits);

public sealed record FrequencyBucketDto(string Label, int Members);

/// <summary>Un alumno con sus visitas: solo código y nombre (nunca documento, teléfono ni correo).</summary>
public sealed record MemberVisitsDto(string MemberCode, string MemberName, string BranchName, int Visits, int Days, DateTimeOffset LastVisit);

/// <summary>
/// Entradas al gimnasio por fecha y hora de entrada, en la hora local. «Frecuencia» = visitas ÷ alumnos distintos que asistieron.
/// El tramo (día, semana o mes) y los días/horas con más afluencia salen de la hora local de la sucursal, no de UTC.
/// </summary>
public sealed record AttendanceReportDto(
    ReportHeader Header,
    string Grouping,
    IReadOnlyList<KpiDto> Summary,
    IReadOnlyList<AttendancePointDto> Series,
    IReadOnlyList<AttendanceBranchDto> ByBranch,
    IReadOnlyList<AttendanceSlotDto> ByWeekday,
    IReadOnlyList<AttendanceSlotDto> ByHour,
    IReadOnlyList<FrequencyBucketDto> Frequency,
    PagedResult<MemberVisitsDto>? Members);

public sealed record AttendanceQuery(ReportFilter Filter, AttendanceGrouping Grouping, AttendanceView View, PageRequest Page) : IQuery<AttendanceReportDto>;

// ---- Ocupación de clases --------------------------------------------------------------------------------------------------

public sealed record SessionOccupancyDto(
    Guid SessionId,
    DateTimeOffset StartsAt,
    string ClassName,
    string Activity,
    string InstructorName,
    Guid BranchId,
    string BranchName,
    string Status,
    int Capacity,
    int Confirmed,
    int Waitlisted,
    int Attended,
    int NoShow,
    decimal? OccupancyPercent);

public sealed record OccupancyGroupDto(string Key, string Name, int Sessions, int Capacity, int Confirmed, int Waitlisted, decimal? OccupancyPercent);

public sealed record OccupancySlotDto(string Weekday, int Hour, int Sessions, int Capacity, int Confirmed, decimal? OccupancyPercent);

public sealed record ClassBranchPerformanceDto(
    Guid BranchId,
    string BranchName,
    int Held,
    int Cancelled,
    int Capacity,
    int Confirmed,
    decimal? OccupancyPercent,
    int NoShows);

/// <summary>
/// Ocupación de las sesiones del período (por inicio de la sesión). Solo cuentan reservas Confirmadas sobre sesiones no canceladas con capacidad;
/// la lista de espera y los cupos ofrecidos se informan aparte como demanda y nunca como ocupación. Ocupación media = Σ ocupados ÷ Σ capacidad.
/// </summary>
public sealed record ClassOccupancyReportDto(
    ReportHeader Header,
    bool InstructorOnly,
    IReadOnlyList<KpiDto> Summary,
    SessionBreakdownDto Sessions,
    IReadOnlyList<OccupancyGroupDto> ByClass,
    IReadOnlyList<OccupancyGroupDto> ByActivity,
    IReadOnlyList<OccupancySlotDto> TopSlots,
    IReadOnlyList<ClassBranchPerformanceDto> ByBranch,
    PagedResult<SessionOccupancyDto> SessionList);

public sealed record ClassOccupancyQuery(ReportFilter Filter, string? Activity, Guid? ClassId, PageRequest Page) : IQuery<ClassOccupancyReportDto>;

// ---- Cancelaciones de clases -----------------------------------------------------------------------------------------------

public sealed record CancelledSessionDto(
    Guid SessionId,
    DateTimeOffset StartsAt,
    DateTimeOffset? CancelledAt,
    string ClassName,
    string InstructorName,
    string BranchName,
    string? Reason,
    int ReservationsAffected);

public sealed record CancellationByClassDto(string ClassName, int Sessions, int Cancelled, decimal? CancelRatePercent);

/// <summary>
/// Cancelaciones: sesiones canceladas por el gimnasio y reservas canceladas por el alumno o el personal (no las que se cancelaron al cancelar la
/// sesión). Tasa de cancelación de reservas = canceladas ÷ (confirmadas + canceladas) de las sesiones no canceladas.
/// </summary>
public sealed record ClassCancellationsReportDto(
    ReportHeader Header,
    bool InstructorOnly,
    IReadOnlyList<KpiDto> Summary,
    IReadOnlyList<CancellationByClassDto> ByClass,
    PagedResult<CancelledSessionDto> CancelledSessions);

public sealed record ClassCancellationsQuery(ReportFilter Filter, PageRequest Page) : IQuery<ClassCancellationsReportDto>;
