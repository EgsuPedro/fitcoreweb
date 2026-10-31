using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FitCore.Api.IntegrationTests.Infrastructure;
using FitCore.Application.Core;
using FitCore.Invoicing.Application.Invoices;
using FitCore.Invoicing.Domain;
using FitCore.SharedKernel;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using static FitCore.Api.IntegrationTests.Infrastructure.InvoicingTestApi;

namespace FitCore.Api.IntegrationTests;

/// <summary>
/// Entrega del comprobante autorizado: el RIDE en PDF y el correo con el XML autorizado y el RIDE adjuntos. Contra PostgreSQL real, un SRI simulado y un emisor de correo de prueba (nunca sale a la red):
/// envío automático al autorizarse, reintentos con espera creciente, fallo definitivo, reenvío manual, varias instancias del Worker a la vez y descarga del PDF con su constancia.
/// </summary>
[Collection(ApiCollection2.Name)]
public class InvoicingDeliveryTests(ApiFixture api)
{
    private sealed record Gym(TestTenant Tenant, Guid Branch);

    private sealed record Authorized(Gym Gym, Guid Id, string Key, string AuthorizedXml, string Number);

    private async Task<Gym> NewGymAsync()
    {
        var tenant = await TestApi.NewTenantAsync(api, $"ent-{Guid.NewGuid():N}"[..20]);
        var gym = new Gym(tenant, await TestApi.CreateBranchAsync(tenant.Owner, "UIO"));
        await ConfigureIssuerAsync(tenant.Owner);
        return gym;
    }

    private WebApplicationFactory<Program> Host(FakeSriGateway fake, RecordingEmailSender mail) =>
        api.CreateHostWithServices(services =>
        {
            services.RemoveAll<ISriGateway>();
            services.AddSingleton<ISriGateway>(fake);
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(mail);
        });

    private static Task<TransmissionRunSummary> RunSriAsync(WebApplicationFactory<Program> host, Gym gym) =>
        host.Services.GetRequiredService<InvoiceTransmissionRunner>().RunForTenantAsync(gym.Tenant.Id, CancellationToken.None);

    private static Task<int> RunMailAsync(WebApplicationFactory<Program> host, Gym gym) =>
        host.Services.GetRequiredService<InvoiceEmailRunner>().RunForTenantAsync(gym.Tenant.Id, CancellationToken.None);

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

    private Task<string?> MailColumnAsync(Guid invoiceId, string column) =>
        ScalarAsync($"SELECT {column}::text FROM invoicing.invoice_emails WHERE invoice_id = @i ORDER BY created_at DESC LIMIT 1", ("i", invoiceId));

    private Task<string?> MakeMailDueAsync(Guid invoiceId) =>
        ScalarAsync("UPDATE invoicing.invoice_emails SET next_attempt_at = now() - interval '1 second', locked_until = NULL WHERE invoice_id = @i AND status = 'Pending'", ("i", invoiceId));

    private static async Task<(Guid Id, string Key)> IssueAsync(HttpClient client, Guid obligation, string? buyerIdType = null)
    {
        var response = await client.PostAsJsonAsync("/api/v1/invoicing/invoices", new { sourceType = "obligation", sourceId = obligation, buyerIdType, submit = true });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("id").GetGuid(), body.GetProperty("accessKey").GetString()!);
    }

    /// <summary>Emite y hace que el SRI simulado autorice: deja el comprobante autorizado y, si el comprador tiene correo, su envío en la cola.</summary>
    private async Task<Authorized> AuthorizedInvoiceAsync(Gym gym, WebApplicationFactory<Program> host, FakeSriGateway fake, string? email = "ana.perez@correo.test", string? buyerIdType = null, decimal amount = 30m)
    {
        var member = await NewMemberAsync(gym.Tenant.Owner, gym.Branch, email: email);
        var obligation = await NewObligationAsync(gym.Tenant.Owner, member, amount);
        await PayAsync(gym.Tenant.Owner, member, obligation, amount);
        var (id, key) = await IssueAsync(gym.Tenant.Owner, obligation, buyerIdType);

        fake.ThenReceive(FakeSriGateway.Received());
        await RunSriAsync(host, gym);
        var signed = await gym.Tenant.Owner.GetStringAsync($"/api/v1/invoicing/invoices/{id}/xml");
        fake.ThenAuthorize(FakeSriGateway.Authorized(key, signed));
        await ScalarAsync("UPDATE invoicing.invoice_transmissions SET next_attempt_at = now() - interval '1 second', locked_until = NULL WHERE invoice_id = @i", ("i", id));
        await RunSriAsync(host, gym);

        var invoice = await GetAsync(gym.Tenant.Owner, id);
        Assert.Equal("Authorized", Text(invoice, "status"));
        var authorizedXml = await gym.Tenant.Owner.GetStringAsync($"/api/v1/invoicing/invoices/{id}/xml?kind=AuthorizedXml");
        return new Authorized(gym, id, key, authorizedXml, Text(invoice, "number"));
    }

    private static Task<JsonElement> GetAsync(HttpClient client, Guid id) => client.GetFromJsonAsync<JsonElement>($"/api/v1/invoicing/invoices/{id}");

    private static string Text(JsonElement e, string name) => e.GetProperty(name).GetString()!;

    // ---- envío automático --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Authorizing_an_invoice_queues_the_email_and_the_worker_sends_the_authorized_xml_and_the_ride_attached()
    {
        var gym = await NewGymAsync();
        var fake = new FakeSriGateway();
        var mail = new RecordingEmailSender();
        using var host = Host(fake, mail);

        var invoice = await AuthorizedInvoiceAsync(gym, host, fake);

        Assert.Equal("Pending", await MailColumnAsync(invoice.Id, "status"));
        Assert.Empty(mail.Sent);
        Assert.Equal(1, await RunMailAsync(host, gym));

        var message = Assert.Single(mail.Sent);
        Assert.Equal("ana.perez@correo.test", message.To);
        Assert.Contains(invoice.Number, message.Subject, StringComparison.Ordinal);
        Assert.Contains("Fitness Quito", message.Subject, StringComparison.Ordinal);
        Assert.Equal(2, message.Attachments!.Count);
        var pdf = message.Attachments.Single(a => a.ContentType == "application/pdf");
        Assert.Equal($"Factura-{invoice.Number}.pdf", pdf.FileName);
        Assert.Equal("%PDF-", Encoding.ASCII.GetString(pdf.Content, 0, 5));
        var xml = message.Attachments.Single(a => a.ContentType == "application/xml");
        Assert.Equal($"{invoice.Key}.xml", xml.FileName);
        Assert.Equal(invoice.AuthorizedXml, Encoding.UTF8.GetString(xml.Content));

        Assert.Equal("Sent", await MailColumnAsync(invoice.Id, "status"));
        Assert.Equal("1", await MailColumnAsync(invoice.Id, "attempts"));
        var dto = await GetAsync(gym.Tenant.Owner, invoice.Id);
        var email = Assert.Single(dto.GetProperty("emails").EnumerateArray());
        Assert.Equal("Sent", Text(email, "status"));
        Assert.True(email.GetProperty("automatic").GetBoolean());
        var events = dto.GetProperty("events").EnumerateArray().Select(e => Text(e, "type")).ToList();
        Assert.Contains("EmailQueued", events);
        Assert.Contains("EmailSent", events);
        // El historial no muestra la dirección completa.
        Assert.DoesNotContain("ana.perez@correo.test", dto.GetProperty("events").ToString(), StringComparison.Ordinal);

        // Ya enviado: no se vuelve a enviar.
        Assert.Equal(0, await RunMailAsync(host, gym));
        Assert.Single(mail.Sent);
    }

    [Fact]
    public async Task A_final_consumer_or_a_buyer_without_email_gets_no_automatic_email()
    {
        var gym = await NewGymAsync();
        var fake = new FakeSriGateway();
        var mail = new RecordingEmailSender();
        using var host = Host(fake, mail);

        var finalConsumer = await AuthorizedInvoiceAsync(gym, host, fake, buyerIdType: "FinalConsumer");
        var noEmail = await AuthorizedInvoiceAsync(gym, host, fake, email: null);

        Assert.Equal("0", await ScalarAsync("SELECT count(*)::text FROM invoicing.invoice_emails WHERE invoice_id = ANY(@ids)", ("ids", new[] { finalConsumer.Id, noEmail.Id })));
        Assert.Equal(0, await RunMailAsync(host, gym));
        Assert.Empty(mail.Sent);
    }

    // ---- reintentos --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_failing_mail_server_is_retried_with_a_growing_wait_and_the_invoice_stays_authorized()
    {
        var gym = await NewGymAsync();
        var fake = new FakeSriGateway();
        var mail = new RecordingEmailSender { Behavior = _ => Error.Failure("email.send_failed", "x") };
        using var host = Host(fake, mail);
        var invoice = await AuthorizedInvoiceAsync(gym, host, fake);

        await RunMailAsync(host, gym);

        Assert.Equal("Pending", await MailColumnAsync(invoice.Id, "status"));
        Assert.Equal("1", await MailColumnAsync(invoice.Id, "attempts"));
        Assert.Equal("email.send_failed", await MailColumnAsync(invoice.Id, "last_error"));
        Assert.Equal("true", await ScalarAsync("SELECT (next_attempt_at > now() + interval '30 seconds')::text FROM invoicing.invoice_emails WHERE invoice_id = @i", ("i", invoice.Id)));
        Assert.Equal("Authorized", Text(await GetAsync(gym.Tenant.Owner, invoice.Id), "status"));

        // Antes de la fecha prevista no se vuelve a intentar.
        Assert.Equal(0, await RunMailAsync(host, gym));

        // El servidor se recupera.
        mail.Behavior = _ => Result.Success();
        await MakeMailDueAsync(invoice.Id);
        Assert.Equal(1, await RunMailAsync(host, gym));
        Assert.Equal("Sent", await MailColumnAsync(invoice.Id, "status"));
        Assert.Equal("2", await MailColumnAsync(invoice.Id, "attempts"));
        Assert.Null(await MailColumnAsync(invoice.Id, "last_error"));
    }

    [Fact]
    public async Task After_the_last_attempt_the_email_fails_for_good_and_leaves_a_trace_in_the_history()
    {
        var gym = await NewGymAsync();
        var fake = new FakeSriGateway();
        var mail = new RecordingEmailSender { Behavior = _ => Error.Failure("email.send_failed", "x") };
        using var host = Host(fake, mail);
        var invoice = await AuthorizedInvoiceAsync(gym, host, fake);
        await ScalarAsync("UPDATE invoicing.invoice_emails SET attempts = 4 WHERE invoice_id = @i", ("i", invoice.Id));

        await RunMailAsync(host, gym);

        Assert.Equal("Failed", await MailColumnAsync(invoice.Id, "status"));
        Assert.Equal("5", await MailColumnAsync(invoice.Id, "attempts"));
        Assert.Contains("EmailFailed", (await GetAsync(gym.Tenant.Owner, invoice.Id)).GetProperty("events").EnumerateArray().Select(e => Text(e, "type")));
    }

    [Fact]
    public async Task An_invalid_recipient_fails_at_once_without_retrying()
    {
        var gym = await NewGymAsync();
        var fake = new FakeSriGateway();
        var mail = new RecordingEmailSender { Behavior = _ => Error.Validation("email.invalid_recipient", "x") };
        using var host = Host(fake, mail);
        var invoice = await AuthorizedInvoiceAsync(gym, host, fake);

        await RunMailAsync(host, gym);

        Assert.Equal("Failed", await MailColumnAsync(invoice.Id, "status"));
        Assert.Equal("1", await MailColumnAsync(invoice.Id, "attempts"));
    }

    [Fact]
    public async Task Without_a_configured_mail_server_nothing_is_attempted_and_no_attempt_is_spent()
    {
        var gym = await NewGymAsync();
        var fake = new FakeSriGateway();
        var mail = new RecordingEmailSender { IsConfigured = false };
        using var host = Host(fake, mail);
        var invoice = await AuthorizedInvoiceAsync(gym, host, fake);

        Assert.Equal(0, await RunMailAsync(host, gym));

        Assert.Equal("Pending", await MailColumnAsync(invoice.Id, "status"));
        Assert.Equal("0", await MailColumnAsync(invoice.Id, "attempts"));
    }

    [Fact]
    public async Task Several_worker_instances_at_once_send_each_email_only_once()
    {
        var gym = await NewGymAsync();
        var fake = new FakeSriGateway();
        var mail = new RecordingEmailSender();
        using var host = Host(fake, mail);
        var invoice = await AuthorizedInvoiceAsync(gym, host, fake);

        var runs = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => RunMailAsync(host, gym)));

        Assert.Equal(1, runs.Sum());
        Assert.Single(mail.Sent);
        Assert.Equal("1", await MailColumnAsync(invoice.Id, "attempts"));
    }

    // ---- reenvío manual ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_person_can_resend_to_the_buyer_or_to_another_address_but_not_while_one_is_in_progress()
    {
        var gym = await NewGymAsync();
        var fake = new FakeSriGateway();
        var mail = new RecordingEmailSender();
        using var host = Host(fake, mail);
        var invoice = await AuthorizedInvoiceAsync(gym, host, fake);
        await RunMailAsync(host, gym);
        var owner = gym.Tenant.Owner;

        // Reenvío a otra dirección.
        var resend = await owner.PostAsJsonAsync($"/api/v1/invoicing/invoices/{invoice.Id}/email", new { to = "contabilidad@cliente.test" });
        Assert.Equal(HttpStatusCode.OK, resend.StatusCode);
        var dto = await resend.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(["Pending", "Sent"], dto.GetProperty("emails").EnumerateArray().Select(e => Text(e, "status")));
        Assert.False(dto.GetProperty("emails")[0].GetProperty("automatic").GetBoolean());

        // Con uno en curso no se acepta otro.
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync($"/api/v1/invoicing/invoices/{invoice.Id}/email", new { to = "otra@cliente.test" })).StatusCode);

        await RunMailAsync(host, gym);
        Assert.Equal(["ana.perez@correo.test", "contabilidad@cliente.test"], mail.Sent.Select(m => m.To).Order(StringComparer.Ordinal));

        // Sin dirección: al correo del comprador.
        var again = await owner.PostAsJsonAsync($"/api/v1/invoicing/invoices/{invoice.Id}/email", new { });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        await RunMailAsync(host, gym);
        Assert.Equal(3, mail.Sent.Count);
        Assert.Equal(2, mail.Sent.Count(m => m.To == "ana.perez@correo.test"));
    }

    [Fact]
    public async Task Resending_needs_an_authorized_invoice_a_valid_address_and_a_recipient()
    {
        var gym = await NewGymAsync();
        var fake = new FakeSriGateway();
        var mail = new RecordingEmailSender();
        using var host = Host(fake, mail);
        var owner = gym.Tenant.Owner;

        // Sin autorizar.
        var member = await NewMemberAsync(owner, gym.Branch);
        var obligation = await NewObligationAsync(owner, member, 30m);
        await PayAsync(owner, member, obligation, 30m);
        var pending = await owner.PostAsJsonAsync("/api/v1/invoicing/invoices", new { sourceType = "obligation", sourceId = obligation });
        var pendingId = (await pending.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync($"/api/v1/invoicing/invoices/{pendingId}/email", new { to = "a@b.test" })).StatusCode);

        // Autorizado sin correo del comprador y sin indicar uno.
        var noEmail = await AuthorizedInvoiceAsync(gym, host, fake, email: null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await owner.PostAsJsonAsync($"/api/v1/invoicing/invoices/{noEmail.Id}/email", new { })).StatusCode);

        // Dirección inválida.
        foreach (var bad in new[] { "no-es-correo", "a@b", "x@y.test\r\nBcc: z@w.test" })
        {
            var response = await owner.PostAsJsonAsync($"/api/v1/invoicing/invoices/{noEmail.Id}/email", new { to = bad });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        }

        Assert.Equal("0", await ScalarAsync("SELECT count(*)::text FROM invoicing.invoice_emails WHERE invoice_id = @i", ("i", noEmail.Id)));

        // Dirección válida: se acepta.
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync($"/api/v1/invoicing/invoices/{noEmail.Id}/email", new { to = "cliente@empresa.test" })).StatusCode);
    }

    [Fact]
    public async Task Resending_is_refused_when_the_server_has_no_mail_configured()
    {
        var gym = await NewGymAsync();
        var fake = new FakeSriGateway();
        var mail = new RecordingEmailSender { IsConfigured = false };
        using var host = Host(fake, mail);
        var invoice = await AuthorizedInvoiceAsync(gym, host, fake);
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = gym.Tenant.Owner.DefaultRequestHeaders.Authorization;

        var response = await client.PostAsJsonAsync($"/api/v1/invoicing/invoices/{invoice.Id}/email", new { });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    // ---- RIDE --------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_ride_is_downloaded_as_a_pdf_with_the_data_of_the_invoice_and_the_download_leaves_a_trace()
    {
        var gym = await NewGymAsync();
        var fake = new FakeSriGateway();
        var mail = new RecordingEmailSender();
        using var host = Host(fake, mail);
        var invoice = await AuthorizedInvoiceAsync(gym, host, fake);

        var response = await gym.Tenant.Owner.GetAsync($"/api/v1/invoicing/invoices/{invoice.Id}/ride");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal($"Factura-{invoice.Number}.pdf", response.Content.Headers.ContentDisposition!.FileName!.Trim('"'));
        var pdf = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal("%PDF-", Encoding.ASCII.GetString(pdf, 0, 5));
        Assert.Contains("RideDownloaded", (await GetAsync(gym.Tenant.Owner, invoice.Id)).GetProperty("events").EnumerateArray().Select(e => Text(e, "type")));
    }

    [Fact]
    public async Task There_is_no_ride_before_authorization_or_for_an_invoice_of_another_gym()
    {
        var gym = await NewGymAsync();
        var fake = new FakeSriGateway();
        var mail = new RecordingEmailSender();
        using var host = Host(fake, mail);
        var owner = gym.Tenant.Owner;
        var member = await NewMemberAsync(owner, gym.Branch);
        var obligation = await NewObligationAsync(owner, member, 30m);
        await PayAsync(owner, member, obligation, 30m);
        var issued = await owner.PostAsJsonAsync("/api/v1/invoicing/invoices", new { sourceType = "obligation", sourceId = obligation });
        var id = (await issued.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.Conflict, (await owner.GetAsync($"/api/v1/invoicing/invoices/{id}/ride")).StatusCode);

        var other = await NewGymAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other.Tenant.Owner.GetAsync($"/api/v1/invoicing/invoices/{id}/ride")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Tenant.Owner.PostAsJsonAsync($"/api/v1/invoicing/invoices/{id}/email", new { })).StatusCode);
    }
}
