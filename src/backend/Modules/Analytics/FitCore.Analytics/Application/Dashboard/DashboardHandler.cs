using FitCore.Analytics.Domain;
using FitCore.Analytics.Persistence;
using FitCore.Application.Core;
using FitCore.Platform.Domain;
using FitCore.Platform.Security;
using FitCore.SharedKernel;

namespace FitCore.Analytics.Application.Dashboard;

internal static class DashboardErrors
{
    public static readonly Error ExpiringDaysInvalid = Error.Validation("analytics.expiring_days_invalid", "Los días para próximos vencimientos pueden ser 7, 15 o 30.");
}

internal sealed class GetDashboardHandler(AnalyticsDbContext db, IAccessContext access, IClock clock)
    : IQueryHandler<GetDashboardQuery, DashboardDto>
{
    private static readonly int[] AllowedExpiringDays = [7, 15, 30];

    public async Task<Result<DashboardDto>> HandleAsync(GetDashboardQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequirePermissionAsync(Permissions.AnalyticsDashboardRead, cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        if (!AllowedExpiringDays.Contains(query.ExpiringDays))
        {
            return DashboardErrors.ExpiringDaysInvalid;
        }

        var request = await AnalyticsScopes.ResolveRequestAsync(db, actor.Value, query.BranchIds, query.Preset, query.From, query.To, clock, cancellationToken);
        if (request.IsFailure)
        {
            return request.Error;
        }

        var (scope, periods) = request.Value;
        var user = actor.Value;
        var omitted = new List<string>();

        FinancialDashboardDto? financial = null;
        if (user.Has(Permissions.ReportsFinancialRead))
        {
            financial = await FinancialAsync(scope, periods, cancellationToken);
        }
        else
        {
            omitted.Add("financial");
        }

        MembersDashboardDto? members = null;
        if (user.Has(Permissions.ReportsMembersRead))
        {
            members = await MembersAsync(scope, periods, query.ExpiringDays, cancellationToken);
        }
        else
        {
            omitted.Add("members");
        }

        OperationsDashboardDto? operations = null;
        if (user.Has(Permissions.ReportsOperationsRead))
        {
            operations = await OperationsAsync(scope, periods, cancellationToken);
        }
        else
        {
            omitted.Add("operations");
        }

        InstructorDashboardDto? instructor = null;
        if (user.Has(Permissions.ReportsInstructorRead))
        {
            instructor = await InstructorAsync(scope, periods, user.UserId, cancellationToken);
        }
        else
        {
            omitted.Add("instructor");
        }

        return new DashboardDto(
            "USD",
            scope.TimeZoneId,
            scope.Today,
            clock.UtcNow,
            PeriodDto.Of(periods.Current),
            PeriodDto.Of(periods.Previous),
            scope.BranchIds,
            financial,
            members,
            operations,
            instructor,
            omitted);
    }

    private async Task<FinancialDashboardDto> FinancialAsync(AnalyticsScope scope, PeriodPair periods, CancellationToken ct)
    {
        var branches = scope.BranchIds;
        var now = await Metrics.PaymentsAsync(db, branches, periods.Current, ct);
        var before = await Metrics.PaymentsAsync(db, branches, periods.Previous, ct);
        var methodsNow = await Metrics.PaymentMethodsAsync(db, branches, periods.Current, ct);
        var methodsBefore = await Metrics.PaymentMethodsAsync(db, branches, periods.Previous, ct);
        var reversedNow = await Metrics.ReversalsAsync(db, branches, periods.Current, ct);
        var reversedBefore = await Metrics.ReversalsAsync(db, branches, periods.Previous, ct);
        var issuedNow = await Metrics.ObligationsIssuedAsync(db, branches, periods.Current, ct);
        var issuedBefore = await Metrics.ObligationsIssuedAsync(db, branches, periods.Previous, ct);
        var outstanding = await Metrics.OutstandingAsync(db, branches, scope.Today, ct);

        static decimal Cash(IReadOnlyList<MethodTotal> methods) => methods.Where(m => m.Method == "Cash").Sum(m => m.Amount);
        static decimal Other(IReadOnlyList<MethodTotal> methods) => methods.Where(m => m.Method != "Cash").Sum(m => m.Amount);

        var kpis = new List<KpiDto>
        {
            Kpi.Money("collected", "Total cobrado", "Pagos confirmados recibidos en el período (importe de cada pago, sin duplicar abonos ni asignaciones). No es utilidad: no descuenta costos ni gastos.", "Fecha de pago", now.Collected, before.Collected, "financial/collections"),
            Kpi.Money("collected_cash", "Cobrado en efectivo", "Parte del total cobrado que se recibió en efectivo.", "Fecha de pago", Cash(methodsNow), Cash(methodsBefore), "financial/payment-methods"),
            Kpi.Money("collected_other", "Cobrado por otros medios", "Parte del total cobrado por transferencia, tarjeta u otros medios.", "Fecha de pago", Other(methodsNow), Other(methodsBefore), "financial/payment-methods"),
            Kpi.Money("pending_verification", "Transferencias por verificar", "Pagos registrados que aún no se confirman. No cuentan como cobrado.", "Fecha de pago", now.PendingVerification, before.PendingVerification, "financial/collections"),
            Kpi.Money("issued", "Obligaciones generadas", "Importe exigible de las obligaciones emitidas en el período, sin las anuladas. Es lo que se facturó al alumno, no lo cobrado.", "Fecha de emisión", issuedNow, issuedBefore, "financial/outstanding-balances"),
            Kpi.SnapshotMoney("outstanding", "Saldo pendiente", "Lo que los alumnos deben hoy: exigible menos lo cobrado de las obligaciones abiertas. Es una foto de hoy; no existe el saldo de una fecha pasada.", "Hoy", outstanding.Outstanding, "financial/outstanding-balances"),
            Kpi.SnapshotMoney("overdue", "Saldo vencido", "Parte del saldo pendiente cuya fecha de vencimiento ya pasó.", "Hoy / fecha de vencimiento", outstanding.Overdue, "financial/outstanding-balances"),
            Kpi.Money("reversed", "Pagos revertidos", "Importe de los pagos que se revirtieron en el período. Se informa aparte y no está en el total cobrado.", "Fecha de reversión", reversedNow.Amount, reversedBefore.Amount, "financial/reversals")
        };

        var total = methodsNow.Sum(m => m.Amount);
        var methods = methodsNow.OrderByDescending(m => m.Amount)
            .Select(m => new PaymentMethodShareDto(m.Method, m.Amount, m.Count, total > 0m ? Math.Round(m.Amount * 100m / total, 1, MidpointRounding.AwayFromZero) : null))
            .ToList();
        return new FinancialDashboardDto(kpis, methods);
    }

    private async Task<MembersDashboardDto> MembersAsync(AnalyticsScope scope, PeriodPair periods, int expiringDays, CancellationToken ct)
    {
        var branches = scope.BranchIds;
        var zone = scope.TimeZoneId;
        var activeNow = await Metrics.ActiveMembersAsync(db, branches, periods.Current.To, zone, ct);
        var activeBefore = await Metrics.ActiveMembersAsync(db, branches, periods.Previous.To, zone, ct);
        var newNow = await Metrics.NewMembersAsync(db, branches, periods.Current, ct);
        var newBefore = await Metrics.NewMembersAsync(db, branches, periods.Previous, ct);
        var flowNow = await Metrics.MembershipFlowAsync(db, branches, periods.Current, ct);
        var flowBefore = await Metrics.MembershipFlowAsync(db, branches, periods.Previous, ct);
        var expiredNow = await Metrics.ExpiredInPeriodAsync(db, branches, periods.Current, scope.Today, ct);
        var expiredBefore = await Metrics.ExpiredInPeriodAsync(db, branches, periods.Previous, scope.Today, ct);
        var expiringSoon = await Metrics.ExpiringSoonAsync(db, branches, scope.Today, expiringDays, ct);

        return new MembersDashboardDto(
        [
            Kpi.Count("active_members", "Alumnos activos", "Alumnos con ficha activa y una membresía vigente el último día del período.", "Último día del período", activeNow, activeBefore, "members/summary"),
            Kpi.Count("new_members", "Alumnos nuevos", "Fichas de alumno creadas en el período.", "Fecha de alta del alumno", newNow, newBefore, "members/acquisitions"),
            Kpi.Count("new_enrollments", "Nuevas inscripciones", "Membresías vendidas en el período al primer contrato de un alumno.", "Fecha de venta de la membresía", flowNow.NewEnrollments, flowBefore.NewEnrollments, "members/acquisitions"),
            Kpi.Count("renewals", "Membresías renovadas", "Membresías vendidas en el período como renovación de otra.", "Fecha de venta de la membresía", flowNow.Renewals, flowBefore.Renewals, "members/renewals"),
            Kpi.Count("reincorporations", "Reincorporaciones", "Membresías vendidas en el período a alumnos que ya tuvieron otras pero no como renovación.", "Fecha de venta de la membresía", flowNow.Reincorporations, flowBefore.Reincorporations, "members/acquisitions"),
            Kpi.Count("expired", "Membresías vencidas", "Membresías activas cuyo fin cayó dentro del período y ya pasó (no incluye canceladas).", "Fecha de fin de la membresía", expiredNow, expiredBefore, "members/expirations"),
            Kpi.SnapshotCount("expiring_soon", "Próximas a vencer", $"Membresías activas que terminan en los próximos {expiringDays} días.", "Hoy / fecha de fin", expiringSoon, "members/expirations")
        ]);
    }

    private async Task<OperationsDashboardDto> OperationsAsync(AnalyticsScope scope, PeriodPair periods, CancellationToken ct)
    {
        var branches = scope.BranchIds;
        var now = clock.UtcNow;
        var visitsNow = await Metrics.GymVisitsAsync(db, branches, periods.Current, ct);
        var visitsBefore = await Metrics.GymVisitsAsync(db, branches, periods.Previous, ct);
        var sessionsNow = await Metrics.SessionCountsAsync(db, branches, periods.Current, now, null, ct);
        var sessionsBefore = await Metrics.SessionCountsAsync(db, branches, periods.Previous, now, null, ct);
        var occupancyNow = await Metrics.OccupancyAsync(db, branches, periods.Current, null, ct);
        var occupancyBefore = await Metrics.OccupancyAsync(db, branches, periods.Previous, null, ct);
        var reservationsNow = await Metrics.ReservationCountsAsync(db, branches, periods.Current, null, ct);
        var reservationsBefore = await Metrics.ReservationCountsAsync(db, branches, periods.Previous, null, ct);
        var attendanceNow = await Metrics.ClassAttendanceAsync(db, branches, periods.Current, null, ct);
        var attendanceBefore = await Metrics.ClassAttendanceAsync(db, branches, periods.Previous, null, ct);

        return new OperationsDashboardDto(
        [
            Kpi.Count("gym_visits", "Asistencia al gimnasio", "Entradas registradas al gimnasio en el período.", "Fecha y hora de entrada", visitsNow, visitsBefore, "attendance"),
            Kpi.Count("sessions_held", "Clases realizadas", "Sesiones cerradas como realizadas. Las ya pasadas que nadie cerró se informan aparte como pendientes de cierre.", "Inicio de la sesión", sessionsNow.Held, sessionsBefore.Held, "classes/occupancy"),
            Kpi.Count("sessions_cancelled", "Clases canceladas", "Sesiones canceladas cuyo inicio caía en el período.", "Inicio de la sesión", sessionsNow.Cancelled, sessionsBefore.Cancelled, "classes/cancellations"),
            Kpi.Ratio("class_occupancy", "Ocupación de clases", "Reservas confirmadas ÷ capacidad de las sesiones no canceladas (ponderada). No cuenta lista de espera, cupos ofrecidos ni reservas canceladas.", "Inicio de la sesión", occupancyNow.Percent, occupancyBefore.Percent, "classes/occupancy"),
            Kpi.Count("reservations_confirmed", "Reservas confirmadas", "Reservas confirmadas de las sesiones del período.", "Inicio de la sesión", reservationsNow.Confirmed, reservationsBefore.Confirmed, "classes/occupancy"),
            Kpi.Count("reservations_cancelled", "Reservas canceladas", "Reservas canceladas por el alumno o el personal en sesiones del período (no incluye las canceladas por cancelar la sesión).", "Inicio de la sesión", reservationsNow.Cancelled, reservationsBefore.Cancelled, "classes/cancellations"),
            Kpi.Count("class_no_shows", "Inasistencias a clases", "Reservas confirmadas marcadas como «no asistió».", "Inicio de la sesión", attendanceNow.NoShow, attendanceBefore.NoShow, "classes/occupancy")
        ],
        new SessionBreakdownDto(sessionsNow.Held, sessionsNow.Cancelled, sessionsNow.Upcoming, sessionsNow.PendingClosure));
    }

    private async Task<InstructorDashboardDto> InstructorAsync(AnalyticsScope scope, PeriodPair periods, Guid userId, CancellationToken ct)
    {
        var branches = scope.BranchIds;
        var now = clock.UtcNow;
        var sessionsNow = await Metrics.SessionCountsAsync(db, branches, periods.Current, now, userId, ct);
        var sessionsBefore = await Metrics.SessionCountsAsync(db, branches, periods.Previous, now, userId, ct);
        var occupancyNow = await Metrics.OccupancyAsync(db, branches, periods.Current, userId, ct);
        var occupancyBefore = await Metrics.OccupancyAsync(db, branches, periods.Previous, userId, ct);
        var attendanceNow = await Metrics.ClassAttendanceAsync(db, branches, periods.Current, userId, ct);
        var attendanceBefore = await Metrics.ClassAttendanceAsync(db, branches, periods.Previous, userId, ct);

        return new InstructorDashboardDto(
        [
            Kpi.Count("my_sessions_held", "Mis clases realizadas", "Sus sesiones cerradas como realizadas.", "Inicio de la sesión", sessionsNow.Held, sessionsBefore.Held),
            Kpi.Count("my_sessions_cancelled", "Mis clases canceladas", "Sus sesiones canceladas cuyo inicio caía en el período.", "Inicio de la sesión", sessionsNow.Cancelled, sessionsBefore.Cancelled),
            Kpi.Ratio("my_occupancy", "Ocupación de mis clases", "Reservas confirmadas ÷ capacidad de sus sesiones no canceladas.", "Inicio de la sesión", occupancyNow.Percent, occupancyBefore.Percent),
            Kpi.Count("my_attended", "Asistencias a mis clases", "Participantes que asistieron a sus clases.", "Inicio de la sesión", attendanceNow.Attended, attendanceBefore.Attended),
            Kpi.Count("my_no_shows", "Inasistencias a mis clases", "Participantes confirmados que no asistieron a sus clases.", "Inicio de la sesión", attendanceNow.NoShow, attendanceBefore.NoShow)
        ],
        new SessionBreakdownDto(sessionsNow.Held, sessionsNow.Cancelled, sessionsNow.Upcoming, sessionsNow.PendingClosure));
    }
}
