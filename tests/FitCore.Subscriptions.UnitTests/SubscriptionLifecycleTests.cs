using FitCore.Subscriptions.Domain;

namespace FitCore.Subscriptions.UnitTests;

public class SubscriptionLifecycleTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 15, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 29);

    private static PlanSnapshot Plan(
        decimal price = 30m, BillingPeriod period = BillingPeriod.Monthly, string code = "pro", int version = 1, int trialDays = 14, Guid? planId = null) =>
        new(planId ?? Guid.NewGuid(), Guid.NewGuid(), version, code, code.ToUpperInvariant(), "USD", period, price, trialDays, null);

    private static Subscription Active(PlanSnapshot? plan = null, DateOnly? start = null) =>
        Subscription.Start(Tenant, plan ?? Plan(), false, start ?? Today, null, Now, null).Value;

    // ---- Renovación --------------------------------------------------------------------------------------------------

    [Fact]
    public void Renovar_empieza_donde_termino_el_periodo_anterior_sin_huecos_ni_solapes()
    {
        var s = Active(start: Today.AddDays(-25)); // vence el 2026-11-24
        var end = s.CurrentPeriodEnd;

        Assert.True(s.Renew(null, Today, 30, null, Now, "Pagó").IsSuccess);

        Assert.Equal(end, s.CurrentPeriodStart);
        Assert.Equal(Subscription.AddPeriod(end, BillingPeriod.Monthly), s.CurrentPeriodEnd);
        Assert.Equal(s.CurrentPeriodEnd, s.NextDueOn);
        Assert.Equal(SubscriptionStatus.Active, s.Status);
        Assert.Equal(SubscriptionEventType.Renewed, s.Events[^1].Type);
    }

    [Fact]
    public void Renovar_con_demasiada_anticipacion_se_rechaza()
    {
        var s = Active(start: Today); // vence en un mes: fuera de una ventana de 7 días

        var result = s.Renew(null, Today, 7, null, Now, null);

        Assert.True(result.IsFailure);
        Assert.Equal("subscriptions.renewal_too_early", result.Error.Code);
        Assert.Equal(Today, s.CurrentPeriodStart);
    }

    [Fact]
    public void Renovar_en_mora_cobra_desde_el_fin_del_periodo_y_levanta_la_mora()
    {
        var s = Active(start: Today.AddDays(-35)); // venció el 2026-10-25... (start + 1 mes)
        var end = s.CurrentPeriodEnd;
        s.MarkPastDue(7, null, Now);

        Assert.True(s.Renew(null, Today, 30, null, Now, null).IsSuccess);

        Assert.Equal(SubscriptionStatus.Active, s.Status);
        Assert.Equal(end, s.CurrentPeriodStart);
        Assert.Null(s.GraceEndsOn);
    }

    [Fact]
    public void Renovar_una_suscripcion_vencida_empieza_hoy_y_no_cobra_dias_sin_servicio()
    {
        var s = Active(start: Today.AddDays(-100));
        s.Expire(null, Now);

        Assert.True(s.Renew(null, Today, 30, null, Now, null).IsSuccess);

        Assert.Equal(Today, s.CurrentPeriodStart);
        Assert.Equal(Today.AddMonths(1), s.CurrentPeriodEnd);
        Assert.Equal(SubscriptionStatus.Active, s.Status);
    }

    [Fact]
    public void Si_el_periodo_siguiente_ya_paso_por_completo_tambien_empieza_hoy()
    {
        var s = Active(start: Today.AddDays(-90));
        s.MarkPastDue(120, null, Now); // gracia larguísima: sigue en mora aunque pasó más de un período

        Assert.True(s.Renew(null, Today, 30, null, Now, null).IsSuccess);

        Assert.Equal(Today, s.CurrentPeriodStart);
        Assert.True(s.CurrentPeriodEnd > Today);
    }

    [Theory]
    [InlineData(SubscriptionStatus.Trialing)]
    [InlineData(SubscriptionStatus.Suspended)]
    [InlineData(SubscriptionStatus.Cancelled)]
    public void No_se_renuevan_pruebas_suspendidas_ni_canceladas(SubscriptionStatus status)
    {
        var s = Subscription.Start(Tenant, Plan(), status == SubscriptionStatus.Trialing, Today, null, Now, null).Value;
        if (status == SubscriptionStatus.Suspended)
        {
            s.Suspend("x", null, Now);
        }

        if (status == SubscriptionStatus.Cancelled)
        {
            s.Cancel("x", null, Now);
        }

        var result = s.Renew(null, Today, 365, null, Now, null);

        Assert.Equal("subscriptions.invalid_transition", result.Error.Code);
    }

    [Fact]
    public void Con_una_cancelacion_programada_no_se_renueva_hasta_retirarla()
    {
        var s = Active(start: Today.AddDays(-25));
        s.ScheduleCancellation("Se va", null, Now);

        Assert.Equal("subscriptions.cancellation_scheduled", s.Renew(null, Today, 30, null, Now, null).Error.Code);

        s.ResumeCancellation(null, Now, null);
        Assert.True(s.Renew(null, Today, 30, null, Now, null).IsSuccess);
    }

    [Fact]
    public void La_baja_programada_entra_en_vigor_al_renovar_y_el_periodo_nuevo_ya_usa_el_precio_nuevo()
    {
        var s = Active(Plan(price: 50m, code: "pro"), Today.AddDays(-25));
        var cheaper = Plan(price: 20m, code: "basic");
        Assert.True(s.ChangePlan(cheaper, Today, null, Now, null).IsSuccess);

        Assert.True(s.Renew(cheaper, Today, 30, null, Now.AddDays(1), null).IsSuccess);

        Assert.Equal("basic", s.PlanCode);
        Assert.Equal(20m, s.Price);
        Assert.Null(s.PendingPlanVersionId);
        Assert.All(s.PlanChanges, c => Assert.Equal(PlanChangeStatus.Applied, c.Status));
        Assert.Contains(s.Events, e => e.Type == SubscriptionEventType.PlanChanged);
    }

    // ---- Cambio de plan ----------------------------------------------------------------------------------------------

    [Fact]
    public void Una_mejora_entra_de_inmediato_sin_cambiar_el_periodo_ni_cobrar_la_diferencia()
    {
        var s = Active(Plan(price: 20m, code: "basic"), Today.AddDays(-10));
        var start = s.CurrentPeriodStart;
        var end = s.CurrentPeriodEnd;

        var result = s.ChangePlan(Plan(price: 60m, code: "pro"), Today, Guid.NewGuid(), Now, "Crece");

        Assert.True(result.IsSuccess);
        Assert.Equal(PlanChangeKind.Upgrade, result.Value.Kind);
        Assert.Equal(PlanChangeStatus.Applied, result.Value.Status);
        Assert.Equal("pro", s.PlanCode);
        Assert.Equal(60m, s.Price);
        Assert.Equal(start, s.CurrentPeriodStart);
        Assert.Equal(end, s.CurrentPeriodEnd);
        Assert.Null(s.PendingPlanVersionId);
        Assert.Equal(SubscriptionEventType.PlanChanged, s.Events[^1].Type);
    }

    [Fact]
    public void Una_baja_se_programa_para_el_fin_del_periodo_y_conserva_lo_ya_pagado()
    {
        var s = Active(Plan(price: 60m, code: "pro"), Today.AddDays(-10));
        var target = Plan(price: 20m, code: "basic");

        var result = s.ChangePlan(target, Today, null, Now, null);

        Assert.True(result.IsSuccess);
        Assert.Equal(PlanChangeKind.Downgrade, result.Value.Kind);
        Assert.Equal(PlanChangeStatus.Scheduled, result.Value.Status);
        Assert.Equal(s.CurrentPeriodEnd, result.Value.EffectiveOn);
        Assert.Equal("pro", s.PlanCode);
        Assert.Equal(60m, s.Price);
        Assert.Equal(target.PlanVersionId, s.PendingPlanVersionId);
        Assert.Equal(SubscriptionEventType.PlanChangeScheduled, s.Events[^1].Type);
    }

    [Fact]
    public void En_prueba_el_cambio_de_plan_es_siempre_inmediato_aunque_sea_mas_barato()
    {
        var s = Subscription.Start(Tenant, Plan(price: 60m, code: "pro"), true, Today, null, Now, null).Value;

        var result = s.ChangePlan(Plan(price: 10m, code: "basic"), Today, null, Now, null);

        Assert.Equal(PlanChangeStatus.Applied, result.Value.Status);
        Assert.Equal("basic", s.PlanCode);
        Assert.Equal(SubscriptionStatus.Trialing, s.Status);
    }

    [Fact]
    public void Comparar_planes_de_periodicidad_distinta_usa_el_precio_por_mes()
    {
        // 120 al año = 10 al mes: es más barato que 15 al mes aunque «120 > 15».
        var s = Active(Plan(price: 15m, period: BillingPeriod.Monthly, code: "mensual"));

        var result = s.ChangePlan(Plan(price: 120m, period: BillingPeriod.Yearly, code: "anual"), Today, null, Now, null);

        Assert.Equal(PlanChangeKind.Downgrade, result.Value.Kind);
        Assert.Equal(Subscription.Classify(10m, 15m), PlanChangeKind.Upgrade);
        Assert.Equal(Subscription.Classify(15m, 15m), PlanChangeKind.Lateral);
    }

    [Fact]
    public void Un_cambio_lateral_entra_de_inmediato()
    {
        var s = Active(Plan(price: 30m, code: "a"));

        var result = s.ChangePlan(Plan(price: 30m, code: "b"), Today, null, Now, null);

        Assert.Equal(PlanChangeKind.Lateral, result.Value.Kind);
        Assert.Equal(PlanChangeStatus.Applied, result.Value.Status);
    }

    [Fact]
    public void Cambiar_al_mismo_plan_o_desde_un_estado_que_no_lo_admite_se_rechaza()
    {
        var plan = Plan(code: "pro");
        var s = Active(plan);
        Assert.Equal("subscriptions.same_plan", s.ChangePlan(plan, Today, null, Now, null).Error.Code);

        var suspended = Active();
        suspended.Suspend("x", null, Now);
        Assert.Equal("subscriptions.invalid_state", suspended.ChangePlan(Plan(price: 99m, code: "otro"), Today, null, Now, null).Error.Code);

        var expired = Active();
        expired.Expire(null, Now);
        Assert.Equal("subscriptions.invalid_state", expired.ChangePlan(Plan(price: 99m, code: "otro"), Today, null, Now, null).Error.Code);
    }

    [Fact]
    public void Un_cambio_programado_nuevo_reemplaza_al_anterior()
    {
        var s = Active(Plan(price: 60m, code: "pro"));
        var first = s.ChangePlan(Plan(price: 30m, code: "medio"), Today, null, Now, null).Value;
        var secondTarget = Plan(price: 10m, code: "minimo");

        var second = s.ChangePlan(secondTarget, Today, null, Now, null).Value;

        Assert.Equal(PlanChangeStatus.Superseded, first.Status);
        Assert.Equal(PlanChangeStatus.Scheduled, second.Status);
        Assert.Equal(secondTarget.PlanVersionId, s.PendingPlanVersionId);
        Assert.Single(s.PlanChanges, c => c.Status == PlanChangeStatus.Scheduled);
    }

    [Fact]
    public void Una_mejora_posterior_cancela_la_baja_programada()
    {
        var s = Active(Plan(price: 40m, code: "pro"));
        s.ChangePlan(Plan(price: 10m, code: "minimo"), Today, null, Now, null);

        s.ChangePlan(Plan(price: 90m, code: "premium"), Today, null, Now, null);

        Assert.Equal("premium", s.PlanCode);
        Assert.Null(s.PendingPlanVersionId);
        Assert.DoesNotContain(s.PlanChanges, c => c.Status == PlanChangeStatus.Scheduled);
    }

    // ---- Cancelación -------------------------------------------------------------------------------------------------

    [Fact]
    public void La_cancelacion_al_final_del_periodo_no_cambia_el_estado_hasta_esa_fecha()
    {
        var s = Active();

        Assert.True(s.ScheduleCancellation(" Cierra el gimnasio ", null, Now).IsSuccess);

        Assert.Equal(SubscriptionStatus.Active, s.Status);
        Assert.True(s.CancelAtPeriodEnd);
        Assert.Equal("Cierra el gimnasio", s.ScheduledCancelReason);
        Assert.Equal(SubscriptionEventType.CancellationScheduled, s.Events[^1].Type);
        Assert.Equal("subscriptions.cancellation_already_scheduled", s.ScheduleCancellation("otra vez", null, Now).Error.Code);

        Assert.True(s.CompleteScheduledCancellation(null, Now.AddDays(31)).IsSuccess);
        Assert.Equal(SubscriptionStatus.Cancelled, s.Status);
        Assert.Equal("Cierra el gimnasio", s.CancelReason);
        Assert.False(s.CancelAtPeriodEnd);
    }

    [Fact]
    public void La_cancelacion_programada_exige_motivo_y_se_puede_retirar()
    {
        var s = Active();
        Assert.Equal("subscriptions.reason_required", s.ScheduleCancellation(" ", null, Now).Error.Code);
        Assert.Equal("subscriptions.no_cancellation_scheduled", s.ResumeCancellation(null, Now, null).Error.Code);

        s.ScheduleCancellation("Motivo", null, Now);
        Assert.True(s.ResumeCancellation(null, Now, "Se arrepintió").IsSuccess);

        Assert.False(s.CancelAtPeriodEnd);
        Assert.Null(s.ScheduledCancelReason);
        Assert.Equal(SubscriptionEventType.CancellationResumed, s.Events[^1].Type);
    }

    [Fact]
    public void Solo_una_suscripcion_en_prueba_o_activa_admite_cancelacion_al_final_del_periodo()
    {
        var pastDue = Active();
        pastDue.MarkPastDue(7, null, Now);
        Assert.Equal("subscriptions.invalid_state", pastDue.ScheduleCancellation("x", null, Now).Error.Code);

        var suspended = Active();
        suspended.Suspend("x", null, Now);
        Assert.Equal("subscriptions.invalid_state", suspended.ScheduleCancellation("x", null, Now).Error.Code);
    }

    // ---- Prórroga ----------------------------------------------------------------------------------------------------

    [Fact]
    public void La_prorroga_alarga_la_prueba_el_periodo_activo_o_la_gracia_segun_el_estado()
    {
        var trial = Subscription.Start(Tenant, Plan(trialDays: 14), true, Today, null, Now, null).Value;
        Assert.True(trial.Extend(7, "Pidió más tiempo", null, Now).IsSuccess);
        Assert.Equal(Today.AddDays(21), trial.TrialEndsOn);
        Assert.Equal(Today.AddDays(21), trial.CurrentPeriodEnd);
        Assert.Equal(Today.AddDays(21), trial.NextDueOn);

        var active = Active();
        var end = active.CurrentPeriodEnd;
        Assert.True(active.Extend(10, "Cortesía", null, Now).IsSuccess);
        Assert.Equal(end.AddDays(10), active.CurrentPeriodEnd);
        Assert.Equal(end.AddDays(10), active.NextDueOn);

        var late = Active();
        late.MarkPastDue(7, null, Now);
        var grace = late.GraceEndsOn!.Value;
        Assert.True(late.Extend(5, "Espera de pago", null, Now).IsSuccess);
        Assert.Equal(grace.AddDays(5), late.GraceEndsOn);
        Assert.Equal(SubscriptionEventType.Extended, late.Events[^1].Type);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    [InlineData(91)]
    public void La_prorroga_tiene_un_rango_de_dias(int days) =>
        Assert.Equal("subscriptions.extension_days_invalid", Active().Extend(days, "x", null, Now).Error.Code);

    [Fact]
    public void La_prorroga_exige_motivo_y_un_estado_que_la_admita()
    {
        Assert.Equal("subscriptions.reason_required", Active().Extend(5, " ", null, Now).Error.Code);

        var expired = Active();
        expired.Expire(null, Now);
        Assert.Equal("subscriptions.invalid_state", expired.Extend(5, "x", null, Now).Error.Code);

        var suspended = Active();
        suspended.Suspend("x", null, Now);
        Assert.Equal("subscriptions.invalid_state", suspended.Extend(5, "x", null, Now).Error.Code);
    }

    // ---- Qué le toca al proceso de vencimientos ----------------------------------------------------------------------

    [Fact]
    public void El_proceso_de_vencimientos_decide_segun_el_estado_y_el_dia_local()
    {
        var trial = Subscription.Start(Tenant, Plan(trialDays: 7), true, Today, null, Now, null).Value; // termina Today+7
        Assert.Equal(LifecycleAction.None, SubscriptionLifecycle.NextAction(trial, Today.AddDays(6)));
        Assert.Equal(LifecycleAction.ExpireTrial, SubscriptionLifecycle.NextAction(trial, Today.AddDays(7)));

        var paid = Active(Plan(price: 30m));
        Assert.Equal(LifecycleAction.None, SubscriptionLifecycle.NextAction(paid, paid.CurrentPeriodEnd.AddDays(-1)));
        Assert.Equal(LifecycleAction.MarkPastDue, SubscriptionLifecycle.NextAction(paid, paid.CurrentPeriodEnd));

        var free = Active(Plan(price: 0m));
        Assert.Equal(LifecycleAction.RenewFree, SubscriptionLifecycle.NextAction(free, free.CurrentPeriodEnd));

        var scheduled = Active(Plan(price: 30m));
        scheduled.ScheduleCancellation("x", null, Now);
        Assert.Equal(LifecycleAction.CancelAtPeriodEnd, SubscriptionLifecycle.NextAction(scheduled, scheduled.CurrentPeriodEnd));

        var late = Active();
        late.MarkPastDue(7, null, Now);
        Assert.Equal(LifecycleAction.None, SubscriptionLifecycle.NextAction(late, late.GraceEndsOn!.Value.AddDays(-1)));
        Assert.Equal(LifecycleAction.ExpireGrace, SubscriptionLifecycle.NextAction(late, late.GraceEndsOn!.Value));
    }

    [Theory]
    [InlineData(SubscriptionStatus.Suspended)]
    [InlineData(SubscriptionStatus.Expired)]
    [InlineData(SubscriptionStatus.Cancelled)]
    public void El_proceso_no_toca_suscripciones_suspendidas_vencidas_ni_canceladas(SubscriptionStatus status)
    {
        var s = Active();
        if (status == SubscriptionStatus.Suspended)
        {
            s.Suspend("x", null, Now);
        }
        else if (status == SubscriptionStatus.Expired)
        {
            s.Expire(null, Now);
        }
        else
        {
            s.Cancel("x", null, Now);
        }

        Assert.Equal(LifecycleAction.None, SubscriptionLifecycle.NextAction(s, Today.AddYears(5)));
    }
}
