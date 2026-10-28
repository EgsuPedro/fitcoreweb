using FitCore.Classes.Domain;

namespace FitCore.Classes.UnitTests;

public class ClassSessionDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo Ecuador = TimeZoneInfo.FindSystemTimeZoneById("America/Guayaquil");
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    private static ClassSeries Series(DateOnly start, DateOnly end, params DayOfWeek[] days) =>
        ClassSeries.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 20, 60, start, end, new TimeOnly(9, 0), days, null, Now);

    [Fact]
    public void Los_dias_se_guardan_como_mascara_y_se_leen_de_lunes_a_domingo_sin_repetidos()
    {
        var series = Series(new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 30), DayOfWeek.Sunday, DayOfWeek.Friday, DayOfWeek.Monday, DayOfWeek.Monday);

        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Friday, DayOfWeek.Sunday], series.Days);
        Assert.Equal((1 << (int)DayOfWeek.Sunday) | (1 << (int)DayOfWeek.Friday) | (1 << (int)DayOfWeek.Monday), series.DaysMask);
    }

    [Fact]
    public void Las_fechas_de_la_serie_son_solo_las_de_los_dias_elegidos_dentro_del_rango_inclusive()
    {
        // 2026-11-02 es lunes.
        var series = Series(new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 16), DayOfWeek.Monday, DayOfWeek.Wednesday);

        Assert.Equal(
            [new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 4), new DateOnly(2026, 11, 9), new DateOnly(2026, 11, 11), new DateOnly(2026, 11, 16)],
            series.Dates(series.EndDate));
    }

    [Fact]
    public void La_hora_local_se_convierte_a_utc_con_el_desfase_de_la_sucursal()
    {
        var utc = ClassSeries.ToUtc(new DateOnly(2026, 11, 2), new TimeOnly(9, 0), Ecuador);

        Assert.Equal(new DateTimeOffset(2026, 11, 2, 14, 0, 0, TimeSpan.Zero), utc);
    }

    [Fact]
    public void Con_horario_de_verano_la_hora_local_se_mantiene_y_el_instante_utc_cambia()
    {
        // Nueva York pasa de UTC-4 a UTC-5 el 1 de noviembre de 2026: las 9:00 locales siguen siendo las 9:00.
        var before = ClassSeries.ToUtc(new DateOnly(2026, 10, 31), new TimeOnly(9, 0), NewYork);
        var after = ClassSeries.ToUtc(new DateOnly(2026, 11, 2), new TimeOnly(9, 0), NewYork);

        Assert.Equal(13, before!.Value.Hour);
        Assert.Equal(14, after!.Value.Hour);
    }

    [Fact]
    public void Una_hora_que_no_existe_ese_dia_no_genera_instante()
    {
        // 8 de marzo de 2026: a las 2:00 los relojes de Nueva York saltan a las 3:00.
        Assert.Null(ClassSeries.ToUtc(new DateOnly(2026, 3, 8), new TimeOnly(2, 30), NewYork));
    }

    [Fact]
    public void Una_hora_repetida_toma_la_primera_ocurrencia()
    {
        // 1 de noviembre de 2026: la 1:30 ocurre dos veces; la primera es todavía UTC-4.
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero), ClassSeries.ToUtc(new DateOnly(2026, 11, 1), new TimeOnly(1, 30), NewYork));
    }

    [Fact]
    public void Extender_solo_cambia_el_fin_y_cancelar_deja_el_motivo()
    {
        var series = Series(new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 30), DayOfWeek.Monday);

        series.ExtendTo(new DateOnly(2026, 12, 14), null, Now);
        Assert.Equal(new DateOnly(2026, 12, 14), series.EndDate);
        Assert.Equal(new DateOnly(2026, 11, 2), series.StartDate);

        series.Cancel("  Cierre del local  ", Guid.NewGuid(), Now);
        Assert.False(series.IsActive);
        Assert.Equal("Cierre del local", series.CancelReason);
        Assert.Equal(Now, series.CancelledAt);
    }

    private static ClassSession Session(DateTimeOffset startsAt) =>
        ClassSession.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), startsAt, startsAt.AddMinutes(60), 20, "  ", "  ", null, Now);

    [Fact]
    public void Una_sesion_nueva_queda_programada_con_su_origen_y_sin_textos_vacios()
    {
        var starts = Now.AddDays(3);
        var session = Session(starts);

        Assert.True(session.IsScheduled);
        Assert.Equal(starts, session.OriginalStartsAt);
        Assert.Null(session.Notes);
        Assert.Null(session.AvailabilityOverrideReason);
        Assert.False(session.IsModified);
    }

    [Fact]
    public void Mover_una_sesion_de_serie_la_marca_modificada_pero_conserva_su_hueco_original()
    {
        var starts = Now.AddDays(3);
        var session = Session(starts);

        session.Reschedule(starts.AddHours(2), starts.AddHours(3), session.InstructorId, 15, "Cambio de sala", null, null, Now);

        Assert.Equal(starts, session.OriginalStartsAt);
        Assert.Equal(starts.AddHours(2), session.StartsAt);
        Assert.Equal(15, session.Capacity);
        Assert.Equal("Cambio de sala", session.Notes);
        Assert.Equal(Now, session.UpdatedAt);
    }

    [Fact]
    public void Cancelar_registra_motivo_quien_y_cuando_y_la_deja_sin_posibilidad_de_modificarse()
    {
        var session = Session(Now.AddDays(3));
        var by = Guid.NewGuid();

        Assert.True(session.CanBeModified(Now));
        session.Cancel("  Instructor enfermo ", by, Now);

        Assert.Equal(SessionStatus.Cancelled, session.Status);
        Assert.Equal("Instructor enfermo", session.CancelReason);
        Assert.Equal(by, session.CancelledBy);
        Assert.Equal(Now, session.CancelledAt);
        Assert.False(session.CanBeModified(Now));
    }

    [Fact]
    public void Una_sesion_que_ya_empezo_no_se_puede_modificar()
    {
        var session = Session(Now.AddMinutes(-1));

        Assert.False(session.CanBeModified(Now));
    }

    private static readonly Guid UioBranch = Guid.NewGuid();

    // 2026-11-02 es lunes. Local de Ecuador (UTC-5): las 9:00 son las 14:00 UTC.
    private static DateTimeOffset MondayAt(int localHour, int localMinute = 0) =>
        new DateTimeOffset(2026, 11, 2, localHour, localMinute, 0, TimeSpan.FromHours(-5)).ToUniversalTime();

    private static bool Fits(IReadOnlyCollection<InstructorSlot> slots, DateTimeOffset start, int minutes = 60, Guid? branch = null) =>
        InstructorAvailabilityRules.Fits(slots, branch ?? UioBranch, start, start.AddMinutes(minutes), Ecuador);

    [Fact]
    public void Sin_franjas_declaradas_el_instructor_no_tiene_restriccion()
    {
        Assert.True(Fits([], MondayAt(23, 30), 120));
    }

    [Fact]
    public void Con_franjas_la_sesion_debe_caber_entera_en_una_del_mismo_dia()
    {
        InstructorSlot[] slots = [new(DayOfWeek.Monday, new TimeOnly(8, 0), new TimeOnly(12, 0), null)];

        Assert.True(Fits(slots, MondayAt(9)));
        Assert.True(Fits(slots, MondayAt(8)));
        Assert.True(Fits(slots, MondayAt(11)));
        Assert.False(Fits(slots, MondayAt(11, 30)));
        Assert.False(Fits(slots, MondayAt(7, 30)));
        Assert.False(Fits(slots, MondayAt(13)));
        Assert.False(Fits(slots, MondayAt(9).AddDays(1)));
    }

    [Fact]
    public void Una_franja_de_otra_sucursal_no_vale_pero_una_sin_sucursal_vale_en_todas()
    {
        var other = Guid.NewGuid();

        Assert.False(Fits([new(DayOfWeek.Monday, new TimeOnly(8, 0), new TimeOnly(12, 0), other)], MondayAt(9)));
        Assert.True(Fits([new(DayOfWeek.Monday, new TimeOnly(8, 0), new TimeOnly(12, 0), UioBranch)], MondayAt(9)));
        Assert.True(Fits([new(DayOfWeek.Monday, new TimeOnly(8, 0), new TimeOnly(12, 0), null)], MondayAt(9)));
    }

    [Fact]
    public void Una_sesion_que_cruza_la_medianoche_no_cabe_en_ninguna_franja()
    {
        InstructorSlot[] slots = [new(DayOfWeek.Monday, new TimeOnly(0, 0), new TimeOnly(23, 59), null)];

        Assert.False(Fits(slots, MondayAt(23, 30), 60));
    }
}
