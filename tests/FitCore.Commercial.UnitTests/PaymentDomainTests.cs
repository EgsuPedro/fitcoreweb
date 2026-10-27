using FitCore.Commercial.Domain;

namespace FitCore.Commercial.UnitTests;

public class PaymentDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private static Payment NewPayment(PaymentMethod method = PaymentMethod.Card, params (Guid, decimal)[] allocations) =>
        Payment.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "RC-000001",
            new PaymentDetails(method, null, "VCH-1", null, Now, null),
            allocations.Length == 0 ? [(Guid.NewGuid(), 10m)] : allocations,
            null,
            Now);

    [Theory]
    [InlineData(PaymentMethod.Card, PaymentStatus.Confirmed)]
    [InlineData(PaymentMethod.Other, PaymentStatus.Confirmed)]
    [InlineData(PaymentMethod.Cash, PaymentStatus.Confirmed)]
    [InlineData(PaymentMethod.BankTransfer, PaymentStatus.PendingVerification)]
    public void Las_transferencias_esperan_verificacion_y_los_demas_medios_se_confirman_al_registrarse(PaymentMethod method, PaymentStatus expected) =>
        Assert.Equal(expected, NewPayment(method).Status);

    [Fact]
    public void El_importe_del_pago_es_siempre_la_suma_de_sus_asignaciones_redondeadas()
    {
        var payment = NewPayment(PaymentMethod.Card, (Guid.NewGuid(), 10.005m), (Guid.NewGuid(), 4.50m));

        Assert.Equal(14.51m, payment.Amount);
        Assert.Equal(payment.Amount, payment.Allocations.Sum(a => a.Amount));
        Assert.Equal(2, payment.Allocations.Count);
    }

    [Fact]
    public void Un_pago_que_se_confirma_al_registrarse_deja_constancia_de_quien_y_cuando()
    {
        var actor = Guid.NewGuid();
        var payment = Payment.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "RC-1", new PaymentDetails(PaymentMethod.Card, null, "V", null, Now, null), [(Guid.NewGuid(), 5m)], actor, Now);

        Assert.Equal(Now, payment.ConfirmedAt);
        Assert.Equal(actor, payment.ConfirmedBy);
    }

    [Fact]
    public void Confirmar_solo_aplica_a_una_transferencia_pendiente()
    {
        var transfer = NewPayment(PaymentMethod.BankTransfer);
        Assert.True(transfer.Confirm(Guid.NewGuid(), Now).IsSuccess);
        Assert.Equal(PaymentStatus.Confirmed, transfer.Status);
        Assert.Equal("payments.not_pending", transfer.Confirm(null, Now).Error.Code);
        Assert.Equal("payments.not_pending", NewPayment(PaymentMethod.Card).Confirm(null, Now).Error.Code);
    }

    [Fact]
    public void Revertir_cambia_el_estado_guarda_el_motivo_y_no_se_repite()
    {
        var payment = NewPayment();
        var actor = Guid.NewGuid();

        Assert.True(payment.Reverse("  Error de monto  ", actor, Now).IsSuccess);

        Assert.Equal(PaymentStatus.Reversed, payment.Status);
        Assert.Equal("Error de monto", payment.ReversalReason);
        Assert.Equal(actor, payment.ReversedBy);
        Assert.Equal(Now, payment.ReversedAt);
        Assert.Equal(10m, payment.Amount); // el registro no se altera
        Assert.Equal("payments.already_reversed", payment.Reverse("otra vez", null, Now).Error.Code);
    }

    [Theory]
    [InlineData(AccessPolicy.RequireFullPayment, 0, 100, 50, false)]
    [InlineData(AccessPolicy.RequireFullPayment, 99.99, 100, 50, false)]
    [InlineData(AccessPolicy.RequireFullPayment, 100, 100, 50, true)]
    [InlineData(AccessPolicy.AllowUntilOverdue, 0, 100, 50, false)]
    [InlineData(AccessPolicy.AllowUntilOverdue, 0.01, 100, 50, true)]
    [InlineData(AccessPolicy.AllowWithMinimumPayment, 49.99, 100, 50, false)]
    [InlineData(AccessPolicy.AllowWithMinimumPayment, 50, 100, 50, true)]
    [InlineData(AccessPolicy.AllowWithMinimumPayment, 0, 100, 50, false)]
    public void La_activacion_sigue_la_politica_del_gimnasio(AccessPolicy policy, double paid, double payable, int minPercent, bool expected) =>
        Assert.Equal(expected, ActivationRules.IsSatisfied(policy, (decimal)paid, (decimal)payable, minPercent));

    [Theory]
    [InlineData(0, ObligationStatus.Pending)]
    [InlineData(0.01, ObligationStatus.PartiallyPaid)]
    [InlineData(34.49, ObligationStatus.PartiallyPaid)]
    [InlineData(34.50, ObligationStatus.Paid)]
    public void El_estado_de_la_obligacion_sale_solo_de_lo_cobrado(double confirmed, ObligationStatus expected)
    {
        var obligation = PaymentObligation.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, "OB-1", "Mensual",
            new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 8), 34.50m, 0m, TaxKind.General, 15m, null, Now);

        obligation.ApplyConfirmedPayments((decimal)confirmed, null, Now);

        Assert.Equal(expected, obligation.Status);
    }

    [Fact]
    public void Una_obligacion_anulada_no_cambia_de_estado_por_pagos()
    {
        var obligation = PaymentObligation.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, "OB-1", "Mensual",
            new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 8), 34.50m, 0m, TaxKind.General, 15m, null, Now);
        obligation.Void("Prueba", null, Now);

        obligation.ApplyConfirmedPayments(34.50m, null, Now);

        Assert.Equal(ObligationStatus.Voided, obligation.Status);
    }
}
