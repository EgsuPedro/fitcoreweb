using FitCore.Analytics.Domain;
using FitCore.Application.Core;

namespace FitCore.Analytics.Application;

public sealed record PeriodDto(DateOnly From, DateOnly To, int Days, DateTimeOffset StartUtc, DateTimeOffset EndUtcExclusive)
{
    public static PeriodDto Of(ReportPeriod period) => new(period.From, period.To, period.Days, period.StartUtc, period.EndUtcExclusive);
}

public sealed record BranchOptionDto(Guid Id, string Code, string Name, string TimeZone, bool IsActive);

/// <summary>Qué áreas de analítica puede consultar el usuario (se valida de nuevo en cada endpoint; esto solo orienta a la pantalla).</summary>
public sealed record AnalyticsSectionsDto(bool Financial, bool Members, bool Operations, bool Instructor, bool Compare, bool Export);

/// <summary>
/// Contexto común de los reportes: el período ya resuelto (con la hora del gimnasio y su equivalente en UTC), el comparable, las
/// sucursales que el usuario puede consultar y las áreas permitidas. Es la única fuente de «qué es hoy» y de la zona horaria.
/// </summary>
public sealed record AnalyticsContextDto(
    string Currency,
    string TimeZone,
    DateOnly Today,
    PeriodPreset Preset,
    PeriodDto Period,
    PeriodDto Comparable,
    IReadOnlyList<Guid> BranchIds,
    IReadOnlyList<BranchOptionDto> Branches,
    AnalyticsSectionsDto Sections,
    int MaxRangeDays);

public sealed record GetAnalyticsContextQuery(PeriodPreset Preset, DateOnly? From, DateOnly? To, IReadOnlyList<Guid> BranchIds) : IQuery<AnalyticsContextDto>;
