using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FitCore.Api.IntegrationTests.Infrastructure;
using FitCore.Invoicing.Application.Invoices;
using FitCore.Invoicing.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using static FitCore.Api.IntegrationTests.Infrastructure.InvoicingTestApi;

namespace FitCore.Api.IntegrationTests;

/// <summary>
/// Transmisión al SRI de punta a punta contra PostgreSQL real y un SRI simulado: la cola, la firma y el envío por el Worker, la consulta de autorización, devoluciones, fallos transitorios con espera
/// creciente, estacionamiento, y varias instancias del Worker a la vez sin enviar dos veces. El SRI real se prueba aparte (pruebas opcionales y bloque 7).
/// </summary>
[Collection(ApiCollection2.Name)]
public class InvoicingTransmissionTests(ApiFixture api)
{
    private static string NewSlug() => $"sri-{Guid.NewGuid():N}"[..20];

    private sealed record Gym(TestTenant Tenant, Guid Branch);

    private async Task<Gym> NewGymAsync(bool production = false)
    {
        var tenant = await TestApi.NewTenantAsync(api, NewSlug());
        var gym = new Gym(tenant, await TestApi.CreateBranchAsync(tenant.Owner, "UIO"));
        await ConfigureIssuerAsync(tenant.Owner);
        if (production)
        {
            Assert.Equal(HttpStatusCode.OK, (await tenant.Owner.PutAsJsonAsync("/api/v1/invoicing/issuer", IssuerBody("Production", confirmProduction: true))).StatusCode);
        }

        return gym;
    }

    private WebApplicationFactory<Program> Host(FakeSriGateway fake) =>
        api.CreateHostWithServices(services =>
        {
            services.RemoveAll<ISriGateway>();
            services.AddSingleton<ISriGateway>(fake);
        });

    private static Task<TransmissionRunSummary> RunAsync(WebApplicationFactory<Program> host, Gym gym) =>
        host.Services.GetRequiredService<InvoiceTransmissionRunner>().RunForTenantAsync(gym.Tenant.Id, CancellationToken.None);

    private async Task<string?> ScalarAsync(string sql, params (string Name, object Value)[] args)
    {
        await using var conn = new NpgsqlConnection(api.AdminConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in args)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        return await cmd.ExecuteScalarAsync() is { } result and not DBNull ? result.ToString() : null;
    }

    private async Task<string?> SqlStateAsync(string sql, params (string Name, object Value)[] args)
    {
        try
        {
            await ScalarAsync(sql, args);
            return null;
        }
        catch (PostgresException ex)
        {
            return ex.SqlState;
        }
    }

    private Task<string?> MakeDueAsync(Guid id) =>
        ScalarAsync("UPDATE invoicing.invoice_transmissions SET next_attempt_at = now() - interval '1 second', locked_until = NULL WHERE invoice_id = @i", ("i", id));

    private async Task<string?> StatusAsync(Guid id) => await ScalarAsync("SELECT status FROM invoicing.invoices WHERE id = @i", ("i", id));

    private async Task<string?> QueueAsync(Guid id, string column) => await ScalarAsync($"SELECT {column}::text FROM invoicing.invoice_transmissions WHERE invoice_id = @i", ("i", id));

    private static async Task<(Guid Id, string Key)> IssueAsync(HttpClient client, Guid obligation, bool submit = true)
    {
        var response = await client.PostAsJsonAsync("/api/v1/invoicing/invoices", new { sourceType = "obligation", sourceId = obligation, submit });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("id").GetGuid(), body.GetProperty("accessKey").GetString()!);
    }

    private async Task<(Gym Gym, Guid Id, string Key)> QueuedInvoiceAsync(bool production = false, decimal amount = 30m)
    {
        var gym = await NewGymAsync(production);
        var (_, obligation) = await PaidSaleAsync(gym.Tenant.Owner, gym.Branch, amount);
        var (id, key) = await IssueAsync(gym.Tenant.Owner, obligation);
        return (gym, id, key);
    }

    private static Task<JsonElement> GetAsync(HttpClient client, Guid id) => client.GetFromJsonAsync<JsonElement>($"/api/v1/invoicing/invoices/{id}");

    private static string Text(JsonElement e, string name) => e.GetProperty(name).GetString()!;

    // ---- flujo completo ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_queued_invoice_is_signed_sent_received_and_then_authorized_by_the_worker()
    {
        var (gym, id, key) = await QueuedInvoiceAsync();
        var fake = new FakeSriGateway().ThenReceive(FakeSriGateway.Received());
        using var host = Host(fake);
        Assert.Equal("PendingSignature", await StatusAsync(id));

        // 1.ª pasada: firma, envía y queda «recibido» a la espera de la autorización.
        var first = await RunAsync(host, gym);

        Assert.Equal(1, first.Processed);
        Assert.Equal("Received", await StatusAsync(id));
        var sent = Assert.Single(fake.Sends);
        Assert.Equal(SriEnvironment.Test, sent.Environment);
        var signed = await gym.Tenant.Owner.GetStringAsync($"/api/v1/invoicing/invoices/{id}/xml");
        Assert.Equal(signed, sent.Xml);
        Assert.Empty(fake.Queries);

        // Antes de la fecha prevista no se vuelve a llamar al SRI.
        await RunAsync(host, gym);
        Assert.Empty(fake.Queries);

        // 2.ª pasada: el SRI autoriza.
        fake.ThenAuthorize(FakeSriGateway.Authorized(key, signed));
        await MakeDueAsync(id);
        await RunAsync(host, gym);

        var invoice = await GetAsync(gym.Tenant.Owner, id);
        Assert.Equal("Authorized", Text(invoice, "status"));
        Assert.Equal(key, Text(invoice.GetProperty("authorization"), "number"));
        Assert.Equal(JsonValueKind.Null, invoice.GetProperty("transmission").ValueKind);
        Assert.Equal(["AuthorizedXml", "SignedXml", "Xml"], invoice.GetProperty("documents").EnumerateArray().Select(d => Text(d, "kind")).Order(StringComparer.Ordinal));
        Assert.Equal(["Created", "Queued", "Signed", "PendingSubmission", "Received", "XmlDownloaded", "Authorized"], invoice.GetProperty("events").EnumerateArray().Select(e => Text(e, "type")));
        var authorized = await gym.Tenant.Owner.GetStringAsync($"/api/v1/invoicing/invoices/{id}/xml?kind=AuthorizedXml");
        Assert.Contains("<autorizacion>", authorized, StringComparison.Ordinal);
        Assert.Contains(key, authorized, StringComparison.Ordinal);
        var queried = Assert.Single(fake.Queries);
        Assert.Equal(key, queried.Key);

        // Constancia de cada intercambio con el SRI, la más reciente primero.
        var attempts = invoice.GetProperty("sriAttempts").EnumerateArray().ToList();
        Assert.Equal(["Authorized", "Received"], attempts.Select(a => Text(a, "outcome")));
        Assert.Equal("60", Text(attempts[0].GetProperty("messages")[0], "id"));
    }

    [Fact]
    public async Task Nothing_is_sent_unless_the_invoice_was_queued()
    {
        var gym = await NewGymAsync();
        var (_, obligation) = await PaidSaleAsync(gym.Tenant.Owner, gym.Branch, 25m);
        var (id, _) = await IssueAsync(gym.Tenant.Owner, obligation, submit: false);
        var fake = new FakeSriGateway();
        using var host = Host(fake);

        var summary = await RunAsync(host, gym);

        Assert.Equal(0, summary.Processed + summary.Failed);
        Assert.Equal("PendingSignature", await StatusAsync(id));
        Assert.Empty(fake.Sends);

        // Un envío explícito sí lo pone en la cola (una sola vez, aunque se pida varias).
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => gym.Tenant.Owner.PostAsync($"/api/v1/invoicing/invoices/{id}/submit", null)));
        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal("1", await ScalarAsync("SELECT count(*)::text FROM invoicing.invoice_transmissions WHERE invoice_id = @i", ("i", id)));
        Assert.Equal("1", await ScalarAsync("SELECT count(*)::text FROM invoicing.invoice_events WHERE invoice_id = @i AND type = 'Queued'", ("i", id)));
    }

    [Fact]
    public async Task Invoices_of_a_production_issuer_go_to_the_production_environment()
    {
        var (gym, _, _) = await QueuedInvoiceAsync(production: true);
        var fake = new FakeSriGateway().ThenReceive(FakeSriGateway.Received());
        using var host = Host(fake);

        await RunAsync(host, gym);

        Assert.Equal(SriEnvironment.Production, Assert.Single(fake.Sends).Environment);
    }

    // ---- devoluciones ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_returned_invoice_keeps_the_messages_of_the_SRI_waits_for_a_person_and_can_be_resent_with_the_same_key()
    {
        var (gym, id, key) = await QueuedInvoiceAsync();
        var fake = new FakeSriGateway().ThenReceive(FakeSriGateway.Returned("2", "RUC DEL EMISOR NO ACTIVO", "Verificar el RUC"));
        using var host = Host(fake);

        await RunAsync(host, gym);

        var rejected = await GetAsync(gym.Tenant.Owner, id);
        Assert.Equal("Rejected", Text(rejected, "status"));
        Assert.Equal(JsonValueKind.Null, rejected.GetProperty("transmission").ValueKind);
        var attempt = rejected.GetProperty("sriAttempts")[0];
        Assert.Equal("Returned", Text(attempt, "outcome"));
        Assert.Equal("RUC DEL EMISOR NO ACTIVO", Text(attempt.GetProperty("messages")[0], "text"));
        Assert.Contains("2: RUC DEL EMISOR NO ACTIVO", Text(rejected.GetProperty("events").EnumerateArray().Last(), "detail"), StringComparison.Ordinal);

        // No se reintenta solo.
        await RunAsync(host, gym);
        Assert.Single(fake.Sends);

        // La persona corrige el problema (aquí: nada que tocar en el comprobante) y lo reenvía: misma clave y mismo número.
        var number = Text(rejected, "number");
        fake.ThenReceive(FakeSriGateway.Received());
        var resubmit = await gym.Tenant.Owner.PostAsync($"/api/v1/invoicing/invoices/{id}/submit", null);
        Assert.Equal(HttpStatusCode.OK, resubmit.StatusCode);
        Assert.Equal("PendingSubmission", await StatusAsync(id));
        await RunAsync(host, gym);

        var again = await GetAsync(gym.Tenant.Owner, id);
        Assert.Equal("Received", Text(again, "status"));
        Assert.Equal(key, Text(again, "accessKey"));
        Assert.Equal(number, Text(again, "number"));
        Assert.Equal(2, fake.Sends.Count);
        Assert.Equal(fake.Sends.First().Xml, fake.Sends.Last().Xml);
    }

    [Theory]
    [InlineData("43", "CLAVE ACCESO REGISTRADA")]
    [InlineData("70", "CLAVE DE ACCESO EN PROCESAMIENTO")]
    public async Task A_key_the_SRI_already_knows_is_not_resent_and_its_authorization_is_queried(string code, string text)
    {
        var (gym, id, key) = await QueuedInvoiceAsync();
        var fake = new FakeSriGateway().ThenReceive(FakeSriGateway.Returned(code, text));
        using var host = Host(fake);

        await RunAsync(host, gym);

        Assert.Equal("Received", await StatusAsync(id));
        Assert.Single(fake.Sends);
        fake.ThenAuthorize(FakeSriGateway.Pending(key));
        await MakeDueAsync(id);
        await RunAsync(host, gym);
        Assert.Equal("Processing", await StatusAsync(id));
        Assert.Single(fake.Sends);
    }

    [Fact]
    public async Task A_duplicate_sequential_error_is_a_real_rejection_not_a_known_key()
    {
        var (gym, id, _) = await QueuedInvoiceAsync();
        var fake = new FakeSriGateway().ThenReceive(FakeSriGateway.Returned("45", "SECUENCIAL REGISTRADO"));
        using var host = Host(fake);

        await RunAsync(host, gym);

        Assert.Equal("Rejected", await StatusAsync(id));
    }

    // ---- fallos transitorios -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task When_the_SRI_does_not_answer_the_invoice_stays_pending_and_is_retried_with_a_growing_wait_without_rejecting_it()
    {
        var (gym, id, _) = await QueuedInvoiceAsync();
        var fake = new FakeSriGateway().ThenFailReception("sri_timeout").ThenFailReception("sri_http_503");
        using var host = Host(fake);

        await RunAsync(host, gym);

        Assert.Equal("PendingSubmission", await StatusAsync(id));
        Assert.Equal("1", await QueueAsync(id, "attempts"));
        Assert.Equal("sri_timeout", await QueueAsync(id, "last_error"));
        var firstWait = double.Parse(await ScalarAsync("SELECT extract(epoch FROM next_attempt_at - last_attempt_at)::text FROM invoicing.invoice_transmissions WHERE invoice_id = @i", ("i", id)) ?? "0", System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(firstWait, 24, 36); // 30 s ± 20 %

        // Mientras no llegue la hora, no se vuelve a llamar.
        await RunAsync(host, gym);
        Assert.Single(fake.Sends);

        await MakeDueAsync(id);
        await RunAsync(host, gym);
        Assert.Equal("2", await QueueAsync(id, "attempts"));
        Assert.Equal("sri_http_503", await QueueAsync(id, "last_error"));
        var secondWait = double.Parse(await ScalarAsync("SELECT extract(epoch FROM next_attempt_at - last_attempt_at)::text FROM invoicing.invoice_transmissions WHERE invoice_id = @i", ("i", id)) ?? "0", System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(secondWait, 48, 72); // 1 min ± 20 %

        // Cuando el SRI vuelve, el mismo XML firmado sale una vez más y todo se normaliza.
        fake.ThenReceive(FakeSriGateway.Received());
        await MakeDueAsync(id);
        await RunAsync(host, gym);
        Assert.Equal("Received", await StatusAsync(id));
        Assert.Equal("0", await QueueAsync(id, "attempts"));
        Assert.Null(await QueueAsync(id, "last_error"));
        Assert.Equal(3, fake.Sends.Count);
        Assert.Single(fake.Sends.Select(s => s.Xml).Distinct());

        var invoice = await GetAsync(gym.Tenant.Owner, id);
        Assert.Equal(["Received", "Unavailable", "Unavailable"], invoice.GetProperty("sriAttempts").EnumerateArray().Select(a => Text(a, "outcome")));
        Assert.Equal("sri_timeout", invoice.GetProperty("sriAttempts")[2].GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task The_authorization_is_polled_until_the_SRI_resolves_it()
    {
        var (gym, id, key) = await QueuedInvoiceAsync();
        var fake = new FakeSriGateway().ThenReceive(FakeSriGateway.Received())
            .ThenAuthorize(FakeSriGateway.Nothing(key))
            .ThenFailAuthorization("sri_timeout")
            .ThenAuthorize(FakeSriGateway.Pending(key))
            .ThenAuthorize(FakeSriGateway.Pending(key, "PPR"));
        using var host = Host(fake);
        await RunAsync(host, gym);
        Assert.Equal("Received", await StatusAsync(id));

        await MakeDueAsync(id);
        await RunAsync(host, gym); // el SRI aún no sabe nada
        Assert.Equal("Received", await StatusAsync(id));
        Assert.Equal("1", await QueueAsync(id, "polls"));

        await MakeDueAsync(id);
        await RunAsync(host, gym); // el SRI no responde: no cuenta como consulta sin respuesta
        Assert.Equal("Received", await StatusAsync(id));
        Assert.Equal("1", await QueueAsync(id, "attempts"));
        Assert.Equal("1", await QueueAsync(id, "polls"));

        await MakeDueAsync(id);
        await RunAsync(host, gym); // en procesamiento
        Assert.Equal("Processing", await StatusAsync(id));

        await MakeDueAsync(id);
        await RunAsync(host, gym); // sigue en procesamiento, sin cambiar de estado
        Assert.Equal("Processing", await StatusAsync(id));
        Assert.Equal("3", await QueueAsync(id, "polls"));

        var signed = await gym.Tenant.Owner.GetStringAsync($"/api/v1/invoicing/invoices/{id}/xml");
        fake.ThenAuthorize(FakeSriGateway.Authorized(key, signed));
        await MakeDueAsync(id);
        await RunAsync(host, gym);

        Assert.Equal("Authorized", await StatusAsync(id));
        Assert.Equal(5, fake.Queries.Count);
        Assert.Single(fake.Sends);
    }

    [Fact]
    public async Task A_not_authorized_invoice_is_final_and_keeps_the_reasons()
    {
        var (gym, id, key) = await QueuedInvoiceAsync();
        var fake = new FakeSriGateway().ThenReceive(FakeSriGateway.Received()).ThenAuthorize(FakeSriGateway.NotAuthorized(key));
        using var host = Host(fake);
        await RunAsync(host, gym);

        await MakeDueAsync(id);
        await RunAsync(host, gym);

        var invoice = await GetAsync(gym.Tenant.Owner, id);
        Assert.Equal("NotAuthorized", Text(invoice, "status"));
        Assert.Contains("39: FIRMA INVALIDA", Text(invoice.GetProperty("events").EnumerateArray().Last(), "detail"), StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, invoice.GetProperty("authorization").ValueKind);
        var again = await gym.Tenant.Owner.PostAsync($"/api/v1/invoicing/invoices/{id}/submit", null);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("invoicing.invoice_not_submittable", (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Without_a_certificate_the_worker_cannot_sign_so_it_waits_and_never_calls_the_SRI()
    {
        var (gym, id, _) = await QueuedInvoiceAsync();
        Assert.Equal(HttpStatusCode.OK, (await gym.Tenant.Owner.DeleteAsync("/api/v1/invoicing/certificate")).StatusCode);
        var fake = new FakeSriGateway().ThenReceive(FakeSriGateway.Received());
        using var host = Host(fake);

        var summary = await RunAsync(host, gym);

        Assert.Equal(1, summary.Failed);
        Assert.Equal("PendingSignature", await StatusAsync(id));
        Assert.Equal("certificate_not_found", await QueueAsync(id, "last_error"));
        Assert.Empty(fake.Sends);
        Assert.Equal("SignFailed", (await GetAsync(gym.Tenant.Owner, id)).GetProperty("sriAttempts")[0].GetProperty("outcome").GetString());

        // Con un certificado nuevo, el mismo comprobante sigue su camino.
        using var form = new MultipartFormDataContent { { new ByteArrayContent(P12()), "file", "firma.p12" }, { new StringContent(CertPassword), "password" } };
        Assert.Equal(HttpStatusCode.OK, (await gym.Tenant.Owner.PutAsync("/api/v1/invoicing/certificate", form)).StatusCode);
        await MakeDueAsync(id);
        await RunAsync(host, gym);
        Assert.Equal("Received", await StatusAsync(id));
    }

    [Fact]
    public async Task An_item_that_stays_unresolved_too_long_is_parked_and_a_person_can_reactivate_it()
    {
        var (gym, id, _) = await QueuedInvoiceAsync();
        var fake = new FakeSriGateway().ThenReceive(FakeSriGateway.Received());
        using var host = Host(fake);
        await ScalarAsync("UPDATE invoicing.invoice_transmissions SET first_attempt_at = now() - interval '3 days' WHERE invoice_id = @i", ("i", id));

        await RunAsync(host, gym);

        Assert.Empty(fake.Sends);
        Assert.Equal("true", await QueueAsync(id, "parked"));
        var parked = await GetAsync(gym.Tenant.Owner, id);
        Assert.True(parked.GetProperty("transmission").GetProperty("parked").GetBoolean());
        Assert.Equal("Parked", Text(parked.GetProperty("events").EnumerateArray().Last(), "type"));

        // Estacionado no se procesa solo, ni aunque su hora haya llegado.
        await MakeDueAsync(id);
        await RunAsync(host, gym);
        Assert.Empty(fake.Sends);

        var reactivate = await gym.Tenant.Owner.PostAsync($"/api/v1/invoicing/invoices/{id}/submit", null);
        Assert.Equal(HttpStatusCode.OK, reactivate.StatusCode);
        Assert.Equal("false", await QueueAsync(id, "parked"));
        Assert.Equal("0", await QueueAsync(id, "attempts"));
        await RunAsync(host, gym);
        Assert.Equal("Received", await StatusAsync(id));
    }

    // ---- varias instancias del Worker --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Several_worker_instances_at_the_same_time_send_each_invoice_exactly_once()
    {
        var gym = await NewGymAsync();
        var ids = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            var (_, obligation) = await PaidSaleAsync(gym.Tenant.Owner, gym.Branch, 10m + i);
            ids.Add((await IssueAsync(gym.Tenant.Owner, obligation)).Id);
        }

        var fake = new FakeSriGateway { Delay = TimeSpan.FromMilliseconds(300) };
        for (var i = 0; i < 6; i++)
        {
            fake.ThenReceive(FakeSriGateway.Received());
        }

        using var hostA = Host(fake);
        using var hostB = Host(fake);

        var runs = await Task.WhenAll(RunAsync(hostA, gym), RunAsync(hostB, gym), RunAsync(hostA, gym), RunAsync(hostB, gym));

        Assert.Equal(6, runs.Sum(r => r.Processed));
        Assert.Equal(6, fake.Sends.Count);
        Assert.Equal(6, fake.Sends.Select(s => s.Xml).Distinct().Count());
        foreach (var id in ids)
        {
            Assert.Equal("Received", await StatusAsync(id));
        }
    }

    [Fact]
    public async Task A_dead_worker_does_not_block_an_invoice_forever_because_its_claim_expires()
    {
        var (gym, id, _) = await QueuedInvoiceAsync();
        await ScalarAsync("UPDATE invoicing.invoice_transmissions SET locked_until = now() + interval '5 minutes' WHERE invoice_id = @i", ("i", id));
        var fake = new FakeSriGateway().ThenReceive(FakeSriGateway.Received());
        using var host = Host(fake);

        await RunAsync(host, gym);
        Assert.Empty(fake.Sends); // otra instancia lo tiene reclamado

        await ScalarAsync("UPDATE invoicing.invoice_transmissions SET locked_until = now() - interval '1 second' WHERE invoice_id = @i", ("i", id));
        await RunAsync(host, gym);
        Assert.Single(fake.Sends);
    }

    // ---- permisos y aislamiento ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Queueing_needs_the_send_permission_and_only_for_invoices_the_user_can_see()
    {
        var gym = await NewGymAsync();
        var (_, obligation) = await PaidSaleAsync(gym.Tenant.Owner, gym.Branch, 30m);
        var (id, _) = await IssueAsync(gym.Tenant.Owner, obligation, submit: false);
        var otherBranch = await TestApi.CreateBranchAsync(gym.Tenant.Owner, "GYE");

        var cashierEmail = $"cajero-{Guid.NewGuid():N}"[..24] + "@sri.test";
        await TestApi.CreateUserAsync(gym.Tenant.Owner, cashierEmail, "Cajero", gym.Branch);
        var cashier = TestApi.Authenticated(api, await TestApi.LoginAsync(api, gym.Tenant.Slug, cashierEmail, TestApi.UserPassword));

        // Con permiso de envío pero solo en otra sucursal: no ve el comprobante.
        var accountantElsewhere = await CommercialTestApi.ClientWithRoleAsync(
            api, gym.Tenant, $"fac-{Guid.NewGuid():N}"[..20] + "@sri.test", false, ["invoicing.read", "invoicing.send"], otherBranch);
        var stranger = await TestApi.NewTenantAsync(api, NewSlug());
        using var anonymous = api.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync($"/api/v1/invoicing/invoices/{id}/submit", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PostAsync($"/api/v1/invoicing/invoices/{id}/submit", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await accountantElsewhere.PostAsync($"/api/v1/invoicing/invoices/{id}/submit", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.Owner.PostAsync($"/api/v1/invoicing/invoices/{id}/submit", null)).StatusCode);
        Assert.Equal("0", await ScalarAsync("SELECT count(*)::text FROM invoicing.invoice_transmissions WHERE invoice_id = @i", ("i", id)));
        Assert.Equal(HttpStatusCode.OK, (await gym.Tenant.Owner.PostAsync($"/api/v1/invoicing/invoices/{id}/submit", null)).StatusCode);
    }

    [Fact]
    public async Task One_gyms_worker_run_never_touches_another_gyms_queue()
    {
        var (gymA, idA, _) = await QueuedInvoiceAsync();
        var (gymB, idB, _) = await QueuedInvoiceAsync();
        var fake = new FakeSriGateway().ThenReceive(FakeSriGateway.Received());
        using var host = Host(fake);

        await RunAsync(host, gymA);

        Assert.Equal("Received", await StatusAsync(idA));
        Assert.Equal("PendingSignature", await StatusAsync(idB));
        Assert.NotNull(gymB);
    }

    // ---- la base ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_database_requires_the_authorization_document_and_fixes_the_authorization_once()
    {
        var (gym, id, key) = await QueuedInvoiceAsync();
        var fake = new FakeSriGateway().ThenReceive(FakeSriGateway.Received());
        using var host = Host(fake);
        await RunAsync(host, gym);
        Assert.Equal("Received", await StatusAsync(id));

        // «Autorizado» sin el XML autorizado, o sin número y fecha, es imposible.
        Assert.Equal("FC131", await SqlStateAsync("UPDATE invoicing.invoices SET status = 'Authorized', authorization_number = @k, authorized_at = now() WHERE id = @i", ("i", id), ("k", key)));
        Assert.Equal("FC131", await SqlStateAsync("UPDATE invoicing.invoices SET status = 'Authorized' WHERE id = @i", ("i", id)));
        // Un número de autorización en un comprobante que no está autorizado, tampoco.
        Assert.Equal("23514", await SqlStateAsync("UPDATE invoicing.invoices SET authorization_number = @k, authorized_at = now() WHERE id = @i", ("i", id), ("k", key)));

        fake.ThenAuthorize(FakeSriGateway.Authorized(key, "<factura/>"));
        await MakeDueAsync(id);
        await RunAsync(host, gym);
        Assert.Equal("Authorized", await StatusAsync(id));

        // Ya autorizado: la autorización no cambia y nada vuelve atrás.
        Assert.Equal("FC130", await SqlStateAsync("UPDATE invoicing.invoices SET authorization_number = '1' WHERE id = @i", ("i", id)));
        Assert.Equal("FC130", await SqlStateAsync("UPDATE invoicing.invoices SET authorized_at = now() WHERE id = @i", ("i", id)));
        Assert.Equal("FC131", await SqlStateAsync("UPDATE invoicing.invoices SET status = 'Received' WHERE id = @i", ("i", id)));
        Assert.Equal("FC130", await SqlStateAsync("UPDATE invoicing.invoice_sri_attempts SET outcome = 'Received' WHERE invoice_id = @i", ("i", id)));
        Assert.Equal("FC130", await SqlStateAsync("DELETE FROM invoicing.invoice_sri_attempts WHERE invoice_id = @i", ("i", id)));
    }

    [Fact]
    public async Task The_database_only_allows_resending_a_returned_invoice_not_other_backward_moves()
    {
        var (gym, id, _) = await QueuedInvoiceAsync();
        var fake = new FakeSriGateway().ThenReceive(FakeSriGateway.Returned());
        using var host = Host(fake);
        await RunAsync(host, gym);
        Assert.Equal("Rejected", await StatusAsync(id));

        Assert.Equal("FC131", await SqlStateAsync("UPDATE invoicing.invoices SET status = 'Received' WHERE id = @i", ("i", id)));
        Assert.Equal("FC131", await SqlStateAsync("UPDATE invoicing.invoices SET status = 'SignedDocument' WHERE id = @i", ("i", id)));
        Assert.Null(await SqlStateAsync("UPDATE invoicing.invoices SET status = 'PendingSubmission', updated_at = now() WHERE id = @i", ("i", id)));
    }

    [Fact]
    public async Task An_authorization_dated_in_the_SRI_time_zone_is_stored_correctly()
    {
        // Las respuestas reales traen la hora con su zona (-05:00): el Worker real falló al guardarla hasta que se normalizó a UTC.
        var (gym, id, key) = await QueuedInvoiceAsync();
        var fake = new FakeSriGateway().ThenReceive(FakeSriGateway.Received());
        using var host = Host(fake);
        await RunAsync(host, gym);
        var signed = await gym.Tenant.Owner.GetStringAsync($"/api/v1/invoicing/invoices/{id}/xml");
        var local = new DateTimeOffset(2026, 10, 30, 18, 0, 5, TimeSpan.FromHours(-5));
        fake.ThenAuthorize(FakeSriGateway.Authorized(key, signed, local));
        await MakeDueAsync(id);

        await RunAsync(host, gym);

        Assert.Equal("Authorized", await StatusAsync(id));
        var invoice = await GetAsync(gym.Tenant.Owner, id);
        Assert.Equal(local.ToUniversalTime(), invoice.GetProperty("authorization").GetProperty("authorizedAt").GetDateTimeOffset().ToUniversalTime());
    }
}
