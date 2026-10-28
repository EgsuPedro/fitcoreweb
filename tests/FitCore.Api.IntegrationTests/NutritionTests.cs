using System.Net;
using System.Net.Http.Json;
using FitCore.Api.IntegrationTests.Infrastructure;
using Npgsql;
using static FitCore.Api.IntegrationTests.Infrastructure.CommercialTestApi;

namespace FitCore.Api.IntegrationTests;

[Collection(ApiCollection4.Name)]
public class NutritionTests(ApiFixture api)
{
    private sealed record ItemRow(Guid Id, int Position, string FoodName, decimal Quantity, string Unit, int? Calories, decimal? ProteinG, decimal? CarbsG, decimal? FatG);

    private sealed record MealRow(Guid Id, int Position, string Name, int? EstimatedCalories, ItemRow[] Items);

    private sealed record PlanRow(
        Guid Id, string Status, int Version, Guid VersionId, bool IsCurrentVersion, string Name, int? TargetCalories, MealRow[] Meals);

    private sealed record PlanSummaryRow(Guid Id, string Name, string Status, int Version, int MealCount, int ActiveAssignments);

    private sealed record VersionRow(Guid Id, int Number, string Name, string? ChangeNote);

    private sealed record AssignRow(
        Guid Id, Guid PlanId, Guid PlanVersionId, string PlanName, int PlanVersion, Guid MemberId, DateOnly StartDate, DateOnly? EndDate,
        DateOnly? EndedOn, string? EndReason, string Status);

    private sealed record ProfileRow(Guid Id, Guid MemberId, string[] Allergies, string[] Intolerances, string[] Restrictions, string? Notes, DateTimeOffset RecordedAt);

    private sealed record MemberPlanRow(AssignRow Assignment, PlanRow Plan, ProfileRow? Profile);

    private sealed record LogRow(Guid Id, Guid MemberId, Guid MealId, string MealName, DateOnly Date, string Status, string? Notes, int PlanVersion);

    private sealed record DayRow(DateOnly Date, int PlannedMeals, int Completed, int Partial, int Skipped, int NotLogged);

    private sealed record LogsRow(DateOnly From, DateOnly To, DayRow[] Days, LogRow[] Logs);

    private sealed record Setup(TestTenant Tenant, Guid Branch, HttpClient Nutritionist, MemberDto Member, HttpClient Student);

    private static DateOnly Today =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/Guayaquil")).DateTime);

    private const string StudentPassword = "Nueva-Clave-2026x";

    private static string Unique(string prefix) => $"{prefix}.{Guid.NewGuid():N}"[..22] + "@gym.test";

    private async Task<HttpClient> UserClientAsync(TestTenant tenant, Guid branch, string role)
    {
        var email = Unique(role.ToLowerInvariant());
        await TestApi.CreateUserAsync(tenant.Owner, email, role, branch);
        return TestApi.Authenticated(api, await TestApi.LoginAsync(api, tenant.Slug, email, TestApi.UserPassword));
    }

    private async Task<Setup> NewSetupAsync(string code)
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var branch = await TestApi.CreateBranchAsync(tenant.Owner, code);
        var nutritionist = await UserClientAsync(tenant, branch, "Nutricionista");
        var studentEmail = Unique("alumno");
        var memberResponse = await tenant.Owner.PostAsJsonAsync("/api/v1/members", new
        {
            branchId = branch, firstName = "Beatriz", lastName = "Prueba", idType = "Cedula", idNumber = MemberTests.NewCedula(), email = studentEmail, phone = "0991234567"
        });
        Assert.Equal(HttpStatusCode.Created, memberResponse.StatusCode);
        var member = (await memberResponse.Content.ReadFromJsonAsync<MemberDto>())!;

        var access = await (await tenant.Owner.PostAsync($"/api/v1/members/{member.Id}/access", null)).Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var temporary = access.GetProperty("temporaryPassword").GetString()!;
        using var temp = TestApi.Authenticated(api, await TestApi.LoginAsync(api, tenant.Slug, studentEmail, temporary));
        Assert.Equal(HttpStatusCode.NoContent, (await temp.PostAsJsonAsync("/api/v1/auth/change-password", new { currentPassword = temporary, newPassword = StudentPassword })).StatusCode);
        var student = TestApi.Authenticated(api, await TestApi.LoginAsync(api, tenant.Slug, studentEmail, StudentPassword));
        return new Setup(tenant, branch, nutritionist, member, student);
    }

    private static object Item(string food = "Arroz integral cocido", decimal quantity = 150, string unit = "g", int? kcal = 170, decimal? p = 3.5m, decimal? c = 35m, decimal? f = 1.4m) =>
        new { foodName = food, quantity, unit, calories = kcal, proteinG = p, carbsG = c, fatG = f };

    private static object Meal(string name, params object[] items) => new { name, suggestedTime = "08:00", items };

    private static object PlanBody(string name = "Mantenimiento", string? changeNote = null, int? calories = 2000, object[]? meals = null) => new
    {
        name,
        goal = "Mantener el peso",
        description = "Plan orientativo de cuatro comidas",
        targetCalories = calories,
        targetProteinG = 110,
        targetCarbsG = 250,
        targetFatG = 65,
        recommendations = "Hidratarse bien durante el día.",
        changeNote,
        meals = meals ?? new object[]
        {
            Meal("Desayuno", Item("Avena", 50, "g", 190), Item("Plátano", 1, "unidad", 90)),
            Meal("Almuerzo", Item(), Item("Pollo a la plancha", 120, "g", null, null, null, null))
        }
    };

    private static async Task<PlanRow> CreatePlanAsync(HttpClient client, object body)
    {
        var response = await client.PostAsJsonAsync("/api/v1/nutrition-plans", body);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"Respondió {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<PlanRow>())!;
    }

    private static async Task<AssignRow> AssignAsync(HttpClient client, Guid planId, Guid memberId, DateOnly? start = null, DateOnly? end = null)
    {
        var response = await client.PostAsJsonAsync($"/api/v1/nutrition-plans/{planId}/assignments", new { memberId, startDate = start, endDate = end });
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"Respondió {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<AssignRow>())!;
    }

    private static async Task<MemberPlanRow> MemberPlanAsync(HttpClient client, Guid memberId) =>
        (await client.GetFromJsonAsync<MemberPlanRow>($"/api/v1/members/{memberId}/nutrition-plan"))!;

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

    private static async Task<LogRow> LogAsync(HttpClient client, object body)
    {
        var response = await client.PostAsJsonAsync("/api/v1/meal-logs", body);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"Respondió {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<LogRow>())!;
    }

    // ---- Permisos sensibles ------------------------------------------------------------------------------------

    [Fact]
    public async Task Ni_el_propietario_ni_el_administrador_ni_el_entrenador_acceden_a_nutricion_por_defecto()
    {
        var s = await NewSetupAsync("PRM");
        var plan = await CreatePlanAsync(s.Nutritionist, PlanBody());
        var admin = await UserClientAsync(s.Tenant, s.Branch, "AdministradorSucursal");
        var trainer = await UserClientAsync(s.Tenant, s.Branch, "Entrenador");

        foreach (var client in new[] { s.Tenant.Owner, admin, trainer })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/nutrition-plans")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/v1/nutrition-plans/{plan.Id}")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/v1/nutrition-plans", PlanBody())).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/v1/nutrition-plans/{plan.Id}/assignments", new { memberId = s.Member.Id })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/v1/members/{s.Member.Id}/nutrition-plan")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/v1/members/{s.Member.Id}/dietary-profile")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/v1/members/{s.Member.Id}/meal-logs")).StatusCode);
        }

        var roles = (await s.Tenant.Owner.GetFromJsonAsync<RoleDto[]>("/api/v1/roles"))!.ToDictionary(r => r.Name, r => r.Permissions);
        Assert.DoesNotContain(roles["Propietario"], p => p.StartsWith("nutrition.", StringComparison.Ordinal));
        Assert.DoesNotContain(roles["AdministradorSucursal"], p => p.StartsWith("nutrition.", StringComparison.Ordinal));
        Assert.DoesNotContain(roles["Entrenador"], p => p.StartsWith("nutrition.", StringComparison.Ordinal));
        Assert.Equal(6, roles["Nutricionista"].Count(p => p.StartsWith("nutrition.", StringComparison.Ordinal)));
        Assert.Equal(["nutrition.logs.record", "nutrition.member.read"], roles["Alumno"].Where(p => p.StartsWith("nutrition.", StringComparison.Ordinal)).Order());
    }

    [Fact]
    public async Task El_propietario_puede_conceder_nutricion_a_un_rol_propio_y_el_alumno_no_gestiona_planes()
    {
        var s = await NewSetupAsync("GRN");

        var granted = await ClientWithRoleAsync(api, s.Tenant, Unique("nutri"), true, ["nutrition.plans.read"]);

        Assert.Equal(HttpStatusCode.OK, (await granted.GetAsync("/api/v1/nutrition-plans")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await granted.PostAsJsonAsync("/api/v1/nutrition-plans", PlanBody())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Student.GetAsync("/api/v1/nutrition-plans")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Student.PostAsJsonAsync("/api/v1/nutrition-plans", PlanBody())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Student.PostAsJsonAsync($"/api/v1/members/{s.Member.Id}/dietary-profile", new { allergies = new[] { "x" } })).StatusCode);
    }

    // ---- Planes -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Crear_un_plan_guarda_comidas_y_alimentos_en_orden_y_estima_calorias_sin_inventar()
    {
        var s = await NewSetupAsync("CR1");

        var plan = await CreatePlanAsync(s.Nutritionist, PlanBody());

        Assert.Equal((1, "Active", true, 2000), (plan.Version, plan.Status, plan.IsCurrentVersion, plan.TargetCalories));
        Assert.Equal(["Desayuno", "Almuerzo"], plan.Meals.Select(m => m.Name));
        Assert.Equal([1, 2], plan.Meals.Select(m => m.Position));
        Assert.Equal(["Avena", "Plátano"], plan.Meals[0].Items.Select(i => i.FoodName));
        Assert.Equal(280, plan.Meals[0].EstimatedCalories);
        // Solo un alimento del almuerzo tiene calorías: la estimación suma lo conocido.
        Assert.Equal(170, plan.Meals[1].EstimatedCalories);

        var onlyUnknown = await CreatePlanAsync(s.Nutritionist, PlanBody("Sin datos", meals: [Meal("Cena", Item("Ensalada", 1, "plato", null, null, null, null))]));
        Assert.Null(onlyUnknown.Meals[0].EstimatedCalories);

        var summary = (await s.Nutritionist.GetFromJsonAsync<PagedDto<PlanSummaryRow>>("/api/v1/nutrition-plans?search=Mantenimiento"))!.Items.Single();
        Assert.Equal((plan.Id, 2, 0), (summary.Id, summary.MealCount, summary.ActiveAssignments));
    }

    [Theory]
    [InlineData("cantidad_cero", "nutrition.quantity_invalid")]
    [InlineData("sin_unidad", "nutrition.unit_required")]
    [InlineData("calorias_negativas", "nutrition.nutrients_invalid")]
    [InlineData("proteina_absurda", "nutrition.nutrients_invalid")]
    [InlineData("objetivo_calorias", "nutrition.targets_invalid")]
    [InlineData("comida_repetida", "nutrition.duplicate_meal")]
    public async Task Los_datos_incoherentes_se_rechazan_con_su_codigo(string caso, string code)
    {
        var s = await NewSetupAsync("VAL");
        var body = caso switch
        {
            "cantidad_cero" => PlanBody(meals: [Meal("A", Item(quantity: 0))]),
            "sin_unidad" => PlanBody(meals: [Meal("A", Item(unit: ""))]),
            "calorias_negativas" => PlanBody(meals: [Meal("A", Item(kcal: -10))]),
            "proteina_absurda" => PlanBody(meals: [Meal("A", Item(p: 5000m))]),
            "objetivo_calorias" => PlanBody(calories: 100),
            _ => PlanBody(meals: [Meal("Desayuno", Item()), Meal("desayuno", Item())])
        };

        await AssertProblemAsync(await s.Nutritionist.PostAsJsonAsync("/api/v1/nutrition-plans", body), HttpStatusCode.UnprocessableEntity, code);
    }

    [Fact]
    public async Task Se_rechazan_planes_sin_comidas_comidas_sin_alimentos_y_nombres_cortos()
    {
        var s = await NewSetupAsync("EMP");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await s.Nutritionist.PostAsJsonAsync("/api/v1/nutrition-plans", PlanBody(meals: []))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await s.Nutritionist.PostAsJsonAsync("/api/v1/nutrition-plans", PlanBody(meals: [Meal("Vacía")]))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await s.Nutritionist.PostAsJsonAsync("/api/v1/nutrition-plans", PlanBody(name: "A"))).StatusCode);
    }

    [Fact]
    public async Task Modificar_un_plan_crea_una_version_nueva_y_conserva_las_anteriores()
    {
        var s = await NewSetupAsync("VER");
        var v1 = await CreatePlanAsync(s.Nutritionist, PlanBody(changeNote: "Inicial"));

        var response = await s.Nutritionist.PutAsJsonAsync($"/api/v1/nutrition-plans/{v1.Id}", PlanBody("Mantenimiento 2", "Menos carbohidratos", 1900, [Meal("Único", Item())]));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var v2 = (await response.Content.ReadFromJsonAsync<PlanRow>())!;

        Assert.Equal((2, "Mantenimiento 2", 1900), (v2.Version, v2.Name, v2.TargetCalories));
        var original = (await s.Nutritionist.GetFromJsonAsync<PlanRow>($"/api/v1/nutrition-plans/{v1.Id}?version=1"))!;
        Assert.Equal(("Mantenimiento", 2000, 2, false), (original.Name, original.TargetCalories, original.Meals.Length, original.IsCurrentVersion));
        var versions = (await s.Nutritionist.GetFromJsonAsync<VersionRow[]>($"/api/v1/nutrition-plans/{v1.Id}/versions"))!;
        Assert.Equal([2, 1], versions.Select(v => v.Number));
        Assert.Equal(["Menos carbohidratos", "Inicial"], versions.Select(v => v.ChangeNote));
        Assert.Equal(HttpStatusCode.NotFound, (await s.Nutritionist.GetAsync($"/api/v1/nutrition-plans/{v1.Id}?version=9")).StatusCode);
    }

    [Fact]
    public async Task Duplicar_archivar_y_reactivar_un_plan()
    {
        var s = await NewSetupAsync("DUP");
        var plan = await CreatePlanAsync(s.Nutritionist, PlanBody());

        var copyResponse = await s.Nutritionist.PostAsync($"/api/v1/nutrition-plans/{plan.Id}/duplicate", null);
        Assert.Equal(HttpStatusCode.Created, copyResponse.StatusCode);
        var copy = (await copyResponse.Content.ReadFromJsonAsync<PlanRow>())!;
        Assert.NotEqual(plan.Id, copy.Id);
        Assert.Equal(("Mantenimiento (copia)", 1), (copy.Name, copy.Version));
        Assert.Equal(plan.Meals.Select(m => m.Items.Length), copy.Meals.Select(m => m.Items.Length));

        Assert.Equal(HttpStatusCode.OK, (await s.Nutritionist.PatchAsJsonAsync($"/api/v1/nutrition-plans/{plan.Id}/status", new { active = false })).StatusCode);
        await AssertProblemAsync(await s.Nutritionist.PutAsJsonAsync($"/api/v1/nutrition-plans/{plan.Id}", PlanBody()), HttpStatusCode.Conflict, "nutrition.plan_archived");
        await AssertProblemAsync(await s.Nutritionist.PostAsJsonAsync($"/api/v1/nutrition-plans/{plan.Id}/assignments", new { memberId = s.Member.Id }), HttpStatusCode.Conflict, "nutrition.plan_archived");
        Assert.Equal([plan.Id], (await s.Nutritionist.GetFromJsonAsync<PagedDto<PlanSummaryRow>>("/api/v1/nutrition-plans?status=Archived"))!.Items.Select(p => p.Id));
        Assert.Equal(HttpStatusCode.OK, (await s.Nutritionist.PatchAsJsonAsync($"/api/v1/nutrition-plans/{plan.Id}/status", new { active = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await s.Nutritionist.PostAsJsonAsync($"/api/v1/nutrition-plans/{plan.Id}/assignments", new { memberId = s.Member.Id })).StatusCode);
    }

    // ---- Asignaciones -----------------------------------------------------------------------------------------

    [Fact]
    public async Task El_alumno_ve_solo_su_plan_asignado_y_no_puede_asignar_ni_ver_el_de_otro()
    {
        var s = await NewSetupAsync("ASG");
        var other = await NewSetupAsync("AS2");
        var plan = await CreatePlanAsync(s.Nutritionist, PlanBody());
        await AssertProblemAsync(await s.Student.GetAsync($"/api/v1/members/{s.Member.Id}/nutrition-plan"), HttpStatusCode.NotFound, "nutrition.no_active_plan");

        var assignment = await AssignAsync(s.Nutritionist, plan.Id, s.Member.Id);
        var seen = await MemberPlanAsync(s.Student, s.Member.Id);

        Assert.Equal((Today, "Current", 1), (assignment.StartDate, seen.Assignment.Status, seen.Assignment.PlanVersion));
        Assert.Equal(["Desayuno", "Almuerzo"], seen.Plan.Meals.Select(m => m.Name));
        Assert.Null(seen.Profile);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Student.PostAsJsonAsync($"/api/v1/nutrition-plans/{plan.Id}/assignments", new { memberId = s.Member.Id })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Student.PutAsJsonAsync($"/api/v1/nutrition-plans/{plan.Id}", PlanBody())).StatusCode);
        // Otro alumno, aunque sea del mismo gimnasio, o un profesional de otro gimnasio, no ven nada de este alumno.
        Assert.Equal(HttpStatusCode.NotFound, (await other.Student.GetAsync($"/api/v1/members/{s.Member.Id}/nutrition-plan")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Nutritionist.GetAsync($"/api/v1/members/{s.Member.Id}/nutrition-plan")).StatusCode);
        await AssertProblemAsync(await other.Nutritionist.PostAsJsonAsync($"/api/v1/nutrition-plans/{plan.Id}/assignments", new { memberId = other.Member.Id }), HttpStatusCode.NotFound, "nutrition.plan_not_found");
        await AssertProblemAsync(await s.Nutritionist.PostAsJsonAsync($"/api/v1/nutrition-plans/{plan.Id}/assignments", new { memberId = other.Member.Id }), HttpStatusCode.NotFound, "nutrition.member_not_found");
    }

    [Fact]
    public async Task Un_plan_modificado_despues_no_cambia_lo_asignado_y_reasignar_da_la_version_nueva()
    {
        var s = await NewSetupAsync("PIN");
        var v1 = await CreatePlanAsync(s.Nutritionist, PlanBody());
        await AssignAsync(s.Nutritionist, v1.Id, s.Member.Id);
        await s.Nutritionist.PutAsJsonAsync($"/api/v1/nutrition-plans/{v1.Id}", PlanBody("Plan renovado", meals: [Meal("Único", Item())]));

        var stillV1 = await MemberPlanAsync(s.Student, s.Member.Id);
        Assert.Equal((1, "Mantenimiento", 2), (stillV1.Assignment.PlanVersion, stillV1.Assignment.PlanName, stillV1.Plan.Meals.Length));

        var reassigned = await AssignAsync(s.Nutritionist, v1.Id, s.Member.Id);
        Assert.Equal(2, reassigned.PlanVersion);
        var nowV2 = await MemberPlanAsync(s.Student, s.Member.Id);
        Assert.Equal(("Plan renovado", 1), (nowV2.Assignment.PlanName, nowV2.Plan.Meals.Length));
    }

    [Fact]
    public async Task Reasignar_cierra_el_plan_anterior_con_su_motivo_y_conserva_el_historial()
    {
        var s = await NewSetupAsync("RE1");
        var planA = await CreatePlanAsync(s.Nutritionist, PlanBody("Plan A"));
        var planB = await CreatePlanAsync(s.Nutritionist, PlanBody("Plan B"));

        var first = await AssignAsync(s.Nutritionist, planA.Id, s.Member.Id);
        var second = await AssignAsync(s.Nutritionist, planB.Id, s.Member.Id);

        var history = (await s.Nutritionist.GetFromJsonAsync<AssignRow[]>($"/api/v1/members/{s.Member.Id}/nutrition-assignments"))!;
        Assert.Equal(2, history.Length);
        var closed = history.Single(a => a.Id == first.Id);
        Assert.Equal(("Reemplazado por otro plan", Today.AddDays(-1), "Ended"), (closed.EndReason, closed.EndedOn, closed.Status));
        Assert.Equal("Current", history.Single(a => a.Id == second.Id).Status);
        Assert.Equal("Plan B", (await MemberPlanAsync(s.Student, s.Member.Id)).Assignment.PlanName);
    }

    [Fact]
    public async Task Las_fechas_se_validan_y_un_plan_a_futuro_no_rige_hasta_su_fecha()
    {
        var s = await NewSetupAsync("FUT");
        var plan = await CreatePlanAsync(s.Nutritionist, PlanBody());
        var url = $"/api/v1/nutrition-plans/{plan.Id}/assignments";

        await AssertProblemAsync(await s.Nutritionist.PostAsJsonAsync(url, new { memberId = s.Member.Id, startDate = Today.AddDays(-1) }), HttpStatusCode.UnprocessableEntity, "nutrition.dates_invalid");
        await AssertProblemAsync(await s.Nutritionist.PostAsJsonAsync(url, new { memberId = s.Member.Id, startDate = Today.AddDays(5), endDate = Today.AddDays(2) }), HttpStatusCode.UnprocessableEntity, "nutrition.dates_invalid");
        await AssertProblemAsync(await s.Nutritionist.PostAsJsonAsync(url, new { memberId = s.Member.Id, startDate = Today, endDate = Today.AddDays(800) }), HttpStatusCode.UnprocessableEntity, "nutrition.dates_invalid");

        var future = await AssignAsync(s.Nutritionist, plan.Id, s.Member.Id, Today.AddDays(7), Today.AddDays(30));
        Assert.Equal("Scheduled", future.Status);
        await AssertProblemAsync(await s.Student.GetAsync($"/api/v1/members/{s.Member.Id}/nutrition-plan"), HttpStatusCode.NotFound, "nutrition.no_active_plan");
    }

    [Fact]
    public async Task Cerrar_una_asignacion_exige_motivo_es_inmediato_y_no_se_repite()
    {
        var s = await NewSetupAsync("END");
        var plan = await CreatePlanAsync(s.Nutritionist, PlanBody());
        var assignment = await AssignAsync(s.Nutritionist, plan.Id, s.Member.Id);
        var url = $"/api/v1/nutrition-assignments/{assignment.Id}/end";

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await s.Nutritionist.PostAsJsonAsync(url, new { reason = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Student.PostAsJsonAsync(url, new { reason = "yo mismo" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.Nutritionist.PostAsJsonAsync(url, new { reason = "Terminó el ciclo" })).StatusCode);
        await AssertProblemAsync(await s.Student.GetAsync($"/api/v1/members/{s.Member.Id}/nutrition-plan"), HttpStatusCode.NotFound, "nutrition.no_active_plan");
        await AssertProblemAsync(await s.Nutritionist.PostAsJsonAsync(url, new { reason = "otra vez" }), HttpStatusCode.Conflict, "nutrition.assignment_ended");
    }

    [Fact]
    public async Task Asignaciones_simultaneas_dejan_un_solo_plan_abierto_y_no_se_asigna_a_alumnos_inactivos()
    {
        var s = await NewSetupAsync("RAC");
        var plans = new List<PlanRow>();
        for (var i = 0; i < 8; i++)
        {
            plans.Add(await CreatePlanAsync(s.Nutritionist, PlanBody($"Plan {i}")));
        }

        var responses = await Task.WhenAll(plans.Select(p => s.Nutritionist.PostAsJsonAsync($"/api/v1/nutrition-plans/{p.Id}/assignments", new { memberId = s.Member.Id })));

        Assert.All(responses, r => Assert.True(r.StatusCode is HttpStatusCode.Created or HttpStatusCode.Conflict));
        var history = (await s.Nutritionist.GetFromJsonAsync<AssignRow[]>($"/api/v1/members/{s.Member.Id}/nutrition-assignments"))!;
        Assert.Equal(1, history.Count(a => a.EndedOn is null));
        Assert.Equal(responses.Count(r => r.StatusCode == HttpStatusCode.Created), history.Length);

        await s.Tenant.Owner.PatchAsJsonAsync($"/api/v1/members/{s.Member.Id}/status", new { isActive = false });
        await AssertProblemAsync(await s.Nutritionist.PostAsJsonAsync($"/api/v1/nutrition-plans/{plans[0].Id}/assignments", new { memberId = s.Member.Id }), HttpStatusCode.Conflict, "nutrition.member_inactive");
    }

    // ---- Alergias y restricciones -----------------------------------------------------------------------------

    [Fact]
    public async Task Las_alergias_se_registran_como_historial_y_el_alumno_ve_las_suyas()
    {
        var s = await NewSetupAsync("ALE");
        var url = $"/api/v1/members/{s.Member.Id}/dietary-profile";

        Assert.Empty((await s.Nutritionist.GetFromJsonAsync<ProfileRow[]>(url))!);
        var first = await s.Nutritionist.PostAsJsonAsync(url, new { allergies = new[] { " Maní ", "maní", "Mariscos" }, intolerances = new[] { "Lactosa" }, restrictions = Array.Empty<string>(), notes = "Reacción severa al maní" });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(["Maní", "Mariscos"], (await first.Content.ReadFromJsonAsync<ProfileRow>())!.Allergies);
        await s.Nutritionist.PostAsJsonAsync(url, new { allergies = new[] { "Maní" }, notes = "Se descartó la alergia a mariscos" });

        var current = (await s.Student.GetFromJsonAsync<ProfileRow[]>(url))!.Single();
        Assert.Equal(["Maní"], current.Allergies);
        Assert.Equal("Se descartó la alergia a mariscos", current.Notes);
        var history = (await s.Nutritionist.GetFromJsonAsync<ProfileRow[]>($"{url}?history=true"))!;
        Assert.Equal(2, history.Length);
        Assert.Equal(["Maní"], history[0].Allergies);
        Assert.Equal(["Maní", "Mariscos"], history[1].Allergies);

        // El plan vigente se lee junto con el perfil.
        var plan = await CreatePlanAsync(s.Nutritionist, PlanBody());
        await AssignAsync(s.Nutritionist, plan.Id, s.Member.Id);
        Assert.Equal(["Maní"], (await MemberPlanAsync(s.Student, s.Member.Id)).Profile!.Allergies);
    }

    [Fact]
    public async Task El_perfil_alimentario_no_admite_registros_vacios_ni_listas_excesivas()
    {
        var s = await NewSetupAsync("PRV");
        var url = $"/api/v1/members/{s.Member.Id}/dietary-profile";

        await AssertProblemAsync(await s.Nutritionist.PostAsJsonAsync(url, new { allergies = new[] { "  " } }), HttpStatusCode.UnprocessableEntity, "nutrition.profile_empty");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await s.Nutritionist.PostAsJsonAsync(url, new { allergies = Enumerable.Range(0, 21).Select(i => $"alergia {i}").ToArray() })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await s.Nutritionist.PostAsJsonAsync(url, new { allergies = new[] { new string('x', 81) } })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await s.Nutritionist.PostAsJsonAsync(url, new { notes = "Sin alergias conocidas" })).StatusCode);
    }

    // ---- Cumplimiento -----------------------------------------------------------------------------------------

    [Fact]
    public async Task El_alumno_registra_el_cumplimiento_de_sus_comidas_y_se_resume_por_dia()
    {
        var s = await NewSetupAsync("LOG");
        var plan = await CreatePlanAsync(s.Nutritionist, PlanBody());
        await AssignAsync(s.Nutritionist, plan.Id, s.Member.Id);
        var mine = await MemberPlanAsync(s.Student, s.Member.Id);
        var breakfast = mine.Plan.Meals[0];
        var lunch = mine.Plan.Meals[1];

        var log = await LogAsync(s.Student, new { mealId = breakfast.Id, status = "Completed", notes = "Todo" });
        await LogAsync(s.Student, new { mealId = lunch.Id, status = "Partial" });

        Assert.Equal((s.Member.Id, "Desayuno", Today, "Completed", 1), (log.MemberId, log.MealName, log.Date, log.Status, log.PlanVersion));
        await AssertProblemAsync(await s.Student.PostAsJsonAsync("/api/v1/meal-logs", new { mealId = breakfast.Id, status = "Skipped" }), HttpStatusCode.Conflict, "nutrition.meal_already_logged");
        var logs = (await s.Student.GetFromJsonAsync<LogsRow>($"/api/v1/members/{s.Member.Id}/meal-logs"))!;
        var day = logs.Days.Single(d => d.Date == Today);
        Assert.Equal((2, 1, 1, 0, 0), (day.PlannedMeals, day.Completed, day.Partial, day.Skipped, day.NotLogged));
        Assert.Equal(2, logs.Logs.Length);
        // El nutricionista lee el cumplimiento; un registro de ayer queda fuera de la vigencia de un plan que empezó hoy.
        Assert.Equal(2, (await s.Nutritionist.GetFromJsonAsync<LogsRow>($"/api/v1/members/{s.Member.Id}/meal-logs"))!.Logs.Length);
        await AssertProblemAsync(await s.Student.PostAsJsonAsync("/api/v1/meal-logs", new { mealId = breakfast.Id, date = Today.AddDays(-1), status = "Completed" }), HttpStatusCode.UnprocessableEntity, "nutrition.log_date_invalid");
    }

    [Fact]
    public async Task Las_comidas_pendientes_se_cuentan_aparte_y_las_fechas_del_registro_se_validan()
    {
        var s = await NewSetupAsync("PEN");
        var plan = await CreatePlanAsync(s.Nutritionist, PlanBody());
        await AssignAsync(s.Nutritionist, plan.Id, s.Member.Id);
        var mine = await MemberPlanAsync(s.Student, s.Member.Id);
        var meal = mine.Plan.Meals[0].Id;

        await AssertProblemAsync(await s.Student.PostAsJsonAsync("/api/v1/meal-logs", new { mealId = meal, date = Today.AddDays(1), status = "Completed" }), HttpStatusCode.UnprocessableEntity, "nutrition.log_date_invalid");
        await AssertProblemAsync(await s.Student.PostAsJsonAsync("/api/v1/meal-logs", new { mealId = meal, date = Today.AddDays(-8), status = "Completed" }), HttpStatusCode.UnprocessableEntity, "nutrition.log_date_invalid");
        await AssertProblemAsync(await s.Student.PostAsJsonAsync("/api/v1/meal-logs", new { mealId = Guid.NewGuid(), status = "Completed" }), HttpStatusCode.UnprocessableEntity, "nutrition.meal_not_in_plan");
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Student.PostAsJsonAsync("/api/v1/meal-logs", new { mealId = meal, status = "Invalido" })).StatusCode);

        var nothingLogged = (await s.Student.GetFromJsonAsync<LogsRow>($"/api/v1/members/{s.Member.Id}/meal-logs"))!;
        var day = nothingLogged.Days.Single(d => d.Date == Today);
        Assert.Equal((2, 0, 2), (day.PlannedMeals, day.Completed, day.NotLogged));
        Assert.Empty(nothingLogged.Logs);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await s.Student.GetAsync($"/api/v1/members/{s.Member.Id}/meal-logs?from={Today.AddDays(-200):yyyy-MM-dd}&to={Today:yyyy-MM-dd}")).StatusCode);
    }

    [Fact]
    public async Task Sin_plan_vigente_no_se_registra_y_el_alumno_no_registra_por_otro()
    {
        var s = await NewSetupAsync("OWN");
        var other = await NewSetupAsync("OW2");
        var plan = await CreatePlanAsync(s.Nutritionist, PlanBody());
        await AssertProblemAsync(await s.Student.PostAsJsonAsync("/api/v1/meal-logs", new { mealId = plan.Meals[0].Id, status = "Completed" }), HttpStatusCode.NotFound, "nutrition.no_active_plan");
        await AssignAsync(s.Nutritionist, plan.Id, s.Member.Id);
        var otherPlan = await CreatePlanAsync(other.Nutritionist, PlanBody());
        await AssignAsync(other.Nutritionist, otherPlan.Id, other.Member.Id);
        var mealId = (await MemberPlanAsync(s.Student, s.Member.Id)).Plan.Meals[0].Id;

        // El identificador del cliente nunca decide de quién es el registro.
        await AssertProblemAsync(await s.Student.PostAsJsonAsync("/api/v1/meal-logs", new { memberId = other.Member.Id, mealId, status = "Completed" }), HttpStatusCode.Forbidden, "nutrition.other_member");
        await AssertProblemAsync(await other.Student.PostAsJsonAsync("/api/v1/meal-logs", new { mealId, status = "Completed" }), HttpStatusCode.UnprocessableEntity, "nutrition.meal_not_in_plan");
        // El nutricionista registra por su alumno; el de otro gimnasio no puede.
        var byStaff = await LogAsync(s.Nutritionist, new { memberId = s.Member.Id, mealId, status = "Skipped" });
        Assert.Equal(s.Member.Id, byStaff.MemberId);
        await AssertProblemAsync(await other.Nutritionist.PostAsJsonAsync("/api/v1/meal-logs", new { memberId = s.Member.Id, mealId, status = "Completed" }), HttpStatusCode.NotFound, "nutrition.member_not_found");
        Assert.Equal(HttpStatusCode.NotFound, (await other.Student.GetAsync($"/api/v1/members/{s.Member.Id}/meal-logs")).StatusCode);
    }

    [Fact]
    public async Task Los_registros_conservan_la_version_del_plan_con_que_se_hicieron()
    {
        var s = await NewSetupAsync("HIS");
        var plan = await CreatePlanAsync(s.Nutritionist, PlanBody());
        await AssignAsync(s.Nutritionist, plan.Id, s.Member.Id);
        var mealId = (await MemberPlanAsync(s.Student, s.Member.Id)).Plan.Meals[0].Id;
        await LogAsync(s.Student, new { mealId, status = "Completed" });

        await s.Nutritionist.PutAsJsonAsync($"/api/v1/nutrition-plans/{plan.Id}", PlanBody("Otro plan", meals: [Meal("Cena", Item())]));
        await AssignAsync(s.Nutritionist, plan.Id, s.Member.Id);

        var logs = (await s.Student.GetFromJsonAsync<LogsRow>($"/api/v1/members/{s.Member.Id}/meal-logs"))!;
        Assert.Equal(("Desayuno", 1), (logs.Logs.Single().MealName, logs.Logs.Single().PlanVersion));
        // La comida de la versión anterior ya no se puede registrar con el plan nuevo.
        await AssertProblemAsync(await s.Student.PostAsJsonAsync("/api/v1/meal-logs", new { mealId = plan.Meals[1].Id, status = "Completed" }), HttpStatusCode.UnprocessableEntity, "nutrition.meal_not_in_plan");
    }

    // ---- Auditoría y base de datos ----------------------------------------------------------------------------

    [Fact]
    public async Task Se_audita_la_lectura_del_personal_sin_registrar_el_contenido()
    {
        var s = await NewSetupAsync("AUD");
        var secret = $"alergia-secreta-{Guid.NewGuid():N}";
        var plan = await CreatePlanAsync(s.Nutritionist, PlanBody());
        await AssignAsync(s.Nutritionist, plan.Id, s.Member.Id);
        await s.Nutritionist.PostAsJsonAsync($"/api/v1/members/{s.Member.Id}/dietary-profile", new { allergies = new[] { secret }, notes = secret });
        await s.Nutritionist.GetAsync($"/api/v1/members/{s.Member.Id}/nutrition-plan");
        await s.Nutritionist.GetAsync($"/api/v1/members/{s.Member.Id}/dietary-profile");
        await s.Nutritionist.GetAsync($"/api/v1/members/{s.Member.Id}/meal-logs");

        var audit = await s.Tenant.Owner.GetStringAsync("/api/v1/audit-logs?pageSize=200");

        foreach (var action in new[] { "Wellness.NutritionPlanAssigned", "Wellness.DietaryProfileSaved", "Wellness.NutritionPlanRead", "Wellness.DietaryProfileRead", "Wellness.NutritionLogsRead" })
        {
            Assert.Contains(action, audit);
        }

        Assert.DoesNotContain(secret, audit);
    }

    [Fact]
    public async Task La_base_impide_reescribir_el_historial_de_nutricion()
    {
        var s = await NewSetupAsync("DB1");
        var plan = await CreatePlanAsync(s.Nutritionist, PlanBody());
        var assignment = await AssignAsync(s.Nutritionist, plan.Id, s.Member.Id);
        var mine = await MemberPlanAsync(s.Student, s.Member.Id);
        var log = await LogAsync(s.Student, new { mealId = mine.Plan.Meals[0].Id, status = "Completed" });
        await s.Nutritionist.PostAsJsonAsync($"/api/v1/members/{s.Member.Id}/dietary-profile", new { allergies = new[] { "Maní" } });
        var itemId = mine.Plan.Meals[0].Items[0].Id;

        foreach (var asApp in new[] { true, false })
        {
            await Assert.ThrowsAsync<PostgresException>(() => SqlAsync($"UPDATE nutrition.plan_versions SET name = 'x' WHERE id = '{plan.VersionId}'", asApp));
            await Assert.ThrowsAsync<PostgresException>(() => SqlAsync($"DELETE FROM nutrition.plan_versions WHERE id = '{plan.VersionId}'", asApp));
            await Assert.ThrowsAsync<PostgresException>(() => SqlAsync($"UPDATE nutrition.meal_items SET quantity = 999 WHERE id = '{itemId}'", asApp));
            await Assert.ThrowsAsync<PostgresException>(() => SqlAsync($"DELETE FROM nutrition.meals WHERE id = '{mine.Plan.Meals[0].Id}'", asApp));
            await Assert.ThrowsAsync<PostgresException>(() => SqlAsync($"UPDATE nutrition.meal_logs SET status = 'Skipped' WHERE id = '{log.Id}'", asApp));
            await Assert.ThrowsAsync<PostgresException>(() => SqlAsync($"DELETE FROM nutrition.meal_logs WHERE id = '{log.Id}'", asApp));
            await Assert.ThrowsAsync<PostgresException>(() => SqlAsync($"UPDATE nutrition.dietary_profiles SET notes = 'editada' WHERE member_id = '{s.Member.Id}'", asApp));
            await Assert.ThrowsAsync<PostgresException>(() => SqlAsync($"DELETE FROM nutrition.dietary_profiles WHERE member_id = '{s.Member.Id}'", asApp));
            await Assert.ThrowsAsync<PostgresException>(() => SqlAsync($"DELETE FROM nutrition.nutrition_assignments WHERE id = '{assignment.Id}'", asApp));
        }

        await Assert.ThrowsAsync<PostgresException>(() => SqlAsync($"UPDATE nutrition.nutrition_assignments SET start_date = start_date + 1 WHERE id = '{assignment.Id}'"));
    }

    [Fact]
    public async Task La_base_rechaza_datos_incoherentes_aunque_la_aplicacion_falle()
    {
        var s = await NewSetupAsync("DB2");
        var plan = await CreatePlanAsync(s.Nutritionist, PlanBody());
        var mealId = plan.Meals[0].Id;
        var tenant = s.Tenant.Id;

        // Debe fallar por la restricción de la tabla (no por otra causa como los permisos).
        async Task AssertCheckAsync(string sql, string constraint)
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => SqlAsync(sql));
            Assert.Equal(constraint, ex.ConstraintName);
        }

        var user = "(SELECT id FROM identity.app_users WHERE tenant_id = '" + tenant + "' LIMIT 1)";
        await AssertCheckAsync(
            $"INSERT INTO nutrition.meal_items (id, tenant_id, meal_id, position, food_name, quantity, unit) VALUES (gen_random_uuid(), '{tenant}', '{mealId}', 9, 'x', 0, 'g')",
            "ck_meal_items_quantity");
        await AssertCheckAsync(
            $"INSERT INTO nutrition.meal_items (id, tenant_id, meal_id, position, food_name, quantity, unit, calories) VALUES (gen_random_uuid(), '{tenant}', '{mealId}', 9, 'x', 1, 'g', -5)",
            "ck_meal_items_calories");
        await AssertCheckAsync(
            $"INSERT INTO nutrition.dietary_profiles (id, tenant_id, member_id, allergies, intolerances, restrictions, recorded_by, recorded_at) " +
            $"VALUES (gen_random_uuid(), '{tenant}', '{s.Member.Id}', '{{}}', '{{}}', '{{}}', {user}, now())",
            "ck_dietary_profiles_content");
    }
}
