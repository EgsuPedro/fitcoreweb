using FitCore.Analytics.Domain;
using FitCore.Application.Core;

namespace FitCore.Analytics.Application.Reports.Members;

public enum MembershipKind
{
    /// <summary>Todas.</summary>
    Any = 0,

    /// <summary>Primera membresía del alumno.</summary>
    New = 1,

    /// <summary>Viene de otra membresía (renovación).</summary>
    Renewal = 2,

    /// <summary>El alumno ya tuvo membresías pero esta no es una renovación.</summary>
    Reincorporation = 3
}

// ---- Resumen -----------------------------------------------------------------------------------------------------------

public sealed record CountRowDto(string Key, string Label, int Count);

public sealed record MembersByPlanDto(Guid PlanId, string PlanName, int Members);

public sealed record MembersByBranchDto(Guid BranchId, string BranchName, int Active, int Inactive);

/// <summary>
/// Foto de alumnos y membresías. «Activos» y «nuevos» siguen el período; el estado de las membresías, la distribución por plan y por
/// sucursal y los alumnos con saldo son una foto de HOY (no existe el estado de una fecha pasada).
/// </summary>
public sealed record MembersSummaryDto(
    ReportHeader Header,
    IReadOnlyList<KpiDto> Kpis,
    IReadOnlyList<CountRowDto> MembershipStates,
    IReadOnlyList<MembersByPlanDto> ByPlan,
    IReadOnlyList<MembersByBranchDto> ByBranch);

public sealed record MembersSummaryQuery(ReportFilter Filter, Guid? PlanId) : IQuery<MembersSummaryDto>;

// ---- Altas, inscripciones y bajas -----------------------------------------------------------------------------------------

public sealed record TrendRowDto(DateOnly From, DateOnly To, int NewMembers, int NewEnrollments, int Renewals, int Reincorporations, int Gains, int Losses);

public sealed record MembershipRowDto(
    Guid MembershipId,
    DateTimeOffset SoldAt,
    string Kind,
    string MemberCode,
    string MemberName,
    Guid BranchId,
    string BranchName,
    string PlanName,
    DateOnly StartDate,
    DateOnly EndDate,
    string Status,
    decimal AgreedPrice);

/// <summary>
/// Altas = inscripciones nuevas + reincorporaciones. Bajas = alumnos activos el día anterior al tramo que ya no tienen membresía vigente al
/// terminarlo (no equivale a «membresía cancelada»). La tendencia se parte por mes calendario dentro del período.
/// </summary>
public sealed record AcquisitionsReportDto(
    ReportHeader Header,
    IReadOnlyList<KpiDto> Summary,
    IReadOnlyList<TrendRowDto> Trend,
    PagedResult<MembershipRowDto> Memberships);

public sealed record AcquisitionsQuery(ReportFilter Filter, Guid? PlanId, MembershipKind Kind, PageRequest Page) : IQuery<AcquisitionsReportDto>;

// ---- Retención ----------------------------------------------------------------------------------------------------------

public sealed record RetentionGroupDto(string Key, string Name, int Cohort, int Retained, decimal? RetentionPercent);

public sealed record LostMemberDto(string MemberCode, string MemberName, string BranchName, string? LastPlan, DateOnly? LastEndDate);

/// <summary>
/// Retención por cohorte. Cohorte = alumnos activos el último día anterior al período (<paramref name="CohortDate"/>); retenidos = los de esa
/// cohorte que siguen activos el último día del período. Retención = retenidos ÷ cohorte; sin cohorte no hay porcentaje («Sin base»).
/// </summary>
public sealed record RetentionReportDto(
    ReportHeader Header,
    DateOnly CohortDate,
    DateOnly EndDate,
    int Cohort,
    int Retained,
    int Lost,
    decimal? RetentionPercent,
    decimal? PreviousRetentionPercent,
    FitCore.Analytics.Domain.Variation Variation,
    IReadOnlyList<RetentionGroupDto> ByBranch,
    IReadOnlyList<RetentionGroupDto> ByPlan,
    PagedResult<LostMemberDto> LostMembers);

public sealed record RetentionQuery(ReportFilter Filter, Guid? PlanId, PageRequest Page) : IQuery<RetentionReportDto>;

// ---- Renovaciones -----------------------------------------------------------------------------------------------------------

public sealed record RenewalGroupDto(string Key, string Name, int Due, int Renewed, decimal? RatePercent);

public sealed record NotRenewedDto(string MemberCode, string MemberName, string BranchName, string PlanName, DateOnly EndDate, int DaysSinceEnd);

/// <summary>
/// Tasa de renovación. Cohorte = membresías activas que vencieron dentro del período; renovada = existe otra membresía no cancelada que viene de
/// ella. Tasa = renovadas ÷ vencidas. Una renovación posterior al corte cuenta cuando exista, así que un período reciente subestima la tasa.
/// </summary>
public sealed record RenewalsReportDto(
    ReportHeader Header,
    int Due,
    int Renewed,
    int NotRenewed,
    decimal? RatePercent,
    decimal? PreviousRatePercent,
    FitCore.Analytics.Domain.Variation Variation,
    IReadOnlyList<RenewalGroupDto> ByPlan,
    IReadOnlyList<RenewalGroupDto> ByBranch,
    PagedResult<NotRenewedDto> NotRenewedMembers);

public sealed record RenewalsQuery(ReportFilter Filter, Guid? PlanId, PageRequest Page) : IQuery<RenewalsReportDto>;

// ---- Próximos vencimientos ---------------------------------------------------------------------------------------------------

public sealed record ExpiringRowDto(
    Guid MembershipId,
    string MemberCode,
    string MemberName,
    string BranchName,
    string PlanName,
    DateOnly EndDate,
    int DaysLeft,
    bool AlreadyRenewed,
    decimal Balance);

/// <summary>Membresías activas que terminan entre hoy y hoy + N días (foto de hoy), con si ya tienen una renovación y lo que el alumno debe.</summary>
public sealed record ExpirationsReportDto(
    ReportHeader Header,
    int Days,
    int Total,
    int AlreadyRenewed,
    IReadOnlyList<CountRowDto> Buckets,
    PagedResult<ExpiringRowDto> Memberships);

public sealed record ExpirationsQuery(ReportFilter Filter, int Days, Guid? PlanId, bool HideRenewed, PageRequest Page) : IQuery<ExpirationsReportDto>;
