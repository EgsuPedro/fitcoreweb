using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FitCore.Api.IntegrationTests.Infrastructure;
using Npgsql;
using static FitCore.Api.IntegrationTests.Infrastructure.CommercialTestApi;

namespace FitCore.Api.IntegrationTests;

[Collection(ApiCollection1.Name)]
public class GoalAndAdherenceTests(ApiFixture api)
{
    private sealed record GoalRow(
        Guid Id, Guid MemberId, string Title, string Category, string? Metric, string? Unit, decimal? TargetValue, decimal? BaselineValue, decimal? CurrentValue,
        decimal? ProgressPercent, bool? Reached, DateOnly StartDate, DateOnly? TargetDate, bool Overdue, string Status, DateOnly? ClosedOn, string? CloseNote);

    private sealed record TrainingWeekRow(DateOnly WeekStart, int DaysCovered, int Expected, int Completed, decimal? Percent);

    private sealed record TrainingAdherenceRow(DateOnly From, DateOnly To, int Expected, int Sessions, decimal? Percent, TrainingWeekRow[] Weeks);

    private sealed record NutritionWeekRow(DateOnly WeekStart, int Planned, int Completed, int Partial, int Skipped, int NotLogged, decimal? Percent);

    private sealed record NutritionAdherenceRow(DateOnly From, DateOnly To, int Planned, int Completed, int Partial, int Skipped, int NotLogged, decimal? Percent, NutritionWeekRow[] Weeks);

    private sealed record TrainerRow(Guid Id);

    private sealed record IdRow(Guid Id);

    private sealed record Setup(TestTenant Tenant, Guid Branch, HttpClient Trainer, MemberDto Member, HttpClient Student);

    private static DateOnly Today =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/Guayaquil")).DateTime);

    private const string StudentPassword = "Nueva-Clave-2026x";

    private static string Unique(string prefix) => $"{prefix}.{Guid.NewGuid():N}"[..22] + "@gym.test";

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd");

    private async Task<Setup> NewSetupAsync(string code, string birthDate = "1990-05-10")
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var branch = await TestApi.CreateBranchAsync(tenant.Owner, code);
        var trainerEmail = Unique("entrena");
        var user = await TestApi.CreateUserAsync(tenant.Owner, trainerEmail, "Entrenador", branch);
        var profile = (await (await tenant.Owner.PostAsJsonAsync("/api/v1/trainers", new { userId = user.Id })).Content.ReadFromJsonAsync<TrainerRow>())!;
        var trainer = TestApi.Authenticated(api, await TestApi.LoginAsync(api, tenant.Slug, trainerEmail, TestApi.UserPassword));

        var studentEmail = Unique("alumno");
        var memberResponse = await tenant.Owner.PostAsJsonAsync("/api/v1/members", new
        {
            branchId = branch, firstName = "Diana", lastName = "Prueba", idType = "Cedula", idNumber = MemberTests.NewCedula(), email = studentEmail, phone = "0991234567", birthDate
        });
        Assert.Equal(HttpStatusCode.Created, memberResponse.StatusCode);
        var member = (await memberResponse.Content.ReadFromJsonAsync<MemberDto>())!;
        Assert.Equal(HttpStatusCode.Created, (await tenant.Owner.PostAsJsonAsync($"/api/v1/trainers/{profile.Id}/members", new { memberId = member.Id })).StatusCode);

        var access = await (await tenant.Owner.PostAsync($"/api/v1/members/{member.Id}/access", null)).Content.ReadFromJsonAsync<JsonElement>();
        var temporary = access.GetProperty("temporaryPassword").GetString()!;
        using var temp = TestApi.Authenticated(api, await TestApi.LoginAsync(api, tenant.Slug, studentEmail, temporary));
        Assert.Equal(HttpStatusCode.NoContent, (await temp.PostAsJsonAsync("/api/v1/auth/change-password", new { currentPassword = temporary, newPassword = StudentPassword })).StatusCode);
        var student = TestApi.Authenticated(api, await TestApi.LoginAsync(api, tenant.Slug, studentEmail, StudentPassword));
        return new Setup(tenant, branch, trainer, member, student);
    }

    private async Task<HttpClient> UserClientAsync(TestTenant tenant, Guid branch, string role)
    {
        var email = Unique(role.ToLowerInvariant());
        await TestApi.CreateUserAsync(tenant.Owner, email, role, branch);
        return TestApi.Authenticated(api, await TestApi.LoginAsync(api, tenant.Slug, email, TestApi.UserPassword));
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, await TestApi.ProblemCodeAsync(response));
    }

    private static async Task RecordAssessmentAsync(HttpClient trainer, Guid memberId, decimal? weight, decimal? height = null, decimal? waist = null)
    {
        var response = await trainer.PostAsJsonAsync($"/api/v1/members/{memberId}/assessments", new { weightKg = weight, heightCm = height, waistCm = waist, source = "Scale" });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
    }

    private static object Goal(string title = "Meta", string category = "BodyMetric", string? metric = "WeightKg", decimal? target = 80m, decimal? baseline = null, DateOnly? targetDate = null) =>
        new { title, category, metric, targetValue = target, baselineValue = baseline, targetDate };

    private static async Task<GoalRow> CreateGoalAsync(HttpClient client, Guid memberId, object body)
    {
        var response = await client.PostAsJsonAsync($"/api/v1/members/{memberId}/goals", body);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"Respondió {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<GoalRow>())!;
    }

    private static async Task<GoalRow[]> GoalsAsync(HttpClient client, Guid memberId, string query = "") =>
        (await client.GetFromJsonAsync<GoalRow[]>($"/api/v1/members/{memberId}/goals{query}"))!;

    private async Task SqlAsync(string sql, bool asApp = false)
    {
        await using var connection = new NpgsqlConnection(asApp ? api.AppConnection : api.AdminConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    // ---- Objetivos ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task El_progreso_de_una_meta_corporal_sale_de_las_evaluaciones_y_el_objetivo_no_se_cierra_solo()
    {
        var s = await NewSetupAsync("PRG");
        await RecordAssessmentAsync(s.Trainer, s.Member.Id, 90m, 175m);

        var goal = await CreateGoalAsync(s.Trainer, s.Member.Id, Goal("Bajar a 80 kg", target: 80m, targetDate: Today.AddMonths(3)));

        Assert.Equal((90m, 90m, 0m, false, "kg"), (goal.BaselineValue, goal.CurrentValue, goal.ProgressPercent, goal.Reached, goal.Unit));
        await RecordAssessmentAsync(s.Trainer, s.Member.Id, 85m);
        Assert.Equal(50m, (await GoalsAsync(s.Student, s.Member.Id)).Single().ProgressPercent);
        await RecordAssessmentAsync(s.Trainer, s.Member.Id, 79m);
        var reached = (await GoalsAsync(s.Student, s.Member.Id)).Single();
        Assert.Equal((100m, true, "Active"), (reached.ProgressPercent, reached.Reached, reached.Status));

        var closed = await s.Trainer.PostAsJsonAsync($"/api/v1/goals/{goal.Id}/close", new { status = "Achieved", note = "Meta lograda" });
        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
        var row = (await closed.Content.ReadFromJsonAsync<GoalRow>())!;
        Assert.Equal(("Achieved", Today), (row.Status, row.ClosedOn));
    }

    [Fact]
    public async Task Sin_evaluaciones_el_objetivo_no_tiene_progreso_y_la_partida_puede_indicarse()
    {
        var s = await NewSetupAsync("NOA");

        var noData = await CreateGoalAsync(s.Trainer, s.Member.Id, Goal("Cintura", metric: "WaistCm", target: 80m));
        Assert.Null(noData.BaselineValue);
        Assert.Null(noData.ProgressPercent);
        Assert.Null(noData.CurrentValue);

        var withBaseline = await CreateGoalAsync(s.Trainer, s.Member.Id, Goal("Peso", metric: "WeightKg", target: 70m, baseline: 80m));
        Assert.Equal(80m, withBaseline.BaselineValue);
        Assert.Null(withBaseline.ProgressPercent);
        await RecordAssessmentAsync(s.Trainer, s.Member.Id, 75m);
        Assert.Equal(50m, (await GoalsAsync(s.Trainer, s.Member.Id)).Single(g => g.Metric == "WeightKg").ProgressPercent);
    }

    [Fact]
    public async Task La_constancia_se_mide_con_las_sesiones_de_los_ultimos_siete_dias()
    {
        var s = await NewSetupAsync("SES");
        var exerciseId = await FirstExerciseAsync(s.Tenant.Owner);
        var plan = await CreateTrainingPlanAsync(s.Trainer, exerciseId, days: 3);
        await AssignTrainingAsync(s.Trainer, plan, s.Member.Id);
        var goal = await CreateGoalAsync(s.Trainer, s.Member.Id, Goal("Entrenar 3 veces", "SessionsPerWeek", null, 3m));
        Assert.Equal((0m, 0m, false, "sesiones/semana"), (goal.CurrentValue, goal.ProgressPercent, goal.Reached, goal.Unit));

        await LogSessionAsync(s.Student, s.Member.Id, 2);

        var updated = (await GoalsAsync(s.Student, s.Member.Id)).Single();
        Assert.Equal((2m, 67m, false), (updated.CurrentValue, updated.ProgressPercent, updated.Reached));
        await LogSessionAsync(s.Student, s.Member.Id, 1);
        Assert.Equal((100m, true), ((await GoalsAsync(s.Student, s.Member.Id)).Single().ProgressPercent, (await GoalsAsync(s.Student, s.Member.Id)).Single().Reached));
    }

    [Theory]
    [InlineData("metrica_faltante", "goal.metric_invalid")]
    [InlineData("meta_fuera_de_rango", "goal.target_invalid")]
    [InlineData("sesiones_decimales", "goal.target_invalid")]
    [InlineData("libre_con_meta", "goal.custom_has_metric")]
    [InlineData("fecha_pasada", "goal.date_invalid")]
    public async Task Los_objetivos_incoherentes_se_rechazan_con_su_codigo(string caso, string code)
    {
        var s = await NewSetupAsync("VAL");
        var body = caso switch
        {
            "metrica_faltante" => Goal(metric: null),
            "meta_fuera_de_rango" => Goal(target: 900m),
            "sesiones_decimales" => Goal("Constancia", "SessionsPerWeek", null, 2.5m),
            "libre_con_meta" => Goal("Libre", "Custom", null, 5m),
            _ => Goal(targetDate: Today.AddDays(-1))
        };

        await AssertProblemAsync(await s.Trainer.PostAsJsonAsync($"/api/v1/members/{s.Member.Id}/goals", body), HttpStatusCode.UnprocessableEntity, code);
    }

    [Fact]
    public async Task No_se_acepta_una_meta_de_peso_que_deja_el_imc_bajo_el_rango_a_un_adulto_con_estatura()
    {
        var s = await NewSetupAsync("UNS");
        var minor = await NewSetupAsync("MEN", birthDate: Iso(Today.AddYears(-12)));
        await RecordAssessmentAsync(s.Trainer, s.Member.Id, 70m, 175m);
        await RecordAssessmentAsync(minor.Trainer, minor.Member.Id, 45m, 150m);

        // 175 cm: 56 kg da IMC 18,3 (bajo); 57 kg da 18,6.
        await AssertProblemAsync(await s.Trainer.PostAsJsonAsync($"/api/v1/members/{s.Member.Id}/goals", Goal(target: 56m)), HttpStatusCode.UnprocessableEntity, "goal.target_unsafe");
        Assert.Equal(HttpStatusCode.Created, (await s.Trainer.PostAsJsonAsync($"/api/v1/members/{s.Member.Id}/goals", Goal(target: 57m))).StatusCode);
        // En un menor los rangos de adultos no aplican: no se bloquea por ellos.
        Assert.Equal(HttpStatusCode.Created, (await minor.Trainer.PostAsJsonAsync($"/api/v1/members/{minor.Member.Id}/goals", Goal(target: 40m))).StatusCode);
    }

    [Fact]
    public async Task Hay_un_maximo_de_objetivos_activos_y_no_se_repite_la_medida()
    {
        var s = await NewSetupAsync("MAX");

        await CreateGoalAsync(s.Trainer, s.Member.Id, Goal("Peso", metric: "WeightKg", target: 70m));
        await AssertProblemAsync(await s.Trainer.PostAsJsonAsync($"/api/v1/members/{s.Member.Id}/goals", Goal("Otro peso", metric: "WeightKg", target: 65m)), HttpStatusCode.Conflict, "goal.duplicate_metric");
        for (var i = 0; i < 4; i++)
        {
            await CreateGoalAsync(s.Trainer, s.Member.Id, Goal($"Libre {i}", "Custom", null, null));
        }

        await AssertProblemAsync(await s.Trainer.PostAsJsonAsync($"/api/v1/members/{s.Member.Id}/goals", Goal("Sexto", "Custom", null, null)), HttpStatusCode.Conflict, "goal.too_many_active");
        var first = (await GoalsAsync(s.Trainer, s.Member.Id)).First(g => g.Category == "Custom");
        await s.Trainer.PostAsJsonAsync($"/api/v1/goals/{first.Id}/close", new { status = "Achieved" });
        Assert.Equal(HttpStatusCode.Created, (await s.Trainer.PostAsJsonAsync($"/api/v1/members/{s.Member.Id}/goals", Goal("Sexto", "Custom", null, null))).StatusCode);
    }

    [Fact]
    public async Task Cerrar_un_objetivo_abandonado_exige_motivo_y_un_cerrado_no_se_reabre_ni_se_borra()
    {
        var s = await NewSetupAsync("CLO");
        var goal = await CreateGoalAsync(s.Trainer, s.Member.Id, Goal("Libre", "Custom", null, null));
        var url = $"/api/v1/goals/{goal.Id}/close";

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await s.Trainer.PostAsJsonAsync(url, new { status = "Abandoned" })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await s.Trainer.PostAsJsonAsync(url, new { status = "Active" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.Trainer.PostAsJsonAsync(url, new { status = "Abandoned", note = "Lesión de rodilla" })).StatusCode);
        await AssertProblemAsync(await s.Trainer.PostAsJsonAsync(url, new { status = "Achieved" }), HttpStatusCode.Conflict, "goal.already_closed");
        Assert.Equal(["Abandoned"], (await GoalsAsync(s.Student, s.Member.Id, "?status=Abandoned")).Select(g => g.Status));
        Assert.Empty(await GoalsAsync(s.Student, s.Member.Id, "?status=Active"));

        // Con el administrador de la base actúan los disparadores (el rol de la aplicación sin tenant ni siquiera ve la fila por RLS).
        await Assert.ThrowsAsync<PostgresException>(() => SqlAsync($"DELETE FROM wellness.goals WHERE id = '{goal.Id}'"));
        await Assert.ThrowsAsync<PostgresException>(() => SqlAsync($"UPDATE wellness.goals SET status = 'Active', closed_on = NULL WHERE id = '{goal.Id}'"));
    }

    [Fact]
    public async Task La_base_impide_cambiar_la_definicion_de_un_objetivo_abierto()
    {
        var s = await NewSetupAsync("DEF");
        var goal = await CreateGoalAsync(s.Trainer, s.Member.Id, Goal("Peso", target: 70m));

        await Assert.ThrowsAsync<PostgresException>(() => SqlAsync($"UPDATE wellness.goals SET target_value = 60 WHERE id = '{goal.Id}'"));
        var ex = await Assert.ThrowsAsync<PostgresException>(() => SqlAsync(
            $"INSERT INTO wellness.goals (id, tenant_id, member_id, title, category, metric, target_value, start_date, status, created_by, created_at) " +
            $"VALUES (gen_random_uuid(), '{s.Tenant.Id}', '{s.Member.Id}', 'x', 'Custom', 'WeightKg', NULL, current_date, 'Active', " +
            $"(SELECT id FROM identity.app_users WHERE tenant_id = '{s.Tenant.Id}' LIMIT 1), now())"));
        Assert.Equal("ck_goals_category_metric", ex.ConstraintName);
    }

    [Fact]
    public async Task El_alumno_ve_solo_los_suyos_no_los_gestiona_y_ni_propietario_ni_administrador_acceden()
    {
        var s = await NewSetupAsync("ACC");
        var other = await NewSetupAsync("AC2");
        var goal = await CreateGoalAsync(s.Trainer, s.Member.Id, Goal("Libre", "Custom", null, null));
        var admin = await UserClientAsync(s.Tenant, s.Branch, "AdministradorSucursal");
        var strangerEmail = Unique("ajeno");
        var stranger = await TestApi.CreateUserAsync(s.Tenant.Owner, strangerEmail, "Entrenador", s.Branch);
        await s.Tenant.Owner.PostAsJsonAsync("/api/v1/trainers", new { userId = stranger.Id });
        var strangerClient = TestApi.Authenticated(api, await TestApi.LoginAsync(api, s.Tenant.Slug, strangerEmail, TestApi.UserPassword));
        var url = $"/api/v1/members/{s.Member.Id}/goals";

        Assert.Equal([goal.Id], (await GoalsAsync(s.Student, s.Member.Id)).Select(g => g.Id));
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Student.PostAsJsonAsync(url, Goal("Mío", "Custom", null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Student.PostAsJsonAsync($"/api/v1/goals/{goal.Id}/close", new { status = "Achieved" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Student.GetAsync($"/api/v1/members/{other.Member.Id}/goals")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Student.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Trainer.PostAsJsonAsync($"/api/v1/goals/{goal.Id}/close", new { status = "Achieved" })).StatusCode);
        await AssertProblemAsync(await strangerClient.GetAsync(url), HttpStatusCode.NotFound, "goal.member_not_found");
        await AssertProblemAsync(await strangerClient.PostAsJsonAsync(url, Goal("Otra meta", "Custom", null, null)), HttpStatusCode.NotFound, "goal.member_not_found");
        foreach (var client in new[] { s.Tenant.Owner, admin })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(url)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(url, Goal("Otra meta", "Custom", null, null))).StatusCode);
        }
    }

    [Fact]
    public async Task Se_audita_el_hecho_sin_registrar_el_texto_del_objetivo()
    {
        var s = await NewSetupAsync("AUD");
        var secret = $"meta-secreta-{Guid.NewGuid():N}";
        var goal = await CreateGoalAsync(s.Trainer, s.Member.Id, Goal(secret, "Custom", null, null));
        await s.Trainer.GetAsync($"/api/v1/members/{s.Member.Id}/goals");
        await s.Trainer.PostAsJsonAsync($"/api/v1/goals/{goal.Id}/close", new { status = "Abandoned", note = secret });

        var audit = await s.Tenant.Owner.GetStringAsync("/api/v1/audit-logs?pageSize=200");

        foreach (var action in new[] { "Wellness.GoalCreated", "Wellness.GoalsRead", "Wellness.GoalClosed" })
        {
            Assert.Contains(action, audit);
        }

        Assert.DoesNotContain(secret, audit);
    }

    // ---- Adherencia de entrenamiento ------------------------------------------------------------------------------

    private static async Task<Guid> FirstExerciseAsync(HttpClient owner)
    {
        var page = await owner.GetFromJsonAsync<PagedDto<IdRow>>("/api/v1/exercises?scope=global&search=Sentadilla%20libre&pageSize=5");
        return page!.Items[0].Id;
    }

    private static async Task<Guid> CreateTrainingPlanAsync(HttpClient trainer, Guid exerciseId, int days)
    {
        var response = await trainer.PostAsJsonAsync("/api/v1/training-plans", new
        {
            name = "Constancia", level = "Beginner",
            days = Enumerable.Range(1, days).Select(i => new { name = $"Día {i}", exercises = new[] { new { exerciseId, sets = 3, reps = 12, restSeconds = 60 } } }).ToArray()
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<IdRow>())!.Id;
    }

    private static async Task AssignTrainingAsync(HttpClient trainer, Guid planId, Guid memberId) =>
        Assert.Equal(HttpStatusCode.Created, (await trainer.PostAsJsonAsync($"/api/v1/training-plans/{planId}/assignments", new { memberId })).StatusCode);

    private static async Task LogSessionAsync(HttpClient student, Guid memberId, int count)
    {
        var plan = await student.GetFromJsonAsync<JsonElement>($"/api/v1/members/{memberId}/training-plan");
        var day = plan.GetProperty("days")[0];
        for (var i = 0; i < count; i++)
        {
            var response = await student.PostAsJsonAsync("/api/v1/workout-sessions", new
            {
                dayId = day.GetProperty("id").GetGuid(),
                exercises = new[] { new { planExerciseId = day.GetProperty("exercises")[0].GetProperty("prescription").GetProperty("id").GetGuid(), completed = true, sets = new[] { new { reps = 12 } } } }
            });
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }
    }

    [Fact]
    public async Task La_adherencia_de_entrenamiento_compara_las_sesiones_con_los_dias_de_la_rutina_y_no_pasa_de_100()
    {
        var s = await NewSetupAsync("ADH");
        var plan = await CreateTrainingPlanAsync(s.Trainer, await FirstExerciseAsync(s.Tenant.Owner), days: 3);
        await AssignTrainingAsync(s.Trainer, plan, s.Member.Id);
        await LogSessionAsync(s.Student, s.Member.Id, 2);

        var adherence = (await s.Student.GetFromJsonAsync<TrainingAdherenceRow>($"/api/v1/members/{s.Member.Id}/training-adherence"))!;

        // La rutina empezó hoy: solo 1 de los 7 días de la semana estuvo vigente → se esperaba ceil(3 × 1 / 7) = 1 sesión.
        var week = adherence.Weeks.Single(w => w.Expected > 0);
        Assert.Equal((1, 1, 2, 100m), (week.DaysCovered, week.Expected, week.Completed, week.Percent));
        Assert.Equal((1, 2, 100m), (adherence.Expected, adherence.Sessions, adherence.Percent));
        Assert.Equal(Today, adherence.To);
        Assert.Equal(Today.AddDays(-27), adherence.From);
        // Las semanas sin rutina no tienen porcentaje (no se supone nada).
        Assert.All(adherence.Weeks.Where(w => w.Expected == 0), w => Assert.Null(w.Percent));
        Assert.Equal(adherence.Weeks.Select(w => w.WeekStart).Order(), adherence.Weeks.Select(w => w.WeekStart));
    }

    [Fact]
    public async Task Sin_rutina_no_hay_adherencia_y_el_rango_se_valida()
    {
        var s = await NewSetupAsync("SIN");
        var url = $"/api/v1/members/{s.Member.Id}/training-adherence";

        var empty = (await s.Trainer.GetFromJsonAsync<TrainingAdherenceRow>(url))!;
        Assert.Equal((0, 0), (empty.Expected, empty.Sessions));
        Assert.Null(empty.Percent);
        await AssertProblemAsync(await s.Trainer.GetAsync($"{url}?from={Iso(Today)}&to={Iso(Today.AddDays(-3))}"), HttpStatusCode.UnprocessableEntity, "adherence.range_invalid");
        await AssertProblemAsync(await s.Trainer.GetAsync($"{url}?from={Iso(Today.AddDays(-200))}"), HttpStatusCode.UnprocessableEntity, "adherence.range_invalid");
        // El futuro no se evalúa: el rango se recorta a hoy.
        Assert.Equal(Today, (await s.Trainer.GetFromJsonAsync<TrainingAdherenceRow>($"{url}?to={Iso(Today.AddDays(10))}"))!.To);
    }

    [Fact]
    public async Task La_adherencia_de_entrenamiento_respeta_el_alcance()
    {
        var s = await NewSetupAsync("SCO");
        var other = await NewSetupAsync("SC2");
        var url = $"/api/v1/members/{s.Member.Id}/training-adherence";

        Assert.Equal(HttpStatusCode.OK, (await s.Student.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Student.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Trainer.GetAsync(url)).StatusCode);
    }

    // ---- Adherencia de nutrición ----------------------------------------------------------------------------------

    private static object Meal(string name) => new { name, items = new[] { new { foodName = "Arroz", quantity = 100, unit = "g" } } };

    private async Task<(HttpClient Nutritionist, Guid PlanId)> NutritionAsync(Setup s, int meals)
    {
        var nutritionist = await UserClientAsync(s.Tenant, s.Branch, "Nutricionista");
        var response = await nutritionist.PostAsJsonAsync("/api/v1/nutrition-plans", new { name = "Plan", meals = Enumerable.Range(1, meals).Select(i => Meal($"Comida {i}")).ToArray() });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var planId = (await response.Content.ReadFromJsonAsync<IdRow>())!.Id;
        Assert.Equal(HttpStatusCode.Created, (await nutritionist.PostAsJsonAsync($"/api/v1/nutrition-plans/{planId}/assignments", new { memberId = s.Member.Id })).StatusCode);
        return (nutritionist, planId);
    }

    [Fact]
    public async Task La_adherencia_de_nutricion_cuenta_completas_parciales_y_omitidas_y_lo_pendiente_de_hoy_no_cuenta_en_contra()
    {
        var s = await NewSetupAsync("NUT");
        var (nutritionist, _) = await NutritionAsync(s, meals: 3);
        var url = $"/api/v1/members/{s.Member.Id}/nutrition-adherence";

        // Hoy sin registros: nada planeado en contra todavía.
        var untouched = (await s.Student.GetFromJsonAsync<NutritionAdherenceRow>(url))!;
        Assert.Equal((0, 0), (untouched.Planned, untouched.NotLogged));
        Assert.Null(untouched.Percent);

        var meals = (await s.Student.GetFromJsonAsync<JsonElement>($"/api/v1/members/{s.Member.Id}/nutrition-plan")).GetProperty("plan").GetProperty("meals");
        var statuses = new[] { "Completed", "Partial", "Skipped" };
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.Created, (await s.Student.PostAsJsonAsync("/api/v1/meal-logs", new { mealId = meals[i].GetProperty("id").GetGuid(), status = statuses[i] })).StatusCode);
        }

        var adherence = (await s.Student.GetFromJsonAsync<NutritionAdherenceRow>(url))!;
        Assert.Equal((3, 1, 1, 1, 0), (adherence.Planned, adherence.Completed, adherence.Partial, adherence.Skipped, adherence.NotLogged));
        Assert.Equal(50m, adherence.Percent);
        Assert.Equal(50m, adherence.Weeks.Single(w => w.Planned > 0).Percent);
        // El nutricionista lo lee y queda auditado, sin contenido.
        Assert.Equal(HttpStatusCode.OK, (await nutritionist.GetAsync(url)).StatusCode);
        Assert.Contains("Wellness.NutritionAdherenceRead", await s.Tenant.Owner.GetStringAsync("/api/v1/audit-logs?pageSize=200"));
    }

    [Fact]
    public async Task La_adherencia_de_nutricion_es_sensible_el_entrenador_el_propietario_y_otros_alumnos_no_la_ven()
    {
        var s = await NewSetupAsync("NSE");
        var other = await NewSetupAsync("NS2");
        await NutritionAsync(s, meals: 2);
        var url = $"/api/v1/members/{s.Member.Id}/nutrition-adherence";

        Assert.Equal(HttpStatusCode.Forbidden, (await s.Trainer.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Tenant.Owner.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Student.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.Student.GetAsync(url)).StatusCode);
        await AssertProblemAsync(await s.Student.GetAsync($"{url}?from={Iso(Today.AddDays(-200))}"), HttpStatusCode.UnprocessableEntity, "adherence.range_invalid");
    }
}
