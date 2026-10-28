using FitCore.Classes.Domain;

namespace FitCore.Classes.UnitTests;

public class ClassAttendanceDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    private static ClassAttendance Record(AttendanceOutcome outcome, string? notes = null, Guid? gymEntry = null) =>
        ClassAttendance.Record(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), outcome, notes, gymEntry, Guid.NewGuid(), Now);

    [Fact]
    public void Registrar_guarda_el_resultado_la_nota_limpia_y_la_entrada_solo_si_asistio()
    {
        var entry = Guid.NewGuid();

        var attended = Record(AttendanceOutcome.Attended, "  Llegó tarde ", entry);
        var noShow = Record(AttendanceOutcome.NoShow, "   ", entry);

        Assert.Equal("Llegó tarde", attended.Notes);
        Assert.Equal(entry, attended.GymEntryId);
        Assert.Null(noShow.Notes);
        Assert.Null(noShow.GymEntryId);
        Assert.Equal(Now, attended.CreatedAt);
        Assert.Null(attended.UpdatedAt);
    }

    [Fact]
    public void Corregir_cambia_el_resultado_y_registra_quien_y_cuando()
    {
        var attendance = Record(AttendanceOutcome.NoShow);
        var by = Guid.NewGuid();

        var changed = attendance.Correct(AttendanceOutcome.Attended, "Se había marcado mal", null, by, Now.AddMinutes(5));

        Assert.True(changed);
        Assert.Equal(AttendanceOutcome.Attended, attendance.Outcome);
        Assert.Equal("Se había marcado mal", attendance.Notes);
        Assert.Equal(by, attendance.UpdatedBy);
        Assert.Equal(Now.AddMinutes(5), attendance.UpdatedAt);
        Assert.Equal(Now, attendance.CreatedAt);
    }

    [Fact]
    public void Repetir_lo_mismo_no_cambia_nada_ni_toca_la_auditoria()
    {
        var entry = Guid.NewGuid();
        var attendance = Record(AttendanceOutcome.Attended, "Puntual", entry);

        var changed = attendance.Correct(AttendanceOutcome.Attended, " Puntual ", entry, Guid.NewGuid(), Now.AddMinutes(5));

        Assert.False(changed);
        Assert.Null(attendance.UpdatedAt);
        Assert.Null(attendance.UpdatedBy);
    }

    [Fact]
    public void Pasar_a_no_asistio_suelta_la_entrada_al_gimnasio()
    {
        var attendance = Record(AttendanceOutcome.Attended, null, Guid.NewGuid());

        Assert.True(attendance.Correct(AttendanceOutcome.NoShow, null, Guid.NewGuid(), null, Now));

        Assert.Null(attendance.GymEntryId);
    }

    [Fact]
    public void Completar_una_sesion_la_deja_cerrada_y_sin_posibilidad_de_modificarse()
    {
        var starts = Now.AddHours(-2);
        var session = ClassSession.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid(), starts, starts.AddHours(1), 20, null, null, null, Now.AddDays(-3));
        var by = Guid.NewGuid();

        session.Complete(by, Now);

        Assert.Equal(SessionStatus.Completed, session.Status);
        Assert.False(session.IsScheduled);
        Assert.False(session.CanBeModified(Now.AddDays(-10)));
        Assert.Equal(by, session.UpdatedBy);
    }
}
