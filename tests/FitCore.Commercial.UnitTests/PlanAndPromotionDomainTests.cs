using FitCore.Commercial.Domain;

namespace FitCore.Commercial.UnitTests;

public class PlanAndPromotionDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static MembershipPlan Plan(decimal price = 30m, int value = 1, DurationUnit unit = DurationUnit.Month, DateOnly? from = null, DateOnly? until = null, bool active = true, params Guid[] branches) =>
        MembershipPlan.Create(
            Guid.NewGuid(),
            new PlanDetails("Plan", null, price, value, unit, null, null, null, TaxKind.General, active, from, until, false, 0),
            branches,
            null,
            Now);

    [Theory]
    [InlineData(2026, 1, 1, 1, DurationUnit.Day, 2026, 1, 1)] // un pase diario termina el mismo día
    [InlineData(2026, 1, 1, 1, DurationUnit.Week, 2026, 1, 7)]
    [InlineData(2026, 1, 1, 1, DurationUnit.Month, 2026, 1, 31)] // un mes desde el 1 termina el 31
    [InlineData(2026, 1, 15, 1, DurationUnit.Month, 2026, 2, 14)]
    [InlineData(2026, 1, 15, 3, DurationUnit.Month, 2026, 4, 14)]
    [InlineData(2026, 1, 15, 6, DurationUnit.Month, 2026, 7, 14)]
    [InlineData(2026, 3, 10, 1, DurationUnit.Year, 2027, 3, 9)]
    [InlineData(2026, 12, 20, 1, DurationUnit.Month, 2027, 1, 19)] // cruza el año
    [InlineData(2026, 1, 31, 1, DurationUnit.Month, 2026, 2, 27)] // febrero: el 31 se ajusta al 28 y luego se resta un día
    public void El_vencimiento_es_inicio_mas_duracion_menos_un_dia(int y, int m, int d, int value, DurationUnit unit, int ey, int em, int ed) =>
        Assert.Equal(new DateOnly(ey, em, ed), Plan(value: value, unit: unit).CalculateEndDate(new DateOnly(y, m, d)));

    [Fact]
    public void El_precio_se_redondea_a_dos_decimales_hacia_fuera_del_cero() =>
        Assert.Equal(10.01m, Plan(price: 10.005m).Price);

    [Fact]
    public void Un_plan_se_vende_solo_si_esta_activo_dentro_de_su_vigencia_y_en_sus_sucursales()
    {
        var north = Guid.NewGuid();
        var south = Guid.NewGuid();
        var today = new DateOnly(2026, 6, 15);

        Assert.True(Plan().IsSellableOn(today, north)); // sin sucursales: se vende en todas
        Assert.True(Plan(branches: [north]).IsSellableOn(today, north));
        Assert.False(Plan(branches: [north]).IsSellableOn(today, south));
        Assert.False(Plan(active: false).IsSellableOn(today, north));
        Assert.False(Plan(from: today.AddDays(1)).IsSellableOn(today, north));
        Assert.False(Plan(until: today.AddDays(-1)).IsSellableOn(today, north));
        Assert.True(Plan(from: today, until: today).IsSellableOn(today, north));
    }

    [Fact]
    public void Cambiar_las_sucursales_de_un_plan_agrega_y_quita_solo_lo_necesario()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var plan = Plan(branches: [a, b]);

        plan.Update(new PlanDetails("Plan", null, 30m, 1, DurationUnit.Month, null, null, null, TaxKind.General, true, null, null, false, 0), [b, c, c], null, Now);

        Assert.Equivalent(new[] { b, c }, plan.Branches.Select(x => x.BranchId));
    }

    private static Promotion Promo(DiscountType type, decimal value, int? maxUses = null, bool active = true, DateOnly? from = null, DateOnly? until = null, params Guid[] plans) =>
        Promotion.Create(
            Guid.NewGuid(),
            new PromotionDetails("Promo", " abc ", type, value, from ?? new DateOnly(2026, 1, 1), until ?? new DateOnly(2026, 12, 31), maxUses, null, active),
            plans,
            null,
            Now);

    [Theory]
    [InlineData(DiscountType.Percentage, 15, 50.00, 7.50)]
    [InlineData(DiscountType.Percentage, 33.33, 10.00, 3.33)]
    [InlineData(DiscountType.Percentage, 100, 25.00, 25.00)]
    [InlineData(DiscountType.FixedAmount, 10, 50.00, 10.00)]
    [InlineData(DiscountType.FixedAmount, 10, 8.00, 8.00)] // nunca supera el precio
    public void El_descuento_se_calcula_redondeado_y_nunca_supera_el_precio(DiscountType type, decimal value, decimal price, decimal expected) =>
        Assert.Equal(expected, Promo(type, value).DiscountFor(price));

    [Fact]
    public void El_estado_depende_de_la_activacion_la_vigencia_y_los_usos()
    {
        var today = new DateOnly(2026, 6, 15);

        Assert.Equal(PromotionStatus.Active, Promo(DiscountType.Percentage, 10).StatusOn(today));
        Assert.Equal(PromotionStatus.Inactive, Promo(DiscountType.Percentage, 10, active: false).StatusOn(today));
        Assert.Equal(PromotionStatus.Scheduled, Promo(DiscountType.Percentage, 10, from: today.AddDays(1)).StatusOn(today));
        Assert.Equal(PromotionStatus.Expired, Promo(DiscountType.Percentage, 10, until: today.AddDays(-1)).StatusOn(today));
        Assert.Equal(PromotionStatus.Active, Promo(DiscountType.Percentage, 10, from: today, until: today).StatusOn(today));
    }

    [Fact]
    public void El_codigo_se_guarda_en_mayusculas_y_sin_espacios_y_aplica_a_todos_los_planes_si_no_se_limita()
    {
        var planA = Guid.NewGuid();

        var open = Promo(DiscountType.Percentage, 10);
        var limited = Promo(DiscountType.Percentage, 10, plans: [planA]);

        Assert.Equal("ABC", open.Code);
        Assert.True(open.AppliesToPlan(Guid.NewGuid()));
        Assert.True(limited.AppliesToPlan(planA));
        Assert.False(limited.AppliesToPlan(Guid.NewGuid()));
    }

    [Theory]
    [InlineData(11.50, 15, 10.00, 1.50)]
    [InlineData(10.00, 15, 8.70, 1.30)]
    [InlineData(30.00, 0, 30.00, 0.00)]
    [InlineData(23.00, 15, 20.00, 3.00)]
    public void El_IVA_se_desglosa_de_un_precio_que_ya_lo_incluye(decimal price, decimal percent, decimal subtotal, decimal tax)
    {
        var (s, t) = TaxRate.Split(price, percent);

        Assert.Equal(subtotal, s);
        Assert.Equal(tax, t);
    }

    [Fact]
    public void Subtotal_mas_impuesto_siempre_suma_exactamente_el_precio()
    {
        for (var cents = 1; cents <= 20000; cents += 7)
        {
            var price = cents / 100m;
            foreach (var percent in new[] { 0m, 5m, 12m, 15m })
            {
                var (subtotal, tax) = TaxRate.Split(price, percent);
                Assert.Equal(price, subtotal + tax);
                Assert.True(tax >= 0m);
            }
        }
    }

    [Fact]
    public void Una_tarifa_rige_dentro_de_su_vigencia()
    {
        var rate = new TaxRate { Kind = TaxKind.General, Percent = 15, ValidFrom = new DateOnly(2026, 1, 1), ValidUntil = new DateOnly(2026, 6, 30) };

        Assert.False(rate.AppliesOn(new DateOnly(2025, 12, 31)));
        Assert.True(rate.AppliesOn(new DateOnly(2026, 1, 1)));
        Assert.True(rate.AppliesOn(new DateOnly(2026, 6, 30)));
        Assert.False(rate.AppliesOn(new DateOnly(2026, 7, 1)));
    }
}
