using FitCore.Classes.Domain;

namespace FitCore.Classes.UnitTests;

public class ReservationDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 28);

    private static ClassReservation Confirmed() =>
        ClassReservation.Confirm(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ReservationSource.Member, Guid.NewGuid(), Now);

    [Fact]
    public void Una_reserva_nueva_queda_confirmada_viva_y_ocupando_cupo()
    {
        var reservation = Confirmed();

        Assert.Equal(ReservationStatus.Confirmed, reservation.Status);
        Assert.True(reservation.IsLive);
        Assert.True(reservation.HoldsSeat);
        Assert.Equal(Now, reservation.CreatedAt);
        Assert.Null(reservation.CancelledAt);
        Assert.False(reservation.LateCancellation);
    }

    [Fact]
    public void Cancelar_libera_el_cupo_y_guarda_motivo_quien_y_cuando()
    {
        var reservation = Confirmed();
        var by = Guid.NewGuid();

        reservation.Cancel("  Cambio de planes ", late: true, by, Now.AddHours(1));

        Assert.Equal(ReservationStatus.Cancelled, reservation.Status);
        Assert.False(reservation.IsLive);
        Assert.False(reservation.HoldsSeat);
        Assert.Equal("Cambio de planes", reservation.CancelReason);
        Assert.True(reservation.LateCancellation);
        Assert.Equal(by, reservation.CancelledBy);
        Assert.Equal(Now.AddHours(1), reservation.CancelledAt);
    }

    private static ClassReservation Waiting() =>
        ClassReservation.Waitlist(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ReservationSource.Staff, null, Now);

    [Fact]
    public void Quien_espera_esta_vivo_pero_no_ocupa_cupo()
    {
        var reservation = Waiting();

        Assert.Equal(ReservationStatus.Waitlisted, reservation.Status);
        Assert.True(reservation.IsLive);
        Assert.False(reservation.HoldsSeat);
        Assert.Null(reservation.OfferExpiresAt);
    }

    [Fact]
    public void Una_invitacion_ocupa_cupo_hasta_su_plazo_y_aceptarla_la_confirma()
    {
        var reservation = Waiting();
        var by = Guid.NewGuid();

        reservation.Offer(Now.AddMinutes(30), by, Now);

        Assert.Equal(ReservationStatus.Offered, reservation.Status);
        Assert.True(reservation.HoldsSeat);
        Assert.Equal(Now, reservation.OfferedAt);
        Assert.Equal(Now.AddMinutes(30), reservation.OfferExpiresAt);

        reservation.Accept(by, Now.AddMinutes(5));

        Assert.Equal(ReservationStatus.Confirmed, reservation.Status);
        Assert.True(reservation.HoldsSeat);
        Assert.Equal(Now, reservation.OfferedAt); // conserva de dónde viene
        Assert.Equal(Now.AddMinutes(5), reservation.UpdatedAt);
    }

    [Fact]
    public void Una_invitacion_vencida_libera_el_cupo_y_ya_no_esta_viva()
    {
        var reservation = Waiting();
        reservation.Offer(Now.AddMinutes(30), null, Now);

        reservation.Expire(null, Now.AddMinutes(31));

        Assert.Equal(ReservationStatus.Expired, reservation.Status);
        Assert.False(reservation.IsLive);
        Assert.False(reservation.HoldsSeat);
    }

    [Fact]
    public void Un_motivo_en_blanco_no_se_guarda()
    {
        var reservation = Confirmed();

        reservation.Cancel("   ", late: false, null, Now);

        Assert.Null(reservation.CancelReason);
    }

    [Theory]
    [InlineData(1990, 6, 15, 2026, 6, 14, 35)]
    [InlineData(1990, 6, 15, 2026, 6, 15, 36)]
    [InlineData(2008, 2, 29, 2026, 2, 27, 17)]
    [InlineData(2008, 2, 29, 2026, 2, 28, 18)] // en año no bisiesto, quien nació un 29 de febrero cumple el 28
    [InlineData(2008, 2, 29, 2026, 3, 1, 18)]
    public void La_edad_se_cumple_el_dia_del_cumpleanos(int by, int bm, int bd, int dy, int dm, int dd, int expected)
    {
        Assert.Equal(expected, EligibilityRules.AgeOn(new DateOnly(by, bm, bd), new DateOnly(dy, dm, dd)));
    }

    [Fact]
    public void La_ventana_de_reserva_se_abre_con_la_anticipacion_maxima_y_se_cierra_con_la_minima()
    {
        var start = Now.AddDays(10);

        Assert.Equal(ReservationErrors.BookingNotOpen, EligibilityRules.BookingWindow(start, 30, 7, Now));
        Assert.Null(EligibilityRules.BookingWindow(start, 30, 10, Now));
        Assert.Null(EligibilityRules.BookingWindow(start, 30, 14, Now));
        Assert.Null(EligibilityRules.BookingWindow(start, 30, 14, start.AddMinutes(-30)));
        Assert.Equal(ReservationErrors.BookingClosed, EligibilityRules.BookingWindow(start, 30, 14, start.AddMinutes(-29)));
        Assert.Null(EligibilityRules.BookingWindow(start, 0, 14, start.AddSeconds(-1)));
        Assert.Equal(ReservationErrors.BookingClosed, EligibilityRules.BookingWindow(start, 0, 14, start.AddSeconds(1)));
    }

    [Fact]
    public void El_plazo_de_cancelacion_es_hasta_los_minutos_indicados_antes_del_inicio()
    {
        var start = Now.AddHours(5);

        Assert.True(EligibilityRules.CancellationOnTime(start, 120, Now));
        Assert.True(EligibilityRules.CancellationOnTime(start, 120, start.AddMinutes(-120)));
        Assert.False(EligibilityRules.CancellationOnTime(start, 120, start.AddMinutes(-119)));
        Assert.True(EligibilityRules.CancellationOnTime(start, 0, start));
        Assert.False(EligibilityRules.CancellationOnTime(start, 0, start.AddSeconds(1)));
    }

    private static ObligationStanding Owing(decimal payable, decimal paid, int dueInDays) => new(payable, paid, Today.AddDays(dueInDays));

    [Fact]
    public void Sin_deuda_ninguna_politica_impide_reservar()
    {
        ObligationStanding[] settled = [Owing(30m, 30m, -5)];

        Assert.Null(EligibilityRules.DebtDenial(DebtPolicy.RequireFullPayment, 50, Today, settled));
        Assert.Null(EligibilityRules.DebtDenial(DebtPolicy.AllowUntilOverdue, 50, Today, settled));
        Assert.Null(EligibilityRules.DebtDenial(DebtPolicy.AllowWithMinimumPayment, 50, Today, settled));
        Assert.Null(EligibilityRules.DebtDenial(DebtPolicy.RequireFullPayment, 50, Today, []));
    }

    [Fact]
    public void La_politica_decide_con_saldo_pendiente_vigente_y_vencido()
    {
        ObligationStanding[] current = [Owing(30m, 10m, 3)];
        ObligationStanding[] overdue = [Owing(30m, 10m, -1)];

        Assert.Equal(ReservationErrors.DebtPending, EligibilityRules.DebtDenial(DebtPolicy.RequireFullPayment, 50, Today, current));
        Assert.Null(EligibilityRules.DebtDenial(DebtPolicy.AllowUntilOverdue, 50, Today, current));
        Assert.Equal(ReservationErrors.DebtOverdue, EligibilityRules.DebtDenial(DebtPolicy.AllowUntilOverdue, 50, Today, overdue));
        Assert.Equal(ReservationErrors.MinimumPaymentNotMet, EligibilityRules.DebtDenial(DebtPolicy.AllowWithMinimumPayment, 50, Today, current));
        Assert.Null(EligibilityRules.DebtDenial(DebtPolicy.AllowWithMinimumPayment, 30, Today, current));
    }

    [Fact]
    public void El_vencimiento_es_el_dia_siguiente_a_la_fecha_limite()
    {
        ObligationStanding[] dueToday = [Owing(30m, 0m, 0)];

        Assert.Null(EligibilityRules.DebtDenial(DebtPolicy.AllowUntilOverdue, 50, Today, dueToday));
    }
}
