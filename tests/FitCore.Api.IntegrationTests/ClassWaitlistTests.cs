using System.Net;
using System.Net.Http.Json;
using FitCore.Api.IntegrationTests.Infrastructure;
using FitCore.Classes.Application.Reservations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static FitCore.Api.IntegrationTests.Infrastructure.CommercialTestApi;

namespace FitCore.Api.IntegrationTests;

[Collection(ApiCollection3.Name)]
public class ClassWaitlistTests(ApiFixture api)
{
    private static readonly TimeSpan Ecuador = TimeSpan.FromHours(-5);
    private const string StudentPassword = "Nueva-Clave-2026x";

    private sealed record TrainerRow(Guid Id, Guid UserId, string FullName);

    private sealed record ClassRow(Guid Id);

    private sealed record SessionRow(Guid Id, int Capacity, int BookedCount, int WaitlistCount, string Status);

    private sealed record ReservationRow(
        Guid Id, Guid SessionId, Guid MemberId, string Status, string Source, DateTimeOffset BookedAt, DateTimeOffset? CancelledAt, string? CancelReason,
        bool LateCancellation, int? WaitlistPosition, DateTimeOffset? OfferedAt, DateTimeOffset? OfferExpiresAt);

    private sealed record AccessRow(Guid MemberId, string Email, string TemporaryPassword);

    private sealed record Setup(TestTenant Tenant, Guid Branch, TrainerRow Instructor, ClassRow Class, PlanDto Plan);

    private static DateTimeOffset Local(DayOfWeek day, int hour, int minute = 0, int weeksAhead = 0)
    {
        var date = DateTimeOffset.UtcNow.ToOffset(Ecuador).Date.AddDays(2);
        while (date.DayOfWeek != day)
        {
            date = date.AddDays(1);
        }

        return new DateTimeOffset(date.AddDays(7 * weeksAhead).AddHours(hour).AddMinutes(minute), Ecuador);
    }

    private async Task<TrainerRow> NewTrainerAsync(TestTenant tenant, string email, params Guid[] branches)
    {
        var user = await TestApi.CreateUserAsync(tenant.Owner, email, "Entrenador", branches);
        var response = await tenant.Owner.PostAsJsonAsync("/api/v1/trainers", new { userId = user.Id, specialties = new[] { "Yoga" } });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TrainerRow>())!;
    }

    private static async Task<ClassRow> NewClassAsync(
        HttpClient owner, Guid branch, TrainerRow instructor, string name = "Yoga matutino", bool waitlist = true, int waitlistCapacity = 5, int offerWindow = 30,
        int cancellation = 120)
    {
        var response = await owner.PostAsJsonAsync("/api/v1/group-classes", new
        {
            branchId = branch,
            name,
            activity = "Yoga",
            instructorId = instructor.Id,
            durationMinutes = 60,
            capacity = 20,
            level = "AllLevels",
            minBookingNoticeMinutes = 30,
            maxBookingAdvanceDays = 30,
            cancellationDeadlineMinutes = cancellation,
            waitlistEnabled = waitlist,
            waitlistCapacity = waitlist ? waitlistCapacity : 0,
            offerWindowMinutes = offerWindow,
            requireActiveMembership = true,
            applyPaymentPolicy = true
        });
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"Respondió {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<ClassRow>())!;
    }

    private async Task<Setup> NewSetupAsync()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var branch = await TestApi.CreateBranchAsync(tenant.Owner, "UIO");
        var instructor = await NewTrainerAsync(tenant, "instructora@gym.test", branch);
        var groupClass = await NewClassAsync(tenant.Owner, branch, instructor);
        var plan = await CreatePlanAsync(tenant.Owner, "Mensual", 30m);
        return new Setup(tenant, branch, instructor, groupClass, plan);
    }

    private static async Task<SessionRow> NewSessionAsync(HttpClient owner, Guid classId, DateTimeOffset startsAt, int? capacity = null)
    {
        var response = await owner.PostAsJsonAsync("/api/v1/class-sessions", new { classId, startsAt, capacity });
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"Respondió {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<SessionRow>())!;
    }

    private static int _members;

    private static async Task<MemberDto> NewMemberAsync(Setup s, bool enroll = true)
    {
        var n = Interlocked.Increment(ref _members);
        var response = await s.Tenant.Owner.PostAsJsonAsync("/api/v1/members", new
        {
            branchId = s.Branch,
            firstName = $"Espera{n}",
            lastName = "Prueba",
            idType = "Cedula",
            idNumber = MemberTests.NewCedula(),
            birthDate = "1995-04-12",
            email = $"espera{n}.{Guid.NewGuid():N}"[..28] + "@alumnos.test",
            phone = "0991234567"
        });
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"Respondió {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        var member = (await response.Content.ReadFromJsonAsync<MemberDto>())!;
        if (enroll)
        {
            Assert.Equal(HttpStatusCode.Created, (await s.Tenant.Owner.PostAsJsonAsync("/api/v1/memberships", new { memberId = member.Id, planId = s.Plan.Id })).StatusCode);
            await PayAsync(s.Tenant.Owner, member.Id, 30m);
        }

        return member;
    }

    private async Task<HttpClient> StudentAsync(Setup s, MemberDto member)
    {
        var grant = await s.Tenant.Owner.PostAsync($"/api/v1/members/{member.Id}/access", null);
        Assert.Equal(HttpStatusCode.Created, grant.StatusCode);
        var access = (await grant.Content.ReadFromJsonAsync<AccessRow>())!;
        var temp = TestApi.Authenticated(api, await TestApi.LoginAsync(api, s.Tenant.Slug, access.Email, access.TemporaryPassword));
        Assert.Equal(HttpStatusCode.NoContent, (await temp.PostAsJsonAsync("/api/v1/auth/change-password", new { currentPassword = access.TemporaryPassword, newPassword = StudentPassword })).StatusCode);
        return TestApi.Authenticated(api, await TestApi.LoginAsync(api, s.Tenant.Slug, access.Email, StudentPassword));
    }

    private async Task<HttpClient> StaffAsync(Setup s, string email, string role)
    {
        await TestApi.CreateUserAsync(s.Tenant.Owner, email, role, s.Branch);
        return TestApi.Authenticated(api, await TestApi.LoginAsync(api, s.Tenant.Slug, email, TestApi.UserPassword));
    }

    private static Task<HttpResponseMessage> BookAsync(HttpClient client, Guid sessionId, Guid memberId, bool joinWaitlist = false) =>
        PostWithKeyAsync(client, "/api/v1/class-reservations", new { sessionId, memberId, joinWaitlist });

    private static async Task<ReservationRow> ReadAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.Created)
    {
        Assert.True(response.StatusCode == expected, $"Respondió {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<ReservationRow>())!;
    }

    private static async Task<ReservationRow> BookOkAsync(HttpClient client, Guid sessionId, Guid memberId) => await ReadAsync(await BookAsync(client, sessionId, memberId));

    private static async Task<ReservationRow> JoinAsync(HttpClient client, Guid sessionId, Guid memberId) => await ReadAsync(await BookAsync(client, sessionId, memberId, joinWaitlist: true));

    private static Task<ReservationRow> GetAsync(HttpClient client, Guid id) => client.GetFromJsonAsync<ReservationRow>($"/api/v1/class-reservations/{id}")!;

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string action, Guid id, bool mine = false, object? body = null) =>
        client.PostAsJsonAsync($"/api/v1/{(mine ? "me/" : string.Empty)}class-reservations/{id}/{action}", body ?? new { });

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.True(response.StatusCode == status, $"Respondió {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        Assert.Equal(code, await TestApi.ProblemCodeAsync(response));
    }

    private static Task<SessionRow> GetSessionAsync(HttpClient client, Guid id) => client.GetFromJsonAsync<SessionRow>($"/api/v1/class-sessions/{id}")!;

    private async Task SqlAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(api.AdminConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Hace vencer una invitación (sin esperar el plazo real) y deja el resto del registro coherente.</summary>
    private Task ExpireOfferInDbAsync(Guid reservationId) =>
        SqlAsync($"UPDATE classes.class_reservations SET offered_at = now() - interval '1 hour', offer_expires_at = now() - interval '1 minute' WHERE id = '{reservationId}'");

    private async Task<WaitlistRunSummary> RunWorkerAsync() =>
        await api.Services.GetRequiredService<WaitlistRunner>().RunAsync(CancellationToken.None);

    /// <summary>Sesión de capacidad 1 con un alumno confirmado y los demás en la lista de espera, en orden de llegada.</summary>
    private async Task<(SessionRow Session, ReservationRow Holder, List<(MemberDto Member, ReservationRow Reservation)> Waiting)> FullSessionWithWaitlistAsync(Setup s, int waiting, DayOfWeek day = DayOfWeek.Monday)
    {
        var session = await NewSessionAsync(s.Tenant.Owner, s.Class.Id, Local(day, 9), capacity: 1);
        var holder = await BookOkAsync(s.Tenant.Owner, session.Id, (await NewMemberAsync(s)).Id);
        var list = new List<(MemberDto, ReservationRow)>();
        for (var i = 0; i < waiting; i++)
        {
            var member = await NewMemberAsync(s);
            list.Add((member, await JoinAsync(s.Tenant.Owner, session.Id, member.Id)));
        }

        return (session, holder, list);
    }

    // ---- Entrar a la fila -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Con_la_sesion_llena_solo_entra_a_la_fila_quien_lo_pide_y_la_posicion_es_estable()
    {
        var s = await NewSetupAsync();
        var (session, _, waiting) = await FullSessionWithWaitlistAsync(s, 3);

        Assert.All(waiting, w => Assert.Equal("Waitlisted", w.Reservation.Status));
        Assert.Equal([1, 2, 3], waiting.Select(w => w.Reservation.WaitlistPosition!.Value));
        var fresh = await GetSessionAsync(s.Tenant.Owner, session.Id);
        Assert.Equal(1, fresh.BookedCount);
        Assert.Equal(3, fresh.WaitlistCount);

        // El listado trae a la fila en orden de llegada, con su posición.
        var list = (await s.Tenant.Owner.GetFromJsonAsync<PagedDto<ReservationRow>>($"/api/v1/class-reservations?sessionId={session.Id}&status=Waitlisted"))!;
        Assert.Equal(waiting.Select(w => w.Reservation.Id), list.Items.Select(r => r.Id));

        // Si el segundo sale, el tercero avanza; los demás no se mueven.
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(s.Tenant.Owner, "cancel", waiting[1].Reservation.Id, body: new { })).StatusCode);
        Assert.Equal(1, (await GetAsync(s.Tenant.Owner, waiting[0].Reservation.Id)).WaitlistPosition);
        Assert.Equal(2, (await GetAsync(s.Tenant.Owner, waiting[2].Reservation.Id)).WaitlistPosition);
        Assert.Null((await GetAsync(s.Tenant.Owner, waiting[1].Reservation.Id)).WaitlistPosition);

        // Sin pedirlo, la sesión llena responde "llena"; quien ya espera no puede entrar otra vez.
        var late = await NewMemberAsync(s);
        await AssertProblemAsync(await BookAsync(s.Tenant.Owner, session.Id, late.Id), HttpStatusCode.Conflict, "classes.session_full");
        await AssertProblemAsync(await BookAsync(s.Tenant.Owner, session.Id, waiting[0].Member.Id, joinWaitlist: true), HttpStatusCode.Conflict, "classes.already_booked");
    }

    [Fact]
    public async Task La_lista_de_espera_respeta_el_tope_de_la_clase_y_que_la_clase_la_admita_y_la_elegibilidad()
    {
        var s = await NewSetupAsync();
        var small = await NewClassAsync(s.Tenant.Owner, s.Branch, s.Instructor, "Fila de uno", waitlistCapacity: 1);
        var none = await NewClassAsync(s.Tenant.Owner, s.Branch, s.Instructor, "Sin fila", waitlist: false);
        var smallSession = await NewSessionAsync(s.Tenant.Owner, small.Id, Local(DayOfWeek.Monday, 9), capacity: 1);
        var noneSession = await NewSessionAsync(s.Tenant.Owner, none.Id, Local(DayOfWeek.Tuesday, 9), capacity: 1);
        var (a, b, c) = (await NewMemberAsync(s), await NewMemberAsync(s), await NewMemberAsync(s));

        await BookOkAsync(s.Tenant.Owner, smallSession.Id, a.Id);
        await JoinAsync(s.Tenant.Owner, smallSession.Id, b.Id);
        await AssertProblemAsync(await BookAsync(s.Tenant.Owner, smallSession.Id, c.Id, joinWaitlist: true), HttpStatusCode.Conflict, "classes.waitlist_full");

        await BookOkAsync(s.Tenant.Owner, noneSession.Id, a.Id);
        await AssertProblemAsync(await BookAsync(s.Tenant.Owner, noneSession.Id, b.Id, joinWaitlist: true), HttpStatusCode.Conflict, "classes.waitlist_disabled");

        // Para esperar también hay que ser elegible, y con cupo libre la solicitud reserva normalmente.
        var noMembership = await NewMemberAsync(s, enroll: false);
        await AssertProblemAsync(await BookAsync(s.Tenant.Owner, smallSession.Id, noMembership.Id, joinWaitlist: true), HttpStatusCode.Conflict, "classes.no_active_membership");
        var roomy = await NewSessionAsync(s.Tenant.Owner, s.Class.Id, Local(DayOfWeek.Wednesday, 9));
        Assert.Equal("Confirmed", (await JoinAsync(s.Tenant.Owner, roomy.Id, c.Id)).Status);
    }

    // ---- Oferta, aceptación y rechazo -------------------------------------------------------------------------------

    [Fact]
    public async Task Al_liberarse_un_cupo_se_reserva_para_el_primero_con_plazo_y_nadie_puede_quitarselo()
    {
        var s = await NewSetupAsync();
        var (session, holder, waiting) = await FullSessionWithWaitlistAsync(s, 2);
        var before = DateTimeOffset.UtcNow;

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(s.Tenant.Owner, "cancel", holder.Id)).StatusCode);

        var first = await GetAsync(s.Tenant.Owner, waiting[0].Reservation.Id);
        Assert.Equal("Offered", first.Status);
        Assert.Null(first.WaitlistPosition);
        Assert.NotNull(first.OfferedAt);
        Assert.InRange(first.OfferExpiresAt!.Value, before.AddMinutes(29), before.AddMinutes(32)); // plazo de la clase: 30 min
        Assert.Equal("Waitlisted", (await GetAsync(s.Tenant.Owner, waiting[1].Reservation.Id)).Status);
        Assert.Equal(1, (await GetAsync(s.Tenant.Owner, waiting[1].Reservation.Id)).WaitlistPosition);

        // El cupo ofrecido cuenta como ocupado: nadie lo toma por la puerta de atrás.
        var session2 = await GetSessionAsync(s.Tenant.Owner, session.Id);
        Assert.Equal(1, session2.BookedCount);
        Assert.Equal(1, session2.WaitlistCount);
        var newcomer = await NewMemberAsync(s);
        await AssertProblemAsync(await BookAsync(s.Tenant.Owner, session.Id, newcomer.Id), HttpStatusCode.Conflict, "classes.session_full");
    }

    [Fact]
    public async Task Aceptar_a_tiempo_confirma_el_cupo_y_solo_lo_acepta_su_dueno()
    {
        var s = await NewSetupAsync();
        var (session, holder, waiting) = await FullSessionWithWaitlistAsync(s, 2);
        var student = await StudentAsync(s, waiting[0].Member);
        var other = await StudentAsync(s, waiting[1].Member);
        await PostAsync(s.Tenant.Owner, "cancel", holder.Id);
        var offer = waiting[0].Reservation.Id;

        // Otro alumno no puede responder por él; el personal sin permiso tampoco.
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(other, "accept", offer, mine: true)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(student, "accept", offer)).StatusCode);

        var accepted = await ReadAsync(await PostAsync(student, "accept", offer, mine: true), HttpStatusCode.OK);
        Assert.Equal("Confirmed", accepted.Status);
        Assert.Null(accepted.WaitlistPosition);
        Assert.Equal(1, (await GetSessionAsync(s.Tenant.Owner, session.Id)).BookedCount);

        // Aceptar otra vez devuelve lo mismo; lo que no fue ofrecido no se acepta.
        Assert.Equal("Confirmed", (await ReadAsync(await PostAsync(student, "accept", offer, mine: true), HttpStatusCode.OK)).Status);
        await AssertProblemAsync(await PostAsync(other, "accept", waiting[1].Reservation.Id, mine: true), HttpStatusCode.Conflict, "classes.reservation_not_offered");
        await AssertProblemAsync(await PostAsync(other, "decline", waiting[1].Reservation.Id, mine: true), HttpStatusCode.Conflict, "classes.reservation_not_offered");
    }

    [Fact]
    public async Task Rechazar_o_cancelar_la_invitacion_pasa_el_cupo_al_siguiente_sin_plazo_de_cancelacion()
    {
        var s = await NewSetupAsync();
        // Plazo de cancelación de 7 días: quien ocupa un cupo ya no podría cancelar, pero rechazar una invitación no tiene plazo.
        var strict = await NewClassAsync(s.Tenant.Owner, s.Branch, s.Instructor, "Plazo largo", cancellation: 7 * 24 * 60);
        var session = await NewSessionAsync(s.Tenant.Owner, strict.Id, Local(DayOfWeek.Tuesday, 9), capacity: 1);
        var holder = await BookOkAsync(s.Tenant.Owner, session.Id, (await NewMemberAsync(s)).Id);
        var members = new List<MemberDto>();
        var reservations = new List<ReservationRow>();
        for (var i = 0; i < 3; i++)
        {
            members.Add(await NewMemberAsync(s));
            reservations.Add(await JoinAsync(s.Tenant.Owner, session.Id, members[i].Id));
        }

        var firstClient = await StudentAsync(s, members[0]);
        await PostAsync(s.Tenant.Owner, "cancel", holder.Id, body: new { reason = "Excepción acordada con el alumno" });
        Assert.Equal("Offered", (await GetAsync(s.Tenant.Owner, reservations[0].Id)).Status);

        // El primero rechaza: se le cancela sin "fuera de plazo" y el segundo recibe la oferta.
        var declined = await ReadAsync(await PostAsync(firstClient, "decline", reservations[0].Id, mine: true), HttpStatusCode.OK);
        Assert.Equal("Cancelled", declined.Status);
        Assert.False(declined.LateCancellation);
        Assert.Equal("Invitación rechazada", declined.CancelReason);
        Assert.Equal("Offered", (await GetAsync(s.Tenant.Owner, reservations[1].Id)).Status);
        Assert.Equal(1, (await GetAsync(s.Tenant.Owner, reservations[2].Id)).WaitlistPosition);

        // El personal puede rechazar en su nombre, y salir de la fila no tiene plazo.
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(s.Tenant.Owner, "decline", reservations[1].Id)).StatusCode);
        Assert.Equal("Offered", (await GetAsync(s.Tenant.Owner, reservations[2].Id)).Status);
        var lastClient = await StudentAsync(s, members[2]);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(lastClient, "cancel", reservations[2].Id, mine: true)).StatusCode);

        // Sin nadie más en la fila, el cupo queda libre para cualquiera.
        Assert.Equal(0, (await GetSessionAsync(s.Tenant.Owner, session.Id)).BookedCount);
        await BookOkAsync(s.Tenant.Owner, session.Id, (await NewMemberAsync(s)).Id);
    }

    [Fact]
    public async Task Una_invitacion_vencida_no_se_acepta_y_el_worker_la_vence_y_ofrece_el_cupo_al_siguiente()
    {
        var s = await NewSetupAsync();
        var (session, holder, waiting) = await FullSessionWithWaitlistAsync(s, 2);
        var student = await StudentAsync(s, waiting[0].Member);
        await PostAsync(s.Tenant.Owner, "cancel", holder.Id);
        await ExpireOfferInDbAsync(waiting[0].Reservation.Id);

        // Venció pero el Worker aún no ha pasado: no se puede aceptar.
        await AssertProblemAsync(await PostAsync(student, "accept", waiting[0].Reservation.Id, mine: true), HttpStatusCode.Conflict, "classes.offer_expired");

        var summary = await RunWorkerAsync();

        Assert.True(summary.OffersExpired >= 1, $"Invitaciones vencidas: {summary.OffersExpired}");
        var expired = await GetAsync(s.Tenant.Owner, waiting[0].Reservation.Id);
        Assert.Equal("Expired", expired.Status);
        var next = await GetAsync(s.Tenant.Owner, waiting[1].Reservation.Id);
        Assert.Equal("Offered", next.Status);
        Assert.Equal(1, (await GetSessionAsync(s.Tenant.Owner, session.Id)).BookedCount);

        // Repetir el barrido no cambia nada.
        var again = await RunWorkerAsync();
        Assert.Equal(0, again.OffersExpired);
        Assert.Equal("Offered", (await GetAsync(s.Tenant.Owner, waiting[1].Reservation.Id)).Status);
        await ExpireOfferInDbAsync(waiting[1].Reservation.Id);
        await RunWorkerAsync();
        Assert.Equal(0, (await GetSessionAsync(s.Tenant.Owner, session.Id)).BookedCount);
        // Quien perdió el plazo puede volver a reservar: como el cupo quedó libre, entra confirmado.
        Assert.Equal("Confirmed", (await JoinAsync(s.Tenant.Owner, session.Id, waiting[0].Member.Id)).Status);
    }

    [Fact]
    public async Task Se_ofrece_al_siguiente_elegible_y_quien_no_lo_es_conserva_su_lugar()
    {
        var s = await NewSetupAsync();
        var (session, holder, waiting) = await FullSessionWithWaitlistAsync(s, 2);
        // El primero de la fila deja de ser elegible (se desactiva): se le salta, pero sigue primero en la fila.
        await SqlAsync($"UPDATE members.members SET status = 'Inactive' WHERE id = '{waiting[0].Member.Id}'");

        await PostAsync(s.Tenant.Owner, "cancel", holder.Id);

        Assert.Equal("Waitlisted", (await GetAsync(s.Tenant.Owner, waiting[0].Reservation.Id)).Status);
        Assert.Equal(1, (await GetAsync(s.Tenant.Owner, waiting[0].Reservation.Id)).WaitlistPosition);
        Assert.Equal("Offered", (await GetAsync(s.Tenant.Owner, waiting[1].Reservation.Id)).Status);
        Assert.Equal(1, (await GetSessionAsync(s.Tenant.Owner, session.Id)).BookedCount);
    }

    [Fact]
    public async Task Subir_la_capacidad_ofrece_los_cupos_nuevos_en_el_acto_y_el_worker_completa_lo_que_falte()
    {
        var s = await NewSetupAsync();
        var (session, _, waiting) = await FullSessionWithWaitlistAsync(s, 3);

        var patched = await s.Tenant.Owner.PatchAsJsonAsync($"/api/v1/class-sessions/{session.Id}", new { capacity = 3 });
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);

        Assert.Equal("Offered", (await GetAsync(s.Tenant.Owner, waiting[0].Reservation.Id)).Status);
        Assert.Equal("Offered", (await GetAsync(s.Tenant.Owner, waiting[1].Reservation.Id)).Status);
        Assert.Equal("Waitlisted", (await GetAsync(s.Tenant.Owner, waiting[2].Reservation.Id)).Status);
        Assert.Equal(3, (await GetSessionAsync(s.Tenant.Owner, session.Id)).BookedCount);

        // Un cupo libre que no se ofreció (lo simulamos con la base) lo ofrece el barrido del Worker.
        await SqlAsync($"UPDATE classes.class_sessions SET capacity = 4 WHERE id = '{session.Id}'");
        var summary = await RunWorkerAsync();
        Assert.True(summary.SeatsOffered >= 1, $"Cupos ofrecidos: {summary.SeatsOffered}");
        Assert.Equal("Offered", (await GetAsync(s.Tenant.Owner, waiting[2].Reservation.Id)).Status);
    }

    [Fact]
    public async Task Mientras_se_libera_un_cupo_nadie_se_cuela_delante_de_la_fila()
    {
        var s = await NewSetupAsync();
        var session = await NewSessionAsync(s.Tenant.Owner, s.Class.Id, Local(DayOfWeek.Thursday, 18), capacity: 2);
        var (a, b, waiter) = (await NewMemberAsync(s), await NewMemberAsync(s), await NewMemberAsync(s));
        var first = await BookOkAsync(s.Tenant.Owner, session.Id, a.Id);
        await BookOkAsync(s.Tenant.Owner, session.Id, b.Id);
        var waiting = await JoinAsync(s.Tenant.Owner, session.Id, waiter.Id);
        var newcomers = new List<MemberDto>();
        for (var i = 0; i < 5; i++)
        {
            newcomers.Add(await NewMemberAsync(s));
        }

        // Se cancela un cupo y, a la vez, cinco recién llegados intentan reservar.
        var cancel = PostAsync(s.Tenant.Owner, "cancel", first.Id);
        var attempts = newcomers.Select(m => BookAsync(s.Tenant.Owner, session.Id, m.Id)).ToList();
        await Task.WhenAll(attempts.Append(cancel));

        Assert.Equal(HttpStatusCode.OK, cancel.Result.StatusCode);
        foreach (var response in attempts.Select(t => t.Result))
        {
            Assert.True(response.StatusCode == HttpStatusCode.Conflict, $"Un recién llegado obtuvo {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
            Assert.Equal("classes.session_full", await TestApi.ProblemCodeAsync(response));
        }

        Assert.Equal("Offered", (await GetAsync(s.Tenant.Owner, waiting.Id)).Status);
        Assert.Equal(2, (await GetSessionAsync(s.Tenant.Owner, session.Id)).BookedCount);
    }

    [Fact]
    public async Task Las_solicitudes_simultaneas_sobre_la_misma_invitacion_dejan_un_solo_resultado()
    {
        var s = await NewSetupAsync();
        var (session, holder, waiting) = await FullSessionWithWaitlistAsync(s, 2);
        await PostAsync(s.Tenant.Owner, "cancel", holder.Id);
        var offer = waiting[0].Reservation.Id;

        // Aceptar y rechazar a la vez: gana una; la otra ve el resultado ya decidido, sin errores del servidor.
        var results = await Task.WhenAll(
            PostAsync(s.Tenant.Owner, "accept", offer),
            PostAsync(s.Tenant.Owner, "decline", offer),
            PostAsync(s.Tenant.Owner, "accept", offer));

        Assert.All(results, r => Assert.True((int)r.StatusCode < 500, $"Error del servidor: {(int)r.StatusCode}"));
        var final = (await GetAsync(s.Tenant.Owner, offer)).Status;
        Assert.Contains(final, new[] { "Confirmed", "Cancelled" });
        var booked = (await GetSessionAsync(s.Tenant.Owner, session.Id)).BookedCount;
        Assert.Equal(1, booked); // un solo cupo ocupado: o el primero lo confirmó, o pasó al segundo
        if (final == "Cancelled")
        {
            Assert.Equal("Offered", (await GetAsync(s.Tenant.Owner, waiting[1].Reservation.Id)).Status);
        }
    }

    [Fact]
    public async Task Cancelar_la_sesion_cancela_la_fila_y_el_worker_cierra_la_fila_de_las_sesiones_que_empezaron()
    {
        var s = await NewSetupAsync();
        var (cancelled, _, cancelledWaiting) = await FullSessionWithWaitlistAsync(s, 2, DayOfWeek.Monday);
        var (started, startedHolder, startedWaiting) = await FullSessionWithWaitlistAsync(s, 2, DayOfWeek.Tuesday);

        await s.Tenant.Owner.PostAsJsonAsync($"/api/v1/class-sessions/{cancelled.Id}/cancel", new { reason = "Feriado" });
        Assert.All(cancelledWaiting, w => Assert.Equal("Cancelled", GetAsync(s.Tenant.Owner, w.Reservation.Id).Result.Status));

        // La otra sesión ya empezó con gente en espera (la desplazamos en la base).
        await PostAsync(s.Tenant.Owner, "cancel", startedHolder.Id); // el primero queda ofrecido
        await SqlAsync($"UPDATE classes.class_sessions SET starts_at = now() - interval '10 minutes', ends_at = now() + interval '50 minutes' WHERE id = '{started.Id}'");
        var summary = await RunWorkerAsync();

        Assert.True(summary.WaitlistsClosed >= 2, $"Cerradas: {summary.WaitlistsClosed}");
        Assert.All(startedWaiting, w => Assert.Equal("Expired", GetAsync(s.Tenant.Owner, w.Reservation.Id).Result.Status));
    }

    // ---- Permisos, aislamiento y base -------------------------------------------------------------------------------

    [Fact]
    public async Task Recepcion_responde_invitaciones_y_el_resto_de_roles_no()
    {
        var s = await NewSetupAsync();
        var (_, holder, waiting) = await FullSessionWithWaitlistAsync(s, 2);
        var reception = await StaffAsync(s, "recep@gym.test", "Recepcion");
        var cashier = await StaffAsync(s, "cajero@gym.test", "Cajero");
        var trainer = TestApi.Authenticated(api, await TestApi.LoginAsync(api, s.Tenant.Slug, "instructora@gym.test", TestApi.UserPassword));
        await PostAsync(s.Tenant.Owner, "cancel", holder.Id);

        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(cashier, "accept", waiting[0].Reservation.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(trainer, "accept", waiting[0].Reservation.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(trainer, "decline", waiting[0].Reservation.Id)).StatusCode);
        Assert.Equal("Confirmed", (await ReadAsync(await PostAsync(reception, "accept", waiting[0].Reservation.Id), HttpStatusCode.OK)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(reception, "accept", Guid.NewGuid())).StatusCode);
    }

    [Fact]
    public async Task Un_gimnasio_no_ve_ni_responde_la_fila_de_otro()
    {
        var a = await NewSetupAsync();
        var b = await NewSetupAsync();
        var (session, holder, waiting) = await FullSessionWithWaitlistAsync(a, 1);
        await PostAsync(a.Tenant.Owner, "cancel", holder.Id);

        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(b.Tenant.Owner, "accept", waiting[0].Reservation.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(b.Tenant.Owner, "decline", waiting[0].Reservation.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Tenant.Owner.GetAsync($"/api/v1/class-reservations/{waiting[0].Reservation.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await BookAsync(b.Tenant.Owner, session.Id, (await NewMemberAsync(b)).Id, joinWaitlist: true)).StatusCode);
        Assert.Equal("Offered", (await GetAsync(a.Tenant.Owner, waiting[0].Reservation.Id)).Status);
    }

    private static string Insert(Guid tenant, Guid branch, Guid session, Guid member, string status) => $"""
        INSERT INTO classes.class_reservations (id, tenant_id, branch_id, session_id, member_id, status, source, late_cancellation, created_at)
        VALUES (gen_random_uuid(), '{tenant}', '{branch}', '{session}', '{member}', '{status}', 'Staff', false, now())
        """;

    [Fact]
    public async Task La_base_impone_el_tope_de_la_fila_las_transiciones_y_la_coherencia_de_la_invitacion()
    {
        var s = await NewSetupAsync();
        var small = await NewClassAsync(s.Tenant.Owner, s.Branch, s.Instructor, "Fila de uno", waitlistCapacity: 1);
        var none = await NewClassAsync(s.Tenant.Owner, s.Branch, s.Instructor, "Sin fila", waitlist: false);
        var smallSession = await NewSessionAsync(s.Tenant.Owner, small.Id, Local(DayOfWeek.Monday, 9), capacity: 5);
        var noneSession = await NewSessionAsync(s.Tenant.Owner, none.Id, Local(DayOfWeek.Tuesday, 9), capacity: 5);
        var (a, b, c) = (await NewMemberAsync(s), await NewMemberAsync(s), await NewMemberAsync(s));
        var confirmed = await BookOkAsync(s.Tenant.Owner, smallSession.Id, c.Id);
        await using var admin = new NpgsqlConnection(api.AdminConnection);
        await admin.OpenAsync();

        async Task<string> ErrorAsync(string sql)
        {
            await using var command = new NpgsqlCommand(sql, admin);
            return (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).SqlState;
        }

        // Tope de la fila (1) y clase sin lista de espera.
        await SqlAsync(Insert(s.Tenant.Id, s.Branch, smallSession.Id, a.Id, "Waitlisted"));
        Assert.Equal("FC005", await ErrorAsync(Insert(s.Tenant.Id, s.Branch, smallSession.Id, b.Id, "Waitlisted")));
        Assert.Equal("FC004", await ErrorAsync(Insert(s.Tenant.Id, s.Branch, noneSession.Id, b.Id, "Waitlisted")));

        // Transiciones: de la fila se pasa a ofrecida (nunca directo a confirmada); una confirmada solo se cancela; una ofrecida no vuelve a la fila.
        var waitingId = await ScalarAsync(admin, $"SELECT id FROM classes.class_reservations WHERE session_id = '{smallSession.Id}' AND status = 'Waitlisted'");
        Assert.Equal(PostgresErrorCodes.CheckViolation, await ErrorAsync($"UPDATE classes.class_reservations SET status = 'Confirmed' WHERE id = '{waitingId}'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await ErrorAsync($"UPDATE classes.class_reservations SET status = 'Expired' WHERE id = '{confirmed.Id}'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await ErrorAsync($"UPDATE classes.class_reservations SET status = 'Waitlisted' WHERE id = '{confirmed.Id}'"));

        // Una invitación necesita su plazo, posterior a la oferta; sin él (o al revés) la base la rechaza.
        Assert.Equal(PostgresErrorCodes.CheckViolation, await ErrorAsync($"UPDATE classes.class_reservations SET status = 'Offered' WHERE id = '{waitingId}'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await ErrorAsync($"UPDATE classes.class_reservations SET status = 'Offered', offered_at = now(), offer_expires_at = now() - interval '1 minute' WHERE id = '{waitingId}'"));
        await SqlAsync($"UPDATE classes.class_reservations SET status = 'Offered', offered_at = now(), offer_expires_at = now() + interval '30 minutes' WHERE id = '{waitingId}'");
        Assert.Equal(PostgresErrorCodes.CheckViolation, await ErrorAsync($"UPDATE classes.class_reservations SET status = 'Waitlisted' WHERE id = '{waitingId}'"));
    }

    private static async Task<Guid> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (Guid)(await command.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task Entrar_a_la_fila_aceptar_y_rechazar_dejan_rastro_en_la_auditoria()
    {
        var s = await NewSetupAsync();
        var (_, holder, waiting) = await FullSessionWithWaitlistAsync(s, 1);
        await PostAsync(s.Tenant.Owner, "cancel", holder.Id);
        await PostAsync(s.Tenant.Owner, "accept", waiting[0].Reservation.Id);

        var audit = (await s.Tenant.Owner.GetFromJsonAsync<PagedDto<AuditDto>>($"/api/v1/audit-logs?entity=ClassReservation&entityId={waiting[0].Reservation.Id}"))!;
        Assert.True(audit.TotalCount >= 3, $"Auditoría con {audit.TotalCount} registros (entrada, oferta y aceptación)");
    }
}
