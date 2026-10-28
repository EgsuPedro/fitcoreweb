using FitCore.Application.Core;

namespace FitCore.Wellness.Application.Adherence;

/// <param name="Expected">Sesiones esperadas (días de la rutina vigente, prorrateados por los días cubiertos).</param>
/// <param name="Percent">Nulo si no había rutina asignada esa semana.</param>
public sealed record TrainingWeekDto(DateOnly WeekStart, int DaysCovered, int Expected, int Completed, decimal? Percent);

/// <param name="Percent">Sesiones hechas (sin contar de más cada semana) sobre las esperadas; nulo si no había rutina en el rango.</param>
public sealed record TrainingAdherenceDto(DateOnly From, DateOnly To, int Expected, int Sessions, decimal? Percent, IReadOnlyList<TrainingWeekDto> Weeks);

public sealed record NutritionWeekDto(DateOnly WeekStart, int Planned, int Completed, int Partial, int Skipped, int NotLogged, decimal? Percent);

/// <param name="Percent">Completas cuentan 1 y parciales 0,5 sobre las comidas planeadas; lo no registrado cuenta 0.</param>
public sealed record NutritionAdherenceDto(DateOnly From, DateOnly To, int Planned, int Completed, int Partial, int Skipped, int NotLogged, decimal? Percent, IReadOnlyList<NutritionWeekDto> Weeks);

public sealed record TrainingAdherenceQuery(Guid MemberId, DateOnly? From, DateOnly? To) : IQuery<TrainingAdherenceDto>;

public sealed record NutritionAdherenceQuery(Guid MemberId, DateOnly? From, DateOnly? To) : IQuery<NutritionAdherenceDto>;
