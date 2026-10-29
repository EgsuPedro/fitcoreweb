using FitCore.Subscriptions.Domain;

namespace FitCore.Subscriptions.UnitTests;

public class BillingTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 15, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 29);

    private static PlanSnapshot Plan(decimal price = 40m, BillingPeriod period = BillingPeriod.Monthly) =>
        new(Guid.NewGuid(), Guid.NewGuid(), 1, "pro", "Pro", "USD", period, price, 14, null);

    private static Subscription Sub(decimal price = 40m) => Subscription.Start(Tenant, Plan(price), false, Today, null, Now, null).Value;

    private static SubscriptionInvoice Invoice(decimal price = 40m)
    {
        var sub = Sub(price);
        return SubscriptionInvoice.Issue("FC-2026-000001", sub, sub.ToSnapshot(), InvoiceKind.Renewal, sub.CurrentPeriodEnd, sub.CurrentPeriodEnd, null, Now).Value;
    }

    [Fact]
    public void Una_factura_nueva_copia_el_importe_la_moneda_y_el_plan_y_cubre_un_periodo()
    {
        var sub = Sub(49.90m);

        var invoice = SubscriptionInvoice.Issue("FC-2026-000007", sub, sub.ToSnapshot(), InvoiceKind.Renewal, sub.CurrentPeriodEnd, sub.CurrentPeriodEnd, null, Now).Value;

        Assert.Equal(InvoiceStatus.Pending, invoice.Status);
        Assert.Equal(49.90m, invoice.Amount);
        Assert.Equal("USD", invoice.Currency);
        Assert.Equal("pro", invoice.PlanCode);
        Assert.Equal(sub.CurrentPeriodEnd, invoice.PeriodStart);
        Assert.Equal(Subscription.AddPeriod(sub.CurrentPeriodEnd, BillingPeriod.Monthly), invoice.PeriodEnd);
        Assert.True(invoice.IsOpen);
        Assert.Contains("No es un comprobante tributario", SubscriptionInvoice.Notice);
    }

    [Fact]
    public void Un_plan_gratuito_no_genera_facturas()
    {
        var sub = Sub(0m);

        var result = SubscriptionInvoice.Issue("FC-1", sub, sub.ToSnapshot(), InvoiceKind.Renewal, Today, Today, null, Now);

        Assert.True(result.IsFailure);
        Assert.Equal("billing.free_plan", result.Error.Code);
    }

    [Fact]
    public void Pagar_cierra_la_factura_y_deja_el_periodo_realmente_aplicado()
    {
        var invoice = Invoice();
        var start = new DateOnly(2026, 12, 1);
        var end = new DateOnly(2027, 1, 1);

        Assert.True(invoice.MarkPaid(start, end, Now, null, Now).IsSuccess);

        Assert.Equal(InvoiceStatus.Paid, invoice.Status);
        Assert.Equal(start, invoice.PeriodStart);
        Assert.Equal(end, invoice.PeriodEnd);
        Assert.Equal(Now, invoice.PaidAt);
        Assert.False(invoice.IsOpen);
        Assert.Equal("billing.invoice_not_open", invoice.MarkPaid(start, end, Now, null, Now).Error.Code);
    }

    [Fact]
    public void Un_intento_fallido_deja_la_factura_abierta_y_se_puede_pagar_despues()
    {
        var invoice = Invoice();

        Assert.True(invoice.RecordFailedAttempt(null, Now).IsSuccess);
        Assert.True(invoice.RecordFailedAttempt(null, Now).IsSuccess);

        Assert.Equal(InvoiceStatus.Failed, invoice.Status);
        Assert.Equal(2, invoice.AttemptCount);
        Assert.True(invoice.IsOpen);
        Assert.True(invoice.MarkPaid(Today, Today.AddMonths(1), Now, null, Now).IsSuccess);
        Assert.Equal(2, invoice.AttemptCount);
    }

    [Fact]
    public void Anular_exige_motivo_y_solo_vale_para_facturas_abiertas()
    {
        var invoice = Invoice();
        Assert.Equal("billing.reason_required", invoice.Void(" ", null, Now).Error.Code);

        Assert.True(invoice.Void("Plan cambiado", null, Now).IsSuccess);
        Assert.Equal(InvoiceStatus.Void, invoice.Status);
        Assert.Equal("Plan cambiado", invoice.VoidReason);
        Assert.Equal("billing.invoice_not_open", invoice.Void("otra vez", null, Now).Error.Code);
        Assert.Equal("billing.invoice_not_open", invoice.MarkPaid(Today, Today.AddDays(30), Now, null, Now).Error.Code);
        Assert.Equal("billing.invoice_not_open", invoice.RecordFailedAttempt(null, Now).Error.Code);

        var paid = Invoice();
        paid.MarkPaid(Today, Today.AddDays(30), Now, null, Now);
        Assert.Equal("billing.invoice_not_open", paid.Void("tarde", null, Now).Error.Code);
    }

    [Fact]
    public void El_reembolso_parcial_y_el_total_cambian_el_estado_y_no_pueden_pasar_del_importe()
    {
        var invoice = Invoice(40m);
        Assert.Equal("billing.invoice_not_paid", invoice.ApplyRefund(10m, null, Now).Error.Code);
        invoice.MarkPaid(Today, Today.AddDays(30), Now, null, Now);

        Assert.True(invoice.ApplyRefund(15m, null, Now).IsSuccess);
        Assert.Equal(InvoiceStatus.PartiallyRefunded, invoice.Status);
        Assert.Equal(15m, invoice.RefundedAmount);

        Assert.Equal("billing.refund_amount_invalid", invoice.ApplyRefund(25.01m, null, Now).Error.Code);
        Assert.Equal("billing.refund_amount_invalid", invoice.ApplyRefund(0m, null, Now).Error.Code);
        Assert.Equal("billing.refund_amount_invalid", invoice.ApplyRefund(-5m, null, Now).Error.Code);

        Assert.True(invoice.ApplyRefund(25m, null, Now).IsSuccess);
        Assert.Equal(InvoiceStatus.Refunded, invoice.Status);
        Assert.Equal(40m, invoice.RefundedAmount);
        Assert.Equal("billing.invoice_not_paid", invoice.ApplyRefund(1m, null, Now).Error.Code);
    }

    [Fact]
    public void El_pago_copia_el_importe_y_la_moneda_de_la_factura_y_nace_sin_reembolsos()
    {
        var invoice = Invoice(40m);

        var payment = SubscriptionPayment.Confirm(invoice, SubscriptionPaymentMethod.BankTransfer, "  TRF-001 ", "clave-1", Now, null, " nota ", Now);

        Assert.Equal(invoice.Id, payment.InvoiceId);
        Assert.Equal(invoice.TenantId, payment.TenantId);
        Assert.Equal(40m, payment.Amount);
        Assert.Equal("USD", payment.Currency);
        Assert.Equal("TRF-001", payment.Reference);
        Assert.Equal("nota", payment.Notes);
        Assert.Equal(0m, payment.RefundedAmount);
        Assert.Null(payment.Provider);
    }

    [Theory]
    [InlineData("4111 1111 1111 1111")]
    [InlineData("4111-1111-1111-1111")]
    [InlineData("4111111111111111")]
    [InlineData("pagó con 5500005555555559 ayer")]
    [InlineData("378282246310005")]
    public void Un_texto_con_forma_de_numero_de_tarjeta_se_detecta(string text) =>
        Assert.True(CardDataGuard.LooksLikeCardNumber(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("TRF-20261029-0012")]
    [InlineData("000123456789")]
    [InlineData("1234567890123456")]
    [InlineData("Transferencia Banco Pichincha")]
    [InlineData("Cédula 1710034065 y teléfono 0991234567")]
    public void Las_referencias_normales_de_un_banco_no_se_confunden_con_una_tarjeta(string? text) =>
        Assert.False(CardDataGuard.LooksLikeCardNumber(text));
}
