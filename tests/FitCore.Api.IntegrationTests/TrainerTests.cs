using System.Net;
using System.Net.Http.Json;
using FitCore.Api.IntegrationTests.Infrastructure;
using FitCore.Platform.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static FitCore.Api.IntegrationTests.Infrastructure.CommercialTestApi;

namespace FitCore.Api.IntegrationTests;

[Collection(ApiCollection1.Name)]
public class TrainerTests(ApiFixture api)
{
    private sealed record TrainerRow(Guid Id, Guid UserId, string FullName, string[] Specialties, string? Bio, string Status, DateOnly JoinedOn, Guid[] BranchIds, int ActiveMembers);

    private sealed record AssignmentRow(
        Guid Id, Guid TrainerId, string TrainerName, Guid MemberId, string MemberCode, string MemberName, Guid BranchId,
        DateOnly AssignedOn, DateOnly? EndedOn, string? EndReason, bool IsActive);

    private sealed record NoteRow(Guid Id, Guid MemberId, Guid TrainerId, string TrainerName, string Text, DateTimeOffset CreatedAt);

    private sealed record SlotRow(Guid Id, string Day, string Start, string End, Guid? BranchId);

    private sealed record Trainer(TrainerRow Profile, HttpClient Client, string Email);

    private static DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/Guayaquil")).DateTime);

    private async Task<Trainer> NewTrainerAsync(TestTenant tenant, string email, Guid branch, string[]? specialties = null, string role = "Entrenador")
    {
        var user = await TestApi.CreateUserAsync(tenant.Owner, email, role, branch);
        var response = await tenant.Owner.PostAsJsonAsync("/api/v1/trainers", new { userId = user.Id, specialties = specialties ?? ["Fuerza"], bio = "Entrenador de prueba" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var profile = (await response.Content.ReadFromJsonAsync<TrainerRow>())!;
        var client = TestApi.Authenticated(api, await TestApi.LoginAsync(api, tenant.Slug, email, TestApi.UserPassword));
        return new Trainer(profile, client, email);
    }

    private static async Task<AssignmentRow> AssignOkAsync(HttpClient client, Guid trainerId, Guid memberId, string? reason = null)
    {
        var response = await client.PostAsJsonAsync($"/api/v1/trainers/{trainerId}/members", new { memberId, reason });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AssignmentRow>())!;
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

    [Fact]
    public async Task Crear_un_perfil_normaliza_las_especialidades_y_no_se_puede_repetir()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var branch = await TestApi.CreateBranchAsync(tenant.Owner, "TRN");
        var user = await TestApi.CreateUserAsync(tenant.Owner, "ana.entrena@gym.test", "Entrenador", branch);

        var response = await tenant.Owner.PostAsJsonAsync("/api/v1/trainers", new
        {
            userId = user.Id, specialties = new[] { " Fuerza ", "fuerza", "Movilidad", "" }, certifications = "NSCA-CPT", bio = "  Diez años entrenando  ",
            joinedOn = "2024-02-01"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var trainer = (await response.Content.ReadFromJsonAsync<TrainerRow>())!;
        Assert.Equal(["Fuerza", "Movilidad"], trainer.Specialties);
        Assert.Equal("Diez años entrenando", trainer.Bio);
        Assert.Equal(new DateOnly(2024, 2, 1), trainer.JoinedOn);
        Assert.Equal("Active", trainer.Status);
        Assert.Equal([branch], trainer.BranchIds);
        Assert.Equal(0, trainer.ActiveMembers);

        await AssertProblemAsync(await tenant.Owner.PostAsJsonAsync("/api/v1/trainers", new { userId = user.Id }), HttpStatusCode.Conflict, "trainers.already_exists");
    }

    [Fact]
    public async Task Un_usuario_inexistente_de_otro_gimnasio_o_inactivo_no_puede_ser_entrenador_y_se_valida_el_cuerpo()
    {
        var a = await TestApi.NewTenantAsync(api);
        var b = await TestApi.NewTenantAsync(api);
        var branchA = await TestApi.CreateBranchAsync(a.Owner, "VA");
        var branchB = await TestApi.CreateBranchAsync(b.Owner, "VB");
        var foreign = await TestApi.CreateUserAsync(b.Owner, "ajeno@gym.test", "Entrenador", branchB);
        var inactive = await TestApi.CreateUserAsync(a.Owner, "inactivo@gym.test", "Entrenador", branchA);
        Assert.Equal(HttpStatusCode.OK, (await a.Owner.PutAsJsonAsync($"/api/v1/users/{inactive.Id}/status", new { isActive = false })).StatusCode);

        await AssertProblemAsync(await a.Owner.PostAsJsonAsync("/api/v1/trainers", new { userId = Guid.NewGuid() }), HttpStatusCode.UnprocessableEntity, "trainers.user_not_found");
        await AssertProblemAsync(await a.Owner.PostAsJsonAsync("/api/v1/trainers", new { userId = foreign.Id }), HttpStatusCode.UnprocessableEntity, "trainers.user_not_found");
        await AssertProblemAsync(await a.Owner.PostAsJsonAsync("/api/v1/trainers", new { userId = inactive.Id }), HttpStatusCode.UnprocessableEntity, "trainers.user_inactive");

        var user = await TestApi.CreateUserAsync(a.Owner, "valido@gym.test", "Entrenador", branchA);
        var tooMany = await a.Owner.PostAsJsonAsync("/api/v1/trainers", new { userId = user.Id, specialties = Enumerable.Range(1, 11).Select(i => $"Esp {i}").ToArray() });
        var tooLongBio = await a.Owner.PostAsJsonAsync("/api/v1/trainers", new { userId = user.Id, bio = new string('x', 1501) });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooMany.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooLongBio.StatusCode);
    }

    [Fact]
    public async Task El_alcance_por_sucursal_y_por_gimnasio_se_respeta()
    {
        var a = await TestApi.NewTenantAsync(api);
        var b = await TestApi.NewTenantAsync(api);
        var north = await TestApi.CreateBranchAsync(a.Owner, "N");
        var south = await TestApi.CreateBranchAsync(a.Owner, "S");
        var branchB = await TestApi.CreateBranchAsync(b.Owner, "OB");
        var trainerNorth = await NewTrainerAsync(a, "norte@gym.test", north);
        var trainerSouth = await NewTrainerAsync(a, "sur@gym.test", south);
        var foreign = await NewTrainerAsync(b, "otro@gym.test", branchB);
        await TestApi.CreateUserAsync(a.Owner, "admin.norte@gym.test", "AdministradorSucursal", north);
        var admin = TestApi.Authenticated(api, await TestApi.LoginAsync(api, a.Slug, "admin.norte@gym.test", TestApi.UserPassword));

        var ownerList = await a.Owner.GetFromJsonAsync<PagedDto<TrainerRow>>("/api/v1/trainers");
        var adminList = await admin.GetFromJsonAsync<PagedDto<TrainerRow>>("/api/v1/trainers");

        Assert.Equal(2, ownerList!.TotalCount);
        Assert.Equal([trainerNorth.Profile.Id], adminList!.Items.Select(t => t.Id));
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/v1/trainers/{trainerSouth.Profile.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.Owner.GetAsync($"/api/v1/trainers/{foreign.Profile.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.Owner.PutAsJsonAsync($"/api/v1/trainers/{foreign.Profile.Id}", new { joinedOn = "2025-01-01" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync($"/api/v1/trainers?branchId={south}")).StatusCode);
        var filtered = await a.Owner.GetFromJsonAsync<PagedDto<TrainerRow>>($"/api/v1/trainers?branchId={south}");
        Assert.Equal([trainerSouth.Profile.Id], filtered!.Items.Select(t => t.Id));
        var searched = await a.Owner.GetFromJsonAsync<PagedDto<TrainerRow>>("/api/v1/trainers?search=norte");
        Assert.Equal([trainerNorth.Profile.Id], searched!.Items.Select(t => t.Id));
    }

    [Fact]
    public async Task Los_permisos_separan_consultar_administrar_y_el_resto()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var branch = await TestApi.CreateBranchAsync(tenant.Owner, "PRM");
        var trainer = await NewTrainerAsync(tenant, "entrena@gym.test", branch);
        await TestApi.CreateUserAsync(tenant.Owner, "recepcion@gym.test", "Recepcion", branch);
        var reception = TestApi.Authenticated(api, await TestApi.LoginAsync(api, tenant.Slug, "recepcion@gym.test", TestApi.UserPassword));
        var member = await MemberTests.CreateMemberAsync(tenant.Owner, branch);

        Assert.Equal(HttpStatusCode.Forbidden, (await reception.GetAsync("/api/v1/trainers")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await trainer.Client.GetAsync("/api/v1/trainers")).StatusCode);
        // El entrenador consulta pero no administra: no habilita entrenadores, no edita perfiles ni asigna alumnos.
        Assert.Equal(HttpStatusCode.Forbidden, (await trainer.Client.PostAsJsonAsync("/api/v1/trainers", new { userId = trainer.Profile.UserId })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await trainer.Client.PutAsJsonAsync($"/api/v1/trainers/{trainer.Profile.Id}", new { joinedOn = "2025-01-01" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await trainer.Client.PatchAsJsonAsync($"/api/v1/trainers/{trainer.Profile.Id}/status", new { isActive = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await trainer.Client.PostAsJsonAsync($"/api/v1/trainers/{trainer.Profile.Id}/members", new { memberId = member.Id })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await trainer.Client.GetAsync($"/api/v1/members/{member.Id}/trainer-assignments")).StatusCode);
        // El rol Entrenador ya no ve el listado general de alumnos ni la asistencia del gimnasio.
        Assert.Equal(HttpStatusCode.Forbidden, (await trainer.Client.GetAsync("/api/v1/members")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await trainer.Client.GetAsync("/api/v1/attendance")).StatusCode);
    }

    [Fact]
    public async Task Mi_perfil_existe_para_el_entrenador_y_no_para_quien_no_lo_es()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var branch = await TestApi.CreateBranchAsync(tenant.Owner, "ME");
        var trainer = await NewTrainerAsync(tenant, "yo@gym.test", branch, ["Yoga"]);

        var mine = await trainer.Client.GetFromJsonAsync<TrainerRow>("/api/v1/trainers/me");
        Assert.Equal(trainer.Profile.Id, mine!.Id);
        Assert.Equal(["Yoga"], mine.Specialties);
        await AssertProblemAsync(await tenant.Owner.GetAsync("/api/v1/trainers/me"), HttpStatusCode.NotFound, "trainers.no_profile");
    }

    [Fact]
    public async Task La_disponibilidad_se_reemplaza_se_ordena_y_valida_las_franjas()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var branch = await TestApi.CreateBranchAsync(tenant.Owner, "DSP");
        var other = await TestApi.CreateBranchAsync(tenant.Owner, "OTH");
        var trainer = await NewTrainerAsync(tenant, "disp@gym.test", branch);
        var colleague = await NewTrainerAsync(tenant, "colega@gym.test", branch);
        var url = $"/api/v1/trainers/{trainer.Profile.Id}/availability";

        var first = await trainer.Client.PutAsJsonAsync(url, new
        {
            slots = new object[]
            {
                new { day = "Sunday", start = "08:00", end = "12:00", branchId = branch },
                new { day = "Monday", start = "16:00", end = "20:00" },
                new { day = "Monday", start = "06:00", end = "10:00" }
            }
        });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var listed = await tenant.Owner.GetFromJsonAsync<SlotRow[]>(url);
        Assert.Equal(["Monday", "Monday", "Sunday"], listed!.Select(s => s.Day));
        Assert.Equal(["06:00", "16:00", "08:00"], listed!.Select(s => s.Start));

        // Reemplazo completo: lo anterior desaparece.
        Assert.Equal(HttpStatusCode.OK, (await trainer.Client.PutAsJsonAsync(url, new { slots = new object[] { new { day = "Friday", start = "07:00", end = "09:00" } } })).StatusCode);
        Assert.Single((await tenant.Owner.GetFromJsonAsync<SlotRow[]>(url))!);

        await AssertProblemAsync(await trainer.Client.PutAsJsonAsync(url, new { slots = new object[] { new { day = "Friday", start = "07:00", end = "09:00" }, new { day = "Friday", start = "08:00", end = "10:00" } } }),
            HttpStatusCode.UnprocessableEntity, "trainers.availability_overlap");
        await AssertProblemAsync(await trainer.Client.PutAsJsonAsync(url, new { slots = new object[] { new { day = "Friday", start = "10:00", end = "09:00" } } }),
            HttpStatusCode.UnprocessableEntity, "trainers.availability_invalid_range");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await trainer.Client.PutAsJsonAsync(url, new { slots = new object[] { new { day = "Friday", start = "25:00", end = "26:00" } } })).StatusCode);
        await AssertProblemAsync(await trainer.Client.PutAsJsonAsync(url, new { slots = new object[] { new { day = "Friday", start = "07:00", end = "09:00", branchId = other } } }),
            HttpStatusCode.UnprocessableEntity, "trainers.availability_branch_invalid");
        // Un fallo no pierde la disponibilidad que ya tenía.
        Assert.Single((await tenant.Owner.GetFromJsonAsync<SlotRow[]>(url))!);

        // La disponibilidad de otro la cambia quien administra, no un colega.
        Assert.Equal(HttpStatusCode.Forbidden, (await colleague.Client.PutAsJsonAsync(url, new { slots = Array.Empty<object>() })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await tenant.Owner.PutAsJsonAsync(url, new { slots = Array.Empty<object>() })).StatusCode);
        Assert.Empty((await tenant.Owner.GetFromJsonAsync<SlotRow[]>(url))!);
    }

    [Fact]
    public async Task Asignar_reasignar_y_quitar_alumnos_conserva_el_historial()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var branch = await TestApi.CreateBranchAsync(tenant.Owner, "ASG");
        var one = await NewTrainerAsync(tenant, "uno@gym.test", branch);
        var two = await NewTrainerAsync(tenant, "dos@gym.test", branch);
        var member = await MemberTests.CreateMemberAsync(tenant.Owner, branch);

        var first = await AssignOkAsync(tenant.Owner, one.Profile.Id, member.Id);
        Assert.True(first.IsActive);
        Assert.Equal(Today, first.AssignedOn);
        Assert.Equal(member.Code, first.MemberCode);
        await AssertProblemAsync(await tenant.Owner.PostAsJsonAsync($"/api/v1/trainers/{one.Profile.Id}/members", new { memberId = member.Id }), HttpStatusCode.Conflict, "trainers.already_assigned");

        // Cambio de entrenador: la asignación anterior se cierra con su motivo y se abre la nueva.
        var second = await AssignOkAsync(tenant.Owner, two.Profile.Id, member.Id, "Cambio de horario");
        var history = await tenant.Owner.GetFromJsonAsync<AssignmentRow[]>($"/api/v1/members/{member.Id}/trainer-assignments");
        Assert.Equal(2, history!.Length);
        Assert.Single(history, a => a.IsActive);
        var ended = history.Single(a => !a.IsActive);
        Assert.Equal(one.Profile.Id, ended.TrainerId);
        Assert.Equal("Cambio de horario", ended.EndReason);
        Assert.Equal(two.Profile.Id, history.Single(a => a.IsActive).TrainerId);
        Assert.Equal(second.Id, history.Single(a => a.IsActive).Id);

        var counts = await tenant.Owner.GetFromJsonAsync<PagedDto<TrainerRow>>("/api/v1/trainers");
        Assert.Equal(0, counts!.Items.Single(t => t.Id == one.Profile.Id).ActiveMembers);
        Assert.Equal(1, counts.Items.Single(t => t.Id == two.Profile.Id).ActiveMembers);

        // Quitar exige motivo y un entrenador al que el alumno esté asignado hoy.
        var url = $"/api/v1/trainers/{two.Profile.Id}/members/{member.Id}/unassign";
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await tenant.Owner.PostAsJsonAsync(url, new { reason = "" })).StatusCode);
        await AssertProblemAsync(await tenant.Owner.PostAsJsonAsync($"/api/v1/trainers/{one.Profile.Id}/members/{member.Id}/unassign", new { reason = "Ya no entrena" }), HttpStatusCode.Conflict, "trainers.no_active_assignment");
        Assert.Equal(HttpStatusCode.OK, (await tenant.Owner.PostAsJsonAsync(url, new { reason = "Terminó su plan" })).StatusCode);
        var after = await tenant.Owner.GetFromJsonAsync<AssignmentRow[]>($"/api/v1/members/{member.Id}/trainer-assignments");
        Assert.DoesNotContain(after!, a => a.IsActive);
        Assert.Equal(2, after!.Length);
    }

    [Fact]
    public async Task No_se_asigna_a_un_entrenador_inactivo_ni_a_otra_sucursal_ni_a_un_alumno_inactivo_o_ajeno()
    {
        var a = await TestApi.NewTenantAsync(api);
        var b = await TestApi.NewTenantAsync(api);
        var north = await TestApi.CreateBranchAsync(a.Owner, "AN");
        var south = await TestApi.CreateBranchAsync(a.Owner, "AS");
        var branchB = await TestApi.CreateBranchAsync(b.Owner, "BB");
        var trainer = await NewTrainerAsync(a, "norte@gym.test", north);
        var memberSouth = await MemberTests.CreateMemberAsync(a.Owner, south);
        var inactiveMember = await MemberTests.CreateMemberAsync(a.Owner, north);
        var foreignMember = await MemberTests.CreateMemberAsync(b.Owner, branchB);
        await a.Owner.PatchAsJsonAsync($"/api/v1/members/{inactiveMember.Id}/status", new { isActive = false });
        var url = $"/api/v1/trainers/{trainer.Profile.Id}/members";

        await AssertProblemAsync(await a.Owner.PostAsJsonAsync(url, new { memberId = memberSouth.Id }), HttpStatusCode.Conflict, "trainers.branch_mismatch");
        await AssertProblemAsync(await a.Owner.PostAsJsonAsync(url, new { memberId = inactiveMember.Id }), HttpStatusCode.Conflict, "trainers.member_inactive");
        await AssertProblemAsync(await a.Owner.PostAsJsonAsync(url, new { memberId = foreignMember.Id }), HttpStatusCode.NotFound, "trainers.member_not_found");
        await AssertProblemAsync(await a.Owner.PostAsJsonAsync(url, new { memberId = Guid.NewGuid() }), HttpStatusCode.NotFound, "trainers.member_not_found");

        var member = await MemberTests.CreateMemberAsync(a.Owner, north);
        Assert.Equal(HttpStatusCode.OK, (await a.Owner.PatchAsJsonAsync($"/api/v1/trainers/{trainer.Profile.Id}/status", new { isActive = false })).StatusCode);
        await AssertProblemAsync(await a.Owner.PostAsJsonAsync(url, new { memberId = member.Id }), HttpStatusCode.Conflict, "trainers.inactive");
    }

    [Fact]
    public async Task Un_entrenador_con_alumnos_activos_no_se_desactiva_hasta_reasignarlos()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var branch = await TestApi.CreateBranchAsync(tenant.Owner, "DEA");
        var trainer = await NewTrainerAsync(tenant, "dea@gym.test", branch);
        var member = await MemberTests.CreateMemberAsync(tenant.Owner, branch);
        await AssignOkAsync(tenant.Owner, trainer.Profile.Id, member.Id);
        var status = $"/api/v1/trainers/{trainer.Profile.Id}/status";

        await AssertProblemAsync(await tenant.Owner.PatchAsJsonAsync(status, new { isActive = false }), HttpStatusCode.Conflict, "trainers.has_active_members");
        await tenant.Owner.PostAsJsonAsync($"/api/v1/trainers/{trainer.Profile.Id}/members/{member.Id}/unassign", new { reason = "Reasignación" });

        Assert.Equal(HttpStatusCode.OK, (await tenant.Owner.PatchAsJsonAsync(status, new { isActive = false })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await tenant.Owner.PatchAsJsonAsync(status, new { isActive = true })).StatusCode);
    }

    [Fact]
    public async Task Peticiones_simultaneas_dejan_exactamente_un_entrenador_activo_por_alumno()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var branch = await TestApi.CreateBranchAsync(tenant.Owner, "RAC");
        var trainers = new List<Trainer>();
        for (var i = 0; i < 4; i++)
        {
            trainers.Add(await NewTrainerAsync(tenant, $"carrera{i}@gym.test", branch));
        }

        var member = await MemberTests.CreateMemberAsync(tenant.Owner, branch);

        var responses = await Task.WhenAll(trainers.Select(t =>
            tenant.Owner.PostAsJsonAsync($"/api/v1/trainers/{t.Profile.Id}/members", new { memberId = member.Id })));

        var history = await tenant.Owner.GetFromJsonAsync<AssignmentRow[]>($"/api/v1/members/{member.Id}/trainer-assignments");
        Assert.Single(history!, a => a.IsActive);
        var created = responses.Count(r => r.StatusCode == HttpStatusCode.Created);
        Assert.True(created >= 1);
        Assert.Equal(created, history!.Length);
        Assert.All(responses, r => Assert.True(r.StatusCode is HttpStatusCode.Created or HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task Un_entrenador_solo_ve_sus_alumnos_y_quien_administra_ve_los_de_cualquiera()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var branch = await TestApi.CreateBranchAsync(tenant.Owner, "OWN");
        var one = await NewTrainerAsync(tenant, "uno@gym.test", branch);
        var two = await NewTrainerAsync(tenant, "dos@gym.test", branch);
        var mine = await MemberTests.CreateMemberAsync(tenant.Owner, branch);
        var theirs = await MemberTests.CreateMemberAsync(tenant.Owner, branch);
        await AssignOkAsync(tenant.Owner, one.Profile.Id, mine.Id);
        await AssignOkAsync(tenant.Owner, two.Profile.Id, theirs.Id);

        var own = await one.Client.GetFromJsonAsync<PagedDto<AssignmentRow>>($"/api/v1/trainers/{one.Profile.Id}/members");
        Assert.Equal([mine.Id], own!.Items.Select(a => a.MemberId));
        await AssertProblemAsync(await one.Client.GetAsync($"/api/v1/trainers/{two.Profile.Id}/members"), HttpStatusCode.Forbidden, "trainers.not_yours");
        var admin = await tenant.Owner.GetFromJsonAsync<PagedDto<AssignmentRow>>($"/api/v1/trainers/{two.Profile.Id}/members");
        Assert.Equal([theirs.Id], admin!.Items.Select(a => a.MemberId));

        await tenant.Owner.PostAsJsonAsync($"/api/v1/trainers/{one.Profile.Id}/members/{mine.Id}/unassign", new { reason = "Cambio" });
        Assert.Empty((await one.Client.GetFromJsonAsync<PagedDto<AssignmentRow>>($"/api/v1/trainers/{one.Profile.Id}/members"))!.Items);
        Assert.Single((await one.Client.GetFromJsonAsync<PagedDto<AssignmentRow>>($"/api/v1/trainers/{one.Profile.Id}/members?activeOnly=false"))!.Items);
    }

    [Fact]
    public async Task Las_observaciones_solo_las_lee_y_escribe_el_entrenador_asignado()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var branch = await TestApi.CreateBranchAsync(tenant.Owner, "NOT");
        var one = await NewTrainerAsync(tenant, "uno@gym.test", branch);
        var two = await NewTrainerAsync(tenant, "dos@gym.test", branch);
        var member = await MemberTests.CreateMemberAsync(tenant.Owner, branch);
        var url = $"/api/v1/members/{member.Id}/trainer-notes";

        // Sin asignación nadie escribe, tampoco el entrenador.
        await AssertProblemAsync(await one.Client.PostAsJsonAsync(url, new { text = "Hola" }), HttpStatusCode.Forbidden, "trainers.not_assigned");
        await AssignOkAsync(tenant.Owner, one.Profile.Id, member.Id);

        var created = await one.Client.PostAsJsonAsync(url, new { text = "  Molestia leve en el hombro derecho  " });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var note = (await created.Content.ReadFromJsonAsync<NoteRow>())!;
        Assert.Equal("Molestia leve en el hombro derecho", note.Text);
        Assert.Equal(one.Profile.Id, note.TrainerId);

        var listed = await one.Client.GetFromJsonAsync<PagedDto<NoteRow>>(url);
        Assert.Equal([note.Id], listed!.Items.Select(n => n.Id));
        // Otro entrenador, el propietario y el administrador de sucursal no leen ni escriben.
        await AssertProblemAsync(await two.Client.GetAsync(url), HttpStatusCode.Forbidden, "trainers.not_assigned");
        await AssertProblemAsync(await two.Client.PostAsJsonAsync(url, new { text = "x" }), HttpStatusCode.Forbidden, "trainers.not_assigned");
        Assert.Equal(HttpStatusCode.Forbidden, (await tenant.Owner.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tenant.Owner.PostAsJsonAsync(url, new { text = "x" })).StatusCode);
        await TestApi.CreateUserAsync(tenant.Owner, "admin@gym.test", "AdministradorSucursal", branch);
        var admin = TestApi.Authenticated(api, await TestApi.LoginAsync(api, tenant.Slug, "admin@gym.test", TestApi.UserPassword));
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync(url)).StatusCode);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await one.Client.PostAsJsonAsync(url, new { text = "   " })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await one.Client.PostAsJsonAsync(url, new { text = new string('x', 2001) })).StatusCode);

        // Al cambiar de entrenador, el anterior pierde el acceso y el nuevo conserva la continuidad.
        await AssignOkAsync(tenant.Owner, two.Profile.Id, member.Id, "Cambio de turno");
        await AssertProblemAsync(await one.Client.GetAsync(url), HttpStatusCode.Forbidden, "trainers.not_assigned");
        var inherited = await two.Client.GetFromJsonAsync<PagedDto<NoteRow>>(url);
        Assert.Equal([note.Id], inherited!.Items.Select(n => n.Id));
    }

    [Fact]
    public async Task Las_observaciones_no_se_modifican_ni_se_borran_y_la_auditoria_registra_el_hecho_sin_el_texto()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var branch = await TestApi.CreateBranchAsync(tenant.Owner, "AUD");
        var trainer = await NewTrainerAsync(tenant, "aud@gym.test", branch);
        var member = await MemberTests.CreateMemberAsync(tenant.Owner, branch);
        await AssignOkAsync(tenant.Owner, trainer.Profile.Id, member.Id);
        var url = $"/api/v1/members/{member.Id}/trainer-notes";
        const string secret = "Texto-Reservado-Del-Entrenador-12345";
        var note = (await (await trainer.Client.PostAsJsonAsync(url, new { text = secret })).Content.ReadFromJsonAsync<NoteRow>())!;
        await trainer.Client.GetAsync(url);

        // La base lo impide aunque se intente con el rol de la aplicación o con el administrador.
        await Assert.ThrowsAsync<PostgresException>(() => SqlAsync($"UPDATE training.trainer_notes SET text = 'editada' WHERE id = '{note.Id}'", asApp: true));
        await Assert.ThrowsAsync<PostgresException>(() => SqlAsync($"DELETE FROM training.trainer_notes WHERE id = '{note.Id}'", asApp: true));
        await Assert.ThrowsAsync<PostgresException>(() => SqlAsync($"UPDATE training.trainer_notes SET text = 'editada' WHERE id = '{note.Id}'"));
        await Assert.ThrowsAsync<PostgresException>(() => SqlAsync($"DELETE FROM training.trainer_notes WHERE id = '{note.Id}'"));

        var audit = await tenant.Owner.GetStringAsync("/api/v1/audit-logs?pageSize=200");
        Assert.Contains("Wellness.TrainerNoteCreated", audit);
        Assert.Contains("Wellness.TrainerNotesRead", audit);
        Assert.DoesNotContain(secret, audit);
    }

    [Fact]
    public async Task Una_asignacion_cerrada_no_se_reescribe_en_la_base()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var branch = await TestApi.CreateBranchAsync(tenant.Owner, "IMM");
        var trainer = await NewTrainerAsync(tenant, "imm@gym.test", branch);
        var member = await MemberTests.CreateMemberAsync(tenant.Owner, branch);
        var assignment = await AssignOkAsync(tenant.Owner, trainer.Profile.Id, member.Id);
        await tenant.Owner.PostAsJsonAsync($"/api/v1/trainers/{trainer.Profile.Id}/members/{member.Id}/unassign", new { reason = "Fin" });

        await Assert.ThrowsAsync<PostgresException>(() => SqlAsync($"UPDATE training.trainer_assignments SET end_reason = 'otra cosa' WHERE id = '{assignment.Id}'"));
        await Assert.ThrowsAsync<PostgresException>(() => SqlAsync($"DELETE FROM training.trainer_assignments WHERE id = '{assignment.Id}'", asApp: true));
    }

    [Fact]
    public async Task Los_permisos_sensibles_no_se_dan_al_propietario_pero_el_puede_concederlos_a_un_rol()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var branch = await TestApi.CreateBranchAsync(tenant.Owner, "SEN");
        var roles = await tenant.Owner.GetFromJsonAsync<RoleDto[]>("/api/v1/roles");
        Assert.DoesNotContain("trainers.notes", roles!.Single(r => r.Name == "Propietario").Permissions);
        Assert.DoesNotContain("trainers.notes", roles!.Single(r => r.Name == "AdministradorSucursal").Permissions);
        Assert.Contains("trainers.notes", roles!.Single(r => r.Name == "Entrenador").Permissions);
        Assert.Contains("trainers.manage", roles!.Single(r => r.Name == "Propietario").Permissions);

        // El propietario administra roles: puede conceder el permiso sensible a un rol sin tenerlo él.
        var roleId = await CreateRoleAsync(tenant.Owner, "Supervisor técnico", false, "trainers.read", "trainers.notes");
        Assert.NotEqual(Guid.Empty, roleId);

        // Quien no administra roles no puede dárselo a nadie (escalada de privilegios).
        await TestApi.CreateUserAsync(tenant.Owner, "admin@gym.test", "AdministradorSucursal", branch);
        var admin = TestApi.Authenticated(api, await TestApi.LoginAsync(api, tenant.Slug, "admin@gym.test", TestApi.UserPassword));
        var denied = await admin.PostAsJsonAsync("/api/v1/roles", new { name = "Intento", description = "x", grantsAllBranches = false, permissions = new[] { "trainers.notes" } });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task Al_reiniciar_la_API_el_propietario_no_recupera_los_permisos_sensibles()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var ownerRole = (await tenant.Owner.GetFromJsonAsync<RoleDto[]>("/api/v1/roles"))!.Single(r => r.Name == "Propietario");
        // Simula un gimnasio creado antes de que existiera la regla: el propietario tiene un permiso sensible.
        await SqlAsync($"""
            INSERT INTO identity.role_permissions (tenant_id, role_id, permission_code)
            SELECT '{tenant.Id}', '{ownerRole.Id}', code FROM identity.permissions WHERE code = 'trainers.notes'
            ON CONFLICT DO NOTHING
            """);
        var before = (await tenant.Owner.GetFromJsonAsync<RoleDto[]>("/api/v1/roles"))!.Single(r => r.Name == "Propietario");
        Assert.Contains("trainers.notes", before.Permissions);

        // Es lo mismo que ocurre en cada arranque: la sincronización de roles de plantilla retira lo que no le corresponde.
        await DatabaseInitializer.InitializeAsync(api.Services, api.Services.GetRequiredService<IConfiguration>());

        var after = (await tenant.Owner.GetFromJsonAsync<RoleDto[]>("/api/v1/roles"))!.Single(r => r.Name == "Propietario");
        Assert.DoesNotContain("trainers.notes", after.Permissions);
        // Lo que sí le corresponde se conserva (incluido poder asignar el rol Alumno, que lleva me.read).
        Assert.Contains("me.read", after.Permissions);
        Assert.Contains("trainers.manage", after.Permissions);
    }

    [Fact]
    public async Task El_alumno_con_acceso_al_portal_no_entra_a_los_recursos_de_entrenadores()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var branch = await TestApi.CreateBranchAsync(tenant.Owner, "STU");
        var email = $"alumno.{Guid.NewGuid():N}"[..20] + "@alumnos.test";
        var member = await (await tenant.Owner.PostAsJsonAsync("/api/v1/members", new
        {
            branchId = branch, firstName = "Eva", lastName = "Prueba", idType = "Cedula", idNumber = MemberTests.NewCedula(), email, phone = "0991234567"
        })).Content.ReadFromJsonAsync<MemberDto>();
        var access = await (await tenant.Owner.PostAsync($"/api/v1/members/{member!.Id}/access", null)).Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var temp = TestApi.Authenticated(api, await TestApi.LoginAsync(api, tenant.Slug, email, access.GetProperty("temporaryPassword").GetString()!));
        await temp.PostAsJsonAsync("/api/v1/auth/change-password", new { currentPassword = access.GetProperty("temporaryPassword").GetString(), newPassword = "Nueva-Clave-2026x" });
        var student = TestApi.Authenticated(api, await TestApi.LoginAsync(api, tenant.Slug, email, "Nueva-Clave-2026x"));

        Assert.Equal(HttpStatusCode.Forbidden, (await student.GetAsync("/api/v1/trainers")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await student.GetAsync($"/api/v1/members/{member.Id}/trainer-notes")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await student.GetAsync($"/api/v1/members/{member.Id}/trainer-assignments")).StatusCode);
    }
}
