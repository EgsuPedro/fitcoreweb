using FitCore.Analytics.Domain;
using FitCore.Application.Core;

namespace FitCore.Analytics.Application.Reports.Branches;

/// <param name="CollectedShare">Parte del total cobrado por las sucursales comparadas (nula si nadie cobró).</param>
/// <param name="Outstanding">Saldo pendiente de hoy (foto actual; no se compara con un período anterior).</param>
public sealed record FinancialComparisonDto(
    decimal Collected,
    decimal PreviousCollected,
    Variation CollectedVariation,
    decimal? CollectedShare,
    decimal Outstanding,
    decimal Overdue);

public sealed record MembersComparisonDto(
    int ActiveMembers,
    int PreviousActiveMembers,
    Variation ActiveVariation,
    int NewEnrollments,
    int PreviousNewEnrollments,
    Variation EnrollmentsVariation,
    int Renewals,
    int PreviousRenewals,
    Variation RenewalsVariation);

/// <param name="Capacity">Σ capacidad de las sesiones no canceladas (con la que se pondera la ocupación).</param>
/// <param name="Booked">Σ reservas confirmadas de esas sesiones.</param>
public sealed record OperationsComparisonDto(
    int Visits,
    int PreviousVisits,
    Variation VisitsVariation,
    int SessionsHeld,
    int SessionsCancelled,
    int Capacity,
    int Booked,
    decimal? OccupancyPercent,
    decimal? PreviousOccupancyPercent,
    Variation OccupancyVariation,
    int ReservationsCancelled,
    decimal? CancellationRatePercent);

/// <summary>
/// Una sucursal. Las secciones que el usuario no puede ver vienen nulas. <paramref name="HasActivity"/> falso = la sucursal no tuvo actividad en
/// el período: se muestra «Sin datos», no se oculta.
/// </summary>
public sealed record BranchComparisonRowDto(
    Guid BranchId,
    string BranchName,
    bool IsActive,
    bool HasActivity,
    FinancialComparisonDto? Financial,
    MembersComparisonDto? Members,
    OperationsComparisonDto? Operations);

public sealed record MonthRangeDto(DateOnly From, DateOnly To);

/// <summary>Evolución mensual de una sucursal, alineada con <see cref="BranchComparisonReportDto.Months"/> (un valor por mes, 0 si no hubo actividad).</summary>
public sealed record BranchEvolutionDto(
    Guid BranchId,
    string BranchName,
    IReadOnlyList<decimal>? Collected,
    IReadOnlyList<int>? NewEnrollments,
    IReadOnlyList<int>? Visits);

/// <summary>
/// Comparación de las sucursales elegidas (siempre dentro de las que el usuario puede consultar), con el mismo período, la misma zona horaria y las
/// mismas definiciones para todas. Incluye el total de las seleccionadas y la evolución por mes calendario.
/// </summary>
public sealed record BranchComparisonReportDto(
    ReportHeader Header,
    IReadOnlyList<BranchComparisonRowDto> Branches,
    BranchComparisonRowDto Total,
    IReadOnlyList<MonthRangeDto> Months,
    IReadOnlyList<BranchEvolutionDto> Evolution,
    IReadOnlyList<string> OmittedSections);

public sealed record BranchComparisonQuery(ReportFilter Filter) : IQuery<BranchComparisonReportDto>;
