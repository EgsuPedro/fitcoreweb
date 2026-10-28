using System.Net;
using System.Net.Http.Json;
using FitCore.Api.IntegrationTests.Infrastructure;
using Npgsql;
using static FitCore.Api.IntegrationTests.Infrastructure.CommercialTestApi;

namespace FitCore.Api.IntegrationTests;

[Collection(ApiCollection3.Name)]
public class TrainingTests(ApiFixture api)
{
    private sealed record PlanExRow(Guid Id, Guid ExerciseId, int Position, string ExerciseName, string TrackingMode, int Sets, int? Reps, int? DurationSeconds, int RestSeconds, decimal? RecommendedLoadKg);

    private sealed record PlanDayRow(Guid Id, int Position, string Name, PlanExRow[] Exercises);

    private sealed record PlanRow(Guid Id, string Status, int Version, Guid VersionId, bool IsCurrentVersion, string Name, PlanDayRow[] Days);

    private sealed record PlanSummaryRow(Guid Id, string Name, string Status, int Version, int DayCount, int ExerciseCount, int ActiveAssignments);

    private sealed record PlanVersionRow(Guid Id, int Number, string Name, string? ChangeNote);

    private sealed record AssignRow(
        Guid Id, Guid PlanId, Guid PlanVersionId, string PlanName, int PlanVersion, Guid MemberId, DateOnly StartDate, DateOnly? EndDate,
        DateOnly? EndedOn, string? EndReason, string Status);

    private sealed record GuideRow(string Name, string[] Instructions, string? Warnings);

    private sealed record MemberPlanExRow(PlanExRow Prescription, GuideRow? Guide);

    private sealed record MemberPlanDayRow(Guid Id, int Position, string Name, MemberPlanExRow[] Exercises);

    private sealed record MemberPlanRow(AssignRow Assignment, MemberPlanDayRow[] Days);

    private sealed record SetRow(int SetNumber, int? Reps, int? DurationSeconds, decimal? LoadKg);

    private sealed record SessExRow(Guid PlanExerciseId, Guid ExerciseId, string ExerciseName, bool Completed, int PlannedSets, SetRow[] Sets);

    private sealed record SessionRow(
        Guid Id, Guid MemberId, DateOnly PerformedOn, string DayName, string PlanName, int PlanVersion, string? MemberNotes, string? TrainerComment,
        int CompletedExercises, int TotalSets, decimal VolumeKg, SessExRow[] Exercises);

    private sealed record TotalsRow(int Sets, int TotalReps, int TotalDurationSeconds, decimal? MaxLoadKg, decimal VolumeKg);

    private sealed record ComparisonRow(Guid ExerciseId, string ExerciseName, TotalsRow? A, TotalsRow? B, decimal VolumeDeltaKg, decimal? MaxLoadDeltaKg, int RepsDelta);

    private sealed record CompareRow(SessionRow A, SessionRow B, ComparisonRow[] Exercises);

    private sealed record ProgressPoint(Guid SessionId, DateOnly Date, decimal? MaxLoadKg, decimal VolumeKg, int TotalReps);

    private sealed record ProgressRow(Guid ExerciseId, string ExerciseName, ProgressPoint[] Points);

    private sealed record ExerciseRow(Guid Id, string Name, bool IsActive);

    private sealed record TrainerRow(Guid Id);

    private sealed record Setup(
        TestTenant Tenant, Guid Branch, TrainerRow Trainer, HttpClient TrainerClient, MemberDto Member, HttpClient Student, Guid Squat, Guid Bench, Guid Plank);

    private static DateOnly Today =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/Guayaquil")).DateTime);

    private const string StudentPassword = "Nueva-Clave-2026x";

    private static string Unique(string prefix) => $"{prefix}.{Guid.NewGuid():N}"[..22] + "@gym.test";

    private async Task<Guid> GlobalExerciseAsync(HttpClient client, string name)
    {
        var page = await client.GetFromJsonAsync<PagedDto<ExerciseRow>>($"/api/v1/exercises?scope=global&search={Uri.EscapeDataString(name)}&pageSize=10");
        return page!.Items.First(e => e.Name == name).Id;
    }

    /// <summary>Gimnasio con un entrenador (con perfil y alumno asignado) y un alumno con acceso al portal.</summary>
    private async Task<Setup> NewSetupAsync(string code, bool assignMember = true)
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var branch = await TestApi.CreateBranchAsync(tenant.Owner, code);
        var trainerEmail = Unique("entrena");
        var user = await TestApi.CreateUserAsync(tenant.Owner, trainerEmail, "Entrenador", branch);
        var profile = (await (await tenant.Owner.PostAsJsonAsync("/api/v1/trainers", new { userId = user.Id })).Content.ReadFromJsonAsync<TrainerRow>())!;
        var trainerClient = TestApi.Authenticated(api, await TestApi.LoginAsync(api, tenant.Slug, trainerEmail, TestApi.UserPassword));

        var studentEmail = Unique("alumno");
        var memberResponse = await tenant.Owner.PostAsJsonAsync("/api/v1/members", new
        {
            branchId = branch, firstName = "Alicia", lastName = "Prueba", idType = "Cedula", idNumber = MemberTests.NewCedula(), email = studentEmail, phone = "0991234567"
        });
        Assert.Equal(HttpStatusCode.Created, memberResponse.StatusCode);
        var member = (await memberResponse.Content.ReadFromJsonAsync<MemberDto>())!;
        var student = await StudentAsync(tenant, member.Id, studentEmail);
        if (assignMember)
        {
            Assert.Equal(HttpStatusCode.Created, (await tenant.Owner.PostAsJsonAsync($"/api/v1/trainers/{profile.Id}/members", new { memberId = member.Id })).StatusCode);
        }

        return new Setup(
            tenant, branch, profile, trainerClient, member, student,
            await GlobalExerciseAsync(tenant.Owner, "Sentadilla libre"),
            await GlobalExerciseAsync(tenant.Owner, "Press de banca con barra"),
            await GlobalExerciseAsync(tenant.Owner, "Plancha frontal"));
    }

    private async Task<HttpClient> StudentAsync(TestTenant tenant, Guid memberId, string email)
    {
        var access = await (await tenant.Owner.PostAsync($"/api/v1/members/{memberId}/access", null)).Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var temporary = access.GetProperty("temporaryPassword").GetString()!;
        using var temp = TestApi.Authenticated(api, await TestApi.LoginAsync(api, tenant.Slug, email, temporary));
        Assert.Equal(HttpStatusCode.NoContent, (await temp.PostAsJsonAsync("/api/v1/auth/change-password", new { currentPassword = temporary, newPassword = StudentPassword })).StatusCode);
        return TestApi.Authenticated(api, await TestApi.LoginAsync(api, tenant.Slug, email, StudentPassword));
    }

    private static object ExRequest(Guid exerciseId, int sets = 3, int? reps = null, int? duration = null, int rest = 60, decimal? load = null, string? notes = null) =>
        new { exerciseId, sets, reps, durationSeconds = duration, restSeconds = rest, recommendedLoadKg = load, notes };

    private static object PlanBody(Setup s, string name = "Fuerza base", string? changeNote = null, object[]? days = null) => new
    {
        name,
        goal = "Ganar fuerza general",
        description = "Rutina de dos días",
        level = "Beginner",
        estimatedMinutes = 60,
        changeNote,
        days = days ?? new object[]
        {
            new { name = "Día 1 – Piernas y pecho", exercises = new[] { ExRequest(s.Squat, 3, reps: 12), ExRequest(s.Bench, 3, reps: 8, load: 40m, notes: "Escápulas juntas") } },
            new { name = "Día 2 – Core", exercises = new[] { ExRequest(s.Plank, 3, duration: 45, rest: 30) } }
        }
    };

    private static async Task<PlanRow> CreatePlanAsync(HttpClient client, object body)
    {
        var response = await client.PostAsJsonAsync("/api/v1/training-plans", body);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"Respondió {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<PlanRow>())!;
    }

    private static async Task<AssignRow> AssignAsync(HttpClient client, Guid planId, Guid memberId, DateOnly? start = null, DateOnly? end = null)
    {
        var response = await client.PostAsJsonAsync($"/api/v1/training-plans/{planId}/assignments", new { memberId, startDate = start, endDate = end });
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"Respondió {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<AssignRow>())!;
    }

    private static async Task<MemberPlanRow> MemberPlanAsync(HttpClient client, Guid memberId) =>
        (await client.GetFromJsonAsync<MemberPlanRow>($"/api/v1/members/{memberId}/training-plan"))!;

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, await TestApi.ProblemCodeAsync(response));
    }

    private async Task SqlAsync(string sql, bool asApp = false)
    {
        await using var connection = new NpgsqlConnection(asApp ? api.AppConnection : api.AdminConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static object SetBody(int? reps = null, int? duration = null, decimal? load = null) => new { reps, durationSeconds = duration, loadKg = load };

    /// <summary>Sesión del día 1 (sentadilla y press) con series completas.</summary>
    private static object SessionBody(MemberPlanRow plan, DateOnly? on = null, decimal bench = 40m, Guid? memberId = null, string? notes = "Buen entrenamiento")
    {
        var day = plan.Days[0];
        return new
        {
            memberId,
            dayId = day.Id,
            performedOn = on,
            durationMinutes = 55,
            notes,
            exercises = new object[]
            {
                new { planExerciseId = day.Exercises[0].Prescription.Id, completed = true, sets = new[] { SetBody(reps: 12), SetBody(reps: 12), SetBody(reps: 10) } },
                new { planExerciseId = day.Exercises[1].Prescription.Id, completed = true, notes = "Subí un poco", sets = new[] { SetBody(reps: 8, load: bench), SetBody(reps: 8, load: bench), SetBody(reps: 6, load: bench) } }
            }
        };
    }

    private static async Task<SessionRow> LogAsync(HttpClient client, object body)
    {
        var response = await client.PostAsJsonAsync("/api/v1/workout-sessions", body);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"Respondió {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<SessionRow>())!;
    }

    // ---- Rutinas ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Crear_una_rutina_guarda_dias_ejercicios_en_orden_y_una_copia_del_nombre()
    {
        var s = await NewSetupAsync("CR1");

        var plan = await CreatePlanAsync(s.TrainerClient, PlanBody(s));

        Assert.Equal(1, plan.Version);
        Assert.Equal("Active", plan.Status);
        Assert.True(plan.IsCurrentVersion);
        Assert.Equal(["Día 1 – Piernas y pecho", "Día 2 – Core"], plan.Days.Select(d => d.Name));
        Assert.Equal([1, 2], plan.Days.Select(d => d.Position));
        var day1 = plan.Days[0];
        Assert.Equal(["Sentadilla libre", "Press de banca con barra"], day1.Exercises.Select(e => e.ExerciseName));
        Assert.Equal([1, 2], day1.Exercises.Select(e => e.Position));
        Assert.Equal("Reps", day1.Exercises[0].TrackingMode);
        Assert.Equal(40m, day1.Exercises[1].RecommendedLoadKg);
        Assert.Equal("Duration", plan.Days[1].Exercises[0].TrackingMode);

        var summary = (await s.TrainerClient.GetFromJsonAsync<PagedDto<PlanSummaryRow>>("/api/v1/training-plans"))!.Items.Single();
        Assert.Equal((plan.Id, 2, 3, 0), (summary.Id, summary.DayCount, summary.ExerciseCount, summary.ActiveAssignments));
    }

    [Theory]
    [InlineData("duration_con_repeticiones", "training.prescription_duration_required")]
    [InlineData("reps_con_carga", "training.load_not_applicable")]
    [InlineData("carga_sin_repeticiones", "training.prescription_reps_required")]
    [InlineData("series_cero", "training.sets_invalid")]
    [InlineData("descanso_excesivo", "training.rest_invalid")]
    public async Task La_prescripcion_debe_ser_coherente_con_el_modo_de_registro_del_ejercicio(string caso, string code)
    {
        var s = await NewSetupAsync("CR2");
        object exercise = caso switch
        {
            "duration_con_repeticiones" => ExRequest(s.Plank, 3, reps: 10),
            "reps_con_carga" => ExRequest(s.Squat, 3, reps: 10, load: 20m),
            "carga_sin_repeticiones" => ExRequest(s.Bench, 3, load: 40m),
            "series_cero" => ExRequest(s.Squat, 0, reps: 10),
            _ => ExRequest(s.Squat, 3, reps: 10, rest: 5000)
        };

        var response = await s.TrainerClient.PostAsJsonAsync("/api/v1/training-plans", PlanBody(s, days: [new { name = "Día", exercises = new[] { exercise } }]));

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, code);
    }

    [Fact]
    public async Task Se_rechazan_rutinas_vacias_ejercicios_repetidos_inexistentes_o_ajenos()
    {
        var s = await NewSetupAsync("CR3");
        var other = await TestApi.NewTenantAsync(api);
        var otherBranch = await TestApi.CreateBranchAsync(other.Owner, "OTR");
        var foreign = (await (await other.Owner.PostAsJsonAsync("/api/v1/exercises", new
        {
            name = "Solo de otro gimnasio", primaryMuscle = "Back", type = "Strength", equipment = "Band", difficulty = "Beginner", trackingMode = "Reps"
        })).Content.ReadFromJsonAsync<ExerciseRow>())!;
        Assert.NotEqual(Guid.Empty, otherBranch);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await s.TrainerClient.PostAsJsonAsync("/api/v1/training-plans", PlanBody(s, days: []))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await s.TrainerClient.PostAsJsonAsync("/api/v1/training-plans", PlanBody(s, days: [new { name = "Vacío", exercises = Array.Empty<object>() }]))).StatusCode);
        await AssertProblemAsync(
            await s.TrainerClient.PostAsJsonAsync("/api/v1/training-plans", PlanBody(s, days: [new { name = "Dup", exercises = new[] { ExRequest(s.Squat, 3, reps: 10), ExRequest(s.Squat, 3, reps: 12) } }])),
            HttpStatusCode.UnprocessableEntity, "training.duplicate_exercise");
        await AssertProblemAsync(
            await s.TrainerClient.PostAsJsonAsync("/api/v1/training-plans", PlanBody(s, days: [new { name = "X", exercises = new[] { ExRequest(Guid.NewGuid(), 3, reps: 10) } }])),
            HttpStatusCode.UnprocessableEntity, "training.exercise_not_found");
        // Un ejercicio de otro gimnasio no existe para este.
        await AssertProblemAsync(
            await s.TrainerClient.PostAsJsonAsync("/api/v1/training-plans", PlanBody(s, days: [new { name = "X", exercises = new[] { ExRequest(foreign.Id, 3, reps: 10) } }])),
            HttpStatusCode.UnprocessableEntity, "training.exercise_not_found");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await s.TrainerClient.PostAsJsonAsync("/api/v1/training-plans", PlanBody(s, name: "A"))).StatusCode);
    }

    [Fact]
    public async Task Un_ejercicio_desactivado_no_entra_a_rutinas_nuevas_pero_no_obliga_a_sacarlo_de_las_existentes()
    {
        var s = await NewSetupAsync("INA");
        var own = (await (await s.Tenant.Owner.PostAsJsonAsync("/api/v1/exercises", new
        {
            name = "Ejercicio propio", primaryMuscle = "Back", type = "Strength", equipment = "Band", difficulty = "Beginner", trackingMode = "Reps"
        })).Content.ReadFromJsonAsync<ExerciseRow>())!;
        var plan = await CreatePlanAsync(s.TrainerClient, PlanBody(s, days: [new { name = "Día", exercises = new[] { ExRequest(own.Id, 3, reps: 10), ExRequest(s.Squat, 3, reps: 10) } }]));
        await s.Tenant.Owner.PatchAsJsonAsync($"/api/v1/exercises/{own.Id}/status", new { isActive = false });

        // Sigue en la rutina que ya lo tenía...
        var updated = await s.TrainerClient.PutAsJsonAsync($"/api/v1/training-plans/{plan.Id}", PlanBody(s, name: "Misma rutina", days: [new { name = "Día", exercises = new[] { ExRequest(own.Id, 4, reps: 10) } }]));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        // ...pero no se puede ofrecer en una rutina nueva.
        await AssertProblemAsync(
            await s.TrainerClient.PostAsJsonAsync("/api/v1/training-plans", PlanBody(s, name: "Otra", days: [new { name = "Día", exercises = new[] { ExRequest(own.Id, 3, reps: 10) } }])),
            HttpStatusCode.UnprocessableEntity, "training.exercise_inactive");
    }

    [Fact]
    public async Task Modificar_una_rutina_crea_una_version_nueva_y_conserva_las_anteriores()
    {
        var s = await NewSetupAsync("VER");
        var v1 = await CreatePlanAsync(s.TrainerClient, PlanBody(s, changeNote: "Inicial"));

        var response = await s.TrainerClient.PutAsJsonAsync($"/api/v1/training-plans/{v1.Id}", PlanBody(s, name: "Fuerza base renovada", changeNote: "Más volumen",
            days: [new { name = "Único día", exercises = new[] { ExRequest(s.Squat, 5, reps: 5), ExRequest(s.Bench, 5, reps: 5, load: 50m) } }]));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var v2 = (await response.Content.ReadFromJsonAsync<PlanRow>())!;

        Assert.Equal(2, v2.Version);
        Assert.Equal("Fuerza base renovada", v2.Name);
        Assert.Single(v2.Days);
        var original = (await s.TrainerClient.GetFromJsonAsync<PlanRow>($"/api/v1/training-plans/{v1.Id}?version=1"))!;
        Assert.Equal("Fuerza base", original.Name);
        Assert.Equal(2, original.Days.Length);
        Assert.False(original.IsCurrentVersion);
        var current = (await s.TrainerClient.GetFromJsonAsync<PlanRow>($"/api/v1/training-plans/{v1.Id}"))!;
        Assert.Equal(2, current.Version);
        var versions = (await s.TrainerClient.GetFromJsonAsync<PlanVersionRow[]>($"/api/v1/training-plans/{v1.Id}/versions"))!;
        Assert.Equal([2, 1], versions.Select(v => v.Number));
        Assert.Equal(["Más volumen", "Inicial"], versions.Select(v => v.ChangeNote));
        Assert.Equal(HttpStatusCode.NotFound, (await s.TrainerClient.GetAsync($"/api/v1/training-plans/{v1.Id}?version=9")).StatusCode);
    }

    [Fact]
    public async Task Duplicar_archivar_y_reactivar_una_rutina()
    {
        var s = await NewSetupAsync("DUP");
        var plan = await CreatePlanAsync(s.TrainerClient, PlanBody(s));

        var copyResponse = await s.TrainerClient.PostAsync($"/api/v1/training-plans/{plan.Id}/duplicate", null);
        Assert.Equal(HttpStatusCode.Created, copyResponse.StatusCode);
        var copy = (await copyResponse.Content.ReadFromJsonAsync<PlanRow>())!;
        Assert.NotEqual(plan.Id, copy.Id);
        Assert.Equal("Fuerza base (copia)", copy.Name);
        Assert.Equal(1, copy.Version);
        Assert.Equal(plan.Days.Select(d => d.Exercises.Length), copy.Days.Select(d => d.Exercises.Length));

        Assert.Equal(HttpStatusCode.OK, (await s.TrainerClient.PatchAsJsonAsync($"/api/v1/training-plans/{plan.Id}/status", new { active = false })).StatusCode);
        await AssertProblemAsync(await s.TrainerClient.PutAsJsonAsync($"/api/v1/training-plans/{plan.Id}", PlanBody(s)), HttpStatusCode.Conflict, "training.plan_archived");
        await AssertProblemAsync(await s.TrainerClient.PostAsJsonAsync($"/api/v1/training-plans/{plan.Id}/assignments", new { memberId = s.Member.Id }), HttpStatusCode.Conflict, "training.plan_archived");
        var archived = (await s.TrainerClient.GetFromJsonAsync<PagedDto<PlanSummaryRow>>("/api/v1/training-plans?status=Archived"))!;
        Assert.Equal([plan.Id], archived.Items.Select(p => p.Id));
        Assert.Equal(HttpStatusCode.OK, (await s.TrainerClient.PatchAsJsonAsync($"/api/v1/training-plans/{plan.Id}/status", new { active = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await s.TrainerClient.PostAsJsonAsync($"/api/v1/training-plans/{plan.Id}/assignments", new { memberId = s.Member.Id })).StatusCode);
    }

    // ---- Asignaciones -----------------------------------------------------------------------------------------

    [Fact]
    public async Task El_entrenador_asigna_la_rutina_y_el_alumno_ve_la_version_asignada_con_la_guia_de_cada_ejercicio()
    {
        var s = await NewSetupAsync("ASG");
        var plan = await CreatePlanAsync(s.TrainerClient, PlanBody(s));
        await AssertProblemAsync(await s.Student.GetAsync($"/api/v1/members/{s.Member.Id}/training-plan"), HttpStatusCode.NotFound, "training.no_active_plan");

        var assignment = await AssignAsync(s.TrainerClient, plan.Id, s.Member.Id);
        var seen = await MemberPlanAsync(s.Student, s.Member.Id);

        Assert.Equal(Today, assignment.StartDate);
        Assert.Equal("Current", seen.Assignment.Status);
        Assert.Equal(1, seen.Assignment.PlanVersion);
        Assert.Equal(["Día 1 – Piernas y pecho", "Día 2 – Core"], seen.Days.Select(d => d.Name));
        var squat = seen.Days[0].Exercises[0];
        Assert.Equal("Sentadilla libre", squat.Guide!.Name);
        Assert.NotEmpty(squat.Guide.Instructions);
        Assert.False(string.IsNullOrWhiteSpace(squat.Guide.Warnings));
        // El alumno lee su rutina, pero no la biblioteca de rutinas ni puede modificar nada.
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Student.GetAsync("/api/v1/training-plans")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Student.PostAsJsonAsync("/api/v1/training-plans", PlanBody(s))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Student.PutAsJsonAsync($"/api/v1/training-plans/{plan.Id}", PlanBody(s))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Student.PostAsJsonAsync($"/api/v1/training-plans/{plan.Id}/assignments", new { memberId = s.Member.Id })).StatusCode);
    }

    [Fact]
    public async Task Una_rutina_modificada_despues_no_cambia_lo_asignado_y_reasignar_da_la_version_nueva()
    {
        var s = await NewSetupAsync("PIN");
        var v1 = await CreatePlanAsync(s.TrainerClient, PlanBody(s));
        await AssignAsync(s.TrainerClient, v1.Id, s.Member.Id);
        await s.TrainerClient.PutAsJsonAsync($"/api/v1/training-plans/{v1.Id}", PlanBody(s, name: "Fuerza avanzada", days: [new { name = "Nuevo", exercises = new[] { ExRequest(s.Squat, 5, reps: 5) } }]));

        var stillV1 = await MemberPlanAsync(s.Student, s.Member.Id);
        Assert.Equal(1, stillV1.Assignment.PlanVersion);
        Assert.Equal("Fuerza base", stillV1.Assignment.PlanName);
        Assert.Equal(2, stillV1.Days.Length);

        var reassigned = await AssignAsync(s.TrainerClient, v1.Id, s.Member.Id);
        Assert.Equal(2, reassigned.PlanVersion);
        var nowV2 = await MemberPlanAsync(s.Student, s.Member.Id);
        Assert.Equal("Fuerza avanzada", nowV2.Assignment.PlanName);
        Assert.Single(nowV2.Days);
    }

    [Fact]
    public async Task Reasignar_cierra_la_rutina_anterior_con_su_motivo_y_conserva_el_historial()
    {
        var s = await NewSetupAsync("RE1");
        var planA = await CreatePlanAsync(s.TrainerClient, PlanBody(s, name: "Rutina A"));
        var planB = await CreatePlanAsync(s.TrainerClient, PlanBody(s, name: "Rutina B"));

        var first = await AssignAsync(s.TrainerClient, planA.Id, s.Member.Id);
        var second = await AssignAsync(s.TrainerClient, planB.Id, s.Member.Id);

        var history = (await s.TrainerClient.GetFromJsonAsync<AssignRow[]>($"/api/v1/members/{s.Member.Id}/training-assignments"))!;
        Assert.Equal(2, history.Length);
        var closed = history.Single(a => a.Id == first.Id);
        Assert.Equal("Reemplazada por otra rutina", closed.EndReason);
        Assert.Equal(Today.AddDays(-1), closed.EndedOn);
        Assert.Equal("Ended", closed.Status);
        Assert.Equal("Current", history.Single(a => a.Id == second.Id).Status);
        Assert.Equal("Rutina B", (await MemberPlanAsync(s.Student, s.Member.Id)).Assignment.PlanName);
    }

    [Fact]
    public async Task Una_rutina_programada_a_futuro_no_rige_hasta_su_fecha_y_las_fechas_se_validan()
    {
        var s = await NewSetupAsync("FUT");
        var plan = await CreatePlanAsync(s.TrainerClient, PlanBody(s));
        var url = $"/api/v1/training-plans/{plan.Id}/assignments";

        await AssertProblemAsync(await s.TrainerClient.PostAsJsonAsync(url, new { memberId = s.Member.Id, startDate = Today.AddDays(-1) }), HttpStatusCode.UnprocessableEntity, "training.dates_invalid");
        await AssertProblemAsync(await s.TrainerClient.PostAsJsonAsync(url, new { memberId = s.Member.Id, startDate = Today.AddDays(5), endDate = Today.AddDays(2) }), HttpStatusCode.UnprocessableEntity, "training.dates_invalid");
        await AssertProblemAsync(await s.TrainerClient.PostAsJsonAsync(url, new { memberId = s.Member.Id, startDate = Today, endDate = Today.AddDays(800) }), HttpStatusCode.UnprocessableEntity, "training.dates_invalid");

        var future = await AssignAsync(s.TrainerClient, plan.Id, s.Member.Id, Today.AddDays(7), Today.AddDays(30));
        Assert.Equal("Scheduled", future.Status);
        await AssertProblemAsync(await s.Student.GetAsync($"/api/v1/members/{s.Member.Id}/training-plan"), HttpStatusCode.NotFound, "training.no_active_plan");
    }

    [Fact]
    public async Task Cerrar_una_asignacion_exige_motivo_es_inmediato_y_no_se_repite()
    {
        var s = await NewSetupAsync("END");
        var plan = await CreatePlanAsync(s.TrainerClient, PlanBody(s));
        var assignment = await AssignAsync(s.TrainerClient, plan.Id, s.Member.Id);
        var url = $"/api/v1/training-assignments/{assignment.Id}/end";

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await s.TrainerClient.PostAsJsonAsync(url, new { reason = "" })).StatusCode);
        var ended = await s.TrainerClient.PostAsJsonAsync(url, new { reason = "Terminó el ciclo" });
        Assert.Equal(HttpStatusCode.OK, ended.StatusCode);
        Assert.Equal("Ended", (await ended.Content.ReadFromJsonAsync<AssignRow>())!.Status);
        await AssertProblemAsync(await s.Student.GetAsync($"/api/v1/members/{s.Member.Id}/training-plan"), HttpStatusCode.NotFound, "training.no_active_plan");
        await AssertProblemAsync(await s.TrainerClient.PostAsJsonAsync(url, new { reason = "otra vez" }), HttpStatusCode.Conflict, "training.assignment_ended");
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Student.PostAsJsonAsync(url, new { reason = "yo mismo" })).StatusCode);
    }

    [Fact]
    public async Task Asignaciones_simultaneas_dejan_una_sola_rutina_abierta()
    {
        var s = await NewSetupAsync("RAC");
        var plans = new List<PlanRow>();
        for (var i = 0; i < 8; i++)
        {
            plans.Add(await CreatePlanAsync(s.TrainerClient, PlanBody(s, name: $"Rutina {i}")));
        }

        var responses = await Task.WhenAll(plans.Select(p => s.TrainerClient.PostAsJsonAsync($"/api/v1/training-plans/{p.Id}/assignments", new { memberId = s.Member.Id })));

        Assert.All(responses, r => Assert.True(r.StatusCode is HttpStatusCode.Created or HttpStatusCode.Conflict));
        var history = (await s.TrainerClient.GetFromJsonAsync<AssignRow[]>($"/api/v1/members/{s.Member.Id}/training-assignments"))!;
        Assert.Equal(1, history.Count(a => a.EndedOn is null));
        Assert.Equal(responses.Count(r => r.StatusCode == HttpStatusCode.Created), history.Length);
    }

    [Fact]
    public async Task Solo_el_entrenador_asignado_o_quien_ve_a_todos_asigna_y_el_resto_no_ve_al_alumno()
    {
        var s = await NewSetupAsync("SCO");
        var plan = await CreatePlanAsync(s.TrainerClient, PlanBody(s));
        var strangerEmail = Unique("ajeno");
        var stranger = await TestApi.CreateUserAsync(s.Tenant.Owner, strangerEmail, "Entrenador", s.Branch);
        await s.Tenant.Owner.PostAsJsonAsync("/api/v1/trainers", new { userId = stranger.Id });
        var strangerClient = TestApi.Authenticated(api, await TestApi.LoginAsync(api, s.Tenant.Slug, strangerEmail, TestApi.UserPassword));
        var adminEmail = Unique("admin");
        await TestApi.CreateUserAsync(s.Tenant.Owner, adminEmail, "AdministradorSucursal", s.Branch);
        var admin = TestApi.Authenticated(api, await TestApi.LoginAsync(api, s.Tenant.Slug, adminEmail, TestApi.UserPassword));
        var reception = await ClientWithRoleAsync(api, s.Tenant, Unique("recep"), true, ["members.read"]);

        // Un entrenador sin ese alumno asignado no lo ve: responde como si no existiera.
        await AssertProblemAsync(await strangerClient.PostAsJsonAsync($"/api/v1/training-plans/{plan.Id}/assignments", new { memberId = s.Member.Id }), HttpStatusCode.NotFound, "training.member_not_found");
        await AssertProblemAsync(await strangerClient.GetAsync($"/api/v1/members/{s.Member.Id}/training-plan"), HttpStatusCode.NotFound, "training.member_not_found");
        await AssertProblemAsync(await strangerClient.GetAsync($"/api/v1/members/{s.Member.Id}/workout-sessions"), HttpStatusCode.NotFound, "training.member_not_found");
        Assert.Equal(HttpStatusCode.Forbidden, (await reception.GetAsync($"/api/v1/members/{s.Member.Id}/training-plan")).StatusCode);
        // El administrador de sucursal (wellness.members.all) sí, y el propietario también.
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync($"/api/v1/training-plans/{plan.Id}/assignments", new { memberId = s.Member.Id })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.Tenant.Owner.GetAsync($"/api/v1/members/{s.Member.Id}/training-plan")).StatusCode);
    }

    [Fact]
    public async Task No_se_asigna_a_un_alumno_inactivo_ni_de_otro_gimnasio()
    {
        var s = await NewSetupAsync("INM");
        var other = await NewSetupAsync("INO");
        var plan = await CreatePlanAsync(s.TrainerClient, PlanBody(s));

        await AssertProblemAsync(await s.TrainerClient.PostAsJsonAsync($"/api/v1/training-plans/{plan.Id}/assignments", new { memberId = other.Member.Id }), HttpStatusCode.NotFound, "training.member_not_found");
        Assert.Equal(HttpStatusCode.NotFound, (await other.TrainerClient.GetAsync($"/api/v1/training-plans/{plan.Id}")).StatusCode);
        await AssertProblemAsync(await other.TrainerClient.PostAsJsonAsync($"/api/v1/training-plans/{plan.Id}/assignments", new { memberId = other.Member.Id }), HttpStatusCode.NotFound, "training.plan_not_found");
        Assert.Equal(HttpStatusCode.NotFound, (await other.Student.GetAsync($"/api/v1/members/{s.Member.Id}/training-plan")).StatusCode);

        await s.Tenant.Owner.PatchAsJsonAsync($"/api/v1/members/{s.Member.Id}/status", new { isActive = false });
        await AssertProblemAsync(await s.Tenant.Owner.PostAsJsonAsync($"/api/v1/training-plans/{plan.Id}/assignments", new { memberId = s.Member.Id }), HttpStatusCode.Conflict, "training.member_inactive");
    }

    // ---- Sesiones ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task El_alumno_registra_una_sesion_con_sus_series_reales_y_se_calcula_el_volumen()
    {
        var s = await NewSetupAsync("SES");
        var plan = await CreatePlanAsync(s.TrainerClient, PlanBody(s));
        await AssignAsync(s.TrainerClient, plan.Id, s.Member.Id);
        var mine = await MemberPlanAsync(s.Student, s.Member.Id);

        var session = await LogAsync(s.Student, SessionBody(mine));

        Assert.Equal(s.Member.Id, session.MemberId);
        Assert.Equal(Today, session.PerformedOn);
        Assert.Equal("Día 1 – Piernas y pecho", session.DayName);
        Assert.Equal("Fuerza base", session.PlanName);
        Assert.Equal(1, session.PlanVersion);
        Assert.Equal((2, 6), (session.CompletedExercises, session.TotalSets));
        // Volumen = 8×40 + 8×40 + 6×40 (la sentadilla libre no tiene carga).
        Assert.Equal(880m, session.VolumeKg);
        var bench = session.Exercises.Single(e => e.ExerciseName == "Press de banca con barra");
        Assert.Equal([1, 2, 3], bench.Sets.Select(x => x.SetNumber));
        Assert.Equal([8, 8, 6], bench.Sets.Select(x => x.Reps));
        Assert.Equal(3, bench.PlannedSets);

        var listed = (await s.Student.GetFromJsonAsync<PagedDto<SessionRow>>($"/api/v1/members/{s.Member.Id}/workout-sessions"))!;
        Assert.Equal([session.Id], listed.Items.Select(x => x.Id));
        Assert.Equal(session.Id, (await s.TrainerClient.GetFromJsonAsync<SessionRow>($"/api/v1/workout-sessions/{session.Id}"))!.Id);
    }

    [Fact]
    public async Task Las_series_deben_ser_coherentes_con_el_tipo_de_ejercicio()
    {
        var s = await NewSetupAsync("VAL");
        var plan = await CreatePlanAsync(s.TrainerClient, PlanBody(s));
        await AssignAsync(s.TrainerClient, plan.Id, s.Member.Id);
        var mine = await MemberPlanAsync(s.Student, s.Member.Id);
        var day1 = mine.Days[0];
        var day2 = mine.Days[1];

        object Body(Guid dayId, Guid planExerciseId, bool completed, object[]? sets) =>
            new { dayId, exercises = new[] { new { planExerciseId, completed, sets } } };

        // Por tiempo: no admite repeticiones; solo repeticiones: no admite carga; repeticiones y carga: pide repeticiones.
        await AssertProblemAsync(await s.Student.PostAsJsonAsync("/api/v1/workout-sessions", Body(day2.Id, day2.Exercises[0].Prescription.Id, true, [SetBody(reps: 10)])), HttpStatusCode.UnprocessableEntity, "training.set_duration_required");
        await AssertProblemAsync(await s.Student.PostAsJsonAsync("/api/v1/workout-sessions", Body(day1.Id, day1.Exercises[0].Prescription.Id, true, [SetBody(reps: 10, load: 20m)])), HttpStatusCode.UnprocessableEntity, "training.load_not_applicable");
        await AssertProblemAsync(await s.Student.PostAsJsonAsync("/api/v1/workout-sessions", Body(day1.Id, day1.Exercises[1].Prescription.Id, true, [SetBody(duration: 30)])), HttpStatusCode.UnprocessableEntity, "training.set_reps_required");
        await AssertProblemAsync(await s.Student.PostAsJsonAsync("/api/v1/workout-sessions", Body(day1.Id, day1.Exercises[1].Prescription.Id, true, [SetBody(reps: 8, load: -5m)])), HttpStatusCode.UnprocessableEntity, "training.load_invalid");
        await AssertProblemAsync(await s.Student.PostAsJsonAsync("/api/v1/workout-sessions", Body(day1.Id, day1.Exercises[1].Prescription.Id, true, [])), HttpStatusCode.UnprocessableEntity, "training.completed_without_sets");
        await AssertProblemAsync(await s.Student.PostAsJsonAsync("/api/v1/workout-sessions", Body(day1.Id, day1.Exercises[1].Prescription.Id, false, [SetBody(reps: 8, load: 40m)])), HttpStatusCode.UnprocessableEntity, "training.session_empty");
        // Un ejercicio de otro día, repetido, o un día que no es de la rutina asignada.
        await AssertProblemAsync(await s.Student.PostAsJsonAsync("/api/v1/workout-sessions", Body(day1.Id, day2.Exercises[0].Prescription.Id, true, [SetBody(duration: 30)])), HttpStatusCode.UnprocessableEntity, "training.exercise_not_in_day");
        await AssertProblemAsync(await s.Student.PostAsJsonAsync("/api/v1/workout-sessions", Body(Guid.NewGuid(), day1.Exercises[0].Prescription.Id, true, [SetBody(reps: 10)])), HttpStatusCode.UnprocessableEntity, "training.day_not_in_plan");
        var duplicated = new { dayId = day1.Id, exercises = new[] { new { planExerciseId = day1.Exercises[0].Prescription.Id, completed = true, sets = new[] { SetBody(reps: 10) } }, new { planExerciseId = day1.Exercises[0].Prescription.Id, completed = true, sets = new[] { SetBody(reps: 10) } } } };
        await AssertProblemAsync(await s.Student.PostAsJsonAsync("/api/v1/workout-sessions", duplicated), HttpStatusCode.UnprocessableEntity, "training.exercise_not_in_day");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await s.Student.PostAsJsonAsync("/api/v1/workout-sessions", new { dayId = day1.Id, exercises = Array.Empty<object>() })).StatusCode);
        // Nada de lo rechazado quedó guardado.
        Assert.Empty((await s.Student.GetFromJsonAsync<PagedDto<SessionRow>>($"/api/v1/members/{s.Member.Id}/workout-sessions"))!.Items);
    }

    [Fact]
    public async Task La_fecha_de_la_sesion_no_puede_ser_futura_ni_antigua_ni_anterior_a_la_rutina()
    {
        var s = await NewSetupAsync("FEC");
        var plan = await CreatePlanAsync(s.TrainerClient, PlanBody(s));
        await AssignAsync(s.TrainerClient, plan.Id, s.Member.Id);
        var mine = await MemberPlanAsync(s.Student, s.Member.Id);

        await AssertProblemAsync(await s.Student.PostAsJsonAsync("/api/v1/workout-sessions", SessionBody(mine, Today.AddDays(1))), HttpStatusCode.UnprocessableEntity, "training.session_date_invalid");
        await AssertProblemAsync(await s.Student.PostAsJsonAsync("/api/v1/workout-sessions", SessionBody(mine, Today.AddDays(-31))), HttpStatusCode.UnprocessableEntity, "training.session_date_invalid");
        // La rutina empezó hoy: ayer no había rutina vigente.
        await AssertProblemAsync(await s.Student.PostAsJsonAsync("/api/v1/workout-sessions", SessionBody(mine, Today.AddDays(-1))), HttpStatusCode.UnprocessableEntity, "training.session_date_invalid");
        Assert.NotNull(await LogAsync(s.Student, SessionBody(mine, Today)));
    }

    [Fact]
    public async Task Sin_rutina_vigente_no_se_registran_sesiones()
    {
        var s = await NewSetupAsync("NOP");
        var plan = await CreatePlanAsync(s.TrainerClient, PlanBody(s));
        await AssignAsync(s.TrainerClient, plan.Id, s.Member.Id);
        var mine = await MemberPlanAsync(s.Student, s.Member.Id);
        var assignment = mine.Assignment;
        await s.TrainerClient.PostAsJsonAsync($"/api/v1/training-assignments/{assignment.Id}/end", new { reason = "Cierre" });

        await AssertProblemAsync(await s.Student.PostAsJsonAsync("/api/v1/workout-sessions", SessionBody(mine)), HttpStatusCode.NotFound, "training.no_active_plan");
    }

    [Fact]
    public async Task El_alumno_solo_registra_sus_sesiones_y_el_entrenador_solo_las_de_sus_alumnos()
    {
        var s = await NewSetupAsync("PRO");
        var other = await NewSetupAsync("PR2");
        var plan = await CreatePlanAsync(s.TrainerClient, PlanBody(s));
        await AssignAsync(s.TrainerClient, plan.Id, s.Member.Id);
        var mine = await MemberPlanAsync(s.Student, s.Member.Id);
        var plan2 = await CreatePlanAsync(other.TrainerClient, PlanBody(other));
        await AssignAsync(other.TrainerClient, plan2.Id, other.Member.Id);

        // El identificador del cliente no sustituye la propiedad: un alumno no registra la sesión de otro.
        await AssertProblemAsync(await s.Student.PostAsJsonAsync("/api/v1/workout-sessions", SessionBody(mine, memberId: other.Member.Id)), HttpStatusCode.Forbidden, "training.other_member");
        var own = await LogAsync(s.Student, SessionBody(mine, memberId: s.Member.Id));
        Assert.Equal(s.Member.Id, own.MemberId);

        // El entrenador asignado registra por su alumno; otro entrenador no puede.
        var byTrainer = await LogAsync(s.TrainerClient, SessionBody(mine, memberId: s.Member.Id, notes: "Registrada por el entrenador"));
        Assert.Equal(s.Member.Id, byTrainer.MemberId);
        await AssertProblemAsync(await other.TrainerClient.PostAsJsonAsync("/api/v1/workout-sessions", SessionBody(mine, memberId: s.Member.Id)), HttpStatusCode.NotFound, "training.member_not_found");
        await AssertProblemAsync(await s.TrainerClient.PostAsJsonAsync("/api/v1/workout-sessions", SessionBody(mine)), HttpStatusCode.NotFound, "training.member_not_found");

        // Las sesiones de otro alumno (aunque sea del mismo gimnasio) o de otro gimnasio no se leen.
        Assert.Equal(HttpStatusCode.NotFound, (await other.Student.GetAsync($"/api/v1/workout-sessions/{own.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.TrainerClient.GetAsync($"/api/v1/workout-sessions/{own.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Student.GetAsync($"/api/v1/members/{s.Member.Id}/workout-sessions")).StatusCode);
    }

    [Fact]
    public async Task Las_sesiones_conservan_su_version_y_los_nombres_aunque_la_rutina_y_la_biblioteca_cambien()
    {
        var s = await NewSetupAsync("HIS");
        var own = (await (await s.Tenant.Owner.PostAsJsonAsync("/api/v1/exercises", new
        {
            name = "Remo especial", primaryMuscle = "Back", type = "Strength", equipment = "Band", difficulty = "Beginner", trackingMode = "RepsAndLoad"
        })).Content.ReadFromJsonAsync<ExerciseRow>())!;
        var plan = await CreatePlanAsync(s.TrainerClient, PlanBody(s, days: [new { name = "Día único", exercises = new[] { ExRequest(own.Id, 3, reps: 10, load: 20m) } }]));
        await AssignAsync(s.TrainerClient, plan.Id, s.Member.Id);
        var mine = await MemberPlanAsync(s.Student, s.Member.Id);
        var session = await LogAsync(s.Student, new
        {
            dayId = mine.Days[0].Id,
            exercises = new[] { new { planExerciseId = mine.Days[0].Exercises[0].Prescription.Id, completed = true, sets = new[] { SetBody(reps: 10, load: 20m) } } }
        });

        // Se renombra el ejercicio y se modifica la rutina (versión 2).
        await s.Tenant.Owner.PutAsJsonAsync($"/api/v1/exercises/{own.Id}", new
        {
            name = "Remo renombrado", primaryMuscle = "Back", type = "Strength", equipment = "Band", difficulty = "Beginner", trackingMode = "RepsAndLoad"
        });
        await s.TrainerClient.PutAsJsonAsync($"/api/v1/training-plans/{plan.Id}", PlanBody(s, name: "Rutina nueva", days: [new { name = "Otro día", exercises = new[] { ExRequest(own.Id, 5, reps: 5, load: 30m) } }]));

        var after = (await s.Student.GetFromJsonAsync<SessionRow>($"/api/v1/workout-sessions/{session.Id}"))!;
        Assert.Equal(1, after.PlanVersion);
        Assert.Equal("Fuerza base", after.PlanName);
        Assert.Equal("Día único", after.DayName);
        Assert.Equal("Remo especial", after.Exercises.Single().ExerciseName);
        Assert.Equal(3, after.Exercises.Single().PlannedSets);
        var v2 = (await s.TrainerClient.GetFromJsonAsync<PlanRow>($"/api/v1/training-plans/{plan.Id}"))!;
        Assert.Equal("Remo renombrado", v2.Days[0].Exercises[0].ExerciseName);
    }

    [Fact]
    public async Task El_entrenador_comenta_la_sesion_una_sola_vez_y_el_alumno_no_puede()
    {
        var s = await NewSetupAsync("COM");
        var other = await NewSetupAsync("CO2");
        var plan = await CreatePlanAsync(s.TrainerClient, PlanBody(s));
        await AssignAsync(s.TrainerClient, plan.Id, s.Member.Id);
        var session = await LogAsync(s.Student, SessionBody(await MemberPlanAsync(s.Student, s.Member.Id)));
        var url = $"/api/v1/workout-sessions/{session.Id}/trainer-comment";

        Assert.Equal(HttpStatusCode.Forbidden, (await s.Student.PostAsJsonAsync(url, new { comment = "me felicito" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.TrainerClient.PostAsJsonAsync(url, new { comment = "intruso" })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await s.TrainerClient.PostAsJsonAsync(url, new { comment = "" })).StatusCode);
        var ok = await s.TrainerClient.PostAsJsonAsync(url, new { comment = "Sube 2,5 kg en el press la próxima semana" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("Sube 2,5 kg en el press la próxima semana", (await ok.Content.ReadFromJsonAsync<SessionRow>())!.TrainerComment);
        await AssertProblemAsync(await s.TrainerClient.PostAsJsonAsync(url, new { comment = "otro" }), HttpStatusCode.Conflict, "training.comment_exists");
        Assert.Equal("Buen entrenamiento", (await s.Student.GetFromJsonAsync<SessionRow>($"/api/v1/workout-sessions/{session.Id}"))!.MemberNotes);
    }

    // ---- Comparación y progreso -------------------------------------------------------------------------------

    [Fact]
    public async Task Comparar_dos_sesiones_muestra_las_diferencias_por_ejercicio_y_el_progreso_en_el_tiempo()
    {
        var s = await NewSetupAsync("CMP");
        var plan = await CreatePlanAsync(s.TrainerClient, PlanBody(s));
        await AssignAsync(s.TrainerClient, plan.Id, s.Member.Id);
        var mine = await MemberPlanAsync(s.Student, s.Member.Id);
        var first = await LogAsync(s.Student, SessionBody(mine, bench: 40m));
        var second = await LogAsync(s.Student, SessionBody(mine, bench: 45m));

        var compare = (await s.Student.GetFromJsonAsync<CompareRow>($"/api/v1/members/{s.Member.Id}/workout-sessions/compare?sessionA={first.Id}&sessionB={second.Id}"))!;

        var press = compare.Exercises.Single(e => e.ExerciseName == "Press de banca con barra");
        Assert.Equal(880m, press.A!.VolumeKg);
        Assert.Equal(990m, press.B!.VolumeKg);
        Assert.Equal(110m, press.VolumeDeltaKg);
        Assert.Equal(5m, press.MaxLoadDeltaKg);
        Assert.Equal(0, press.RepsDelta);
        var squat = compare.Exercises.Single(e => e.ExerciseName == "Sentadilla libre");
        Assert.Equal(0m, squat.VolumeDeltaKg);
        Assert.Null(squat.MaxLoadDeltaKg);

        var progress = (await s.Student.GetFromJsonAsync<ProgressRow>($"/api/v1/members/{s.Member.Id}/exercise-progress?exerciseId={s.Bench}"))!;
        Assert.Equal("Press de banca con barra", progress.ExerciseName);
        Assert.Equal([40m, 45m], progress.Points.Select(p => p.MaxLoadKg));
        Assert.Equal([880m, 990m], progress.Points.Select(p => p.VolumeKg));
        Assert.Equal(HttpStatusCode.NotFound, (await s.Student.GetAsync($"/api/v1/members/{s.Member.Id}/exercise-progress?exerciseId={s.Plank}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Student.GetAsync($"/api/v1/members/{s.Member.Id}/workout-sessions/compare?sessionA={first.Id}&sessionB={Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task No_se_compara_con_la_sesion_de_otro_alumno()
    {
        var s = await NewSetupAsync("CM1");
        var other = await NewSetupAsync("CM2");
        var plan = await CreatePlanAsync(s.TrainerClient, PlanBody(s));
        await AssignAsync(s.TrainerClient, plan.Id, s.Member.Id);
        var mine = await LogAsync(s.Student, SessionBody(await MemberPlanAsync(s.Student, s.Member.Id)));
        var plan2 = await CreatePlanAsync(other.TrainerClient, PlanBody(other));
        await AssignAsync(other.TrainerClient, plan2.Id, other.Member.Id);
        var theirs = await LogAsync(other.Student, SessionBody(await MemberPlanAsync(other.Student, other.Member.Id)));

        var response = await s.Student.GetAsync($"/api/v1/members/{s.Member.Id}/workout-sessions/compare?sessionA={mine.Id}&sessionB={theirs.Id}");

        await AssertProblemAsync(response, HttpStatusCode.NotFound, "training.session_not_found");
    }

    // ---- Base de datos y permisos -----------------------------------------------------------------------------

    [Fact]
    public async Task La_base_impide_reescribir_el_historial_de_entrenamiento()
    {
        var s = await NewSetupAsync("DB1");
        var plan = await CreatePlanAsync(s.TrainerClient, PlanBody(s));
        await AssignAsync(s.TrainerClient, plan.Id, s.Member.Id);
        var mine = await MemberPlanAsync(s.Student, s.Member.Id);
        var session = await LogAsync(s.Student, SessionBody(mine));
        var versionId = mine.Assignment.PlanVersionId;
        var planExerciseId = mine.Days[0].Exercises[0].Prescription.Id;

        // Con el rol de la aplicación (sin permisos) y con el administrador (triggers): versiones, ejercicios prescritos y series no cambian.
        foreach (var asApp in new[] { true, false })
        {
            await Assert.ThrowsAsync<PostgresException>(() => SqlAsync($"UPDATE training.plan_versions SET name = 'x' WHERE id = '{versionId}'", asApp));
            await Assert.ThrowsAsync<PostgresException>(() => SqlAsync($"DELETE FROM training.plan_versions WHERE id = '{versionId}'", asApp));
            await Assert.ThrowsAsync<PostgresException>(() => SqlAsync($"UPDATE training.plan_exercises SET sets = 9 WHERE id = '{planExerciseId}'", asApp));
            await Assert.ThrowsAsync<PostgresException>(() => SqlAsync($"DELETE FROM training.workout_sets WHERE tenant_id = '{s.Tenant.Id}'", asApp));
            await Assert.ThrowsAsync<PostgresException>(() => SqlAsync($"UPDATE training.workout_session_exercises SET completed = false WHERE session_id = '{session.Id}'", asApp));
            await Assert.ThrowsAsync<PostgresException>(() => SqlAsync($"DELETE FROM training.workout_sessions WHERE id = '{session.Id}'", asApp));
            await Assert.ThrowsAsync<PostgresException>(() => SqlAsync($"DELETE FROM training.training_assignments WHERE id = '{mine.Assignment.Id}'", asApp));
        }

        // Lo que registró el alumno no se reescribe ni con SQL de administración (solo se agrega el comentario del entrenador).
        await Assert.ThrowsAsync<PostgresException>(() => SqlAsync($"UPDATE training.workout_sessions SET member_notes = 'editada' WHERE id = '{session.Id}'"));
        await Assert.ThrowsAsync<PostgresException>(() => SqlAsync($"UPDATE training.training_assignments SET start_date = start_date + 1 WHERE id = '{mine.Assignment.Id}'"));
    }

    [Fact]
    public async Task La_base_rechaza_prescripciones_y_series_incoherentes_aunque_la_aplicacion_falle()
    {
        var s = await NewSetupAsync("DB2");
        var plan = await CreatePlanAsync(s.TrainerClient, PlanBody(s));
        await AssignAsync(s.TrainerClient, plan.Id, s.Member.Id);
        var mine = await MemberPlanAsync(s.Student, s.Member.Id);
        var day = mine.Days[0].Id;
        var tenant = s.Tenant.Id;

        // Un ejercicio por tiempo con repeticiones, o con carga, o una serie sin ninguna medida, no entran.
        await Assert.ThrowsAsync<PostgresException>(() => SqlAsync(
            $"INSERT INTO training.plan_exercises (id, tenant_id, day_id, exercise_id, position, exercise_name, tracking_mode, sets, reps, duration_seconds, rest_seconds) " +
            $"VALUES (gen_random_uuid(), '{tenant}', '{day}', '{s.Plank}', 9, 'x', 'Duration', 3, 10, 30, 30)"));
        await Assert.ThrowsAsync<PostgresException>(() => SqlAsync(
            $"INSERT INTO training.plan_exercises (id, tenant_id, day_id, exercise_id, position, exercise_name, tracking_mode, sets, reps, rest_seconds, recommended_load_kg) " +
            $"VALUES (gen_random_uuid(), '{tenant}', '{day}', '{s.Squat}', 9, 'x', 'Reps', 3, 10, 30, 20)"));
        await Assert.ThrowsAsync<PostgresException>(() => SqlAsync(
            $"INSERT INTO training.workout_sets (id, tenant_id, session_exercise_id, set_number) VALUES (gen_random_uuid(), '{tenant}', gen_random_uuid(), 1)"));
    }

    [Fact]
    public async Task Los_permisos_de_rutinas_se_reparten_entre_propietario_administrador_entrenador_y_alumno()
    {
        var s = await NewSetupAsync("PER");
        var roles = (await s.Tenant.Owner.GetFromJsonAsync<RoleDto[]>("/api/v1/roles"))!.ToDictionary(r => r.Name, r => r.Permissions);

        Assert.All(new[] { "training.plans.read", "training.plans.manage", "training.members.manage", "training.member.read" }, p => Assert.Contains(p, roles["Propietario"]));
        Assert.Contains("training.sessions.log", roles["Entrenador"]);
        Assert.DoesNotContain("training.sessions.log", roles["AdministradorSucursal"]);
        Assert.Equal(["me.read", "nutrition.logs.record", "nutrition.member.read", "training.member.read", "training.sessions.log", "wellness.assessments.read", "wellness.goals.read", "wellness.photos.manage", "wellness.photos.read"], roles["Alumno"].Order());
        Assert.DoesNotContain("training.plans.read", roles["Recepcion"]);
        var recepcion = await ClientWithRoleAsync(api, s.Tenant, Unique("rec"), true, ["members.read"]);
        Assert.Equal(HttpStatusCode.Forbidden, (await recepcion.GetAsync("/api/v1/training-plans")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await recepcion.PostAsJsonAsync("/api/v1/workout-sessions", new { dayId = Guid.NewGuid(), exercises = Array.Empty<object>() })).StatusCode);
    }
}
