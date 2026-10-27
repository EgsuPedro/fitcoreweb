using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FitCore.Api.IntegrationTests.Infrastructure;
using FitCore.Commercial.Application.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using static FitCore.Api.IntegrationTests.Infrastructure.CommercialTestApi;

namespace FitCore.Api.IntegrationTests;

[Collection(ApiCollection3.Name)]
public class NotificationTests(ApiFixture api)
{
    private const string StaffPhone = "0987654321";
    private const string StaffPhoneE164 = "+593987654321";

    private sealed record NotificationRow(
        Guid Id, Guid MemberId, string MemberName, string Type, string RecipientKind, string Recipient, string MessageText,
        string Status, int Attempts, string? LastError, string? Provider, DateOnly MembershipEndDate);

    private sealed record SettingsRow(bool Enabled, string[] StaffPhones, string Provider);

    private sealed record Setup(TestTenant Tenant, Guid Branch, MemberDto Member, MembershipDto Membership);

    private static DateOnly Today =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/Guayaquil")).DateTime);

    private ExpiryNotificationRunner Runner => api.Services.GetRequiredService<ExpiryNotificationRunner>();

    /// <summary>Gimnasio con un alumno (con teléfono) y una membresía activa que termina dentro de <paramref name="daysToEnd"/> días.</summary>
    private async Task<Setup> NewSetupAsync(string code, int daysToEnd, string? phone = "0991234567")
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var branch = await TestApi.CreateBranchAsync(tenant.Owner, code);
        var member = await MemberTests.CreateMemberAsync(tenant.Owner, branch, phone: phone);
        var plan = await CreatePlanAsync(tenant.Owner, "Mensual", 30m);
        var enroll = await tenant.Owner.PostAsJsonAsync("/api/v1/memberships", new { memberId = member.Id, planId = plan.Id });
        Assert.Equal(HttpStatusCode.Created, enroll.StatusCode);
        var membership = (await enroll.Content.ReadFromJsonAsync<MembershipDto>())!;
        await PayAsync(tenant.Owner, member.Id, 30m);
        await SetEndAsync(membership.Id, daysToEnd);
        return new Setup(tenant, branch, member, membership);
    }

    private async Task SetEndAsync(Guid membershipId, int daysToEnd)
    {
        var end = Today.AddDays(daysToEnd);
        await SqlAsync($"UPDATE members.memberships SET start_date = '{end.AddDays(-29):yyyy-MM-dd}', end_date = '{end:yyyy-MM-dd}' WHERE id = '{membershipId}'");
    }

    private async Task SqlAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(api.AdminConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task EnableAsync(HttpClient owner, bool enabled = true, params string[] staffPhones)
    {
        var response = await owner.PutAsJsonAsync("/api/v1/notification-settings", new { enabled, staffPhones });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<List<NotificationRow>> ListAsync(HttpClient client, string query = "")
    {
        var page = await client.GetFromJsonAsync<PagedDto<NotificationRow>>($"/api/v1/notifications?pageSize=100{query}");
        return [.. page!.Items];
    }

    private static async Task GrantConsentAsync(HttpClient owner, Guid memberId, bool granted = true)
    {
        var response = await owner.PutAsJsonAsync($"/api/v1/members/{memberId}/whatsapp-consent", new { granted });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Por_defecto_los_avisos_estan_apagados_y_no_se_genera_nada()
    {
        var s = await NewSetupAsync("OFF", daysToEnd: 2);

        var settings = await s.Tenant.Owner.GetFromJsonAsync<SettingsRow>("/api/v1/notification-settings");
        await Runner.RunAsync(CancellationToken.None);

        Assert.False(settings!.Enabled);
        Assert.Empty(settings.StaffPhones);
        Assert.Equal("Simulated", settings.Provider);
        Assert.Empty(await ListAsync(s.Tenant.Owner));
    }

    [Fact]
    public async Task El_personal_recibe_un_aviso_cuando_faltan_tres_dias_o_menos_y_solo_una_vez()
    {
        var s = await NewSetupAsync("STF", daysToEnd: 3);
        await EnableAsync(s.Tenant.Owner, true, StaffPhone, "+593 98 765 4321");

        var first = await Runner.RunAsync(CancellationToken.None);
        var second = await Runner.RunAsync(CancellationToken.None);

        var list = await ListAsync(s.Tenant.Owner);
        var alert = Assert.Single(list);
        Assert.True(first.Created >= 1 && first.Sent >= 1);
        Assert.Equal(0, second.Created);
        Assert.Equal("StaffExpiringSoon", alert.Type);
        Assert.Equal("Staff", alert.RecipientKind);
        Assert.Equal("Sent", alert.Status);
        Assert.Equal("simulated", alert.Provider);
        Assert.Equal(1, alert.Attempts);
        Assert.Contains(s.Member.Code, alert.MessageText);
        Assert.Contains("vence el", alert.MessageText);
        // El número completo nunca se devuelve en la consulta.
        Assert.DoesNotContain("987654321", alert.Recipient);
        Assert.StartsWith("+59398", alert.Recipient);
        Assert.EndsWith("4321", alert.Recipient);
        // Dos formatos del mismo teléfono se guardan una sola vez, normalizado.
        var settings = await s.Tenant.Owner.GetFromJsonAsync<SettingsRow>("/api/v1/notification-settings");
        Assert.Equal([StaffPhoneE164], settings!.StaffPhones);
    }

    [Fact]
    public async Task No_se_avisa_si_faltan_mas_de_tres_dias()
    {
        var s = await NewSetupAsync("FAR", daysToEnd: 4);
        await EnableAsync(s.Tenant.Owner, true, StaffPhone);
        await GrantConsentAsync(s.Tenant.Owner, s.Member.Id);

        await Runner.RunAsync(CancellationToken.None);

        Assert.Empty(await ListAsync(s.Tenant.Owner));
    }

    [Fact]
    public async Task Al_alumno_solo_se_le_escribe_con_consentimiento_el_dia_del_vencimiento()
    {
        var s = await NewSetupAsync("DUE", daysToEnd: 0);
        await EnableAsync(s.Tenant.Owner, true);

        await Runner.RunAsync(CancellationToken.None);
        Assert.Empty(await ListAsync(s.Tenant.Owner, $"&memberId={s.Member.Id}"));

        await GrantConsentAsync(s.Tenant.Owner, s.Member.Id);
        await Runner.RunAsync(CancellationToken.None);

        var message = Assert.Single(await ListAsync(s.Tenant.Owner, $"&memberId={s.Member.Id}"));
        Assert.Equal("MemberDueDay", message.Type);
        Assert.Equal("Member", message.RecipientKind);
        Assert.Equal("Sent", message.Status);
        Assert.StartsWith("Hola Ana", message.MessageText);
        Assert.Contains("vence hoy", message.MessageText);
        // El mensaje es cordial y no revela montos ni deudas.
        Assert.DoesNotContain("$", message.MessageText);
        Assert.DoesNotContain("deuda", message.MessageText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Tres_dias_despues_del_vencimiento_se_envia_el_segundo_mensaje_y_si_revoca_el_consentimiento_no_se_escribe()
    {
        var s = await NewSetupAsync("AFT", daysToEnd: -3);
        await EnableAsync(s.Tenant.Owner, true);
        await GrantConsentAsync(s.Tenant.Owner, s.Member.Id);

        await Runner.RunAsync(CancellationToken.None);

        var message = Assert.Single(await ListAsync(s.Tenant.Owner));
        Assert.Equal("MemberAfterDue", message.Type);
        Assert.Contains("venció el", message.MessageText);

        var revoked = await NewSetupAsync("REV", daysToEnd: 0);
        await EnableAsync(revoked.Tenant.Owner, true);
        await GrantConsentAsync(revoked.Tenant.Owner, revoked.Member.Id);
        await GrantConsentAsync(revoked.Tenant.Owner, revoked.Member.Id, granted: false);
        await Runner.RunAsync(CancellationToken.None);
        Assert.Empty(await ListAsync(revoked.Tenant.Owner));
    }

    [Fact]
    public async Task Una_membresia_renovada_no_genera_avisos()
    {
        var s = await NewSetupAsync("REN", daysToEnd: 1);
        await EnableAsync(s.Tenant.Owner, true, StaffPhone);
        await GrantConsentAsync(s.Tenant.Owner, s.Member.Id);
        var renew = await s.Tenant.Owner.PostAsJsonAsync($"/api/v1/memberships/{s.Membership.Id}/renew", new { });
        Assert.Equal(HttpStatusCode.Created, renew.StatusCode);

        await Runner.RunAsync(CancellationToken.None);

        Assert.Empty(await ListAsync(s.Tenant.Owner));
    }

    [Fact]
    public async Task Una_membresia_cancelada_no_genera_avisos()
    {
        var s = await NewSetupAsync("CAN", daysToEnd: 1);
        await EnableAsync(s.Tenant.Owner, true, StaffPhone);
        var cancel = await s.Tenant.Owner.PostAsJsonAsync($"/api/v1/memberships/{s.Membership.Id}/cancel", new { reason = "Prueba de avisos" });
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);

        await Runner.RunAsync(CancellationToken.None);

        Assert.Empty(await ListAsync(s.Tenant.Owner));
    }

    [Fact]
    public async Task Activar_el_consentimiento_exige_un_telefono_valido_y_queda_registrado_quien_y_cuando()
    {
        var withoutPhone = await NewSetupAsync("NPH", daysToEnd: 10, phone: null);
        var denied = await withoutPhone.Tenant.Owner.PutAsJsonAsync($"/api/v1/members/{withoutPhone.Member.Id}/whatsapp-consent", new { granted = true });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, denied.StatusCode);
        Assert.Equal("members.whatsapp_phone_required", await TestApi.ProblemCodeAsync(denied));

        var withPhone = await NewSetupAsync("PHN", daysToEnd: 10);
        var ok = await withPhone.Tenant.Owner.PutAsJsonAsync($"/api/v1/members/{withPhone.Member.Id}/whatsapp-consent", new { granted = true });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var json = (await ok.Content.ReadFromJsonAsync<JsonElement>());
        Assert.True(json.GetProperty("whatsAppConsent").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, json.GetProperty("whatsAppConsentAt").ValueKind);
        var by = await ScalarAsync<Guid>($"SELECT whatsapp_consent_by FROM members.members WHERE id = '{withPhone.Member.Id}'");
        Assert.NotEqual(Guid.Empty, by);
    }

    [Fact]
    public async Task Los_avisos_de_un_gimnasio_no_se_ven_en_otro_y_los_permisos_se_aplican()
    {
        var a = await NewSetupAsync("ISA", daysToEnd: 2);
        var b = await NewSetupAsync("ISB", daysToEnd: 2);
        await EnableAsync(a.Tenant.Owner, true, StaffPhone);
        await Runner.RunAsync(CancellationToken.None);

        Assert.Single(await ListAsync(a.Tenant.Owner));
        Assert.Empty(await ListAsync(b.Tenant.Owner));

        var noRead = await ClientWithRoleAsync(api, a.Tenant, "sinaviso@gym.test", true, ["members.read"]);
        Assert.Equal(HttpStatusCode.Forbidden, (await noRead.GetAsync("/api/v1/notifications")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await noRead.PutAsJsonAsync("/api/v1/notification-settings", new { enabled = true, staffPhones = Array.Empty<string>() })).StatusCode);

        // Cambiar la configuración es de todo el gimnasio: quien solo ve una sucursal no puede.
        var scoped = await ClientWithRoleAsync(api, a.Tenant, "unasede@gym.test", false, ["commercial.settings.manage", "notifications.read"], a.Branch);
        var response = await scoped.PutAsJsonAsync("/api/v1/notification-settings", new { enabled = true, staffPhones = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Telefonos_invalidos_del_personal_se_rechazan()
    {
        var tenant = await TestApi.NewTenantAsync(api);

        var bad = await tenant.Owner.PutAsJsonAsync("/api/v1/notification-settings", new { enabled = true, staffPhones = new[] { "abc" } });
        var tooMany = await tenant.Owner.PutAsJsonAsync("/api/v1/notification-settings", new { enabled = true, staffPhones = new[] { "0991111111", "0991111112", "0991111113", "0991111114", "0991111115", "0991111116" } });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooMany.StatusCode);
    }

    [Fact]
    public async Task Si_el_proveedor_falla_se_reintenta_y_tras_tres_intentos_se_deja_de_insistir()
    {
        var s = await NewSetupAsync("FLR", daysToEnd: 1);
        await EnableAsync(s.Tenant.Owner, true, StaffPhone);
        var failing = new SwappedSenderRunner(api.Services, new ScriptedSender(failures: 2));

        var first = await failing.RunAsync(CancellationToken.None);
        var afterFirst = Assert.Single(await ListAsync(s.Tenant.Owner));
        Assert.Equal(1, first.Failed);
        Assert.Equal("Failed", afterFirst.Status);
        Assert.Equal(1, afterFirst.Attempts);
        Assert.Equal("proveedor caído", afterFirst.LastError);

        await failing.RunAsync(CancellationToken.None);
        var recovered = await failing.RunAsync(CancellationToken.None);
        var final = Assert.Single(await ListAsync(s.Tenant.Owner));
        Assert.Equal(1, recovered.Sent);
        Assert.Equal("Sent", final.Status);
        Assert.Equal(3, final.Attempts);

        // Un aviso que agotó los intentos no se vuelve a enviar.
        var exhausted = await NewSetupAsync("EXH", daysToEnd: 1);
        await EnableAsync(exhausted.Tenant.Owner, true, StaffPhone);
        var alwaysFailing = new SwappedSenderRunner(api.Services, new ScriptedSender(failures: 100));
        for (var i = 0; i < 5; i++)
        {
            await alwaysFailing.RunAsync(CancellationToken.None);
        }

        var stuck = Assert.Single(await ListAsync(exhausted.Tenant.Owner));
        Assert.Equal("Failed", stuck.Status);
        Assert.Equal(3, stuck.Attempts);
    }

    [Fact]
    public async Task Dos_ejecuciones_simultaneas_no_duplican_ni_envian_dos_veces()
    {
        var s = await NewSetupAsync("RAC", daysToEnd: 0);
        await EnableAsync(s.Tenant.Owner, true, StaffPhone);
        await GrantConsentAsync(s.Tenant.Owner, s.Member.Id);
        var sender = new ScriptedSender(failures: 0);
        var runner = new SwappedSenderRunner(api.Services, sender);

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => runner.RunAsync(CancellationToken.None)));

        var list = await ListAsync(s.Tenant.Owner);
        Assert.Equal(2, list.Count);
        Assert.All(list, n => Assert.Equal("Sent", n.Status));
        Assert.Equal(2, sender.Sent);
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(api.AdminConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    // ---- Dobles de prueba ------------------------------------------------------------------------------------------

    private sealed class ScriptedSender(int failures) : IWhatsAppSender
    {
        private int _failures = failures;
        private int _sent;

        public int Sent => _sent;

        public string ProviderName => "test";

        public Task<WhatsAppSendResult> SendAsync(WhatsAppMessage message, CancellationToken cancellationToken)
        {
            if (Interlocked.Decrement(ref _failures) >= 0)
            {
                return Task.FromResult(WhatsAppSendResult.Fail("proveedor caído"));
            }

            Interlocked.Increment(ref _sent);
            return Task.FromResult(WhatsAppSendResult.Ok(Guid.NewGuid().ToString("N")));
        }
    }

    /// <summary>Ejecuta el verdadero <see cref="ExpiryNotificationRunner"/> pero con otro emisor (el resto de dependencias es el real).</summary>
    private sealed class SwappedSenderRunner(IServiceProvider services, IWhatsAppSender sender)
    {
        private readonly ExpiryNotificationRunner _runner = new(new SwappingScopeFactory(services, sender), services.GetRequiredService<FitCore.SharedKernel.IClock>(), NullLogger<ExpiryNotificationRunner>.Instance);

        public Task<NotificationRunSummary> RunAsync(CancellationToken cancellationToken) => _runner.RunAsync(cancellationToken);
    }

    private sealed class SwappingScopeFactory(IServiceProvider root, IWhatsAppSender sender) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new SwappingScope(root.CreateScope(), sender);
    }

    private sealed class SwappingScope(IServiceScope inner, IWhatsAppSender sender) : IServiceScope
    {
        public IServiceProvider ServiceProvider { get; } = new SwappingProvider(inner.ServiceProvider, sender);

        public void Dispose() => inner.Dispose();
    }

    private sealed class SwappingProvider(IServiceProvider inner, IWhatsAppSender sender) : IServiceProvider, ISupportRequiredService
    {
        public object? GetService(Type serviceType) => serviceType == typeof(IWhatsAppSender) ? sender : inner.GetService(serviceType);

        public object GetRequiredService(Type serviceType) => serviceType == typeof(IWhatsAppSender) ? sender : inner.GetRequiredService(serviceType);
    }
}
