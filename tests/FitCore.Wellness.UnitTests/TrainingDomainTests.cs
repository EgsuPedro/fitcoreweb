using FitCore.Wellness.Domain;

namespace FitCore.Wellness.UnitTests;

public class TrainingDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 3, 10);

    private static Prescription P(int sets = 3, int? reps = null, int? duration = null, int rest = 60, decimal? load = null) => new(sets, reps, duration, rest, load, null);

    // ---- Prescripción según el modo de registro ----

    [Fact]
    public void Repeticiones_y_carga_pide_repeticiones_y_acepta_carga_opcional()
    {
        Assert.Null(TrainingRules.ValidatePrescription(TrackingMode.RepsAndLoad, P(reps: 10)));
        Assert.Null(TrainingRules.ValidatePrescription(TrackingMode.RepsAndLoad, P(reps: 10, load: 40m)));
        Assert.Equal("training.prescription_reps_required", TrainingRules.ValidatePrescription(TrackingMode.RepsAndLoad, P()));
        Assert.Equal("training.prescription_reps_required", TrainingRules.ValidatePrescription(TrackingMode.RepsAndLoad, P(duration: 30)));
        Assert.Equal("training.prescription_reps_required", TrainingRules.ValidatePrescription(TrackingMode.RepsAndLoad, P(reps: 0)));
    }

    [Fact]
    public void Solo_repeticiones_no_admite_carga()
    {
        Assert.Null(TrainingRules.ValidatePrescription(TrackingMode.Reps, P(reps: 15)));
        Assert.Equal("training.load_not_applicable", TrainingRules.ValidatePrescription(TrackingMode.Reps, P(reps: 15, load: 10m)));
        Assert.Equal("training.prescription_reps_required", TrainingRules.ValidatePrescription(TrackingMode.Reps, P(duration: 20)));
    }

    [Fact]
    public void Por_tiempo_pide_duracion_y_no_admite_repeticiones_ni_carga()
    {
        Assert.Null(TrainingRules.ValidatePrescription(TrackingMode.Duration, P(sets: 3, duration: 45)));
        Assert.Equal("training.prescription_duration_required", TrainingRules.ValidatePrescription(TrackingMode.Duration, P(reps: 10)));
        Assert.Equal("training.prescription_duration_required", TrainingRules.ValidatePrescription(TrackingMode.Duration, P(duration: 2)));
        Assert.Equal("training.prescription_duration_required", TrainingRules.ValidatePrescription(TrackingMode.Duration, P(reps: 10, duration: 30)));
        Assert.Equal("training.load_not_applicable", TrainingRules.ValidatePrescription(TrackingMode.Duration, P(duration: 30, load: 5m)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(21)]
    public void Las_series_deben_estar_entre_1_y_20(int sets) =>
        Assert.Equal("training.sets_invalid", TrainingRules.ValidatePrescription(TrackingMode.Reps, P(sets: sets, reps: 10)));

    [Theory]
    [InlineData(-1)]
    [InlineData(1801)]
    public void El_descanso_debe_estar_entre_0_y_30_minutos(int rest) =>
        Assert.Equal("training.rest_invalid", TrainingRules.ValidatePrescription(TrackingMode.Reps, P(reps: 10, rest: rest)));

    [Theory]
    [InlineData(-5)]
    [InlineData(1001)]
    public void La_carga_recomendada_debe_ser_razonable(decimal load) =>
        Assert.Equal("training.load_invalid", TrainingRules.ValidatePrescription(TrackingMode.RepsAndLoad, P(reps: 8, load: load)));

    // ---- Series realizadas ----

    [Fact]
    public void Las_series_realizadas_siguen_el_mismo_criterio_que_la_prescripcion()
    {
        Assert.Null(TrainingRules.ValidateActualSet(TrackingMode.RepsAndLoad, new ActualSet(8, null, 60m)));
        Assert.Null(TrainingRules.ValidateActualSet(TrackingMode.RepsAndLoad, new ActualSet(8, null, null)));
        Assert.Equal("training.set_reps_required", TrainingRules.ValidateActualSet(TrackingMode.RepsAndLoad, new ActualSet(null, 30, 60m)));
        Assert.Null(TrainingRules.ValidateActualSet(TrackingMode.Reps, new ActualSet(20, null, null)));
        Assert.Equal("training.load_not_applicable", TrainingRules.ValidateActualSet(TrackingMode.Reps, new ActualSet(20, null, 5m)));
        Assert.Null(TrainingRules.ValidateActualSet(TrackingMode.Duration, new ActualSet(null, 60, null)));
        Assert.Equal("training.set_duration_required", TrainingRules.ValidateActualSet(TrackingMode.Duration, new ActualSet(12, null, null)));
        Assert.Equal("training.set_duration_required", TrainingRules.ValidateActualSet(TrackingMode.Duration, new ActualSet(null, 0, null)));
        Assert.Equal("training.load_invalid", TrainingRules.ValidateActualSet(TrackingMode.RepsAndLoad, new ActualSet(5, null, -1m)));
        Assert.Equal("training.set_reps_required", TrainingRules.ValidateActualSet(TrackingMode.RepsAndLoad, new ActualSet(201, null, 10m)));
    }

    [Fact]
    public void El_volumen_es_repeticiones_por_carga_y_sin_carga_no_suma()
    {
        Assert.Equal(480m, TrainingRules.Volume(new ActualSet(8, null, 60m)));
        Assert.Equal(0m, TrainingRules.Volume(new ActualSet(8, null, null)));
        Assert.Equal(0m, TrainingRules.Volume(new ActualSet(null, 60, null)));
        Assert.Equal(22.5m, TrainingRules.Volume(new ActualSet(5, null, 4.5m)));
    }

    // ---- Versiones ----

    [Fact]
    public void La_cabecera_solo_apunta_a_la_version_vigente_y_nunca_retrocede_por_diseno()
    {
        var tenant = Guid.NewGuid();
        var plan = TrainingPlan.Create(tenant, Guid.NewGuid(), Now);
        Assert.Equal(0, plan.CurrentVersion);

        var header = new PlanHeader("Fuerza base", "Ganar fuerza", null, Difficulty.Beginner, 60, null, null);
        var v1 = TrainingPlanVersion.Create(tenant, plan.Id, 1, header, "  Primera versión  ", null, Now);
        plan.PublishVersion(v1, null, Now);
        var v2 = TrainingPlanVersion.Create(tenant, plan.Id, 2, header with { Name = "Fuerza base v2" }, null, null, Now.AddDays(7));
        plan.PublishVersion(v2, null, Now.AddDays(7));

        Assert.Equal(2, plan.CurrentVersion);
        Assert.Equal(v2.Id, plan.CurrentVersionId);
        Assert.Equal("Primera versión", v1.ChangeNote);
        // La primera versión conserva sus datos aunque haya una nueva.
        Assert.Equal("Fuerza base", v1.Name);
        Assert.Equal("Fuerza base v2", v2.Name);
    }

    [Fact]
    public void El_ejercicio_prescrito_guarda_copia_del_nombre_y_del_modo_de_registro()
    {
        var day = Guid.NewGuid();
        var item = TrainingPlanExercise.Create(Guid.NewGuid(), day, Guid.NewGuid(), 1, "Plancha frontal", TrackingMode.Duration, P(sets: 3, duration: 40, rest: 30));

        Assert.Equal("Plancha frontal", item.ExerciseName);
        Assert.Equal(TrackingMode.Duration, item.TrackingMode);
        Assert.Equal(40, item.DurationSeconds);
        Assert.Null(item.Reps);
    }

    // ---- Asignaciones ----

    private static TrainingAssignment Assignment(DateOnly start, DateOnly? end = null) =>
        TrainingAssignment.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), start, end, Guid.NewGuid(), Now);

    [Fact]
    public void Una_asignacion_rige_desde_su_inicio_hasta_su_fin_programado()
    {
        var a = Assignment(Today, Today.AddDays(10));

        Assert.False(a.IsCurrentOn(Today.AddDays(-1)));
        Assert.True(a.IsCurrentOn(Today));
        Assert.True(a.IsCurrentOn(Today.AddDays(10)));
        Assert.False(a.IsCurrentOn(Today.AddDays(11)));
        Assert.Equal(Today.AddDays(10), a.LastDay);
    }

    [Fact]
    public void Sin_fin_programado_ni_cierre_rige_indefinidamente()
    {
        var a = Assignment(Today);

        Assert.Null(a.LastDay);
        Assert.True(a.IsCurrentOn(Today.AddDays(1000)));
    }

    [Fact]
    public void El_cierre_explicito_acorta_la_vigencia_y_manda_el_menor_entre_cierre_y_fin_programado()
    {
        var a = Assignment(Today, Today.AddDays(30));
        a.End(Today.AddDays(4), "  Cambio de objetivo  ");

        Assert.Equal(Today.AddDays(4), a.LastDay);
        Assert.Equal("Cambio de objetivo", a.EndReason);
        Assert.True(a.IsCurrentOn(Today.AddDays(4)));
        Assert.False(a.IsCurrentOn(Today.AddDays(5)));

        // Si el fin programado es anterior al cierre, manda el fin programado.
        var b = Assignment(Today, Today.AddDays(2));
        b.End(Today.AddDays(9), "x");
        Assert.Equal(Today.AddDays(2), b.LastDay);
    }

    [Fact]
    public void Una_asignacion_cerrada_antes_de_empezar_nunca_rige()
    {
        var a = Assignment(Today.AddDays(5));
        a.End(Today.AddDays(4), "Se canceló antes de empezar");

        Assert.False(a.IsCurrentOn(Today.AddDays(5)));
        Assert.False(a.IsCurrentOn(Today.AddDays(6)));
    }

    // ---- Sesiones ----

    [Fact]
    public void La_sesion_guarda_la_version_con_que_se_hizo_y_el_comentario_del_entrenador_se_agrega_aparte()
    {
        var version = Guid.NewGuid();
        var session = WorkoutSession.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), version, Guid.NewGuid(), Today, 55, "  Me sentí bien  ", Guid.NewGuid(), Now);

        Assert.Equal(version, session.PlanVersionId);
        Assert.Equal("Me sentí bien", session.MemberNotes);
        Assert.Null(session.TrainerComment);

        session.AddTrainerComment("  Sube 2,5 kg la próxima  ", Now.AddHours(2));

        Assert.Equal("Sube 2,5 kg la próxima", session.TrainerComment);
        Assert.Equal(Now.AddHours(2), session.TrainerCommentAt);
        Assert.Equal("Me sentí bien", session.MemberNotes);
    }

    [Fact]
    public void La_serie_guarda_lo_realmente_hecho()
    {
        var set = WorkoutSet.Create(Guid.NewGuid(), Guid.NewGuid(), 2, new ActualSet(10, null, 52.5m));

        Assert.Equal(2, set.SetNumber);
        Assert.Equal(10, set.Reps);
        Assert.Equal(52.5m, set.LoadKg);
        Assert.Null(set.DurationSeconds);
    }
}
