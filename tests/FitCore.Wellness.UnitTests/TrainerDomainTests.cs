using FitCore.Wellness.Domain;

namespace FitCore.Wellness.UnitTests;

public class TrainerDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Las_especialidades_se_limpian_sin_vacios_ni_repetidos_conservando_el_orden()
    {
        var result = TrainerProfile.NormalizeSpecialties(["  Fuerza ", "", "fuerza", "Movilidad", "   ", "FUERZA", "Funcional"]);

        Assert.Equal(["Fuerza", "Movilidad", "Funcional"], result);
    }

    [Fact]
    public void Un_perfil_nuevo_esta_activo_y_guarda_los_datos_normalizados()
    {
        var profile = TrainerProfile.Create(Guid.NewGuid(), Guid.NewGuid(), [" Pesas ", "pesas"], "  ", "  Entrenador con 10 años  ", new DateOnly(2026, 1, 5), Guid.NewGuid(), Now);

        Assert.True(profile.IsActive);
        Assert.Equal(["Pesas"], profile.Specialties);
        Assert.Null(profile.Certifications);
        Assert.Equal("Entrenador con 10 años", profile.Bio);
    }

    [Fact]
    public void Actualizar_y_cambiar_el_estado_registra_quien_y_cuando()
    {
        var profile = TrainerProfile.Create(Guid.NewGuid(), Guid.NewGuid(), null, null, null, new DateOnly(2026, 1, 5), null, Now);
        var by = Guid.NewGuid();

        profile.Update(["Yoga"], "Certificado X", null, new DateOnly(2026, 2, 1), by, Now.AddDays(1));
        profile.SetStatus(TrainerStatus.Inactive, by, Now.AddDays(2));

        Assert.Equal(["Yoga"], profile.Specialties);
        Assert.Equal(new DateOnly(2026, 2, 1), profile.JoinedOn);
        Assert.False(profile.IsActive);
        Assert.Equal(by, profile.UpdatedBy);
        Assert.Equal(Now.AddDays(2), profile.UpdatedAt);
    }

    private static AvailabilitySlot Slot(DayOfWeek day, string start, string end, Guid? branch = null) =>
        new(day, TimeOnly.Parse(start, System.Globalization.CultureInfo.InvariantCulture), TimeOnly.Parse(end, System.Globalization.CultureInfo.InvariantCulture), branch);

    [Fact]
    public void Franjas_validas_en_distintos_dias_y_contiguas_se_aceptan()
    {
        var slots = new[]
        {
            Slot(DayOfWeek.Monday, "06:00", "10:00"),
            Slot(DayOfWeek.Monday, "10:00", "12:00"),
            Slot(DayOfWeek.Monday, "16:00", "20:00"),
            Slot(DayOfWeek.Tuesday, "06:00", "10:00")
        };

        Assert.Null(AvailabilityRules.Validate(slots));
    }

    [Fact]
    public void Una_franja_con_fin_igual_o_anterior_al_inicio_se_rechaza()
    {
        Assert.Equal("trainers.availability_invalid_range", AvailabilityRules.Validate([Slot(DayOfWeek.Monday, "10:00", "10:00")]));
        Assert.Equal("trainers.availability_invalid_range", AvailabilityRules.Validate([Slot(DayOfWeek.Monday, "10:00", "09:00")]));
    }

    [Fact]
    public void Franjas_solapadas_del_mismo_dia_se_rechazan_pero_en_dias_distintos_no()
    {
        Assert.Equal(
            "trainers.availability_overlap",
            AvailabilityRules.Validate([Slot(DayOfWeek.Friday, "06:00", "10:00"), Slot(DayOfWeek.Friday, "09:59", "12:00")]));
        Assert.Null(AvailabilityRules.Validate([Slot(DayOfWeek.Friday, "06:00", "10:00"), Slot(DayOfWeek.Saturday, "09:00", "12:00")]));
    }

    [Fact]
    public void Demasiadas_franjas_se_rechazan()
    {
        var slots = Enumerable.Range(0, AvailabilityRules.MaxSlots + 1)
            .Select(i => Slot((DayOfWeek)(i % 7), $"{6 + (i / 7):00}:00", $"{6 + (i / 7):00}:30"))
            .ToList();

        Assert.Equal("trainers.availability_too_many", AvailabilityRules.Validate(slots));
    }

    [Fact]
    public void Una_asignacion_nueva_esta_activa_y_al_cerrarla_guarda_fecha_y_motivo()
    {
        var assignment = TrainerAssignment.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 3, 1), Guid.NewGuid(), Now);
        Assert.True(assignment.IsActive);

        var by = Guid.NewGuid();
        assignment.End(new DateOnly(2026, 4, 1), "  Cambio de horario  ", by);

        Assert.False(assignment.IsActive);
        Assert.Equal(new DateOnly(2026, 4, 1), assignment.EndedOn);
        Assert.Equal("Cambio de horario", assignment.EndReason);
        Assert.Equal(by, assignment.EndedBy);
    }

    [Fact]
    public void La_observacion_guarda_el_texto_recortado_y_su_autor()
    {
        var author = Guid.NewGuid();

        var note = TrainerNote.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "  Molestia leve en el hombro  ", author, Now);

        Assert.Equal("Molestia leve en el hombro", note.Text);
        Assert.Equal(author, note.CreatedBy);
    }
}
