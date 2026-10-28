using FitCore.Classes.Domain;

namespace FitCore.Classes.UnitTests;

public class GroupClassDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    private static GroupClassDetails Details(
        string name = "  Yoga matutino  ",
        int notice = 30,
        int advanceDays = 14,
        bool waitlist = true,
        int waitlistCapacity = 5,
        bool membership = true,
        bool paymentPolicy = true,
        string? description = "  Para empezar el día  ",
        string? requirements = "   ") =>
        new(name, description, ActivityType.Yoga, Guid.NewGuid(), 60, 20, ClassLevel.Beginner, requirements, notice, advanceDays, 120, waitlist, waitlistCapacity, 30, membership, paymentPolicy, null);

    [Fact]
    public void Crear_normaliza_los_textos_y_deja_la_clase_activa_con_su_origen()
    {
        var tenant = Guid.NewGuid();
        var branch = Guid.NewGuid();
        var by = Guid.NewGuid();

        var groupClass = GroupClass.Create(tenant, branch, Details(), by, Now);

        Assert.Equal("Yoga matutino", groupClass.Name);
        Assert.Equal("Para empezar el día", groupClass.Description);
        Assert.Null(groupClass.Requirements);
        Assert.Equal(tenant, groupClass.TenantId);
        Assert.Equal(branch, groupClass.BranchId);
        Assert.True(groupClass.IsActive);
        Assert.Equal(Now, groupClass.CreatedAt);
        Assert.Equal(by, groupClass.CreatedBy);
        Assert.Null(groupClass.UpdatedAt);
    }

    [Fact]
    public void Sin_lista_de_espera_su_capacidad_queda_en_cero()
    {
        var groupClass = GroupClass.Create(Guid.NewGuid(), Guid.NewGuid(), Details(waitlist: false, waitlistCapacity: 9), null, Now);

        Assert.False(groupClass.WaitlistEnabled);
        Assert.Equal(0, groupClass.WaitlistCapacity);
    }

    [Fact]
    public void Actualizar_cambia_los_datos_y_registra_quien_y_cuando_sin_tocar_el_origen()
    {
        var branch = Guid.NewGuid();
        var groupClass = GroupClass.Create(Guid.NewGuid(), branch, Details(), null, Now);
        var by = Guid.NewGuid();
        var later = Now.AddHours(1);

        groupClass.Update(Details(name: "Yoga restaurativo"), by, later);

        Assert.Equal("Yoga restaurativo", groupClass.Name);
        Assert.Equal(branch, groupClass.BranchId);
        Assert.Equal(Now, groupClass.CreatedAt);
        Assert.Equal(later, groupClass.UpdatedAt);
        Assert.Equal(by, groupClass.UpdatedBy);
    }

    [Fact]
    public void Desactivar_y_reactivar_no_pierde_la_clase()
    {
        var groupClass = GroupClass.Create(Guid.NewGuid(), Guid.NewGuid(), Details(), null, Now);

        groupClass.SetActive(false, null, Now.AddMinutes(1));
        Assert.False(groupClass.IsActive);

        groupClass.SetActive(true, null, Now.AddMinutes(2));
        Assert.True(groupClass.IsActive);
    }

    [Fact]
    public void Una_configuracion_coherente_no_tiene_problemas()
    {
        Assert.Null(GroupClass.CheckCoherence(Details()));
        Assert.Null(GroupClass.CheckCoherence(Details(waitlist: false, waitlistCapacity: 0, membership: false, paymentPolicy: false)));
        Assert.Null(GroupClass.CheckCoherence(Details(notice: 0, advanceDays: 1)));
    }

    [Theory]
    [InlineData(1440, 1)]
    [InlineData(1441, 1)]
    [InlineData(20160, 14)]
    public void La_anticipacion_minima_debe_ser_menor_que_la_maxima(int noticeMinutes, int advanceDays)
    {
        Assert.Equal(GroupClassErrors.BookingWindowIncoherent, GroupClass.CheckCoherence(Details(notice: noticeMinutes, advanceDays: advanceDays)));
    }

    [Fact]
    public void Un_minuto_menos_que_la_anticipacion_maxima_es_valido()
    {
        Assert.Null(GroupClass.CheckCoherence(Details(notice: 1439, advanceDays: 1)));
    }

    [Fact]
    public void La_lista_de_espera_exige_cupo_y_sin_lista_no_puede_tenerlo()
    {
        Assert.Equal(GroupClassErrors.WaitlistCapacityRequired, GroupClass.CheckCoherence(Details(waitlist: true, waitlistCapacity: 0)));
        Assert.Equal(GroupClassErrors.WaitlistDisabledWithCapacity, GroupClass.CheckCoherence(Details(waitlist: false, waitlistCapacity: 3)));
    }

    [Fact]
    public void La_politica_de_pagos_solo_aplica_si_se_exige_membresia()
    {
        Assert.Equal(GroupClassErrors.PaymentPolicyNeedsMembership, GroupClass.CheckCoherence(Details(membership: false, paymentPolicy: true)));
        Assert.Null(GroupClass.CheckCoherence(Details(membership: false, paymentPolicy: false)));
    }
}
