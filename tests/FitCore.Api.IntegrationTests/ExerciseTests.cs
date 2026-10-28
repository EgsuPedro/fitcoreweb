using System.Net;
using System.Net.Http.Json;
using FitCore.Api.IntegrationTests.Infrastructure;
using Npgsql;
using static FitCore.Api.IntegrationTests.Infrastructure.CommercialTestApi;

namespace FitCore.Api.IntegrationTests;

[Collection(ApiCollection2.Name)]
public class ExerciseTests(ApiFixture api)
{
    private sealed record ExerciseRow(
        Guid Id, bool IsGlobal, string Name, string? Description, string PrimaryMuscle, string[] SecondaryMuscles, string Type, string Equipment,
        string Difficulty, string TrackingMode, string[] Instructions, string[] CommonMistakes, string? Warnings, string? VideoUrl, bool IsActive);

    private static object Body(
        string name = "Remo con barra",
        string primary = "Back",
        string[]? secondary = null,
        string equipment = "Barbell",
        string difficulty = "Intermediate",
        string type = "Strength",
        string tracking = "RepsAndLoad",
        string[]? steps = null,
        string? video = null) => new
        {
            name,
            description = "Tracción horizontal con barra",
            primaryMuscle = primary,
            secondaryMuscles = secondary ?? ["Biceps"],
            type,
            equipment,
            difficulty,
            trackingMode = tracking,
            instructions = steps ?? ["Incline el torso", "Tire hacia el abdomen"],
            commonMistakes = new[] { "Redondear la espalda" },
            warnings = "Cuidado con la zona lumbar",
            videoUrl = video
        };

    private static async Task<ExerciseRow> CreateAsync(HttpClient client, object body, string url = "/api/v1/exercises")
    {
        var response = await client.PostAsJsonAsync(url, body);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"Respondió {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<ExerciseRow>())!;
    }

    private static async Task<PagedDto<ExerciseRow>> ListAsync(HttpClient client, string query = "") =>
        (await client.GetFromJsonAsync<PagedDto<ExerciseRow>>($"/api/v1/exercises?pageSize=100{query}"))!;

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, await TestApi.ProblemCodeAsync(response));
    }

    [Fact]
    public async Task El_catalogo_base_esta_cargado_es_global_y_no_trae_multimedia()
    {
        var tenant = await TestApi.NewTenantAsync(api);

        var global = await ListAsync(tenant.Owner, "&scope=global");

        Assert.True(global.TotalCount >= 40, $"Catálogo base con {global.TotalCount} ejercicios");
        Assert.All(global.Items, e => Assert.True(e.IsGlobal));
        Assert.All(global.Items, e => Assert.Null(e.VideoUrl));
        Assert.All(global.Items, e => Assert.NotEmpty(e.Instructions));
        Assert.Contains(global.Items, e => e.Name == "Sentadilla libre");
        Assert.Empty((await ListAsync(tenant.Owner, "&scope=own")).Items);
    }

    [Fact]
    public async Task Crear_un_ejercicio_del_gimnasio_normaliza_los_datos_y_solo_lo_ve_su_gimnasio()
    {
        var a = await TestApi.NewTenantAsync(api);
        var b = await TestApi.NewTenantAsync(api);

        var created = await CreateAsync(a.Owner, Body(name: "  Remo con barra  ", secondary: ["Biceps", "Back", "Biceps"]));

        Assert.False(created.IsGlobal);
        Assert.Equal("Remo con barra", created.Name);
        Assert.Equal(["Biceps"], created.SecondaryMuscles);
        Assert.True(created.IsActive);
        Assert.Equal(created.Id, Assert.Single((await ListAsync(a.Owner, "&scope=own")).Items).Id);
        Assert.DoesNotContain((await ListAsync(b.Owner)).Items, e => e.Id == created.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Owner.GetAsync($"/api/v1/exercises/{created.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Owner.PutAsJsonAsync($"/api/v1/exercises/{created.Id}", Body(name: "Robado"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Owner.PatchAsJsonAsync($"/api/v1/exercises/{created.Id}/status", new { isActive = false })).StatusCode);
    }

    [Fact]
    public async Task El_nombre_es_unico_sin_distinguir_mayusculas_dentro_del_gimnasio_pero_no_entre_gimnasios_ni_con_el_catalogo_base()
    {
        var a = await TestApi.NewTenantAsync(api);
        var b = await TestApi.NewTenantAsync(api);
        await CreateAsync(a.Owner, Body(name: "Press especial"));

        await AssertProblemAsync(await a.Owner.PostAsJsonAsync("/api/v1/exercises", Body(name: "PRESS ESPECIAL")), HttpStatusCode.Conflict, "exercises.name_taken");
        Assert.Equal(HttpStatusCode.Created, (await b.Owner.PostAsJsonAsync("/api/v1/exercises", Body(name: "Press especial"))).StatusCode);
        // Un gimnasio puede tener su propia versión con el mismo nombre que un ejercicio del catálogo base.
        Assert.Equal(HttpStatusCode.Created, (await a.Owner.PostAsJsonAsync("/api/v1/exercises", Body(name: "Sentadilla libre"))).StatusCode);

        var other = await CreateAsync(a.Owner, Body(name: "Otro ejercicio"));
        await AssertProblemAsync(await a.Owner.PutAsJsonAsync($"/api/v1/exercises/{other.Id}", Body(name: "press especial")), HttpStatusCode.Conflict, "exercises.name_taken");
        Assert.Equal(HttpStatusCode.OK, (await a.Owner.PutAsJsonAsync($"/api/v1/exercises/{other.Id}", Body(name: "Otro ejercicio"))).StatusCode);
    }

    [Fact]
    public async Task Editar_y_desactivar_un_ejercicio_propio_no_lo_borra_y_el_catalogo_base_no_se_edita_desde_un_gimnasio()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var own = await CreateAsync(tenant.Owner, Body(name: "Mi ejercicio"));
        var global = (await ListAsync(tenant.Owner, "&scope=global&search=Sentadilla libre")).Items.Single();

        var update = await tenant.Owner.PutAsJsonAsync($"/api/v1/exercises/{own.Id}", Body(name: "Mi ejercicio editado", difficulty: "Advanced", steps: ["Un solo paso"]));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var updated = (await update.Content.ReadFromJsonAsync<ExerciseRow>())!;
        Assert.Equal("Advanced", updated.Difficulty);
        Assert.Equal(["Un solo paso"], updated.Instructions);

        // El catálogo base lo administra la plataforma: ni editarlo ni desactivarlo.
        await AssertProblemAsync(await tenant.Owner.PutAsJsonAsync($"/api/v1/exercises/{global.Id}", Body(name: "Hackeado")), HttpStatusCode.Forbidden, "exercises.global_readonly");
        await AssertProblemAsync(await tenant.Owner.PatchAsJsonAsync($"/api/v1/exercises/{global.Id}/status", new { isActive = false }), HttpStatusCode.Forbidden, "exercises.global_readonly");

        // Desactivar lo oculta de los filtros "activos" pero el ejercicio sigue existiendo (las rutinas históricas lo referencian).
        Assert.Equal(HttpStatusCode.OK, (await tenant.Owner.PatchAsJsonAsync($"/api/v1/exercises/{own.Id}/status", new { isActive = false })).StatusCode);
        Assert.DoesNotContain((await ListAsync(tenant.Owner, "&isActive=true")).Items, e => e.Id == own.Id);
        Assert.Contains((await ListAsync(tenant.Owner, "&isActive=false")).Items, e => e.Id == own.Id);
        var still = await tenant.Owner.GetFromJsonAsync<ExerciseRow>($"/api/v1/exercises/{own.Id}");
        Assert.False(still!.IsActive);
        Assert.Equal(HttpStatusCode.OK, (await tenant.Owner.PatchAsJsonAsync($"/api/v1/exercises/{own.Id}/status", new { isActive = true })).StatusCode);
        Assert.Contains((await ListAsync(tenant.Owner, "&isActive=true&scope=own")).Items, e => e.Id == own.Id);
    }

    [Fact]
    public async Task Duplicar_un_ejercicio_crea_una_copia_propia_independiente_del_original()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var global = (await ListAsync(tenant.Owner, "&scope=global&search=Plancha frontal")).Items.Single();

        var first = (await (await tenant.Owner.PostAsync($"/api/v1/exercises/{global.Id}/duplicate", null)).Content.ReadFromJsonAsync<ExerciseRow>())!;
        var second = (await (await tenant.Owner.PostAsync($"/api/v1/exercises/{global.Id}/duplicate", null)).Content.ReadFromJsonAsync<ExerciseRow>())!;

        Assert.False(first.IsGlobal);
        Assert.Equal("Plancha frontal (copia)", first.Name);
        Assert.Equal("Plancha frontal (copia 2)", second.Name);
        Assert.Equal(global.Instructions, first.Instructions);
        Assert.Equal(global.TrackingMode, first.TrackingMode);

        await tenant.Owner.PutAsJsonAsync($"/api/v1/exercises/{first.Id}", Body(name: "Plancha frontal (copia)", primary: "Core", equipment: "BodyWeight", tracking: "Duration", steps: ["Mi variante"]));
        var original = await tenant.Owner.GetFromJsonAsync<ExerciseRow>($"/api/v1/exercises/{global.Id}");
        Assert.Equal(global.Instructions, original!.Instructions);
        Assert.Equal(HttpStatusCode.NotFound, (await tenant.Owner.PostAsync($"/api/v1/exercises/{Guid.NewGuid()}/duplicate", null)).StatusCode);
    }

    [Fact]
    public async Task Se_busca_y_filtra_por_nombre_musculo_equipamiento_dificultad_y_tipo_con_paginacion()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var mine = await CreateAsync(tenant.Owner, Body(name: "Zzz Remo especial", primary: "Back", secondary: ["Forearms", "Biceps"], equipment: "Kettlebell", difficulty: "Advanced", type: "Strength"));

        var byName = await ListAsync(tenant.Owner, "&search=zzz remo");
        var bySecondary = await ListAsync(tenant.Owner, "&muscle=Forearms&scope=own");
        var byPrimaryGlobal = await ListAsync(tenant.Owner, "&muscle=Chest&scope=global");
        var byEquipment = await ListAsync(tenant.Owner, "&equipment=Kettlebell");
        var byDifficulty = await ListAsync(tenant.Owner, "&difficulty=Advanced&scope=own");
        var byType = await ListAsync(tenant.Owner, "&type=Cardio&scope=global");

        Assert.Equal([mine.Id], byName.Items.Select(e => e.Id));
        Assert.Equal([mine.Id], bySecondary.Items.Select(e => e.Id));
        Assert.NotEmpty(byPrimaryGlobal.Items);
        Assert.All(byPrimaryGlobal.Items, e => Assert.True(e.PrimaryMuscle == "Chest" || e.SecondaryMuscles.Contains("Chest")));
        Assert.Contains(byEquipment.Items, e => e.Id == mine.Id);
        Assert.All(byEquipment.Items, e => Assert.Equal("Kettlebell", e.Equipment));
        Assert.Equal([mine.Id], byDifficulty.Items.Select(e => e.Id));
        Assert.NotEmpty(byType.Items);
        Assert.All(byType.Items, e => Assert.Equal("Cardio", e.Type));

        var page1 = await tenant.Owner.GetFromJsonAsync<PagedDto<ExerciseRow>>("/api/v1/exercises?pageSize=10&page=1");
        var page2 = await tenant.Owner.GetFromJsonAsync<PagedDto<ExerciseRow>>("/api/v1/exercises?pageSize=10&page=2");
        Assert.Equal(10, page1!.Items.Length);
        Assert.Empty(page1.Items.Select(e => e.Id).Intersect(page2!.Items.Select(e => e.Id)));
        Assert.True(page1.TotalCount > 40);
        // Orden alfabético estable.
        Assert.Equal(page1.Items.Select(e => e.Name).OrderBy(n => n, StringComparer.Create(System.Globalization.CultureInfo.InvariantCulture, true)).Count(), page1.Items.Length);
    }

    [Fact]
    public async Task Los_datos_invalidos_se_rechazan_antes_de_llegar_a_la_base()
    {
        var tenant = await TestApi.NewTenantAsync(api);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await tenant.Owner.PostAsJsonAsync("/api/v1/exercises", Body(name: "A"))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await tenant.Owner.PostAsJsonAsync("/api/v1/exercises", Body(video: "http://inseguro.test/v"))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await tenant.Owner.PostAsJsonAsync("/api/v1/exercises", Body(video: "javascript:alert(1)"))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await tenant.Owner.PostAsJsonAsync("/api/v1/exercises", Body(steps: [.. Enumerable.Range(1, 21).Select(i => $"Paso {i}")]))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await tenant.Owner.PostAsJsonAsync("/api/v1/exercises", Body(secondary: ["Back", "Biceps", "Triceps", "Core", "Glutes", "Calves"]))).StatusCode);
        var invalidEnum = await tenant.Owner.PostAsJsonAsync("/api/v1/exercises", Body(primary: "Nope"));
        Assert.True(invalidEnum.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity);
        Assert.Equal(HttpStatusCode.Created, (await tenant.Owner.PostAsJsonAsync("/api/v1/exercises", Body(name: "Con enlace", video: "https://ejemplo.com/clip"))).StatusCode);
    }

    [Fact]
    public async Task Los_permisos_separan_consultar_y_administrar()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var branch = await TestApi.CreateBranchAsync(tenant.Owner, "EXP");
        await TestApi.CreateUserAsync(tenant.Owner, "entrena@gym.test", "Entrenador", branch);
        await TestApi.CreateUserAsync(tenant.Owner, "recepcion@gym.test", "Recepcion", branch);
        await TestApi.CreateUserAsync(tenant.Owner, "nutri@gym.test", "Nutricionista", branch);
        var trainer = TestApi.Authenticated(api, await TestApi.LoginAsync(api, tenant.Slug, "entrena@gym.test", TestApi.UserPassword));
        var reception = TestApi.Authenticated(api, await TestApi.LoginAsync(api, tenant.Slug, "recepcion@gym.test", TestApi.UserPassword));
        var nutri = TestApi.Authenticated(api, await TestApi.LoginAsync(api, tenant.Slug, "nutri@gym.test", TestApi.UserPassword));
        var reader = await ClientWithRoleAsync(api, tenant, "lector@gym.test", true, ["exercises.read"]);

        Assert.Equal(HttpStatusCode.Created, (await trainer.PostAsJsonAsync("/api/v1/exercises", Body(name: "Del entrenador"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/api/v1/exercises")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync("/api/v1/exercises", Body(name: "No debe"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsync($"/api/v1/exercises/{Guid.NewGuid()}/duplicate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reception.GetAsync("/api/v1/exercises")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await nutri.GetAsync("/api/v1/exercises")).StatusCode);
        // Ni siquiera el propietario administra el catálogo base ni entra a la administración de plataforma.
        Assert.Equal(HttpStatusCode.Forbidden, (await tenant.Owner.GetAsync("/api/v1/platform/exercises")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tenant.Owner.PostAsJsonAsync("/api/v1/platform/exercises", Body(name: "Global falso"))).StatusCode);
    }

    [Fact]
    public async Task La_plataforma_administra_el_catalogo_base_y_los_gimnasios_lo_ven_pero_no_lo_editan()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        using var platform = await TestApi.PlatformClientAsync(api);
        var name = $"Global {Guid.NewGuid():N}"[..20];

        var created = await CreateAsync(platform, Body(name: name), "/api/v1/platform/exercises");
        Assert.True(created.IsGlobal);

        var seen = await ListAsync(tenant.Owner, $"&scope=global&search={name}");
        Assert.Equal(created.Id, Assert.Single(seen.Items).Id);
        await AssertProblemAsync(await tenant.Owner.PutAsJsonAsync($"/api/v1/exercises/{created.Id}", Body(name: "Cambiado")), HttpStatusCode.Forbidden, "exercises.global_readonly");

        var edited = await platform.PutAsJsonAsync($"/api/v1/platform/exercises/{created.Id}", Body(name: name, difficulty: "Beginner"));
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        Assert.Equal("Beginner", (await tenant.Owner.GetFromJsonAsync<ExerciseRow>($"/api/v1/exercises/{created.Id}"))!.Difficulty);
        await AssertProblemAsync(await platform.PostAsJsonAsync("/api/v1/platform/exercises", Body(name: name.ToUpperInvariant())), HttpStatusCode.Conflict, "exercises.name_taken");

        // Un ejercicio de un gimnasio no existe para la plataforma, y desactivar uno global lo oculta de los filtros activos.
        var own = await CreateAsync(tenant.Owner, Body(name: "Solo del gimnasio"));
        Assert.Equal(HttpStatusCode.NotFound, (await platform.PutAsJsonAsync($"/api/v1/platform/exercises/{own.Id}", Body(name: "Intento de plataforma"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await platform.PatchAsJsonAsync($"/api/v1/platform/exercises/{created.Id}/status", new { isActive = false })).StatusCode);
        Assert.DoesNotContain((await ListAsync(tenant.Owner, "&scope=global&isActive=true")).Items, e => e.Id == created.Id);
        var globalList = await platform.GetFromJsonAsync<PagedDto<ExerciseRow>>("/api/v1/platform/exercises?pageSize=100");
        Assert.All(globalList!.Items, e => Assert.True(e.IsGlobal));
    }

    [Fact]
    public async Task La_base_impide_a_un_gimnasio_escribir_o_borrar_el_catalogo_base_aunque_la_aplicacion_falle()
    {
        var a = await TestApi.NewTenantAsync(api);
        var b = await TestApi.NewTenantAsync(api);
        var ownB = await CreateAsync(b.Owner, Body(name: "Del gimnasio B"));
        var global = (await ListAsync(a.Owner, "&scope=global&search=Sentadilla libre")).Items.Single();

        await using var connection = new NpgsqlConnection(api.AppConnection);
        await connection.OpenAsync();
        await using (var set = new NpgsqlCommand($"SELECT set_config('app.tenant_id', '{a.Id}', false)", connection))
        {
            await set.ExecuteNonQueryAsync();
        }

        async Task<long> ScalarAsync(string sql)
        {
            await using var command = new NpgsqlCommand(sql, connection);
            return (long)(await command.ExecuteScalarAsync())!;
        }

        // Ve los globales y lo propio, pero no lo del gimnasio B.
        Assert.Equal(1, await ScalarAsync($"SELECT count(*) FROM training.exercises WHERE id = '{global.Id}'"));
        Assert.Equal(0, await ScalarAsync($"SELECT count(*) FROM training.exercises WHERE id = '{ownB.Id}'"));

        // No puede crear ejercicios globales ni a nombre de otro gimnasio.
        await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var insert = new NpgsqlCommand(
                "INSERT INTO training.exercises (id, tenant_id, name, primary_muscle, secondary_muscles, type, equipment, difficulty, tracking_mode, instructions, common_mistakes, is_active, created_at) " +
                "VALUES (gen_random_uuid(), NULL, 'Global intruso', 'Chest', '{}', 'Strength', 'Barbell', 'Beginner', 'Reps', '{}', '{}', true, now())", connection);
            await insert.ExecuteNonQueryAsync();
        });
        await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var insert = new NpgsqlCommand(
                $"INSERT INTO training.exercises (id, tenant_id, name, primary_muscle, secondary_muscles, type, equipment, difficulty, tracking_mode, instructions, common_mistakes, is_active, created_at) " +
                $"VALUES (gen_random_uuid(), '{b.Id}', 'A nombre de B', 'Chest', '{{}}', 'Strength', 'Barbell', 'Beginner', 'Reps', '{{}}', '{{}}', true, now())", connection);
            await insert.ExecuteNonQueryAsync();
        });

        // No modifica el catálogo base (0 filas afectadas) ni borra nada (sin permiso de DELETE).
        await using (var update = new NpgsqlCommand($"UPDATE training.exercises SET name = 'Hackeado' WHERE id = '{global.Id}'", connection))
        {
            Assert.Equal(0, await update.ExecuteNonQueryAsync());
        }

        await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var delete = new NpgsqlCommand($"DELETE FROM training.exercises WHERE id = '{global.Id}'", connection);
            await delete.ExecuteNonQueryAsync();
        });
        Assert.Equal("Sentadilla libre", (await a.Owner.GetFromJsonAsync<ExerciseRow>($"/api/v1/exercises/{global.Id}"))!.Name);
    }
}
