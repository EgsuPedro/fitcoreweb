using System.Net;
using System.Net.Http.Json;
using FitCore.Api.IntegrationTests.Infrastructure;
using Npgsql;
using static FitCore.Api.IntegrationTests.Infrastructure.CommercialTestApi;

namespace FitCore.Api.IntegrationTests;

[Collection(ApiCollection4.Name)]
public class AssessmentTests(ApiFixture api)
{
    private sealed record AssessRow(
        Guid Id, Guid MemberId, DateOnly AssessedOn, decimal? WeightKg, decimal? HeightCm, decimal? WaistCm, decimal? BodyFatPercent, string? BodyFatMethod,
        string Source, Guid? CorrectsId, bool IsCorrected, decimal? Bmi, bool HeightCarried, string? BmiReferenceRange, decimal? WaistHipRatio);

    private sealed record ChangeRow(string Metric, string Unit, decimal? From, decimal? To, decimal? Delta);

    private sealed record CompareRow(AssessRow A, AssessRow B, int DaysBetween, ChangeRow[] Changes);

    private sealed record PointRow(Guid AssessmentId, DateOnly Date, decimal? WeightKg, decimal? Bmi, decimal? WaistCm);

    private sealed record TrainerRow(Guid Id);

    private sealed record Setup(TestTenant Tenant, Guid Branch, HttpClient Trainer, Guid TrainerId, MemberDto Member, HttpClient Student);

    private static DateOnly Today =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/Guayaquil")).DateTime);

    private const string StudentPassword = "Nueva-Clave-2026x";

    private static string Unique(string prefix) => $"{prefix}.{Guid.NewGuid():N}"[..22] + "@gym.test";

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
            branchId = branch, firstName = "Carlos", lastName = "Prueba", idType = "Cedula", idNumber = MemberTests.NewCedula(), email = studentEmail, phone = "0991234567", birthDate
        });
        Assert.Equal(HttpStatusCode.Created, memberResponse.StatusCode);
        var member = (await memberResponse.Content.ReadFromJsonAsync<MemberDto>())!;
        Assert.Equal(HttpStatusCode.Created, (await tenant.Owner.PostAsJsonAsync($"/api/v1/trainers/{profile.Id}/members", new { memberId = member.Id })).StatusCode);

        var access = await (await tenant.Owner.PostAsync($"/api/v1/members/{member.Id}/access", null)).Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var temporary = access.GetProperty("temporaryPassword").GetString()!;
        using var temp = TestApi.Authenticated(api, await TestApi.LoginAsync(api, tenant.Slug, studentEmail, temporary));
        Assert.Equal(HttpStatusCode.NoContent, (await temp.PostAsJsonAsync("/api/v1/auth/change-password", new { currentPassword = temporary, newPassword = StudentPassword })).StatusCode);
        var student = TestApi.Authenticated(api, await TestApi.LoginAsync(api, tenant.Slug, studentEmail, StudentPassword));
        return new Setup(tenant, branch, trainer, profile.Id, member, student);
    }

    private static object Body(DateOnly? on = null, decimal? weight = 70m, decimal? height = 175m, decimal? waist = null, decimal? hip = null, decimal? fat = null, string? method = null, Guid? corrects = null, string source = "Scale") =>
        new { assessedOn = on, weightKg = weight, heightCm = height, waistCm = waist, hipCm = hip, bodyFatPercent = fat, bodyFatMethod = method, source, notes = (string?)null, correctsId = corrects };

    private static async Task<AssessRow> RecordAsync(HttpClient client, Guid memberId, object body)
    {
        var response = await client.PostAsJsonAsync($"/api/v1/members/{memberId}/assessments", body);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"Respondió {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<AssessRow>())!;
    }

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

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd");

    [Fact]
    public async Task El_entrenador_registra_una_evaluacion_y_el_imc_se_calcula_con_estatura_valida()
    {
        var s = await NewSetupAsync("IMC");

        var a = await RecordAsync(s.Trainer, s.Member.Id, Body(waist: 82m, hip: 100m));

        Assert.Equal((Today, 70m, 175m, "Scale"), (a.AssessedOn, a.WeightKg, a.HeightCm, a.Source));
        Assert.Equal((22.9m, false, "Reference", 0.82m), (a.Bmi, a.HeightCarried, a.BmiReferenceRange, a.WaistHipRatio));
        Assert.Null(a.BodyFatPercent);
    }

    [Fact]
    public async Task Sin_estatura_no_hay_imc_y_despues_se_arrastra_la_ultima_conocida_marcandola()
    {
        var s = await NewSetupAsync("ARR");

        var noHeight = await RecordAsync(s.Trainer, s.Member.Id, Body(Today.AddDays(-20), weight: 80m, height: null));
        Assert.Null(noHeight.Bmi);
        Assert.Null(noHeight.BmiReferenceRange);

        await RecordAsync(s.Trainer, s.Member.Id, Body(Today.AddDays(-10), weight: 78m, height: 180m));
        var later = await RecordAsync(s.Trainer, s.Member.Id, Body(Today.AddDays(-1), weight: 76m, height: null));
        Assert.Equal((23.5m, true), (later.Bmi, later.HeightCarried));
        // La anterior a la primera estatura sigue sin IMC: no se rellena hacia atrás.
        var list = (await s.Trainer.GetFromJsonAsync<PagedDto<AssessRow>>($"/api/v1/members/{s.Member.Id}/assessments"))!.Items;
        Assert.Null(list.Single(a => a.Id == noHeight.Id).Bmi);
    }

    [Fact]
    public async Task El_rango_de_referencia_no_se_informa_para_menores_ni_sin_fecha_de_nacimiento()
    {
        var minor = await NewSetupAsync("MEN", birthDate: Iso(Today.AddYears(-12)));
        var adult = await NewSetupAsync("ADU");

        var forMinor = await RecordAsync(minor.Trainer, minor.Member.Id, Body(weight: 40m, height: 150m));
        var forAdult = await RecordAsync(adult.Trainer, adult.Member.Id, Body(weight: 95m, height: 170m));

        Assert.Equal((17.8m, null), (forMinor.Bmi, forMinor.BmiReferenceRange));
        Assert.Equal((32.9m, "Above"), (forAdult.Bmi, forAdult.BmiReferenceRange));
    }

    [Theory]
    [InlineData("vacia", "assessment.empty")]
    [InlineData("peso", "assessment.weight_invalid")]
    [InlineData("estatura", "assessment.height_invalid")]
    [InlineData("perimetro", "assessment.circumference_invalid")]
    [InlineData("grasa_sin_metodo", "assessment.body_fat_method_required")]
    [InlineData("grasa_rango", "assessment.body_fat_invalid")]
    [InlineData("metodo_sin_grasa", "assessment.body_fat_method_without_value")]
    public async Task Las_mediciones_incoherentes_se_rechazan_con_su_codigo(string caso, string code)
    {
        var s = await NewSetupAsync("VAL");
        var body = caso switch
        {
            "vacia" => Body(weight: null, height: null),
            "peso" => Body(weight: 900m),
            "estatura" => Body(height: 20m),
            "perimetro" => Body(waist: 1m),
            "grasa_sin_metodo" => Body(fat: 20m),
            "grasa_rango" => Body(fat: 90m, method: "Caliper"),
            _ => Body(method: "Dexa")
        };

        await AssertProblemAsync(await s.Trainer.PostAsJsonAsync($"/api/v1/members/{s.Member.Id}/assessments", body), HttpStatusCode.UnprocessableEntity, code);
    }

    [Fact]
    public async Task La_fecha_de_la_evaluacion_no_puede_ser_futura_ni_de_hace_mas_de_diez_anos()
    {
        var s = await NewSetupAsync("FEC");
        var url = $"/api/v1/members/{s.Member.Id}/assessments";

        await AssertProblemAsync(await s.Trainer.PostAsJsonAsync(url, Body(Today.AddDays(1))), HttpStatusCode.UnprocessableEntity, "assessment.date_invalid");
        await AssertProblemAsync(await s.Trainer.PostAsJsonAsync(url, Body(Today.AddYears(-11))), HttpStatusCode.UnprocessableEntity, "assessment.date_invalid");
        Assert.Equal(HttpStatusCode.Created, (await s.Trainer.PostAsJsonAsync(url, Body(Today.AddDays(-30)))).StatusCode);
    }

    [Fact]
    public async Task Un_error_se_corrige_con_una_evaluacion_nueva_y_la_original_se_conserva()
    {
        var s = await NewSetupAsync("COR");
        var wrong = await RecordAsync(s.Trainer, s.Member.Id, Body(weight: 700m / 10m));

        var fixedOne = await RecordAsync(s.Trainer, s.Member.Id, Body(weight: 79m, corrects: wrong.Id));

        Assert.Equal(wrong.Id, fixedOne.CorrectsId);
        var current = (await s.Trainer.GetFromJsonAsync<PagedDto<AssessRow>>($"/api/v1/members/{s.Member.Id}/assessments"))!.Items;
        Assert.Equal([fixedOne.Id], current.Select(a => a.Id));
        var all = (await s.Trainer.GetFromJsonAsync<PagedDto<AssessRow>>($"/api/v1/members/{s.Member.Id}/assessments?includeCorrected=true"))!.Items;
        Assert.Equal(2, all.Length);
        Assert.True(all.Single(a => a.Id == wrong.Id).IsCorrected);
        await AssertProblemAsync(await s.Trainer.PostAsJsonAsync($"/api/v1/members/{s.Member.Id}/assessments", Body(corrects: wrong.Id)), HttpStatusCode.Conflict, "assessment.already_corrected");
        await AssertProblemAsync(await s.Trainer.PostAsJsonAsync($"/api/v1/members/{s.Member.Id}/assessments", Body(corrects: Guid.NewGuid())), HttpStatusCode.NotFound, "assessment.not_found");
    }

    [Fact]
    public async Task Comparar_dos_evaluaciones_muestra_la_evolucion_de_la_mas_antigua_a_la_mas_reciente()
    {
        var s = await NewSetupAsync("CMP");
        var first = await RecordAsync(s.Trainer, s.Member.Id, Body(Today.AddDays(-60), weight: 85m, height: 175m, waist: 95m));
        var second = await RecordAsync(s.Trainer, s.Member.Id, Body(Today.AddDays(-5), weight: 80m, height: null, waist: 90m));

        // Aunque se pidan al revés, se leen de la más antigua a la más reciente.
        var compare = (await s.Trainer.GetFromJsonAsync<CompareRow>($"/api/v1/members/{s.Member.Id}/assessments/compare?a={second.Id}&b={first.Id}"))!;

        Assert.Equal((first.Id, second.Id, 55), (compare.A.Id, compare.B.Id, compare.DaysBetween));
        Assert.Equal(-5m, compare.Changes.Single(c => c.Metric == "WeightKg").Delta);
        Assert.Equal(-5m, compare.Changes.Single(c => c.Metric == "WaistCm").Delta);
        Assert.Equal(-1.7m, compare.Changes.Single(c => c.Metric == "Bmi").Delta);
        // Lo que no se midió en una de las dos no tiene diferencia (no se inventa).
        Assert.Null(compare.Changes.Single(c => c.Metric == "HipCm").Delta);
        await AssertProblemAsync(await s.Trainer.GetAsync($"/api/v1/members/{s.Member.Id}/assessments/compare?a={first.Id}&b={first.Id}"), HttpStatusCode.UnprocessableEntity, "assessment.compare_invalid");
        await AssertProblemAsync(await s.Trainer.GetAsync($"/api/v1/members/{s.Member.Id}/assessments/compare?a={first.Id}&b={Guid.NewGuid()}"), HttpStatusCode.NotFound, "assessment.not_found");
    }

    [Fact]
    public async Task El_progreso_devuelve_la_serie_en_orden_cronologico_sin_las_corregidas()
    {
        var s = await NewSetupAsync("PRG");
        var old = await RecordAsync(s.Trainer, s.Member.Id, Body(Today.AddDays(-40), weight: 90m));
        await RecordAsync(s.Trainer, s.Member.Id, Body(Today.AddDays(-20), weight: 88m));
        await RecordAsync(s.Trainer, s.Member.Id, Body(Today.AddDays(-40), weight: 89m, corrects: old.Id));

        var points = (await s.Student.GetFromJsonAsync<PointRow[]>($"/api/v1/members/{s.Member.Id}/assessments/progress"))!;

        Assert.Equal([89m, 88m], points.Select(p => p.WeightKg));
        Assert.Equal([Today.AddDays(-40), Today.AddDays(-20)], points.Select(p => p.Date));
        var recent = (await s.Student.GetFromJsonAsync<PointRow[]>($"/api/v1/members/{s.Member.Id}/assessments/progress?from={Iso(Today.AddDays(-30))}"))!;
        Assert.Single(recent);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await s.Student.GetAsync($"/api/v1/members/{s.Member.Id}/assessments/progress?from={Iso(Today)}&to={Iso(Today.AddDays(-5))}")).StatusCode);
    }

    [Fact]
    public async Task El_alumno_ve_solo_las_suyas_y_no_registra_ni_corrige()
    {
        var s = await NewSetupAsync("ALU");
        var other = await NewSetupAsync("AL2");
        var mine = await RecordAsync(s.Trainer, s.Member.Id, Body());
        var theirs = await RecordAsync(other.Trainer, other.Member.Id, Body(weight: 60m));

        Assert.Equal([mine.Id], (await s.Student.GetFromJsonAsync<PagedDto<AssessRow>>($"/api/v1/members/{s.Member.Id}/assessments"))!.Items.Select(a => a.Id));
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Student.PostAsJsonAsync($"/api/v1/members/{s.Member.Id}/assessments", Body())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Student.GetAsync($"/api/v1/members/{other.Member.Id}/assessments")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Student.GetAsync($"/api/v1/members/{s.Member.Id}/assessments")).StatusCode);
        // No se compara con una evaluación de otro alumno.
        Assert.Equal(HttpStatusCode.NotFound, (await s.Student.GetAsync($"/api/v1/members/{s.Member.Id}/assessments/compare?a={mine.Id}&b={theirs.Id}")).StatusCode);
    }

    [Fact]
    public async Task Solo_el_entrenador_asignado_registra_y_ni_propietario_ni_administrador_ni_nutricionista_acceden()
    {
        var s = await NewSetupAsync("ACC");
        await RecordAsync(s.Trainer, s.Member.Id, Body());
        var strangerEmail = Unique("ajeno");
        var stranger = await TestApi.CreateUserAsync(s.Tenant.Owner, strangerEmail, "Entrenador", s.Branch);
        await s.Tenant.Owner.PostAsJsonAsync("/api/v1/trainers", new { userId = stranger.Id });
        var strangerClient = TestApi.Authenticated(api, await TestApi.LoginAsync(api, s.Tenant.Slug, strangerEmail, TestApi.UserPassword));
        var adminEmail = Unique("admin");
        await TestApi.CreateUserAsync(s.Tenant.Owner, adminEmail, "AdministradorSucursal", s.Branch);
        var admin = TestApi.Authenticated(api, await TestApi.LoginAsync(api, s.Tenant.Slug, adminEmail, TestApi.UserPassword));
        var nutriEmail = Unique("nutri");
        await TestApi.CreateUserAsync(s.Tenant.Owner, nutriEmail, "Nutricionista", s.Branch);
        var nutri = TestApi.Authenticated(api, await TestApi.LoginAsync(api, s.Tenant.Slug, nutriEmail, TestApi.UserPassword));
        var url = $"/api/v1/members/{s.Member.Id}/assessments";

        // Un entrenador sin ese alumno asignado no lo ve: responde como si no existiera.
        await AssertProblemAsync(await strangerClient.GetAsync(url), HttpStatusCode.NotFound, "assessment.member_not_found");
        await AssertProblemAsync(await strangerClient.PostAsJsonAsync(url, Body()), HttpStatusCode.NotFound, "assessment.member_not_found");
        // Los permisos son sensibles: no los tienen por defecto el propietario, el administrador ni el nutricionista.
        foreach (var client in new[] { s.Tenant.Owner, admin, nutri })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(url)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(url, Body())).StatusCode);
        }
    }

    [Fact]
    public async Task No_se_registra_a_alumnos_inactivos_ni_de_otro_gimnasio()
    {
        var s = await NewSetupAsync("INA");
        var other = await NewSetupAsync("OTR");

        await AssertProblemAsync(await other.Trainer.PostAsJsonAsync($"/api/v1/members/{s.Member.Id}/assessments", Body()), HttpStatusCode.NotFound, "assessment.member_not_found");
        await s.Tenant.Owner.PatchAsJsonAsync($"/api/v1/members/{s.Member.Id}/status", new { isActive = false });

        await AssertProblemAsync(await s.Trainer.PostAsJsonAsync($"/api/v1/members/{s.Member.Id}/assessments", Body()), HttpStatusCode.Conflict, "assessment.member_inactive");
    }

    [Fact]
    public async Task Se_audita_el_hecho_sin_registrar_las_mediciones()
    {
        var s = await NewSetupAsync("AUD");
        await RecordAsync(s.Trainer, s.Member.Id, Body(weight: 73.7m, fat: 21.3m, method: "Caliper"));
        await s.Trainer.GetAsync($"/api/v1/members/{s.Member.Id}/assessments");

        var audit = await s.Tenant.Owner.GetStringAsync("/api/v1/audit-logs?pageSize=200");

        Assert.Contains("Wellness.AssessmentRecorded", audit);
        Assert.Contains("Wellness.AssessmentsRead", audit);
        Assert.DoesNotContain("73.7", audit);
        Assert.DoesNotContain("21.3", audit);
    }

    [Fact]
    public async Task La_base_impide_reescribir_o_borrar_evaluaciones_y_rechaza_datos_incoherentes()
    {
        var s = await NewSetupAsync("DB1");
        var a = await RecordAsync(s.Trainer, s.Member.Id, Body());

        foreach (var asApp in new[] { true, false })
        {
            await Assert.ThrowsAsync<PostgresException>(() => SqlAsync($"UPDATE wellness.body_assessments SET weight_kg = 10 WHERE id = '{a.Id}'", asApp));
            await Assert.ThrowsAsync<PostgresException>(() => SqlAsync($"DELETE FROM wellness.body_assessments WHERE id = '{a.Id}'", asApp));
        }

        // Debe fallar por la restricción de la tabla (no por otra causa como los permisos).
        var user = $"(SELECT id FROM identity.app_users WHERE tenant_id = '{s.Tenant.Id}' LIMIT 1)";
        var ex = await Assert.ThrowsAsync<PostgresException>(() => SqlAsync(
            $"INSERT INTO wellness.body_assessments (id, tenant_id, member_id, assessed_on, weight_kg, body_fat_percent, source, recorded_by, created_at) " +
            $"VALUES (gen_random_uuid(), '{s.Tenant.Id}', '{s.Member.Id}', current_date, 70, 20, 'Manual', {user}, now())"));
        Assert.Equal("ck_body_assessments_body_fat_method", ex.ConstraintName);
    }
}
