using System.Net;
using System.Net.Http.Json;
using FitCore.Api.IntegrationTests.Infrastructure;
using Npgsql;
using static FitCore.Api.IntegrationTests.Infrastructure.CommercialTestApi;

namespace FitCore.Api.IntegrationTests;

[Collection(ApiCollection1.Name)]
public class ClassAttendanceTests(ApiFixture api)
{
    private static readonly TimeSpan Ecuador = TimeSpan.FromHours(-5);
    private const string StudentPassword = "Nueva-Clave-2026x";

    private sealed record TrainerRow(Guid Id, Guid UserId, string FullName);

    private sealed record ClassRow(Guid Id);

    private sealed record SessionRow(Guid Id, int Capacity, int BookedCount, int WaitlistCount, string Status);

    private sealed record ReservationRow(Guid Id, Guid SessionId, Guid MemberId, string Status);

    private sealed record EntryRow(
        Guid ReservationId, Guid MemberId, string MemberName, string MemberCode, string ReservationStatus, int? WaitlistPosition, string? Outcome, string? Notes,
        DateTimeOffset? RecordedAt, bool HasGymEntry);

    private sealed record RosterRow(
        Guid SessionId, string ClassName, string SessionStatus, int Capacity, int Confirmed, int Attended, int NoShow, int Pending,
        EntryRow[] Participants, EntryRow[] Offers, EntryRow[] Waitlist);

    private sealed record HistoryRow(
        Guid Id, Guid ReservationId, Guid SessionId, string ClassName, string BranchName, string InstructorName, DateTimeOffset StartsAt, Guid MemberId, string MemberName,
        string Outcome, string? Notes, bool HasGymEntry, DateTimeOffset RecordedAt, string RecordedByName, DateTimeOffset? CorrectedAt);

    private sealed record AccessRow(Guid MemberId, string Email, string TemporaryPassword);

    private sealed record Setup(TestTenant Tenant, Guid Branch, Guid OtherBranch, TrainerRow Instructor, ClassRow Class, PlanDto Plan);

    private static DateTimeOffset Local(DayOfWeek day, int hour, int minute = 0, int weeksAhead = 0)
    {
        var date = DateTimeOffset.UtcNow.ToOffset(Ecuador).Date.AddDays(2);
        while (date.DayOfWeek != day)
        {
            date = date.AddDays(1);
        }

        return new DateTimeOffset(date.AddDays(7 * weeksAhead).AddHours(hour).AddMinutes(minute), Ecuador);
    }

    private async Task<TrainerRow> NewTrainerAsync(TestTenant tenant, string email, params Guid[] branches)
    {
        var user = await TestApi.CreateUserAsync(tenant.Owner, email, "Entrenador", branches);
        var response = await tenant.Owner.PostAsJsonAsync("/api/v1/trainers", new { userId = user.Id, specialties = new[] { "Yoga" } });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TrainerRow>())!;
    }

    private static async Task<ClassRow> NewClassAsync(HttpClient owner, Guid branch, TrainerRow instructor, string name = "Yoga matutino")
    {
        var response = await owner.PostAsJsonAsync("/api/v1/group-classes", new
        {
            branchId = branch,
            name,
            activity = "Yoga",
            instructorId = instructor.Id,
            durationMinutes = 60,
            capacity = 20,
            level = "AllLevels",
            minBookingNoticeMinutes = 30,
            maxBookingAdvanceDays = 30,
            cancellationDeadlineMinutes = 120,
            waitlistEnabled = true,
            waitlistCapacity = 5,
            offerWindowMinutes = 30,
            requireActiveMembership = true,
            applyPaymentPolicy = true
        });
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"Respondió {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<ClassRow>())!;
    }

    private async Task<Setup> NewSetupAsync()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var branch = await TestApi.CreateBranchAsync(tenant.Owner, "UIO");
        var other = await TestApi.CreateBranchAsync(tenant.Owner, "GYE");
        var instructor = await NewTrainerAsync(tenant, "instructora@gym.test", branch);
        var groupClass = await NewClassAsync(tenant.Owner, branch, instructor);
        var plan = await CreatePlanAsync(tenant.Owner, "Mensual", 30m);
        return new Setup(tenant, branch, other, instructor, groupClass, plan);
    }

    private static async Task<SessionRow> NewSessionAsync(HttpClient owner, Guid classId, DateTimeOffset startsAt, int? capacity = null, Guid? instructorId = null)
    {
        var response = await owner.PostAsJsonAsync("/api/v1/class-sessions", new { classId, startsAt, capacity, instructorId });
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"Respondió {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<SessionRow>())!;
    }

    private static int _members;

    private static async Task<MemberDto> NewMemberAsync(Setup s)
    {
        var n = Interlocked.Increment(ref _members);
        var response = await s.Tenant.Owner.PostAsJsonAsync("/api/v1/members", new
        {
            branchId = s.Branch,
            firstName = $"Clase{n}",
            lastName = "Prueba",
            idType = "Cedula",
            idNumber = MemberTests.NewCedula(),
            birthDate = "1995-04-12",
            email = $"clase{n}.{Guid.NewGuid():N}"[..28] + "@alumnos.test",
            phone = "0991234567"
        });
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"Respondió {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        var member = (await response.Content.ReadFromJsonAsync<MemberDto>())!;
        Assert.Equal(HttpStatusCode.Created, (await s.Tenant.Owner.PostAsJsonAsync("/api/v1/memberships", new { memberId = member.Id, planId = s.Plan.Id })).StatusCode);
        await PayAsync(s.Tenant.Owner, member.Id, 30m);
        return member;
    }

    private async Task<HttpClient> StudentAsync(Setup s, MemberDto member)
    {
        var grant = await s.Tenant.Owner.PostAsync($"/api/v1/members/{member.Id}/access", null);
        Assert.Equal(HttpStatusCode.Created, grant.StatusCode);
        var access = (await grant.Content.ReadFromJsonAsync<AccessRow>())!;
        var temp = TestApi.Authenticated(api, await TestApi.LoginAsync(api, s.Tenant.Slug, access.Email, access.TemporaryPassword));
        Assert.Equal(HttpStatusCode.NoContent, (await temp.PostAsJsonAsync("/api/v1/auth/change-password", new { currentPassword = access.TemporaryPassword, newPassword = StudentPassword })).StatusCode);
        return TestApi.Authenticated(api, await TestApi.LoginAsync(api, s.Tenant.Slug, access.Email, StudentPassword));
    }

    private async Task<HttpClient> StaffAsync(Setup s, string email, string role, Guid? branch = null)
    {
        await TestApi.CreateUserAsync(s.Tenant.Owner, email, role, branch ?? s.Branch);
        return TestApi.Authenticated(api, await TestApi.LoginAsync(api, s.Tenant.Slug, email, TestApi.UserPassword));
    }

    private static async Task<ReservationRow> BookAsync(HttpClient client, Guid sessionId, Guid memberId, bool join = false)
    {
        var response = await PostWithKeyAsync(client, "/api/v1/class-reservations", new { sessionId, memberId, joinWaitlist = join });
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"Respondió {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<ReservationRow>())!;
    }

    private static Task<HttpResponseMessage> RecordAsync(HttpClient client, Guid sessionId, params (Guid Reservation, string Outcome, string? Notes)[] entries) =>
        client.PutAsJsonAsync($"/api/v1/class-sessions/{sessionId}/attendance", new { entries = entries.Select(e => new { reservationId = e.Reservation, outcome = e.Outcome, notes = e.Notes }) });

    private static async Task<RosterRow> ReadRosterAsync(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Respondió {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<RosterRow>())!;
    }

    private static Task<HttpResponseMessage> CompleteAsync(HttpClient client, Guid sessionId, bool markPending = false) =>
        client.PostAsJsonAsync($"/api/v1/class-sessions/{sessionId}/complete", new { markPendingAsNoShow = markPending });

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.True(response.StatusCode == status, $"Respondió {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        Assert.Equal(code, await TestApi.ProblemCodeAsync(response));
    }

    private async Task SqlAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(api.AdminConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<long> CountAsync(Guid tenantId, string sql)
    {
        await using var connection = new NpgsqlConnection(api.AppConnection);
        await connection.OpenAsync();
        await using (var set = new NpgsqlCommand($"SELECT set_config('app.tenant_id', '{tenantId}', false)", connection))
        {
            await set.ExecuteScalarAsync();
        }

        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>La sesión empezó hace 10 minutos y dura una hora (se desplaza en la base: el servidor no deja programar en el pasado).</summary>
    private Task StartNowAsync(Guid sessionId, int minutesAgo = 10) =>
        SqlAsync($"UPDATE classes.class_sessions SET starts_at = now() - interval '{minutesAgo} minutes', ends_at = now() - interval '{minutesAgo} minutes' + interval '60 minutes' WHERE id = '{sessionId}'");

    /// <summary>Sesión de capacidad 3 con dos confirmados, uno en espera... lista para empezar.</summary>
    private async Task<(SessionRow Session, ReservationRow A, ReservationRow B, MemberDto MemberA, MemberDto MemberB)> SessionWithTwoAsync(Setup s, DayOfWeek day = DayOfWeek.Monday, int capacity = 2)
    {
        var session = await NewSessionAsync(s.Tenant.Owner, s.Class.Id, Local(day, 9), capacity);
        var (memberA, memberB) = (await NewMemberAsync(s), await NewMemberAsync(s));
        return (session, await BookAsync(s.Tenant.Owner, session.Id, memberA.Id), await BookAsync(s.Tenant.Owner, session.Id, memberB.Id), memberA, memberB);
    }

    // ---- Lista de participantes -------------------------------------------------------------------------------------

    [Fact]
    public async Task La_lista_separa_confirmados_invitaciones_y_espera_con_su_asistencia()
    {
        var s = await NewSetupAsync();
        var (session, a, b, memberA, _) = await SessionWithTwoAsync(s);
        var waiting = await BookAsync(s.Tenant.Owner, session.Id, (await NewMemberAsync(s)).Id, join: true);

        var roster = (await s.Tenant.Owner.GetFromJsonAsync<RosterRow>($"/api/v1/class-sessions/{session.Id}/roster"))!;

        Assert.Equal("Yoga matutino", roster.ClassName);
        Assert.Equal(2, roster.Confirmed);
        Assert.Equal(2, roster.Pending);
        Assert.Equal(0, roster.Attended);
        Assert.Equal(2, roster.Participants.Length);
        Assert.Contains(roster.Participants, p => p.ReservationId == a.Id && p.MemberCode == memberA.Code && p.Outcome is null);
        Assert.Empty(roster.Offers);
        var queued = Assert.Single(roster.Waitlist);
        Assert.Equal(waiting.Id, queued.ReservationId);
        Assert.Equal(1, queued.WaitlistPosition);

        // Si se libera un cupo, la lista lo muestra como invitación pendiente y no como participante.
        await s.Tenant.Owner.PostAsJsonAsync($"/api/v1/class-reservations/{b.Id}/cancel", new { });
        var after = (await s.Tenant.Owner.GetFromJsonAsync<RosterRow>($"/api/v1/class-sessions/{session.Id}/roster"))!;
        Assert.Equal(1, after.Confirmed);
        Assert.Equal(waiting.Id, Assert.Single(after.Offers).ReservationId);
        Assert.Empty(after.Waitlist);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Tenant.Owner.GetAsync($"/api/v1/class-sessions/{Guid.NewGuid()}/roster")).StatusCode);
    }

    // ---- Registrar asistencia ---------------------------------------------------------------------------------------

    [Fact]
    public async Task La_asistencia_se_registra_desde_el_inicio_de_la_sesion_y_registrarla_dos_veces_no_duplica()
    {
        var s = await NewSetupAsync();
        var (session, a, b, _, _) = await SessionWithTwoAsync(s);

        // Antes de empezar no se registra.
        await AssertProblemAsync(await RecordAsync(s.Tenant.Owner, session.Id, (a.Id, "Attended", null)), HttpStatusCode.Conflict, "classes.session_not_started");

        await StartNowAsync(session.Id);
        var first = await ReadRosterAsync(await RecordAsync(s.Tenant.Owner, session.Id, (a.Id, "Attended", "Llegó puntual"), (b.Id, "NoShow", null)));
        Assert.Equal(1, first.Attended);
        Assert.Equal(1, first.NoShow);
        Assert.Equal(0, first.Pending);
        Assert.Equal("Attended", first.Participants.Single(p => p.ReservationId == a.Id).Outcome);
        Assert.Equal("Llegó puntual", first.Participants.Single(p => p.ReservationId == a.Id).Notes);
        Assert.False(first.Participants.Single(p => p.ReservationId == a.Id).HasGymEntry);

        // Repetir exactamente lo mismo no crea nada nuevo ni toca la auditoría.
        var again = await ReadRosterAsync(await RecordAsync(s.Tenant.Owner, session.Id, (a.Id, "Attended", "Llegó puntual"), (b.Id, "NoShow", null)));
        Assert.Equal(first.Attended, again.Attended);
        Assert.Equal(2L, await CountAsync(s.Tenant.Id, $"SELECT count(*) FROM classes.class_attendance WHERE session_id = '{session.Id}'"));
        var audit = (await s.Tenant.Owner.GetFromJsonAsync<PagedDto<AuditDto>>("/api/v1/audit-logs?entity=ClassAttendance&pageSize=100"))!;
        Assert.Equal(2, audit.TotalCount); // dos altas; el reenvío idéntico no agregó nada
    }

    [Fact]
    public async Task Una_correccion_cambia_el_resultado_sin_duplicar_y_queda_auditada()
    {
        var s = await NewSetupAsync();
        var (session, a, b, memberA, _) = await SessionWithTwoAsync(s);
        await StartNowAsync(session.Id);
        await RecordAsync(s.Tenant.Owner, session.Id, (a.Id, "NoShow", null), (b.Id, "Attended", null));

        var corrected = await ReadRosterAsync(await RecordAsync(s.Tenant.Owner, session.Id, (a.Id, "Attended", "Se había registrado mal")));

        Assert.Equal(2, corrected.Attended);
        Assert.Equal(0, corrected.NoShow);
        Assert.Equal(2L, await CountAsync(s.Tenant.Id, $"SELECT count(*) FROM classes.class_attendance WHERE session_id = '{session.Id}'"));
        var history = (await s.Tenant.Owner.GetFromJsonAsync<PagedDto<HistoryRow>>($"/api/v1/class-attendance?memberId={memberA.Id}"))!;
        var row = Assert.Single(history.Items);
        Assert.Equal("Attended", row.Outcome);
        Assert.Equal("Se había registrado mal", row.Notes);
        Assert.NotNull(row.CorrectedAt);
        Assert.False(string.IsNullOrWhiteSpace(row.RecordedByName));

        var audit = (await s.Tenant.Owner.GetFromJsonAsync<PagedDto<AuditDto>>("/api/v1/audit-logs?entity=ClassAttendance&pageSize=100"))!;
        Assert.True(audit.TotalCount >= 3, $"Auditoría con {audit.TotalCount} registros (dos altas y una corrección)");
    }

    [Fact]
    public async Task La_asistencia_se_contrasta_con_la_entrada_al_gimnasio_sin_escribir_en_ella()
    {
        var s = await NewSetupAsync();
        var (session, a, b, memberA, _) = await SessionWithTwoAsync(s);
        await StartNowAsync(session.Id);
        var reception = await StaffAsync(s, "recep@gym.test", "Recepcion");
        var entry = await CheckInAsync(reception, s.Branch, memberA.Id);
        Assert.Equal(HttpStatusCode.Created, entry.StatusCode);
        var gymRowsBefore = await CountAsync(s.Tenant.Id, "SELECT count(*) FROM access.attendance");

        var roster = await ReadRosterAsync(await RecordAsync(s.Tenant.Owner, session.Id, (a.Id, "Attended", null), (b.Id, "Attended", null)));

        // Quien entró al gimnasio durante la clase queda marcado; quien no, no. Y la asistencia general no se toca.
        Assert.True(roster.Participants.Single(p => p.ReservationId == a.Id).HasGymEntry);
        Assert.False(roster.Participants.Single(p => p.ReservationId == b.Id).HasGymEntry);
        Assert.Equal(gymRowsBefore, await CountAsync(s.Tenant.Id, "SELECT count(*) FROM access.attendance"));

        // Marcarlo como «no asistió» desvincula la entrada (no tiene sentido enlazarla).
        var changed = await ReadRosterAsync(await RecordAsync(s.Tenant.Owner, session.Id, (a.Id, "NoShow", null)));
        Assert.False(changed.Participants.Single(p => p.ReservationId == a.Id).HasGymEntry);
    }

    [Fact]
    public async Task Solo_las_reservas_confirmadas_de_la_sesion_tienen_asistencia_y_la_solicitud_se_valida()
    {
        var s = await NewSetupAsync();
        var (session, a, b, _, _) = await SessionWithTwoAsync(s);
        var waiting = await BookAsync(s.Tenant.Owner, session.Id, (await NewMemberAsync(s)).Id, join: true);
        var other = await NewSessionAsync(s.Tenant.Owner, s.Class.Id, Local(DayOfWeek.Tuesday, 9));
        var elsewhere = await BookAsync(s.Tenant.Owner, other.Id, (await NewMemberAsync(s)).Id);
        await s.Tenant.Owner.PostAsJsonAsync($"/api/v1/class-reservations/{b.Id}/cancel", new { }); // pasa a invitación para el que esperaba
        await StartNowAsync(session.Id);

        // En espera, ofrecida, cancelada o de otra sesión: no.
        await AssertProblemAsync(await RecordAsync(s.Tenant.Owner, session.Id, (waiting.Id, "Attended", null)), HttpStatusCode.Conflict, "classes.reservation_not_attendable");
        await AssertProblemAsync(await RecordAsync(s.Tenant.Owner, session.Id, (b.Id, "Attended", null)), HttpStatusCode.Conflict, "classes.reservation_not_attendable");
        await AssertProblemAsync(await RecordAsync(s.Tenant.Owner, session.Id, (elsewhere.Id, "Attended", null)), HttpStatusCode.UnprocessableEntity, "classes.reservation_not_in_session");
        await AssertProblemAsync(await RecordAsync(s.Tenant.Owner, session.Id, (Guid.NewGuid(), "Attended", null)), HttpStatusCode.UnprocessableEntity, "classes.reservation_not_in_session");

        // Una entrada mala no deja registrada ninguna de las buenas (todo o nada).
        await AssertProblemAsync(await RecordAsync(s.Tenant.Owner, session.Id, (a.Id, "Attended", null), (waiting.Id, "Attended", null)), HttpStatusCode.Conflict, "classes.reservation_not_attendable");
        Assert.Equal(0L, await CountAsync(s.Tenant.Id, $"SELECT count(*) FROM classes.class_attendance WHERE session_id = '{session.Id}'"));

        // Solicitud mal formada.
        await AssertProblemAsync(await RecordAsync(s.Tenant.Owner, session.Id, (a.Id, "Attended", null), (a.Id, "NoShow", null)), HttpStatusCode.UnprocessableEntity, "classes.attendance_duplicate_entries");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await RecordAsync(s.Tenant.Owner, session.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await RecordAsync(s.Tenant.Owner, session.Id, (a.Id, "Presente", null))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await RecordAsync(s.Tenant.Owner, session.Id, (a.Id, "Attended", new string('x', 301)))).StatusCode);

        // Una sesión cancelada no tiene asistencia.
        await s.Tenant.Owner.PostAsJsonAsync($"/api/v1/class-sessions/{other.Id}/cancel", new { reason = "Feriado" });
        await AssertProblemAsync(await RecordAsync(s.Tenant.Owner, other.Id, (elsewhere.Id, "Attended", null)), HttpStatusCode.Conflict, "classes.session_cancelled");
    }

    // ---- Completar la sesión ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Completar_cierra_la_fila_puede_marcar_a_los_pendientes_y_es_idempotente()
    {
        var s = await NewSetupAsync();
        var (session, a, b, _, _) = await SessionWithTwoAsync(s);
        var waiting = await BookAsync(s.Tenant.Owner, session.Id, (await NewMemberAsync(s)).Id, join: true);

        await AssertProblemAsync(await CompleteAsync(s.Tenant.Owner, session.Id), HttpStatusCode.Conflict, "classes.session_not_started");
        await StartNowAsync(session.Id);
        await RecordAsync(s.Tenant.Owner, session.Id, (a.Id, "Attended", null));

        var completed = await ReadRosterAsync(await CompleteAsync(s.Tenant.Owner, session.Id, markPending: true));

        Assert.Equal("Completed", completed.SessionStatus);
        Assert.Equal(1, completed.Attended);
        Assert.Equal(1, completed.NoShow); // el pendiente quedó como «no asistió»
        Assert.Equal(0, completed.Pending);
        Assert.Empty(completed.Waitlist);
        var closed = (await s.Tenant.Owner.GetFromJsonAsync<PagedDto<ReservationRow>>($"/api/v1/class-reservations?sessionId={session.Id}&status=Expired"))!;
        Assert.Equal(waiting.Id, Assert.Single(closed.Items).Id);

        // Completar otra vez devuelve lo mismo; la sesión completada ya no se cancela ni se reprograma; sí se puede corregir la asistencia.
        Assert.Equal("Completed", (await ReadRosterAsync(await CompleteAsync(s.Tenant.Owner, session.Id))).SessionStatus);
        await AssertProblemAsync(await s.Tenant.Owner.PostAsJsonAsync($"/api/v1/class-sessions/{session.Id}/cancel", new { reason = "Tarde" }), HttpStatusCode.Conflict, "classes.session_not_modifiable");
        await AssertProblemAsync(await s.Tenant.Owner.PatchAsJsonAsync($"/api/v1/class-sessions/{session.Id}", new { capacity = 3 }), HttpStatusCode.Conflict, "classes.session_not_modifiable");
        var fixedUp = await ReadRosterAsync(await RecordAsync(s.Tenant.Owner, session.Id, (b.Id, "Attended", "Había llegado")));
        Assert.Equal(2, fixedUp.Attended);

        // Sin marcar pendientes, quedan sin resultado.
        var second = await NewSessionAsync(s.Tenant.Owner, s.Class.Id, Local(DayOfWeek.Tuesday, 9));
        await BookAsync(s.Tenant.Owner, second.Id, (await NewMemberAsync(s)).Id);
        await StartNowAsync(second.Id, minutesAgo: 300); // otra franja: el instructor no puede dar dos sesiones a la vez
        var open = await ReadRosterAsync(await CompleteAsync(s.Tenant.Owner, second.Id));
        Assert.Equal(1, open.Pending);
        Assert.Equal(0, open.NoShow);
    }

    // ---- Historial --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task El_historial_filtra_por_alumno_resultado_sesion_y_fecha_y_el_alumno_ve_solo_el_suyo()
    {
        var s = await NewSetupAsync();
        var (session, a, b, memberA, memberB) = await SessionWithTwoAsync(s);
        var student = await StudentAsync(s, memberA);
        var other = await StudentAsync(s, memberB);
        await StartNowAsync(session.Id);
        await RecordAsync(s.Tenant.Owner, session.Id, (a.Id, "Attended", null), (b.Id, "NoShow", "No avisó"));

        var all = (await s.Tenant.Owner.GetFromJsonAsync<PagedDto<HistoryRow>>("/api/v1/class-attendance"))!;
        Assert.Equal(2, all.TotalCount);
        Assert.Equal([b.Id], (await s.Tenant.Owner.GetFromJsonAsync<PagedDto<HistoryRow>>("/api/v1/class-attendance?outcome=NoShow"))!.Items.Select(i => i.ReservationId));
        Assert.Equal([a.Id], (await s.Tenant.Owner.GetFromJsonAsync<PagedDto<HistoryRow>>($"/api/v1/class-attendance?memberId={memberA.Id}"))!.Items.Select(i => i.ReservationId));
        Assert.Equal(2, (await s.Tenant.Owner.GetFromJsonAsync<PagedDto<HistoryRow>>($"/api/v1/class-attendance?sessionId={session.Id}"))!.TotalCount);
        var from = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddHours(1).ToString("o"));
        Assert.Equal(0, (await s.Tenant.Owner.GetFromJsonAsync<PagedDto<HistoryRow>>($"/api/v1/class-attendance?from={from}"))!.TotalCount);
        var first = all.Items[0];
        Assert.Equal("Yoga matutino", first.ClassName);
        Assert.Equal("Sucursal UIO", first.BranchName);
        Assert.Equal(s.Instructor.FullName, first.InstructorName);

        // El alumno ve su propia participación (con el resultado) y nada de los demás.
        var mine = (await student.GetFromJsonAsync<PagedDto<HistoryRow>>("/api/v1/me/class-attendance"))!;
        var row = Assert.Single(mine.Items);
        Assert.Equal(memberA.Id, row.MemberId);
        Assert.Equal("Attended", row.Outcome);
        var theirs = (await other.GetFromJsonAsync<PagedDto<HistoryRow>>("/api/v1/me/class-attendance"))!;
        Assert.Equal("No avisó", Assert.Single(theirs.Items).Notes);
        Assert.Empty((await other.GetFromJsonAsync<PagedDto<HistoryRow>>("/api/v1/me/class-attendance?outcome=Attended"))!.Items);
    }

    // ---- Permisos, aislamiento y base -------------------------------------------------------------------------------

    [Fact]
    public async Task El_instructor_registra_solo_las_sesiones_que_da_y_cada_rol_ve_lo_que_le_toca()
    {
        var s = await NewSetupAsync();
        var second = await NewTrainerAsync(s.Tenant, "segundo@gym.test", s.Branch);
        var gyeTrainer = await NewTrainerAsync(s.Tenant, "gye@gym.test", s.OtherBranch);
        var gyeClass = await NewClassAsync(s.Tenant.Owner, s.OtherBranch, gyeTrainer, "Yoga GYE");
        var (mine, a, _, _, _) = await SessionWithTwoAsync(s, DayOfWeek.Monday);
        var theirs = await NewSessionAsync(s.Tenant.Owner, s.Class.Id, Local(DayOfWeek.Monday, 11), instructorId: second.Id);
        var theirReservation = await BookAsync(s.Tenant.Owner, theirs.Id, (await NewMemberAsync(s)).Id);
        var inGye = await NewSessionAsync(s.Tenant.Owner, gyeClass.Id, Local(DayOfWeek.Monday, 9));
        await StartNowAsync(mine.Id);
        await StartNowAsync(theirs.Id);

        var trainer = TestApi.Authenticated(api, await TestApi.LoginAsync(api, s.Tenant.Slug, "instructora@gym.test", TestApi.UserPassword));
        var reception = await StaffAsync(s, "recep@gym.test", "Recepcion");
        var admin = await StaffAsync(s, "admin.uio@gym.test", "AdministradorSucursal");
        var cashier = await StaffAsync(s, "cajero@gym.test", "Cajero");

        // El instructor registra la suya, puede ver la lista de la otra pero no registrar en ella.
        Assert.Equal(HttpStatusCode.OK, (await RecordAsync(trainer, mine.Id, (a.Id, "Attended", null))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await trainer.GetAsync($"/api/v1/class-sessions/{theirs.Id}/roster")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await RecordAsync(trainer, theirs.Id, (theirReservation.Id, "Attended", null))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await CompleteAsync(trainer, theirs.Id)).StatusCode);

        // Recepción y administración registran cualquiera de su sucursal; GYE no existe para ellas; caja no tiene permiso.
        Assert.Equal(HttpStatusCode.OK, (await RecordAsync(reception, theirs.Id, (theirReservation.Id, "Attended", null))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/v1/class-sessions/{mine.Id}/roster")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/v1/class-sessions/{inGye.Id}/roster")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await RecordAsync(admin, inGye.Id, (Guid.NewGuid(), "Attended", null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync($"/api/v1/class-sessions/{mine.Id}/roster")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await RecordAsync(cashier, mine.Id, (a.Id, "Attended", null))).StatusCode);
        await AssertProblemAsync(await admin.GetAsync($"/api/v1/class-attendance?branchId={s.OtherBranch}"), HttpStatusCode.Forbidden, "branches.not_allowed");

        // El alumno no entra a nada del personal.
        var student = await StudentAsync(s, await NewMemberAsync(s));
        Assert.Equal(HttpStatusCode.Forbidden, (await student.GetAsync($"/api/v1/class-sessions/{mine.Id}/roster")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await RecordAsync(student, mine.Id, (a.Id, "Attended", null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await student.GetAsync("/api/v1/class-attendance")).StatusCode);
    }

    [Fact]
    public async Task Un_gimnasio_no_ve_ni_registra_la_asistencia_de_otro_y_la_base_lo_garantiza()
    {
        var a = await NewSetupAsync();
        var b = await NewSetupAsync();
        var (session, ra, _, _, _) = await SessionWithTwoAsync(a);
        await StartNowAsync(session.Id);
        await RecordAsync(a.Tenant.Owner, session.Id, (ra.Id, "Attended", null));

        Assert.Equal(HttpStatusCode.NotFound, (await b.Tenant.Owner.GetAsync($"/api/v1/class-sessions/{session.Id}/roster")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await RecordAsync(b.Tenant.Owner, session.Id, (ra.Id, "NoShow", null))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await CompleteAsync(b.Tenant.Owner, session.Id)).StatusCode);
        Assert.Empty((await b.Tenant.Owner.GetFromJsonAsync<PagedDto<HistoryRow>>("/api/v1/class-attendance"))!.Items);
        Assert.Equal(0L, await CountAsync(b.Tenant.Id, "SELECT count(*) FROM classes.class_attendance"));
        Assert.Equal(1L, await CountAsync(a.Tenant.Id, "SELECT count(*) FROM classes.class_attendance"));
        Assert.Equal("Attended", (await a.Tenant.Owner.GetFromJsonAsync<PagedDto<HistoryRow>>("/api/v1/class-attendance"))!.Items.Single().Outcome);
    }

    [Fact]
    public async Task La_base_impone_una_asistencia_por_reserva_confirmada_y_ya_empezada_y_no_deja_cambiar_su_origen()
    {
        var s = await NewSetupAsync();
        var (session, a, b, memberA, memberB) = await SessionWithTwoAsync(s);
        var future = await NewSessionAsync(s.Tenant.Owner, s.Class.Id, Local(DayOfWeek.Wednesday, 9));
        var futureReservation = await BookAsync(s.Tenant.Owner, future.Id, (await NewMemberAsync(s)).Id);
        var waiting = await BookAsync(s.Tenant.Owner, session.Id, (await NewMemberAsync(s)).Id, join: true);
        await StartNowAsync(session.Id);
        await RecordAsync(s.Tenant.Owner, session.Id, (a.Id, "Attended", null));
        await using var admin = new NpgsqlConnection(api.AdminConnection);
        await admin.OpenAsync();

        async Task<string> ErrorAsync(string sql)
        {
            await using var command = new NpgsqlCommand(sql, admin);
            return (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).SqlState;
        }

        string Insert(Guid reservation, Guid sessionId, Guid member, string outcome = "NoShow", string extra = "") => $"""
            INSERT INTO classes.class_attendance (id, tenant_id, branch_id, session_id, reservation_id, member_id, outcome, created_at{(extra.Length > 0 ? ", gym_entry_id" : string.Empty)})
            VALUES (gen_random_uuid(), '{s.Tenant.Id}', '{s.Branch}', '{sessionId}', '{reservation}', '{member}', '{outcome}', now(){extra})
            """;

        // Una por reserva; solo si está confirmada y la sesión ya empezó; y la reserva, la sesión y el alumno tienen que coincidir.
        Assert.Equal(PostgresErrorCodes.UniqueViolation, await ErrorAsync(Insert(a.Id, session.Id, memberA.Id)));
        Assert.Equal("FC006", await ErrorAsync(Insert(waiting.Id, session.Id, waiting.MemberId)));
        Assert.Equal("FC008", await ErrorAsync(Insert(futureReservation.Id, future.Id, futureReservation.MemberId)));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, await ErrorAsync(Insert(b.Id, session.Id, memberA.Id)));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await ErrorAsync(Insert(b.Id, session.Id, memberB.Id, "Presente")));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await ErrorAsync(Insert(b.Id, session.Id, memberB.Id, "NoShow", $", '{Guid.NewGuid()}'")));

        // Lo que ancla la asistencia no cambia, y una reserva con asistencia ya no se cancela. Una sesión completada no se reabre.
        Assert.Equal(PostgresErrorCodes.CheckViolation, await ErrorAsync($"UPDATE classes.class_attendance SET member_id = '{memberB.Id}' WHERE reservation_id = '{a.Id}'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await ErrorAsync($"UPDATE classes.class_attendance SET created_at = now() WHERE reservation_id = '{a.Id}'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await ErrorAsync($"UPDATE classes.class_reservations SET status = 'Cancelled', cancelled_at = now() WHERE id = '{a.Id}'"));
        await CompleteAsync(s.Tenant.Owner, session.Id);
        Assert.Equal(PostgresErrorCodes.CheckViolation, await ErrorAsync($"UPDATE classes.class_sessions SET status = 'Scheduled' WHERE id = '{session.Id}'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await ErrorAsync($"UPDATE classes.class_sessions SET status = 'Cancelled', cancel_reason = 'x', cancelled_at = now() WHERE id = '{session.Id}'"));
    }

    [Fact]
    public async Task El_rol_de_la_aplicacion_no_puede_borrar_asistencias()
    {
        var s = await NewSetupAsync();
        var (session, a, _, _, _) = await SessionWithTwoAsync(s);
        await StartNowAsync(session.Id);
        await RecordAsync(s.Tenant.Owner, session.Id, (a.Id, "Attended", null));
        await using var connection = new NpgsqlConnection(api.AppConnection);
        await connection.OpenAsync();
        await using (var set = new NpgsqlCommand($"SELECT set_config('app.tenant_id', '{s.Tenant.Id}', false)", connection))
        {
            await set.ExecuteScalarAsync();
        }

        await using var delete = new NpgsqlCommand($"DELETE FROM classes.class_attendance WHERE reservation_id = '{a.Id}'", connection);
        var error = await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
    }
}
