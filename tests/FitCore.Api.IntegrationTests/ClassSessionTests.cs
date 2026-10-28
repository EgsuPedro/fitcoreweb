using System.Net;
using System.Net.Http.Json;
using FitCore.Api.IntegrationTests.Infrastructure;
using Npgsql;

namespace FitCore.Api.IntegrationTests;

[Collection(ApiCollection4.Name)]
public class ClassSessionTests(ApiFixture api)
{
    private static readonly TimeSpan Ecuador = TimeSpan.FromHours(-5);

    private sealed record ClassRow(Guid Id, Guid BranchId, Guid InstructorId, int Capacity, int DurationMinutes);

    private sealed record TrainerRow(Guid Id, Guid UserId, string FullName);

    private sealed record SessionRow(
        Guid Id, Guid ClassId, string ClassName, string Activity, Guid BranchId, string BranchName, Guid? SeriesId, Guid InstructorId, string InstructorName,
        DateTimeOffset StartsAt, DateTimeOffset EndsAt, int Capacity, string Status, string? Notes, string? CancelReason, DateTimeOffset? CancelledAt,
        string? AvailabilityOverrideReason, bool IsModified);

    private sealed record SeriesRow(
        Guid Id, Guid ClassId, string ClassName, Guid BranchId, Guid InstructorId, int Capacity, int DurationMinutes, DateOnly StartDate, DateOnly EndDate,
        string[] Days, string Status, string? CancelReason, int TotalSessions, int ScheduledSessions);

    private sealed record SkippedRow(DateOnly Date, string Reason);

    private sealed record RunRow(SeriesRow Series, int Created, int AlreadyExisted, int PastDates, SkippedRow[] Skipped);

    private sealed record CancelSeriesRow(SeriesRow Series, int CancelledSessions);

    private sealed record Scenario(TestTenant Tenant, Guid Branch, Guid OtherBranch, TrainerRow Instructor, ClassRow Class);

    /// <summary>Instante futuro: el próximo día de la semana pedido (al menos 2 días adelante) a la hora local de Ecuador (UTC-5).</summary>
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

    private static object ClassBody(Guid branch, Guid instructor, string name, int duration = 60, int capacity = 20) => new
    {
        branchId = branch,
        name,
        activity = "Yoga",
        instructorId = instructor,
        durationMinutes = duration,
        capacity,
        level = "AllLevels",
        minBookingNoticeMinutes = 30,
        maxBookingAdvanceDays = 30,
        cancellationDeadlineMinutes = 120,
        waitlistEnabled = true,
        waitlistCapacity = 5,
        offerWindowMinutes = 30,
        requireActiveMembership = true,
        applyPaymentPolicy = true
    };

    private async Task<ClassRow> NewClassAsync(TestTenant tenant, Guid branch, TrainerRow instructor, string name = "Yoga matutino", int duration = 60, int capacity = 20)
    {
        var response = await tenant.Owner.PostAsJsonAsync("/api/v1/group-classes", ClassBody(branch, instructor.Id, name, duration, capacity));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ClassRow>())!;
    }

    private async Task<Scenario> NewScenarioAsync()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var branch = await TestApi.CreateBranchAsync(tenant.Owner, "UIO");
        var other = await TestApi.CreateBranchAsync(tenant.Owner, "GYE");
        var instructor = await NewTrainerAsync(tenant, "instructora@gym.test", branch);
        var groupClass = await NewClassAsync(tenant, branch, instructor);
        return new Scenario(tenant, branch, other, instructor, groupClass);
    }

    private static async Task<SessionRow> CreateSessionAsync(HttpClient client, Guid classId, DateTimeOffset startsAt, Guid? instructorId = null, int? capacity = null, string? overrideReason = null)
    {
        var response = await client.PostAsJsonAsync("/api/v1/class-sessions", new { classId, startsAt, instructorId, capacity, availabilityOverrideReason = overrideReason });
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"Respondió {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<SessionRow>())!;
    }

    private static Task<HttpResponseMessage> PostSessionAsync(HttpClient client, Guid classId, DateTimeOffset startsAt, Guid? instructorId = null, string? overrideReason = null) =>
        client.PostAsJsonAsync("/api/v1/class-sessions", new { classId, startsAt, instructorId, availabilityOverrideReason = overrideReason });

    private static object SeriesBody(Guid classId, DateOnly start, DateOnly end, string time = "09:00", string[]? days = null, Guid? instructorId = null) => new
    {
        classId,
        startDate = start,
        endDate = end,
        startTime = time,
        days = days ?? ["Monday", "Wednesday", "Friday"],
        instructorId
    };

    private static async Task<RunRow> CreateSeriesAsync(HttpClient client, object body)
    {
        var response = await client.PostAsJsonAsync("/api/v1/class-sessions/recurrences", body);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"Respondió {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<RunRow>())!;
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, await TestApi.ProblemCodeAsync(response));
    }

    private static async Task<SessionRow[]> SeriesSessionsAsync(HttpClient client, Guid seriesId) =>
        (await client.GetFromJsonAsync<PagedDto<SessionRow>>($"/api/v1/class-sessions?seriesId={seriesId}&pageSize=100"))!.Items;

    private async Task<NpgsqlConnection> OpenAsync(bool asApp, Guid? tenantId)
    {
        var connection = new NpgsqlConnection(asApp ? api.AppConnection : api.AdminConnection);
        await connection.OpenAsync();
        if (tenantId is { } id)
        {
            // Sin tenant fijado la política de RLS oculta todas las filas y las pruebas pasarían en vacío.
            await using var set = new NpgsqlCommand($"SELECT set_config('app.tenant_id', '{id}', false)", connection);
            await set.ExecuteScalarAsync();
        }

        return connection;
    }

    private async Task<long> CountAsync(Guid tenantId, string sql)
    {
        await using var connection = await OpenAsync(asApp: true, tenantId);
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task SetAvailabilityAsync(Scenario s, TrainerRow trainer, params object[] slots)
    {
        var response = await s.Tenant.Owner.PutAsJsonAsync($"/api/v1/trainers/{trainer.Id}/availability", new { slots });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Programar_una_sesion_hereda_sucursal_duracion_capacidad_e_instructor_de_la_clase()
    {
        var s = await NewScenarioAsync();
        var start = Local(DayOfWeek.Monday, 9);

        var session = await CreateSessionAsync(s.Tenant.Owner, s.Class.Id, start);

        Assert.Equal(s.Class.Id, session.ClassId);
        Assert.Equal("Yoga matutino", session.ClassName);
        Assert.Equal("Yoga", session.Activity);
        Assert.Equal(s.Branch, session.BranchId);
        Assert.Equal("Sucursal UIO", session.BranchName);
        Assert.Equal(s.Instructor.Id, session.InstructorId);
        Assert.Equal(s.Instructor.FullName, session.InstructorName);
        Assert.Equal(start, session.StartsAt);
        Assert.Equal(start.AddMinutes(60), session.EndsAt);
        Assert.Equal(20, session.Capacity);
        Assert.Equal("Scheduled", session.Status);
        Assert.Null(session.SeriesId);
        Assert.False(session.IsModified);
        Assert.Equal(session, await s.Tenant.Owner.GetFromJsonAsync<SessionRow>($"/api/v1/class-sessions/{session.Id}"));

        var custom = await CreateSessionAsync(s.Tenant.Owner, s.Class.Id, Local(DayOfWeek.Tuesday, 18), capacity: 8);
        Assert.Equal(8, custom.Capacity);
    }

    [Fact]
    public async Task El_listado_filtra_por_rango_instructor_clase_y_estado_y_ordena_por_fecha()
    {
        var s = await NewScenarioAsync();
        var second = await NewTrainerAsync(s.Tenant, "segundo@gym.test", s.Branch);
        var spinning = await NewClassAsync(s.Tenant, s.Branch, second, "Spinning");
        var late = await CreateSessionAsync(s.Tenant.Owner, s.Class.Id, Local(DayOfWeek.Friday, 18));
        var early = await CreateSessionAsync(s.Tenant.Owner, s.Class.Id, Local(DayOfWeek.Monday, 7));
        var other = await CreateSessionAsync(s.Tenant.Owner, spinning.Id, Local(DayOfWeek.Wednesday, 12));
        await s.Tenant.Owner.PostAsJsonAsync($"/api/v1/class-sessions/{late.Id}/cancel", new { reason = "Feriado" });

        var all = (await s.Tenant.Owner.GetFromJsonAsync<PagedDto<SessionRow>>("/api/v1/class-sessions"))!;
        Assert.Equal(3, all.TotalCount);
        Assert.Equal(all.Items.OrderBy(x => x.StartsAt).Select(x => x.Id), all.Items.Select(x => x.Id));

        Assert.Equal(2, (await s.Tenant.Owner.GetFromJsonAsync<PagedDto<SessionRow>>($"/api/v1/class-sessions?classId={s.Class.Id}"))!.TotalCount);
        Assert.Equal([other.Id], (await s.Tenant.Owner.GetFromJsonAsync<PagedDto<SessionRow>>($"/api/v1/class-sessions?instructorId={second.Id}"))!.Items.Select(x => x.Id));
        Assert.Equal([late.Id], (await s.Tenant.Owner.GetFromJsonAsync<PagedDto<SessionRow>>("/api/v1/class-sessions?status=Cancelled"))!.Items.Select(x => x.Id));

        // El rango incluye el inicio y excluye el fin; las fechas con desfase se normalizan.
        var from = Uri.EscapeDataString(all.Items[0].StartsAt.ToString("o"));
        var to = Uri.EscapeDataString(all.Items[1].StartsAt.ToString("o"));
        Assert.Equal([all.Items[0].Id], (await s.Tenant.Owner.GetFromJsonAsync<PagedDto<SessionRow>>($"/api/v1/class-sessions?from={from}&to={to}"))!.Items.Select(x => x.Id));
        Assert.Equal(3, new[] { early, other, late }.Length);

        var page = (await s.Tenant.Owner.GetFromJsonAsync<PagedDto<SessionRow>>("/api/v1/class-sessions?pageSize=2"))!;
        Assert.Equal(2, page.Items.Length);
        Assert.Equal(3, page.TotalCount);
    }

    [Fact]
    public async Task Las_validaciones_rechazan_pasado_clase_inexistente_o_inactiva_y_datos_fuera_de_rango()
    {
        var s = await NewScenarioAsync();

        await AssertProblemAsync(await PostSessionAsync(s.Tenant.Owner, s.Class.Id, DateTimeOffset.UtcNow.AddHours(-1)), HttpStatusCode.UnprocessableEntity, "classes.session_in_past");
        await AssertProblemAsync(await PostSessionAsync(s.Tenant.Owner, Guid.NewGuid(), Local(DayOfWeek.Monday, 9)), HttpStatusCode.NotFound, "classes.not_found");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await s.Tenant.Owner.PostAsJsonAsync("/api/v1/class-sessions", new { classId = s.Class.Id, startsAt = Local(DayOfWeek.Monday, 9), capacity = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await s.Tenant.Owner.PostAsJsonAsync("/api/v1/class-sessions", new { classId = s.Class.Id, startsAt = Local(DayOfWeek.Monday, 9), capacity = 501 })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await s.Tenant.Owner.PostAsJsonAsync("/api/v1/class-sessions", new { classId = s.Class.Id, startsAt = Local(DayOfWeek.Monday, 9), notes = new string('x', 501) })).StatusCode);

        await s.Tenant.Owner.PatchAsJsonAsync($"/api/v1/group-classes/{s.Class.Id}/status", new { isActive = false });
        await AssertProblemAsync(await PostSessionAsync(s.Tenant.Owner, s.Class.Id, Local(DayOfWeek.Monday, 9)), HttpStatusCode.Conflict, "classes.class_inactive");

        // El instructor de una sesión debe ser válido y trabajar en la sucursal.
        await s.Tenant.Owner.PatchAsJsonAsync($"/api/v1/group-classes/{s.Class.Id}/status", new { isActive = true });
        var elsewhere = await NewTrainerAsync(s.Tenant, "gye@gym.test", s.OtherBranch);
        await AssertProblemAsync(await PostSessionAsync(s.Tenant.Owner, s.Class.Id, Local(DayOfWeek.Monday, 9), elsewhere.Id), HttpStatusCode.UnprocessableEntity, "classes.instructor_not_in_branch");
        await AssertProblemAsync(await PostSessionAsync(s.Tenant.Owner, s.Class.Id, Local(DayOfWeek.Monday, 9), Guid.NewGuid()), HttpStatusCode.UnprocessableEntity, "classes.instructor_invalid");
    }

    [Fact]
    public async Task Un_instructor_no_puede_dar_dos_sesiones_a_la_vez_pero_si_una_a_continuacion_de_otra_y_una_cancelada_libera_el_horario()
    {
        var s = await NewScenarioAsync();
        var second = await NewTrainerAsync(s.Tenant, "segundo@gym.test", s.Branch);
        var spinning = await NewClassAsync(s.Tenant, s.Branch, s.Instructor, "Spinning", duration: 45);
        var first = await CreateSessionAsync(s.Tenant.Owner, s.Class.Id, Local(DayOfWeek.Monday, 9));

        // Mismo horario, parcialmente cruzado y contenido: todos chocan, aunque sea otra clase del mismo instructor.
        await AssertProblemAsync(await PostSessionAsync(s.Tenant.Owner, s.Class.Id, Local(DayOfWeek.Monday, 9)), HttpStatusCode.Conflict, "classes.instructor_overlap");
        await AssertProblemAsync(await PostSessionAsync(s.Tenant.Owner, spinning.Id, Local(DayOfWeek.Monday, 9, 30)), HttpStatusCode.Conflict, "classes.instructor_overlap");
        await AssertProblemAsync(await PostSessionAsync(s.Tenant.Owner, spinning.Id, Local(DayOfWeek.Monday, 8, 30)), HttpStatusCode.Conflict, "classes.instructor_overlap");

        // Pegadas por los dos lados sí caben (el fin de una no cruza con el inicio de la otra), y otro instructor no choca.
        await CreateSessionAsync(s.Tenant.Owner, spinning.Id, Local(DayOfWeek.Monday, 10));
        await CreateSessionAsync(s.Tenant.Owner, spinning.Id, Local(DayOfWeek.Monday, 8, 15));
        await CreateSessionAsync(s.Tenant.Owner, s.Class.Id, Local(DayOfWeek.Monday, 9), second.Id);

        // Cancelar la primera libera el horario.
        Assert.Equal(HttpStatusCode.OK, (await s.Tenant.Owner.PostAsJsonAsync($"/api/v1/class-sessions/{first.Id}/cancel", new { reason = "Instructora enferma" })).StatusCode);
        await CreateSessionAsync(s.Tenant.Owner, s.Class.Id, Local(DayOfWeek.Monday, 9));
    }

    [Fact]
    public async Task Fuera_de_la_disponibilidad_declarada_solo_se_programa_como_excepcion_con_motivo_y_queda_auditada()
    {
        var s = await NewScenarioAsync();
        await SetAvailabilityAsync(s, s.Instructor, new { day = "Monday", start = "08:00", end = "12:00", branchId = s.Branch });

        var inside = await CreateSessionAsync(s.Tenant.Owner, s.Class.Id, Local(DayOfWeek.Monday, 9, weeksAhead: 1), overrideReason: "No hace falta, cabe");
        Assert.Null(inside.AvailabilityOverrideReason);

        // El martes, o el lunes por la tarde, queda fuera.
        await AssertProblemAsync(await PostSessionAsync(s.Tenant.Owner, s.Class.Id, Local(DayOfWeek.Tuesday, 9)), HttpStatusCode.Conflict, "classes.instructor_unavailable");
        await AssertProblemAsync(await PostSessionAsync(s.Tenant.Owner, s.Class.Id, Local(DayOfWeek.Monday, 15)), HttpStatusCode.Conflict, "classes.instructor_unavailable");
        await AssertProblemAsync(await PostSessionAsync(s.Tenant.Owner, s.Class.Id, Local(DayOfWeek.Monday, 11, 30)), HttpStatusCode.Conflict, "classes.instructor_unavailable");

        // Un motivo demasiado corto no vale; uno real convierte el horario en una excepción explícita.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostSessionAsync(s.Tenant.Owner, s.Class.Id, Local(DayOfWeek.Tuesday, 9), overrideReason: "ok")).StatusCode);
        var exception = await CreateSessionAsync(s.Tenant.Owner, s.Class.Id, Local(DayOfWeek.Tuesday, 9), overrideReason: "Reemplazo de última hora acordado con la instructora");
        Assert.Equal("Reemplazo de última hora acordado con la instructora", exception.AvailabilityOverrideReason);

        var audit = (await s.Tenant.Owner.GetFromJsonAsync<PagedDto<AuditDto>>($"/api/v1/audit-logs?entity=ClassSession&entityId={exception.Id}"))!;
        Assert.True(audit.TotalCount >= 1, "La sesión excepcional no quedó en la auditoría");

        // Un instructor sin franjas declaradas no tiene restricción.
        var free = await NewTrainerAsync(s.Tenant, "libre@gym.test", s.Branch);
        await CreateSessionAsync(s.Tenant.Owner, s.Class.Id, Local(DayOfWeek.Sunday, 22), free.Id);
    }

    [Fact]
    public async Task Solicitudes_simultaneas_por_el_mismo_horario_del_instructor_crean_una_sola_sesion()
    {
        var s = await NewScenarioAsync();
        var start = Local(DayOfWeek.Thursday, 17);

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => PostSessionAsync(s.Tenant.Owner, s.Class.Id, start)));

        foreach (var response in responses.Where(r => r.StatusCode != HttpStatusCode.Created))
        {
            Assert.True(response.StatusCode == HttpStatusCode.Conflict, $"Respondió {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(1L, await CountAsync(s.Tenant.Id, $"SELECT count(*) FROM classes.class_sessions WHERE class_id = '{s.Class.Id}'"));
    }

    [Fact]
    public async Task Reprogramar_cambia_hora_instructor_capacidad_y_notas_validando_de_nuevo_solapes_y_disponibilidad()
    {
        var s = await NewScenarioAsync();
        var second = await NewTrainerAsync(s.Tenant, "segundo@gym.test", s.Branch);
        var session = await CreateSessionAsync(s.Tenant.Owner, s.Class.Id, Local(DayOfWeek.Monday, 9));
        var blocker = await CreateSessionAsync(s.Tenant.Owner, s.Class.Id, Local(DayOfWeek.Monday, 14));
        var url = $"/api/v1/class-sessions/{session.Id}";

        var moved = await s.Tenant.Owner.PatchAsJsonAsync(url, new { startsAt = Local(DayOfWeek.Monday, 11), capacity = 12, notes = "Sala 2" });
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        var after = (await moved.Content.ReadFromJsonAsync<SessionRow>())!;
        Assert.Equal(Local(DayOfWeek.Monday, 11), after.StartsAt);
        Assert.Equal(Local(DayOfWeek.Monday, 12), after.EndsAt);
        Assert.Equal(12, after.Capacity);
        Assert.Equal("Sala 2", after.Notes);

        // Cambiar a un horario ocupado, o a otro instructor, se valida igual que al crear.
        await AssertProblemAsync(await s.Tenant.Owner.PatchAsJsonAsync(url, new { startsAt = Local(DayOfWeek.Monday, 14, 30) }), HttpStatusCode.Conflict, "classes.instructor_overlap");
        var withSecond = await s.Tenant.Owner.PatchAsJsonAsync(url, new { instructorId = second.Id });
        Assert.Equal(HttpStatusCode.OK, withSecond.StatusCode);
        Assert.Equal(second.Id, (await withSecond.Content.ReadFromJsonAsync<SessionRow>())!.InstructorId);
        await AssertProblemAsync(await s.Tenant.Owner.PatchAsJsonAsync(url, new { instructorId = Guid.NewGuid() }), HttpStatusCode.UnprocessableEntity, "classes.instructor_invalid");

        // Las notas vacías se borran; sin ningún dato no hay nada que cambiar; al pasado no se mueve.
        var cleared = (await (await s.Tenant.Owner.PatchAsJsonAsync(url, new { notes = "" })).Content.ReadFromJsonAsync<SessionRow>())!;
        Assert.Null(cleared.Notes);
        await AssertProblemAsync(await s.Tenant.Owner.PatchAsJsonAsync(url, new { }), HttpStatusCode.UnprocessableEntity, "classes.session_nothing_to_change");
        await AssertProblemAsync(await s.Tenant.Owner.PatchAsJsonAsync(url, new { startsAt = DateTimeOffset.UtcNow.AddHours(-2) }), HttpStatusCode.UnprocessableEntity, "classes.session_in_past");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await s.Tenant.Owner.PatchAsJsonAsync(url, new { capacity = 0 })).StatusCode);
        Assert.Equal(blocker.StartsAt, (await s.Tenant.Owner.GetFromJsonAsync<SessionRow>($"/api/v1/class-sessions/{blocker.Id}"))!.StartsAt);

        // Mover fuera de la disponibilidad pide el motivo.
        await SetAvailabilityAsync(s, second, new { day = "Monday", start = "08:00", end = "12:00", branchId = s.Branch });
        await AssertProblemAsync(await s.Tenant.Owner.PatchAsJsonAsync(url, new { startsAt = Local(DayOfWeek.Monday, 16) }), HttpStatusCode.Conflict, "classes.instructor_unavailable");
        var excepted = (await (await s.Tenant.Owner.PatchAsJsonAsync(url, new { startsAt = Local(DayOfWeek.Monday, 16), availabilityOverrideReason = "Recuperación de clase perdida" })).Content.ReadFromJsonAsync<SessionRow>())!;
        Assert.Equal("Recuperación de clase perdida", excepted.AvailabilityOverrideReason);
    }

    [Fact]
    public async Task Cancelar_exige_motivo_es_idempotente_y_una_sesion_cancelada_no_se_modifica()
    {
        var s = await NewScenarioAsync();
        var session = await CreateSessionAsync(s.Tenant.Owner, s.Class.Id, Local(DayOfWeek.Wednesday, 9));
        var cancel = $"/api/v1/class-sessions/{session.Id}/cancel";

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await s.Tenant.Owner.PostAsJsonAsync(cancel, new { reason = "" })).StatusCode);

        var cancelled = (await (await s.Tenant.Owner.PostAsJsonAsync(cancel, new { reason = "  Sala en mantenimiento " })).Content.ReadFromJsonAsync<SessionRow>())!;
        Assert.Equal("Cancelled", cancelled.Status);
        Assert.Equal("Sala en mantenimiento", cancelled.CancelReason);
        Assert.NotNull(cancelled.CancelledAt);

        // Repetir la cancelación devuelve lo mismo y conserva el motivo original.
        var again = (await (await s.Tenant.Owner.PostAsJsonAsync(cancel, new { reason = "Otro motivo" })).Content.ReadFromJsonAsync<SessionRow>())!;
        Assert.Equal(cancelled.CancelReason, again.CancelReason);
        // PostgreSQL guarda microsegundos: la lectura puede diferir en la fracción que .NET maneja de más.
        Assert.True((cancelled.CancelledAt!.Value - again.CancelledAt!.Value).Duration() < TimeSpan.FromMilliseconds(1));

        await AssertProblemAsync(await s.Tenant.Owner.PatchAsJsonAsync($"/api/v1/class-sessions/{session.Id}", new { capacity = 5 }), HttpStatusCode.Conflict, "classes.session_not_modifiable");
        Assert.Equal(HttpStatusCode.NotFound, (await s.Tenant.Owner.PostAsJsonAsync($"/api/v1/class-sessions/{Guid.NewGuid()}/cancel", new { reason = "No existe" })).StatusCode);
    }

    [Fact]
    public async Task Una_serie_semanal_genera_sesiones_concretas_en_hora_local_y_regenerarla_no_duplica()
    {
        var s = await NewScenarioAsync();
        var start = DateOnly.FromDateTime(Local(DayOfWeek.Monday, 0).DateTime);
        var end = start.AddDays(27);

        var run = await CreateSeriesAsync(s.Tenant.Owner, SeriesBody(s.Class.Id, start, end));

        // Lunes, miércoles y viernes durante cuatro semanas.
        Assert.Equal(12, run.Created);
        Assert.Equal(0, run.AlreadyExisted);
        Assert.Empty(run.Skipped);
        Assert.Equal(12, run.Series.TotalSessions);
        Assert.Equal(12, run.Series.ScheduledSessions);
        Assert.Equal(["Monday", "Wednesday", "Friday"], run.Series.Days);
        Assert.Equal(60, run.Series.DurationMinutes);

        var sessions = await SeriesSessionsAsync(s.Tenant.Owner, run.Series.Id);
        Assert.Equal(12, sessions.Length);
        Assert.All(sessions, x =>
        {
            Assert.Equal(run.Series.Id, x.SeriesId);
            Assert.Equal(TimeSpan.FromHours(14), x.StartsAt.UtcDateTime.TimeOfDay); // 09:00 de Ecuador
        });
        Assert.Equal(12, sessions.Select(x => x.StartsAt).Distinct().Count());

        // Regenerar cualquier número de veces no crea nada nuevo.
        for (var i = 0; i < 3; i++)
        {
            var again = (await (await s.Tenant.Owner.PostAsJsonAsync($"/api/v1/class-sessions/recurrences/{run.Series.Id}/generate", new { })).Content.ReadFromJsonAsync<RunRow>())!;
            Assert.Equal(0, again.Created);
            Assert.Equal(12, again.AlreadyExisted);
        }

        Assert.Equal(12L, await CountAsync(s.Tenant.Id, $"SELECT count(*) FROM classes.class_sessions WHERE series_id = '{run.Series.Id}'"));
    }

    [Fact]
    public async Task Una_sesion_movida_o_cancelada_de_la_serie_no_reaparece_al_regenerar_y_la_serie_se_puede_extender_pero_no_acortar()
    {
        var s = await NewScenarioAsync();
        var start = DateOnly.FromDateTime(Local(DayOfWeek.Monday, 0).DateTime);
        var run = await CreateSeriesAsync(s.Tenant.Owner, SeriesBody(s.Class.Id, start, start.AddDays(13), days: ["Monday", "Thursday"]));
        Assert.Equal(4, run.Created);
        var sessions = await SeriesSessionsAsync(s.Tenant.Owner, run.Series.Id);
        var generate = $"/api/v1/class-sessions/recurrences/{run.Series.Id}/generate";

        var moved = (await (await s.Tenant.Owner.PatchAsJsonAsync($"/api/v1/class-sessions/{sessions[0].Id}", new { startsAt = sessions[0].StartsAt.AddHours(3) })).Content.ReadFromJsonAsync<SessionRow>())!;
        Assert.True(moved.IsModified);
        await s.Tenant.Owner.PostAsJsonAsync($"/api/v1/class-sessions/{sessions[1].Id}/cancel", new { reason = "Feriado" });

        var regenerated = (await (await s.Tenant.Owner.PostAsJsonAsync(generate, new { })).Content.ReadFromJsonAsync<RunRow>())!;
        Assert.Equal(0, regenerated.Created);
        Assert.Equal(4, regenerated.AlreadyExisted);
        Assert.Equal(4, regenerated.Series.TotalSessions);
        Assert.Equal(3, regenerated.Series.ScheduledSessions);

        // Extender dos semanas más solo agrega las fechas nuevas.
        var extended = (await (await s.Tenant.Owner.PostAsJsonAsync(generate, new { endDate = start.AddDays(27) })).Content.ReadFromJsonAsync<RunRow>())!;
        Assert.Equal(4, extended.Created);
        Assert.Equal(4, extended.AlreadyExisted);
        Assert.Equal(start.AddDays(27), extended.Series.EndDate);
        Assert.Equal(8, extended.Series.TotalSessions);

        await AssertProblemAsync(await s.Tenant.Owner.PostAsJsonAsync(generate, new { endDate = start.AddDays(10) }), HttpStatusCode.UnprocessableEntity, "classes.series_cannot_shrink");
        await AssertProblemAsync(await s.Tenant.Owner.PostAsJsonAsync(generate, new { endDate = start.AddDays(400) }), HttpStatusCode.UnprocessableEntity, "classes.series_range_invalid");
    }

    [Fact]
    public async Task Las_fechas_en_conflicto_se_informan_con_su_motivo_y_si_ninguna_se_puede_programar_no_se_crea_la_serie()
    {
        var s = await NewScenarioAsync();
        var monday = Local(DayOfWeek.Monday, 9);
        var start = DateOnly.FromDateTime(monday.DateTime);
        // Choca con el miércoles de la primera semana de la serie.
        await CreateSessionAsync(s.Tenant.Owner, s.Class.Id, new DateTimeOffset(start.AddDays(2).ToDateTime(new TimeOnly(9, 30)), Ecuador));

        var run = await CreateSeriesAsync(s.Tenant.Owner, SeriesBody(s.Class.Id, start, start.AddDays(6)));

        Assert.Equal(2, run.Created);
        var wednesday = Assert.Single(run.Skipped);
        Assert.Equal("instructor_overlap", wednesday.Reason);
        Assert.Equal(DayOfWeek.Wednesday, wednesday.Date.DayOfWeek);

        // Con franjas declaradas solo los lunes por la mañana, las demás fechas se omiten por disponibilidad.
        var second = await NewTrainerAsync(s.Tenant, "segundo@gym.test", s.Branch);
        await SetAvailabilityAsync(s, second, new { day = "Monday", start = "08:00", end = "12:00", branchId = s.Branch });
        var restricted = await CreateSeriesAsync(s.Tenant.Owner, SeriesBody(s.Class.Id, start.AddDays(7), start.AddDays(13), instructorId: second.Id));
        Assert.Equal(1, restricted.Created);
        Assert.Equal(2, restricted.Skipped.Count(x => x.Reason == "instructor_unavailable"));

        // Si todas las fechas chocan, la serie no queda a medias en la base.
        var before = await CountAsync(s.Tenant.Id, "SELECT count(*) FROM classes.class_series");
        await AssertProblemAsync(
            await s.Tenant.Owner.PostAsJsonAsync("/api/v1/class-sessions/recurrences", SeriesBody(s.Class.Id, start.AddDays(7), start.AddDays(13), days: ["Tuesday"], instructorId: second.Id)),
            HttpStatusCode.Conflict, "classes.series_no_sessions");
        Assert.Equal(before, await CountAsync(s.Tenant.Id, "SELECT count(*) FROM classes.class_series"));
    }

    [Fact]
    public async Task Los_datos_de_la_serie_se_validan()
    {
        var s = await NewScenarioAsync();
        var start = DateOnly.FromDateTime(Local(DayOfWeek.Monday, 0).DateTime);
        var url = "/api/v1/class-sessions/recurrences";

        await AssertProblemAsync(await s.Tenant.Owner.PostAsJsonAsync(url, SeriesBody(s.Class.Id, start, start.AddDays(-1))), HttpStatusCode.UnprocessableEntity, "classes.series_range_invalid");
        await AssertProblemAsync(await s.Tenant.Owner.PostAsJsonAsync(url, SeriesBody(s.Class.Id, start, start.AddDays(367))), HttpStatusCode.UnprocessableEntity, "classes.series_range_invalid");
        await AssertProblemAsync(await s.Tenant.Owner.PostAsJsonAsync(url, SeriesBody(s.Class.Id, start.AddDays(-30), start.AddDays(-2))), HttpStatusCode.UnprocessableEntity, "classes.series_in_past");
        await AssertProblemAsync(await s.Tenant.Owner.PostAsJsonAsync(url, SeriesBody(s.Class.Id, start, start.AddDays(300), days: ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday"])), HttpStatusCode.UnprocessableEntity, "classes.series_too_many_sessions");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await s.Tenant.Owner.PostAsJsonAsync(url, SeriesBody(s.Class.Id, start, start.AddDays(7), days: []))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Tenant.Owner.PostAsJsonAsync(url, SeriesBody(Guid.NewGuid(), start, start.AddDays(7)))).StatusCode);

        await s.Tenant.Owner.PatchAsJsonAsync($"/api/v1/group-classes/{s.Class.Id}/status", new { isActive = false });
        await AssertProblemAsync(await s.Tenant.Owner.PostAsJsonAsync(url, SeriesBody(s.Class.Id, start, start.AddDays(7))), HttpStatusCode.Conflict, "classes.class_inactive");
    }

    [Fact]
    public async Task Regenerar_o_crear_la_misma_serie_en_paralelo_no_duplica_sesiones()
    {
        var s = await NewScenarioAsync();
        var start = DateOnly.FromDateTime(Local(DayOfWeek.Monday, 0).DateTime);
        var run = await CreateSeriesAsync(s.Tenant.Owner, SeriesBody(s.Class.Id, start, start.AddDays(6), days: ["Monday", "Friday"]));
        Assert.Equal(2, run.Created);

        // Seis solicitudes extienden la misma serie a la vez: gana una por fecha, las demás ven lo ya creado.
        var generate = $"/api/v1/class-sessions/recurrences/{run.Series.Id}/generate";
        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => s.Tenant.Owner.PostAsJsonAsync(generate, new { endDate = start.AddDays(27) })));

        foreach (var response in responses)
        {
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"Respondió {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }

        var results = await Task.WhenAll(responses.Select(r => r.Content.ReadFromJsonAsync<RunRow>()));
        Assert.Equal(6, results.Sum(r => r!.Created));
        Assert.Equal(8L, await CountAsync(s.Tenant.Id, $"SELECT count(*) FROM classes.class_sessions WHERE series_id = '{run.Series.Id}'"));
        Assert.Equal(8L, await CountAsync(s.Tenant.Id, $"SELECT count(DISTINCT original_starts_at) FROM classes.class_sessions WHERE series_id = '{run.Series.Id}'"));

        // Dos series idénticas a la vez: el instructor no puede estar en dos sitios, así que las sesiones solo existen una vez.
        var other = DateOnly.FromDateTime(Local(DayOfWeek.Tuesday, 0, weeksAhead: 1).DateTime);
        var racing = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => s.Tenant.Owner.PostAsJsonAsync("/api/v1/class-sessions/recurrences", SeriesBody(s.Class.Id, other, other.AddDays(6), "18:00", ["Tuesday"]))));
        foreach (var response in racing.Where(r => r.StatusCode != HttpStatusCode.Created))
        {
            Assert.True(response.StatusCode == HttpStatusCode.Conflict, $"Respondió {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }

        Assert.Equal(1, racing.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(1L, await CountAsync(s.Tenant.Id, $"SELECT count(*) FROM classes.class_sessions WHERE starts_at = '{Local(DayOfWeek.Tuesday, 18, weeksAhead: 1).UtcDateTime:O}'"));
    }

    [Fact]
    public async Task Cancelar_una_serie_cancela_sus_sesiones_futuras_programadas_y_no_se_puede_regenerar()
    {
        var s = await NewScenarioAsync();
        var start = DateOnly.FromDateTime(Local(DayOfWeek.Monday, 0).DateTime);
        var run = await CreateSeriesAsync(s.Tenant.Owner, SeriesBody(s.Class.Id, start, start.AddDays(13), days: ["Monday", "Tuesday"]));
        Assert.Equal(4, run.Created);
        var sessions = await SeriesSessionsAsync(s.Tenant.Owner, run.Series.Id);
        await s.Tenant.Owner.PostAsJsonAsync($"/api/v1/class-sessions/{sessions[0].Id}/cancel", new { reason = "Antes de la serie" });
        var url = $"/api/v1/class-sessions/recurrences/{run.Series.Id}";

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await s.Tenant.Owner.PostAsJsonAsync($"{url}/cancel", new { reason = "" })).StatusCode);
        var cancelled = (await (await s.Tenant.Owner.PostAsJsonAsync($"{url}/cancel", new { reason = "El instructor se va" })).Content.ReadFromJsonAsync<CancelSeriesRow>())!;

        Assert.Equal(3, cancelled.CancelledSessions);
        Assert.Equal("Cancelled", cancelled.Series.Status);
        Assert.Equal("El instructor se va", cancelled.Series.CancelReason);
        Assert.Equal(0, cancelled.Series.ScheduledSessions);
        Assert.All(await SeriesSessionsAsync(s.Tenant.Owner, run.Series.Id), x => Assert.Equal("Cancelled", x.Status));
        Assert.Equal("Antes de la serie", (await s.Tenant.Owner.GetFromJsonAsync<SessionRow>($"/api/v1/class-sessions/{sessions[0].Id}"))!.CancelReason);

        // Idempotente: repetirlo no cancela nada más; regenerarla se rechaza.
        Assert.Equal(0, (await (await s.Tenant.Owner.PostAsJsonAsync($"{url}/cancel", new { reason = "Otra vez" })).Content.ReadFromJsonAsync<CancelSeriesRow>())!.CancelledSessions);
        await AssertProblemAsync(await s.Tenant.Owner.PostAsJsonAsync($"{url}/generate", new { }), HttpStatusCode.Conflict, "classes.series_cancelled");
        Assert.Equal(HttpStatusCode.NotFound, (await s.Tenant.Owner.GetAsync($"/api/v1/class-sessions/recurrences/{Guid.NewGuid()}")).StatusCode);

        // El horario liberado queda disponible para el instructor.
        await CreateSessionAsync(s.Tenant.Owner, s.Class.Id, sessions[1].StartsAt);
    }

    [Fact]
    public async Task El_listado_de_series_filtra_por_clase_y_estado()
    {
        var s = await NewScenarioAsync();
        var second = await NewTrainerAsync(s.Tenant, "segundo@gym.test", s.Branch);
        var spinning = await NewClassAsync(s.Tenant, s.Branch, second, "Spinning");
        var start = DateOnly.FromDateTime(Local(DayOfWeek.Monday, 0).DateTime);
        var first = await CreateSeriesAsync(s.Tenant.Owner, SeriesBody(s.Class.Id, start, start.AddDays(6)));
        var other = await CreateSeriesAsync(s.Tenant.Owner, SeriesBody(spinning.Id, start, start.AddDays(6)));
        await s.Tenant.Owner.PostAsJsonAsync($"/api/v1/class-sessions/recurrences/{other.Series.Id}/cancel", new { reason = "Cierre" });

        Assert.Equal(2, (await s.Tenant.Owner.GetFromJsonAsync<PagedDto<SeriesRow>>("/api/v1/class-sessions/recurrences"))!.TotalCount);
        Assert.Equal([first.Series.Id], (await s.Tenant.Owner.GetFromJsonAsync<PagedDto<SeriesRow>>("/api/v1/class-sessions/recurrences?status=Active"))!.Items.Select(x => x.Id));
        Assert.Equal([other.Series.Id], (await s.Tenant.Owner.GetFromJsonAsync<PagedDto<SeriesRow>>($"/api/v1/class-sessions/recurrences?classId={spinning.Id}"))!.Items.Select(x => x.Id));
        var read = (await s.Tenant.Owner.GetFromJsonAsync<SeriesRow>($"/api/v1/class-sessions/recurrences/{first.Series.Id}"))!;
        Assert.Equal("Yoga matutino", read.ClassName);
        Assert.Equal(3, read.TotalSessions);
    }

    [Fact]
    public async Task Cada_rol_ve_y_programa_solo_lo_que_su_permiso_y_sus_sucursales_permiten()
    {
        var s = await NewScenarioAsync();
        var gyeTrainer = await NewTrainerAsync(s.Tenant, "gye@gym.test", s.OtherBranch);
        var gyeClass = await NewClassAsync(s.Tenant, s.OtherBranch, gyeTrainer, "Yoga GYE");
        var inUio = await CreateSessionAsync(s.Tenant.Owner, s.Class.Id, Local(DayOfWeek.Monday, 9));
        var inGye = await CreateSessionAsync(s.Tenant.Owner, gyeClass.Id, Local(DayOfWeek.Monday, 9));
        var start = DateOnly.FromDateTime(Local(DayOfWeek.Monday, 0).DateTime);
        var gyeSeries = await CreateSeriesAsync(s.Tenant.Owner, SeriesBody(gyeClass.Id, start, start.AddDays(6)));

        async Task<HttpClient> ClientAsync(string email, string role, Guid branch)
        {
            await TestApi.CreateUserAsync(s.Tenant.Owner, email, role, branch);
            return TestApi.Authenticated(api, await TestApi.LoginAsync(api, s.Tenant.Slug, email, TestApi.UserPassword));
        }

        var admin = await ClientAsync("admin.uio@gym.test", "AdministradorSucursal", s.Branch);
        var reception = await ClientAsync("recep.uio@gym.test", "Recepcion", s.Branch);
        var cashier = await ClientAsync("cajero.uio@gym.test", "Cajero", s.Branch);

        // El administrador de UIO programa en UIO; todo lo de GYE no existe para él.
        Assert.Equal([inUio.Id], (await admin.GetFromJsonAsync<PagedDto<SessionRow>>("/api/v1/class-sessions"))!.Items.Select(x => x.Id));
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/v1/class-sessions/{inUio.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/v1/class-sessions/{inGye.Id}")).StatusCode);
        await AssertProblemAsync(await admin.GetAsync($"/api/v1/class-sessions?branchId={s.OtherBranch}"), HttpStatusCode.Forbidden, "branches.not_allowed");
        Assert.Equal(HttpStatusCode.NotFound, (await PostSessionAsync(admin, gyeClass.Id, Local(DayOfWeek.Tuesday, 9))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PatchAsJsonAsync($"/api/v1/class-sessions/{inGye.Id}", new { capacity = 3 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync($"/api/v1/class-sessions/{inGye.Id}/cancel", new { reason = "Intruso" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/v1/class-sessions/recurrences/{gyeSeries.Series.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync($"/api/v1/class-sessions/recurrences/{gyeSeries.Series.Id}/generate", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync($"/api/v1/class-sessions/recurrences/{gyeSeries.Series.Id}/cancel", new { reason = "Intruso" })).StatusCode);
        Assert.Empty((await admin.GetFromJsonAsync<PagedDto<SeriesRow>>("/api/v1/class-sessions/recurrences"))!.Items);
        Assert.Equal(HttpStatusCode.Created, (await PostSessionAsync(admin, s.Class.Id, Local(DayOfWeek.Tuesday, 9))).StatusCode);

        // Recepción y el entrenador consultan sin programar; caja no tiene ni lectura.
        Assert.Equal(HttpStatusCode.OK, (await reception.GetAsync("/api/v1/class-sessions")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostSessionAsync(reception, s.Class.Id, Local(DayOfWeek.Friday, 9))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reception.PostAsJsonAsync($"/api/v1/class-sessions/{inUio.Id}/cancel", new { reason = "No puede" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync("/api/v1/class-sessions")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync("/api/v1/class-sessions/recurrences")).StatusCode);
        var trainerClient = TestApi.Authenticated(api, await TestApi.LoginAsync(api, s.Tenant.Slug, "instructora@gym.test", TestApi.UserPassword));
        Assert.Equal(HttpStatusCode.OK, (await trainerClient.GetAsync("/api/v1/class-sessions")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await trainerClient.PatchAsJsonAsync($"/api/v1/class-sessions/{inUio.Id}", new { capacity = 5 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await trainerClient.PostAsJsonAsync("/api/v1/class-sessions/recurrences", SeriesBody(s.Class.Id, start, start.AddDays(6)))).StatusCode);
    }

    [Fact]
    public async Task Un_gimnasio_no_ve_ni_modifica_las_sesiones_de_otro_y_la_base_lo_garantiza()
    {
        var a = await NewScenarioAsync();
        var b = await NewScenarioAsync();
        var session = await CreateSessionAsync(a.Tenant.Owner, a.Class.Id, Local(DayOfWeek.Monday, 9));
        var start = DateOnly.FromDateTime(Local(DayOfWeek.Monday, 0).DateTime);
        var series = await CreateSeriesAsync(a.Tenant.Owner, SeriesBody(a.Class.Id, start.AddDays(7), start.AddDays(13)));

        Assert.Equal(HttpStatusCode.NotFound, (await b.Tenant.Owner.GetAsync($"/api/v1/class-sessions/{session.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Tenant.Owner.PatchAsJsonAsync($"/api/v1/class-sessions/{session.Id}", new { capacity = 2 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Tenant.Owner.PostAsJsonAsync($"/api/v1/class-sessions/{session.Id}/cancel", new { reason = "Ajeno" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Tenant.Owner.GetAsync($"/api/v1/class-sessions/recurrences/{series.Series.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostSessionAsync(b.Tenant.Owner, a.Class.Id, Local(DayOfWeek.Tuesday, 9))).StatusCode);
        Assert.Empty((await b.Tenant.Owner.GetFromJsonAsync<PagedDto<SessionRow>>("/api/v1/class-sessions"))!.Items);

        // Con el rol de la aplicación y el gimnasio B fijado, las filas de A no existen; con A fijado, sí (la prueba no pasa en vacío).
        Assert.Equal(0L, await CountAsync(b.Tenant.Id, "SELECT count(*) FROM classes.class_sessions"));
        Assert.Equal(0L, await CountAsync(b.Tenant.Id, "SELECT count(*) FROM classes.class_series"));
        Assert.Equal(4L, await CountAsync(a.Tenant.Id, "SELECT count(*) FROM classes.class_sessions"));
        Assert.Equal(1L, await CountAsync(a.Tenant.Id, "SELECT count(*) FROM classes.class_series"));

        // Y una sesión de A con la clase o el instructor de B la rechaza la clave foránea compuesta, aunque se salte la aplicación.
        await using var admin = await OpenAsync(asApp: false, null);
        async Task<string> ErrorAsync(string sql)
        {
            await using var command = new NpgsqlCommand(sql, admin);
            return (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).SqlState;
        }

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, await ErrorAsync(Insert(a.Tenant.Id, a.Branch, b.Class.Id, a.Instructor.Id, "now() + interval '30 days'")));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, await ErrorAsync(Insert(a.Tenant.Id, a.Branch, a.Class.Id, b.Instructor.Id, "now() + interval '30 days'")));
    }

    private static string Insert(Guid tenant, Guid branch, Guid classId, Guid instructor, string startsAt, string status = "Scheduled", string? duration = null) => $"""
        INSERT INTO classes.class_sessions (id, tenant_id, branch_id, class_id, instructor_id, starts_at, ends_at, original_starts_at, capacity, status, created_at)
        VALUES (gen_random_uuid(), '{tenant}', '{branch}', '{classId}', '{instructor}', {startsAt}, {startsAt} + interval '{duration ?? "1 hour"}', {startsAt}, 10, '{status}', now())
        """;

    [Fact]
    public async Task La_base_rechaza_solapes_datos_incoherentes_y_cambios_prohibidos_aunque_se_salte_la_aplicacion()
    {
        var s = await NewScenarioAsync();
        var gye = await NewTrainerAsync(s.Tenant, "gye@gym.test", s.OtherBranch);
        var gyeClass = await NewClassAsync(s.Tenant, s.OtherBranch, gye, "Yoga GYE");
        var session = await CreateSessionAsync(s.Tenant.Owner, s.Class.Id, Local(DayOfWeek.Monday, 9));
        var start = DateOnly.FromDateTime(Local(DayOfWeek.Monday, 0).DateTime);
        var series = await CreateSeriesAsync(s.Tenant.Owner, SeriesBody(s.Class.Id, start.AddDays(7), start.AddDays(13), days: ["Wednesday"]));
        var seriesSession = (await SeriesSessionsAsync(s.Tenant.Owner, series.Series.Id)).Single();
        await using var admin = await OpenAsync(asApp: false, s.Tenant.Id);

        async Task<string> ErrorAsync(string sql)
        {
            await using var command = new NpgsqlCommand(sql, admin);
            return (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).SqlState;
        }

        var at = $"'{session.StartsAt.AddMinutes(30):O}'::timestamptz";

        // El mismo instructor no puede solaparse en la base, y el estado cancelado sí libera el hueco.
        Assert.Equal(PostgresErrorCodes.ExclusionViolation, await ErrorAsync(Insert(s.Tenant.Id, s.Branch, s.Class.Id, s.Instructor.Id, at)));
        await using (var cancelled = new NpgsqlCommand($"""
            INSERT INTO classes.class_sessions (id, tenant_id, branch_id, class_id, instructor_id, starts_at, ends_at, original_starts_at, capacity, status, cancel_reason, cancelled_at, created_at)
            VALUES (gen_random_uuid(), '{s.Tenant.Id}', '{s.Branch}', '{s.Class.Id}', '{s.Instructor.Id}', {at}, {at} + interval '1 hour', {at}, 10, 'Cancelled', 'x', now(), now())
            """, admin))
        {
            Assert.Equal(1, await cancelled.ExecuteNonQueryAsync());
        }

        // Rangos y estados inválidos, motivo de cancelación incoherente, sucursal distinta a la de la clase.
        Assert.Equal(PostgresErrorCodes.CheckViolation, await ErrorAsync($"UPDATE classes.class_sessions SET ends_at = starts_at WHERE id = '{session.Id}'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await ErrorAsync($"UPDATE classes.class_sessions SET capacity = 0 WHERE id = '{session.Id}'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await ErrorAsync($"UPDATE classes.class_sessions SET status = 'Abierta' WHERE id = '{session.Id}'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await ErrorAsync($"UPDATE classes.class_sessions SET status = 'Cancelled' WHERE id = '{session.Id}'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await ErrorAsync($"UPDATE classes.class_sessions SET availability_override_reason = 'no' WHERE id = '{session.Id}'"));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, await ErrorAsync(Insert(s.Tenant.Id, s.OtherBranch, s.Class.Id, gye.Id, "now() + interval '40 days'")));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, await ErrorAsync(Insert(s.Tenant.Id, s.Branch, gyeClass.Id, s.Instructor.Id, "now() + interval '41 days'")));

        // Lo que ancla la sesión no cambia; una cancelación no se deshace.
        Assert.Equal(PostgresErrorCodes.CheckViolation, await ErrorAsync($"UPDATE classes.class_sessions SET branch_id = '{s.OtherBranch}' WHERE id = '{session.Id}'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await ErrorAsync($"UPDATE classes.class_sessions SET original_starts_at = now() WHERE id = '{session.Id}'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await ErrorAsync($"UPDATE classes.class_sessions SET series_id = NULL WHERE id = '{seriesSession.Id}'"));
        await s.Tenant.Owner.PostAsJsonAsync($"/api/v1/class-sessions/{session.Id}/cancel", new { reason = "Prueba" });
        Assert.Equal(PostgresErrorCodes.CheckViolation, await ErrorAsync($"UPDATE classes.class_sessions SET status = 'Scheduled', cancel_reason = NULL, cancelled_at = NULL WHERE id = '{session.Id}'"));

        // Un hueco de la serie existe una sola vez.
        Assert.Equal(PostgresErrorCodes.UniqueViolation, await ErrorAsync($"""
            INSERT INTO classes.class_sessions (id, tenant_id, branch_id, class_id, series_id, instructor_id, starts_at, ends_at, original_starts_at, capacity, status, created_at)
            VALUES (gen_random_uuid(), '{s.Tenant.Id}', '{s.Branch}', '{s.Class.Id}', '{series.Series.Id}', '{gye.Id}', now() + interval '50 days', now() + interval '50 days 1 hour',
                    '{seriesSession.StartsAt:O}', 10, 'Scheduled', now())
            """));

        // La serie: rango, días y capacidad válidos; su regla no cambia y solo se extiende.
        Assert.Equal(PostgresErrorCodes.CheckViolation, await ErrorAsync($"UPDATE classes.class_series SET days_mask = 0 WHERE id = '{series.Series.Id}'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await ErrorAsync($"UPDATE classes.class_series SET end_date = start_date + 400 WHERE id = '{series.Series.Id}'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await ErrorAsync($"UPDATE classes.class_series SET start_time = '10:00' WHERE id = '{series.Series.Id}'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await ErrorAsync($"UPDATE classes.class_series SET end_date = end_date - 1 WHERE id = '{series.Series.Id}'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await ErrorAsync($"UPDATE classes.class_series SET status = 'Cancelled' WHERE id = '{series.Series.Id}'"));
    }

    [Fact]
    public async Task El_rol_de_la_aplicacion_no_puede_borrar_sesiones_ni_series()
    {
        var s = await NewScenarioAsync();
        var session = await CreateSessionAsync(s.Tenant.Owner, s.Class.Id, Local(DayOfWeek.Monday, 9));
        var start = DateOnly.FromDateTime(Local(DayOfWeek.Monday, 0).DateTime);
        var series = await CreateSeriesAsync(s.Tenant.Owner, SeriesBody(s.Class.Id, start.AddDays(7), start.AddDays(13)));
        await using var connection = await OpenAsync(asApp: true, s.Tenant.Id);

        foreach (var sql in new[] { $"DELETE FROM classes.class_sessions WHERE id = '{session.Id}'", $"DELETE FROM classes.class_series WHERE id = '{series.Series.Id}'" })
        {
            await using var command = new NpgsqlCommand(sql, connection);
            var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        }
    }

    [Fact]
    public async Task Programar_reprogramar_y_cancelar_dejan_rastro_en_la_auditoria()
    {
        var s = await NewScenarioAsync();
        var session = await CreateSessionAsync(s.Tenant.Owner, s.Class.Id, Local(DayOfWeek.Monday, 9));
        await s.Tenant.Owner.PatchAsJsonAsync($"/api/v1/class-sessions/{session.Id}", new { capacity = 9 });
        await s.Tenant.Owner.PostAsJsonAsync($"/api/v1/class-sessions/{session.Id}/cancel", new { reason = "Prueba" });
        var start = DateOnly.FromDateTime(Local(DayOfWeek.Monday, 0).DateTime);
        var series = await CreateSeriesAsync(s.Tenant.Owner, SeriesBody(s.Class.Id, start.AddDays(7), start.AddDays(13)));

        var sessionAudit = (await s.Tenant.Owner.GetFromJsonAsync<PagedDto<AuditDto>>($"/api/v1/audit-logs?entity=ClassSession&entityId={session.Id}"))!;
        Assert.True(sessionAudit.TotalCount >= 3, $"Auditoría de la sesión con {sessionAudit.TotalCount} registros");
        var seriesAudit = (await s.Tenant.Owner.GetFromJsonAsync<PagedDto<AuditDto>>($"/api/v1/audit-logs?entity=ClassSeries&entityId={series.Series.Id}"))!;
        Assert.True(seriesAudit.TotalCount >= 1, "La serie no quedó en la auditoría");
    }
}
