using FitCore.Commercial.Domain;

namespace FitCore.Commercial.UnitTests;

public class CashDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private static CashRegister Register(bool allowMultiple = false) =>
        CashRegister.Create(Guid.NewGuid(), Guid.NewGuid(), "  Caja 1  ", allowMultiple, null, Now);

    private static CashSession OpenSession(decimal opening = 50m, bool allowMultiple = false) =>
        CashSession.Open(Guid.NewGuid(), Register(allowMultiple), opening, " Apertura ", Guid.NewGuid(), Now);

    [Theory]
    [InlineData(50, 30, 0, 80)]
    [InlineData(50, 30, 15.5, 64.5)]
    [InlineData(0, 0, 0, 0)]
    [InlineData(100, 0, 100, 0)]
    public void El_saldo_esperado_es_apertura_mas_ingresos_menos_egresos(double opening, double totalIn, double totalOut, double expected) =>
        Assert.Equal((decimal)expected, CashMath.Expected((decimal)opening, (decimal)totalIn, (decimal)totalOut));

    [Fact]
    public void Una_caja_nueva_esta_activa_es_exclusiva_y_limpia_su_nombre()
    {
        var register = Register();

        Assert.True(register.IsActive);
        Assert.False(register.AllowMultipleOpenSessions);
        Assert.Equal("Caja 1", register.Name);
    }

    [Fact]
    public void La_sesion_copia_si_la_caja_es_exclusiva_y_redondea_la_apertura()
    {
        var exclusive = OpenSession(10.005m);
        var shared = OpenSession(10m, allowMultiple: true);

        Assert.True(exclusive.Exclusive);
        Assert.False(shared.Exclusive);
        Assert.Equal(10.01m, exclusive.OpeningAmount);
        Assert.Equal(CashSessionStatus.Open, exclusive.Status);
        Assert.Equal("Apertura", exclusive.OpenNotes);
    }

    [Fact]
    public void Cerrar_sin_diferencia_no_exige_observacion_y_guarda_el_esperado()
    {
        var session = OpenSession();
        var closer = Guid.NewGuid();

        Assert.True(session.Close(80m, 80m, null, closer, Now.AddHours(8)).IsSuccess);

        Assert.Equal(CashSessionStatus.Closed, session.Status);
        Assert.Equal(80m, session.CountedAmount);
        Assert.Equal(80m, session.ExpectedAtClose);
        Assert.Equal(0m, session.Difference);
        Assert.Equal(closer, session.ClosedBy);
        Assert.Equal(Now.AddHours(8), session.ClosedAt);
        Assert.Null(session.CloseNotes);
    }

    [Theory]
    [InlineData(75, 80, -5)] // faltante
    [InlineData(82.5, 80, 2.5)] // sobrante
    public void Una_diferencia_exige_observacion_y_se_guarda_con_su_signo(double counted, double expected, double difference)
    {
        var withoutNotes = OpenSession();
        var error = withoutNotes.Close((decimal)counted, (decimal)expected, "   ", Guid.NewGuid(), Now);
        Assert.Equal("cash.difference_requires_notes", error.Error.Code);
        Assert.Equal(CashSessionStatus.Open, withoutNotes.Status);

        var withNotes = OpenSession();
        Assert.True(withNotes.Close((decimal)counted, (decimal)expected, "  Revisado con el supervisor  ", Guid.NewGuid(), Now).IsSuccess);
        Assert.Equal((decimal)difference, withNotes.Difference);
        Assert.Equal("Revisado con el supervisor", withNotes.CloseNotes);
    }

    [Fact]
    public void Una_sesion_cerrada_no_se_cierra_otra_vez()
    {
        var session = OpenSession();
        session.Close(50m, 50m, null, Guid.NewGuid(), Now);

        var again = session.Close(50m, 50m, null, Guid.NewGuid(), Now);

        Assert.Equal("cash.session_closed", again.Error.Code);
    }

    [Theory]
    [InlineData(CashMovementType.CashPayment, CashDirection.In)]
    [InlineData(CashMovementType.ManualIn, CashDirection.In)]
    [InlineData(CashMovementType.PaymentReversal, CashDirection.Out)]
    [InlineData(CashMovementType.ManualOut, CashDirection.Out)]
    public void La_direccion_de_un_movimiento_sale_de_su_tipo(CashMovementType type, CashDirection expected)
    {
        var movement = CashMovement.Create(Guid.NewGuid(), OpenSession(), type, 10.005m, "  motivo  ", null, null, Now);

        Assert.Equal(expected, movement.Direction);
        Assert.Equal(10.01m, movement.Amount);
        Assert.Equal("motivo", movement.Reason);
        Assert.Equal(movement.SessionId, movement.SessionId);
    }

    [Fact]
    public void El_movimiento_hereda_la_sucursal_de_su_sesion()
    {
        var session = OpenSession();

        var movement = CashMovement.Create(Guid.NewGuid(), session, CashMovementType.ManualIn, 5m, "Fondo", null, null, Now);

        Assert.Equal(session.BranchId, movement.BranchId);
        Assert.Equal(session.Id, movement.SessionId);
    }
}
