using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FitCore.Api.IntegrationTests.Infrastructure;
using Npgsql;
using static FitCore.Api.IntegrationTests.Infrastructure.CommercialTestApi;

namespace FitCore.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
public class MembershipTests(ApiFixture api)
{
    /// <summary>Fecha local de Ecuador continental: la de las sucursales de prueba (America/Guayaquil).</summary>
    private static DateOnly Today =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/Guayaquil")).DateTime);

    private sealed record Setup(TestTenant Tenant, Guid Branch, MemberDto Member, PlanDto Plan);

    private async Task<Setup> NewSetupAsync(decimal price = 34.50m, bool allowFreeze = false, int maxFreezeDays = 0, string code = "MEM")
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var branch = await TestApi.CreateBranchAsync(tenant.Owner, code);
        var member = await MemberTests.CreateMemberAsync(tenant.Owner, branch);
        var plan = await CreatePlanWithFreezeAsync(tenant.Owner, "Mensual", price, allowFreeze, maxFreezeDays);
        return new Setup(tenant, branch, member, plan);
    }

    private static async Task<PlanDto> CreatePlanWithFreezeAsync(HttpClient client, string name, decimal price, bool allowFreeze, int maxFreezeDays, Guid[]? branchIds = null)
    {
        var response = await client.PostAsJsonAsync("/api/v1/membership-plans", PlanBody(name, price, allowFreeze: allowFreeze, maxFreezeDays: maxFreezeDays, branchIds: branchIds));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PlanDto>())!;
    }

    private static Task<HttpResponseMessage> PostEnrollAsync(HttpClient client, Guid memberId, Guid planId, string? promotionCode = null, DateOnly? start = null, DateOnly? due = null, Guid? promotionId = null) =>
        client.PostAsJsonAsync("/api/v1/memberships", new { memberId, planId, startDate = start, promotionCode, promotionId, dueDate = due });

    private static async Task<MembershipDto> EnrollAsync(HttpClient client, Guid memberId, Guid planId, string? promotionCode = null, DateOnly? start = null)
    {
        var response = await PostEnrollAsync(client, memberId, planId, promotionCode, start);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<MembershipDto>())!;
    }

    private static async Task<MembershipDto> GetAsync(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<MembershipDto>($"/api/v1/memberships/{id}"))!;

    /// <summary>Atajo solo para fijar fechas o estados que el negocio no permite fabricar (por ejemplo, retroceder el calendario).</summary>
    private async Task SqlAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(api.AdminConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Activa la membresía como en producción: con un pago real del total de su obligación.</summary>
    private async Task<MembershipDto> ActivateAsync(HttpClient client, MembershipDto membership)
    {
        await PayAsync(client, membership.MemberId, membership.Obligation!.PayableAmount);
        return await GetAsync(client, membership.Id);
    }
    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(api.AdminConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    // ---- Inscripción -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Inscribir_crea_la_membresia_pendiente_de_pago_y_su_obligacion_con_el_IVA_desglosado()
    {
        var s = await NewSetupAsync();

        var membership = await EnrollAsync(s.Tenant.Owner, s.Member.Id, s.Plan.Id);

        Assert.Equal("PendingPayment", membership.Status);
        Assert.Equal(s.Member.Id, membership.MemberId);
        Assert.Equal(s.Branch, membership.BranchId);
        Assert.Equal(Today, membership.StartDate);
        Assert.Equal(Today.AddMonths(1).AddDays(-1), membership.EndDate);
        Assert.Equal(34.50m, membership.AgreedPrice);
        Assert.Equal(0m, membership.DiscountAmount);
        Assert.Equal(15m, membership.TaxPercent);
        Assert.NotNull(membership.CreatedBy);

        var obligation = membership.Obligation!;
        Assert.Equal("OB-000001", obligation.Reference);
        Assert.Equal(membership.Id, obligation.MembershipId);
        Assert.Equal("Pending", obligation.Status);
        Assert.Equal(34.50m, obligation.OriginalAmount);
        Assert.Equal(34.50m, obligation.PayableAmount);
        Assert.Equal(30.00m, obligation.Subtotal);
        Assert.Equal(4.50m, obligation.TaxAmount);
        Assert.Equal(15m, obligation.TaxPercent);
        Assert.Equal(Today, obligation.IssueDate);
        Assert.Equal(Today.AddDays(7), obligation.DueDate);
        Assert.Contains("Mensual", obligation.Concept, StringComparison.Ordinal);

        var events = await s.Tenant.Owner.GetFromJsonAsync<MembershipEventDto[]>($"/api/v1/memberships/{membership.Id}/events");
        Assert.Equal(["Created"], events!.Select(e => e.Type));
    }

    [Fact]
    public async Task Los_cambios_posteriores_del_plan_no_alteran_las_membresias_ya_contratadas()
    {
        var s = await NewSetupAsync();
        var membership = await EnrollAsync(s.Tenant.Owner, s.Member.Id, s.Plan.Id);

        var edit = await s.Tenant.Owner.PutAsJsonAsync($"/api/v1/membership-plans/{s.Plan.Id}",
            PlanBody("Mensual renombrado", 99m, durationValue: 6, taxKind: "Zero", allowFreeze: true, maxFreezeDays: 30));
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);

        var after = await GetAsync(s.Tenant.Owner, membership.Id);
        Assert.Equal("Mensual", after.PlanName);
        Assert.Equal(34.50m, after.PlanPrice);
        Assert.Equal(15m, after.TaxPercent);
        Assert.False(after.AllowFreeze);
        Assert.Equal(membership.EndDate, after.EndDate);
        Assert.Equal(34.50m, after.Obligation!.PayableAmount);
        Assert.Equal(4.50m, after.Obligation.TaxAmount);
    }

    [Fact]
    public async Task Una_promocion_por_codigo_descuenta_consume_un_uso_y_queda_copiada_en_la_membresia()
    {
        var s = await NewSetupAsync(price: 50m);
        var promotion = await CreatePromotionAsync(s.Tenant.Owner, "Verano", "VERANO20", 20m, [s.Plan.Id]);

        var membership = await EnrollAsync(s.Tenant.Owner, s.Member.Id, s.Plan.Id, promotionCode: "verano20");

        Assert.Equal("VERANO20", membership.PromotionCode);
        Assert.Equal(10.00m, membership.DiscountAmount);
        Assert.Equal(50m, membership.PlanPrice);
        Assert.Equal(40.00m, membership.AgreedPrice);
        var obligation = membership.Obligation!;
        Assert.Equal(50m, obligation.OriginalAmount);
        Assert.Equal(10m, obligation.DiscountAmount);
        Assert.Equal(40m, obligation.PayableAmount);
        Assert.Equal(34.78m, obligation.Subtotal);
        Assert.Equal(5.22m, obligation.TaxAmount);
        Assert.Equal(obligation.PayableAmount, obligation.Subtotal + obligation.TaxAmount);

        var used = (await s.Tenant.Owner.GetFromJsonAsync<PromotionDto>($"/api/v1/promotions/{promotion.Id}"))!;
        Assert.Equal(1, used.UsedCount);

        // También se puede indicar por identificador.
        var second = await MemberTests.CreateMemberAsync(s.Tenant.Owner, s.Branch);
        var byId = await PostEnrollAsync(s.Tenant.Owner, second.Id, s.Plan.Id, promotionId: promotion.Id);
        Assert.Equal(HttpStatusCode.Created, byId.StatusCode);
        Assert.Equal(2, (await s.Tenant.Owner.GetFromJsonAsync<PromotionDto>($"/api/v1/promotions/{promotion.Id}"))!.UsedCount);
    }

    [Fact]
    public async Task Las_reglas_de_promocion_se_validan_en_el_backend()
    {
        var s = await NewSetupAsync(price: 50m);
        var otherPlan = await CreatePlanAsync(s.Tenant.Owner, "Otro plan", 40m);
        await CreatePromotionAsync(s.Tenant.Owner, "Solo otro", "SOLOOTRO", 10m, [otherPlan.Id]);
        await s.Tenant.Owner.PostAsJsonAsync("/api/v1/promotions", PromotionBody("Vencida", code: "VENCIDA", validFrom: Today.AddDays(-20), validUntil: Today.AddDays(-1)));
        await s.Tenant.Owner.PostAsJsonAsync("/api/v1/promotions", PromotionBody("Futura", code: "FUTURA", validFrom: Today.AddDays(5), validUntil: Today.AddDays(15)));
        await s.Tenant.Owner.PostAsJsonAsync("/api/v1/promotions", PromotionBody("Apagada", code: "APAGADA", isActive: false));
        await CreatePromotionAsync(s.Tenant.Owner, "Vigente", "VIGENTE", 10m);

        async Task<(HttpStatusCode, string?)> TryAsync(string code)
        {
            var member = await MemberTests.CreateMemberAsync(s.Tenant.Owner, s.Branch);
            var response = await PostEnrollAsync(s.Tenant.Owner, member.Id, s.Plan.Id, code);
            return (response.StatusCode, response.IsSuccessStatusCode ? null : await TestApi.ProblemCodeAsync(response));
        }

        Assert.Equal((HttpStatusCode.UnprocessableEntity, "promotions.not_available"), await TryAsync("VENCIDA"));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "promotions.not_available"), await TryAsync("FUTURA"));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "promotions.not_available"), await TryAsync("APAGADA"));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "promotions.not_applicable"), await TryAsync("SOLOOTRO"));
        Assert.Equal((HttpStatusCode.NotFound, "promotions.not_found"), await TryAsync("NOEXISTE"));
        Assert.Equal((HttpStatusCode.Created, null), await TryAsync("VIGENTE"));

        var both = await s.Tenant.Owner.PostAsJsonAsync("/api/v1/memberships", new { memberId = s.Member.Id, planId = s.Plan.Id, promotionId = Guid.NewGuid(), promotionCode = "VIGENTE" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, both.StatusCode);
    }

    [Fact]
    public async Task Bajar_el_tope_de_descuento_despues_de_crear_la_promocion_tambien_se_respeta_al_aplicarla()
    {
        var s = await NewSetupAsync(price: 50m);
        await CreatePromotionAsync(s.Tenant.Owner, "Treinta", "TREINTA", 30m);
        await s.Tenant.Owner.PutAsJsonAsync("/api/v1/commercial-settings", new { accessPolicy = "AllowUntilOverdue", minPaymentPercent = 50, dueDays = 7, maxDiscountPercent = 10 });

        var response = await PostEnrollAsync(s.Tenant.Owner, s.Member.Id, s.Plan.Id, "TREINTA");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("promotions.discount_above_maximum", await TestApi.ProblemCodeAsync(response));
        Assert.Equal(0, (await s.Tenant.Owner.GetFromJsonAsync<PagedDto<MembershipDto>>("/api/v1/memberships"))!.TotalCount);
    }

    [Fact]
    public async Task Aplicar_una_promocion_exige_el_permiso_de_aplicarla()
    {
        var s = await NewSetupAsync(price: 50m);
        await CreatePromotionAsync(s.Tenant.Owner, "Verano", "VERANO10", 10m);
        await TestApi.CreateUserAsync(s.Tenant.Owner, "recepcion@gym.test", "Recepcion", s.Branch);
        using var reception = TestApi.Authenticated(api, await TestApi.LoginAsync(api, s.Tenant.Slug, "recepcion@gym.test", TestApi.UserPassword));

        var denied = await PostEnrollAsync(reception, s.Member.Id, s.Plan.Id, "VERANO10");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal("promotions.apply_not_allowed", await TestApi.ProblemCodeAsync(denied));

        // Sin promoción, recepción sí inscribe; el uso de la promoción no se consumió.
        Assert.Equal(HttpStatusCode.Created, (await PostEnrollAsync(reception, s.Member.Id, s.Plan.Id)).StatusCode);
        var promotions = await s.Tenant.Owner.GetFromJsonAsync<PagedDto<PromotionDto>>("/api/v1/promotions");
        Assert.Equal(0, promotions!.Items.Single().UsedCount);
    }

    [Fact]
    public async Task Una_promocion_con_un_solo_uso_no_se_puede_consumir_dos_veces_ni_en_paralelo()
    {
        var s = await NewSetupAsync(price: 50m);
        var response = await s.Tenant.Owner.PostAsJsonAsync("/api/v1/promotions", PromotionBody("Uno solo", code: "UNICO", value: 10m, maxUses: 1));
        var promotion = (await response.Content.ReadFromJsonAsync<PromotionDto>())!;
        var members = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => MemberTests.CreateMemberAsync(s.Tenant.Owner, s.Branch)));

        var results = await Task.WhenAll(members.Select(m => PostEnrollAsync(s.Tenant.Owner, m.Id, s.Plan.Id, "UNICO")));

        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Created));
        foreach (var rejected in results.Where(r => r.StatusCode != HttpStatusCode.Created))
        {
            Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
            Assert.Equal("promotions.exhausted", await TestApi.ProblemCodeAsync(rejected));
        }

        var after = (await s.Tenant.Owner.GetFromJsonAsync<PromotionDto>($"/api/v1/promotions/{promotion.Id}"))!;
        Assert.Equal(1, after.UsedCount);
        Assert.Equal("Exhausted", after.Status);
        Assert.Equal(1, await ScalarAsync<long>($"SELECT count(*) FROM members.memberships WHERE promotion_id = '{promotion.Id}'"));
        // Las inscripciones rechazadas no dejaron obligaciones huérfanas.
        Assert.Equal(1, await ScalarAsync<long>($"SELECT count(*) FROM billing.payment_obligations WHERE tenant_id = '{s.Tenant.Id}'"));
    }

    [Fact]
    public async Task Un_alumno_no_puede_tener_dos_membresias_que_se_solapan_ni_inscribiendose_en_paralelo()
    {
        var s = await NewSetupAsync();
        await EnrollAsync(s.Tenant.Owner, s.Member.Id, s.Plan.Id);

        var second = await PostEnrollAsync(s.Tenant.Owner, s.Member.Id, s.Plan.Id);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("memberships.overlap", await TestApi.ProblemCodeAsync(second));

        var other = await MemberTests.CreateMemberAsync(s.Tenant.Owner, s.Branch);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => PostEnrollAsync(s.Tenant.Owner, other.Id, s.Plan.Id)));

        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.All(results.Where(r => r.StatusCode != HttpStatusCode.Created), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        Assert.Equal(1, await ScalarAsync<long>($"SELECT count(*) FROM members.memberships WHERE member_id = '{other.Id}'"));
        Assert.Equal(1, await ScalarAsync<long>($"SELECT count(*) FROM billing.payment_obligations WHERE member_id = '{other.Id}'"));
    }

    [Fact]
    public async Task Un_plan_no_vendible_un_alumno_inactivo_o_fechas_invalidas_se_rechazan_sin_dejar_rastro()
    {
        var s = await NewSetupAsync();
        var inactivePlan = await CreatePlanAsync(s.Tenant.Owner, "Apagado", 20m);
        await s.Tenant.Owner.PutAsJsonAsync($"/api/v1/membership-plans/{inactivePlan.Id}", PlanBody("Apagado", 20m, isActive: false));
        var otherBranch = await TestApi.CreateBranchAsync(s.Tenant.Owner, "OTRA");
        var otherBranchPlan = await CreatePlanAsync(s.Tenant.Owner, "Solo otra", 20m, [otherBranch]);
        var expiredPlanResponse = await s.Tenant.Owner.PostAsJsonAsync("/api/v1/membership-plans",
            PlanBody("Fuera de vigencia", 20m, validFrom: Today.AddDays(-30).ToString("yyyy-MM-dd"), validUntil: Today.AddDays(-1).ToString("yyyy-MM-dd")));
        var expiredPlan = (await expiredPlanResponse.Content.ReadFromJsonAsync<PlanDto>())!;

        foreach (var plan in new[] { inactivePlan, otherBranchPlan, expiredPlan })
        {
            var response = await PostEnrollAsync(s.Tenant.Owner, s.Member.Id, plan.Id);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal("plans.not_sellable", await TestApi.ProblemCodeAsync(response));
        }

        var inactiveMember = await MemberTests.CreateMemberAsync(s.Tenant.Owner, s.Branch);
        await s.Tenant.Owner.PatchAsJsonAsync($"/api/v1/members/{inactiveMember.Id}/status", new { isActive = false });
        var inactive = await PostEnrollAsync(s.Tenant.Owner, inactiveMember.Id, s.Plan.Id);
        Assert.Equal("members.inactive", await TestApi.ProblemCodeAsync(inactive));

        var past = await PostEnrollAsync(s.Tenant.Owner, s.Member.Id, s.Plan.Id, start: Today.AddDays(-1));
        Assert.Equal("memberships.start_in_past", await TestApi.ProblemCodeAsync(past));
        var far = await PostEnrollAsync(s.Tenant.Owner, s.Member.Id, s.Plan.Id, start: Today.AddDays(400));
        Assert.Equal("memberships.start_too_far", await TestApi.ProblemCodeAsync(far));
        var dueInPast = await PostEnrollAsync(s.Tenant.Owner, s.Member.Id, s.Plan.Id, due: Today.AddDays(-1));
        Assert.Equal("obligations.due_in_past", await TestApi.ProblemCodeAsync(dueInPast));

        Assert.Equal(0, await ScalarAsync<long>($"SELECT count(*) FROM members.memberships WHERE tenant_id = '{s.Tenant.Id}'"));
        Assert.Equal(0, await ScalarAsync<long>($"SELECT count(*) FROM billing.payment_obligations WHERE tenant_id = '{s.Tenant.Id}'"));
    }

    [Fact]
    public async Task Una_inscripcion_futura_y_un_vencimiento_propio_se_respetan_y_las_politicas_cambian_el_vencimiento_por_defecto()
    {
        var s = await NewSetupAsync();
        var start = Today.AddDays(10);

        var future = (await (await PostEnrollAsync(s.Tenant.Owner, s.Member.Id, s.Plan.Id, start: start, due: Today.AddDays(3))).Content.ReadFromJsonAsync<MembershipDto>())!;

        Assert.Equal(start, future.StartDate);
        Assert.Equal(start.AddMonths(1).AddDays(-1), future.EndDate);
        Assert.Equal(Today.AddDays(3), future.Obligation!.DueDate);

        await s.Tenant.Owner.PutAsJsonAsync("/api/v1/commercial-settings", new { accessPolicy = "AllowUntilOverdue", minPaymentPercent = 50, dueDays = 2, maxDiscountPercent = 30 });
        var other = await MemberTests.CreateMemberAsync(s.Tenant.Owner, s.Branch);
        var defaulted = await EnrollAsync(s.Tenant.Owner, other.Id, s.Plan.Id);
        Assert.Equal(Today.AddDays(2), defaulted.Obligation!.DueDate);
    }

    [Fact]
    public async Task Las_referencias_de_obligacion_son_consecutivas_por_gimnasio_e_independientes_entre_gimnasios()
    {
        var a = await NewSetupAsync(code: "RA");
        var b = await NewSetupAsync(code: "RB");
        var secondMember = await MemberTests.CreateMemberAsync(a.Tenant.Owner, a.Branch);

        var first = await EnrollAsync(a.Tenant.Owner, a.Member.Id, a.Plan.Id);
        var second = await EnrollAsync(a.Tenant.Owner, secondMember.Id, a.Plan.Id);
        var other = await EnrollAsync(b.Tenant.Owner, b.Member.Id, b.Plan.Id);

        Assert.Equal("OB-000001", first.Obligation!.Reference);
        Assert.Equal("OB-000002", second.Obligation!.Reference);
        Assert.Equal("OB-000001", other.Obligation!.Reference);
    }

    // ---- Aislamiento y permisos ------------------------------------------------------------------------------------

    [Fact]
    public async Task Otro_gimnasio_no_puede_inscribir_ver_ni_operar_membresias_ajenas()
    {
        var a = await NewSetupAsync(code: "XA");
        var b = await NewSetupAsync(code: "XB");
        var membershipB = await EnrollAsync(b.Tenant.Owner, b.Member.Id, b.Plan.Id);

        var memberResponse = await PostEnrollAsync(a.Tenant.Owner, b.Member.Id, a.Plan.Id);
        Assert.Equal(HttpStatusCode.NotFound, memberResponse.StatusCode);
        Assert.Equal("members.not_found", await TestApi.ProblemCodeAsync(memberResponse));
        var planResponse = await PostEnrollAsync(a.Tenant.Owner, a.Member.Id, b.Plan.Id);
        Assert.Equal("plans.not_found", await TestApi.ProblemCodeAsync(planResponse));

        var url = $"/api/v1/memberships/{membershipB.Id}";
        Assert.Equal(HttpStatusCode.NotFound, (await a.Tenant.Owner.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.Tenant.Owner.GetAsync($"{url}/events")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.Tenant.Owner.PostAsJsonAsync($"{url}/renew", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.Tenant.Owner.PostAsJsonAsync($"{url}/freeze", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.Tenant.Owner.PostAsync($"{url}/reactivate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.Tenant.Owner.PostAsJsonAsync($"{url}/cancel", new { reason = "Intento ajeno" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.Tenant.Owner.GetAsync($"/api/v1/payment-obligations/{membershipB.Obligation!.Id}")).StatusCode);

        Assert.Equal(0, (await a.Tenant.Owner.GetFromJsonAsync<PagedDto<MembershipDto>>("/api/v1/memberships"))!.TotalCount);
        Assert.Equal(0, (await a.Tenant.Owner.GetFromJsonAsync<PagedDto<ObligationDto>>("/api/v1/payment-obligations"))!.TotalCount);
        Assert.Equal("PendingPayment", (await GetAsync(b.Tenant.Owner, membershipB.Id)).Status);
    }

    [Fact]
    public async Task Quien_solo_accede_a_una_sucursal_no_ve_ni_inscribe_alumnos_de_otra()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var north = await TestApi.CreateBranchAsync(tenant.Owner, "SN");
        var south = await TestApi.CreateBranchAsync(tenant.Owner, "SS");
        var plan = await CreatePlanAsync(tenant.Owner, "Global", 30m);
        var memberNorth = await MemberTests.CreateMemberAsync(tenant.Owner, north);
        var memberSouth = await MemberTests.CreateMemberAsync(tenant.Owner, south);
        var membershipSouth = await EnrollAsync(tenant.Owner, memberSouth.Id, plan.Id);
        await TestApi.CreateUserAsync(tenant.Owner, "recepcion.norte@gym.test", "Recepcion", north);
        using var reception = TestApi.Authenticated(api, await TestApi.LoginAsync(api, tenant.Slug, "recepcion.norte@gym.test", TestApi.UserPassword));

        var denied = await PostEnrollAsync(reception, memberSouth.Id, plan.Id);
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await reception.GetAsync($"/api/v1/memberships/{membershipSouth.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reception.GetAsync($"/api/v1/memberships?branchId={south}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await reception.GetAsync($"/api/v1/payment-obligations/{membershipSouth.Obligation!.Id}")).StatusCode);

        var own = await EnrollAsync(reception, memberNorth.Id, plan.Id);
        var list = await reception.GetFromJsonAsync<PagedDto<MembershipDto>>("/api/v1/memberships");
        Assert.Equal([own.Id], list!.Items.Select(m => m.Id));
        var obligations = await reception.GetFromJsonAsync<PagedDto<ObligationDto>>("/api/v1/payment-obligations");
        Assert.Equal([own.Obligation!.Id], obligations!.Items.Select(o => o.Id));
    }

    [Fact]
    public async Task Los_permisos_del_rol_se_aplican_a_cada_operacion_de_membresias()
    {
        var s = await NewSetupAsync(allowFreeze: true, maxFreezeDays: 15);
        var active = await ActivateAsync(s.Tenant.Owner, await EnrollAsync(s.Tenant.Owner, s.Member.Id, s.Plan.Id));
        await TestApi.CreateUserAsync(s.Tenant.Owner, "recepcion@gym.test", "Recepcion", s.Branch);
        await TestApi.CreateUserAsync(s.Tenant.Owner, "entrenador@gym.test", "Entrenador", s.Branch);
        using var reception = TestApi.Authenticated(api, await TestApi.LoginAsync(api, s.Tenant.Slug, "recepcion@gym.test", TestApi.UserPassword));
        using var trainer = TestApi.Authenticated(api, await TestApi.LoginAsync(api, s.Tenant.Slug, "entrenador@gym.test", TestApi.UserPassword));
        var url = $"/api/v1/memberships/{active.Id}";

        // Entrenador: ni siquiera consulta membresías ni cobros.
        Assert.Equal(HttpStatusCode.Forbidden, (await trainer.GetAsync("/api/v1/memberships")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await trainer.GetAsync("/api/v1/payment-obligations")).StatusCode);

        // Recepción: consulta, inscribe y renueva; no congela ni cancela.
        Assert.Equal(HttpStatusCode.OK, (await reception.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reception.PostAsJsonAsync($"{url}/freeze", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reception.PostAsync($"{url}/reactivate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reception.PostAsJsonAsync($"{url}/cancel", new { reason = "No autorizado" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await reception.PostAsJsonAsync($"{url}/renew", new { })).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().GetAsync("/api/v1/memberships")).StatusCode);
        Assert.Equal("Active", (await GetAsync(s.Tenant.Owner, active.Id)).Status);
    }

    // ---- Renovación ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Renovar_crea_otra_membresia_desde_el_dia_siguiente_sin_tocar_la_original()
    {
        var s = await NewSetupAsync();
        var original = await ActivateAsync(s.Tenant.Owner, await EnrollAsync(s.Tenant.Owner, s.Member.Id, s.Plan.Id));

        var response = await s.Tenant.Owner.PostAsJsonAsync($"/api/v1/memberships/{original.Id}/renew", new { });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var renewal = (await response.Content.ReadFromJsonAsync<MembershipDto>())!;
        Assert.NotEqual(original.Id, renewal.Id);
        Assert.Equal(original.Id, renewal.RenewedFromId);
        Assert.Equal(original.EndDate.AddDays(1), renewal.StartDate);
        Assert.Equal(renewal.StartDate.AddMonths(1).AddDays(-1), renewal.EndDate);
        Assert.Equal("PendingPayment", renewal.Status);
        Assert.Equal("OB-000002", renewal.Obligation!.Reference);

        // La original conserva fechas, estado y obligación; solo su historial registra la renovación.
        var after = await GetAsync(s.Tenant.Owner, original.Id);
        Assert.Equal(original.StartDate, after.StartDate);
        Assert.Equal(original.EndDate, after.EndDate);
        Assert.Equal("Active", after.Status);
        Assert.Equal(original.Obligation!.Id, after.Obligation!.Id);
        var events = await s.Tenant.Owner.GetFromJsonAsync<MembershipEventDto[]>($"/api/v1/memberships/{original.Id}/events");
        Assert.Equal(["Created", "Activated", "Renewed"], events!.Select(e => e.Type));
    }

    [Fact]
    public async Task Renovar_una_membresia_ya_renovada_pendiente_de_pago_cancelada_o_congelada_se_rechaza()
    {
        var s = await NewSetupAsync(allowFreeze: true, maxFreezeDays: 15);
        var url = (MembershipDto m) => $"/api/v1/memberships/{m.Id}/renew";

        var pending = await EnrollAsync(s.Tenant.Owner, s.Member.Id, s.Plan.Id);
        var pendingRenew = await s.Tenant.Owner.PostAsJsonAsync(url(pending), new { });
        Assert.Equal(HttpStatusCode.Conflict, pendingRenew.StatusCode);
        Assert.Equal("memberships.pending_payment", await TestApi.ProblemCodeAsync(pendingRenew));

        var active = await ActivateAsync(s.Tenant.Owner, pending);
        Assert.Equal(HttpStatusCode.Created, (await s.Tenant.Owner.PostAsJsonAsync(url(active), new { })).StatusCode);
        var twice = await s.Tenant.Owner.PostAsJsonAsync(url(active), new { });
        Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);
        Assert.Equal("memberships.already_renewed", await TestApi.ProblemCodeAsync(twice));

        var otherMember = await MemberTests.CreateMemberAsync(s.Tenant.Owner, s.Branch);
        var toCancel = await EnrollAsync(s.Tenant.Owner, otherMember.Id, s.Plan.Id);
        await s.Tenant.Owner.PostAsJsonAsync($"/api/v1/memberships/{toCancel.Id}/cancel", new { reason = "Se arrepintió" });
        var cancelledRenew = await s.Tenant.Owner.PostAsJsonAsync(url(toCancel), new { });
        Assert.Equal("memberships.cannot_renew", await TestApi.ProblemCodeAsync(cancelledRenew));

        var thirdMember = await MemberTests.CreateMemberAsync(s.Tenant.Owner, s.Branch);
        var frozen = await ActivateAsync(s.Tenant.Owner, await EnrollAsync(s.Tenant.Owner, thirdMember.Id, s.Plan.Id));
        await s.Tenant.Owner.PostAsJsonAsync($"/api/v1/memberships/{frozen.Id}/freeze", new { });
        var frozenRenew = await s.Tenant.Owner.PostAsJsonAsync(url(frozen), new { });
        Assert.Equal("memberships.frozen", await TestApi.ProblemCodeAsync(frozenRenew));
    }

    [Fact]
    public async Task Renovar_una_membresia_vencida_empieza_hoy_y_permite_cambiar_de_plan_y_aplicar_promocion()
    {
        var s = await NewSetupAsync();
        var premium = await CreatePlanAsync(s.Tenant.Owner, "Trimestral", 90m, durationUnit: "Month", durationValue: 3);
        await CreatePromotionAsync(s.Tenant.Owner, "Regreso", "REGRESO10", 10m);
        var original = await ActivateAsync(s.Tenant.Owner, await EnrollAsync(s.Tenant.Owner, s.Member.Id, s.Plan.Id));
        await SqlAsync($"UPDATE members.memberships SET start_date = start_date - 60, end_date = end_date - 60 WHERE id = '{original.Id}'");
        Assert.Equal("Expired", (await GetAsync(s.Tenant.Owner, original.Id)).Status);

        var response = await s.Tenant.Owner.PostAsJsonAsync($"/api/v1/memberships/{original.Id}/renew", new { planId = premium.Id, promotionCode = "REGRESO10" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var renewal = (await response.Content.ReadFromJsonAsync<MembershipDto>())!;
        Assert.Equal(Today, renewal.StartDate);
        Assert.Equal(Today.AddMonths(3).AddDays(-1), renewal.EndDate);
        Assert.Equal("Trimestral", renewal.PlanName);
        Assert.Equal(9m, renewal.DiscountAmount);
        Assert.Equal(81m, renewal.AgreedPrice);
    }

    // ---- Congelar y reactivar --------------------------------------------------------------------------------------

    [Fact]
    public async Task Congelar_exige_membresia_activa_y_un_plan_que_lo_permita()
    {
        var withFreeze = await NewSetupAsync(allowFreeze: true, maxFreezeDays: 15, code: "FZ");
        var pending = await EnrollAsync(withFreeze.Tenant.Owner, withFreeze.Member.Id, withFreeze.Plan.Id);
        var onPending = await withFreeze.Tenant.Owner.PostAsJsonAsync($"/api/v1/memberships/{pending.Id}/freeze", new { });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, onPending.StatusCode);
        Assert.Equal("memberships.not_active", await TestApi.ProblemCodeAsync(onPending));

        var active = await ActivateAsync(withFreeze.Tenant.Owner, pending);
        var frozen = await withFreeze.Tenant.Owner.PostAsJsonAsync($"/api/v1/memberships/{active.Id}/freeze", new { reason = "Viaje por trabajo" });
        Assert.Equal(HttpStatusCode.OK, frozen.StatusCode);
        var dto = (await frozen.Content.ReadFromJsonAsync<MembershipDto>())!;
        Assert.Equal("Frozen", dto.Status);
        Assert.Equal(Today, dto.FreezeStartedOn);
        Assert.Equal(active.EndDate, dto.EndDate);
        var twice = await withFreeze.Tenant.Owner.PostAsJsonAsync($"/api/v1/memberships/{active.Id}/freeze", new { });
        Assert.Equal("memberships.not_active", await TestApi.ProblemCodeAsync(twice));

        var noFreeze = await NewSetupAsync(code: "NF");
        var activeNoFreeze = await ActivateAsync(noFreeze.Tenant.Owner, await EnrollAsync(noFreeze.Tenant.Owner, noFreeze.Member.Id, noFreeze.Plan.Id));
        var denied = await noFreeze.Tenant.Owner.PostAsJsonAsync($"/api/v1/memberships/{activeNoFreeze.Id}/freeze", new { });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, denied.StatusCode);
        Assert.Equal("memberships.freeze_not_allowed", await TestApi.ProblemCodeAsync(denied));
    }

    [Fact]
    public async Task Reactivar_alarga_el_fin_por_los_dias_congelados_hasta_el_cupo_del_plan_y_deja_el_cambio_a_la_vista()
    {
        var s = await NewSetupAsync(allowFreeze: true, maxFreezeDays: 15);
        var active = await ActivateAsync(s.Tenant.Owner, await EnrollAsync(s.Tenant.Owner, s.Member.Id, s.Plan.Id));
        var url = $"/api/v1/memberships/{active.Id}";

        // Primer congelamiento: 10 días.
        await s.Tenant.Owner.PostAsJsonAsync($"{url}/freeze", new { });
        await SqlAsync($"UPDATE members.memberships SET freeze_started_on = '{Today.AddDays(-10):yyyy-MM-dd}' WHERE id = '{active.Id}'");
        var first = (await (await s.Tenant.Owner.PostAsync($"{url}/reactivate", null)).Content.ReadFromJsonAsync<MembershipDto>())!;
        Assert.Equal("Active", first.Status);
        Assert.Equal(active.EndDate.AddDays(10), first.EndDate);
        Assert.Equal(10, first.FrozenDaysUsed);
        Assert.Equal(5, first.RemainingFreezeDays);
        Assert.Null(first.FreezeStartedOn);

        // Segundo: 20 días congelado, pero solo quedan 5 de cupo.
        await s.Tenant.Owner.PostAsJsonAsync($"{url}/freeze", new { });
        await SqlAsync($"UPDATE members.memberships SET freeze_started_on = '{Today.AddDays(-20):yyyy-MM-dd}' WHERE id = '{active.Id}'");
        var second = (await (await s.Tenant.Owner.PostAsync($"{url}/reactivate", null)).Content.ReadFromJsonAsync<MembershipDto>())!;
        Assert.Equal(first.EndDate.AddDays(5), second.EndDate);
        Assert.Equal(15, second.FrozenDaysUsed);
        Assert.Equal(0, second.RemainingFreezeDays);

        // Sin cupo no se puede volver a congelar; reactivar lo que no está congelado también falla.
        var exhausted = await s.Tenant.Owner.PostAsJsonAsync($"{url}/freeze", new { });
        Assert.Equal("memberships.freeze_limit_reached", await TestApi.ProblemCodeAsync(exhausted));
        var notFrozen = await s.Tenant.Owner.PostAsync($"{url}/reactivate", null);
        Assert.Equal("memberships.not_frozen", await TestApi.ProblemCodeAsync(notFrozen));

        // El historial muestra fechas anterior y nueva, días congelados y otorgados.
        var events = (await s.Tenant.Owner.GetFromJsonAsync<MembershipEventDto[]>($"{url}/events"))!;
        Assert.Equal(["Created", "Activated", "Frozen", "Reactivated", "Frozen", "Reactivated"], events.Select(e => e.Type));
        using var details = JsonDocument.Parse(events.Last().Details!);
        Assert.Equal(20, details.RootElement.GetProperty("frozenDays").GetInt32());
        Assert.Equal(5, details.RootElement.GetProperty("grantedDays").GetInt32());
        Assert.Equal(first.EndDate, DateOnly.Parse(details.RootElement.GetProperty("oldEnd").GetString()!));
        Assert.Equal(second.EndDate, DateOnly.Parse(details.RootElement.GetProperty("newEnd").GetString()!));
    }

    [Fact]
    public async Task No_se_congela_una_membresia_que_ya_tiene_una_renovacion_programada()
    {
        var s = await NewSetupAsync(allowFreeze: true, maxFreezeDays: 15);
        var active = await ActivateAsync(s.Tenant.Owner, await EnrollAsync(s.Tenant.Owner, s.Member.Id, s.Plan.Id));
        await s.Tenant.Owner.PostAsJsonAsync($"/api/v1/memberships/{active.Id}/renew", new { });

        var response = await s.Tenant.Owner.PostAsJsonAsync($"/api/v1/memberships/{active.Id}/freeze", new { });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("memberships.freeze_blocked_by_renewal", await TestApi.ProblemCodeAsync(response));
        Assert.Equal("Active", (await GetAsync(s.Tenant.Owner, active.Id)).Status);
    }

    // ---- Cancelar --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Cancelar_exige_un_motivo_anula_la_obligacion_sin_pagos_y_libera_el_periodo()
    {
        var s = await NewSetupAsync();
        var membership = await EnrollAsync(s.Tenant.Owner, s.Member.Id, s.Plan.Id);
        var url = $"/api/v1/memberships/{membership.Id}/cancel";

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await s.Tenant.Owner.PostAsJsonAsync(url, new { reason = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await s.Tenant.Owner.PostAsJsonAsync(url, new { reason = "no" })).StatusCode);
        Assert.Equal("PendingPayment", (await GetAsync(s.Tenant.Owner, membership.Id)).Status);

        var response = await s.Tenant.Owner.PostAsJsonAsync(url, new { reason = "El alumno cambió de ciudad" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cancelled = (await response.Content.ReadFromJsonAsync<MembershipDto>())!;
        Assert.Equal("Cancelled", cancelled.Status);
        Assert.Equal("El alumno cambió de ciudad", cancelled.CancelReason);
        Assert.Equal("Voided", cancelled.Obligation!.Status);
        Assert.Contains("cancelada", cancelled.Obligation.VoidReason, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(cancelled.UpdatedBy);
        var events = await s.Tenant.Owner.GetFromJsonAsync<MembershipEventDto[]>($"/api/v1/memberships/{membership.Id}/events");
        Assert.Equal(["Created", "Cancelled"], events!.Select(e => e.Type));

        var again = await s.Tenant.Owner.PostAsJsonAsync(url, new { reason = "Otra vez" });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("memberships.already_cancelled", await TestApi.ProblemCodeAsync(again));

        // El periodo queda libre: el alumno se puede inscribir de nuevo, con una obligación nueva.
        var reenrolled = await EnrollAsync(s.Tenant.Owner, s.Member.Id, s.Plan.Id);
        Assert.Equal("OB-000002", reenrolled.Obligation!.Reference);
    }

    [Fact]
    public async Task Una_membresia_vencida_no_se_puede_cancelar()
    {
        var s = await NewSetupAsync();
        var active = await ActivateAsync(s.Tenant.Owner, await EnrollAsync(s.Tenant.Owner, s.Member.Id, s.Plan.Id));
        await SqlAsync($"UPDATE members.memberships SET start_date = start_date - 60, end_date = end_date - 60 WHERE id = '{active.Id}'");

        var response = await s.Tenant.Owner.PostAsJsonAsync($"/api/v1/memberships/{active.Id}/cancel", new { reason = "Ya venció" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("memberships.expired", await TestApi.ProblemCodeAsync(response));
    }

    // ---- Consultas -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task El_estado_vencido_se_deriva_de_la_fecha_de_fin_y_se_puede_filtrar()
    {
        var s = await NewSetupAsync();
        var second = await MemberTests.CreateMemberAsync(s.Tenant.Owner, s.Branch);
        var third = await MemberTests.CreateMemberAsync(s.Tenant.Owner, s.Branch);
        var current = await ActivateAsync(s.Tenant.Owner, await EnrollAsync(s.Tenant.Owner, s.Member.Id, s.Plan.Id));
        var oldActive = await ActivateAsync(s.Tenant.Owner, await EnrollAsync(s.Tenant.Owner, second.Id, s.Plan.Id));
        var oldPending = await EnrollAsync(s.Tenant.Owner, third.Id, s.Plan.Id);
        await SqlAsync($"UPDATE members.memberships SET start_date = start_date - 90, end_date = end_date - 90 WHERE id IN ('{oldActive.Id}', '{oldPending.Id}')");

        async Task<Guid[]> ListAsync(string status) =>
            [.. (await s.Tenant.Owner.GetFromJsonAsync<PagedDto<MembershipDto>>($"/api/v1/memberships?status={status}"))!.Items.Select(m => m.Id)];

        Assert.Equal("Expired", (await GetAsync(s.Tenant.Owner, oldActive.Id)).Status);
        Assert.Equivalent(new[] { oldActive.Id, oldPending.Id }, await ListAsync("Expired"));
        Assert.Equal([current.Id], await ListAsync("Active"));
        Assert.Empty(await ListAsync("PendingPayment"));
    }

    [Fact]
    public async Task Las_proximas_expiraciones_listan_solo_membresias_activas_que_terminan_dentro_del_plazo()
    {
        var s = await NewSetupAsync();
        var members = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => MemberTests.CreateMemberAsync(s.Tenant.Owner, s.Branch)));
        var soon = await ActivateAsync(s.Tenant.Owner, await EnrollAsync(s.Tenant.Owner, members[0].Id, s.Plan.Id));
        var later = await ActivateAsync(s.Tenant.Owner, await EnrollAsync(s.Tenant.Owner, members[1].Id, s.Plan.Id));
        var pending = await EnrollAsync(s.Tenant.Owner, members[2].Id, s.Plan.Id);
        var today = await ActivateAsync(s.Tenant.Owner, await EnrollAsync(s.Tenant.Owner, members[3].Id, s.Plan.Id));
        await SqlAsync($"UPDATE members.memberships SET start_date = '{Today.AddDays(-27):yyyy-MM-dd}', end_date = '{Today.AddDays(3):yyyy-MM-dd}' WHERE id = '{soon.Id}'");
        await SqlAsync($"UPDATE members.memberships SET start_date = '{Today.AddDays(-29):yyyy-MM-dd}', end_date = '{Today:yyyy-MM-dd}' WHERE id = '{today.Id}'");
        await SqlAsync($"UPDATE members.memberships SET start_date = '{Today.AddDays(-5):yyyy-MM-dd}', end_date = '{Today.AddDays(25):yyyy-MM-dd}' WHERE id = '{later.Id}'");

        async Task<Guid[]> ExpiringAsync(string query) =>
            [.. (await s.Tenant.Owner.GetFromJsonAsync<PagedDto<MembershipDto>>($"/api/v1/memberships/expiring?{query}"))!.Items.Select(m => m.Id)];

        Assert.Equal([today.Id, soon.Id], await ExpiringAsync("days=7"));
        Assert.Equal([today.Id], await ExpiringAsync("days=1"));
        Assert.Equal([today.Id, soon.Id, later.Id], await ExpiringAsync("days=30"));
        Assert.DoesNotContain(pending.Id, await ExpiringAsync("days=90"));
    }

    [Fact]
    public async Task Las_obligaciones_se_listan_por_alumno_estado_y_vencimiento()
    {
        var s = await NewSetupAsync();
        var second = await MemberTests.CreateMemberAsync(s.Tenant.Owner, s.Branch);
        var first = await EnrollAsync(s.Tenant.Owner, s.Member.Id, s.Plan.Id);
        var other = await EnrollAsync(s.Tenant.Owner, second.Id, s.Plan.Id);
        await SqlAsync($"UPDATE billing.payment_obligations SET issue_date = issue_date - 20, due_date = due_date - 10 WHERE id = '{other.Obligation!.Id}'");

        async Task<string[]> ListAsync(string query) =>
            [.. (await s.Tenant.Owner.GetFromJsonAsync<PagedDto<ObligationDto>>($"/api/v1/payment-obligations?{query}"))!.Items.Select(o => o.Reference)];

        Assert.Equal(["OB-000001"], await ListAsync($"memberId={s.Member.Id}"));
        Assert.Equal(["OB-000001"], await ListAsync("status=Pending"));
        Assert.Equal(["OB-000002"], await ListAsync("status=Overdue"));
        Assert.Equal(["OB-000002"], await ListAsync("search=OB-000002"));
        Assert.Equal(["OB-000001"], await ListAsync($"membershipId={first.Id}"));
        var overdue = await s.Tenant.Owner.GetFromJsonAsync<ObligationDto>($"/api/v1/payment-obligations/{other.Obligation.Id}");
        Assert.Equal("Overdue", overdue!.Status);
    }

    [Fact]
    public async Task Inscribir_y_renovar_quedan_auditados()
    {
        var s = await NewSetupAsync();
        var active = await ActivateAsync(s.Tenant.Owner, await EnrollAsync(s.Tenant.Owner, s.Member.Id, s.Plan.Id));
        var renewal = (await (await s.Tenant.Owner.PostAsJsonAsync($"/api/v1/memberships/{active.Id}/renew", new { })).Content.ReadFromJsonAsync<MembershipDto>())!;

        var audit = await s.Tenant.Owner.GetFromJsonAsync<PagedDto<AuditDto>>("/api/v1/audit-logs?pageSize=100");

        Assert.Contains(audit!.Items, l => l.Action == "Membership.Created" && l.EntityId == renewal.Id.ToString());
        Assert.Contains(audit.Items, l => l.Action == "PaymentObligation.Created" && l.EntityId == renewal.Obligation!.Id.ToString());
    }

    // ---- La base como última defensa -------------------------------------------------------------------------------

    [Fact]
    public async Task La_base_rechaza_solapamientos_importes_inconsistentes_y_relaciones_entre_gimnasios_aunque_el_codigo_falle()
    {
        var a = await NewSetupAsync(code: "DBA");
        var b = await NewSetupAsync(code: "DBB");
        var membership = await EnrollAsync(a.Tenant.Owner, a.Member.Id, a.Plan.Id);
        var membershipB = await EnrollAsync(b.Tenant.Owner, b.Member.Id, b.Plan.Id);

        await using var connection = new NpgsqlConnection(api.AppConnection);
        await connection.OpenAsync();
        await Exec(connection, $"SELECT set_config('app.tenant_id', '{a.Tenant.Id}', false)");

        // RLS: solo se ven las filas del tenant activo.
        Assert.Equal(1L, await Scalar(connection, "SELECT count(*) FROM members.memberships"));
        Assert.Equal(0L, await Scalar(connection, $"SELECT count(*) FROM members.memberships WHERE id = '{membershipB.Id}'"));
        Assert.Equal(1L, await Scalar(connection, "SELECT count(*) FROM billing.payment_obligations"));
        Assert.Equal(0, await Exec(connection, $"UPDATE billing.payment_obligations SET concept = 'x' WHERE id = '{membershipB.Obligation!.Id}'"));

        // Importes inconsistentes y estados imposibles.
        var badAmount = await Assert.ThrowsAsync<PostgresException>(() => Exec(connection,
            $"UPDATE members.memberships SET agreed_price = 1 WHERE id = '{membership.Id}'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, badAmount.SqlState);
        var badStatus = await Assert.ThrowsAsync<PostgresException>(() => Exec(connection,
            $"UPDATE members.memberships SET status = 'Cancelled' WHERE id = '{membership.Id}'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, badStatus.SqlState);
        var badObligation = await Assert.ThrowsAsync<PostgresException>(() => Exec(connection,
            $"UPDATE billing.payment_obligations SET tax_amount = tax_amount + 1 WHERE id = '{membership.Obligation!.Id}'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, badObligation.SqlState);

        // Una membresía de A para un alumno de B (sin membresías propias): pasa RLS pero la clave compuesta lo impide.
        var freeMemberB = await MemberTests.CreateMemberAsync(b.Tenant.Owner, b.Branch);
        var crossTenant = await Assert.ThrowsAsync<PostgresException>(() => Exec(connection,
            $"UPDATE members.memberships SET member_id = '{freeMemberB.Id}' WHERE id = '{membership.Id}'"));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, crossTenant.SqlState);

        // Solapamiento: insertar directamente otra membresía vigente para el mismo alumno y fechas.
        var overlap = await Assert.ThrowsAsync<PostgresException>(() => Exec(connection, $"""
            INSERT INTO members.memberships (id, tenant_id, branch_id, member_id, plan_id, plan_name, plan_price, duration_value, duration_unit,
              tax_kind, tax_percent, allow_freeze, max_freeze_days, discount_amount, agreed_price, start_date, end_date, status, frozen_days_used, created_at)
            SELECT gen_random_uuid(), tenant_id, branch_id, member_id, plan_id, plan_name, plan_price, duration_value, duration_unit,
              tax_kind, tax_percent, allow_freeze, max_freeze_days, discount_amount, agreed_price, start_date, end_date, 'Active', 0, now()
            FROM members.memberships WHERE id = '{membership.Id}'
            """));
        Assert.Equal(PostgresErrorCodes.ExclusionViolation, overlap.SqlState);

        // Una cancelada no ocupa el periodo: la misma fila como cancelada sí se acepta.
        var cancelledCopy = await Exec(connection, $"""
            INSERT INTO members.memberships (id, tenant_id, branch_id, member_id, plan_id, plan_name, plan_price, duration_value, duration_unit,
              tax_kind, tax_percent, allow_freeze, max_freeze_days, discount_amount, agreed_price, start_date, end_date, status, frozen_days_used, created_at, cancel_reason)
            SELECT gen_random_uuid(), tenant_id, branch_id, member_id, plan_id, plan_name, plan_price, duration_value, duration_unit,
              tax_kind, tax_percent, allow_freeze, max_freeze_days, discount_amount, agreed_price, start_date, end_date, 'Cancelled', 0, now(), 'prueba'
            FROM members.memberships WHERE id = '{membership.Id}'
            """);
        Assert.Equal(1, cancelledCopy);
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
