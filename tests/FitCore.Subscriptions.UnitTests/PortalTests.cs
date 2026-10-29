using FitCore.Subscriptions.Domain;

namespace FitCore.Subscriptions.UnitTests;

public class PortalTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Subscription = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 15, 0, 0, TimeSpan.Zero);

    private static SubscriptionRequest Pending(SubscriptionRequestKind kind = SubscriptionRequestKind.Reactivation, Guid? plan = null, string? message = null) =>
        SubscriptionRequest.Create(Tenant, Subscription, kind, plan, message, Guid.NewGuid(), Now).Value;

    [Fact]
    public void Un_cambio_de_plan_exige_el_plan_y_las_demas_solicitudes_no_lo_llevan()
    {
        Assert.Equal("subscription_requests.plan_required", SubscriptionRequest.Create(Tenant, Subscription, SubscriptionRequestKind.ChangePlan, null, null, null, Now).Error.Code);
        Assert.Equal("subscription_requests.plan_not_allowed", SubscriptionRequest.Create(Tenant, Subscription, SubscriptionRequestKind.Reactivation, Guid.NewGuid(), null, null, Now).Error.Code);
        Assert.True(SubscriptionRequest.Create(Tenant, Subscription, SubscriptionRequestKind.ChangePlan, Guid.NewGuid(), null, null, Now).IsSuccess);
    }

    [Fact]
    public void La_cancelacion_exige_motivo_y_ningun_mensaje_admite_tarjetas_ni_pasa_de_500()
    {
        Assert.Equal("subscription_requests.message_required", SubscriptionRequest.Create(Tenant, Subscription, SubscriptionRequestKind.Cancellation, null, "  ", null, Now).Error.Code);
        Assert.Equal("subscription_requests.message_too_long", SubscriptionRequest.Create(Tenant, Subscription, SubscriptionRequestKind.Reactivation, null, new string('x', 501), null, Now).Error.Code);
        Assert.Equal("subscription_requests.message_sensitive", SubscriptionRequest.Create(Tenant, Subscription, SubscriptionRequestKind.Reactivation, null, "pagué con 4111 1111 1111 1111", null, Now).Error.Code);
        Assert.True(SubscriptionRequest.Create(Tenant, Subscription, SubscriptionRequestKind.Cancellation, null, "Cerramos el local", null, Now).IsSuccess);
    }

    [Fact]
    public void Una_solicitud_se_resuelve_una_sola_vez()
    {
        var request = Pending();
        Assert.Equal(SubscriptionRequestStatus.Pending, request.Status);
        Assert.Null(request.ResolvedAt);

        Assert.True(request.Approve("Listo", Guid.NewGuid(), Now.AddHours(1)).IsSuccess);
        Assert.Equal(SubscriptionRequestStatus.Approved, request.Status);
        Assert.Equal(Now.AddHours(1), request.ResolvedAt);

        Assert.Equal("subscription_requests.already_resolved", request.Reject("tarde", null, Now).Error.Code);
        Assert.Equal("subscription_requests.already_resolved", request.Withdraw(null, Now).Error.Code);
        Assert.Equal("subscription_requests.already_resolved", request.Approve(null, null, Now).Error.Code);
    }

    [Fact]
    public void Rechazar_exige_una_razon_y_la_nota_no_admite_datos_de_pago()
    {
        var request = Pending();
        Assert.Equal("subscription_requests.note_required", request.Reject(" ", null, Now).Error.Code);
        Assert.Equal(SubscriptionRequestStatus.Pending, request.Status);
        Assert.Equal("subscription_requests.note_sensitive", request.Approve("tarjeta 4111111111111111", null, Now).Error.Code);
        Assert.Equal(SubscriptionRequestStatus.Pending, request.Status);

        Assert.True(request.Reject("Primero regularice la factura", null, Now).IsSuccess);
        Assert.Equal("Primero regularice la factura", request.ResolutionNote);
    }

    [Fact]
    public void Retirar_una_solicitud_la_cierra_sin_nota()
    {
        var request = Pending();
        Assert.True(request.Withdraw(Guid.NewGuid(), Now).IsSuccess);
        Assert.Equal(SubscriptionRequestStatus.Withdrawn, request.Status);
        Assert.Null(request.ResolutionNote);
    }

    [Theory]
    [InlineData(SubscriptionEventType.Created)]
    [InlineData(SubscriptionEventType.InvoiceVoided)]
    public void Los_hechos_internos_no_generan_aviso(SubscriptionEventType type) => Assert.Null(NoticeTypes.For(type));

    [Fact]
    public void Todo_otro_hecho_del_historial_tiene_su_aviso_y_la_suspension_y_el_vencimiento_son_criticos()
    {
        var avisables = Enum.GetValues<SubscriptionEventType>().Where(t => t is not (SubscriptionEventType.Created or SubscriptionEventType.InvoiceVoided));
        Assert.All(avisables, t => Assert.NotNull(NoticeTypes.For(t)));
        Assert.Equal(NoticeSeverity.Critical, NoticeTypes.For(SubscriptionEventType.Suspended)!.Value.Severity);
        Assert.Equal(NoticeSeverity.Critical, NoticeTypes.For(SubscriptionEventType.Expired)!.Value.Severity);
        Assert.Equal(NoticeSeverity.Warning, NoticeTypes.For(SubscriptionEventType.PaymentFailed)!.Value.Severity);
    }

    [Fact]
    public void Marcar_un_aviso_leido_conserva_la_primera_fecha()
    {
        var notice = new SubscriptionNotice { Id = Guid.NewGuid(), TenantId = Tenant, Type = NoticeTypes.DueSoon, DedupeKey = "k", CreatedAt = Now };
        Assert.Null(notice.ReadAt);
        notice.MarkRead(Now.AddMinutes(5));
        notice.MarkRead(Now.AddDays(1));
        Assert.Equal(Now.AddMinutes(5), notice.ReadAt);
    }
}
