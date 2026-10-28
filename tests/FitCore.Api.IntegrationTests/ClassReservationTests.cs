using System.Net;
using System.Net.Http.Json;
using FitCore.Api.IntegrationTests.Infrastructure;
using Npgsql;
using static FitCore.Api.IntegrationTests.Infrastructure.CommercialTestApi;

namespace FitCore.Api.IntegrationTests;

[Collection(ApiCollection2.Name)]
public class ClassReservationTests(ApiFixture api)
{
    private static readonly TimeSpan Ecuador = TimeSpan.FromHours(-5);
    private const string StudentPassword = "Nueva-Clave-2026x";

    private sealed record TrainerRow(Guid Id, Guid UserId, string FullName);

    private sealed record ClassRow(Guid Id);

    private sealed record SessionRow(Guid Id, Guid ClassId, DateTimeOffset StartsAt, DateTimeOffset EndsAt, int Capacity, int BookedCount, int WaitlistCount, string Status);

    private sealed record ReservationRow(
        Guid Id, Guid SessionId, Guid ClassId, string ClassName, string Activity, Guid BranchId, string BranchName, string InstructorName, DateTimeOffset StartsAt,
        DateTimeOffset EndsAt, string SessionStatus, Guid MemberId, string MemberName, string MemberCode, string Status, string Source, DateTimeOffset BookedAt,
        DateTimeOffset? CancelledAt, string? CancelReason, bool LateCancellation);

    private sealed record AccessRow(Guid MemberId, string Email, string TemporaryPassword);

    private sealed record Setup(TestTenant Tenant, Guid Branch, Guid OtherBranch, TrainerRow Instructor, ClassRow Class, PlanDto Plan);

    private static DateTimeOffset Local(DayOfWeek day, int hour, int minute = 0, int weeksAhead = 0)
    {
        var date = DateTimeOffset.UtcNow.ToOffset(Ecuador).Date.AddDays(2);
        while (date.DayOfWeek != day)
        {
            date = date.AddDays(1);
        }

        return new DateTimeOffset(date.AddDays(7 * weeksAhead).AddHours(hour).AddMinutes(minute), Ecuador);
    }

    /// <summary>Instante dentro de exactamente N días (hora local de Ecuador): sirve para sesiones cercanas.</summary>
    private static DateTimeOffset InDays(int days, int hour) =>
        new(DateTimeOffset.UtcNow.ToOffset(Ecuador).Date.AddDays(days).AddHours(hour), Ecuador);

    private async Task<TrainerRow> NewTrainerAsync(TestTenant tenant, string email, params Guid[] branches)
    {
        var user = await TestApi.CreateUserAsync(tenant.Owner, email, "Entrenador", branches);
        var response = await tenant.Owner.PostAsJsonAsync("/api/v1/trainers", new { userId = user.Id, specialties = new[] { "Yoga" } });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TrainerRow>())!;
    }

    private static async Task<ClassRow> NewClassAsync(
        HttpClient owner, Guid branch, TrainerRow instructor, string name = "Yoga matutino", int capacity = 20, int notice = 30, int advanceDays = 30, int cancellation = 120,
        bool membership = true, bool paymentPolicy = true, int? minAge = null, bool waitlist = true)
    {
        var response = await owner.PostAsJsonAsync("/api/v1/group-classes", new
        {
            branchId = branch,
            name,
            activity = "Yoga",
            instructorId = instructor.Id,
            durationMinutes = 60,
            capacity,
            level = "AllLevels",
            minBookingNoticeMinutes = notice,
            maxBookingAdvanceDays = advanceDays,
            cancellationDeadlineMinutes = cancellation,
            waitlistEnabled = waitlist,
            waitlistCapacity = waitlist ? 5 : 0,
            offerWindowMinutes = 30,
            requireActiveMembership = membership,
            applyPaymentPolicy = paymentPolicy,
            minAge
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ClassRow>())!;
    }

    private async Task<Setup> NewSetupAsync(int capacity = 20)
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var branch = await TestApi.CreateBranchAsync(tenant.Owner, "UIO");
        var other = await TestApi.CreateBranchAsync(tenant.Owner, "GYE");
        var instructor = await NewTrainerAsync(tenant, "instructora@gym.test", branch);
        var groupClass = await NewClassAsync(tenant.Owner, branch, instructor, capacity: capacity);
        var plan = await CreatePlanAsync(tenant.Owner, "Mensual", 30m);
        return new Setup(tenant, branch, other, instructor, groupClass, plan);
    }

    private static async Task<SessionRow> NewSessionAsync(HttpClient owner, Guid classId, DateTimeOffset startsAt, int? capacity = null, Guid? instructorId = null)
    {
        var response = await owner.PostAsJsonAsync("/api/v1/class-sessions", new { classId, startsAt, capacity, instructorId });
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"Respondió {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<SessionRow>())!;
    }

    private static int _members;

    /// <summary>Alumno con correo propio; con <c>pay</c> inscrito en el plan y con ese pago (30 = saldado y activo; menos = activo con deuda).</summary>
    private static async Task<MemberDto> NewMemberAsync(Setup s, decimal? pay = 30m, Guid? branch = null, PlanDto? plan = null, bool enroll = true)
    {
        var n = Interlocked.Increment(ref _members);
        var response = await s.Tenant.Owner.PostAsJsonAsync("/api/v1/members", new
        {
            branchId = branch ?? s.Branch,
            firstName = $"Alumno{n}",
            lastName = "Prueba",
            idType = "Cedula",
            idNumber = MemberTests.NewCedula(),
            birthDate = "1995-04-12",
            email = $"alumno{n}.{Guid.NewGuid():N}"[..28] + "@alumnos.test",
            phone = "0991234567"
        });
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"Respondió {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        var member = (await response.Content.ReadFromJsonAsync<MemberDto>())!;
        if (enroll)
        {
            var enrolled = await s.Tenant.Owner.PostAsJsonAsync("/api/v1/memberships", new { memberId = member.Id, planId = (plan ?? s.Plan).Id });
            Assert.Equal(HttpStatusCode.Created, enrolled.StatusCode);
            if (pay is { } amount)
            {
                await PayAsync(s.Tenant.Owner, member.Id, amount);
            }
        }

        return member;
    }

    private async Task<HttpClient> StudentAsync(Setup s, MemberDto member)
    {
        var grant = await s.Tenant.Owner.PostAsync($"/api/v1/members/{member.Id}/access", null);
        Assert.Equal(HttpStatusCode.Created, grant.StatusCode);
        var access = (await grant.Content.ReadFromJsonAsync<AccessRow>())!;
        var temp = TestApi.Authenticated(api, await TestApi.LoginAsync(api, s.Tenant.Slug, access.Email, access.TemporaryPassword));
        var change = await temp.PostAsJsonAsync("/api/v1/auth/change-password", new { currentPassword = access.TemporaryPassword, newPassword = StudentPassword });
        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);
        return TestApi.Authenticated(api, await TestApi.LoginAsync(api, s.Tenant.Slug, access.Email, StudentPassword));
    }

    private async Task<HttpClient> StaffAsync(Setup s, string email, string role, Guid branch)
    {
        await TestApi.CreateUserAsync(s.Tenant.Owner, email, role, branch);
        return TestApi.Authenticated(api, await TestApi.LoginAsync(api, s.Tenant.Slug, email, TestApi.UserPassword));
    }

    private static Task<HttpResponseMessage> BookAsync(HttpClient client, Guid sessionId, Guid memberId, string? key = null) =>
        PostWithKeyAsync(client, "/api/v1/class-reservations", new { sessionId, memberId }, key);

    private static Task<HttpResponseMessage> BookMineAsync(HttpClient client, Guid sessionId, string? key = null) =>
        PostWithKeyAsync(client, "/api/v1/me/class-reservations", new { sessionId }, key);

    private static async Task<ReservationRow> BookOkAsync(HttpClient client, Guid sessionId, Guid memberId)
    {
        var response = await BookAsync(client, sessionId, memberId);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"Respondió {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<ReservationRow>())!;
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.True(response.StatusCode == status, $"Respondió {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        Assert.Equal(code, await TestApi.ProblemCodeAsync(response));
    }

    private static Task<SessionRow> GetSessionAsync(HttpClient client, Guid id) => client.GetFromJsonAsync<SessionRow>($"/api/v1/class-sessions/{id}")!;

    private static Task<HttpResponseMessage> CancelAsync(HttpClient client, Guid id, string? reason = null, bool mine = false) =>
        client.PostAsJsonAsync($"/api/v1/{(mine ? "me/" : string.Empty)}class-reservations/{id}/cancel", new { reason });

    private async Task SqlAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(api.AdminConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<long> CountAsync(Guid tenantId, string sql)
    {
        await using var connection = new NpgsqlConnection(api.AppConnection);
        await connection.OpenAsync();
        await using (var set = new NpgsqlCommand($"SELECT set_config('app.tenant_id', '{tenantId}', false)", connection))
        {
            await set.ExecuteScalarAsync();
        }

        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task SetPolicyAsync(HttpClient owner, string policy, int minPercent = 50) =>
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/v1/commercial-settings", new { accessPolicy = policy, minPaymentPercent = minPercent, dueDays = 7, maxDiscountPercent = 30 })).StatusCode);

    // ---- Reservar ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task El_personal_reserva_para_un_alumno_y_la_reserva_se_consulta_y_se_filtra()
    {
        var s = await NewSetupAsync();
        var member = await NewMemberAsync(s);
        var session = await NewSessionAsync(s.Tenant.Owner, s.Class.Id, Local(DayOfWeek.Monday, 9));

        var reservation = await BookOkAsync(s.Tenant.Owner, session.Id, member.Id);

        Assert.Equal("Confirmed", reservation.Status);
        Assert.Equal("Staff", reservation.Source);
        Assert.Equal(session.Id, reservation.SessionId);
        Assert.Equal("Yoga matutino", reservation.ClassName);
        Assert.Equal("Sucursal UIO", reservation.BranchName);
        Assert.Equal(member.Id, reservation.MemberId);
        Assert.Equal(member.Code, reservation.MemberCode);
        Assert.Equal(session.StartsAt, reservation.StartsAt);
        Assert.False(reservation.LateCancellation);
        Assert.Equal(1, (await GetSessionAsync(s.Tenant.Owner, session.Id)).BookedCount);

        var read = (await s.Tenant.Owner.GetFromJsonAsync<ReservationRow>($"/api/v1/class-reservations/{reservation.Id}"))!;
        Assert.Equal(reservation.Id, read.Id);
        Assert.Equal(1, (await s.Tenant.Owner.GetFromJsonAsync<PagedDto<ReservationRow>>($"/api/v1/class-reservations?sessionId={session.Id}"))!.TotalCount);
        Assert.Equal(1, (await s.Tenant.Owner.GetFromJsonAsync<PagedDto<ReservationRow>>($"/api/v1/class-reservations?memberId={member.Id}&status=Confirmed"))!.TotalCount);
        Assert.Equal(0, (await s.Tenant.Owner.GetFromJsonAsync<PagedDto<ReservationRow>>("/api/v1/class-reservations?status=Cancelled"))!.TotalCount);
        Assert.Equal(0, (await s.Tenant.Owner.GetFromJsonAsync<PagedDto<ReservationRow>>($"/api/v1/class-reservations?memberId={Guid.NewGuid()}"))!.TotalCount);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Tenant.Owner.GetAsync($"/api/v1/class-reservations/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Repetir_la_solicitud_con_la_misma_clave_devuelve_la_misma_reserva_y_un_alumno_no_reserva_dos_veces()
    {
        var s = await NewSetupAsync();
        var member = await NewMemberAsync(s);
        var other = await NewMemberAsync(s);
        var session = await NewSessionAsync(s.Tenant.Owner, s.Class.Id, Local(DayOfWeek.Monday, 9));
        var key = Guid.NewGuid().ToString();

        var first = await BookAsync(s.Tenant.Owner, session.Id, member.Id, key);
        var retry = await BookAsync(s.Tenant.Owner, session.Id, member.Id, key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal((await first.Content.ReadFromJsonAsync<ReservationRow>())!.Id, (await retry.Content.ReadFromJsonAsync<ReservationRow>())!.Id);
        Assert.Equal(1L, await CountAsync(s.Tenant.Id, $"SELECT count(*) FROM classes.class_reservations WHERE session_id = '{session.Id}'"));

        // La misma clave con otro contenido se rechaza; sin clave no se reserva; otra clave para la misma reserva choca con la regla de no repetir.
        await AssertProblemAsync(await BookAsync(s.Tenant.Owner, session.Id, other.Id, key), HttpStatusCode.UnprocessableEntity, "idempotency.key_reused");
        await AssertProblemAsync(await s.Tenant.Owner.PostAsJsonAsync("/api/v1/class-reservations", new { sessionId = session.Id, memberId = other.Id }), HttpStatusCode.UnprocessableEntity, "idempotency.key_required");
        await AssertProblemAsync(await BookAsync(s.Tenant.Owner, session.Id, member.Id), HttpStatusCode.Conflict, "classes.already_booked");
        Assert.Equal(1L, await CountAsync(s.Tenant.Id, $"SELECT count(*) FROM classes.class_reservations WHERE session_id = '{session.Id}'"));
    }

    [Fact]
    public async Task No_se_supera_la_capacidad_y_cancelar_libera_el_cupo_en_el_acto()
    {
        var s = await NewSetupAsync();
        var session = await NewSessionAsync(s.Tenant.Owner, s.Class.Id, Local(DayOfWeek.Monday, 9), capacity: 2);
        var (a, b, c) = (await NewMemberAsync(s), await NewMemberAsync(s), await NewMemberAsync(s));

        var first = await BookOkAsync(s.Tenant.Owner, session.Id, a.Id);
        await BookOkAsync(s.Tenant.Owner, session.Id, b.Id);
        await AssertProblemAsync(await BookAsync(s.Tenant.Owner, session.Id, c.Id), HttpStatusCode.Conflict, "classes.session_full");
        Assert.Equal(2, (await GetSessionAsync(s.Tenant.Owner, session.Id)).BookedCount);

        Assert.Equal(HttpStatusCode.OK, (await CancelAsync(s.Tenant.Owner, first.Id)).StatusCode);
        Assert.Equal(1, (await GetSessionAsync(s.Tenant.Owner, session.Id)).BookedCount);
        await BookOkAsync(s.Tenant.Owner, session.Id, c.Id);
        Assert.Equal(2, (await GetSessionAsync(s.Tenant.Owner, session.Id)).BookedCount);

        // Quien canceló no recupera su cupo: la sesión volvió a llenarse con otros.
        await AssertProblemAsync(await BookAsync(s.Tenant.Owner, session.Id, a.Id), HttpStatusCode.Conflict, "classes.session_full");
    }

    [Fact]
    public async Task Solicitudes_simultaneas_por_el_ultimo_cupo_lo_dejan_para_uno_solo()
    {
        var s = await NewSetupAsync();
        var session = await NewSessionAsync(s.Tenant.Owner, s.Class.Id, Local(DayOfWeek.Tuesday, 18), capacity: 3);
        var members = new List<MemberDto>();
        for (var i = 0; i < 8; i++)
        {
            members.Add(await NewMemberAsync(s));
        }

        var responses = await Task.WhenAll(members.Select(m => BookAsync(s.Tenant.Owner, session.Id, m.Id)));

        foreach (var response in responses.Where(r => r.StatusCode != HttpStatusCode.Created))
        {
            Assert.True(response.StatusCode == HttpStatusCode.Conflict, $"Respondió {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
            Assert.Equal("classes.session_full", await TestApi.ProblemCodeAsync(response));
        }

        Assert.Equal(3, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(3L, await CountAsync(s.Tenant.Id, $"SELECT count(*) FROM classes.class_reservations WHERE session_id = '{session.Id}' AND status = 'Confirmed'"));
        Assert.Equal(3, (await GetSessionAsync(s.Tenant.Owner, session.Id)).BookedCount);
    }

    [Fact]
    public async Task El_mismo_alumno_en_paralelo_reserva_una_sola_vez_y_la_misma_clave_en_paralelo_devuelve_siempre_la_misma_reserva()
    {
        var s = await NewSetupAsync();
        var session = await NewSessionAsync(s.Tenant.Owner, s.Class.Id, Local(DayOfWeek.Wednesday, 18));
        var other = await NewSessionAsync(s.Tenant.Owner, s.Class.Id, Local(DayOfWeek.Thursday, 18));
        var member = await NewMemberAsync(s);

        // Doble clic con claves distintas: gana una, las demás saben que ya está reservado.
        var clicks = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => BookAsync(s.Tenant.Owner, session.Id, member.Id)));
        foreach (var response in clicks.Where(r => r.StatusCode != HttpStatusCode.Created))
        {
            Assert.True(response.StatusCode == HttpStatusCode.Conflict, $"Respondió {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
            Assert.Equal("classes.already_booked", await TestApi.ProblemCodeAsync(response));
        }

        Assert.Equal(1, clicks.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(1L, await CountAsync(s.Tenant.Id, $"SELECT count(*) FROM classes.class_reservations WHERE session_id = '{session.Id}'"));

        // Reintentos con la misma clave: todos reciben la misma reserva.
        var key = Guid.NewGuid().ToString();
        var retries = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => BookAsync(s.Tenant.Owner, other.Id, member.Id, key)));
        foreach (var response in retries)
        {
            Assert.True(response.StatusCode == HttpStatusCode.Created, $"Respondió {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }

        var ids = await Task.WhenAll(retries.Select(async r => (await r.Content.ReadFromJsonAsync<ReservationRow>())!.Id));
        Assert.Single(ids.Distinct());
        Assert.Equal(1L, await CountAsync(s.Tenant.Id, $"SELECT count(*) FROM classes.class_reservations WHERE session_id = '{other.Id}'"));
    }

    [Fact]
    public async Task Las_reservas_respetan_la_ventana_de_apertura_y_cierre_y_el_estado_de_la_sesion()
    {
        var s = await NewSetupAsync();
        var member = await NewMemberAsync(s);
        var monday = Local(DayOfWeek.Monday, 9);

        // Se abre un día antes: una sesión de dentro de días todavía no admite reservas.
        var opensLate = await NewClassAsync(s.Tenant.Owner, s.Branch, s.Instructor, "Abre tarde", advanceDays: 1, notice: 0);
        var notOpen = await NewSessionAsync(s.Tenant.Owner, opensLate.Id, Local(DayOfWeek.Monday, 11, weeksAhead: 1));
        await AssertProblemAsync(await BookAsync(s.Tenant.Owner, notOpen.Id, member.Id), HttpStatusCode.Conflict, "classes.booking_not_open");

        // Cierra 7 días antes: una sesión de pasado mañana ya no admite reservas.
        var closesEarly = await NewClassAsync(s.Tenant.Owner, s.Branch, s.Instructor, "Cierra pronto", advanceDays: 60, notice: 7 * 24 * 60);
        var closed = await NewSessionAsync(s.Tenant.Owner, closesEarly.Id, InDays(2, 13));
        await AssertProblemAsync(await BookAsync(s.Tenant.Owner, closed.Id, member.Id), HttpStatusCode.Conflict, "classes.booking_closed");

        // Sesión cancelada y clase desactivada.
        var session = await NewSessionAsync(s.Tenant.Owner, s.Class.Id, monday);
        await s.Tenant.Owner.PostAsJsonAsync($"/api/v1/class-sessions/{session.Id}/cancel", new { reason = "Feriado" });
        await AssertProblemAsync(await BookAsync(s.Tenant.Owner, session.Id, member.Id), HttpStatusCode.Conflict, "classes.session_not_bookable");

        var live = await NewSessionAsync(s.Tenant.Owner, s.Class.Id, Local(DayOfWeek.Tuesday, 9));
        await s.Tenant.Owner.PatchAsJsonAsync($"/api/v1/group-classes/{s.Class.Id}/status", new { isActive = false });
        await AssertProblemAsync(await BookAsync(s.Tenant.Owner, live.Id, member.Id), HttpStatusCode.Conflict, "classes.class_inactive");
        await s.Tenant.Owner.PatchAsJsonAsync($"/api/v1/group-classes/{s.Class.Id}/status", new { isActive = true });

        // Una sesión que ya empezó (la desplazamos en la base) no admite reservas.
        await SqlAsync($"UPDATE classes.class_sessions SET starts_at = now() - interval '30 minutes', ends_at = now() + interval '30 minutes' WHERE id = '{live.Id}'");
        await AssertProblemAsync(await BookAsync(s.Tenant.Owner, live.Id, member.Id), HttpStatusCode.Conflict, "classes.session_not_bookable");
        Assert.Equal(HttpStatusCode.NotFound, (await BookAsync(s.Tenant.Owner, Guid.NewGuid(), member.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await BookAsync(s.Tenant.Owner, notOpen.Id, Guid.NewGuid())).StatusCode);
    }

    [Fact]
    public async Task Solo_reservan_los_alumnos_con_membresia_activa_que_cubra_la_sesion_y_valga_en_la_sucursal()
    {
        var s = await NewSetupAsync();
        var session = await NewSessionAsync(s.Tenant.Owner, s.Class.Id, Local(DayOfWeek.Monday, 9));
        var noMembership = await NewMemberAsync(s, enroll: false);
        var pending = await NewMemberAsync(s, pay: null);
        var active = await NewMemberAsync(s);

        await AssertProblemAsync(await BookAsync(s.Tenant.Owner, session.Id, noMembership.Id), HttpStatusCode.Conflict, "classes.no_active_membership");
        await AssertProblemAsync(await BookAsync(s.Tenant.Owner, session.Id, pending.Id), HttpStatusCode.Conflict, "classes.membership_pending_payment");
        await BookOkAsync(s.Tenant.Owner, session.Id, active.Id);

        // La membresía vence antes de la sesión: no sirve para reservar esa fecha.
        var wide = await NewClassAsync(s.Tenant.Owner, s.Branch, s.Instructor, "Lejana", advanceDays: 120);
        var far = await NewSessionAsync(s.Tenant.Owner, wide.Id, Local(DayOfWeek.Monday, 15, weeksAhead: 9));
        await AssertProblemAsync(await BookAsync(s.Tenant.Owner, far.Id, active.Id), HttpStatusCode.Conflict, "classes.no_active_membership");

        // Un plan restringido a otra sucursal no vale aquí; uno sin restricción vale también para un alumno de otra sucursal.
        var gyeOnly = await CreatePlanAsync(s.Tenant.Owner, "Solo GYE", 30m, [s.OtherBranch]);
        var restricted = await NewMemberAsync(s, branch: s.OtherBranch, plan: gyeOnly);
        await AssertProblemAsync(await BookAsync(s.Tenant.Owner, session.Id, restricted.Id), HttpStatusCode.Conflict, "classes.plan_branch_restricted");
        var visitor = await NewMemberAsync(s, branch: s.OtherBranch);
        await BookOkAsync(s.Tenant.Owner, session.Id, visitor.Id);

        // Un alumno inactivo no reserva, ni siquiera en una clase que no exige membresía.
        var open = await NewClassAsync(s.Tenant.Owner, s.Branch, s.Instructor, "Abierta", membership: false, paymentPolicy: false);
        var openSession = await NewSessionAsync(s.Tenant.Owner, open.Id, Local(DayOfWeek.Friday, 9));
        await BookOkAsync(s.Tenant.Owner, openSession.Id, noMembership.Id);
        await SqlAsync($"UPDATE members.members SET status = 'Inactive' WHERE id = '{active.Id}'");
        await AssertProblemAsync(await BookAsync(s.Tenant.Owner, openSession.Id, active.Id), HttpStatusCode.Conflict, "classes.member_inactive");
    }

    [Fact]
    public async Task La_politica_de_pagos_del_gimnasio_decide_si_un_alumno_con_deuda_reserva()
    {
        var s = await NewSetupAsync();
        var session = await NewSessionAsync(s.Tenant.Owner, s.Class.Id, Local(DayOfWeek.Monday, 9));
        var noCheck = await NewClassAsync(s.Tenant.Owner, s.Branch, s.Instructor, "Sin control de pagos", paymentPolicy: false);
        var noCheckSession = await NewSessionAsync(s.Tenant.Owner, noCheck.Id, Local(DayOfWeek.Wednesday, 9));
        var debtor = await NewMemberAsync(s, pay: 10m); // 10 de 30: activa, debe 20 dentro del plazo

        // Hasta vencer, el valor por defecto deja reservar.
        await BookOkAsync(s.Tenant.Owner, session.Id, debtor.Id);

        await SetPolicyAsync(s.Tenant.Owner, "RequireFullPayment");
        var second = await NewSessionAsync(s.Tenant.Owner, s.Class.Id, Local(DayOfWeek.Tuesday, 9));
        await AssertProblemAsync(await BookAsync(s.Tenant.Owner, second.Id, debtor.Id), HttpStatusCode.Conflict, "classes.debt_pending");

        await SetPolicyAsync(s.Tenant.Owner, "AllowWithMinimumPayment", 50);
        await AssertProblemAsync(await BookAsync(s.Tenant.Owner, second.Id, debtor.Id), HttpStatusCode.Conflict, "classes.minimum_payment_not_met");
        await SetPolicyAsync(s.Tenant.Owner, "AllowWithMinimumPayment", 30);
        Assert.Equal(HttpStatusCode.Created, (await BookAsync(s.Tenant.Owner, second.Id, debtor.Id)).StatusCode);

        // Con la cuota vencida, el valor por defecto también la rechaza; una clase sin control de pagos no.
        await SetPolicyAsync(s.Tenant.Owner, "AllowUntilOverdue");
        await SqlAsync($"UPDATE billing.payment_obligations SET issue_date = issue_date - 20, due_date = due_date - 20 WHERE member_id = '{debtor.Id}'");
        var third = await NewSessionAsync(s.Tenant.Owner, s.Class.Id, Local(DayOfWeek.Thursday, 9));
        await AssertProblemAsync(await BookAsync(s.Tenant.Owner, third.Id, debtor.Id), HttpStatusCode.Conflict, "classes.debt_overdue");
        await BookOkAsync(s.Tenant.Owner, noCheckSession.Id, debtor.Id);
    }

    [Fact]
    public async Task La_edad_minima_exige_fecha_de_nacimiento_y_se_calcula_a_la_fecha_de_la_sesion()
    {
        var s = await NewSetupAsync();
        var adults = await NewClassAsync(s.Tenant.Owner, s.Branch, s.Instructor, "Solo mayores", minAge: 18);
        var session = await NewSessionAsync(s.Tenant.Owner, adults.Id, Local(DayOfWeek.Monday, 9));
        var adult = await NewMemberAsync(s);
        var minor = await NewMemberAsync(s);
        var unknown = await NewMemberAsync(s);
        await SqlAsync($"UPDATE members.members SET birth_date = (now() - interval '15 years')::date WHERE id = '{minor.Id}'");
        await SqlAsync($"UPDATE members.members SET birth_date = NULL WHERE id = '{unknown.Id}'");

        await BookOkAsync(s.Tenant.Owner, session.Id, adult.Id);
        await AssertProblemAsync(await BookAsync(s.Tenant.Owner, session.Id, minor.Id), HttpStatusCode.Conflict, "classes.below_min_age");
        await AssertProblemAsync(await BookAsync(s.Tenant.Owner, session.Id, unknown.Id), HttpStatusCode.Conflict, "classes.member_age_unknown");
    }

    [Fact]
    public async Task Un_alumno_no_puede_estar_en_dos_sesiones_que_se_cruzan()
    {
        var s = await NewSetupAsync();
        var second = await NewTrainerAsync(s.Tenant, "segundo@gym.test", s.Branch);
        var member = await NewMemberAsync(s);
        var first = await NewSessionAsync(s.Tenant.Owner, s.Class.Id, Local(DayOfWeek.Monday, 9));
        var crossing = await NewSessionAsync(s.Tenant.Owner, s.Class.Id, Local(DayOfWeek.Monday, 9, 30), instructorId: second.Id);
        var adjacent = await NewSessionAsync(s.Tenant.Owner, s.Class.Id, Local(DayOfWeek.Monday, 10));

        await BookOkAsync(s.Tenant.Owner, first.Id, member.Id);
        await AssertProblemAsync(await BookAsync(s.Tenant.Owner, crossing.Id, member.Id), HttpStatusCode.Conflict, "classes.member_schedule_conflict");
        await BookOkAsync(s.Tenant.Owner, adjacent.Id, member.Id);
    }

    // ---- Cancelar ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Cancelar_fuera_de_plazo_solo_lo_hace_el_personal_con_motivo_y_queda_marcado()
    {
        var s = await NewSetupAsync();
        // Plazo de 7 días: una sesión de dentro de unos días ya está fuera de plazo.
        var strict = await NewClassAsync(s.Tenant.Owner, s.Branch, s.Instructor, "Plazo largo", cancellation: 7 * 24 * 60);
        var session = await NewSessionAsync(s.Tenant.Owner, strict.Id, Local(DayOfWeek.Monday, 9));
        var member = await NewMemberAsync(s);
        var student = await StudentAsync(s, member);
        var reservation = (await (await BookMineAsync(student, session.Id)).Content.ReadFromJsonAsync<ReservationRow>())!;
        Assert.Equal("Member", reservation.Source);

        await AssertProblemAsync(await CancelAsync(student, reservation.Id, mine: true), HttpStatusCode.Conflict, "classes.cancellation_deadline_passed");
        await AssertProblemAsync(await CancelAsync(s.Tenant.Owner, reservation.Id), HttpStatusCode.UnprocessableEntity, "classes.late_cancellation_reason_required");
        await AssertProblemAsync(await CancelAsync(s.Tenant.Owner, reservation.Id, "ok"), HttpStatusCode.UnprocessableEntity, "classes.late_cancellation_reason_required");

        var cancelled = (await (await CancelAsync(s.Tenant.Owner, reservation.Id, "El alumno avisó por teléfono")).Content.ReadFromJsonAsync<ReservationRow>())!;
        Assert.Equal("Cancelled", cancelled.Status);
        Assert.True(cancelled.LateCancellation);
        Assert.Equal("El alumno avisó por teléfono", cancelled.CancelReason);
        Assert.NotNull(cancelled.CancelledAt);
        Assert.Equal(0, (await GetSessionAsync(s.Tenant.Owner, session.Id)).BookedCount);
    }

    [Fact]
    public async Task Cancelar_a_tiempo_es_idempotente_libera_el_cupo_y_ya_no_se_cancela_una_sesion_empezada()
    {
        var s = await NewSetupAsync();
        var session = await NewSessionAsync(s.Tenant.Owner, s.Class.Id, Local(DayOfWeek.Monday, 9));
        var started = await NewSessionAsync(s.Tenant.Owner, s.Class.Id, Local(DayOfWeek.Tuesday, 9));
        var member = await NewMemberAsync(s);
        var reservation = await BookOkAsync(s.Tenant.Owner, session.Id, member.Id);
        var inStarted = await BookOkAsync(s.Tenant.Owner, started.Id, member.Id);

        var cancelled = (await (await CancelAsync(s.Tenant.Owner, reservation.Id)).Content.ReadFromJsonAsync<ReservationRow>())!;
        Assert.Equal("Cancelled", cancelled.Status);
        Assert.False(cancelled.LateCancellation);

        // Repetir la cancelación devuelve lo mismo, y una vez cancelada se puede reservar de nuevo (otra reserva).
        var again = (await (await CancelAsync(s.Tenant.Owner, reservation.Id, "Otra vez")).Content.ReadFromJsonAsync<ReservationRow>())!;
        // PostgreSQL guarda microsegundos: la lectura puede diferir en la fracción que .NET maneja de más.
        Assert.True((cancelled.CancelledAt!.Value - again.CancelledAt!.Value).Duration() < TimeSpan.FromMilliseconds(1));
        Assert.Null(again.CancelReason);
        var rebooked = await BookOkAsync(s.Tenant.Owner, session.Id, member.Id);
        Assert.NotEqual(reservation.Id, rebooked.Id);
        Assert.Equal(2L, await CountAsync(s.Tenant.Id, $"SELECT count(*) FROM classes.class_reservations WHERE session_id = '{session.Id}'"));

        // Cuando la sesión ya empezó, nadie cancela.
        await SqlAsync($"UPDATE classes.class_sessions SET starts_at = now() - interval '10 minutes', ends_at = now() + interval '50 minutes' WHERE id = '{started.Id}'");
        await AssertProblemAsync(await CancelAsync(s.Tenant.Owner, inStarted.Id, "Tarde"), HttpStatusCode.Conflict, "classes.session_started");
        Assert.Equal(HttpStatusCode.NotFound, (await CancelAsync(s.Tenant.Owner, Guid.NewGuid())).StatusCode);
    }

    [Fact]
    public async Task Cancelar_una_sesion_o_una_serie_cancela_sus_reservas_y_la_capacidad_no_baja_de_lo_reservado()
    {
        var s = await NewSetupAsync();
        var session = await NewSessionAsync(s.Tenant.Owner, s.Class.Id, Local(DayOfWeek.Monday, 9), capacity: 3);
        var (a, b) = (await NewMemberAsync(s), await NewMemberAsync(s));
        var first = await BookOkAsync(s.Tenant.Owner, session.Id, a.Id);
        await BookOkAsync(s.Tenant.Owner, session.Id, b.Id);

        // La capacidad puede bajar hasta lo reservado, no por debajo.
        await AssertProblemAsync(await s.Tenant.Owner.PatchAsJsonAsync($"/api/v1/class-sessions/{session.Id}", new { capacity = 1 }), HttpStatusCode.Conflict, "classes.capacity_below_booked");
        Assert.Equal(HttpStatusCode.OK, (await s.Tenant.Owner.PatchAsJsonAsync($"/api/v1/class-sessions/{session.Id}", new { capacity = 2 })).StatusCode);

        var cancelled = await s.Tenant.Owner.PostAsJsonAsync($"/api/v1/class-sessions/{session.Id}/cancel", new { reason = "Instructora enferma" });
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        var read = (await s.Tenant.Owner.GetFromJsonAsync<ReservationRow>($"/api/v1/class-reservations/{first.Id}"))!;
        Assert.Equal("Cancelled", read.Status);
        Assert.Equal("Sesión cancelada: Instructora enferma", read.CancelReason);
        Assert.False(read.LateCancellation);
        Assert.Equal(0, (await GetSessionAsync(s.Tenant.Owner, session.Id)).BookedCount);

        // Y las de una serie entera.
        var start = DateOnly.FromDateTime(Local(DayOfWeek.Monday, 0, weeksAhead: 1).DateTime);
        var seriesResponse = await s.Tenant.Owner.PostAsJsonAsync("/api/v1/class-sessions/recurrences",
            new { classId = s.Class.Id, startDate = start, endDate = start.AddDays(13), startTime = "17:00", days = new[] { "Monday", "Thursday" } });
        Assert.Equal(HttpStatusCode.Created, seriesResponse.StatusCode);
        var seriesId = (await seriesResponse.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("series").GetProperty("id").GetGuid();
        var sessions = (await s.Tenant.Owner.GetFromJsonAsync<PagedDto<SessionRow>>($"/api/v1/class-sessions?seriesId={seriesId}"))!.Items;
        foreach (var member in new[] { a, b })
        {
            await BookOkAsync(s.Tenant.Owner, sessions[0].Id, member.Id);
        }

        await s.Tenant.Owner.PostAsJsonAsync($"/api/v1/class-sessions/recurrences/{seriesId}/cancel", new { reason = "Cierre del local" });
        Assert.Equal(0L, await CountAsync(s.Tenant.Id, $"SELECT count(*) FROM classes.class_reservations WHERE session_id = '{sessions[0].Id}' AND status <> 'Cancelled'"));
    }

    // ---- Portal del alumno -------------------------------------------------------------------------------------------

    [Fact]
    public async Task El_alumno_reserva_y_cancela_lo_suyo_y_no_puede_tocar_lo_de_otro_ni_usar_los_endpoints_del_personal()
    {
        var s = await NewSetupAsync();
        var session = await NewSessionAsync(s.Tenant.Owner, s.Class.Id, Local(DayOfWeek.Monday, 9));
        var ana = await NewMemberAsync(s);
        var luis = await NewMemberAsync(s);
        var anaClient = await StudentAsync(s, ana);
        var luisClient = await StudentAsync(s, luis);

        // El alumno reserva por sí mismo: aunque envíe otro alumno en el cuerpo, la reserva es suya.
        var response = await PostWithKeyAsync(anaClient, "/api/v1/me/class-reservations", new { sessionId = session.Id, memberId = luis.Id });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var mine = (await response.Content.ReadFromJsonAsync<ReservationRow>())!;
        Assert.Equal(ana.Id, mine.MemberId);
        Assert.Equal("Member", mine.Source);

        var list = (await anaClient.GetFromJsonAsync<PagedDto<ReservationRow>>("/api/v1/me/class-reservations?upcomingOnly=true"))!;
        Assert.Equal([mine.Id], list.Items.Select(r => r.Id));
        Assert.Empty((await luisClient.GetFromJsonAsync<PagedDto<ReservationRow>>("/api/v1/me/class-reservations"))!.Items);

        // La reserva de otro responde como si no existiera; los endpoints del personal están vedados.
        Assert.Equal(HttpStatusCode.NotFound, (await CancelAsync(luisClient, mine.Id, mine: true)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await anaClient.GetAsync("/api/v1/class-reservations")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await BookAsync(anaClient, session.Id, ana.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await CancelAsync(anaClient, mine.Id)).StatusCode);

        // Reservar dos veces responde con el motivo; cancelar a tiempo funciona y libera el cupo.
        await AssertProblemAsync(await BookMineAsync(anaClient, session.Id), HttpStatusCode.Conflict, "classes.already_booked");
        var cancelled = (await (await CancelAsync(anaClient, mine.Id, mine: true)).Content.ReadFromJsonAsync<ReservationRow>())!;
        Assert.Equal("Cancelled", cancelled.Status);
        Assert.Empty((await anaClient.GetFromJsonAsync<PagedDto<ReservationRow>>("/api/v1/me/class-reservations?upcomingOnly=true&status=Confirmed"))!.Items);

        // El portal es solo del alumno: el personal de recepción no tiene `me.read`.
        var reception = await StaffAsync(s, "recep@gym.test", "Recepcion", s.Branch);
        Assert.Equal(HttpStatusCode.Forbidden, (await reception.GetAsync("/api/v1/me/class-reservations")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await BookMineAsync(reception, session.Id)).StatusCode);
    }

    // ---- Permisos, sucursales y aislamiento -------------------------------------------------------------------------

    [Fact]
    public async Task Cada_rol_ve_y_reserva_solo_lo_que_su_permiso_y_sus_sucursales_permiten()
    {
        var s = await NewSetupAsync();
        var gyeTrainer = await NewTrainerAsync(s.Tenant, "gye@gym.test", s.OtherBranch);
        var gyeClass = await NewClassAsync(s.Tenant.Owner, s.OtherBranch, gyeTrainer, "Yoga GYE");
        var uio = await NewSessionAsync(s.Tenant.Owner, s.Class.Id, Local(DayOfWeek.Tuesday, 9));
        var gye = await NewSessionAsync(s.Tenant.Owner, gyeClass.Id, Local(DayOfWeek.Monday, 9));
        var member = await NewMemberAsync(s);
        var inGye = await BookOkAsync(s.Tenant.Owner, gye.Id, member.Id);

        var admin = await StaffAsync(s, "admin.uio@gym.test", "AdministradorSucursal", s.Branch);
        var reception = await StaffAsync(s, "recep.uio@gym.test", "Recepcion", s.Branch);
        var cashier = await StaffAsync(s, "cajero.uio@gym.test", "Cajero", s.Branch);
        var trainer = TestApi.Authenticated(api, await TestApi.LoginAsync(api, s.Tenant.Slug, "instructora@gym.test", TestApi.UserPassword));

        // Quien administra UIO reserva en UIO; GYE no existe para él ni siquiera como destino de una reserva o cancelación.
        await BookOkAsync(admin, uio.Id, member.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await BookAsync(admin, gye.Id, (await NewMemberAsync(s)).Id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/v1/class-reservations/{inGye.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await CancelAsync(admin, inGye.Id, "No es mío")).StatusCode);
        await AssertProblemAsync(await admin.GetAsync($"/api/v1/class-reservations?branchId={s.OtherBranch}"), HttpStatusCode.Forbidden, "branches.not_allowed");
        Assert.DoesNotContain(inGye.Id, (await admin.GetFromJsonAsync<PagedDto<ReservationRow>>("/api/v1/class-reservations"))!.Items.Select(r => r.Id));

        // Recepción reserva y cancela; el entrenador solo consulta; caja no tiene ni lectura.
        var other = await NewMemberAsync(s);
        var byReception = await BookOkAsync(reception, uio.Id, other.Id);
        Assert.Equal(HttpStatusCode.OK, (await CancelAsync(reception, byReception.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await trainer.GetAsync("/api/v1/class-reservations")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await BookAsync(trainer, uio.Id, other.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await CancelAsync(trainer, byReception.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync("/api/v1/class-reservations")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await BookAsync(cashier, uio.Id, other.Id)).StatusCode);
    }

    [Fact]
    public async Task Un_gimnasio_no_reserva_ni_ve_nada_de_otro_y_la_base_lo_garantiza()
    {
        var a = await NewSetupAsync();
        var b = await NewSetupAsync();
        var sessionA = await NewSessionAsync(a.Tenant.Owner, a.Class.Id, Local(DayOfWeek.Monday, 9));
        var memberA = await NewMemberAsync(a);
        var memberB = await NewMemberAsync(b);
        var reservation = await BookOkAsync(a.Tenant.Owner, sessionA.Id, memberA.Id);

        // Ni la sesión ni el alumno de otro gimnasio existen; los identificadores no se pueden mezclar.
        Assert.Equal(HttpStatusCode.NotFound, (await BookAsync(b.Tenant.Owner, sessionA.Id, memberB.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await BookAsync(a.Tenant.Owner, sessionA.Id, memberB.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Tenant.Owner.GetAsync($"/api/v1/class-reservations/{reservation.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await CancelAsync(b.Tenant.Owner, reservation.Id, "Ajena")).StatusCode);
        Assert.Empty((await b.Tenant.Owner.GetFromJsonAsync<PagedDto<ReservationRow>>("/api/v1/class-reservations"))!.Items);

        // Con el rol de la aplicación, B no ve las filas de A; con A fijado, sí (la prueba no pasa en vacío).
        Assert.Equal(0L, await CountAsync(b.Tenant.Id, "SELECT count(*) FROM classes.class_reservations"));
        Assert.Equal(1L, await CountAsync(a.Tenant.Id, "SELECT count(*) FROM classes.class_reservations"));

        // Y aunque se salte la aplicación, la base rechaza mezclar alumno o sesión de otro gimnasio.
        await using var admin = new NpgsqlConnection(api.AdminConnection);
        await admin.OpenAsync();
        async Task<string> ErrorAsync(string sql)
        {
            await using var command = new NpgsqlCommand(sql, admin);
            return (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).SqlState;
        }

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, await ErrorAsync(Insert(a.Tenant.Id, a.Branch, sessionA.Id, memberB.Id)));
    }

    private static string Insert(Guid tenant, Guid branch, Guid session, Guid member, string status = "Confirmed") => $"""
        INSERT INTO classes.class_reservations (id, tenant_id, branch_id, session_id, member_id, status, source, late_cancellation, created_at)
        VALUES (gen_random_uuid(), '{tenant}', '{branch}', '{session}', '{member}', '{status}', 'Staff', false, now())
        """;

    [Fact]
    public async Task La_base_impone_capacidad_unicidad_y_estados_aunque_se_salte_la_aplicacion()
    {
        var s = await NewSetupAsync();
        var session = await NewSessionAsync(s.Tenant.Owner, s.Class.Id, Local(DayOfWeek.Monday, 9), capacity: 1);
        var cancelledSession = await NewSessionAsync(s.Tenant.Owner, s.Class.Id, Local(DayOfWeek.Tuesday, 9));
        var (a, b, c, d) = (await NewMemberAsync(s), await NewMemberAsync(s), await NewMemberAsync(s), await NewMemberAsync(s));
        var first = await BookOkAsync(s.Tenant.Owner, session.Id, a.Id);
        await s.Tenant.Owner.PostAsJsonAsync($"/api/v1/class-sessions/{cancelledSession.Id}/cancel", new { reason = "Prueba" });
        await using var admin = new NpgsqlConnection(api.AdminConnection);
        await admin.OpenAsync();

        async Task<string> ErrorAsync(string sql)
        {
            await using var command = new NpgsqlCommand(sql, admin);
            return (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).SqlState;
        }

        // Capacidad: el cupo único ya está ocupado, ofrecido cuenta como ocupado, y una sesión cancelada no admite reservas.
        Assert.Equal("FC001", await ErrorAsync(Insert(s.Tenant.Id, s.Branch, session.Id, b.Id)));
        Assert.Equal("FC001", await ErrorAsync(Insert(s.Tenant.Id, s.Branch, session.Id, b.Id, "Offered")));
        Assert.Equal("FC002", await ErrorAsync(Insert(s.Tenant.Id, s.Branch, cancelledSession.Id, b.Id)));
        await using (var waiting = new NpgsqlCommand(Insert(s.Tenant.Id, s.Branch, session.Id, b.Id, "Waitlisted"), admin))
        {
            Assert.Equal(1, await waiting.ExecuteNonQueryAsync()); // la lista de espera no ocupa cupo
        }

        // Una reserva viva por alumno y sesión.
        Assert.Equal(PostgresErrorCodes.UniqueViolation, await ErrorAsync(Insert(s.Tenant.Id, s.Branch, session.Id, a.Id, "Waitlisted")));

        // Estados y coherencia de la cancelación.
        Assert.Equal(PostgresErrorCodes.CheckViolation, await ErrorAsync($"UPDATE classes.class_reservations SET status = 'Asistió' WHERE id = '{first.Id}'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await ErrorAsync($"UPDATE classes.class_reservations SET status = 'Cancelled' WHERE id = '{first.Id}'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await ErrorAsync($"UPDATE classes.class_reservations SET late_cancellation = true WHERE id = '{first.Id}'"));

        // Lo que ancla la reserva no cambia, y una cancelada no se reactiva.
        Assert.Equal(PostgresErrorCodes.CheckViolation, await ErrorAsync($"UPDATE classes.class_reservations SET member_id = '{b.Id}' WHERE id = '{first.Id}'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await ErrorAsync($"UPDATE classes.class_reservations SET session_id = '{cancelledSession.Id}' WHERE id = '{first.Id}'"));
        // Al cancelar, el cupo pasa a quien esperaba (bloque 5); se agranda la sesión para que el cupo no sea lo primero que rechace la base.
        await CancelAsync(s.Tenant.Owner, first.Id);
        await SqlAsync($"UPDATE classes.class_sessions SET capacity = 5 WHERE id = '{session.Id}'");
        Assert.Equal(PostgresErrorCodes.CheckViolation, await ErrorAsync($"UPDATE classes.class_reservations SET status = 'Confirmed', cancelled_at = NULL WHERE id = '{first.Id}'"));

        // La capacidad de la sesión no baja de lo ocupado: con tres cupos tomados (uno ofrecido), bajar a uno lo impide la base.
        await SqlAsync(Insert(s.Tenant.Id, s.Branch, session.Id, c.Id));
        await SqlAsync(Insert(s.Tenant.Id, s.Branch, session.Id, d.Id));
        Assert.Equal("FC003", await ErrorAsync($"UPDATE classes.class_sessions SET capacity = 1 WHERE id = '{session.Id}'"));
    }

    [Fact]
    public async Task El_rol_de_la_aplicacion_no_puede_borrar_reservas()
    {
        var s = await NewSetupAsync();
        var session = await NewSessionAsync(s.Tenant.Owner, s.Class.Id, Local(DayOfWeek.Monday, 9));
        var reservation = await BookOkAsync(s.Tenant.Owner, session.Id, (await NewMemberAsync(s)).Id);
        await using var connection = new NpgsqlConnection(api.AppConnection);
        await connection.OpenAsync();
        await using (var set = new NpgsqlCommand($"SELECT set_config('app.tenant_id', '{s.Tenant.Id}', false)", connection))
        {
            await set.ExecuteScalarAsync();
        }

        await using var delete = new NpgsqlCommand($"DELETE FROM classes.class_reservations WHERE id = '{reservation.Id}'", connection);
        var error = await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
    }

    [Fact]
    public async Task Reservar_y_cancelar_dejan_rastro_en_la_auditoria_sin_datos_de_mas()
    {
        var s = await NewSetupAsync();
        var session = await NewSessionAsync(s.Tenant.Owner, s.Class.Id, Local(DayOfWeek.Monday, 9));
        var reservation = await BookOkAsync(s.Tenant.Owner, session.Id, (await NewMemberAsync(s)).Id);
        await CancelAsync(s.Tenant.Owner, reservation.Id, "Prueba de auditoría");

        var audit = (await s.Tenant.Owner.GetFromJsonAsync<PagedDto<AuditDto>>($"/api/v1/audit-logs?entity=ClassReservation&entityId={reservation.Id}"))!;
        Assert.True(audit.TotalCount >= 2, $"Auditoría con {audit.TotalCount} registros");
    }
}
