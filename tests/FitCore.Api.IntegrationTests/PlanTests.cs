using System.Net;
using System.Net.Http.Json;
using FitCore.Api.IntegrationTests.Infrastructure;
using Npgsql;
using static FitCore.Api.IntegrationTests.Infrastructure.CommercialTestApi;

namespace FitCore.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
public class PlanTests(ApiFixture api)
{
    [Fact]
    public async Task Crear_un_plan_valido_desglosa_el_IVA_incluido_en_el_precio_y_registra_quien_lo_creo()
    {
        var tenant = await TestApi.NewTenantAsync(api);

        var plan = await CreatePlanAsync(tenant.Owner, "Mensual Full", price: 11.50m);

        Assert.Equal("USD", plan.Currency);
        Assert.Equal(11.50m, plan.Price);
        Assert.Equal(15m, plan.TaxPercent);
        Assert.Equal(10.00m, plan.Subtotal);
        Assert.Equal(1.50m, plan.TaxAmount);
        Assert.Equal(plan.Price, plan.Subtotal + plan.TaxAmount);
        Assert.True(plan.IsActive);
        Assert.Empty(plan.BranchIds);
        Assert.NotNull(plan.CreatedBy);
        Assert.Equal(plan.Id, (await tenant.Owner.GetFromJsonAsync<PlanDto>($"/api/v1/membership-plans/{plan.Id}"))!.Id);
    }

    [Theory]
    [InlineData("Day", 1)]
    [InlineData("Week", 1)]
    [InlineData("Month", 1)]
    [InlineData("Month", 3)]
    [InlineData("Month", 6)]
    [InlineData("Year", 1)]
    public async Task Se_aceptan_planes_diarios_semanales_mensuales_trimestrales_semestrales_y_anuales(string unit, int value)
    {
        var tenant = await TestApi.NewTenantAsync(api);

        var plan = await CreatePlanAsync(tenant.Owner, $"Plan {unit} {value}", durationUnit: unit, durationValue: value);

        Assert.Equal(unit, plan.DurationUnit);
        Assert.Equal(value, plan.DurationValue);
    }

    [Fact]
    public async Task Validacion_rechaza_precios_no_positivos_duraciones_invalidas_y_vigencias_al_reves()
    {
        var tenant = await TestApi.NewTenantAsync(api);

        async Task<HttpResponseMessage> PostAsync(object body) => await tenant.Owner.PostAsJsonAsync("/api/v1/membership-plans", body);

        foreach (var price in new[] { 0m, -5m, 100000.01m, 10.123m })
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostAsync(PlanBody("P", price))).StatusCode);
        }

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostAsync(PlanBody("P", durationValue: 0))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostAsync(PlanBody("P", durationValue: 61, durationUnit: "Month"))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostAsync(PlanBody("", 30m))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostAsync(PlanBody("P", validFrom: "2030-02-01", validUntil: "2030-01-01"))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostAsync(PlanBody("P", allowFreeze: true, maxFreezeDays: 0))).StatusCode);

        var none = await tenant.Owner.GetFromJsonAsync<PagedDto<PlanDto>>("/api/v1/membership-plans");
        Assert.Empty(none!.Items);
    }

    [Fact]
    public async Task El_nombre_no_se_repite_ignorando_mayusculas_pero_si_puede_repetirse_en_otro_gimnasio()
    {
        var a = await TestApi.NewTenantAsync(api);
        var b = await TestApi.NewTenantAsync(api);
        await CreatePlanAsync(a.Owner, "Premium");

        var duplicate = await a.Owner.PostAsJsonAsync("/api/v1/membership-plans", PlanBody("PREMIUM"));
        var other = await b.Owner.PostAsJsonAsync("/api/v1/membership-plans", PlanBody("Premium"));

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("plans.name_taken", await TestApi.ProblemCodeAsync(duplicate));
        Assert.Equal(HttpStatusCode.Created, other.StatusCode);
    }

    [Fact]
    public async Task Editar_un_plan_actualiza_sus_datos_y_permite_desactivarlo()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var plan = await CreatePlanAsync(tenant.Owner, "Editable", 20m);

        var response = await tenant.Owner.PutAsJsonAsync($"/api/v1/membership-plans/{plan.Id}",
            PlanBody("Editable 2", 25m, durationValue: 3, allowFreeze: true, maxFreezeDays: 15, isActive: false, taxKind: "Zero"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = (await response.Content.ReadFromJsonAsync<PlanDto>())!;
        Assert.Equal("Editable 2", updated.Name);
        Assert.Equal(25m, updated.Price);
        Assert.Equal(3, updated.DurationValue);
        Assert.False(updated.IsActive);
        Assert.True(updated.AllowFreeze);
        Assert.Equal(15, updated.MaxFreezeDays);
        Assert.Equal("Zero", updated.TaxKind);
        Assert.Equal(0m, updated.TaxPercent);
        Assert.Equal(25m, updated.Subtotal);
        Assert.NotNull(updated.UpdatedBy);

        var inactive = await tenant.Owner.GetFromJsonAsync<PagedDto<PlanDto>>("/api/v1/membership-plans?isActive=false");
        Assert.Equal([plan.Id], inactive!.Items.Select(p => p.Id));
    }

    [Fact]
    public async Task Las_sucursales_del_plan_se_validan_y_se_pueden_cambiar()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var other = await TestApi.NewTenantAsync(api);
        var north = await TestApi.CreateBranchAsync(tenant.Owner, "PN");
        var south = await TestApi.CreateBranchAsync(tenant.Owner, "PS");
        var foreign = await TestApi.CreateBranchAsync(other.Owner, "PF");

        var plan = await CreatePlanAsync(tenant.Owner, "Por sucursal", branchIds: [north]);
        Assert.Equal([north], plan.BranchIds);

        var moved = await tenant.Owner.PutAsJsonAsync($"/api/v1/membership-plans/{plan.Id}", PlanBody("Por sucursal", branchIds: [south]));
        Assert.Equal([south], (await moved.Content.ReadFromJsonAsync<PlanDto>())!.BranchIds);

        var all = await tenant.Owner.PutAsJsonAsync($"/api/v1/membership-plans/{plan.Id}", PlanBody("Por sucursal", branchIds: []));
        Assert.Empty((await all.Content.ReadFromJsonAsync<PlanDto>())!.BranchIds);

        var foreignBranch = await tenant.Owner.PostAsJsonAsync("/api/v1/membership-plans", PlanBody("Ajena", branchIds: [foreign]));
        Assert.Equal(HttpStatusCode.NotFound, foreignBranch.StatusCode);
        Assert.Equal("branches.not_found", await TestApi.ProblemCodeAsync(foreignBranch));

        var duplicated = await tenant.Owner.PostAsJsonAsync("/api/v1/membership-plans", PlanBody("Repetida", branchIds: [north, north]));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, duplicated.StatusCode);
    }

    [Fact]
    public async Task Quien_solo_accede_a_una_sucursal_ve_planes_globales_y_de_su_sucursal_pero_solo_administra_los_suyos()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var north = await TestApi.CreateBranchAsync(tenant.Owner, "AN");
        var south = await TestApi.CreateBranchAsync(tenant.Owner, "AS");
        var global = await CreatePlanAsync(tenant.Owner, "Global");
        var northOnly = await CreatePlanAsync(tenant.Owner, "Solo norte", branchIds: [north]);
        var southOnly = await CreatePlanAsync(tenant.Owner, "Solo sur", branchIds: [south]);
        using var manager = await ClientWithRoleAsync(api, tenant, "gestor.norte@gym.test", false, ["plans.read", "plans.manage", "branches.read"], north);

        var list = await manager.GetFromJsonAsync<PagedDto<PlanDto>>("/api/v1/membership-plans?pageSize=100");
        Assert.Equivalent(new[] { global.Id, northOnly.Id }, list!.Items.Select(p => p.Id));
        Assert.Equal(HttpStatusCode.NotFound, (await manager.GetAsync($"/api/v1/membership-plans/{southOnly.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync($"/api/v1/membership-plans?branchId={south}")).StatusCode);

        // Un plan para todas las sucursales no es suyo para editar; el de su sucursal sí.
        var editGlobal = await manager.PutAsJsonAsync($"/api/v1/membership-plans/{global.Id}", PlanBody("Global", 99m));
        Assert.Equal(HttpStatusCode.Forbidden, editGlobal.StatusCode);
        Assert.Equal("plans.scope_not_allowed", await TestApi.ProblemCodeAsync(editGlobal));
        Assert.Equal(HttpStatusCode.OK, (await manager.PutAsJsonAsync($"/api/v1/membership-plans/{northOnly.Id}", PlanBody("Solo norte", 40m, branchIds: [north]))).StatusCode);

        // No puede ampliar su plan a otra sucursal ni crear uno sin indicar sucursales.
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PutAsJsonAsync($"/api/v1/membership-plans/{northOnly.Id}", PlanBody("Solo norte", branchIds: [north, south]))).StatusCode);
        var noBranches = await manager.PostAsJsonAsync("/api/v1/membership-plans", PlanBody("Sin sucursal"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noBranches.StatusCode);
        Assert.Equal("plans.branches_required", await TestApi.ProblemCodeAsync(noBranches));
        Assert.Equal(HttpStatusCode.Created, (await manager.PostAsJsonAsync("/api/v1/membership-plans", PlanBody("Nuevo norte", branchIds: [north]))).StatusCode);
    }

    [Fact]
    public async Task Los_permisos_del_rol_se_aplican_a_planes()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var branch = await TestApi.CreateBranchAsync(tenant.Owner, "PP");
        var plan = await CreatePlanAsync(tenant.Owner, "Visible");
        await TestApi.CreateUserAsync(tenant.Owner, "recepcion@gym.test", "Recepcion", branch);
        await TestApi.CreateUserAsync(tenant.Owner, "entrenador@gym.test", "Entrenador", branch);
        using var reception = TestApi.Authenticated(api, await TestApi.LoginAsync(api, tenant.Slug, "recepcion@gym.test", TestApi.UserPassword));
        using var trainer = TestApi.Authenticated(api, await TestApi.LoginAsync(api, tenant.Slug, "entrenador@gym.test", TestApi.UserPassword));

        Assert.Equal(HttpStatusCode.OK, (await reception.GetAsync($"/api/v1/membership-plans/{plan.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reception.PostAsJsonAsync("/api/v1/membership-plans", PlanBody("X", branchIds: [branch]))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reception.PutAsJsonAsync($"/api/v1/membership-plans/{plan.Id}", PlanBody("Visible"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await trainer.GetAsync("/api/v1/membership-plans")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().GetAsync("/api/v1/membership-plans")).StatusCode);
    }

    [Fact]
    public async Task Un_plan_de_otro_gimnasio_responde_404_y_no_aparece_en_listados()
    {
        var a = await TestApi.NewTenantAsync(api);
        var b = await TestApi.NewTenantAsync(api);
        var planB = await CreatePlanAsync(b.Owner, "De B");
        await CreatePlanAsync(a.Owner, "De A");

        Assert.Equal(HttpStatusCode.NotFound, (await a.Owner.GetAsync($"/api/v1/membership-plans/{planB.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.Owner.PutAsJsonAsync($"/api/v1/membership-plans/{planB.Id}", PlanBody("Hack"))).StatusCode);
        var list = await a.Owner.GetFromJsonAsync<PagedDto<PlanDto>>("/api/v1/membership-plans?pageSize=100");
        Assert.Equal(["De A"], list!.Items.Select(p => p.Name));
        Assert.Equal("De B", (await b.Owner.GetFromJsonAsync<PlanDto>($"/api/v1/membership-plans/{planB.Id}"))!.Name);
    }

    [Fact]
    public async Task Buscar_por_nombre_y_filtrar_por_sucursal()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var north = await TestApi.CreateBranchAsync(tenant.Owner, "FN");
        var south = await TestApi.CreateBranchAsync(tenant.Owner, "FS");
        var gold = await CreatePlanAsync(tenant.Owner, "Oro anual", branchIds: [north]);
        var silver = await CreatePlanAsync(tenant.Owner, "Plata semestral", branchIds: [south]);
        var global = await CreatePlanAsync(tenant.Owner, "Pase diario");

        async Task<Guid[]> ListAsync(string query) => [.. (await tenant.Owner.GetFromJsonAsync<PagedDto<PlanDto>>($"/api/v1/membership-plans?{query}"))!.Items.Select(p => p.Id)];

        Assert.Equal([gold.Id], await ListAsync("search=ORO"));
        Assert.Equal([silver.Id], await ListAsync("search=semes"));
        Assert.Empty(await ListAsync("search=%25"));
        Assert.Equivalent(new[] { gold.Id, global.Id }, await ListAsync($"branchId={north}"));
    }

    [Fact]
    public async Task Row_Level_Security_y_restricciones_protegen_los_planes_aunque_el_codigo_falle()
    {
        var a = await TestApi.NewTenantAsync(api);
        var b = await TestApi.NewTenantAsync(api);
        var branchB = await TestApi.CreateBranchAsync(b.Owner, "RLSB");
        var planA = await CreatePlanAsync(a.Owner, "Plan RLS A");
        var planB = await CreatePlanAsync(b.Owner, "Plan RLS B", branchIds: [branchB]);

        await using var connection = new NpgsqlConnection(api.AppConnection);
        await connection.OpenAsync();
        await Exec(connection, $"SELECT set_config('app.tenant_id', '{a.Id}', false)");

        Assert.Equal(1L, await Scalar(connection, "SELECT count(*) FROM members.membership_plans"));
        Assert.Equal(0L, await Scalar(connection, $"SELECT count(*) FROM members.membership_plans WHERE id = '{planB.Id}'"));
        Assert.Equal(0, await Exec(connection, $"UPDATE members.membership_plans SET price = 1 WHERE id = '{planB.Id}'"));

        // Un plan de A vendido en la sucursal de B: pasa RLS, pero la FK compuesta lo impide.
        var cross = await Assert.ThrowsAsync<PostgresException>(() => Exec(connection,
            $"INSERT INTO members.plan_branches (tenant_id, plan_id, branch_id) VALUES ('{a.Id}', '{planA.Id}', '{branchB}')"));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, cross.SqlState);

        // Las reglas también viven en la base: precio no positivo y nombre repetido sin distinguir mayúsculas.
        var badPrice = await Assert.ThrowsAsync<PostgresException>(() => Exec(connection,
            $"UPDATE members.membership_plans SET price = -1 WHERE id = '{planA.Id}'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, badPrice.SqlState);
        await CreatePlanAsync(a.Owner, "Otro plan");
        var sameName = await Assert.ThrowsAsync<PostgresException>(() => Exec(connection,
            $"UPDATE members.membership_plans SET name = upper('Plan RLS A') WHERE name = 'Otro plan'"));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, sameName.SqlState);
    }

    private static async Task<int> Exec(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> Scalar(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
