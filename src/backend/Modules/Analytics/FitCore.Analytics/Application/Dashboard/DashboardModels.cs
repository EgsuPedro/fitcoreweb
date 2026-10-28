using FitCore.Analytics.Domain;
using FitCore.Application.Core;

namespace FitCore.Analytics.Application.Dashboard;

/// <summary>Cobrado por medio de pago en el período; la participación es sobre el total cobrado (nula si no hubo cobros).</summary>
public sealed record PaymentMethodShareDto(string Method, decimal Amount, int Count, decimal? Share);

public sealed record FinancialDashboardDto(IReadOnlyList<KpiDto> Kpis, IReadOnlyList<PaymentMethodShareDto> Methods);

public sealed record MembersDashboardDto(IReadOnlyList<KpiDto> Kpis);

/// <param name="Sessions">Estado de las sesiones del período: realizadas, canceladas, por ocurrir y pendientes de cierre (pasadas sin cerrar).</param>
public sealed record SessionBreakdownDto(int Held, int Cancelled, int Upcoming, int PendingClosure);

public sealed record OperationsDashboardDto(IReadOnlyList<KpiDto> Kpis, SessionBreakdownDto Sessions);

public sealed record InstructorDashboardDto(IReadOnlyList<KpiDto> Kpis, SessionBreakdownDto Sessions);

/// <summary>
/// El tablero: cada sección viaja solo si el usuario tiene el permiso de su área; las demás vienen nulas y se nombran en
/// <paramref name="OmittedSections"/> (el servidor nunca calcula ni entrega lo que el usuario no puede ver).
/// </summary>
public sealed record DashboardDto(
    string Currency,
    string TimeZone,
    DateOnly Today,
    DateTimeOffset GeneratedAt,
    PeriodDto Period,
    PeriodDto Comparable,
    IReadOnlyList<Guid> BranchIds,
    FinancialDashboardDto? Financial,
    MembersDashboardDto? Members,
    OperationsDashboardDto? Operations,
    InstructorDashboardDto? Instructor,
    IReadOnlyList<string> OmittedSections);

public sealed record GetDashboardQuery(PeriodPreset Preset, DateOnly? From, DateOnly? To, IReadOnlyList<Guid> BranchIds, int ExpiringDays) : IQuery<DashboardDto>;
