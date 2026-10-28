using FitCore.Analytics.Application.Reports;
using FitCore.Analytics.Application.Reports.Branches;
using FitCore.Analytics.Application.Reports.Financial;
using FitCore.Analytics.Application.Reports.Members;
using FitCore.Analytics.Application.Reports.Operations;
using FitCore.Analytics.Domain;
using FitCore.Analytics.Persistence;
using FitCore.Application.Core;
using FitCore.Platform.Domain;
using FitCore.SharedKernel;
using static FitCore.Analytics.Domain.CsvWriter;

namespace FitCore.Analytics.Application.Exports;

internal static class ExportErrors
{
    public static readonly Error ReportInvalid = Error.Validation("analytics.export_report_invalid", "El reporte a exportar no es válido.");
    public static Error TooLarge(int maxRows) => Error.Validation("analytics.export_too_large", $"La exportación supera {maxRows:N0} filas: acote el período, la sucursal o los filtros.");
    public static readonly Error RateLimited = Error.Conflict("analytics.export_rate_limited", $"Se alcanzó el límite de {ReportExport.MaxPerUserPerHour} exportaciones por hora: intente más tarde.");
    public static readonly Error NotFound = Error.NotFound("analytics.export_not_found", "La exportación no existe.");
    public static readonly Error Expired = Error.NotFound("analytics.export_expired", "La exportación venció: vuelva a generarla.");
}

/// <summary>Lo que necesita armar un archivo: el despachador (con el usuario de la solicitud) y el tope de filas.</summary>
internal sealed record ExportContext(IDispatcher Dispatcher, int MaxRows);

internal sealed record CsvTable(string FileSlug, IReadOnlyList<string> Headers, List<IReadOnlyList<string>> Rows);

/// <summary>
/// Qué permisos permiten exportar cada reporte (cualquiera de la lista) y cómo se convierte en filas. Los datos salen de los MISMOS casos de uso de los
/// reportes (por el despachador, con el usuario de la solicitud): el archivo hereda sus permisos, su alcance de sucursales y su filtro de instructor.
/// </summary>
internal static class Exporters
{
    private static readonly string[] Financial = [Permissions.ReportsFinancialRead];
    private static readonly string[] MembersArea = [Permissions.ReportsMembersRead];
    private static readonly string[] Operations = [Permissions.ReportsOperationsRead];
    private static readonly string[] Classes = [Permissions.ReportsOperationsRead, Permissions.ReportsInstructorRead];
    private static readonly string[] Compare = [Permissions.ReportsCompare];

    public static string[]? Required(ExportReport report) => report switch
    {
        ExportReport.Collections or ExportReport.PaymentMethods or ExportReport.OutstandingBalances or ExportReport.CashRegisters or ExportReport.Reversals => Financial,
        ExportReport.MemberAcquisitions or ExportReport.MemberRetention or ExportReport.MemberRenewals or ExportReport.MemberExpirations => MembersArea,
        ExportReport.Attendance => Operations,
        ExportReport.ClassOccupancy or ExportReport.ClassCancellations => Classes,
        ExportReport.BranchComparison => Compare,
        _ => null
    };

    public static async Task<Result<CsvTable>> BuildAsync(IDispatcher dispatcher, int maxRows, ExportRequest request, CancellationToken ct)
    {
        var context = new ExportContext(dispatcher, maxRows);
        var filter = new ReportFilter(request.Preset, request.From, request.To, request.BranchIds ?? []);
        return request.Report switch
        {
            ExportReport.Collections => await CollectionsAsync(context, filter, request, ct),
            ExportReport.PaymentMethods => await MethodsAsync(context, filter, ct),
            ExportReport.OutstandingBalances => await OutstandingAsync(context, request, ct),
            ExportReport.CashRegisters => await CashAsync(context, filter, request, ct),
            ExportReport.Reversals => await ReversalsAsync(context, filter, ct),
            ExportReport.MemberAcquisitions => await AcquisitionsAsync(context, filter, request, ct),
            ExportReport.MemberRetention => await RetentionAsync(context, filter, request, ct),
            ExportReport.MemberRenewals => await RenewalsAsync(context, filter, request, ct),
            ExportReport.MemberExpirations => await ExpirationsAsync(context, request, ct),
            ExportReport.Attendance => await AttendanceAsync(context, filter, ct),
            ExportReport.ClassOccupancy => await OccupancyAsync(context, filter, request, ct),
            ExportReport.ClassCancellations => await CancellationsAsync(context, filter, ct),
            ExportReport.BranchComparison => await ComparisonAsync(context, filter, ct),
            _ => ExportErrors.ReportInvalid
        };
    }

    /// <summary>
    /// Recorre todas las páginas de un listado (de 1000 en 1000). Si el total supera el tope, falla desde la primera página en lugar de entregar un
    /// archivo recortado sin avisar.
    /// </summary>
    private static async Task<Result<(string Zone, List<IReadOnlyList<string>> Rows)>> PageAllAsync<T>(
        int maxRows, Func<PageRequest, Task<Result<(string Zone, PagedResult<T> Page)>>> fetch, Func<T, string, IReadOnlyList<string>> map)
    {
        var rows = new List<IReadOnlyList<string>>();
        var zone = string.Empty;
        for (var page = 1; ; page++)
        {
            var result = await fetch(new PageRequest(page, Paging.MaxReportPageSize));
            if (result.IsFailure)
            {
                return result.Error;
            }

            (zone, var data) = result.Value;
            if (data.TotalCount > maxRows)
            {
                return ExportErrors.TooLarge(maxRows);
            }

            rows.AddRange(data.Items.Select(item => map(item, zone)));
            if (rows.Count >= data.TotalCount || data.Items.Count == 0)
            {
                return (zone, rows);
            }
        }
    }

    private static async Task<Result<CsvTable>> Listing<TReport, T>(
        ExportContext context, string slug, string[] headers, Func<PageRequest, IQuery<TReport>> query, Func<TReport, (string Zone, PagedResult<T>? Page)> select,
        Func<T, string, IReadOnlyList<string>> map, CancellationToken ct)
    {
        var result = await PageAllAsync<T>(
            context.MaxRows,
            async page =>
            {
                var report = await context.Dispatcher.QueryAsync(query(page), ct);
                if (report.IsFailure)
                {
                    return report.Error;
                }

                var (zone, data) = select(report.Value);
                return data is null ? ExportErrors.ReportInvalid : Result.Success((zone, data));
            },
            map);
        return result.IsFailure ? result.Error : new CsvTable(slug, headers, result.Value.Rows);
    }

    private static Task<Result<CsvTable>> CollectionsAsync(ExportContext d, ReportFilter f, ExportRequest r, CancellationToken ct) =>
        Listing(d, "cobros",
            ["Recibo", "Fecha de pago", "Sucursal", "Código del alumno", "Alumno", "Medio", "Detalle del medio", "Importe (USD)", "Estado", "Asignaciones", "Pago parcial"],
            page => new CollectionsQuery(f, r.Status ?? PaymentStatusFilter.Confirmed, r.Method, r.PlanId, page),
            (CollectionsReportDto x) => (x.Header.TimeZone, x.Payments),
            (p, zone) => [Text(p.ReceiptNumber), Text(LocalDateTime(p.PaidAt, zone)), Text(p.BranchName), Text(p.MemberCode), Text(p.MemberName), Text(p.Method), Text(p.MethodDetail), Number(p.Amount), Text(p.Status), Count(p.Allocations), Text(Bool(p.IsPartial))], ct);

    private static async Task<Result<CsvTable>> MethodsAsync(ExportContext d, ReportFilter f, CancellationToken ct)
    {
        var report = await d.Dispatcher.QueryAsync(new PaymentMethodsQuery(f), ct);
        if (report.IsFailure)
        {
            return report.Error;
        }

        List<IReadOnlyList<string>> rows = [.. report.Value.Methods.Select(m => (IReadOnlyList<string>)[Text(m.Method), Number(m.Amount), Count(m.Count), Percent(m.Share), Number(m.PreviousAmount), Percent(m.Variation.Percent)])];
        return new CsvTable("medios-de-pago", ["Medio", "Importe (USD)", "Pagos", "Participación (%)", "Importe del período anterior (USD)", "Variación (%)"], rows);
    }

    private static Task<Result<CsvTable>> OutstandingAsync(ExportContext d, ExportRequest r, CancellationToken ct)
    {
        var filter = new ReportFilter(PeriodPreset.Today, null, null, r.BranchIds ?? []);
        if (r.View == BalanceView.Obligations)
        {
            return Listing(d, "saldos-por-obligacion",
                ["Referencia", "Concepto", "Código del alumno", "Alumno", "Sucursal", "Plan", "Emisión", "Vencimiento", "Exigible (USD)", "Pagado (USD)", "Saldo (USD)", "Días de atraso", "Estado"],
                page => new OutstandingQuery(filter, BalanceView.Obligations, r.OverdueOnly ?? false, r.PlanId, page),
                (OutstandingReportDto x) => (x.Header.TimeZone, x.Obligations),
                (o, _) => [Text(o.Reference), Text(o.Concept), Text(o.MemberCode), Text(o.MemberName), Text(o.BranchName), Text(o.PlanName), Date(o.IssueDate), Date(o.DueDate), Number(o.Payable), Number(o.Paid), Number(o.Balance), Count(o.DaysOverdue), Text(o.Status)], ct);
        }

        return Listing(d, "saldos-por-alumno",
            ["Código del alumno", "Alumno", "Saldo pendiente (USD)", "Vencido (USD)", "Vence desde", "Obligaciones abiertas"],
            page => new OutstandingQuery(filter, BalanceView.Members, r.OverdueOnly ?? false, r.PlanId, page),
            (OutstandingReportDto x) => (x.Header.TimeZone, x.Members),
            (m, _) => [Text(m.MemberCode), Text(m.MemberName), Number(m.Outstanding), Number(m.Overdue), Date(m.OldestDueDate), Count(m.OpenObligations)], ct);
    }

    private static Task<Result<CsvTable>> CashAsync(ExportContext d, ReportFilter f, ExportRequest r, CancellationToken ct) =>
        Listing(d, "caja",
            ["Sucursal", "Caja", "Abrió", "Apertura", "Cerró", "Cierre", "Estado", "Fondo inicial (USD)", "Esperado (USD)", "Contado (USD)", "Diferencia (USD)"],
            page => new CashReportQuery(f, r.OnlyWithDifference ?? false, page),
            (CashReportDto x) => (x.Header.TimeZone, x.Sessions),
            (s, zone) => [Text(s.BranchName), Text(s.RegisterName), Text(s.OpenedBy), Text(LocalDateTime(s.OpenedAt, zone)), Text(s.ClosedBy), Text(LocalDateTime(s.ClosedAt, zone)), Text(s.Status), Number(s.OpeningAmount), Number(s.Expected), Number(s.Counted), Number(s.Difference)], ct);

    private static Task<Result<CsvTable>> ReversalsAsync(ExportContext d, ReportFilter f, CancellationToken ct) =>
        Listing(d, "reversiones",
            ["Recibo", "Fecha de pago", "Fecha de reversión", "Sucursal", "Código del alumno", "Alumno", "Medio", "Importe (USD)", "Motivo", "Revertido por"],
            page => new ReversalsQuery(f, page),
            (ReversalsReportDto x) => (x.Header.TimeZone, x.Reversals),
            (p, zone) => [Text(p.ReceiptNumber), Text(LocalDateTime(p.PaidAt, zone)), Text(LocalDateTime(p.ReversedAt, zone)), Text(p.BranchName), Text(p.MemberCode), Text(p.MemberName), Text(p.Method), Number(p.Amount), Text(p.Reason), Text(p.ReversedBy)], ct);

    private static Task<Result<CsvTable>> AcquisitionsAsync(ExportContext d, ReportFilter f, ExportRequest r, CancellationToken ct) =>
        Listing(d, "membresias-vendidas",
            ["Fecha de venta", "Tipo", "Código del alumno", "Alumno", "Sucursal", "Plan", "Inicio", "Fin", "Estado", "Precio acordado (USD)"],
            page => new AcquisitionsQuery(f, r.PlanId, r.Kind ?? MembershipKind.Any, page),
            (AcquisitionsReportDto x) => (x.Header.TimeZone, x.Memberships),
            (m, zone) => [Text(LocalDateTime(m.SoldAt, zone)), Text(m.Kind), Text(m.MemberCode), Text(m.MemberName), Text(m.BranchName), Text(m.PlanName), Date(m.StartDate), Date(m.EndDate), Text(m.Status), Number(m.AgreedPrice)], ct);

    private static Task<Result<CsvTable>> RetentionAsync(ExportContext d, ReportFilter f, ExportRequest r, CancellationToken ct) =>
        Listing(d, "alumnos-sin-membresia-vigente",
            ["Código del alumno", "Alumno", "Sucursal", "Último plan", "Último vencimiento"],
            page => new RetentionQuery(f, r.PlanId, page),
            (RetentionReportDto x) => (x.Header.TimeZone, x.LostMembers),
            (m, _) => [Text(m.MemberCode), Text(m.MemberName), Text(m.BranchName), Text(m.LastPlan), Date(m.LastEndDate)], ct);

    private static Task<Result<CsvTable>> RenewalsAsync(ExportContext d, ReportFilter f, ExportRequest r, CancellationToken ct) =>
        Listing(d, "membresias-sin-renovar",
            ["Código del alumno", "Alumno", "Sucursal", "Plan", "Venció", "Días desde el vencimiento"],
            page => new RenewalsQuery(f, r.PlanId, page),
            (RenewalsReportDto x) => (x.Header.TimeZone, x.NotRenewedMembers),
            (m, _) => [Text(m.MemberCode), Text(m.MemberName), Text(m.BranchName), Text(m.PlanName), Date(m.EndDate), Count(m.DaysSinceEnd)], ct);

    private static Task<Result<CsvTable>> ExpirationsAsync(ExportContext d, ExportRequest r, CancellationToken ct) =>
        Listing(d, "proximos-vencimientos",
            ["Código del alumno", "Alumno", "Sucursal", "Plan", "Vence", "Días restantes", "Ya renovó", "Saldo (USD)"],
            page => new ExpirationsQuery(new ReportFilter(PeriodPreset.Today, null, null, r.BranchIds ?? []), r.Days ?? 30, r.PlanId, r.HideRenewed ?? false, page),
            (ExpirationsReportDto x) => (x.Header.TimeZone, x.Memberships),
            (m, _) => [Text(m.MemberCode), Text(m.MemberName), Text(m.BranchName), Text(m.PlanName), Date(m.EndDate), Count(m.DaysLeft), Text(Bool(m.AlreadyRenewed)), Number(m.Balance)], ct);

    private static Task<Result<CsvTable>> AttendanceAsync(ExportContext d, ReportFilter f, CancellationToken ct) =>
        Listing(d, "asistencia-por-alumno",
            ["Código del alumno", "Alumno", "Sucursal", "Visitas", "Días con visita", "Última visita"],
            page => new AttendanceQuery(f, AttendanceGrouping.Auto, AttendanceView.Members, page),
            (AttendanceReportDto x) => (x.Header.TimeZone, x.Members),
            (m, zone) => [Text(m.MemberCode), Text(m.MemberName), Text(m.BranchName), Count(m.Visits), Count(m.Days), Text(LocalDateTime(m.LastVisit, zone))], ct);

    private static Task<Result<CsvTable>> OccupancyAsync(ExportContext d, ReportFilter f, ExportRequest r, CancellationToken ct) =>
        Listing(d, "ocupacion-de-clases",
            ["Inicio", "Clase", "Actividad", "Instructor", "Sucursal", "Estado", "Capacidad", "Confirmados", "En espera", "Asistieron", "No asistieron", "Ocupación (%)"],
            page => new ClassOccupancyQuery(f, r.Activity, r.ClassId, page),
            (ClassOccupancyReportDto x) => (x.Header.TimeZone, x.SessionList),
            (s, zone) => [Text(LocalDateTime(s.StartsAt, zone)), Text(s.ClassName), Text(s.Activity), Text(s.InstructorName), Text(s.BranchName), Text(s.Status), Count(s.Capacity), Count(s.Confirmed), Count(s.Waitlisted), Count(s.Attended), Count(s.NoShow), Percent(s.OccupancyPercent)], ct);

    private static Task<Result<CsvTable>> CancellationsAsync(ExportContext d, ReportFilter f, CancellationToken ct) =>
        Listing(d, "clases-canceladas",
            ["Inicio", "Cancelada el", "Clase", "Instructor", "Sucursal", "Motivo", "Reservas afectadas"],
            page => new ClassCancellationsQuery(f, page),
            (ClassCancellationsReportDto x) => (x.Header.TimeZone, x.CancelledSessions),
            (s, zone) => [Text(LocalDateTime(s.StartsAt, zone)), Text(LocalDateTime(s.CancelledAt, zone)), Text(s.ClassName), Text(s.InstructorName), Text(s.BranchName), Text(s.Reason), Count(s.ReservationsAffected)], ct);

    private static async Task<Result<CsvTable>> ComparisonAsync(ExportContext d, ReportFilter f, CancellationToken ct)
    {
        var report = await d.Dispatcher.QueryAsync(new BranchComparisonQuery(f), ct);
        if (report.IsFailure)
        {
            return report.Error;
        }

        // Las áreas que el usuario no puede ver salen con las celdas vacías (la columna existe, el dato no).
        IReadOnlyList<string> Row(BranchComparisonRowDto b) =>
        [
            Text(b.BranchName),
            Number(b.Financial?.Collected), Number(b.Financial?.PreviousCollected), Percent(b.Financial?.CollectedShare), Number(b.Financial?.Outstanding), Number(b.Financial?.Overdue),
            Count(b.Members?.ActiveMembers), Count(b.Members?.NewEnrollments), Count(b.Members?.Renewals),
            Count(b.Operations?.Visits), Count(b.Operations?.SessionsHeld), Count(b.Operations?.SessionsCancelled), Percent(b.Operations?.OccupancyPercent), Percent(b.Operations?.CancellationRatePercent),
            Text(Bool(b.HasActivity))
        ];
        List<IReadOnlyList<string>> rows = [.. report.Value.Branches.Select(Row), Row(report.Value.Total)];
        return new CsvTable(
            "comparacion-de-sucursales",
            ["Sucursal", "Cobrado (USD)", "Cobrado del período anterior (USD)", "Participación del cobrado (%)", "Saldo pendiente (USD)", "Saldo vencido (USD)",
             "Alumnos activos", "Nuevas inscripciones", "Renovaciones", "Visitas", "Clases realizadas", "Clases canceladas", "Ocupación (%)", "Cancelación de reservas (%)", "Con actividad"],
            rows);
    }
}
