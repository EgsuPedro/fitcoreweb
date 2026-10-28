using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FitCore.Api.IntegrationTests.Infrastructure;
using Npgsql;
using static FitCore.Api.IntegrationTests.Infrastructure.CommercialTestApi;

namespace FitCore.Api.IntegrationTests;

/// <summary>Reportes operativos y de clases (bloque 6): asistencia al gimnasio, ocupación, demanda, cancelaciones y el acceso del instructor.</summary>
[Collection(ApiCollection1.Name)]
public class AnalyticsOperationsReportTests(ApiFixture api)
{
    private static readonly TimeSpan Ecuador = TimeSpan.FromHours(-5);

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.Add(Ecuador));

    private static string D(int daysFromToday) => Today.AddDays(daysFromToday).ToString("yyyy-MM-dd");

    private static string Range(int fromDays, int toDays) => $"preset=Custom&from={D(fromDays)}&to={D(toDays)}";

    private sealed record Gym(TestTenant Tenant, Guid Uio, Guid Gye, PlanDto Plan);

    private sealed record Ref(Guid Id);

    private async Task<Gym> NewGymAsync()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var uio = await TestApi.CreateBranchAsync(tenant.Owner, "UIO");
        var gye = await TestApi.CreateBranchAsync(tenant.Owner, "GYE");
        return new Gym(tenant, uio, gye, await CreatePlanAsync(tenant.Owner, "Mensual", 30m));
    }

    private static async Task<JsonElement> GetAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync($"/api/v1/reports/{url}");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Respondió {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static JsonElement Kpi(JsonElement report, string key) => report.GetProperty("summary").EnumerateArray().Single(k => k.GetProperty("key").GetString() == key);

    private static decimal Num(JsonElement element, string property) => element.GetProperty(property).GetDecimal();

    private async Task SqlAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(api.AdminConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<List<Guid>> IdsAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(api.AdminConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var ids = new List<Guid>();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    private static int _members;

    private static async Task<MemberDto> NewMemberAsync(HttpClient owner, Guid branch, bool enroll)
    {
        var n = Interlocked.Increment(ref _members);
        var response = await owner.PostAsJsonAsync("/api/v1/members", new
        {
            branchId = branch, firstName = $"Socio{n}", lastName = "Operacion", idType = "Cedula", idNumber = MemberTests.NewCedula(), birthDate = "1992-03-04",
            email = $"o{n}.{Guid.NewGuid():N}"[..24] + "@alumnos.test", phone = "0991234567"
        });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<MemberDto>())!;
    }

    private async Task<HttpClient> StaffAsync(Gym gym, string email, string role, params Guid[] branches)
    {
        await TestApi.CreateUserAsync(gym.Tenant.Owner, email, role, branches);
        return TestApi.Authenticated(api, await TestApi.LoginAsync(api, gym.Tenant.Slug, email, TestApi.UserPassword));
    }

    // ---- Asistencia al gimnasio ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task La_asistencia_al_gimnasio_se_reparte_por_dia_local_hora_sucursal_y_frecuencia()
    {
        var gym = await NewGymAsync();
        var owner = gym.Tenant.Owner;
        var people = new List<MemberDto>();
        foreach (var branch in new[] { gym.Uio, gym.Uio, gym.Gye })
        {
            var member = await NewMemberAsync(owner, branch, true);
            Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync("/api/v1/memberships", new { memberId = member.Id, planId = gym.Plan.Id })).StatusCode);
            await PayAsync(owner, member.Id, 30m, "Card", $"V-{Guid.NewGuid():N}"[..10]);
            people.Add(member);
        }

        // A entra 3 veces, B 2 veces y C 1 vez (cada visita se cierra con su salida para poder volver a entrar).
        var visits = new[] { (0, 3), (1, 2), (2, 1) };
        foreach (var (index, count) in visits)
        {
            for (var i = 0; i < count; i++)
            {
                var branch = index == 2 ? gym.Gye : gym.Uio;
                var checkIn = await CheckInAsync(owner, branch, people[index].Id);
                Assert.True(checkIn.StatusCode == HttpStatusCode.Created, await checkIn.Content.ReadAsStringAsync());
                Assert.True((await CheckOutAsync(owner, people[index].Id)).IsSuccessStatusCode);
            }
        }

        // Se fechan las 6 entradas en horas locales conocidas (Quito, UTC−5). La de las 23:30 cae en UTC al día siguiente.
        var schedule = new (int Person, int Order, int Day, string Time)[]
        {
            (0, 0, -2, "08:30"), (0, 1, -2, "18:15"), (0, 2, -1, "08:10"),
            (1, 0, -1, "19:00"), (1, 1, -2, "23:30"),
            (2, 0, 0, "07:00")
        };
        foreach (var (person, order, day, time) in schedule)
        {
            var id = (await IdsAsync($"SELECT id FROM access.attendance WHERE member_id = '{people[person].Id}' ORDER BY check_in_at OFFSET {order} LIMIT 1")).Single();
            await SqlAsync($"SET session_replication_role = replica; UPDATE access.attendance SET check_in_at = '{D(day)} {time}:00-05'::timestamptz, check_out_at = '{D(day)} {time}:00-05'::timestamptz + interval '45 minutes' WHERE id = '{id}'");
        }

        var report = await GetAsync(owner, $"attendance?{Range(-3, 0)}");
        Assert.Equal(6m, Num(Kpi(report, "visits"), "value"));
        Assert.Equal(3m, Num(Kpi(report, "unique_visitors"), "value"));
        Assert.Equal(2.0m, Num(Kpi(report, "frequency"), "value"));
        Assert.Equal("NoBaseline", Kpi(report, "visits").GetProperty("variation").GetProperty("state").GetString());

        // Por día local: el pago de las 23:30 pertenece al día -2, no al -1.
        Assert.Equal("day", report.GetProperty("grouping").GetString());
        var series = report.GetProperty("series").EnumerateArray().ToDictionary(p => p.GetProperty("start").GetString()!, p => p);
        Assert.Equal(4, series.Count);
        Assert.Equal(0, series[D(-3)].GetProperty("visits").GetInt32());
        Assert.Equal(3, series[D(-2)].GetProperty("visits").GetInt32());
        Assert.Equal(2, series[D(-2)].GetProperty("visitors").GetInt32());
        Assert.Equal(2, series[D(-1)].GetProperty("visits").GetInt32());
        Assert.Equal(1, series[D(0)].GetProperty("visits").GetInt32());

        var hours = report.GetProperty("byHour").EnumerateArray().ToDictionary(h => h.GetProperty("key").GetString()!, h => h.GetProperty("visits").GetInt32());
        Assert.Equal(24, hours.Count);
        Assert.Equal(2, hours["8"]);
        Assert.Equal(1, hours["18"]);
        Assert.Equal(1, hours["19"]);
        Assert.Equal(1, hours["23"]);
        Assert.Equal(1, hours["7"]);
        Assert.Equal(0, hours["3"]);

        var weekdays = report.GetProperty("byWeekday").EnumerateArray().ToList();
        Assert.Equal(7, weekdays.Count);
        Assert.Equal("Monday", weekdays[0].GetProperty("key").GetString());
        Assert.Equal("Sunday", weekdays[6].GetProperty("key").GetString());
        Assert.Equal(6, weekdays.Sum(w => w.GetProperty("visits").GetInt32()));
        var expectedForMinus2 = weekdays.Single(w => w.GetProperty("key").GetString() == Today.AddDays(-2).DayOfWeek.ToString()).GetProperty("visits").GetInt32();
        Assert.True(expectedForMinus2 >= 3);

        var branches = report.GetProperty("byBranch").EnumerateArray().ToDictionary(b => b.GetProperty("branchName").GetString()!);
        Assert.Equal(5, branches["Sucursal UIO"].GetProperty("visits").GetInt32());
        Assert.Equal(1, branches["Sucursal GYE"].GetProperty("visits").GetInt32());
        Assert.Equal(83.3m, Num(branches["Sucursal UIO"], "share"));

        // Cuántos alumnos vinieron 1 vez, 2-3 veces, etc.
        var frequency = report.GetProperty("frequency").EnumerateArray().ToDictionary(f => f.GetProperty("label").GetString()!, f => f.GetProperty("members").GetInt32());
        Assert.Equal(1, frequency["1"]);
        Assert.Equal(2, frequency["2-3"]);
        Assert.Equal(0, frequency["8+"]);

        // Por alumno (solo código y nombre) y agrupado por semana.
        var byMember = await GetAsync(owner, $"attendance?{Range(-3, 0)}&view=Members");
        var top = byMember.GetProperty("members").GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(people[0].Code, top[0].GetProperty("memberCode").GetString());
        Assert.Equal(3, top[0].GetProperty("visits").GetInt32());
        Assert.Equal(2, top[0].GetProperty("days").GetInt32());
        Assert.Equal(3, byMember.GetProperty("members").GetProperty("totalCount").GetInt32());
        Assert.DoesNotContain("phone", byMember.GetRawText(), StringComparison.OrdinalIgnoreCase);
        var weekly = await GetAsync(owner, $"attendance?{Range(-3, 0)}&grouping=Week");
        Assert.Equal("week", weekly.GetProperty("grouping").GetString());
        Assert.Equal(6, weekly.GetProperty("series").EnumerateArray().Sum(p => p.GetProperty("visits").GetInt32()));
        Assert.All(weekly.GetProperty("series").EnumerateArray(), p => Assert.Equal(DayOfWeek.Monday, DateOnly.Parse(p.GetProperty("start").GetString()!).DayOfWeek));

        // Alcance: recepción (de Quito) ve solo Quito; el cajero no tiene el permiso.
        var reception = await StaffAsync(gym, "recep@gym.test", "Recepcion", gym.Uio);
        Assert.Equal(5m, Num(Kpi(await GetAsync(reception, $"attendance?{Range(-3, 0)}"), "visits"), "value"));
        Assert.Equal(HttpStatusCode.Forbidden, (await reception.GetAsync($"/api/v1/reports/attendance?branchId={gym.Gye}")).StatusCode);
        var cashier = await StaffAsync(gym, "cajero@gym.test", "Cajero", gym.Uio);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync("/api/v1/reports/attendance")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().GetAsync("/api/v1/reports/attendance")).StatusCode);
    }

    [Fact]
    public async Task Sin_entradas_la_asistencia_muestra_ceros_y_no_falla()
    {
        var gym = await NewGymAsync();
        var report = await GetAsync(gym.Tenant.Owner, $"attendance?{Range(-6, 0)}");

        Assert.Equal(0m, Num(Kpi(report, "visits"), "value"));
        Assert.True(Kpi(report, "visits").GetProperty("isEmpty").GetBoolean());
        Assert.Equal(JsonValueKind.Null, Kpi(report, "frequency").GetProperty("value").ValueKind);
        Assert.Equal(7, report.GetProperty("series").GetArrayLength());
        Assert.All(report.GetProperty("series").EnumerateArray(), p => Assert.Equal(0, p.GetProperty("visits").GetInt32()));
        Assert.Equal(2, report.GetProperty("byBranch").GetArrayLength());
        Assert.All(report.GetProperty("byBranch").EnumerateArray(), b => Assert.Equal(JsonValueKind.Null, b.GetProperty("share").ValueKind));
    }

    // ---- Clases: ocupación, demanda y cancelaciones --------------------------------------------------------------------------

    private async Task<(Ref Trainer, Ref Class)> NewClassAsync(Gym gym, string email, string name, string activity, int capacity)
    {
        var user = await TestApi.CreateUserAsync(gym.Tenant.Owner, email, "Entrenador", gym.Uio);
        var trainer = await gym.Tenant.Owner.PostAsJsonAsync("/api/v1/trainers", new { userId = user.Id, specialties = new[] { activity } });
        Assert.Equal(HttpStatusCode.Created, trainer.StatusCode);
        var trainerRef = (await trainer.Content.ReadFromJsonAsync<Ref>(new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
        var response = await gym.Tenant.Owner.PostAsJsonAsync("/api/v1/group-classes", new
        {
            branchId = gym.Uio, name, activity, instructorId = trainerRef.Id, durationMinutes = 60, capacity, level = "AllLevels",
            minBookingNoticeMinutes = 0, maxBookingAdvanceDays = 30, cancellationDeadlineMinutes = 0, waitlistEnabled = true, waitlistCapacity = 5,
            offerWindowMinutes = 30, requireActiveMembership = false, applyPaymentPolicy = false
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (trainerRef, (await response.Content.ReadFromJsonAsync<Ref>(new JsonSerializerOptions(JsonSerializerDefaults.Web)))!);
    }

    private static DateTimeOffset At(int days, int hour) => new(DateTimeOffset.UtcNow.ToOffset(Ecuador).Date.AddDays(days).AddHours(hour), Ecuador);

    private static async Task<Guid> NewSessionAsync(HttpClient owner, Guid classId, DateTimeOffset startsAt)
    {
        var response = await owner.PostAsJsonAsync("/api/v1/class-sessions", new { classId, startsAt });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<Ref>(new JsonSerializerOptions(JsonSerializerDefaults.Web)))!.Id;
    }

    private static async Task<Guid> BookAsync(HttpClient owner, Guid sessionId, Guid memberId, bool waitlist = false)
    {
        var response = await PostWithKeyAsync(owner, "/api/v1/class-reservations", new { sessionId, memberId, joinWaitlist = waitlist });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private sealed record ClassScenario(Gym Gym, Ref Yoga, Ref Spin, Guid Y1, Guid Y2, Guid Y3, Guid Y4, Guid S1);

    private async Task<ClassScenario> BuildClassScenarioAsync()
    {
        var gym = await NewGymAsync();
        var owner = gym.Tenant.Owner;
        var (_, yoga) = await NewClassAsync(gym, "carla@gym.test", "Yoga de Carla", "Yoga", capacity: 2);
        var (_, spin) = await NewClassAsync(gym, "pedro@gym.test", "Spinning de Pedro", "Spinning", capacity: 4);
        var m = new List<MemberDto>();
        for (var i = 0; i < 4; i++)
        {
            m.Add(await NewMemberAsync(owner, gym.Uio, false));
        }

        // Y1: cupo 2 lleno + 1 en espera. Y2: 1 confirmado y otro que cancela. Y3: se cancela con una reserva. Y4: ya ocurrió (1 asistió, 1 no). S1: 3 de 4.
        var y1 = await NewSessionAsync(owner, yoga.Id, At(1, 9));
        await BookAsync(owner, y1, m[0].Id);
        await BookAsync(owner, y1, m[1].Id);
        await BookAsync(owner, y1, m[2].Id, waitlist: true);

        var y2 = await NewSessionAsync(owner, yoga.Id, At(2, 9));
        await BookAsync(owner, y2, m[0].Id);
        var dropped = await BookAsync(owner, y2, m[3].Id);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync($"/api/v1/class-reservations/{dropped}/cancel", new { reason = "Cambio de planes" })).StatusCode);

        var y3 = await NewSessionAsync(owner, yoga.Id, At(3, 9));
        await BookAsync(owner, y3, m[0].Id);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync($"/api/v1/class-sessions/{y3}/cancel", new { reason = "Mantenimiento de la sala" })).StatusCode);

        var y4 = await NewSessionAsync(owner, yoga.Id, At(4, 9));
        var r0 = await BookAsync(owner, y4, m[0].Id);
        var r1 = await BookAsync(owner, y4, m[1].Id);
        await SqlAsync($"UPDATE classes.class_sessions SET starts_at = now() - interval '3 hours', ends_at = now() - interval '2 hours' WHERE id = '{y4}'");
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/v1/class-sessions/{y4}/attendance", new { entries = new object[] { new { reservationId = r0, outcome = "Attended", notes = (string?)null }, new { reservationId = r1, outcome = "NoShow", notes = (string?)null } } })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync($"/api/v1/class-sessions/{y4}/complete", new { markPendingAsNoShow = false })).StatusCode);

        var s1 = await NewSessionAsync(owner, spin.Id, At(1, 18));
        await BookAsync(owner, s1, m[0].Id);
        await BookAsync(owner, s1, m[1].Id);
        await BookAsync(owner, s1, m[2].Id);
        return new ClassScenario(gym, yoga, spin, y1, y2, y3, y4, s1);
    }

    [Fact]
    public async Task La_ocupacion_cuenta_solo_confirmados_y_separa_la_demanda_en_espera_de_la_ocupacion()
    {
        var s = await BuildClassScenarioAsync();
        var owner = s.Gym.Tenant.Owner;

        var report = await GetAsync(owner, $"classes/occupancy?{Range(-2, 7)}");
        Assert.False(report.GetProperty("instructorOnly").GetBoolean());

        // Ocupación = (2 + 1 + 2 + 3) / (2 + 2 + 2 + 4) = 80 %: la sesión cancelada y la espera no cuentan.
        Assert.Equal(80.0m, Num(Kpi(report, "occupancy_reserved"), "value"));
        Assert.Equal("Percent", Kpi(report, "occupancy_reserved").GetProperty("unit").GetString());
        Assert.Equal(8m, Num(Kpi(report, "reservations_confirmed"), "value"));
        Assert.Equal(1m, Num(Kpi(report, "waitlisted"), "value"));

        // Ocupación asistida: solo la sesión ya realizada (1 asistió de 2 cupos).
        Assert.Equal(50.0m, Num(Kpi(report, "occupancy_attended"), "value"));
        Assert.Equal(1m, Num(Kpi(report, "no_shows"), "value"));
        Assert.Equal(50.0m, Num(Kpi(report, "attendance_rate"), "value"));
        Assert.Equal(1m, Num(Kpi(report, "sessions_held"), "value"));
        Assert.Equal(1m, Num(Kpi(report, "sessions_cancelled"), "value"));
        Assert.Equal("NoBaseline", Kpi(report, "occupancy_reserved").GetProperty("variation").GetProperty("state").GetString());

        var sessions = report.GetProperty("sessions");
        Assert.Equal(1, sessions.GetProperty("held").GetInt32());
        Assert.Equal(1, sessions.GetProperty("cancelled").GetInt32());
        Assert.Equal(3, sessions.GetProperty("upcoming").GetInt32());
        Assert.Equal(0, sessions.GetProperty("pendingClosure").GetInt32());

        // Demanda: Yoga (5 confirmados + 1 esperando) por encima de Spinning (3).
        var byActivity = report.GetProperty("byActivity").EnumerateArray().ToList();
        Assert.Equal(["Yoga", "Spinning"], byActivity.Select(a => a.GetProperty("name").GetString()));
        Assert.Equal(3, byActivity[0].GetProperty("sessions").GetInt32()); // Y1, Y2 y Y4 (la cancelada no cuenta)
        Assert.Equal(6, byActivity[0].GetProperty("capacity").GetInt32());
        Assert.Equal(5, byActivity[0].GetProperty("confirmed").GetInt32());
        Assert.Equal(1, byActivity[0].GetProperty("waitlisted").GetInt32());
        Assert.Equal(83.3m, Num(byActivity[0], "occupancyPercent"));
        Assert.Equal(75.0m, Num(byActivity[1], "occupancyPercent"));
        var byClass = report.GetProperty("byClass").EnumerateArray().ToList();
        Assert.Equal(["Yoga de Carla", "Spinning de Pedro"], byClass.Select(c => c.GetProperty("name").GetString()));

        // Horarios de mayor ocupación: el de las 18:00 (Spinning) con 75 % está entre los horarios y ninguno supera el 100 %.
        var slots = report.GetProperty("topSlots").EnumerateArray().ToList();
        Assert.Contains(slots, sl => sl.GetProperty("hour").GetInt32() == 18 && Num(sl, "occupancyPercent") == 75.0m);
        Assert.All(slots, sl => Assert.InRange(Num(sl, "occupancyPercent"), 0m, 100m));
        Assert.Equal(100.0m, slots[0].GetProperty("occupancyPercent").GetDecimal());

        // Rendimiento por sucursal: Quito con actividad y Guayaquil en cero (no ausente).
        var branches = report.GetProperty("byBranch").EnumerateArray().ToDictionary(b => b.GetProperty("branchName").GetString()!);
        Assert.Equal(1, branches["Sucursal UIO"].GetProperty("held").GetInt32());
        Assert.Equal(1, branches["Sucursal UIO"].GetProperty("cancelled").GetInt32());
        Assert.Equal(80.0m, Num(branches["Sucursal UIO"], "occupancyPercent"));
        Assert.Equal(0, branches["Sucursal GYE"].GetProperty("held").GetInt32());
        Assert.Equal(JsonValueKind.Null, branches["Sucursal GYE"].GetProperty("occupancyPercent").ValueKind);

        // Por sesión: la cancelada no tiene ocupación; las demás sí.
        var list = report.GetProperty("sessionList").GetProperty("items").EnumerateArray().ToDictionary(x => x.GetProperty("sessionId").GetGuid());
        Assert.Equal(5, list.Count);
        Assert.Equal("Cancelled", list[s.Y3].GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, list[s.Y3].GetProperty("occupancyPercent").ValueKind);
        Assert.Equal(100.0m, Num(list[s.Y1], "occupancyPercent"));
        Assert.Equal(1, list[s.Y1].GetProperty("waitlisted").GetInt32());
        Assert.Equal("Completed", list[s.Y4].GetProperty("status").GetString());
        Assert.Equal(1, list[s.Y4].GetProperty("attended").GetInt32());
        Assert.Equal("Usuario carla@gym.test", list[s.Y1].GetProperty("instructorName").GetString());

        // Filtros por actividad y por clase.
        var spinning = await GetAsync(owner, $"classes/occupancy?{Range(-2, 7)}&activity=Spinning");
        Assert.Equal(1, spinning.GetProperty("sessionList").GetProperty("totalCount").GetInt32());
        Assert.Equal(75.0m, Num(Kpi(spinning, "occupancy_reserved"), "value"));
        var onlyYoga = await GetAsync(owner, $"classes/occupancy?{Range(-2, 7)}&classId={s.Yoga.Id}");
        Assert.Equal(4, onlyYoga.GetProperty("sessionList").GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task Una_sesion_pasada_que_nadie_cerro_es_pendiente_de_cierre_y_sin_sesiones_no_hay_ocupacion()
    {
        var gym = await NewGymAsync();
        var owner = gym.Tenant.Owner;
        var empty = await GetAsync(owner, $"classes/occupancy?{Range(-6, 0)}");
        Assert.Equal(JsonValueKind.Null, Kpi(empty, "occupancy_reserved").GetProperty("value").ValueKind);
        Assert.True(Kpi(empty, "occupancy_reserved").GetProperty("isEmpty").GetBoolean());
        Assert.Equal("NoData", Kpi(empty, "occupancy_reserved").GetProperty("variation").GetProperty("state").GetString());
        Assert.Equal(0, empty.GetProperty("sessionList").GetProperty("totalCount").GetInt32());

        var (_, yoga) = await NewClassAsync(gym, "carla@gym.test", "Yoga", "Yoga", capacity: 3);
        var session = await NewSessionAsync(owner, yoga.Id, At(1, 9));
        await SqlAsync($"UPDATE classes.class_sessions SET starts_at = now() - interval '5 hours', ends_at = now() - interval '4 hours' WHERE id = '{session}'");

        var report = await GetAsync(owner, $"classes/occupancy?{Range(-2, 1)}");
        Assert.Equal(1, report.GetProperty("sessions").GetProperty("pendingClosure").GetInt32());
        Assert.Equal(0, report.GetProperty("sessions").GetProperty("held").GetInt32());
        Assert.Equal("PendingClosure", report.GetProperty("sessionList").GetProperty("items")[0].GetProperty("status").GetString());
        // Sin asistencia registrada tampoco hay ocupación asistida: sin dato, no 0 %.
        Assert.Equal(JsonValueKind.Null, Kpi(report, "occupancy_attended").GetProperty("value").ValueKind);
    }

    [Fact]
    public async Task Las_cancelaciones_separan_sesiones_canceladas_de_reservas_canceladas_por_el_alumno()
    {
        var s = await BuildClassScenarioAsync();
        var owner = s.Gym.Tenant.Owner;

        var report = await GetAsync(owner, $"classes/cancellations?{Range(-2, 7)}");
        Assert.Equal(1m, Num(Kpi(report, "sessions_cancelled"), "value"));
        Assert.Equal(1m, Num(Kpi(report, "reservations_cancelled"), "value")); // la de Y3 se canceló al cancelar la sesión y no cuenta aquí
        Assert.Equal(0m, Num(Kpi(report, "late_cancellations"), "value"));
        Assert.Equal(11.1m, Num(Kpi(report, "cancellation_rate"), "value")); // 1 / (8 confirmadas + 1)
        Assert.Equal(1m, Num(Kpi(report, "no_shows"), "value"));

        var cancelled = report.GetProperty("cancelledSessions").GetProperty("items").EnumerateArray().Single();
        Assert.Equal(s.Y3, cancelled.GetProperty("sessionId").GetGuid());
        Assert.Equal("Mantenimiento de la sala", cancelled.GetProperty("reason").GetString());
        Assert.Equal(1, cancelled.GetProperty("reservationsAffected").GetInt32());

        var byClass = report.GetProperty("byClass").EnumerateArray().ToDictionary(c => c.GetProperty("className").GetString()!);
        Assert.Equal(4, byClass["Yoga de Carla"].GetProperty("sessions").GetInt32());
        Assert.Equal(1, byClass["Yoga de Carla"].GetProperty("cancelled").GetInt32());
        Assert.Equal(25.0m, Num(byClass["Yoga de Carla"], "cancelRatePercent"));
        Assert.Equal(0.0m, Num(byClass["Spinning de Pedro"], "cancelRatePercent"));
    }

    // ---- El instructor y la seguridad ---------------------------------------------------------------------------------------

    [Fact]
    public async Task El_entrenador_solo_ve_la_ocupacion_y_las_cancelaciones_de_sus_propias_sesiones()
    {
        var s = await BuildClassScenarioAsync();
        var carla = TestApi.Authenticated(api, await TestApi.LoginAsync(api, s.Gym.Tenant.Slug, "carla@gym.test", TestApi.UserPassword));
        var pedro = TestApi.Authenticated(api, await TestApi.LoginAsync(api, s.Gym.Tenant.Slug, "pedro@gym.test", TestApi.UserPassword));

        var carlaView = await GetAsync(carla, $"classes/occupancy?{Range(-2, 7)}");
        Assert.True(carlaView.GetProperty("instructorOnly").GetBoolean());
        Assert.Equal(4, carlaView.GetProperty("sessionList").GetProperty("totalCount").GetInt32()); // Y1..Y4; ninguna de Spinning
        Assert.Equal(["Yoga"], carlaView.GetProperty("byActivity").EnumerateArray().Select(a => a.GetProperty("name").GetString()));
        Assert.Equal(83.3m, Num(Kpi(carlaView, "occupancy_reserved"), "value"));

        var pedroView = await GetAsync(pedro, $"classes/occupancy?{Range(-2, 7)}");
        Assert.Equal(1, pedroView.GetProperty("sessionList").GetProperty("totalCount").GetInt32());
        Assert.Equal(75.0m, Num(Kpi(pedroView, "occupancy_reserved"), "value"));

        var carlaCancellations = await GetAsync(carla, $"classes/cancellations?{Range(-2, 7)}");
        Assert.True(carlaCancellations.GetProperty("instructorOnly").GetBoolean());
        Assert.Equal(1, carlaCancellations.GetProperty("cancelledSessions").GetProperty("totalCount").GetInt32());
        Assert.Equal(0, (await GetAsync(pedro, $"classes/cancellations?{Range(-2, 7)}")).GetProperty("cancelledSessions").GetProperty("totalCount").GetInt32());

        // Sin el permiso de operación, el entrenador no ve la asistencia al gimnasio ni lo financiero.
        Assert.Equal(HttpStatusCode.Forbidden, (await carla.GetAsync("/api/v1/reports/attendance")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await carla.GetAsync("/api/v1/reports/financial/collections")).StatusCode);
    }

    [Fact]
    public async Task Los_reportes_de_clases_exigen_operacion_o_instructor_y_se_acotan_al_alcance()
    {
        var s = await BuildClassScenarioAsync();
        var other = await NewGymAsync();

        // Cajero y contador tienen analítica pero ni operación ni instructor: 403. Sin sesión: 401.
        var cashier = await StaffAsync(s.Gym, "cajero@gym.test", "Cajero", s.Gym.Uio);
        var accountant = await StaffAsync(s.Gym, "contador@gym.test", "Contador", s.Gym.Uio);
        foreach (var url in new[] { "classes/occupancy", "classes/cancellations" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync($"/api/v1/reports/{url}")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await accountant.GetAsync($"/api/v1/reports/{url}")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().GetAsync($"/api/v1/reports/{url}")).StatusCode);
        }

        // Recepción de Guayaquil no ve Quito; el propietario no puede pedir sucursales ni clases de otro gimnasio.
        var receptionGye = await StaffAsync(s.Gym, "recep.gye@gym.test", "Recepcion", s.Gym.Gye);
        var scoped = await GetAsync(receptionGye, $"classes/occupancy?{Range(-2, 7)}");
        Assert.Equal(0, scoped.GetProperty("sessionList").GetProperty("totalCount").GetInt32());
        Assert.Equal(HttpStatusCode.Forbidden, (await receptionGye.GetAsync($"/api/v1/reports/classes/occupancy?branchId={s.Gym.Uio}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Gym.Tenant.Owner.GetAsync($"/api/v1/reports/classes/occupancy?branchId={other.Uio}")).StatusCode);
        var foreignClass = await GetAsync(s.Gym.Tenant.Owner, $"classes/occupancy?{Range(-2, 7)}&classId={Guid.NewGuid()}");
        Assert.Equal(0, foreignClass.GetProperty("sessionList").GetProperty("totalCount").GetInt32());
    }
}
