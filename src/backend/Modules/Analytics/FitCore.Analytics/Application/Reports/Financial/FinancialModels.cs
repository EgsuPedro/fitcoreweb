using FitCore.Application.Core;

namespace FitCore.Analytics.Application.Reports.Financial;

public enum PaymentStatusFilter
{
    /// <summary>Solo lo cobrado (vigente). Es el valor por defecto: lo revertido no cuenta como ingreso.</summary>
    Confirmed = 1,
    PendingVerification = 2,

    /// <summary>Informe histórico de pagos revertidos, por la fecha en que se pagaron.</summary>
    Reversed = 3,
    All = 4
}

public enum BalanceView
{
    Members = 1,
    Obligations = 2
}

// ---- Cobros ---------------------------------------------------------------------------------------------------------

public sealed record CollectionsByBranchDto(Guid BranchId, string BranchName, decimal Amount, int Count, decimal? Share);

/// <param name="PlanId">Nulo = cobros sin plan (productos, servicios sueltos).</param>
public sealed record CollectionsByPlanDto(Guid? PlanId, string PlanName, decimal Amount, decimal? Share);

public sealed record CollectionRowDto(
    Guid PaymentId,
    string ReceiptNumber,
    DateTimeOffset PaidAt,
    Guid BranchId,
    string BranchName,
    string MemberCode,
    string MemberName,
    string Method,
    string? MethodDetail,
    decimal Amount,
    string Status,
    int Allocations,
    bool IsPartial);

/// <summary>
/// Cobros del período por fecha de pago. El resumen se calcula siempre sobre los pagos Confirmados; los pendientes de verificar y los revertidos
/// se informan aparte. «Pago parcial» = un pago cuya asignación cubre solo una parte del exigible de la obligación (un abono).
/// </summary>
public sealed record CollectionsReportDto(
    ReportHeader Header,
    IReadOnlyList<KpiDto> Summary,
    IReadOnlyList<CollectionsByBranchDto> ByBranch,
    IReadOnlyList<CollectionsByPlanDto> ByPlan,
    PagedResult<CollectionRowDto> Payments);

public sealed record CollectionsQuery(ReportFilter Filter, PaymentStatusFilter Status, string? Method, Guid? PlanId, PageRequest Page) : IQuery<CollectionsReportDto>;

// ---- Medios de pago ----------------------------------------------------------------------------------------------------

public sealed record MethodRowDto(string Method, decimal Amount, int Count, decimal? Share, decimal PreviousAmount, FitCore.Analytics.Domain.Variation Variation);

public sealed record PaymentMethodsReportDto(ReportHeader Header, decimal Total, decimal PreviousTotal, IReadOnlyList<MethodRowDto> Methods, IReadOnlyList<string> OtherDetails);

public sealed record PaymentMethodsQuery(ReportFilter Filter) : IQuery<PaymentMethodsReportDto>;

// ---- Saldos pendientes ----------------------------------------------------------------------------------------------

public sealed record AgingRowDto(string Label, int Count, decimal Amount);

public sealed record BalanceByBranchDto(Guid BranchId, string BranchName, decimal Outstanding, decimal Overdue, int OpenCount, int OverdueCount);

/// <summary>Un alumno con saldo: código y nombre, nunca documento ni contacto.</summary>
public sealed record MemberBalanceDto(Guid MemberId, string MemberCode, string MemberName, decimal Outstanding, decimal Overdue, DateOnly OldestDueDate, int OpenObligations);

public sealed record ObligationBalanceDto(
    Guid ObligationId,
    string Reference,
    string Concept,
    string MemberCode,
    string MemberName,
    Guid BranchId,
    string BranchName,
    string? PlanName,
    DateOnly IssueDate,
    DateOnly DueDate,
    decimal Payable,
    decimal Paid,
    decimal Balance,
    int DaysOverdue,
    string Status);

/// <summary>
/// Saldo pendiente de HOY (no existe el saldo de una fecha pasada): lo exigible menos lo cobrado de las obligaciones abiertas. Vencido = ya pasó
/// su fecha de vencimiento. Se muestra por alumno o por obligación.
/// </summary>
public sealed record OutstandingReportDto(
    ReportHeader Header,
    decimal Outstanding,
    decimal Overdue,
    int OpenCount,
    int OverdueCount,
    IReadOnlyList<AgingRowDto> Aging,
    IReadOnlyList<BalanceByBranchDto> ByBranch,
    PagedResult<MemberBalanceDto>? Members,
    PagedResult<ObligationBalanceDto>? Obligations);

public sealed record OutstandingQuery(ReportFilter Filter, BalanceView View, bool OverdueOnly, Guid? PlanId, PageRequest Page) : IQuery<OutstandingReportDto>;

// ---- Caja ---------------------------------------------------------------------------------------------------------------

public sealed record CashSessionRowDto(
    Guid SessionId,
    Guid BranchId,
    string BranchName,
    string RegisterName,
    string OpenedBy,
    DateTimeOffset OpenedAt,
    string? ClosedBy,
    DateTimeOffset? ClosedAt,
    string Status,
    decimal OpeningAmount,
    decimal? Expected,
    decimal? Counted,
    decimal? Difference);

public sealed record CashMovementTotalDto(string Type, string Direction, decimal Amount, int Count);

/// <summary>
/// Turnos de caja abiertos en el período con el esperado, contado y diferencia tal como se guardaron al cerrar (negativo = faltante, positivo =
/// sobrante), y los movimientos de caja del período por tipo.
/// </summary>
public sealed record CashReportDto(
    ReportHeader Header,
    int SessionsOpened,
    int SessionsStillOpen,
    int SessionsClosed,
    decimal Shortage,
    decimal Surplus,
    int SessionsWithDifference,
    IReadOnlyList<CashMovementTotalDto> Movements,
    PagedResult<CashSessionRowDto> Sessions);

public sealed record CashReportQuery(ReportFilter Filter, bool OnlyWithDifference, PageRequest Page) : IQuery<CashReportDto>;

// ---- Reversiones y anulaciones --------------------------------------------------------------------------------------------

public sealed record ReversalRowDto(
    Guid PaymentId,
    string ReceiptNumber,
    DateTimeOffset PaidAt,
    DateTimeOffset ReversedAt,
    string BranchName,
    string MemberCode,
    string MemberName,
    string Method,
    decimal Amount,
    string? Reason,
    string? ReversedBy);

public sealed record VoidedObligationRowDto(
    Guid ObligationId,
    string Reference,
    string Concept,
    DateTimeOffset VoidedAt,
    string BranchName,
    string MemberCode,
    string MemberName,
    decimal Payable,
    string? Reason,
    string? VoidedBy);

/// <summary>Pagos revertidos (por la fecha de reversión) y obligaciones anuladas (por la fecha de anulación): informe histórico, fuera de los indicadores vigentes.</summary>
public sealed record ReversalsReportDto(
    ReportHeader Header,
    decimal ReversedAmount,
    int ReversedCount,
    decimal VoidedAmount,
    int VoidedCount,
    PagedResult<ReversalRowDto> Reversals,
    PagedResult<VoidedObligationRowDto> VoidedObligations);

public sealed record ReversalsQuery(ReportFilter Filter, PageRequest Page) : IQuery<ReversalsReportDto>;
