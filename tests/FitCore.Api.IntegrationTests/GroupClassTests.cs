using System.Net;
using System.Net.Http.Json;
using FitCore.Api.IntegrationTests.Infrastructure;
using Npgsql;

namespace FitCore.Api.IntegrationTests;

[Collection(ApiCollection3.Name)]
public class GroupClassTests(ApiFixture api)
{
    private sealed record ClassRow(
        Guid Id, string Name, string? Description, string Activity, Guid InstructorId, string InstructorName, Guid BranchId, string BranchName,
        int DurationMinutes, int Capacity, string Level, string? Requirements, int MinBookingNoticeMinutes, int MaxBookingAdvanceDays,
        int CancellationDeadlineMinutes, bool WaitlistEnabled, int WaitlistCapacity, int OfferWindowMinutes, bool RequireActiveMembership,
        bool ApplyPaymentPolicy, int? MinAge, bool IsActive);

    private sealed record TrainerRow(Guid Id, Guid UserId, string FullName);

    private sealed record Scenario(TestTenant Tenant, Guid Branch, Guid OtherBranch, TrainerRow Instructor);

    private async Task<TrainerRow> NewTrainerAsync(TestTenant tenant, string email, params Guid[] branches)
    {
        var user = await TestApi.CreateUserAsync(tenant.Owner, email, "Entrenador", branches);
        var response = await tenant.Owner.PostAsJsonAsync("/api/v1/trainers", new { userId = user.Id, specialties = new[] { "Yoga" } });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TrainerRow>())!;
    }

    private async Task<Scenario> NewScenarioAsync()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var branch = await TestApi.CreateBranchAsync(tenant.Owner, "UIO");
        var other = await TestApi.CreateBranchAsync(tenant.Owner, "GYE");
        var instructor = await NewTrainerAsync(tenant, "instructora@gym.test", branch);
        return new Scenario(tenant, branch, other, instructor);
    }

    private static object Body(
        Guid? branch,
        Guid instructor,
        string name = "Yoga matutino",
        int duration = 60,
        int capacity = 20,
        int notice = 30,
        int advanceDays = 14,
        int cancellation = 120,
        bool waitlist = true,
        int waitlistCapacity = 5,
        bool membership = true,
        bool paymentPolicy = true,
        int? minAge = null) => new
        {
            branchId = branch,
            name,
            description = "Clase para empezar el día",
            activity = "Yoga",
            instructorId = instructor,
            durationMinutes = duration,
            capacity,
            level = "Beginner",
            requirements = "Toalla y botella de agua",
            minBookingNoticeMinutes = notice,
            maxBookingAdvanceDays = advanceDays,
            cancellationDeadlineMinutes = cancellation,
            waitlistEnabled = waitlist,
            waitlistCapacity = waitlist ? waitlistCapacity : 0,
            offerWindowMinutes = 30,
            requireActiveMembership = membership,
            applyPaymentPolicy = paymentPolicy,
            minAge
        };

    private static async Task<ClassRow> CreateOkAsync(HttpClient client, object body)
    {
        var response = await client.PostAsJsonAsync("/api/v1/group-classes", body);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"Respondió {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<ClassRow>())!;
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, await TestApi.ProblemCodeAsync(response));
    }

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

    [Fact]
    public async Task Crear_consultar_y_actualizar_una_clase_normaliza_los_datos_y_muestra_instructor_y_sucursal()
    {
        var s = await NewScenarioAsync();

        var created = await CreateOkAsync(s.Tenant.Owner, Body(s.Branch, s.Instructor.Id, name: "  Yoga matutino  "));

        Assert.Equal("Yoga matutino", created.Name);
        Assert.Equal("Yoga", created.Activity);
        Assert.Equal(s.Instructor.FullName, created.InstructorName);
        Assert.Equal("Sucursal UIO", created.BranchName);
        Assert.Equal(20, created.Capacity);
        Assert.Equal(60, created.DurationMinutes);
        Assert.True(created.IsActive);

        var read = (await s.Tenant.Owner.GetFromJsonAsync<ClassRow>($"/api/v1/group-classes/{created.Id}"))!;
        Assert.Equal(created, read);

        var updated = await s.Tenant.Owner.PutAsJsonAsync($"/api/v1/group-classes/{created.Id}", Body(s.Branch, s.Instructor.Id, name: "Yoga restaurativo", capacity: 12, waitlist: false, minAge: 16));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var after = (await updated.Content.ReadFromJsonAsync<ClassRow>())!;
        Assert.Equal("Yoga restaurativo", after.Name);
        Assert.Equal(12, after.Capacity);
        Assert.False(after.WaitlistEnabled);
        Assert.Equal(0, after.WaitlistCapacity);
        Assert.Equal(16, after.MinAge);
        Assert.Equal(created.Id, after.Id);
    }

    [Fact]
    public async Task El_listado_filtra_por_actividad_estado_instructor_y_texto_y_pagina()
    {
        var s = await NewScenarioAsync();
        var second = await NewTrainerAsync(s.Tenant, "segundo@gym.test", s.Branch);
        await CreateOkAsync(s.Tenant.Owner, Body(s.Branch, s.Instructor.Id, name: "Yoga A"));
        var spinning = await CreateOkAsync(s.Tenant.Owner, Body(s.Branch, second.Id, name: "Spinning B"));
        await CreateOkAsync(s.Tenant.Owner, Body(s.Branch, second.Id, name: "Yoga C"));
        Assert.Equal(HttpStatusCode.OK, (await s.Tenant.Owner.PatchAsJsonAsync($"/api/v1/group-classes/{spinning.Id}/status", new { isActive = false })).StatusCode);

        var all = (await s.Tenant.Owner.GetFromJsonAsync<PagedDto<ClassRow>>("/api/v1/group-classes"))!;
        Assert.Equal(3, all.TotalCount);
        Assert.Equal(["Spinning B", "Yoga A", "Yoga C"], all.Items.Select(c => c.Name));

        Assert.Equal(2, (await s.Tenant.Owner.GetFromJsonAsync<PagedDto<ClassRow>>("/api/v1/group-classes?isActive=true"))!.TotalCount);
        Assert.Equal(1, (await s.Tenant.Owner.GetFromJsonAsync<PagedDto<ClassRow>>("/api/v1/group-classes?isActive=false"))!.TotalCount);
        Assert.Equal(2, (await s.Tenant.Owner.GetFromJsonAsync<PagedDto<ClassRow>>($"/api/v1/group-classes?instructorId={second.Id}"))!.TotalCount);
        Assert.Equal(2, (await s.Tenant.Owner.GetFromJsonAsync<PagedDto<ClassRow>>("/api/v1/group-classes?search=yoga"))!.TotalCount);
        Assert.Equal(0, (await s.Tenant.Owner.GetFromJsonAsync<PagedDto<ClassRow>>($"/api/v1/group-classes?branchId={s.OtherBranch}"))!.TotalCount);

        var firstPage = (await s.Tenant.Owner.GetFromJsonAsync<PagedDto<ClassRow>>("/api/v1/group-classes?pageSize=2"))!;
        Assert.Equal(2, firstPage.Items.Length);
        Assert.Equal(3, firstPage.TotalCount);
    }

    [Fact]
    public async Task Desactivar_y_reactivar_conserva_la_clase_y_no_existe_forma_de_borrarla()
    {
        var s = await NewScenarioAsync();
        var created = await CreateOkAsync(s.Tenant.Owner, Body(s.Branch, s.Instructor.Id));

        var off = await s.Tenant.Owner.PatchAsJsonAsync($"/api/v1/group-classes/{created.Id}/status", new { isActive = false });
        Assert.False((await off.Content.ReadFromJsonAsync<ClassRow>())!.IsActive);
        Assert.False((await s.Tenant.Owner.GetFromJsonAsync<ClassRow>($"/api/v1/group-classes/{created.Id}"))!.IsActive);

        var on = await s.Tenant.Owner.PatchAsJsonAsync($"/api/v1/group-classes/{created.Id}/status", new { isActive = true });
        Assert.True((await on.Content.ReadFromJsonAsync<ClassRow>())!.IsActive);

        // No hay endpoint de borrado.
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await s.Tenant.Owner.DeleteAsync($"/api/v1/group-classes/{created.Id}")).StatusCode);

        // Y aunque alguien llegara a la base con el rol de la aplicación, DELETE está revocado (trazabilidad de sesiones y reservas).
        await using var connection = await OpenAsync(asApp: true, s.Tenant.Id);
        await using var delete = new NpgsqlCommand($"DELETE FROM classes.group_classes WHERE id = '{created.Id}'", connection);
        var error = await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(4, 20)]
    [InlineData(481, 20)]
    [InlineData(60, 0)]
    [InlineData(60, 501)]
    public async Task Duracion_y_capacidad_fuera_de_rango_se_rechazan(int duration, int capacity)
    {
        var s = await NewScenarioAsync();

        var response = await s.Tenant.Owner.PostAsJsonAsync("/api/v1/group-classes", Body(s.Branch, s.Instructor.Id, duration: duration, capacity: capacity));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Empty((await s.Tenant.Owner.GetFromJsonAsync<PagedDto<ClassRow>>("/api/v1/group-classes"))!.Items);
    }

    [Fact]
    public async Task Las_configuraciones_incoherentes_se_rechazan_con_su_codigo()
    {
        var s = await NewScenarioAsync();
        var owner = s.Tenant.Owner;

        // La anticipación mínima (en minutos) debe quedar por debajo de la máxima (1 día = 1440 minutos).
        await AssertProblemAsync(await owner.PostAsJsonAsync("/api/v1/group-classes", Body(s.Branch, s.Instructor.Id, notice: 1440, advanceDays: 1)),
            HttpStatusCode.UnprocessableEntity, "classes.booking_window_incoherent");

        // Lista de espera activada sin cupo para esperar.
        await AssertProblemAsync(await owner.PostAsJsonAsync("/api/v1/group-classes", Body(s.Branch, s.Instructor.Id, waitlist: true, waitlistCapacity: 0)),
            HttpStatusCode.UnprocessableEntity, "classes.waitlist_capacity_required");

        // Capacidad de espera sin lista de espera.
        var withoutWaitlist = ToDictionary(Body(s.Branch, s.Instructor.Id, waitlist: false));
        withoutWaitlist["waitlistCapacity"] = 3;
        await AssertProblemAsync(await owner.PostAsJsonAsync("/api/v1/group-classes", withoutWaitlist),
            HttpStatusCode.UnprocessableEntity, "classes.waitlist_disabled_with_capacity");

        // La política de pagos solo tiene sentido si se exige membresía.
        await AssertProblemAsync(await owner.PostAsJsonAsync("/api/v1/group-classes", Body(s.Branch, s.Instructor.Id, membership: false, paymentPolicy: true)),
            HttpStatusCode.UnprocessableEntity, "classes.payment_policy_needs_membership");

        // Sin sucursal.
        await AssertProblemAsync(await owner.PostAsJsonAsync("/api/v1/group-classes", Body(null, s.Instructor.Id)),
            HttpStatusCode.UnprocessableEntity, "classes.branch_required");

        Assert.Empty((await owner.GetFromJsonAsync<PagedDto<ClassRow>>("/api/v1/group-classes"))!.Items);
    }

    private static Dictionary<string, object?> ToDictionary(object value) =>
        value.GetType().GetProperties().ToDictionary(p => p.Name[..1].ToLowerInvariant() + p.Name[1..], p => p.GetValue(value));

    [Fact]
    public async Task El_instructor_debe_ser_un_entrenador_activo_de_la_sucursal_y_del_gimnasio()
    {
        var s = await NewScenarioAsync();
        var elsewhere = await NewTrainerAsync(s.Tenant, "gye@gym.test", s.OtherBranch);
        var inactive = await NewTrainerAsync(s.Tenant, "inactivo@gym.test", s.Branch);
        Assert.Equal(HttpStatusCode.OK, (await s.Tenant.Owner.PatchAsJsonAsync($"/api/v1/trainers/{inactive.Id}/status", new { isActive = false })).StatusCode);
        var foreign = await NewForeignTrainerAsync();

        await AssertProblemAsync(await s.Tenant.Owner.PostAsJsonAsync("/api/v1/group-classes", Body(s.Branch, Guid.NewGuid())), HttpStatusCode.UnprocessableEntity, "classes.instructor_invalid");
        await AssertProblemAsync(await s.Tenant.Owner.PostAsJsonAsync("/api/v1/group-classes", Body(s.Branch, inactive.Id)), HttpStatusCode.UnprocessableEntity, "classes.instructor_invalid");
        await AssertProblemAsync(await s.Tenant.Owner.PostAsJsonAsync("/api/v1/group-classes", Body(s.Branch, foreign.Id)), HttpStatusCode.UnprocessableEntity, "classes.instructor_invalid");
        await AssertProblemAsync(await s.Tenant.Owner.PostAsJsonAsync("/api/v1/group-classes", Body(s.Branch, elsewhere.Id)), HttpStatusCode.UnprocessableEntity, "classes.instructor_not_in_branch");

        // El mismo instructor sí sirve en su sucursal, y al editar se vuelve a validar.
        var created = await CreateOkAsync(s.Tenant.Owner, Body(s.Branch, s.Instructor.Id));
        await AssertProblemAsync(await s.Tenant.Owner.PutAsJsonAsync($"/api/v1/group-classes/{created.Id}", Body(s.Branch, elsewhere.Id)), HttpStatusCode.UnprocessableEntity, "classes.instructor_not_in_branch");
    }

    private async Task<TrainerRow> NewForeignTrainerAsync()
    {
        var other = await TestApi.NewTenantAsync(api);
        var branch = await TestApi.CreateBranchAsync(other.Owner, "XYZ");
        return await NewTrainerAsync(other, "ajeno@gym.test", branch);
    }

    [Fact]
    public async Task El_nombre_es_unico_por_sucursal_sin_distinguir_mayusculas_y_la_sucursal_no_cambia()
    {
        var s = await NewScenarioAsync();
        var gye = await NewTrainerAsync(s.Tenant, "gye2@gym.test", s.OtherBranch);
        var created = await CreateOkAsync(s.Tenant.Owner, Body(s.Branch, s.Instructor.Id, name: "Zumba"));

        await AssertProblemAsync(await s.Tenant.Owner.PostAsJsonAsync("/api/v1/group-classes", Body(s.Branch, s.Instructor.Id, name: " ZUMBA ")), HttpStatusCode.Conflict, "classes.name_taken");

        // El mismo nombre en otra sucursal es válido.
        var other = await CreateOkAsync(s.Tenant.Owner, Body(s.OtherBranch, gye.Id, name: "Zumba"));
        Assert.Equal(s.OtherBranch, other.BranchId);

        // Editar sin cambiar el nombre no choca consigo misma; renombrarla a una existente sí.
        Assert.Equal(HttpStatusCode.OK, (await s.Tenant.Owner.PutAsJsonAsync($"/api/v1/group-classes/{created.Id}", Body(s.Branch, s.Instructor.Id, name: "Zumba", capacity: 15))).StatusCode);
        var pilates = await CreateOkAsync(s.Tenant.Owner, Body(s.Branch, s.Instructor.Id, name: "Pilates"));
        await AssertProblemAsync(await s.Tenant.Owner.PutAsJsonAsync($"/api/v1/group-classes/{pilates.Id}", Body(s.Branch, s.Instructor.Id, name: "zumba")), HttpStatusCode.Conflict, "classes.name_taken");

        // La sucursal de una clase no cambia.
        await AssertProblemAsync(await s.Tenant.Owner.PutAsJsonAsync($"/api/v1/group-classes/{created.Id}", Body(s.OtherBranch, s.Instructor.Id, name: "Zumba")), HttpStatusCode.UnprocessableEntity, "classes.branch_immutable");
    }

    [Fact]
    public async Task Cada_rol_ve_y_administra_solo_lo_que_su_permiso_y_sus_sucursales_permiten()
    {
        var s = await NewScenarioAsync();
        var inUio = await CreateOkAsync(s.Tenant.Owner, Body(s.Branch, s.Instructor.Id, name: "Yoga UIO"));
        var gyeTrainer = await NewTrainerAsync(s.Tenant, "gye3@gym.test", s.OtherBranch);
        var inGye = await CreateOkAsync(s.Tenant.Owner, Body(s.OtherBranch, gyeTrainer.Id, name: "Yoga GYE"));

        async Task<HttpClient> ClientAsync(string email, string role, Guid branch)
        {
            await TestApi.CreateUserAsync(s.Tenant.Owner, email, role, branch);
            return TestApi.Authenticated(api, await TestApi.LoginAsync(api, s.Tenant.Slug, email, TestApi.UserPassword));
        }

        var admin = await ClientAsync("admin.uio@gym.test", "AdministradorSucursal", s.Branch);
        var reception = await ClientAsync("recep.uio@gym.test", "Recepcion", s.Branch);
        var cashier = await ClientAsync("cajero.uio@gym.test", "Cajero", s.Branch);

        // El administrador de UIO administra UIO y no existe nada de GYE para él.
        var visible = (await admin.GetFromJsonAsync<PagedDto<ClassRow>>("/api/v1/group-classes"))!;
        Assert.Equal([inUio.Id], visible.Items.Select(c => c.Id));
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/v1/group-classes/{inUio.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/v1/group-classes/{inGye.Id}")).StatusCode);
        await AssertProblemAsync(await admin.GetAsync($"/api/v1/group-classes?branchId={s.OtherBranch}"), HttpStatusCode.Forbidden, "branches.not_allowed");
        await AssertProblemAsync(await admin.PostAsJsonAsync("/api/v1/group-classes", Body(s.OtherBranch, gyeTrainer.Id, name: "Intruso")), HttpStatusCode.Forbidden, "branches.not_allowed");
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsJsonAsync($"/api/v1/group-classes/{inGye.Id}", Body(s.OtherBranch, gyeTrainer.Id, name: "Cambiada"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PatchAsJsonAsync($"/api/v1/group-classes/{inGye.Id}/status", new { isActive = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/v1/group-classes", Body(s.Branch, s.Instructor.Id, name: "Nueva UIO"))).StatusCode);

        // Recepción consulta pero no administra; caja no tiene ni lectura.
        Assert.Equal(HttpStatusCode.OK, (await reception.GetAsync("/api/v1/group-classes")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reception.PostAsJsonAsync("/api/v1/group-classes", Body(s.Branch, s.Instructor.Id, name: "No"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reception.PatchAsJsonAsync($"/api/v1/group-classes/{inUio.Id}/status", new { isActive = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync("/api/v1/group-classes")).StatusCode);

        // El entrenador del gimnasio consulta, sin administrar.
        var trainerClient = TestApi.Authenticated(api, await TestApi.LoginAsync(api, s.Tenant.Slug, "instructora@gym.test", TestApi.UserPassword));
        Assert.Equal(HttpStatusCode.OK, (await trainerClient.GetAsync("/api/v1/group-classes")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await trainerClient.PutAsJsonAsync($"/api/v1/group-classes/{inUio.Id}", Body(s.Branch, s.Instructor.Id))).StatusCode);
    }

    [Fact]
    public async Task Un_gimnasio_no_ve_ni_modifica_las_clases_de_otro_y_la_base_lo_garantiza()
    {
        var a = await NewScenarioAsync();
        var b = await NewScenarioAsync();
        var created = await CreateOkAsync(a.Tenant.Owner, Body(a.Branch, a.Instructor.Id));

        Assert.Equal(HttpStatusCode.NotFound, (await b.Tenant.Owner.GetAsync($"/api/v1/group-classes/{created.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Tenant.Owner.PutAsJsonAsync($"/api/v1/group-classes/{created.Id}", Body(b.Branch, b.Instructor.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Tenant.Owner.PatchAsJsonAsync($"/api/v1/group-classes/{created.Id}/status", new { isActive = false })).StatusCode);
        Assert.Empty((await b.Tenant.Owner.GetFromJsonAsync<PagedDto<ClassRow>>("/api/v1/group-classes"))!.Items);

        // Con el rol de la aplicación y el gimnasio B fijado, la fila de A no existe; con A fijado, sí (la prueba no pasa en vacío).
        await using (var asB = await OpenAsync(asApp: true, b.Tenant.Id))
        {
            await using var count = new NpgsqlCommand("SELECT count(*) FROM classes.group_classes", asB);
            Assert.Equal(0L, await count.ExecuteScalarAsync());
        }

        await using (var asA = await OpenAsync(asApp: true, a.Tenant.Id))
        {
            await using var count = new NpgsqlCommand("SELECT count(*) FROM classes.group_classes", asA);
            Assert.Equal(1L, await count.ExecuteScalarAsync());
        }

        // Y una clase de A con el instructor de B la rechaza la clave foránea compuesta, aunque se salte la aplicación.
        await using var admin = await OpenAsync(asApp: false, null);
        await using var insert = new NpgsqlCommand($"""
            INSERT INTO classes.group_classes (id, tenant_id, branch_id, name, activity, instructor_id, duration_minutes, capacity, level,
              min_booking_notice_minutes, max_booking_advance_days, cancellation_deadline_minutes, waitlist_enabled, waitlist_capacity,
              offer_window_minutes, require_active_membership, apply_payment_policy, is_active, created_at)
            VALUES (gen_random_uuid(), '{a.Tenant.Id}', '{a.Branch}', 'Cruzada', 'Yoga', '{b.Instructor.Id}', 60, 10, 'AllLevels',
              0, 14, 0, false, 0, 30, true, true, true, now())
            """, admin);
        var fk = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, fk.SqlState);
    }

    [Fact]
    public async Task La_base_rechaza_datos_incoherentes_y_el_cambio_de_sucursal_aunque_se_salte_la_aplicacion()
    {
        var s = await NewScenarioAsync();
        var created = await CreateOkAsync(s.Tenant.Owner, Body(s.Branch, s.Instructor.Id));
        await using var admin = await OpenAsync(asApp: false, s.Tenant.Id);

        async Task<string> ErrorAsync(string sql)
        {
            await using var command = new NpgsqlCommand(sql, admin);
            return (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).SqlState;
        }

        Assert.Equal(PostgresErrorCodes.CheckViolation, await ErrorAsync($"UPDATE classes.group_classes SET capacity = 0 WHERE id = '{created.Id}'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await ErrorAsync($"UPDATE classes.group_classes SET duration_minutes = 0 WHERE id = '{created.Id}'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await ErrorAsync($"UPDATE classes.group_classes SET min_booking_notice_minutes = 100000 WHERE id = '{created.Id}'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await ErrorAsync($"UPDATE classes.group_classes SET waitlist_enabled = true, waitlist_capacity = 0 WHERE id = '{created.Id}'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await ErrorAsync($"UPDATE classes.group_classes SET activity = 'Otra' WHERE id = '{created.Id}'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await ErrorAsync($"UPDATE classes.group_classes SET branch_id = '{s.OtherBranch}' WHERE id = '{created.Id}'"));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, await ErrorAsync($"""
            INSERT INTO classes.group_classes (id, tenant_id, branch_id, name, activity, instructor_id, duration_minutes, capacity, level,
              min_booking_notice_minutes, max_booking_advance_days, cancellation_deadline_minutes, waitlist_enabled, waitlist_capacity,
              offer_window_minutes, require_active_membership, apply_payment_policy, is_active, created_at)
            VALUES (gen_random_uuid(), '{s.Tenant.Id}', '{s.Branch}', ' yoga MATUTINO ', 'Yoga', '{s.Instructor.Id}', 60, 10, 'AllLevels',
              0, 14, 0, false, 0, 30, true, true, true, now())
            """));
    }

    [Fact]
    public async Task Los_datos_persisten_en_postgresql_y_los_cambios_quedan_auditados_sin_exponer_mas_de_lo_necesario()
    {
        var s = await NewScenarioAsync();
        var created = await CreateOkAsync(s.Tenant.Owner, Body(s.Branch, s.Instructor.Id, name: "Funcional", minAge: 14));
        await s.Tenant.Owner.PatchAsJsonAsync($"/api/v1/group-classes/{created.Id}/status", new { isActive = false });

        await using var connection = await OpenAsync(asApp: true, s.Tenant.Id);
        await using var command = new NpgsqlCommand(
            "SELECT name, activity, level, capacity, duration_minutes, min_age, waitlist_enabled, is_active, updated_at IS NOT NULL FROM classes.group_classes WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", created.Id);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("Funcional", reader.GetString(0));
        Assert.Equal("Yoga", reader.GetString(1));
        Assert.Equal("Beginner", reader.GetString(2));
        Assert.Equal(20, reader.GetInt32(3));
        Assert.Equal(60, reader.GetInt32(4));
        Assert.Equal(14, reader.GetInt32(5));
        Assert.True(reader.GetBoolean(6));
        Assert.False(reader.GetBoolean(7));
        Assert.True(reader.GetBoolean(8));
        await reader.DisposeAsync();

        var audit = (await s.Tenant.Owner.GetFromJsonAsync<PagedDto<AuditDto>>($"/api/v1/audit-logs?entity=GroupClass&entityId={created.Id}"))!;
        Assert.True(audit.TotalCount >= 2, $"Auditoría con {audit.TotalCount} registros");
    }
}
