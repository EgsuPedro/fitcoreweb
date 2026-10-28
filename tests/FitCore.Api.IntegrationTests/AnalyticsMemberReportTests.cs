using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FitCore.Api.IntegrationTests.Infrastructure;
using Npgsql;
using static FitCore.Api.IntegrationTests.Infrastructure.CommercialTestApi;

namespace FitCore.Api.IntegrationTests;

/// <summary>Reportes de alumnos y membresías (bloque 5): activos, altas y bajas, retención por cohorte, renovación y vencimientos.</summary>
[Collection(ApiCollection3.Name)]
public class AnalyticsMemberReportTests(ApiFixture api)
{
    private static readonly TimeSpan Ecuador = TimeSpan.FromHours(-5);

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.Add(Ecuador));

    private static string D(int daysFromToday) => Today.AddDays(daysFromToday).ToString("yyyy-MM-dd");

    private static string Range(int fromDays, int toDays) => $"preset=Custom&from={D(fromDays)}&to={D(toDays)}";

    private sealed record Gym(TestTenant Tenant, Guid Uio, Guid Gye, PlanDto Mensual, PlanDto Trimestral);

    private async Task<Gym> NewGymAsync()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var uio = await TestApi.CreateBranchAsync(tenant.Owner, "UIO");
        var gye = await TestApi.CreateBranchAsync(tenant.Owner, "GYE");
        return new Gym(tenant, uio, gye, await CreatePlanAsync(tenant.Owner, "Mensual", 30m), await CreatePlanAsync(tenant.Owner, "Trimestral", 80m, durationValue: 3));
    }

    private async Task<JsonElement> GetAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync($"/api/v1/reports/members/{url}");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Respondió {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static JsonElement Kpi(JsonElement report, string key) =>
        (report.TryGetProperty("kpis", out var kpis) ? kpis : report.GetProperty("summary")).EnumerateArray().Single(k => k.GetProperty("key").GetString() == key);

    private static decimal Num(JsonElement element, string property) => element.GetProperty(property).GetDecimal();

    private async Task SqlAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(api.AdminConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static int _members;

    private static async Task<MemberDto> NewMemberAsync(HttpClient owner, Guid branch)
    {
        var n = Interlocked.Increment(ref _members);
        var response = await owner.PostAsJsonAsync("/api/v1/members", new
        {
            branchId = branch, firstName = $"Socio{n}", lastName = "Membresia", idType = "Cedula", idNumber = MemberTests.NewCedula(), birthDate = "1992-03-04",
            email = $"m{n}.{Guid.NewGuid():N}"[..24] + "@alumnos.test", phone = "0991234567"
        });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<MemberDto>())!;
    }

    private static async Task<MembershipDto> EnrollAsync(HttpClient owner, MemberDto member, PlanDto plan, bool pay = true)
    {
        var response = await owner.PostAsJsonAsync("/api/v1/memberships", new { memberId = member.Id, planId = plan.Id });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var membership = (await response.Content.ReadFromJsonAsync<MembershipDto>())!;
        if (pay)
        {
            await PayAsync(owner, member.Id, plan.Price, "Card", $"V-{Guid.NewGuid():N}"[..10]);
        }

        return membership;
    }

    /// <summary>Mueve una membresía en el tiempo: fechas del contrato, fecha de venta y fecha en que se activó (al cobrarla).</summary>
    private async Task MoveAsync(Guid membershipId, int startDays, int endDays, int soldDaysAgo)
    {
        await SqlAsync($"UPDATE members.memberships SET start_date = '{D(startDays)}', end_date = '{D(endDays)}', created_at = now() - interval '{soldDaysAgo} days' WHERE id = '{membershipId}'");
        await SqlAsync($"UPDATE members.membership_events SET occurred_at = now() - interval '{soldDaysAgo} days' WHERE membership_id = '{membershipId}' AND type IN ('Created','Activated')");
    }

    private async Task<HttpClient> StaffAsync(Gym gym, string email, string role, params Guid[] branches)
    {
        await TestApi.CreateUserAsync(gym.Tenant.Owner, email, role, branches);
        return TestApi.Authenticated(api, await TestApi.LoginAsync(api, gym.Tenant.Slug, email, TestApi.UserPassword));
    }

    // ---- Resumen -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task El_resumen_cuenta_activos_con_membresia_vigente_y_muestra_estados_planes_y_sucursales()
    {
        var gym = await NewGymAsync();
        var owner = gym.Tenant.Owner;
        var a = await NewMemberAsync(owner, gym.Uio);
        await EnrollAsync(owner, a, gym.Mensual);
        var b = await NewMemberAsync(owner, gym.Uio);
        await EnrollAsync(owner, b, gym.Mensual);
        var c = await NewMemberAsync(owner, gym.Uio);
        var membershipC = await EnrollAsync(owner, c, gym.Mensual);
        Assert.Equal(HttpStatusCode.OK, (await PostWithKeyAsync(owner, $"/api/v1/memberships/{membershipC.Id}/cancel", new { reason = "Se mudó de ciudad" })).StatusCode);
        var d = await NewMemberAsync(owner, gym.Uio);
        await EnrollAsync(owner, d, gym.Mensual, pay: false);
        var e = await NewMemberAsync(owner, gym.Gye);
        await EnrollAsync(owner, e, gym.Trimestral);
        var f = await NewMemberAsync(owner, gym.Uio);
        Assert.True((await owner.PatchAsJsonAsync($"/api/v1/members/{f.Id}/status", new { isActive = false })).IsSuccessStatusCode);

        var report = await GetAsync(owner, $"summary?{Range(-9, 0)}");
        Assert.Equal(3m, Num(Kpi(report, "active_members"), "value")); // A, B y E: C canceló, D no pagó, F está inactivo
        Assert.Equal(6m, Num(Kpi(report, "new_members"), "value"));
        Assert.Equal(1m, Num(Kpi(report, "inactive_members"), "value"));
        Assert.Equal(1m, Num(Kpi(report, "members_with_balance"), "value")); // solo D debe
        Assert.Equal("NotApplicable", Kpi(report, "inactive_members").GetProperty("variation").GetProperty("state").GetString());

        var states = report.GetProperty("membershipStates").EnumerateArray().ToDictionary(x => x.GetProperty("key").GetString()!, x => x.GetProperty("count").GetInt32());
        Assert.Equal(3, states["Active"]);
        Assert.Equal(1, states["Cancelled"]);
        Assert.Equal(1, states["PendingPayment"]);
        Assert.Equal(0, states["Expired"]);
        Assert.Equal(0, states["Frozen"]);

        var byPlan = report.GetProperty("byPlan").EnumerateArray().ToDictionary(x => x.GetProperty("planName").GetString()!, x => x.GetProperty("members").GetInt32());
        Assert.Equal(2, byPlan["Mensual"]);
        Assert.Equal(1, byPlan["Trimestral"]);
        var byBranch = report.GetProperty("byBranch").EnumerateArray().ToDictionary(x => x.GetProperty("branchName").GetString()!);
        Assert.Equal(2, byBranch["Sucursal UIO"].GetProperty("active").GetInt32());
        Assert.Equal(1, byBranch["Sucursal UIO"].GetProperty("inactive").GetInt32());
        Assert.Equal(1, byBranch["Sucursal GYE"].GetProperty("active").GetInt32());

        // Filtro por plan: solo los del trimestral.
        var onlyQuarterly = await GetAsync(owner, $"summary?{Range(-9, 0)}&planId={gym.Trimestral.Id}");
        Assert.Equal(1m, Num(Kpi(onlyQuarterly, "active_members"), "value"));
    }

    [Fact]
    public async Task Un_alumno_cuya_membresia_ya_termino_deja_de_ser_activo_pero_lo_era_en_el_periodo_anterior()
    {
        var gym = await NewGymAsync();
        var owner = gym.Tenant.Owner;
        var member = await NewMemberAsync(owner, gym.Uio);
        var membership = await EnrollAsync(owner, member, gym.Mensual);

        // Contrato de hace 40 días a hace 3: vigente hace 15 días, vencido hoy.
        await MoveAsync(membership.Id, -40, -3, soldDaysAgo: 40);

        var today = await GetAsync(owner, $"summary?{Range(-9, 0)}");
        var active = Kpi(today, "active_members");
        Assert.Equal(0m, Num(active, "value"));
        Assert.Equal(1m, Num(active, "previous")); // el período anterior (-19 a -10) cierra el día -10, aún vigente
        Assert.Equal("Ok", active.GetProperty("variation").GetProperty("state").GetString());
        Assert.Equal(-100.0m, Num(active.GetProperty("variation"), "percent"));
        Assert.Equal(1, today.GetProperty("membershipStates").EnumerateArray().Single(x => x.GetProperty("key").GetString() == "Expired").GetProperty("count").GetInt32());
    }

    // ---- Retención -------------------------------------------------------------------------------------------------------

    private async Task<(Gym Gym, MemberDto R1, MemberDto R2, MemberDto R3, MemberDto R4)> RetentionScenarioAsync()
    {
        var gym = await NewGymAsync();
        var owner = gym.Tenant.Owner;

        // Cuatro alumnos activos hace 10 días (la cohorte del período «últimos 10 días»).
        var r1 = await NewMemberAsync(owner, gym.Uio);   // sigue vigente
        var m1 = await EnrollAsync(owner, r1, gym.Mensual);
        var r2 = await NewMemberAsync(owner, gym.Uio);   // su membresía terminó y no volvió
        var m2 = await EnrollAsync(owner, r2, gym.Mensual);
        var r3 = await NewMemberAsync(owner, gym.Gye);   // terminó pero renovó
        var m3 = await EnrollAsync(owner, r3, gym.Trimestral);
        var r4 = await NewMemberAsync(owner, gym.Uio);   // canceló hace 3 días
        var m4 = await EnrollAsync(owner, r4, gym.Mensual);

        await MoveAsync(m1.Id, -40, 20, soldDaysAgo: 40);
        await MoveAsync(m2.Id, -40, -5, soldDaysAgo: 40);
        await MoveAsync(m3.Id, -40, -5, soldDaysAgo: 40);
        await MoveAsync(m4.Id, -40, 20, soldDaysAgo: 40);

        // R3 renueva hoy: la nueva membresía nace de la anterior y se cobra.
        var renewal = await gym.Tenant.Owner.PostAsJsonAsync($"/api/v1/memberships/{m3.Id}/renew", new { });
        Assert.True(renewal.IsSuccessStatusCode, await renewal.Content.ReadAsStringAsync());
        await PayAsync(owner, r3.Id, gym.Trimestral.Price, "Card", "V-RENEW");

        // R4 cancela (la cancelación se fecha hace 3 días).
        Assert.Equal(HttpStatusCode.OK, (await PostWithKeyAsync(owner, $"/api/v1/memberships/{m4.Id}/cancel", new { reason = "Lesión prolongada" })).StatusCode);
        await SqlAsync($"UPDATE members.memberships SET cancelled_at = now() - interval '3 days' WHERE id = '{m4.Id}'");
        return (gym, r1, r2, r3, r4);
    }

    [Fact]
    public async Task La_retencion_se_mide_sobre_la_cohorte_y_una_cancelacion_con_otra_membresia_no_es_baja()
    {
        var (gym, _, r2, _, r4) = await RetentionScenarioAsync();
        var owner = gym.Tenant.Owner;

        var report = await GetAsync(owner, $"retention?{Range(-9, 0)}");
        Assert.Equal(D(-10), report.GetProperty("cohortDate").GetString());
        Assert.Equal(D(0), report.GetProperty("endDate").GetString());
        Assert.Equal(4, report.GetProperty("cohort").GetInt32());
        Assert.Equal(2, report.GetProperty("retained").GetInt32()); // R1 y R3 (renovó)
        Assert.Equal(2, report.GetProperty("lost").GetInt32());
        Assert.Equal(50.0m, Num(report, "retentionPercent"));

        // En el período anterior (cohorte del día -20) nadie había salido todavía: 100 %.
        Assert.Equal(100.0m, Num(report, "previousRetentionPercent"));
        Assert.Equal("Ok", report.GetProperty("variation").GetProperty("state").GetString());
        Assert.Equal(-50.0m, Num(report.GetProperty("variation"), "absolute")); // puntos porcentuales
        Assert.Equal(JsonValueKind.Null, report.GetProperty("variation").GetProperty("percent").ValueKind);

        var byBranch = report.GetProperty("byBranch").EnumerateArray().ToDictionary(x => x.GetProperty("name").GetString()!);
        Assert.Equal(3, byBranch["Sucursal UIO"].GetProperty("cohort").GetInt32());
        Assert.Equal(1, byBranch["Sucursal UIO"].GetProperty("retained").GetInt32());
        Assert.Equal(1, byBranch["Sucursal GYE"].GetProperty("cohort").GetInt32());
        Assert.Equal(100.0m, Num(byBranch["Sucursal GYE"], "retentionPercent"));
        var byPlan = report.GetProperty("byPlan").EnumerateArray().ToDictionary(x => x.GetProperty("name").GetString()!);
        Assert.Equal(3, byPlan["Mensual"].GetProperty("cohort").GetInt32());
        Assert.Equal(1, byPlan["Trimestral"].GetProperty("cohort").GetInt32());

        // Quiénes salieron: solo código, nombre, sucursal y su último plan.
        var lost = report.GetProperty("lostMembers").GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(new[] { r2.Code, r4.Code }.Order(), lost.Select(l => l.GetProperty("memberCode").GetString()!).Order());
        Assert.All(lost, l => Assert.Equal("Mensual", l.GetProperty("lastPlan").GetString()));
        Assert.DoesNotContain("phone", report.GetRawText(), StringComparison.OrdinalIgnoreCase);

        // Con filtro de plan la cohorte solo incluye a quienes tenían ese plan (mensual: R1, R2, R4).
        var monthly = await GetAsync(owner, $"retention?{Range(-9, 0)}&planId={gym.Mensual.Id}");
        Assert.Equal(3, monthly.GetProperty("cohort").GetInt32());
        Assert.Equal(1, monthly.GetProperty("retained").GetInt32());
    }

    [Fact]
    public async Task Sin_cohorte_no_hay_porcentaje_de_retencion_ni_error_de_division()
    {
        var gym = await NewGymAsync();
        var report = await GetAsync(gym.Tenant.Owner, $"retention?{Range(-9, 0)}");

        Assert.Equal(0, report.GetProperty("cohort").GetInt32());
        Assert.Equal(JsonValueKind.Null, report.GetProperty("retentionPercent").ValueKind);
        Assert.Equal("NoData", report.GetProperty("variation").GetProperty("state").GetString());
        Assert.Equal(0, report.GetProperty("lostMembers").GetProperty("totalCount").GetInt32());
    }

    // ---- Altas, inscripciones y bajas ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Las_altas_se_clasifican_en_nuevas_renovaciones_y_reincorporaciones_con_su_tendencia_mensual()
    {
        var gym = await NewGymAsync();
        var owner = gym.Tenant.Owner;

        var n1 = await NewMemberAsync(owner, gym.Uio);   // inscripción nueva hoy
        var membership1 = await EnrollAsync(owner, n1, gym.Mensual);
        var n2 = await NewMemberAsync(owner, gym.Uio);   // inscripción nueva hace 35 días
        var membership2 = await EnrollAsync(owner, n2, gym.Mensual);
        await MoveAsync(membership2.Id, -35, -5, soldDaysAgo: 35);
        var renewal = await owner.PostAsJsonAsync($"/api/v1/memberships/{membership1.Id}/renew", new { }); // renovación hoy (de n1)
        Assert.True(renewal.IsSuccessStatusCode, await renewal.Content.ReadAsStringAsync());
        var old = await NewMemberAsync(owner, gym.Gye);  // tuvo una membresía hace 100 días y vuelve hoy sin renovar: reincorporación
        var oldMembership = await EnrollAsync(owner, old, gym.Mensual);
        await MoveAsync(oldMembership.Id, -100, -70, soldDaysAgo: 100);
        await EnrollAsync(owner, old, gym.Mensual);

        var report = await GetAsync(owner, $"acquisitions?{Range(-40, 0)}");
        Assert.Equal(2m, Num(Kpi(report, "new_enrollments"), "value")); // n1 (hoy) y n2 (hace 35 días); la de hace 100 días queda fuera
        Assert.Equal(1m, Num(Kpi(report, "renewals"), "value"));
        Assert.Equal(1m, Num(Kpi(report, "reincorporations"), "value"));
        Assert.Equal(3m, Num(Kpi(report, "gains"), "value")); // nuevas + reincorporaciones

        // La tendencia parte el período por mes calendario y suma lo mismo que el total.
        var trend = report.GetProperty("trend").EnumerateArray().ToList();
        Assert.True(trend.Count >= 2);
        Assert.Equal(2, trend.Sum(t => t.GetProperty("newEnrollments").GetInt32()));
        Assert.Equal(1, trend.Sum(t => t.GetProperty("renewals").GetInt32()));
        Assert.Equal(D(-40), trend.First().GetProperty("from").GetString());
        Assert.Equal(D(0), trend.Last().GetProperty("to").GetString());
        Assert.All(trend, t => Assert.Equal(DateOnly.Parse(t.GetProperty("from").GetString()!).Month, DateOnly.Parse(t.GetProperty("to").GetString()!).Month));

        // Historial de membresías vendidas, filtrable por tipo.
        Assert.Equal(4, report.GetProperty("memberships").GetProperty("totalCount").GetInt32());
        var renewals = await GetAsync(owner, $"acquisitions?{Range(-40, 0)}&kind=Renewal");
        var row = renewals.GetProperty("memberships").GetProperty("items").EnumerateArray().Single();
        Assert.Equal("Renewal", row.GetProperty("kind").GetString());
        Assert.Equal(n1.Code, row.GetProperty("memberCode").GetString());
        Assert.Equal(1, (await GetAsync(owner, $"acquisitions?{Range(-40, 0)}&kind=Reincorporation")).GetProperty("memberships").GetProperty("totalCount").GetInt32());
        Assert.Equal(2, (await GetAsync(owner, $"acquisitions?{Range(-40, 0)}&kind=New")).GetProperty("memberships").GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task Las_bajas_de_la_tendencia_cuadran_con_la_retencion_del_mismo_tramo()
    {
        var (gym, _, _, _, _) = await RetentionScenarioAsync();
        var report = await GetAsync(gym.Tenant.Owner, $"acquisitions?{Range(-9, 0)}");

        // Un solo tramo (o dos, si el mes cambia): las bajas del total son las de la retención (2) y la tendencia no pierde ninguna.
        Assert.Equal(2m, Num(Kpi(report, "losses"), "value"));
        Assert.True(report.GetProperty("trend").EnumerateArray().Sum(t => t.GetProperty("losses").GetInt32()) >= 2);
    }

    // ---- Renovaciones ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task La_tasa_de_renovacion_se_calcula_sobre_las_membresias_que_vencieron_en_el_periodo()
    {
        var gym = await NewGymAsync();
        var owner = gym.Tenant.Owner;
        var people = new List<(MemberDto Member, MembershipDto Membership)>();
        for (var i = 0; i < 5; i++)
        {
            var member = await NewMemberAsync(owner, i == 4 ? gym.Gye : gym.Uio);
            var membership = await EnrollAsync(owner, member, i == 4 ? gym.Trimestral : gym.Mensual, pay: i != 3);
            people.Add((member, membership));
        }

        // P0 renueva; P1 no renueva; P2 renueva pero cancela la renovación; P3 nunca pagó (no cuenta); P4 (trimestral, Guayaquil) no renueva.
        foreach (var (_, membership) in people.Take(5))
        {
            await MoveAsync(membership.Id, -40, -3, soldDaysAgo: 40);
        }

        var renewed = await owner.PostAsJsonAsync($"/api/v1/memberships/{people[0].Membership.Id}/renew", new { });
        Assert.True(renewed.IsSuccessStatusCode, await renewed.Content.ReadAsStringAsync());
        var second = await owner.PostAsJsonAsync($"/api/v1/memberships/{people[2].Membership.Id}/renew", new { });
        Assert.True(second.IsSuccessStatusCode, await second.Content.ReadAsStringAsync());
        var secondId = (await second.Content.ReadFromJsonAsync<MembershipDto>())!.Id;
        Assert.Equal(HttpStatusCode.OK, (await PostWithKeyAsync(owner, $"/api/v1/memberships/{secondId}/cancel", new { reason = "Cambió de opinión" })).StatusCode);

        var report = await GetAsync(owner, $"renewals?{Range(-9, 0)}");
        Assert.Equal(4, report.GetProperty("due").GetInt32()); // P0, P1, P2 y P4 (P3 nunca se activó)
        Assert.Equal(1, report.GetProperty("renewed").GetInt32()); // solo P0: la renovación cancelada de P2 no cuenta
        Assert.Equal(3, report.GetProperty("notRenewed").GetInt32());
        Assert.Equal(25.0m, Num(report, "ratePercent"));
        Assert.Equal(JsonValueKind.Null, report.GetProperty("previousRatePercent").ValueKind);
        Assert.Equal("NoBaseline", report.GetProperty("variation").GetProperty("state").GetString());

        var byPlan = report.GetProperty("byPlan").EnumerateArray().ToDictionary(x => x.GetProperty("name").GetString()!);
        Assert.Equal(3, byPlan["Mensual"].GetProperty("due").GetInt32());
        Assert.Equal(1, byPlan["Mensual"].GetProperty("renewed").GetInt32());
        Assert.Equal(0, byPlan["Trimestral"].GetProperty("renewed").GetInt32());
        var notRenewed = report.GetProperty("notRenewedMembers").GetProperty("items").EnumerateArray().Select(x => x.GetProperty("memberCode").GetString()).ToList();
        Assert.Equal(3, notRenewed.Count);
        Assert.DoesNotContain(people[0].Member.Code, notRenewed);
        Assert.Contains(people[2].Member.Code, notRenewed);

        // Filtro de plan y de sucursal.
        Assert.Equal(1, (await GetAsync(owner, $"renewals?{Range(-9, 0)}&planId={gym.Trimestral.Id}")).GetProperty("due").GetInt32());
        Assert.Equal(1, (await GetAsync(owner, $"renewals?{Range(-9, 0)}&branchId={gym.Gye}")).GetProperty("due").GetInt32());

        // Un período sin vencimientos: sin tasa.
        var empty = await GetAsync(owner, $"renewals?{Range(-90, -60)}");
        Assert.Equal(0, empty.GetProperty("due").GetInt32());
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("ratePercent").ValueKind);
    }

    // ---- Próximos vencimientos ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Los_proximos_vencimientos_se_agrupan_por_cercania_e_indican_si_ya_renovaron_y_cuanto_deben()
    {
        var gym = await NewGymAsync();
        var owner = gym.Tenant.Owner;
        var ends = new[] { 3, 10, 25, 80 };
        var memberships = new List<(MemberDto Member, MembershipDto Membership)>();
        foreach (var end in ends)
        {
            var member = await NewMemberAsync(owner, gym.Uio);
            var membership = await EnrollAsync(owner, member, gym.Mensual);
            await MoveAsync(membership.Id, end - 30, end, soldDaysAgo: 5);
            memberships.Add((member, membership));
        }

        // El de 3 días ya renovó; el de 10 debe una camiseta de 20.
        var renewal = await owner.PostAsJsonAsync($"/api/v1/memberships/{memberships[0].Membership.Id}/renew", new { });
        Assert.True(renewal.IsSuccessStatusCode, await renewal.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync("/api/v1/payment-obligations", new { memberId = memberships[1].Member.Id, concept = "Camiseta", amount = 20m, taxKind = "General" })).StatusCode);

        var report = await GetAsync(owner, "expirations?days=30");
        Assert.Equal(3, report.GetProperty("total").GetInt32()); // 3, 10 y 25 días; el de 80 queda fuera
        Assert.Equal(1, report.GetProperty("alreadyRenewed").GetInt32());
        var buckets = report.GetProperty("buckets").EnumerateArray().ToDictionary(b => b.GetProperty("key").GetString()!, b => b.GetProperty("count").GetInt32());
        Assert.Equal(1, buckets["0-7"]);
        Assert.Equal(1, buckets["8-15"]);
        Assert.Equal(1, buckets["16-30"]);
        Assert.False(buckets.ContainsKey("31-60"));

        var rows = report.GetProperty("memberships").GetProperty("items").EnumerateArray().ToList();
        Assert.Equal([3, 10, 25], rows.Select(r => r.GetProperty("daysLeft").GetInt32()));
        Assert.True(rows[0].GetProperty("alreadyRenewed").GetBoolean());
        Assert.Equal(20m, Num(rows[1], "balance"));
        Assert.Equal(0m, Num(rows[2], "balance"));

        Assert.Equal(2, (await GetAsync(owner, "expirations?days=30&hideRenewed=true")).GetProperty("memberships").GetProperty("totalCount").GetInt32());
        Assert.Equal(4, (await GetAsync(owner, "expirations?days=90")).GetProperty("total").GetInt32());

        var invalid = await owner.GetAsync("/api/v1/reports/members/expirations?days=45");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        Assert.Equal("analytics.expiration_days_invalid", await TestApi.ProblemCodeAsync(invalid));
    }

    // ---- Seguridad --------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Los_reportes_de_alumnos_exigen_su_permiso_y_se_acotan_a_la_sucursal_y_al_gimnasio()
    {
        var a = await NewGymAsync();
        var b = await NewGymAsync();
        var memberA = await NewMemberAsync(a.Tenant.Owner, a.Uio);
        await EnrollAsync(a.Tenant.Owner, memberA, a.Mensual);
        var memberG = await NewMemberAsync(a.Tenant.Owner, a.Gye);
        await EnrollAsync(a.Tenant.Owner, memberG, a.Mensual);
        var memberB = await NewMemberAsync(b.Tenant.Owner, b.Uio);
        await EnrollAsync(b.Tenant.Owner, memberB, b.Mensual);

        string[] reports = ["summary", "acquisitions", "retention", "renewals", "expirations"];
        var cashier = await StaffAsync(a, "cajero@gym.test", "Cajero", a.Uio);
        var reception = await StaffAsync(a, "recep@gym.test", "Recepcion", a.Uio);
        foreach (var report in reports)
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync($"/api/v1/reports/members/{report}")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await reception.GetAsync($"/api/v1/reports/members/{report}")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().GetAsync($"/api/v1/reports/members/{report}")).StatusCode);
        }

        // El gerente de Quito solo cuenta a los de Quito; Guayaquil se rechaza.
        var manager = await StaffAsync(a, "gerente@gym.test", "AdministradorSucursal", a.Uio);
        Assert.Equal(1m, Num(Kpi(await GetAsync(manager, $"summary?{Range(-9, 0)}"), "active_members"), "value"));
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync($"/api/v1/reports/members/summary?{Range(-9, 0)}&branchId={a.Gye}")).StatusCode);

        // El otro gimnasio no existe para este propietario: ni su sucursal ni su plan.
        Assert.Equal(2m, Num(Kpi(await GetAsync(a.Tenant.Owner, $"summary?{Range(-9, 0)}"), "active_members"), "value"));
        Assert.Equal(HttpStatusCode.Forbidden, (await a.Tenant.Owner.GetAsync($"/api/v1/reports/members/summary?branchId={b.Uio}")).StatusCode);
        Assert.Equal(0m, Num(Kpi(await GetAsync(a.Tenant.Owner, $"summary?{Range(-9, 0)}&planId={b.Mensual.Id}"), "active_members"), "value"));

        var accountant = await StaffAsync(a, "contador@gym.test", "Contador", a.Uio);
        Assert.Equal(2m, Num(Kpi(await GetAsync(accountant, $"summary?{Range(-9, 0)}"), "active_members"), "value"));
    }
}
