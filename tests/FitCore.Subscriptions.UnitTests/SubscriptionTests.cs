using FitCore.Subscriptions.Domain;

namespace FitCore.Subscriptions.UnitTests;

public class SubscriptionTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 15, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 29);

    private static PlanSnapshot Plan(
        decimal price = 30m, BillingPeriod period = BillingPeriod.Monthly, int trialDays = 14, int? graceDays = null, string code = "pro") =>
        new(Guid.NewGuid(), Guid.NewGuid(), 1, code, "Pro", "USD", period, price, trialDays, graceDays);

    private static Subscription Started(bool trial = false, PlanSnapshot? plan = null) =>
        Subscription.Start(Tenant, plan ?? Plan(), trial, Today, null, Now, null).Value;

    [Fact]
    public void Contratar_sin_prueba_nace_activa_con_el_periodo_y_el_precio_copiados_del_plan()
    {
        var s = Started(plan: Plan(price: 49.90m, period: BillingPeriod.Quarterly));

        Assert.Equal(SubscriptionStatus.Active, s.Status);
        Assert.Equal(Today, s.CurrentPeriodStart);
        Assert.Equal(new DateOnly(2027, 1, 29), s.CurrentPeriodEnd);
        Assert.Equal(s.CurrentPeriodEnd, s.NextDueOn);
        Assert.Null(s.TrialEndsOn);
        Assert.Equal(49.90m, s.Price);
        Assert.Equal("USD", s.Currency);
        Assert.Equal("pro", s.PlanCode);
        Assert.Equal(1, s.PlanVersionNumber);
        Assert.Single(s.Events);
        Assert.Equal(SubscriptionEventType.Created, s.Events[0].Type);
    }

    [Fact]
    public void Contratar_con_prueba_dura_los_dias_del_plan_y_registra_el_inicio_de_la_prueba()
    {
        var s = Started(trial: true, plan: Plan(trialDays: 14));

        Assert.Equal(SubscriptionStatus.Trialing, s.Status);
        Assert.Equal(new DateOnly(2026, 11, 12), s.TrialEndsOn);
        Assert.Equal(new DateOnly(2026, 11, 12), s.CurrentPeriodEnd);
        Assert.Equal(new DateOnly(2026, 11, 12), s.NextDueOn);
        Assert.Equal([SubscriptionEventType.Created, SubscriptionEventType.TrialStarted], s.Events.Select(e => e.Type).ToArray());
    }

    [Fact]
    public void Un_plan_sin_prueba_no_admite_iniciar_una()
    {
        var result = Subscription.Start(Tenant, Plan(trialDays: 0), trial: true, Today, null, Now, null);

        Assert.True(result.IsFailure);
        Assert.Equal("subscriptions.plan_without_trial", result.Error.Code);
    }

    [Theory]
    [InlineData(2026, 1, 31, BillingPeriod.Monthly, 2026, 2, 28)]
    [InlineData(2028, 1, 31, BillingPeriod.Monthly, 2028, 2, 29)]
    [InlineData(2026, 11, 30, BillingPeriod.Quarterly, 2027, 2, 28)]
    [InlineData(2028, 2, 29, BillingPeriod.Yearly, 2029, 2, 28)]
    [InlineData(2026, 10, 29, BillingPeriod.Yearly, 2027, 10, 29)]
    public void El_periodo_termina_con_tope_al_ultimo_dia_del_mes(int y, int m, int d, BillingPeriod period, int ey, int em, int ed) =>
        Assert.Equal(new DateOnly(ey, em, ed), Subscription.AddPeriod(new DateOnly(y, m, d), period));

    [Fact]
    public void Activar_una_prueba_empieza_el_primer_periodo_de_pago_ese_dia()
    {
        var s = Started(trial: true);
        var activationDay = new DateOnly(2026, 11, 5);

        var result = s.Activate(activationDay, null, Now.AddDays(7), "Pagó por transferencia");

        Assert.True(result.IsSuccess);
        Assert.Equal(SubscriptionStatus.Active, s.Status);
        Assert.Equal(activationDay, s.CurrentPeriodStart);
        Assert.Equal(new DateOnly(2026, 12, 5), s.CurrentPeriodEnd);
        Assert.Equal(s.CurrentPeriodEnd, s.NextDueOn);
        Assert.Null(s.TrialEndsOn);
        Assert.Equal(SubscriptionEventType.Activated, s.Events[^1].Type);
        Assert.Equal(SubscriptionStatus.Trialing, s.Events[^1].FromStatus);
    }

    [Theory]
    [InlineData(SubscriptionStatus.Active)]
    [InlineData(SubscriptionStatus.Suspended)]
    [InlineData(SubscriptionStatus.Expired)]
    [InlineData(SubscriptionStatus.Cancelled)]
    [InlineData(SubscriptionStatus.PastDue)]
    public void Solo_una_prueba_se_activa(SubscriptionStatus status)
    {
        var s = InState(status);

        var result = s.Activate(Today, null, Now, null);

        Assert.True(result.IsFailure);
        Assert.Equal("subscriptions.invalid_transition", result.Error.Code);
        Assert.Equal(status, s.Status);
    }

    [Fact]
    public void Suspender_exige_motivo_guarda_el_estado_previo_y_reactivar_lo_restituye()
    {
        var s = Started(trial: true);

        Assert.Equal("subscriptions.reason_required", s.Suspend("  ", null, Now).Error.Code);
        Assert.Equal(SubscriptionStatus.Trialing, s.Status);

        Assert.True(s.Suspend(" Fraude en el pago ", null, Now.AddDays(1)).IsSuccess);
        Assert.Equal(SubscriptionStatus.Suspended, s.Status);
        Assert.Equal("Fraude en el pago", s.SuspensionReason);
        Assert.Equal(SubscriptionStatus.Trialing, s.StatusBeforeSuspension);

        Assert.True(s.Reactivate(Today.AddDays(2), 7, null, Now.AddDays(2), null).IsSuccess);
        Assert.Equal(SubscriptionStatus.Trialing, s.Status);
        Assert.Null(s.SuspensionReason);
        Assert.Null(s.StatusBeforeSuspension);
        Assert.Null(s.SuspendedAt);
    }

    [Fact]
    public void Reactivar_con_el_periodo_vencido_deja_la_suscripcion_en_mora_con_gracia_nueva()
    {
        var s = Started();
        s.Suspend("Revisión", null, Now);
        var after = s.CurrentPeriodEnd.AddDays(10);

        var result = s.Reactivate(after, graceDays: 7, null, Now.AddDays(40), null);

        Assert.True(result.IsSuccess);
        Assert.Equal(SubscriptionStatus.PastDue, s.Status);
        Assert.Equal(after.AddDays(7), s.GraceEndsOn);
    }

    [Fact]
    public void No_se_puede_suspender_dos_veces_ni_reactivar_lo_que_no_esta_suspendido()
    {
        var s = Started();
        Assert.True(s.Suspend("x", null, Now).IsSuccess);
        Assert.Equal("subscriptions.invalid_transition", s.Suspend("otra vez", null, Now).Error.Code);

        var active = Started();
        Assert.Equal("subscriptions.invalid_transition", active.Reactivate(Today, 7, null, Now, null).Error.Code);
    }

    [Fact]
    public void Pasar_a_mora_calcula_la_gracia_desde_el_fin_del_periodo()
    {
        var s = Started(plan: Plan(period: BillingPeriod.Monthly));

        Assert.True(s.MarkPastDue(7, null, Now.AddDays(31)).IsSuccess);

        Assert.Equal(SubscriptionStatus.PastDue, s.Status);
        Assert.Equal(s.CurrentPeriodEnd.AddDays(7), s.GraceEndsOn);
        Assert.Equal(SubscriptionEventType.PastDue, s.Events[^1].Type);
    }

    [Fact]
    public void Una_prueba_no_pasa_a_mora_pero_si_vence()
    {
        var s = Started(trial: true);

        Assert.Equal("subscriptions.invalid_transition", s.MarkPastDue(7, null, Now).Error.Code);
        Assert.True(s.Expire(null, Now.AddDays(15)).IsSuccess);
        Assert.Equal(SubscriptionStatus.Expired, s.Status);
    }

    [Fact]
    public void Cancelar_es_definitivo_exige_motivo_y_no_admite_mas_cambios()
    {
        var s = Started();

        Assert.Equal("subscriptions.reason_required", s.Cancel("", null, Now).Error.Code);
        Assert.True(s.Cancel("El cliente lo pidió", null, Now).IsSuccess);
        Assert.Equal(SubscriptionStatus.Cancelled, s.Status);
        Assert.Equal(Now, s.CancelledAt);
        Assert.False(s.IsCurrent);

        Assert.True(s.Suspend("x", null, Now).IsFailure);
        Assert.True(s.Expire(null, Now).IsFailure);
        Assert.True(s.Cancel("otra vez", null, Now).IsFailure);
        Assert.True(s.Activate(Today, null, Now, null).IsFailure);
    }

    [Fact]
    public void Cada_transicion_deja_su_hecho_en_el_historial_con_origen_y_destino()
    {
        var s = Started();
        s.Suspend("Motivo", Guid.Empty, Now.AddDays(1));
        s.Reactivate(Today.AddDays(2), 7, Guid.Empty, Now.AddDays(2), "Resuelto");

        var events = s.Events.Select(e => (e.Type, e.FromStatus, e.ToStatus)).ToArray();
        Assert.Equal(
            [
                (SubscriptionEventType.Created, (SubscriptionStatus?)null, (SubscriptionStatus?)SubscriptionStatus.Active),
                (SubscriptionEventType.Suspended, SubscriptionStatus.Active, SubscriptionStatus.Suspended),
                (SubscriptionEventType.Reactivated, SubscriptionStatus.Suspended, SubscriptionStatus.Active)
            ],
            events);
        Assert.All(s.Events, e => Assert.Equal(s.Id, e.SubscriptionId));
        Assert.All(s.Events, e => Assert.Equal(Tenant, e.TenantId));
    }

    [Fact]
    public void La_tabla_de_transiciones_coincide_con_el_ciclo_de_vida_acordado()
    {
        Assert.True(SubscriptionTransitions.IsAllowed(SubscriptionStatus.Trialing, SubscriptionStatus.Active));
        Assert.True(SubscriptionTransitions.IsAllowed(SubscriptionStatus.Active, SubscriptionStatus.PastDue));
        Assert.True(SubscriptionTransitions.IsAllowed(SubscriptionStatus.PastDue, SubscriptionStatus.Active));
        Assert.True(SubscriptionTransitions.IsAllowed(SubscriptionStatus.Expired, SubscriptionStatus.Active));

        // Imposibles: volver de cancelada, saltar de prueba a mora, de activa a prueba.
        Assert.Empty(SubscriptionTransitions.From(SubscriptionStatus.Cancelled));
        Assert.False(SubscriptionTransitions.IsAllowed(SubscriptionStatus.Trialing, SubscriptionStatus.PastDue));
        Assert.False(SubscriptionTransitions.IsAllowed(SubscriptionStatus.Active, SubscriptionStatus.Trialing));
        Assert.False(SubscriptionTransitions.IsAllowed(SubscriptionStatus.Expired, SubscriptionStatus.PastDue));
        Assert.All(Enum.GetValues<SubscriptionStatus>(), s => Assert.False(SubscriptionTransitions.IsAllowed(s, s)));
    }

    [Fact]
    public void Un_plan_de_precio_cero_se_reconoce_como_gratuito()
    {
        Assert.True(Started(plan: Plan(price: 0m)).IsFree);
        Assert.False(Started(plan: Plan(price: 0.01m)).IsFree);
    }

    private static Subscription InState(SubscriptionStatus status)
    {
        var s = Started(trial: status == SubscriptionStatus.Trialing);
        switch (status)
        {
            case SubscriptionStatus.Active:
            case SubscriptionStatus.Trialing:
                break;
            case SubscriptionStatus.PastDue:
                s.MarkPastDue(7, null, Now);
                break;
            case SubscriptionStatus.Suspended:
                s.Suspend("x", null, Now);
                break;
            case SubscriptionStatus.Expired:
                s.Expire(null, Now);
                break;
            case SubscriptionStatus.Cancelled:
                s.Cancel("x", null, Now);
                break;
        }

        Assert.Equal(status, s.Status);
        return s;
    }
}
