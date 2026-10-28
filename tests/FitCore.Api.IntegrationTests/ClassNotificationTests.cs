using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FitCore.Api.IntegrationTests.Infrastructure;
using FitCore.Classes.Application.Notifications;
using FitCore.Classes.Application.Reservations;
using FitCore.Platform.Persistence;
using FitCore.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using static FitCore.Api.IntegrationTests.Infrastructure.CommercialTestApi;

namespace FitCore.Api.IntegrationTests;

[Collection(ApiCollection4.Name)]
public class ClassNotificationTests(ApiFixture api)
{
    private static readonly TimeSpan Ecuador = TimeSpan.FromHours(-5);
    private const string EmptyJson = "{}";
    private const string StudentPassword = "Nueva-Clave-2026x";

    private sealed record TrainerRow(Guid Id, Guid UserId, string FullName);

    private sealed record ClassRow(Guid Id);

    private sealed record SessionRow(Guid Id, int BookedCount);

    private sealed record ReservationRow(Guid Id, Guid SessionId, Guid MemberId, string Status);

    private sealed record NotificationRow(Guid Id, string Type, string Title, string Body, JsonElement Data, bool Critical, DateTimeOffset CreatedAt, bool IsRead, DateTimeOffset? ReadAt);

    private sealed record PreferenceRow(string Type, string Label, string Description, bool Enabled, bool Required);

    private sealed record AccessRow(Guid MemberId, string Email, string TemporaryPassword);

    private sealed record Student(MemberDto Member, HttpClient Client);

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

    private async Task<Setup> NewSetupAsync()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var branch = await TestApi.CreateBranchAsync(tenant.Owner, "UIO");
        var instructor = await NewTrainerAsync(tenant, "instructora@gym.test", branch);
        var response = await tenant.Owner.PostAsJsonAsync("/api/v1/group-classes", new
        {
            branchId = branch,
            name = "Yoga matutino",
            activity = "Yoga",
            instructorId = instructor.Id,
            durationMinutes = 60,
            capacity = 20,
            level = "AllLevels",
            minBookingNoticeMinutes = 30,
            maxBookingAdvanceDays = 30,
            cancellationDeadlineMinutes = 120,
            waitlistEnabled = true,
            waitlistCapacity = 5,
            offerWindowMinutes = 30,
            requireActiveMembership = true,
            applyPaymentPolicy = true
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var groupClass = (await response.Content.ReadFromJsonAsync<ClassRow>())!;
        return new Setup(tenant, branch, instructor, groupClass, await CreatePlanAsync(tenant.Owner, "Mensual", 30m));
    }

    private static async Task<SessionRow> NewSessionAsync(HttpClient owner, Guid classId, DateTimeOffset startsAt, int? capacity = null, Guid? instructorId = null)
    {
        var response = await owner.PostAsJsonAsync("/api/v1/class-sessions", new { classId, startsAt, capacity, instructorId });
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"Respondió {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<SessionRow>())!;
    }

    private static int _members;

    private async Task<MemberDto> NewMemberAsync(Setup s)
    {
        var n = Interlocked.Increment(ref _members);
        var response = await s.Tenant.Owner.PostAsJsonAsync("/api/v1/members", new
        {
            branchId = s.Branch,
            firstName = $"Aviso{n}",
            lastName = "Prueba",
            idType = "Cedula",
            idNumber = MemberTests.NewCedula(),
            birthDate = "1995-04-12",
            email = $"aviso{n}.{Guid.NewGuid():N}"[..28] + "@alumnos.test",
            phone = "0991234567"
        });
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"Respondió {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        var member = (await response.Content.ReadFromJsonAsync<MemberDto>())!;
        Assert.Equal(HttpStatusCode.Created, (await s.Tenant.Owner.PostAsJsonAsync("/api/v1/memberships", new { memberId = member.Id, planId = s.Plan.Id })).StatusCode);
        await PayAsync(s.Tenant.Owner, member.Id, 30m);
        return member;
    }

    /// <summary>Alumno con acceso al portal (sin él no tiene bandeja).</summary>
    private async Task<Student> NewStudentAsync(Setup s)
    {
        var member = await NewMemberAsync(s);
        var grant = await s.Tenant.Owner.PostAsync($"/api/v1/members/{member.Id}/access", null);
        Assert.Equal(HttpStatusCode.Created, grant.StatusCode);
        var access = (await grant.Content.ReadFromJsonAsync<AccessRow>())!;
        var temp = TestApi.Authenticated(api, await TestApi.LoginAsync(api, s.Tenant.Slug, access.Email, access.TemporaryPassword));
        Assert.Equal(HttpStatusCode.NoContent, (await temp.PostAsJsonAsync("/api/v1/auth/change-password", new { currentPassword = access.TemporaryPassword, newPassword = StudentPassword })).StatusCode);
        return new Student(member, TestApi.Authenticated(api, await TestApi.LoginAsync(api, s.Tenant.Slug, access.Email, StudentPassword)));
    }

    private HttpClient Login(Setup s, string email) => TestApi.Authenticated(api, TestApi.LoginAsync(api, s.Tenant.Slug, email, TestApi.UserPassword).GetAwaiter().GetResult());

    private static async Task<ReservationRow> BookAsync(HttpClient staff, Guid sessionId, Guid memberId, bool join = false)
    {
        var response = await PostWithKeyAsync(staff, "/api/v1/class-reservations", new { sessionId, memberId, joinWaitlist = join });
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"Respondió {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<ReservationRow>())!;
    }

    private static Task<HttpResponseMessage> CancelAsync(HttpClient client, Guid id, string? reason = null, bool mine = false) =>
        client.PostAsJsonAsync($"/api/v1/{(mine ? "me/" : string.Empty)}class-reservations/{id}/cancel", new { reason });

    /// <summary>Lo que hace el Worker cada pocos segundos: publicar los eventos pendientes de la outbox a sus manejadores.</summary>
    private async Task DrainOutboxAsync()
    {
        var processor = new OutboxProcessor(api.Services.GetRequiredService<IServiceScopeFactory>(), api.Services.GetRequiredService<IClock>(), NullLogger<OutboxProcessor>.Instance);
        for (var i = 0; i < 300 && await processor.ProcessBatchAsync(CancellationToken.None) > 0; i++)
        {
        }
    }

    private static async Task<NotificationRow[]> InboxAsync(HttpClient client, string query = "") =>
        (await client.GetFromJsonAsync<PagedDto<NotificationRow>>($"/api/v1/me/notifications?pageSize=100{query}"))!.Items;

    private async Task SqlAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(api.AdminConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<Guid> ScalarGuidAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(api.AdminConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (Guid)(await command.ExecuteScalarAsync())!;
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

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.True(response.StatusCode == status, $"Respondió {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        Assert.Equal(code, await TestApi.ProblemCodeAsync(response));
    }

    // ---- Avisos nacidos de eventos (outbox) --------------------------------------------------------------------------

    [Fact]
    public async Task Una_reserva_confirmada_avisa_al_alumno_desde_la_outbox_y_no_se_duplica_si_el_evento_se_repite()
    {
        var s = await NewSetupAsync();
        var student = await NewStudentAsync(s);
        var withoutAccount = await NewMemberAsync(s);
        var session = await NewSessionAsync(s.Tenant.Owner, s.Class.Id, Local(DayOfWeek.Monday, 9));

        var reservation = await BookAsync(s.Tenant.Owner, session.Id, student.Member.Id);
        await BookAsync(s.Tenant.Owner, session.Id, withoutAccount.Id); // sin usuario no hay bandeja: no es un error

        // El evento quedó registrado en la misma transacción que la reserva y todavía no hay aviso (lo entrega la outbox).
        Assert.Empty(await InboxAsync(student.Client));
        Assert.True(await CountAsync(s.Tenant.Id, "SELECT count(*) FROM messaging.outbox_messages WHERE type LIKE '%ReservationConfirmed' AND processed_at IS NULL") >= 1);

        await DrainOutboxAsync();
        var inbox = await InboxAsync(student.Client);
        var notice = Assert.Single(inbox);
        Assert.Equal("ReservationConfirmed", notice.Type);
        Assert.Equal("Reserva confirmada", notice.Title);
        Assert.Contains("Yoga matutino", notice.Body);
        Assert.Contains("Sucursal UIO", notice.Body);
        Assert.Contains("lunes", notice.Body); // fecha en español y en la zona de la sucursal
        Assert.Equal(session.Id, notice.Data.GetProperty("sessionId").GetGuid());
        Assert.Equal(reservation.Id, notice.Data.GetProperty("reservationId").GetGuid());
        Assert.False(notice.IsRead);
        Assert.Equal(1, (await student.Client.GetFromJsonAsync<NotificationSummary>("/api/v1/me/notifications/summary"))!.Unread);

        // Entrega duplicada del mismo evento (la outbox es «al menos una vez»): sigue habiendo un solo aviso.
        await SqlAsync($"UPDATE messaging.outbox_messages SET processed_at = NULL, attempts = 0, error = NULL WHERE tenant_id = '{s.Tenant.Id}' AND type LIKE '%ReservationConfirmed'");
        await DrainOutboxAsync();
        Assert.Single(await InboxAsync(student.Client));
        Assert.Equal(1L, await CountAsync(s.Tenant.Id, "SELECT count(*) FROM classes.inbox_notifications"));
    }

    private sealed record NotificationSummary(int Unread);

    private sealed record MarkAllResult(int Marked);

    [Fact]
    public async Task Entrar_a_la_lista_de_espera_y_recibir_el_cupo_avisa_y_no_avisa_de_lo_que_ya_no_vale()
    {
        var s = await NewSetupAsync();
        var holder = await NewMemberAsync(s);
        var waiter = await NewStudentAsync(s);
        var session = await NewSessionAsync(s.Tenant.Owner, s.Class.Id, Local(DayOfWeek.Monday, 9), capacity: 1);
        var held = await BookAsync(s.Tenant.Owner, session.Id, holder.Id);
        await BookAsync(s.Tenant.Owner, session.Id, waiter.Member.Id, join: true);

        await DrainOutboxAsync();
        var joined = Assert.Single(await InboxAsync(waiter.Client));
        Assert.Equal("WaitlistJoined", joined.Type);

        // Se libera el cupo: el aviso es la invitación, con su hora límite, y es crítico (no se puede apagar).
        await CancelAsync(s.Tenant.Owner, held.Id);
        await DrainOutboxAsync();
        var offer = (await InboxAsync(waiter.Client)).Single(n => n.Type == "WaitlistOffer");
        Assert.True(offer.Critical);
        Assert.Contains("Acéptalo antes de las", offer.Body);

        // Si la invitación se atiende antes de que la outbox corra, no se avisa de lo que ya cambió: aquí solo llega la invitación.
        var other = await NewStudentAsync(s);
        var second = await NewSessionAsync(s.Tenant.Owner, s.Class.Id, Local(DayOfWeek.Tuesday, 9), capacity: 1);
        var held2 = await BookAsync(s.Tenant.Owner, second.Id, holder.Id);
        await BookAsync(s.Tenant.Owner, second.Id, other.Member.Id, join: true);
        await CancelAsync(s.Tenant.Owner, held2.Id);
        await DrainOutboxAsync();
        var types = (await InboxAsync(other.Client)).Select(n => n.Type).ToList();
        Assert.Equal(["WaitlistOffer"], types);
    }

    [Fact]
    public async Task Una_invitacion_vencida_avisa_a_quien_la_perdio_y_ofrece_al_siguiente()
    {
        var s = await NewSetupAsync();
        var holder = await NewMemberAsync(s);
        var (first, next) = (await NewStudentAsync(s), await NewStudentAsync(s));
        var session = await NewSessionAsync(s.Tenant.Owner, s.Class.Id, Local(DayOfWeek.Monday, 9), capacity: 1);
        var held = await BookAsync(s.Tenant.Owner, session.Id, holder.Id);
        var firstReservation = await BookAsync(s.Tenant.Owner, session.Id, first.Member.Id, join: true);
        await BookAsync(s.Tenant.Owner, session.Id, next.Member.Id, join: true);
        await CancelAsync(s.Tenant.Owner, held.Id);
        await SqlAsync($"UPDATE classes.class_reservations SET offered_at = now() - interval '1 hour', offer_expires_at = now() - interval '1 minute' WHERE id = '{firstReservation.Id}'");

        await api.Services.GetRequiredService<WaitlistRunner>().RunAsync(CancellationToken.None);
        await DrainOutboxAsync();

        var lost = await InboxAsync(first.Client);
        Assert.Contains(lost, n => n.Type == "WaitlistOfferExpired" && n.Title == "La invitación venció");
        // Su invitación, ya vencida, no se le avisa tarde: llegó antes de vencer solo si la outbox corrió a tiempo (aquí no).
        Assert.DoesNotContain(lost, n => n.Type == "WaitlistOffer");
        Assert.Contains(await InboxAsync(next.Client), n => n.Type == "WaitlistOffer");
    }

    [Fact]
    public async Task Cancelar_una_sesion_avisa_a_los_inscritos_con_el_motivo_y_al_instructor()
    {
        var s = await NewSetupAsync();
        var (confirmed, waiting) = (await NewStudentAsync(s), await NewStudentAsync(s));
        var session = await NewSessionAsync(s.Tenant.Owner, s.Class.Id, Local(DayOfWeek.Monday, 9), capacity: 1);
        await BookAsync(s.Tenant.Owner, session.Id, confirmed.Member.Id);
        await BookAsync(s.Tenant.Owner, session.Id, waiting.Member.Id, join: true);
        await DrainOutboxAsync();

        await s.Tenant.Owner.PostAsJsonAsync($"/api/v1/class-sessions/{session.Id}/cancel", new { reason = "Feriado nacional" });
        await DrainOutboxAsync();

        foreach (var student in new[] { confirmed, waiting })
        {
            var notice = Assert.Single(await InboxAsync(student.Client), n => n.Type == "SessionCancelled");
            Assert.Equal("Clase cancelada", notice.Title);
            Assert.Contains("Feriado nacional", notice.Body);
            Assert.True(notice.Critical);
        }

        var instructor = await InboxAsync(Login(s, "instructora@gym.test"));
        var told = Assert.Single(instructor, n => n.Type == "SessionCancelled");
        Assert.Equal("Sesión cancelada", told.Title);
        Assert.Contains("Feriado nacional", told.Body);
    }

    [Fact]
    public async Task Cancelar_la_propia_reserva_no_avisa_pero_que_la_cancele_el_gimnasio_si()
    {
        var s = await NewSetupAsync();
        var (mine, theirs) = (await NewStudentAsync(s), await NewStudentAsync(s));
        var session = await NewSessionAsync(s.Tenant.Owner, s.Class.Id, Local(DayOfWeek.Monday, 9));
        var own = await BookAsync(s.Tenant.Owner, session.Id, mine.Member.Id);
        var byGym = await BookAsync(s.Tenant.Owner, session.Id, theirs.Member.Id);
        await DrainOutboxAsync();

        await CancelAsync(mine.Client, own.Id, mine: true);
        await CancelAsync(s.Tenant.Owner, byGym.Id);
        await DrainOutboxAsync();

        Assert.DoesNotContain(await InboxAsync(mine.Client), n => n.Type == "ReservationCancelledByGym");
        var notice = Assert.Single(await InboxAsync(theirs.Client), n => n.Type == "ReservationCancelledByGym");
        Assert.Contains("El gimnasio canceló tu reserva", notice.Body);
    }

    [Fact]
    public async Task Cambiar_el_horario_o_el_instructor_avisa_a_los_inscritos_y_a_los_instructores_pero_no_cambiar_la_capacidad()
    {
        var s = await NewSetupAsync();
        var second = await NewTrainerAsync(s.Tenant, "segundo@gym.test", s.Branch);
        var (confirmed, waiting) = (await NewStudentAsync(s), await NewStudentAsync(s));
        var session = await NewSessionAsync(s.Tenant.Owner, s.Class.Id, Local(DayOfWeek.Monday, 9), capacity: 1);
        await BookAsync(s.Tenant.Owner, session.Id, confirmed.Member.Id);
        await BookAsync(s.Tenant.Owner, session.Id, waiting.Member.Id, join: true);
        await DrainOutboxAsync();
        var before = (await InboxAsync(confirmed.Client)).Length;

        // La capacidad no es un cambio que le importe a los inscritos.
        await s.Tenant.Owner.PatchAsJsonAsync($"/api/v1/class-sessions/{session.Id}", new { notes = "Traer toalla" });
        await DrainOutboxAsync();
        Assert.Equal(before, (await InboxAsync(confirmed.Client)).Length);

        // Mover la hora sí: avisa a quien tiene cupo y a quien espera, y al instructor.
        var patched = await s.Tenant.Owner.PatchAsJsonAsync($"/api/v1/class-sessions/{session.Id}", new { startsAt = Local(DayOfWeek.Monday, 11) });
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
        await DrainOutboxAsync();
        foreach (var student in new[] { confirmed, waiting })
        {
            var notice = Assert.Single(await InboxAsync(student.Client), n => n.Type == "SessionRescheduled");
            Assert.Equal("Cambio de horario", notice.Title);
            Assert.Contains("antes", notice.Body);
            Assert.Contains("11:00", notice.Body);
        }

        // Cambiar de instructor: el nuevo recibe la sesión y el anterior sabe que ya no la imparte.
        await s.Tenant.Owner.PatchAsJsonAsync($"/api/v1/class-sessions/{session.Id}", new { instructorId = second.Id });
        await DrainOutboxAsync();
        Assert.Contains(await InboxAsync(Login(s, "segundo@gym.test")), n => n.Body.StartsWith("Se te asignó", StringComparison.Ordinal));
        Assert.Contains(await InboxAsync(Login(s, "instructora@gym.test")), n => n.Body.StartsWith("Ya no impartes", StringComparison.Ordinal));
    }

    // ---- Recordatorios ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task El_recordatorio_se_genera_solo_para_clases_proximas_una_vez_por_reserva_y_respeta_la_preferencia()
    {
        var s = await NewSetupAsync();
        var (wants, declines, notYet) = (await NewStudentAsync(s), await NewStudentAsync(s), await NewStudentAsync(s));
        var soon = await NewSessionAsync(s.Tenant.Owner, s.Class.Id, DateTimeOffset.UtcNow.AddMinutes(100));
        var later = await NewSessionAsync(s.Tenant.Owner, s.Class.Id, DateTimeOffset.UtcNow.AddHours(6));
        await BookAsync(s.Tenant.Owner, soon.Id, wants.Member.Id);
        await BookAsync(s.Tenant.Owner, soon.Id, declines.Member.Id);
        await BookAsync(s.Tenant.Owner, later.Id, notYet.Member.Id);
        var off = await declines.Client.PutAsJsonAsync("/api/v1/me/notification-preferences", new { preferences = new[] { new { type = "ClassReminder", enabled = false } } });
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        var runner = api.Services.GetRequiredService<NotificationRunner>();

        var first = await runner.CreateRemindersAsync(s.Tenant.Id, CancellationToken.None);
        var again = await runner.CreateRemindersAsync(s.Tenant.Id, CancellationToken.None);

        Assert.Equal(1, first.RemindersCreated); // solo quien lo quiere y cuya clase es próxima
        Assert.Equal(0, again.RemindersCreated); // repetir el trabajo no duplica
        var reminder = Assert.Single(await InboxAsync(wants.Client), n => n.Type == "ClassReminder");
        Assert.Equal("Tu clase empieza pronto", reminder.Title);
        Assert.DoesNotContain(await InboxAsync(declines.Client), n => n.Type == "ClassReminder");
        Assert.DoesNotContain(await InboxAsync(notYet.Client), n => n.Type == "ClassReminder");
        Assert.True((await runner.RunAsync(CancellationToken.None)).Tenants >= 1); // el trabajo completo corre sin errores
    }

    // ---- Bandeja y preferencias -------------------------------------------------------------------------------------

    [Fact]
    public async Task La_bandeja_pagina_filtra_y_marca_como_leido_de_forma_idempotente()
    {
        var s = await NewSetupAsync();
        var student = await NewStudentAsync(s);
        for (var i = 0; i < 3; i++)
        {
            var session = await NewSessionAsync(s.Tenant.Owner, s.Class.Id, Local(DayOfWeek.Monday, 7 + (2 * i)));
            await BookAsync(s.Tenant.Owner, session.Id, student.Member.Id);
        }

        await DrainOutboxAsync();
        var all = await InboxAsync(student.Client);
        Assert.Equal(3, all.Length);
        Assert.Equal(all.OrderByDescending(n => n.CreatedAt).Select(n => n.Id), all.Select(n => n.Id));
        var page = (await student.Client.GetFromJsonAsync<PagedDto<NotificationRow>>("/api/v1/me/notifications?pageSize=2"))!;
        Assert.Equal(2, page.Items.Length);
        Assert.Equal(3, page.TotalCount);
        Assert.Empty(await InboxAsync(student.Client, "&type=ClassReminder"));

        var read = await student.Client.PatchAsync($"/api/v1/me/notifications/{all[0].Id}/read", null);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var marked = (await read.Content.ReadFromJsonAsync<NotificationRow>())!;
        Assert.True(marked.IsRead);
        var again = (await (await student.Client.PatchAsync($"/api/v1/me/notifications/{all[0].Id}/read", null)).Content.ReadFromJsonAsync<NotificationRow>())!;
        Assert.Equal(marked.ReadAt, again.ReadAt); // no pisa la hora de la primera lectura
        Assert.Equal(2, (await InboxAsync(student.Client, "&unreadOnly=true")).Length);

        var allRead = (await (await student.Client.PostAsync("/api/v1/me/notifications/read-all", null)).Content.ReadFromJsonAsync<MarkAllResult>())!;
        Assert.Equal(2, allRead.Marked);
        Assert.Equal(0, (await student.Client.GetFromJsonAsync<NotificationSummary>("/api/v1/me/notifications/summary"))!.Unread);
    }

    [Fact]
    public async Task Las_preferencias_se_guardan_por_usuario_y_los_avisos_criticos_no_se_pueden_apagar()
    {
        var s = await NewSetupAsync();
        var (student, other) = (await NewStudentAsync(s), await NewStudentAsync(s));

        var defaults = (await student.Client.GetFromJsonAsync<PreferenceRow[]>("/api/v1/me/notification-preferences"))!;
        Assert.Equal(8, defaults.Length);
        Assert.All(defaults, p => Assert.True(p.Enabled));
        Assert.Equal(
            ["ReservationCancelledByGym", "SessionCancelled", "SessionRescheduled", "WaitlistOffer"],
            defaults.Where(p => p.Required).Select(p => p.Type).Order());

        var updated = (await (await student.Client.PutAsJsonAsync("/api/v1/me/notification-preferences",
            new { preferences = new[] { new { type = "ReservationConfirmed", enabled = false }, new { type = "ClassReminder", enabled = false } } })).Content.ReadFromJsonAsync<PreferenceRow[]>())!;
        Assert.False(updated.Single(p => p.Type == "ReservationConfirmed").Enabled);
        Assert.False(updated.Single(p => p.Type == "ClassReminder").Enabled);
        Assert.True(updated.Single(p => p.Type == "WaitlistJoined").Enabled);
        Assert.True((await other.Client.GetFromJsonAsync<PreferenceRow[]>("/api/v1/me/notification-preferences"))!.Single(p => p.Type == "ReservationConfirmed").Enabled);

        // Críticos y datos inválidos se rechazan, y nada queda a medias.
        await AssertProblemAsync(await student.Client.PutAsJsonAsync("/api/v1/me/notification-preferences",
            new { preferences = new[] { new { type = "ClassReminder", enabled = true }, new { type = "WaitlistOffer", enabled = false } } }), HttpStatusCode.UnprocessableEntity, "classes.notification_required");
        await AssertProblemAsync(await student.Client.PutAsJsonAsync("/api/v1/me/notification-preferences",
            new { preferences = new[] { new { type = "ClassReminder", enabled = true }, new { type = "ClassReminder", enabled = false } } }), HttpStatusCode.UnprocessableEntity, "classes.notification_duplicate_types");
        Assert.Equal(HttpStatusCode.BadRequest, (await student.Client.PutAsJsonAsync("/api/v1/me/notification-preferences", new { preferences = new[] { new { type = "Inexistente", enabled = false } } })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await student.Client.PutAsJsonAsync("/api/v1/me/notification-preferences", new { preferences = Array.Empty<object>() })).StatusCode);
        Assert.False((await student.Client.GetFromJsonAsync<PreferenceRow[]>("/api/v1/me/notification-preferences"))!.Single(p => p.Type == "ClassReminder").Enabled);

        // Apagada «reserva confirmada», no se avisa; los críticos siguen llegando.
        var session = await NewSessionAsync(s.Tenant.Owner, s.Class.Id, Local(DayOfWeek.Monday, 9));
        await BookAsync(s.Tenant.Owner, session.Id, student.Member.Id);
        await BookAsync(s.Tenant.Owner, session.Id, other.Member.Id);
        await DrainOutboxAsync(); // los avisos de las reservas salen antes de que la sesión se cancele
        await s.Tenant.Owner.PostAsJsonAsync($"/api/v1/class-sessions/{session.Id}/cancel", new { reason = "Mantenimiento" });
        await DrainOutboxAsync();
        var mine = await InboxAsync(student.Client);
        Assert.DoesNotContain(mine, n => n.Type == "ReservationConfirmed");
        Assert.Contains(mine, n => n.Type == "SessionCancelled");
        Assert.Contains(await InboxAsync(other.Client), n => n.Type == "ReservationConfirmed");
    }

    [Fact]
    public async Task Cada_usuario_ve_solo_su_bandeja_y_un_gimnasio_no_ve_la_de_otro()
    {
        var a = await NewSetupAsync();
        var b = await NewSetupAsync();
        var (mine, mate) = (await NewStudentAsync(a), await NewStudentAsync(a));
        var foreign = await NewStudentAsync(b);
        var session = await NewSessionAsync(a.Tenant.Owner, a.Class.Id, Local(DayOfWeek.Monday, 9));
        await BookAsync(a.Tenant.Owner, session.Id, mine.Member.Id);
        var foreignSession = await NewSessionAsync(b.Tenant.Owner, b.Class.Id, Local(DayOfWeek.Monday, 9));
        await BookAsync(b.Tenant.Owner, foreignSession.Id, foreign.Member.Id);
        await DrainOutboxAsync();
        var notice = Assert.Single(await InboxAsync(mine.Client));

        // Otro alumno, otro gimnasio y el propio propietario del gimnasio no leen ni marcan el aviso ajeno.
        Assert.Empty(await InboxAsync(mate.Client));
        Assert.Empty(await InboxAsync(a.Tenant.Owner));
        Assert.Equal(HttpStatusCode.NotFound, (await mate.Client.PatchAsync($"/api/v1/me/notifications/{notice.Id}/read", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Tenant.Owner.PatchAsync($"/api/v1/me/notifications/{notice.Id}/read", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.Client.PatchAsync($"/api/v1/me/notifications/{notice.Id}/read", null)).StatusCode);
        Assert.False((await InboxAsync(mine.Client)).Single().IsRead);
        var mateAll = await mate.Client.PostAsync("/api/v1/me/notifications/read-all", null);
        Assert.Equal(0, (await mateAll.Content.ReadFromJsonAsync<MarkAllResult>())!.Marked); // marcar todo solo toca lo propio
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().GetAsync("/api/v1/me/notifications")).StatusCode);

        // Con el rol de la aplicación y el gimnasio B fijado, los avisos de A no existen; con A fijado, sí (no pasa en vacío).
        Assert.Equal(1L, await CountAsync(a.Tenant.Id, "SELECT count(*) FROM classes.inbox_notifications"));
        Assert.Equal(1L, await CountAsync(b.Tenant.Id, "SELECT count(*) FROM classes.inbox_notifications"));
        Assert.Equal(0L, await CountAsync(b.Tenant.Id, $"SELECT count(*) FROM classes.inbox_notifications WHERE id = '{notice.Id}'"));
    }

    // ---- Entrega por canales con reintentos -------------------------------------------------------------------------

    private sealed class ScriptedChannel(string name, bool configured, Func<NotificationMessage, ChannelResult> script) : INotificationChannel
    {
        public int Calls { get; private set; }

        public string Name => name;

        public bool IsConfigured => configured;

        public Task<ChannelResult> SendAsync(NotificationMessage message, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(script(message));
        }
    }

    private async Task<Guid> PendingAsync(Setup s, Guid userId, string channel = "Email", int minutesDue = -1)
    {
        var id = Guid.NewGuid();
        await SqlAsync($"""
            INSERT INTO classes.inbox_notifications (id, tenant_id, recipient_user_id, type, title, body, data, dedupe_key, channel, status, attempts, created_at, next_attempt_at)
            VALUES ('{id}', '{s.Tenant.Id}', '{userId}', 'ClassReminder', 'Aviso de prueba', 'Cuerpo de prueba', '{EmptyJson}', 'prueba:{id}', '{channel}', 'Pending', 0, now(), now() + interval '{minutesDue} minutes')
            """);
        return id;
    }

    private async Task<(string Status, int Attempts, string? Error, bool Due, bool Delivered)> StateAsync(Guid id)
    {
        await using var connection = new NpgsqlConnection(api.AdminConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"SELECT status, attempts, last_error, next_attempt_at <= now(), delivered_at IS NOT NULL FROM classes.inbox_notifications WHERE id = '{id}'", connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetString(0), reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetString(2), !reader.IsDBNull(3) && reader.GetBoolean(3), reader.GetBoolean(4));
    }

    [Fact]
    public async Task Un_fallo_transitorio_se_reintenta_con_espera_y_al_lograrse_queda_entregado_sin_guardar_datos_del_proveedor()
    {
        var s = await NewSetupAsync();
        var user = await TestApi.CreateUserAsync(s.Tenant.Owner, "destino@gym.test", "Recepcion", s.Branch);
        var id = await PendingAsync(s, user.Id);
        var runner = api.Services.GetRequiredService<NotificationRunner>();

        // El proveedor se cae con un mensaje que trae datos sensibles: solo se guarda un código, no el texto.
        var broken = new ScriptedChannel("Email", true, _ => throw new InvalidOperationException("smtp usuario=pedro@correo.test clave=abc123"));
        var first = await runner.DeliverPendingAsync(s.Tenant.Id, [broken], CancellationToken.None);
        var afterFirst = await StateAsync(id);
        Assert.Equal(1, first.Retrying);
        Assert.Equal(("Pending", 1, "channel_error"), (afterFirst.Status, afterFirst.Attempts, afterFirst.Error));
        Assert.False(afterFirst.Due); // espera antes del siguiente intento
        Assert.Equal(1, broken.Calls);

        // Todavía no le toca: no se vuelve a intentar.
        await runner.DeliverPendingAsync(s.Tenant.Id, [broken], CancellationToken.None);
        Assert.Equal(1, broken.Calls);

        // Llega su hora y el proveedor ya responde: se entrega y se limpia el error.
        await SqlAsync($"UPDATE classes.inbox_notifications SET next_attempt_at = now() - interval '1 minute' WHERE id = '{id}'");
        var working = new ScriptedChannel("Email", true, _ => ChannelResult.Success);
        var second = await runner.DeliverPendingAsync(s.Tenant.Id, [working], CancellationToken.None);
        var delivered = await StateAsync(id);
        Assert.Equal(1, second.Delivered);
        Assert.Equal(("Delivered", 1), (delivered.Status, delivered.Attempts));
        Assert.Null(delivered.Error);
        Assert.True(delivered.Delivered);
    }

    [Fact]
    public async Task Los_fallos_definitivos_y_los_canales_sin_configurar_no_se_reintentan_y_los_intentos_se_agotan()
    {
        var s = await NewSetupAsync();
        var user = await TestApi.CreateUserAsync(s.Tenant.Owner, "destino@gym.test", "Recepcion", s.Branch);
        var runner = api.Services.GetRequiredService<NotificationRunner>();

        var permanent = await PendingAsync(s, user.Id);
        var summary = await runner.DeliverPendingAsync(s.Tenant.Id, [new ScriptedChannel("Email", true, _ => ChannelResult.PermanentFailure("invalid_recipient"))], CancellationToken.None);
        Assert.Equal(1, summary.Failed);
        var state = await StateAsync(permanent);
        Assert.Equal(("Failed", 1, "invalid_recipient"), (state.Status, state.Attempts, state.Error));

        // Un canal que no está configurado (o no existe) no simula nada: el aviso queda fallido, sin enviarse.
        var unconfigured = await PendingAsync(s, user.Id, "Sms");
        var silent = new ScriptedChannel("Sms", false, _ => ChannelResult.Success);
        await runner.DeliverPendingAsync(s.Tenant.Id, [silent], CancellationToken.None);
        Assert.Equal(0, silent.Calls);
        Assert.Equal(("Failed", "channel_unavailable"), ((await StateAsync(unconfigured)).Status, (await StateAsync(unconfigured)).Error));

        // Un fallo transitorio que no se resuelve se agota a los cinco intentos.
        var stubborn = await PendingAsync(s, user.Id, "WhatsApp");
        var failing = new ScriptedChannel("WhatsApp", true, _ => ChannelResult.TransientFailure("rate_limited"));
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            await SqlAsync($"UPDATE classes.inbox_notifications SET next_attempt_at = now() - interval '1 minute' WHERE id = '{stubborn}' AND status = 'Pending'");
            await runner.DeliverPendingAsync(s.Tenant.Id, [failing], CancellationToken.None);
        }

        var exhausted = await StateAsync(stubborn);
        Assert.Equal(5, failing.Calls);
        Assert.Equal(("Failed", 5, "rate_limited"), (exhausted.Status, exhausted.Attempts, exhausted.Error));
        await runner.DeliverPendingAsync(s.Tenant.Id, [failing], CancellationToken.None);
        Assert.Equal(5, failing.Calls); // ya no se toca
    }

    [Fact]
    public async Task La_base_impone_la_clave_anti_duplicado_los_estados_y_que_el_contenido_no_cambie()
    {
        var s = await NewSetupAsync();
        var student = await NewStudentAsync(s);
        var session = await NewSessionAsync(s.Tenant.Owner, s.Class.Id, Local(DayOfWeek.Monday, 9));
        await BookAsync(s.Tenant.Owner, session.Id, student.Member.Id);
        await DrainOutboxAsync();
        var notice = (await InboxAsync(student.Client)).Single();
        var userId = await ScalarGuidAsync($"SELECT recipient_user_id FROM classes.inbox_notifications WHERE id = '{notice.Id}'");
        var foreignTenant = await TestApi.NewTenantAsync(api);
        await using var admin = new NpgsqlConnection(api.AdminConnection);
        await admin.OpenAsync();

        async Task<string> ErrorAsync(string sql)
        {
            await using var command = new NpgsqlCommand(sql, admin);
            return (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).SqlState;
        }

        string Insert(Guid tenant, Guid user, string dedupe, string type = "ClassReminder", string status = "Delivered", string extra = "") => $"""
            INSERT INTO classes.inbox_notifications (id, tenant_id, recipient_user_id, type, title, body, data, dedupe_key, channel, status, attempts, created_at, delivered_at, next_attempt_at)
            VALUES (gen_random_uuid(), '{tenant}', '{user}', '{type}', 't', 'b', '{EmptyJson}', '{dedupe}', 'InApp', '{status}', 0, now(),
                    {(status == "Delivered" ? "now()" : "NULL")}, {(status == "Pending" ? "now()" : "NULL")}){extra}
            """;

        Assert.Equal(PostgresErrorCodes.UniqueViolation, await ErrorAsync(Insert(s.Tenant.Id, userId, $"reservation-confirmed:{notice.Data.GetProperty("reservationId").GetGuid()}", "ReservationConfirmed")));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, await ErrorAsync(Insert(foreignTenant.Id, userId, "otro-gimnasio")));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await ErrorAsync(Insert(s.Tenant.Id, userId, "tipo-malo", "Inventado")));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await ErrorAsync(Insert(s.Tenant.Id, userId, "estado-malo", status: "Entregado")));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await ErrorAsync($"UPDATE classes.inbox_notifications SET delivered_at = NULL WHERE id = '{notice.Id}'"));

        // Lo avisado no cambia, lo leído no vuelve a no leído, lo entregado no cambia de estado.
        Assert.Equal(PostgresErrorCodes.CheckViolation, await ErrorAsync($"UPDATE classes.inbox_notifications SET body = 'otro texto' WHERE id = '{notice.Id}'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await ErrorAsync($"UPDATE classes.inbox_notifications SET recipient_user_id = gen_random_uuid() WHERE id = '{notice.Id}'"));
        await student.Client.PatchAsync($"/api/v1/me/notifications/{notice.Id}/read", null);
        Assert.Equal(PostgresErrorCodes.CheckViolation, await ErrorAsync($"UPDATE classes.inbox_notifications SET read_at = NULL WHERE id = '{notice.Id}'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await ErrorAsync($"UPDATE classes.inbox_notifications SET status = 'Failed', delivered_at = NULL WHERE id = '{notice.Id}'"));
    }

    [Fact]
    public async Task El_rol_de_la_aplicacion_no_puede_borrar_avisos()
    {
        var s = await NewSetupAsync();
        var student = await NewStudentAsync(s);
        var session = await NewSessionAsync(s.Tenant.Owner, s.Class.Id, Local(DayOfWeek.Monday, 9));
        await BookAsync(s.Tenant.Owner, session.Id, student.Member.Id);
        await DrainOutboxAsync();
        await using var connection = new NpgsqlConnection(api.AppConnection);
        await connection.OpenAsync();
        await using (var set = new NpgsqlCommand($"SELECT set_config('app.tenant_id', '{s.Tenant.Id}', false)", connection))
        {
            await set.ExecuteScalarAsync();
        }

        await using var delete = new NpgsqlCommand("DELETE FROM classes.inbox_notifications", connection);
        var error = await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
    }
}
