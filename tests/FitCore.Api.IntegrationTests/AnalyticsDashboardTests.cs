using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FitCore.Api.IntegrationTests.Infrastructure;
using Npgsql;
using static FitCore.Api.IntegrationTests.Infrastructure.CommercialTestApi;

namespace FitCore.Api.IntegrationTests;

/// <summary>El dashboard (bloque 3): cada indicador contra datos reales en PostgreSQL, con sus reglas de fechas, reversos y alcance.</summary>
[Collection(ApiCollection4.Name)]
public class AnalyticsDashboardTests(ApiFixture api)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record VariationRow(string State, decimal? Absolute, decimal? Percent);

    private sealed record KpiRow(string Key, string Unit, string? Currency, decimal? Value, decimal? Previous, VariationRow Variation, bool IsEmpty, string Basis, string Name, string Definition);

    private sealed record MethodRow(string Method, decimal Amount, int Count, decimal? Share);

    private sealed record SessionsRow(int Held, int Cancelled, int Upcoming, int PendingClosure);

    private sealed record FinancialRow(KpiRow[] Kpis, MethodRow[] Methods);

    private sealed record KpisRow(KpiRow[] Kpis);

    private sealed record KpisWithSessionsRow(KpiRow[] Kpis, SessionsRow Sessions);

    private sealed record DashboardRow(
        string Currency, string TimeZone, string Today, FinancialRow? Financial, KpisRow? Members, KpisWithSessionsRow? Operations, KpisWithSessionsRow? Instructor,
        string[] OmittedSections);

    private sealed record SeriesPeriodRow(string[] Starts, decimal[] Values);

    private sealed record SeriesRow(string Key, string Unit, SeriesPeriodRow Current, SeriesPeriodRow Previous);

    private sealed record SeriesResponse(int BucketDays, SeriesRow[] Series);

    private sealed record SessionRow(Guid Id, DateTimeOffset StartsAt, int Capacity);

    private sealed record TrainerRow(Guid Id);

    private sealed record ClassRow(Guid Id);

    private static readonly TimeSpan Ecuador = TimeSpan.FromHours(-5);

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.Add(Ecuador));

    /// <summary>Período personalizado «últimos N días, hoy incluido»; su comparable son los N días anteriores.</summary>
    private static string Last(int days) => $"preset=Custom&from={Today.AddDays(-(days - 1)):yyyy-MM-dd}&to={Today:yyyy-MM-dd}";

    private async Task<DashboardRow> DashboardAsync(HttpClient client, string query)
    {
        var response = await client.GetAsync($"/api/v1/analytics/dashboard?{query}");
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Respondió {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<DashboardRow>(Json))!;
    }

    private static KpiRow Kpi(IEnumerable<KpiRow> kpis, string key) => kpis.Single(k => k.Key == key);

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
            branchId = branch,
            firstName = $"Alumno{n}",
            lastName = "Analitica",
            idType = "Cedula",
            idNumber = MemberTests.NewCedula(),
            birthDate = "1995-04-12",
            email = $"a{n}.{Guid.NewGuid():N}"[..24] + "@alumnos.test",
            phone = "0991234567"
        });
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"Respondió {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<MemberDto>())!;
    }

    private static async Task<MembershipDto> EnrollAsync(HttpClient owner, MemberDto member, PlanDto plan)
    {
        var response = await owner.PostAsJsonAsync("/api/v1/memberships", new { memberId = member.Id, planId = plan.Id });
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"Respondió {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<MembershipDto>())!;
    }

    private static async Task<ObligationDto> ExtraObligationAsync(HttpClient owner, MemberDto member, decimal amount)
    {
        var response = await owner.PostAsJsonAsync("/api/v1/payment-obligations", new { memberId = member.Id, concept = "Producto", amount, taxKind = "General" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ObligationDto>())!;
    }

    private static async Task ReverseAsync(HttpClient owner, Guid paymentId) =>
        Assert.Equal(HttpStatusCode.OK, (await PostWithKeyAsync(owner, $"/api/v1/payments/{paymentId}/reverse", new { reason = "Cobro duplicado en caja" })).StatusCode);

    private sealed record Gym(TestTenant Tenant, Guid Uio, Guid Gye, PlanDto Plan);

    private async Task<Gym> NewGymAsync()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var uio = await TestApi.CreateBranchAsync(tenant.Owner, "UIO");
        var gye = await TestApi.CreateBranchAsync(tenant.Owner, "GYE");
        var plan = await CreatePlanAsync(tenant.Owner, "Mensual", 30m);

        // Los cobros en efectivo exigen una caja abierta en la sucursal del alumno (Quito).
        await OpenOkAsync(tenant.Owner, (await CreateRegisterAsync(tenant.Owner, uio)).Id);
        return new Gym(tenant, uio, gye, plan);
    }

    private async Task<HttpClient> StaffAsync(Gym gym, string email, string role, params Guid[] branches)
    {
        await TestApi.CreateUserAsync(gym.Tenant.Owner, email, role, branches);
        return TestApi.Authenticated(api, await TestApi.LoginAsync(api, gym.Tenant.Slug, email, TestApi.UserPassword));
    }

    // ---- Finanzas ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task El_cobrado_cuenta_solo_pagos_confirmados_del_periodo_y_separa_por_verificar_y_revertidos()
    {
        var gym = await NewGymAsync();
        var owner = gym.Tenant.Owner;

        // A paga completo en efectivo; B abona en dos pagos de tarjeta; C paga y se revierte; D transfiere (sin verificar); E paga en otra sucursal.
        var a = await NewMemberAsync(owner, gym.Uio);
        await EnrollAsync(owner, a, gym.Plan);
        await PayAsync(owner, a.Id, 30m, "Cash", null);
        var b = await NewMemberAsync(owner, gym.Uio);
        await EnrollAsync(owner, b, gym.Plan);
        await PayAsync(owner, b.Id, 10m, "Card", "VCH-1");
        await PayAsync(owner, b.Id, 5m, "Card", "VCH-2");
        var c = await NewMemberAsync(owner, gym.Uio);
        await EnrollAsync(owner, c, gym.Plan);
        await ReverseAsync(owner, (await PayAsync(owner, c.Id, 30m, "Card", "VCH-3")).Id);
        var d = await NewMemberAsync(owner, gym.Uio);
        await EnrollAsync(owner, d, gym.Plan);
        await PayAsync(owner, d.Id, 30m, "BankTransfer", "TRF-1");
        var e = await NewMemberAsync(owner, gym.Gye);
        await EnrollAsync(owner, e, gym.Plan);
        await PayAsync(owner, e.Id, 30m, "Card", "VCH-4");

        var dashboard = await DashboardAsync(owner, Last(10));
        var financial = dashboard.Financial!;

        // Cobrado = 30 + 10 + 5 + 30(E): el revertido y el pendiente de verificar no cuentan.
        var collected = Kpi(financial.Kpis, "collected");
        Assert.Equal(75m, collected.Value);
        Assert.Equal("USD", collected.Currency);
        Assert.Equal("Currency", collected.Unit);
        Assert.Equal("Fecha de pago", collected.Basis);
        Assert.Equal(30m, Kpi(financial.Kpis, "collected_cash").Value);
        Assert.Equal(45m, Kpi(financial.Kpis, "collected_other").Value);
        Assert.Equal(30m, Kpi(financial.Kpis, "pending_verification").Value);
        Assert.Equal(30m, Kpi(financial.Kpis, "reversed").Value);
        Assert.Equal(150m, Kpi(financial.Kpis, "issued").Value);

        // El período anterior no tiene nada: sin base de comparación, no un porcentaje engañoso.
        Assert.Equal(0m, collected.Previous);
        Assert.Equal("NoBaseline", collected.Variation.State);
        Assert.Null(collected.Variation.Percent);
        Assert.Equal(75m, collected.Variation.Absolute);

        // Saldo pendiente de hoy: B debe 15, C vuelve a deber 30 tras el reverso y D debe 30 (la transferencia aún no está verificada).
        var outstanding = Kpi(financial.Kpis, "outstanding");
        Assert.Equal(75m, outstanding.Value);
        Assert.Equal("NotApplicable", outstanding.Variation.State);
        Assert.Equal(0m, Kpi(financial.Kpis, "overdue").Value);

        // Por método: tarjeta (10 + 5 + 30) y efectivo; la participación suma el 100 %.
        Assert.Equal(["Card", "Cash"], financial.Methods.Select(m => m.Method));
        Assert.Equal(45m, financial.Methods[0].Amount);
        Assert.Equal(3, financial.Methods[0].Count);
        Assert.Equal(100m, financial.Methods.Sum(m => m.Share));
        Assert.Equal("USD", dashboard.Currency);
        Assert.Empty(dashboard.OmittedSections.Where(s => s == "financial"));
    }

    [Fact]
    public async Task Un_pago_repartido_entre_varias_obligaciones_se_cuenta_una_sola_vez()
    {
        var gym = await NewGymAsync();
        var owner = gym.Tenant.Owner;
        var member = await NewMemberAsync(owner, gym.Uio);
        var membership = await EnrollAsync(owner, member, gym.Plan);
        var extra = await ExtraObligationAsync(owner, member, 20m);

        await PayAsync(owner, member.Id, 50m, "Card", "VCH-9",
            allocations: [new { obligationId = membership.Obligation!.Id, amount = 30m }, new { obligationId = extra.Id, amount = 20m }]);

        var financial = (await DashboardAsync(owner, Last(10))).Financial!;
        Assert.Equal(50m, Kpi(financial.Kpis, "collected").Value);
        Assert.Equal(1, financial.Methods.Single().Count);
        Assert.Equal(0m, Kpi(financial.Kpis, "outstanding").Value);
    }

    [Fact]
    public async Task La_variacion_compara_con_el_periodo_anterior_y_el_saldo_vencido_usa_la_fecha_de_vencimiento()
    {
        var gym = await NewGymAsync();
        var owner = gym.Tenant.Owner;
        var a = await NewMemberAsync(owner, gym.Uio);
        await EnrollAsync(owner, a, gym.Plan);

        // Dos pagos de 10 hace 15 días (período anterior de «últimos 10 días») y uno de 40 hoy.
        var earlier = DateTimeOffset.UtcNow.AddDays(-15);
        Assert.Equal(HttpStatusCode.Created, (await PostPaymentAsync(owner, PaymentBody(a.Id, 10m, "Card", "VCH-A", paidAt: earlier))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await PostPaymentAsync(owner, PaymentBody(a.Id, 10m, "Card", "VCH-B", paidAt: earlier))).StatusCode);
        var extra = await ExtraObligationAsync(owner, a, 40m);
        await PayAsync(owner, a.Id, 40m, "Cash", null, allocations: [new { obligationId = extra.Id, amount = 40m }]);

        var collected = Kpi((await DashboardAsync(owner, Last(10))).Financial!.Kpis, "collected");
        Assert.Equal(40m, collected.Value);
        Assert.Equal(20m, collected.Previous);
        Assert.Equal("Ok", collected.Variation.State);
        Assert.Equal(20m, collected.Variation.Absolute);
        Assert.Equal(100.0m, collected.Variation.Percent);

        // La obligación de la membresía vence hoy+N: se mueve su vencimiento al pasado y pasa a saldo vencido (10 pendientes de 30 − 20 pagados).
        await SqlAsync($"UPDATE billing.payment_obligations SET issue_date = current_date - 10, due_date = current_date - 5 WHERE tenant_id = '{gym.Tenant.Id}' AND membership_id IS NOT NULL");
        var financial = (await DashboardAsync(owner, Last(10))).Financial!;
        Assert.Equal(10m, Kpi(financial.Kpis, "outstanding").Value);
        Assert.Equal(10m, Kpi(financial.Kpis, "overdue").Value);
    }

    [Fact]
    public async Task Un_periodo_sin_actividad_se_marca_vacio_sin_porcentajes_ni_errores_de_division()
    {
        var gym = await NewGymAsync();
        var dashboard = await DashboardAsync(gym.Tenant.Owner, "preset=Last7Days");

        Assert.All(dashboard.Financial!.Kpis, k => Assert.True(k.IsEmpty, k.Key));
        Assert.All(dashboard.Members!.Kpis, k => Assert.True(k.IsEmpty, k.Key));
        Assert.Empty(dashboard.Financial.Methods);

        var collected = Kpi(dashboard.Financial.Kpis, "collected");
        Assert.Equal(0m, collected.Value);
        Assert.Equal("NoBaseline", collected.Variation.State);
        Assert.Null(collected.Variation.Percent);

        // Sin sesiones no hay capacidad: la ocupación no existe (nulo), no es un 0 % engañoso.
        var occupancy = Kpi(dashboard.Operations!.Kpis, "class_occupancy");
        Assert.Null(occupancy.Value);
        Assert.Equal("NoData", occupancy.Variation.State);
        Assert.True(occupancy.IsEmpty);
    }

    // ---- Alumnos y membresías ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Los_alumnos_activos_son_los_que_tienen_membresia_vigente_y_las_inscripciones_se_clasifican()
    {
        var gym = await NewGymAsync();
        var owner = gym.Tenant.Owner;

        // A y B tienen membresía pagada (vigente); C paga y se le cancela la membresía; D se inscribe pero no paga (no estuvo activa); E no tiene membresía.
        var a = await NewMemberAsync(owner, gym.Uio);
        var membershipA = await EnrollAsync(owner, a, gym.Plan);
        await PayAsync(owner, a.Id, 30m, "Cash", null);
        var b = await NewMemberAsync(owner, gym.Gye);
        await EnrollAsync(owner, b, gym.Plan);
        await PayAsync(owner, b.Id, 30m, "Card", "VCH-G");
        var c = await NewMemberAsync(owner, gym.Uio);
        var membershipC = await EnrollAsync(owner, c, gym.Plan);
        await PayAsync(owner, c.Id, 30m, "Cash", null);
        Assert.Equal(HttpStatusCode.OK, (await PostWithKeyAsync(owner, $"/api/v1/memberships/{membershipC.Id}/cancel", new { reason = "Se mudó de ciudad" })).StatusCode);
        var d = await NewMemberAsync(owner, gym.Uio);
        await EnrollAsync(owner, d, gym.Plan);
        await NewMemberAsync(owner, gym.Uio);

        // A renueva: la renovación es una venta más, pero no un alumno nuevo.
        var renew = await owner.PostAsJsonAsync($"/api/v1/memberships/{membershipA.Id}/renew", new { });
        Assert.True(renew.IsSuccessStatusCode, await renew.Content.ReadAsStringAsync());

        var members = (await DashboardAsync(owner, Last(10))).Members!;
        Assert.Equal(2m, Kpi(members.Kpis, "active_members").Value);
        Assert.Equal(5m, Kpi(members.Kpis, "new_members").Value);
        Assert.Equal(4m, Kpi(members.Kpis, "new_enrollments").Value);
        Assert.Equal(1m, Kpi(members.Kpis, "renewals").Value);
        Assert.Equal(0m, Kpi(members.Kpis, "reincorporations").Value);
        Assert.Equal("Fecha de venta de la membresía", Kpi(members.Kpis, "new_enrollments").Basis);

        // Solo la sucursal GYE: B es el único activo.
        var gyeOnly = (await DashboardAsync(owner, $"{Last(10)}&branchId={gym.Gye}")).Members!;
        Assert.Equal(1m, Kpi(gyeOnly.Kpis, "active_members").Value);
        Assert.Equal(1m, Kpi(gyeOnly.Kpis, "new_members").Value);
    }

    [Fact]
    public async Task Las_membresias_proximas_a_vencer_y_las_vencidas_usan_su_fecha_de_fin()
    {
        var gym = await NewGymAsync();
        var owner = gym.Tenant.Owner;
        var soon = await NewMemberAsync(owner, gym.Uio);
        var soonMembership = await EnrollAsync(owner, soon, gym.Plan);
        await PayAsync(owner, soon.Id, 30m, "Cash", null);
        var expired = await NewMemberAsync(owner, gym.Uio);
        var expiredMembership = await EnrollAsync(owner, expired, gym.Plan);
        await PayAsync(owner, expired.Id, 30m, "Cash", null);

        // Una termina en 5 días y la otra terminó hace 3 (el rango de fechas no tiene triggers de inmutabilidad en la base de pruebas).
        await SqlAsync($"UPDATE members.memberships SET start_date = current_date - 20, end_date = current_date + 5 WHERE id = '{soonMembership.Id}'");
        await SqlAsync($"UPDATE members.memberships SET start_date = current_date - 33, end_date = current_date - 3 WHERE id = '{expiredMembership.Id}'");

        var members = (await DashboardAsync(owner, $"{Last(10)}&expiringDays=7")).Members!;
        Assert.Equal(1m, Kpi(members.Kpis, "expiring_soon").Value);
        Assert.Equal("NotApplicable", Kpi(members.Kpis, "expiring_soon").Variation.State);
        Assert.Equal(1m, Kpi(members.Kpis, "expired").Value);

        // Con 30 días sigue siendo la misma; con 3 días (valor no permitido) se rechaza.
        Assert.Equal(1m, Kpi((await DashboardAsync(owner, $"{Last(10)}&expiringDays=30")).Members!.Kpis, "expiring_soon").Value);
        var invalid = await owner.GetAsync($"/api/v1/analytics/dashboard?{Last(10)}&expiringDays=3");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        Assert.Equal("analytics.expiring_days_invalid", await TestApi.ProblemCodeAsync(invalid));
    }

    // ---- Operación ---------------------------------------------------------------------------------------------------

    private async Task<(TrainerRow Trainer, ClassRow Class)> NewClassAsync(Gym gym, string email, string name, int capacity = 2)
    {
        var user = await TestApi.CreateUserAsync(gym.Tenant.Owner, email, "Entrenador", gym.Uio);
        var trainer = await gym.Tenant.Owner.PostAsJsonAsync("/api/v1/trainers", new { userId = user.Id, specialties = new[] { "Yoga" } });
        Assert.Equal(HttpStatusCode.Created, trainer.StatusCode);
        var trainerRow = (await trainer.Content.ReadFromJsonAsync<TrainerRow>(Json))!;
        var response = await gym.Tenant.Owner.PostAsJsonAsync("/api/v1/group-classes", new
        {
            branchId = gym.Uio, name, activity = "Yoga", instructorId = trainerRow.Id, durationMinutes = 60, capacity, level = "AllLevels",
            minBookingNoticeMinutes = 0, maxBookingAdvanceDays = 30, cancellationDeadlineMinutes = 0, waitlistEnabled = true, waitlistCapacity = 5,
            offerWindowMinutes = 30, requireActiveMembership = false, applyPaymentPolicy = false
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (trainerRow, (await response.Content.ReadFromJsonAsync<ClassRow>(Json))!);
    }

    private static DateTimeOffset InDays(int days, int hour) =>
        new(DateTimeOffset.UtcNow.ToOffset(Ecuador).Date.AddDays(days).AddHours(hour), Ecuador);

    private static async Task<SessionRow> NewSessionAsync(HttpClient owner, Guid classId, DateTimeOffset startsAt)
    {
        var response = await owner.PostAsJsonAsync("/api/v1/class-sessions", new { classId, startsAt });
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"Respondió {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<SessionRow>(Json))!;
    }

    private static async Task<Guid> BookAsync(HttpClient owner, Guid sessionId, Guid memberId, bool waitlist = false)
    {
        var response = await PostWithKeyAsync(owner, "/api/v1/class-reservations", new { sessionId, memberId, joinWaitlist = waitlist });
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"Respondió {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task La_ocupacion_cuenta_solo_confirmados_y_las_clases_se_clasifican_como_realizadas_canceladas_y_pendientes_de_cierre()
    {
        var gym = await NewGymAsync();
        var owner = gym.Tenant.Owner;
        var (_, yoga) = await NewClassAsync(gym, "yoga@gym.test", "Yoga");
        var m = new List<MemberDto>();
        for (var i = 0; i < 4; i++)
        {
            m.Add(await NewMemberAsync(owner, gym.Uio));
        }

        // S1 (mañana, cupo 2): m0 y m1 confirmados, m2 en espera, m3 reserva y cancela. => ocupa 2/2.
        var s1 = await NewSessionAsync(owner, yoga.Id, InDays(1, 9));
        await BookAsync(owner, s1.Id, m[0].Id);
        await BookAsync(owner, s1.Id, m[1].Id);
        await BookAsync(owner, s1.Id, m[2].Id, waitlist: true);
        var cancelled = await BookAsync(owner, s1.Id, m[3].Id, waitlist: true);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync($"/api/v1/class-reservations/{cancelled}/cancel", new { reason = "Cambio de planes" })).StatusCode);

        // S2 (pasado mañana): se cancela con una reserva; no cuenta como ocupación ni como cancelación de reserva.
        var s2 = await NewSessionAsync(owner, yoga.Id, InDays(2, 9));
        await BookAsync(owner, s2.Id, m[0].Id);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync($"/api/v1/class-sessions/{s2.Id}/cancel", new { reason = "Mantenimiento" })).StatusCode);

        // S3: una reserva, empieza hace 2 h y se cierra con asistencia. S4: empieza hace 4 h y nadie la cierra.
        var s3 = await NewSessionAsync(owner, yoga.Id, InDays(3, 9));
        var r3 = await BookAsync(owner, s3.Id, m[0].Id);
        var s4 = await NewSessionAsync(owner, yoga.Id, InDays(4, 9));
        await SqlAsync($"UPDATE classes.class_sessions SET starts_at = now() - interval '2 hours', ends_at = now() - interval '1 hour' WHERE id = '{s3.Id}'");
        await SqlAsync($"UPDATE classes.class_sessions SET starts_at = now() - interval '4 hours', ends_at = now() - interval '3 hours' WHERE id = '{s4.Id}'");
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/v1/class-sessions/{s3.Id}/attendance", new { entries = new[] { new { reservationId = r3, outcome = "Attended", notes = (string?)null } } })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync($"/api/v1/class-sessions/{s3.Id}/complete", new { markPendingAsNoShow = false })).StatusCode);

        var operations = (await DashboardAsync(owner, $"preset=Custom&from={Today.AddDays(-2):yyyy-MM-dd}&to={Today.AddDays(7):yyyy-MM-dd}")).Operations!;

        // Confirmadas: S1 (2) + S3 (1); canceladas por el alumno: 1 (la de S2 se canceló al cancelar la sesión).
        Assert.Equal(3m, Kpi(operations.Kpis, "reservations_confirmed").Value);
        Assert.Equal(1m, Kpi(operations.Kpis, "reservations_cancelled").Value);

        // Ocupación = (2 + 1) / (2 + 2 + 2) = 50 %: S4 sin reservas cuenta su capacidad; la cancelada (S2) no; la espera no suma.
        var occupancy = Kpi(operations.Kpis, "class_occupancy");
        Assert.Equal("Percent", occupancy.Unit);
        Assert.Equal(50m, occupancy.Value); // S1 2/2, S3 1/2, S4 0/2 → 3 de 6
        Assert.Equal(1m, Kpi(operations.Kpis, "sessions_held").Value);
        Assert.Equal(1m, Kpi(operations.Kpis, "sessions_cancelled").Value);
        Assert.Equal(new SessionsRow(Held: 1, Cancelled: 1, Upcoming: 1, PendingClosure: 1), operations.Sessions);
        Assert.Equal(0m, Kpi(operations.Kpis, "class_no_shows").Value);
    }

    [Fact]
    public async Task El_entrenador_ve_solo_sus_sesiones_y_ningun_dato_financiero()
    {
        var gym = await NewGymAsync();
        var owner = gym.Tenant.Owner;
        var (_, mine) = await NewClassAsync(gym, "carla@gym.test", "Yoga de Carla");
        var (_, other) = await NewClassAsync(gym, "pedro@gym.test", "Spinning de Pedro");
        var member = await NewMemberAsync(owner, gym.Uio);
        var sessionMine = await NewSessionAsync(owner, mine.Id, InDays(1, 9));
        var sessionOther = await NewSessionAsync(owner, other.Id, InDays(1, 11));
        await BookAsync(owner, sessionMine.Id, member.Id);
        await BookAsync(owner, sessionOther.Id, member.Id);

        var carla = TestApi.Authenticated(api, await TestApi.LoginAsync(api, gym.Tenant.Slug, "carla@gym.test", TestApi.UserPassword));
        var dashboard = await DashboardAsync(carla, $"preset=Custom&from={Today:yyyy-MM-dd}&to={Today.AddDays(5):yyyy-MM-dd}");

        Assert.Null(dashboard.Financial);
        Assert.Null(dashboard.Members);
        Assert.Null(dashboard.Operations);
        Assert.Equal(["financial", "members", "operations"], dashboard.OmittedSections.Order());
        Assert.Equal(1, dashboard.Instructor!.Sessions.Upcoming);
        Assert.Equal(50m, Kpi(dashboard.Instructor.Kpis, "my_occupancy").Value); // 1 confirmado de 2 cupos, solo su sesión
    }

    // ---- Alcance y seguridad -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Cada_rol_recibe_solo_las_secciones_y_sucursales_que_le_corresponden()
    {
        var gym = await NewGymAsync();
        var owner = gym.Tenant.Owner;
        var a = await NewMemberAsync(owner, gym.Uio);
        await EnrollAsync(owner, a, gym.Plan);
        await PayAsync(owner, a.Id, 30m, "Cash", null);
        var e = await NewMemberAsync(owner, gym.Gye);
        await EnrollAsync(owner, e, gym.Plan);
        await PayAsync(owner, e.Id, 30m, "Card", "VCH-7");

        // El gerente de Quito ve solo su sucursal; pedir la otra se rechaza.
        var manager = await StaffAsync(gym, "gerente@gym.test", "AdministradorSucursal", gym.Uio);
        var managerView = await DashboardAsync(manager, Last(10));
        Assert.Equal(30m, Kpi(managerView.Financial!.Kpis, "collected").Value);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync($"/api/v1/analytics/dashboard?{Last(10)}&branchId={gym.Gye}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync($"/api/v1/analytics/dashboard/comparison?{Last(10)}&branchId={gym.Gye}")).StatusCode);

        // El cajero ve solo lo financiero; recepción solo lo operativo; el contador todas las sucursales pero sin operación.
        var cashier = await DashboardAsync(await StaffAsync(gym, "cajero@gym.test", "Cajero", gym.Uio), Last(10));
        Assert.NotNull(cashier.Financial);
        Assert.Null(cashier.Members);
        Assert.Null(cashier.Operations);
        Assert.Equal(30m, Kpi(cashier.Financial.Kpis, "collected").Value);

        var reception = await DashboardAsync(await StaffAsync(gym, "recep@gym.test", "Recepcion", gym.Uio), Last(10));
        Assert.Null(reception.Financial);
        Assert.NotNull(reception.Operations);

        var accountant = await DashboardAsync(await StaffAsync(gym, "contador@gym.test", "Contador", gym.Uio), Last(10));
        Assert.Equal(60m, Kpi(accountant.Financial!.Kpis, "collected").Value);
        Assert.NotNull(accountant.Members);
        Assert.Null(accountant.Operations);
        Assert.Contains("operations", accountant.OmittedSections);

        // Sin permiso de analítica o sin sesión: nada.
        var nutritionist = await StaffAsync(gym, "nutri@gym.test", "Nutricionista", gym.Uio);
        Assert.Equal(HttpStatusCode.Forbidden, (await nutritionist.GetAsync("/api/v1/analytics/dashboard")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().GetAsync("/api/v1/analytics/dashboard")).StatusCode);
    }

    [Fact]
    public async Task Los_datos_de_un_gimnasio_nunca_aparecen_en_el_de_otro()
    {
        var a = await NewGymAsync();
        var b = await NewGymAsync();
        var memberA = await NewMemberAsync(a.Tenant.Owner, a.Uio);
        await EnrollAsync(a.Tenant.Owner, memberA, a.Plan);
        await PayAsync(a.Tenant.Owner, memberA.Id, 30m, "Cash", null);
        var memberB = await NewMemberAsync(b.Tenant.Owner, b.Uio);
        await EnrollAsync(b.Tenant.Owner, memberB, b.Plan);
        await PayAsync(b.Tenant.Owner, memberB.Id, 30m, "Cash", null);

        var viewA = await DashboardAsync(a.Tenant.Owner, Last(10));
        Assert.Equal(30m, Kpi(viewA.Financial!.Kpis, "collected").Value);
        Assert.Equal(1m, Kpi(viewA.Members!.Kpis, "new_members").Value);

        // Pedir una sucursal del otro gimnasio es indistinguible de pedir una inexistente.
        Assert.Equal(HttpStatusCode.Forbidden, (await a.Tenant.Owner.GetAsync($"/api/v1/analytics/dashboard?{Last(10)}&branchId={b.Uio}")).StatusCode);
    }

    // ---- Evolución ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task La_evolucion_reparte_los_cobros_por_dia_local_y_suma_lo_mismo_que_el_total()
    {
        var gym = await NewGymAsync();
        var owner = gym.Tenant.Owner;
        var a = await NewMemberAsync(owner, gym.Uio);
        await EnrollAsync(owner, a, gym.Plan);
        var extra = await ExtraObligationAsync(owner, a, 60m);

        // Un pago a las 23:30 de hace 3 días (hora de Quito) y otro a las 00:30 de hace 2 días: son días distintos aunque ambos caen en UTC el mismo día.
        var lateNight = new DateTimeOffset(Today.AddDays(-3).ToDateTime(new TimeOnly(23, 30)), Ecuador);
        var earlyMorning = new DateTimeOffset(Today.AddDays(-2).ToDateTime(new TimeOnly(0, 30)), Ecuador);
        Assert.Equal(HttpStatusCode.Created, (await PostPaymentAsync(owner, PaymentBody(a.Id, 10m, "Card", "VCH-N", allocations: [new { obligationId = extra.Id, amount = 10m }], paidAt: lateNight))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await PostPaymentAsync(owner, PaymentBody(a.Id, 20m, "Card", "VCH-M", allocations: [new { obligationId = extra.Id, amount = 20m }], paidAt: earlyMorning))).StatusCode);

        var response = await owner.GetAsync($"/api/v1/analytics/dashboard/comparison?{Last(7)}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var series = (await response.Content.ReadFromJsonAsync<SeriesResponse>(Json))!;
        Assert.Equal(1, series.BucketDays);

        var collected = series.Series.Single(s => s.Key == "collected");
        Assert.Equal(7, collected.Current.Starts.Length);
        Assert.Equal(7, collected.Previous.Starts.Length);
        Assert.Equal(30m, collected.Current.Values.Sum());
        var byDay = collected.Current.Starts.Zip(collected.Current.Values).ToDictionary(x => x.First, x => x.Second);
        Assert.Equal(10m, byDay[Today.AddDays(-3).ToString("yyyy-MM-dd")]);
        Assert.Equal(20m, byDay[Today.AddDays(-2).ToString("yyyy-MM-dd")]);
        Assert.Equal(0m, byDay[Today.ToString("yyyy-MM-dd")]);
        Assert.Equal(0m, collected.Previous.Values.Sum());

        // La serie suma lo mismo que el indicador del tablero.
        var dashboard = await DashboardAsync(owner, Last(7));
        Assert.Equal(Kpi(dashboard.Financial!.Kpis, "collected").Value, collected.Current.Values.Sum());

        // Un rol sin permiso financiero no recibe esa serie.
        var reception = await StaffAsync(gym, "recep2@gym.test", "Recepcion", gym.Uio);
        var receptionSeries = (await (await reception.GetAsync($"/api/v1/analytics/dashboard/comparison?{Last(7)}")).Content.ReadFromJsonAsync<SeriesResponse>(Json))!;
        Assert.DoesNotContain(receptionSeries.Series, s => s.Key == "collected");
        Assert.Contains(receptionSeries.Series, s => s.Key == "gym_visits");
    }

    [Fact]
    public async Task Los_rangos_largos_se_agrupan_en_tramos_y_el_rango_invalido_se_rechaza()
    {
        var gym = await NewGymAsync();
        var owner = gym.Tenant.Owner;

        var weekly = (await (await owner.GetAsync($"/api/v1/analytics/dashboard/comparison?{Last(60)}")).Content.ReadFromJsonAsync<SeriesResponse>(Json))!;
        Assert.Equal(7, weekly.BucketDays);
        Assert.Equal(9, weekly.Series.First().Current.Starts.Length); // 60 días en tramos de 7: 8 completos y 1 de 4 días

        var invalid = await owner.GetAsync("/api/v1/analytics/dashboard/comparison?preset=Custom&from=2026-03-10&to=2026-03-01");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        Assert.Equal("analytics.period_invalid", await TestApi.ProblemCodeAsync(invalid));
    }
}
