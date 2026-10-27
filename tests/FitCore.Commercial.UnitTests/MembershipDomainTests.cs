using FitCore.Commercial.Domain;

namespace FitCore.Commercial.UnitTests;

public class MembershipDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Start = new(2026, 3, 1);
    private static readonly DateOnly End = new(2026, 3, 31);

    private static Membership NewMembership(bool allowFreeze = true, int maxFreezeDays = 15, decimal price = 34.50m, decimal discount = 0m) =>
        Membership.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            new MembershipTerms(Guid.NewGuid(), "Mensual", price, 1, DurationUnit.Month, TaxKind.General, 15m, null, null, allowFreeze, maxFreezeDays, null, null, null, discount),
            Start,
            End,
            null,
            null,
            Now);

    private static Membership NewActive(bool allowFreeze = true, int maxFreezeDays = 15)
    {
        var membership = NewMembership(allowFreeze, maxFreezeDays);
        membership.Activate(null, Now);
        return membership;
    }

    [Fact]
    public void Nace_pendiente_de_pago_con_el_precio_acordado_como_precio_menos_descuento()
    {
        var membership = NewMembership(price: 50m, discount: 10m);

        Assert.Equal(MembershipStatus.PendingPayment, membership.Status);
        Assert.Equal(40m, membership.AgreedPrice);
        Assert.Equal(10m, membership.DiscountAmount);
    }

    [Fact]
    public void Activar_solo_cambia_una_membresia_pendiente_y_es_idempotente()
    {
        var membership = NewMembership();

        membership.Activate(null, Now);
        membership.Activate(null, Now.AddDays(1));

        Assert.Equal(MembershipStatus.Active, membership.Status);
        Assert.Equal(Now, membership.UpdatedAt);
    }

    [Theory]
    [InlineData(2026, 3, 1, MembershipStatus.Active)]
    [InlineData(2026, 3, 31, MembershipStatus.Active)] // el último día todavía rige
    [InlineData(2026, 4, 1, MembershipStatus.Expired)]
    public void El_vencimiento_se_deriva_de_la_fecha_de_fin(int y, int m, int d, MembershipStatus expected) =>
        Assert.Equal(expected, NewActive().EffectiveStatus(new DateOnly(y, m, d)));

    [Fact]
    public void Una_pendiente_pasada_su_fecha_tambien_esta_vencida_pero_una_congelada_o_cancelada_conserva_su_estado()
    {
        var late = new DateOnly(2026, 6, 1);

        Assert.Equal(MembershipStatus.Expired, NewMembership().EffectiveStatus(late));

        var frozen = NewActive();
        frozen.Freeze(Start, null, Now);
        Assert.Equal(MembershipStatus.Frozen, frozen.EffectiveStatus(late));

        var cancelled = NewMembership();
        cancelled.Cancel("motivo", Start, null, Now);
        Assert.Equal(MembershipStatus.Cancelled, cancelled.EffectiveStatus(late));
    }

    [Fact]
    public void Congelar_exige_activa_plan_que_lo_permita_y_cupo_disponible()
    {
        Assert.Equal("memberships.not_active", NewMembership().Freeze(Start, null, Now).Error.Code);
        Assert.Equal("memberships.freeze_not_allowed", NewActive(allowFreeze: false, maxFreezeDays: 0).Freeze(Start, null, Now).Error.Code);

        var membership = NewActive(maxFreezeDays: 15);
        Assert.True(membership.Freeze(Start, null, Now).IsSuccess);
        Assert.Equal(MembershipStatus.Frozen, membership.Status);
        Assert.Equal(Start, membership.FreezeStartedOn);
        Assert.Equal("memberships.not_active", membership.Freeze(Start, null, Now).Error.Code);
    }

    [Theory]
    [InlineData(10, 10, 5)] // dentro del cupo
    [InlineData(15, 15, 0)] // exactamente el cupo
    [InlineData(40, 15, 0)] // se limita al cupo
    [InlineData(0, 0, 15)] // reactivar el mismo día no alarga nada
    public void Reactivar_alarga_el_fin_solo_por_los_dias_congelados_dentro_del_cupo(int frozenDays, int granted, int remaining)
    {
        var membership = NewActive(maxFreezeDays: 15);
        membership.Freeze(Start, null, Now);

        var result = membership.Reactivate(Start.AddDays(frozenDays), null, Now);

        Assert.True(result.IsSuccess);
        Assert.Equal(granted, result.Value);
        Assert.Equal(End.AddDays(granted), membership.EndDate);
        Assert.Equal(granted, membership.FrozenDaysUsed);
        Assert.Equal(remaining, membership.RemainingFreezeDays);
        Assert.Equal(MembershipStatus.Active, membership.Status);
        Assert.Null(membership.FreezeStartedOn);
    }

    [Fact]
    public void Reactivar_algo_que_no_esta_congelado_falla_y_los_dias_usados_se_acumulan()
    {
        var membership = NewActive(maxFreezeDays: 15);
        Assert.Equal("memberships.not_frozen", membership.Reactivate(Start, null, Now).Error.Code);

        membership.Freeze(Start, null, Now);
        membership.Reactivate(Start.AddDays(10), null, Now);
        membership.Freeze(Start.AddDays(10), null, Now);
        var second = membership.Reactivate(Start.AddDays(40), null, Now);

        Assert.Equal(5, second.Value);
        Assert.Equal(15, membership.FrozenDaysUsed);
        Assert.Equal("memberships.freeze_limit_reached", membership.Freeze(Start.AddDays(40), null, Now).Error.Code);
    }

    [Fact]
    public void Cancelar_guarda_el_motivo_y_no_se_puede_repetir_ni_hacer_con_una_vencida()
    {
        var membership = NewActive();

        Assert.True(membership.Cancel("  Se mudó  ", Start, Guid.Empty, Now).IsSuccess);
        Assert.Equal(MembershipStatus.Cancelled, membership.Status);
        Assert.Equal("Se mudó", membership.CancelReason);
        Assert.Equal(Now, membership.CancelledAt);
        Assert.Equal("memberships.already_cancelled", membership.Cancel("otra vez", Start, null, Now).Error.Code);

        var expired = NewActive();
        Assert.Equal("memberships.expired", expired.Cancel("tarde", new DateOnly(2026, 6, 1), null, Now).Error.Code);
    }

    [Fact]
    public void La_obligacion_desglosa_el_IVA_congela_la_tarifa_y_se_vence_solo_si_tiene_saldo_pendiente()
    {
        var obligation = PaymentObligation.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, "OB-000001", "Mensual",
            new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 8), 50m, 10m, TaxKind.General, 15m, null, Now);

        Assert.Equal(40m, obligation.PayableAmount);
        Assert.Equal(34.78m, obligation.Subtotal);
        Assert.Equal(5.22m, obligation.TaxAmount);
        Assert.Equal(15m, obligation.TaxPercent);
        Assert.Equal(ObligationStatus.Pending, obligation.EffectiveStatus(new DateOnly(2026, 3, 8)));
        Assert.Equal(ObligationStatus.Overdue, obligation.EffectiveStatus(new DateOnly(2026, 3, 9)));

        Assert.True(obligation.Void("Cancelada", null, Now).IsSuccess);
        Assert.Equal(ObligationStatus.Voided, obligation.EffectiveStatus(new DateOnly(2026, 12, 1)));
        Assert.Equal("obligations.already_voided", obligation.Void("otra vez", null, Now).Error.Code);
    }
}
