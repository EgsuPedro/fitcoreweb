using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FitCore.Api.IntegrationTests.Infrastructure;
using static FitCore.Api.IntegrationTests.Infrastructure.CommercialTestApi;

namespace FitCore.Api.IntegrationTests;

/// <summary>Comparación entre sucursales (bloque 7): mismas definiciones y período para todas, ceros visibles y permisos por área.</summary>
[Collection(ApiCollection2.Name)]
public class AnalyticsBranchComparisonTests(ApiFixture api)
{
    private static readonly TimeSpan Ecuador = TimeSpan.FromHours(-5);
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.Add(Ecuador));

    private static string Range(int fromDays, int toDays) => $"preset=Custom&from={Today.AddDays(fromDays):yyyy-MM-dd}&to={Today.AddDays(toDays):yyyy-MM-dd}";

    private sealed record Ref(Guid Id);

    private sealed record Gym(TestTenant Tenant, Guid Uio, Guid Gye, Guid Cue, PlanDto Mensual, PlanDto Trimestral);

    private async Task<Gym> NewGymAsync()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var uio = await TestApi.CreateBranchAsync(tenant.Owner, "UIO");
        var gye = await TestApi.CreateBranchAsync(tenant.Owner, "GYE");
        var cue = await TestApi.CreateBranchAsync(tenant.Owner, "CUE");
        return new Gym(tenant, uio, gye, cue, await CreatePlanAsync(tenant.Owner, "Mensual", 30m), await CreatePlanAsync(tenant.Owner, "Trimestral", 80m, durationValue: 3));
    }

    private static async Task<JsonElement> GetAsync(HttpClient client, string query)
    {
        var response = await client.GetAsync($"/api/v1/reports/branches/comparison?{query}");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Respondió {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static decimal Num(JsonElement element, string property) => element.GetProperty(property).GetDecimal();

    private static JsonElement Row(JsonElement report, string branchName) => report.GetProperty("branches").EnumerateArray().Single(b => b.GetProperty("branchName").GetString() == branchName);

    private static int _members;

    private static async Task<MemberDto> NewMemberAsync(HttpClient owner, Guid branch)
    {
        var n = Interlocked.Increment(ref _members);
        var response = await owner.PostAsJsonAsync("/api/v1/members", new
        {
            branchId = branch, firstName = $"Socio{n}", lastName = "Comparacion", idType = "Cedula", idNumber = MemberTests.NewCedula(), birthDate = "1992-03-04",
            email = $"c{n}.{Guid.NewGuid():N}"[..24] + "@alumnos.test", phone = "0991234567"
        });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<MemberDto>())!;
    }

    private static async Task<MembershipDto> EnrollAsync(HttpClient owner, MemberDto member, PlanDto plan)
    {
        var response = await owner.PostAsJsonAsync("/api/v1/memberships", new { memberId = member.Id, planId = plan.Id });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<MembershipDto>())!;
    }

    private async Task<HttpClient> StaffAsync(Gym gym, string email, string role, params Guid[] branches)
    {
        await TestApi.CreateUserAsync(gym.Tenant.Owner, email, role, branches);
        return TestApi.Authenticated(api, await TestApi.LoginAsync(api, gym.Tenant.Slug, email, TestApi.UserPassword));
    }

    /// <summary>Quito y Guayaquil con actividad distinta; Cuenca queda sin ninguna.</summary>
    private async Task<Gym> BuildAsync()
    {
        var gym = await NewGymAsync();
        var owner = gym.Tenant.Owner;

        // Quito: A paga completo (30); B (trimestral) abona 10 hace 15 días y 20 hoy.
        var a = await NewMemberAsync(owner, gym.Uio);
        await EnrollAsync(owner, a, gym.Mensual);
        await PayAsync(owner, a.Id, 30m, "Card", "V-A");
        var b = await NewMemberAsync(owner, gym.Uio);
        await EnrollAsync(owner, b, gym.Trimestral);
        Assert.Equal(HttpStatusCode.Created, (await PostPaymentAsync(owner, PaymentBody(b.Id, 10m, "Card", "V-B1", paidAt: DateTimeOffset.UtcNow.AddDays(-15)))).StatusCode);
        await PayAsync(owner, b.Id, 20m, "Card", "V-B2");

        // Guayaquil: C paga y renueva; D se inscribe y no paga.
        var c = await NewMemberAsync(owner, gym.Gye);
        var membershipC = await EnrollAsync(owner, c, gym.Mensual);
        await PayAsync(owner, c.Id, 30m, "Card", "V-C");
        var renewal = await owner.PostAsJsonAsync($"/api/v1/memberships/{membershipC.Id}/renew", new { });
        Assert.True(renewal.IsSuccessStatusCode, await renewal.Content.ReadAsStringAsync());
        var d = await NewMemberAsync(owner, gym.Gye);
        await EnrollAsync(owner, d, gym.Mensual);

        // Una entrada en cada sucursal con actividad.
        Assert.Equal(HttpStatusCode.Created, (await CheckInAsync(owner, gym.Uio, a.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await CheckInAsync(owner, gym.Gye, c.Id)).StatusCode);

        // Una clase en Quito: cupo 2 con 1 reserva confirmada.
        var user = await TestApi.CreateUserAsync(owner, "carla@gym.test", "Entrenador", gym.Uio);
        var trainer = (await (await owner.PostAsJsonAsync("/api/v1/trainers", new { userId = user.Id, specialties = new[] { "Yoga" } })).Content.ReadFromJsonAsync<Ref>(Web))!;
        var group = await owner.PostAsJsonAsync("/api/v1/group-classes", new
        {
            branchId = gym.Uio, name = "Yoga", activity = "Yoga", instructorId = trainer.Id, durationMinutes = 60, capacity = 2, level = "AllLevels",
            minBookingNoticeMinutes = 0, maxBookingAdvanceDays = 30, cancellationDeadlineMinutes = 0, waitlistEnabled = false, waitlistCapacity = 0,
            offerWindowMinutes = 30, requireActiveMembership = false, applyPaymentPolicy = false
        });
        Assert.Equal(HttpStatusCode.Created, group.StatusCode);
        var classId = (await group.Content.ReadFromJsonAsync<Ref>(Web))!.Id;
        var startsAt = new DateTimeOffset(DateTimeOffset.UtcNow.ToOffset(Ecuador).Date.AddDays(1).AddHours(9), Ecuador);
        var session = (await (await owner.PostAsJsonAsync("/api/v1/class-sessions", new { classId, startsAt })).Content.ReadFromJsonAsync<Ref>(Web))!;
        Assert.Equal(HttpStatusCode.Created, (await PostWithKeyAsync(owner, "/api/v1/class-reservations", new { sessionId = session.Id, memberId = a.Id, joinWaitlist = false })).StatusCode);
        return gym;
    }

    [Fact]
    public async Task Las_sucursales_se_comparan_con_las_mismas_definiciones_y_la_que_no_tiene_actividad_aparece_sin_datos()
    {
        var gym = await BuildAsync();
        var owner = gym.Tenant.Owner;
        var report = await GetAsync(owner, Range(-9, 7));

        // Cobros: Quito 50 (el abono de hace 15 días queda en el período anterior), Guayaquil 30, Cuenca 0.
        var uio = Row(report, "Sucursal UIO");
        var gye = Row(report, "Sucursal GYE");
        var cue = Row(report, "Sucursal CUE");
        Assert.Equal(50m, Num(uio.GetProperty("financial"), "collected"));
        Assert.Equal(10m, Num(uio.GetProperty("financial"), "previousCollected"));
        Assert.Equal(400.0m, Num(uio.GetProperty("financial").GetProperty("collectedVariation"), "percent"));
        Assert.Equal(62.5m, Num(uio.GetProperty("financial"), "collectedShare"));
        Assert.Equal(30m, Num(gye.GetProperty("financial"), "collected"));
        Assert.Equal("NoBaseline", gye.GetProperty("financial").GetProperty("collectedVariation").GetProperty("state").GetString());
        Assert.Equal(37.5m, Num(gye.GetProperty("financial"), "collectedShare"));
        Assert.Equal(0m, Num(cue.GetProperty("financial"), "collected"));

        // Saldos de hoy: B debe 50 en Quito; D debe 30 y la renovación de C otros 30 en Guayaquil.
        Assert.Equal(50m, Num(uio.GetProperty("financial"), "outstanding"));
        Assert.Equal(60m, Num(gye.GetProperty("financial"), "outstanding"));

        // Alumnos: activos al cierre, inscripciones nuevas y renovaciones.
        Assert.Equal(2, uio.GetProperty("members").GetProperty("activeMembers").GetInt32());
        Assert.Equal(1, gye.GetProperty("members").GetProperty("activeMembers").GetInt32());
        Assert.Equal(2, uio.GetProperty("members").GetProperty("newEnrollments").GetInt32());
        Assert.Equal(2, gye.GetProperty("members").GetProperty("newEnrollments").GetInt32());
        Assert.Equal(1, gye.GetProperty("members").GetProperty("renewals").GetInt32());
        Assert.Equal(0, uio.GetProperty("members").GetProperty("renewals").GetInt32());

        // Operación: visitas, ocupación ponderada (solo Quito tiene clases) y cero explícito en las demás.
        Assert.Equal(1, uio.GetProperty("operations").GetProperty("visits").GetInt32());
        Assert.Equal(1, gye.GetProperty("operations").GetProperty("visits").GetInt32());
        Assert.Equal(50.0m, Num(uio.GetProperty("operations"), "occupancyPercent"));
        Assert.Equal(2, uio.GetProperty("operations").GetProperty("capacity").GetInt32());
        Assert.Equal(JsonValueKind.Null, gye.GetProperty("operations").GetProperty("occupancyPercent").ValueKind);

        // La sucursal sin actividad no se oculta: está, en cero y marcada sin datos.
        Assert.False(cue.GetProperty("hasActivity").GetBoolean());
        Assert.True(uio.GetProperty("hasActivity").GetBoolean());
        Assert.Equal(3, report.GetProperty("branches").GetArrayLength());
        Assert.Equal(["Sucursal UIO", "Sucursal GYE", "Sucursal CUE"], report.GetProperty("branches").EnumerateArray().Select(b => b.GetProperty("branchName").GetString()));

        // El total usa las mismas cuentas: sumas y la ocupación sobre las sumas.
        var total = report.GetProperty("total");
        Assert.Equal(80m, Num(total.GetProperty("financial"), "collected"));
        Assert.Equal(110m, Num(total.GetProperty("financial"), "outstanding"));
        Assert.Equal(3, total.GetProperty("members").GetProperty("activeMembers").GetInt32());
        Assert.Equal(2, total.GetProperty("operations").GetProperty("visits").GetInt32());
        Assert.Equal(50.0m, Num(total.GetProperty("operations"), "occupancyPercent"));
        Assert.Equal(0, report.GetProperty("omittedSections").GetArrayLength());

        // Debe coincidir con los otros reportes: el total cobrado del dashboard y el reparto por sucursal de cobros.
        var dashboard = JsonDocument.Parse(await owner.GetStringAsync($"/api/v1/analytics/dashboard?{Range(-9, 7)}")).RootElement;
        var collected = dashboard.GetProperty("financial").GetProperty("kpis").EnumerateArray().Single(k => k.GetProperty("key").GetString() == "collected");
        Assert.Equal(80m, collected.GetProperty("value").GetDecimal());
        var collections = JsonDocument.Parse(await owner.GetStringAsync($"/api/v1/reports/financial/collections?{Range(-9, 7)}")).RootElement;
        var byBranch = collections.GetProperty("byBranch").EnumerateArray().ToDictionary(b => b.GetProperty("branchName").GetString()!, b => b.GetProperty("amount").GetDecimal());
        Assert.Equal(byBranch["Sucursal UIO"], Num(uio.GetProperty("financial"), "collected"));
        Assert.Equal(byBranch["Sucursal GYE"], Num(gye.GetProperty("financial"), "collected"));
    }

    [Fact]
    public async Task La_evolucion_mensual_suma_lo_mismo_que_el_periodo_y_se_puede_elegir_un_subconjunto()
    {
        var gym = await BuildAsync();
        var owner = gym.Tenant.Owner;
        var report = await GetAsync(owner, Range(-40, 0));

        var months = report.GetProperty("months").EnumerateArray().ToList();
        Assert.True(months.Count >= 2);
        Assert.Equal(Today.AddDays(-40).ToString("yyyy-MM-dd"), months.First().GetProperty("from").GetString());
        Assert.Equal(Today.ToString("yyyy-MM-dd"), months.Last().GetProperty("to").GetString());

        var evolution = report.GetProperty("evolution").EnumerateArray().ToDictionary(e => e.GetProperty("branchName").GetString()!);
        Assert.Equal(3, evolution.Count);
        Assert.All(evolution.Values, e => Assert.Equal(months.Count, e.GetProperty("collected").GetArrayLength()));
        var uioMonths = evolution["Sucursal UIO"].GetProperty("collected").EnumerateArray().Select(v => v.GetDecimal()).ToList();
        Assert.Equal(60m, uioMonths.Sum()); // 30 + 10 (hace 15 días, ahora dentro del rango) + 20
        Assert.Equal(Num(Row(report, "Sucursal UIO").GetProperty("financial"), "collected"), uioMonths.Sum());
        Assert.Equal(0m, evolution["Sucursal CUE"].GetProperty("collected").EnumerateArray().Sum(v => v.GetDecimal()));
        Assert.Equal(2, evolution["Sucursal GYE"].GetProperty("newEnrollments").EnumerateArray().Sum(v => v.GetInt32()));
        Assert.Equal(1, evolution["Sucursal UIO"].GetProperty("visits").EnumerateArray().Sum(v => v.GetInt32()));

        // Solo dos sucursales: el total no incluye a la tercera.
        var subset = await GetAsync(owner, $"{Range(-9, 7)}&branchId={gym.Uio}&branchId={gym.Gye}");
        Assert.Equal(2, subset.GetProperty("branches").GetArrayLength());
        Assert.Equal(80m, Num(subset.GetProperty("total").GetProperty("financial"), "collected"));
        var onlyGye = await GetAsync(owner, $"{Range(-9, 7)}&branchId={gym.Gye}");
        Assert.Equal(30m, Num(onlyGye.GetProperty("total").GetProperty("financial"), "collected"));
        Assert.Equal(100.0m, Num(onlyGye.GetProperty("branches")[0].GetProperty("financial"), "collectedShare"));
    }

    [Fact]
    public async Task Sin_ninguna_actividad_todas_las_sucursales_salen_en_cero_sin_errores()
    {
        var gym = await NewGymAsync();
        var report = await GetAsync(gym.Tenant.Owner, Range(-9, 0));

        Assert.Equal(3, report.GetProperty("branches").GetArrayLength());
        Assert.All(report.GetProperty("branches").EnumerateArray(), b =>
        {
            Assert.False(b.GetProperty("hasActivity").GetBoolean());
            Assert.Equal(0m, Num(b.GetProperty("financial"), "collected"));
            Assert.Equal(JsonValueKind.Null, b.GetProperty("financial").GetProperty("collectedShare").ValueKind);
            Assert.Equal(JsonValueKind.Null, b.GetProperty("operations").GetProperty("occupancyPercent").ValueKind);
        });
        Assert.False(report.GetProperty("total").GetProperty("hasActivity").GetBoolean());
    }

    [Fact]
    public async Task Cada_rol_compara_solo_sus_sucursales_y_solo_las_areas_que_puede_ver()
    {
        var gym = await BuildAsync();
        var other = await NewGymAsync();

        // El contador compara todas las sucursales pero no tiene el permiso de operación: esa sección no viaja.
        var accountant = await StaffAsync(gym, "contador@gym.test", "Contador", gym.Uio);
        var accountantView = await GetAsync(accountant, Range(-9, 7));
        Assert.Equal(3, accountantView.GetProperty("branches").GetArrayLength());
        Assert.Equal(["operations"], accountantView.GetProperty("omittedSections").EnumerateArray().Select(s => s.GetString()));
        Assert.All(accountantView.GetProperty("branches").EnumerateArray(), b =>
        {
            Assert.Equal(JsonValueKind.Object, b.GetProperty("financial").ValueKind);
            Assert.Equal(JsonValueKind.Null, b.GetProperty("operations").ValueKind);
        });
        Assert.All(accountantView.GetProperty("evolution").EnumerateArray(), e => Assert.Equal(JsonValueKind.Null, e.GetProperty("visits").ValueKind));

        // El gerente de Quito solo se compara a sí mismo; Guayaquil se rechaza.
        var manager = await StaffAsync(gym, "gerente@gym.test", "AdministradorSucursal", gym.Uio);
        var managerView = await GetAsync(manager, Range(-9, 7));
        Assert.Equal(1, managerView.GetProperty("branches").GetArrayLength());
        Assert.Equal(50m, Num(managerView.GetProperty("total").GetProperty("financial"), "collected"));
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync($"/api/v1/reports/branches/comparison?branchId={gym.Gye}")).StatusCode);

        // Sin el permiso de comparar (cajero, recepción, entrenador) o sin sesión: nada. Y el otro gimnasio no existe.
        foreach (var (email, role) in new[] { ("cajero@gym.test", "Cajero"), ("recep@gym.test", "Recepcion"), ("entrenador@gym.test", "Entrenador") })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await (await StaffAsync(gym, email, role, gym.Uio)).GetAsync("/api/v1/reports/branches/comparison")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().GetAsync("/api/v1/reports/branches/comparison")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await gym.Tenant.Owner.GetAsync($"/api/v1/reports/branches/comparison?branchId={other.Uio}")).StatusCode);
        var invalid = await gym.Tenant.Owner.GetAsync("/api/v1/reports/branches/comparison?preset=Custom&from=2026-03-10&to=2026-03-01");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
    }
}
