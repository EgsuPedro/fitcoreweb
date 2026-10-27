using FitCore.Commercial.Domain;

namespace FitCore.Commercial.UnitTests;

public class AttendanceDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 3, 10);

    private static ObligationStanding Standing(decimal payable, decimal paid, int dueOffsetDays) =>
        new(payable, paid, Today.AddDays(dueOffsetDays));

    [Fact]
    public void Exigir_pago_completo_rechaza_cualquier_saldo_aunque_no_haya_vencido()
    {
        var policy = AccessPolicy.RequireFullPayment;

        Assert.Null(AccessRules.DebtDenial(policy, 50, Today, [Standing(100, 100, -30)]));
        Assert.Equal(AccessRules.DebtPending, AccessRules.DebtDenial(policy, 50, Today, [Standing(100, 99.99m, 30)]));
        Assert.Null(AccessRules.DebtDenial(policy, 50, Today, []));
    }

    [Theory]
    [InlineData(100, 0, 0, null)] // debe todo pero aún no vence
    [InlineData(100, 50, 5, null)]
    [InlineData(100, 50, 0, null)] // vence hoy: todavía entra
    [InlineData(100, 50, -1, AccessRules.DebtOverdue)] // venció ayer
    [InlineData(100, 100, -30, null)] // venció pero está saldada
    public void Permitir_hasta_el_vencimiento_corta_el_acceso_solo_con_saldo_vencido(double payable, double paid, int dueOffset, string? expected) =>
        Assert.Equal(expected, AccessRules.DebtDenial(AccessPolicy.AllowUntilOverdue, 50, Today, [Standing((decimal)payable, (decimal)paid, dueOffset)]));

    [Fact]
    public void Basta_una_obligacion_vencida_entre_varias_para_cortar_el_acceso()
    {
        var obligations = new[] { Standing(100, 100, -10), Standing(20, 0, -1), Standing(50, 0, 10) };

        Assert.Equal(AccessRules.DebtOverdue, AccessRules.DebtDenial(AccessPolicy.AllowUntilOverdue, 50, Today, obligations));
    }

    [Theory]
    [InlineData(100, 59.99, AccessRules.MinimumPaymentNotMet)]
    [InlineData(100, 60, null)]
    [InlineData(100, 100, null)]
    [InlineData(0, 0, null)] // nada exigible: nada que pagar
    public void Con_pago_minimo_hay_que_alcanzar_el_porcentaje_del_total_exigible(double payable, double paid, string? expected) =>
        Assert.Equal(expected, AccessRules.DebtDenial(AccessPolicy.AllowWithMinimumPayment, 60, Today, [Standing((decimal)payable, (decimal)paid, -100)]));

    [Fact]
    public void El_pago_minimo_suma_todas_las_obligaciones_y_un_sobrepago_no_cubre_otra()
    {
        // 60 de 100 y 0 de 100: 30 % del total, aunque una esté al 60 %.
        Assert.Equal(
            AccessRules.MinimumPaymentNotMet,
            AccessRules.DebtDenial(AccessPolicy.AllowWithMinimumPayment, 60, Today, [Standing(100, 60, 0), Standing(100, 0, 0)]));
        Assert.Null(AccessRules.DebtDenial(AccessPolicy.AllowWithMinimumPayment, 50, Today, [Standing(100, 100, 0), Standing(100, 0, 0)]));
    }

    [Fact]
    public void El_saldo_adeudado_nunca_es_negativo()
    {
        Assert.Equal(0m, Standing(100, 150, 0).Owed);
        Assert.Equal(25m, Standing(100, 75, 0).Owed);
    }

    [Fact]
    public void El_token_QR_es_aleatorio_no_lleva_datos_y_su_huella_es_estable()
    {
        var tokens = Enumerable.Range(0, 200).Select(_ => QrTokens.Generate()).ToList();

        Assert.Equal(200, tokens.Distinct().Count());
        Assert.All(tokens, t =>
        {
            Assert.True(QrTokens.LooksValid(t));
            Assert.Equal(47, t.Length);
            Assert.Matches("^fc1_[A-Za-z0-9_-]{43}$", t);
        });

        var token = tokens[0];
        Assert.Equal(QrTokens.Hash(token), QrTokens.Hash("  " + token + " "));
        Assert.Equal(64, QrTokens.Hash(token).Length);
        Assert.NotEqual(QrTokens.Hash(token), QrTokens.Hash(tokens[1]));
        Assert.DoesNotContain(token, QrTokens.Hash(token), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("fc1_corto")]
    [InlineData("xx1_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("A-000001")]
    public void Un_texto_con_forma_incorrecta_no_parece_un_token(string text) => Assert.False(QrTokens.LooksValid(text));

    [Fact]
    public void Una_entrada_nueva_esta_abierta_y_marca_la_excepcion_solo_con_motivo()
    {
        var normal = AttendanceEntry.CheckIn(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), AttendanceMethod.Manual, null, Guid.NewGuid(), Now);
        var exception = AttendanceEntry.CheckIn(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, AttendanceMethod.Manual, "  Autorizado por la gerencia  ", Guid.NewGuid(), Now);

        Assert.True(normal.IsOpen);
        Assert.False(normal.IsException);
        Assert.Null(normal.ExceptionReason);
        Assert.Equal(Now, normal.CheckInAt);
        Assert.True(exception.IsException);
        Assert.Equal("Autorizado por la gerencia", exception.ExceptionReason);
        Assert.Null(exception.MembershipId);
    }

    [Fact]
    public void La_salida_cierra_la_entrada_una_sola_vez()
    {
        var entry = AttendanceEntry.CheckIn(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, AttendanceMethod.Qr, null, Guid.NewGuid(), Now);
        var actor = Guid.NewGuid();

        Assert.True(entry.CheckOut(AttendanceMethod.Manual, actor, Now.AddMinutes(75)).IsSuccess);

        Assert.False(entry.IsOpen);
        Assert.Equal(Now.AddMinutes(75), entry.CheckOutAt);
        Assert.Equal(AttendanceMethod.Manual, entry.CheckOutMethod);
        Assert.Equal(actor, entry.CheckOutBy);
        Assert.Equal("attendance.no_open_entry", entry.CheckOut(AttendanceMethod.Manual, actor, Now.AddMinutes(80)).Error.Code);
        Assert.Equal(Now.AddMinutes(75), entry.CheckOutAt);
    }
}
