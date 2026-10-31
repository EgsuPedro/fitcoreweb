using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using FitCore.Api.IntegrationTests.Infrastructure;
using FitCore.Application.Core;
using FitCore.Invoicing.Application.Invoices;
using FitCore.Invoicing.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using static FitCore.Api.IntegrationTests.Infrastructure.InvoicingTestApi;

namespace FitCore.Api.IntegrationTests;

/// <summary>
/// Notas de crédito de punta a punta contra PostgreSQL real y un SRI simulado: solo sobre facturas autorizadas, total o parcial por línea, sin acreditar más de lo facturado (ni con solicitudes en
/// paralelo ni directo en la base), receptor identificado, numeración propia, y el mismo ciclo de la factura (firma con el XSD oficial de la nota, SRI, autorización, RIDE y correo).
/// </summary>
[Collection(ApiCollection2.Name)]
public class InvoicingCreditNoteTests(ApiFixture api)
{
    private sealed record Gym(TestTenant Tenant, Guid Branch);

    private sealed record Authorized(Gym Gym, Guid Id, string Number, Guid LineId);

    private async Task<Gym> NewGymAsync()
    {
        var tenant = await TestApi.NewTenantAsync(api, $"ncr-{Guid.NewGuid():N}"[..20]);
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

    private async Task<string?> SqlStateAsync(string sql, params (string Name, object Value)[] args)
    {
        try
        {
            await ScalarAsync(sql, args);
            return null;
        }
        catch (PostgresException ex)
        {
            return ex.SqlState + (ex.SqlState == "23514" ? ":" + ex.ConstraintName : string.Empty);
        }
    }

    private static string Text(JsonElement e, string name) => e.GetProperty(name).GetString()!;

    private static Task<JsonElement> GetAsync(HttpClient client, Guid id) => client.GetFromJsonAsync<JsonElement>($"/api/v1/invoicing/invoices/{id}");

    /// <summary>Emite una factura y hace que el SRI simulado la autorice.</summary>
    private async Task<Authorized> AuthorizedInvoiceAsync(Gym gym, WebApplicationFactory<Program> host, FakeSriGateway fake, decimal amount = 30m, string? buyerIdType = null, string? email = "ana.perez@correo.test")
    {
        var owner = gym.Tenant.Owner;
        var member = await NewMemberAsync(owner, gym.Branch, email: email);
        var obligation = await NewObligationAsync(owner, member, amount);
        await PayAsync(owner, member, obligation, amount);
        var issued = await owner.PostAsJsonAsync("/api/v1/invoicing/invoices", new { sourceType = "obligation", sourceId = obligation, buyerIdType, submit = true });
        Assert.Equal(HttpStatusCode.Created, issued.StatusCode);
        var body = await issued.Content.ReadFromJsonAsync<JsonElement>();
        var id = body.GetProperty("id").GetGuid();
        var key = Text(body, "accessKey");

        fake.ThenReceive(FakeSriGateway.Received());
        await RunSriAsync(host, gym);
        var signed = await owner.GetStringAsync($"/api/v1/invoicing/invoices/{id}/xml");
        fake.ThenAuthorize(FakeSriGateway.Authorized(key, signed));
        await MakeDueAsync(id);
        await RunSriAsync(host, gym);

        var invoice = await GetAsync(owner, id);
        Assert.Equal("Authorized", Text(invoice, "status"));
        return new Authorized(gym, id, Text(invoice, "number"), Guid.Parse(await ScalarAsync("SELECT id::text FROM invoicing.invoice_lines WHERE invoice_id = @i", ("i", id)) ?? string.Empty));
    }

    private Task<string?> MakeDueAsync(Guid id) =>
        ScalarAsync("UPDATE invoicing.invoice_transmissions SET next_attempt_at = now() - interval '1 second', locked_until = NULL WHERE invoice_id = @i", ("i", id));

    private static async Task<(HttpStatusCode Status, JsonElement Body)> IssueNoteAsync(HttpClient client, object request)
    {
        var response = await client.PostAsJsonAsync("/api/v1/invoicing/credit-notes", request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private static string Code(JsonElement problem) => problem.GetProperty("code").GetString()!;

    // ---- emisión y reglas ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_full_credit_note_copies_the_invoice_with_its_own_numbering_and_links_both_ways()
    {
        var gym = await NewGymAsync();
        var fake = new FakeSriGateway();
        using var host = Host(fake, new RecordingEmailSender());
        var invoice = await AuthorizedInvoiceAsync(gym, host, fake);
        var owner = gym.Tenant.Owner;
        var original = await GetAsync(owner, invoice.Id);

        var (status, note) = await IssueNoteAsync(owner, new { invoiceId = invoice.Id, reason = "Devolución de la membresía" });

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal("CreditNote", Text(note, "documentType"));
        Assert.Equal("PendingSignature", Text(note, "status"));
        Assert.Equal(invoice.Number[..8] + "000000001", Text(note, "number"));
        var info = note.GetProperty("creditNote");
        Assert.Equal(invoice.Id, info.GetProperty("modifiedInvoiceId").GetGuid());
        Assert.Equal(invoice.Number, Text(info, "modifiedNumber"));
        Assert.Equal("Devolución de la membresía", Text(info, "reason"));
        Assert.Equal("04", Text(note, "accessKey").Substring(8, 2));
        var totals = note.GetProperty("calculation");
        Assert.Equal(original.GetProperty("calculation").GetProperty("total").GetDecimal(), totals.GetProperty("total").GetDecimal());
        Assert.Equal(original.GetProperty("calculation").GetProperty("totalTax").GetDecimal(), totals.GetProperty("totalTax").GetDecimal());
        Assert.Equal(JsonValueKind.Array, note.GetProperty("calculation").GetProperty("payments").ValueKind);
        Assert.Equal(0, note.GetProperty("calculation").GetProperty("payments").GetArrayLength());

        // La factura lo sabe: la nota aparece, lo acreditado sube y queda constancia en su historial.
        var after = await GetAsync(owner, invoice.Id);
        Assert.Equal(Text(note, "number"), Text(after.GetProperty("creditNotes")[0], "number"));
        Assert.Equal(totals.GetProperty("total").GetDecimal(), after.GetProperty("creditedTotal").GetDecimal());
        Assert.Contains("CreditNoteIssued", after.GetProperty("events").EnumerateArray().Select(e => Text(e, "type")));
        Assert.Equal("Authorized", Text(after, "status"));
    }

    [Fact]
    public async Task The_credit_note_goes_through_signing_the_SRI_authorization_and_the_email_like_an_invoice()
    {
        var gym = await NewGymAsync();
        var fake = new FakeSriGateway();
        var mail = new RecordingEmailSender();
        using var host = Host(fake, mail);
        var invoice = await AuthorizedInvoiceAsync(gym, host, fake);
        await RunMailAsync(host, gym);
        mail.Sent.Clear();
        var owner = gym.Tenant.Owner;

        var (_, note) = await IssueNoteAsync(owner, new { invoiceId = invoice.Id, reason = "Devolución de la membresía", submit = true });
        var noteId = note.GetProperty("id").GetGuid();
        var noteKey = Text(note, "accessKey");
        fake.ThenReceive(FakeSriGateway.Received());
        await RunSriAsync(host, gym);

        // Lo que se envió al SRI es la nota firmada, con el esquema de la nota de crédito.
        var sent = fake.Sends.Last().Xml;
        var doc = XDocument.Parse(sent);
        Assert.Equal("notaCredito", doc.Root!.Name.LocalName);
        Assert.Equal("04", doc.Root.Element("infoTributaria")!.Element("codDoc")!.Value);
        Assert.Equal(invoice.Number, doc.Root.Element("infoNotaCredito")!.Element("numDocModificado")!.Value);
        Assert.NotNull(doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Signature"));
        Assert.Empty(FitCore.Invoicing.Xml.InvoiceXmlValidator.Validate(sent, InvoiceDocumentType.CreditNote));

        fake.ThenAuthorize(FakeSriGateway.Authorized(noteKey, sent));
        await MakeDueAsync(noteId);
        await RunSriAsync(host, gym);
        Assert.Equal("Authorized", Text(await GetAsync(owner, noteId), "status"));

        // Y el comprador recibe el RIDE y el XML de la nota.
        Assert.Equal(1, await RunMailAsync(host, gym));
        var message = Assert.Single(mail.Sent);
        Assert.Contains("Nota de crédito", message.Subject, StringComparison.Ordinal);
        Assert.Contains(Text(note, "number"), message.Subject, StringComparison.Ordinal);
        Assert.Contains(invoice.Number, message.TextBody, StringComparison.Ordinal);
        var pdf = message.Attachments!.Single(a => a.ContentType == "application/pdf");
        Assert.Equal($"NotaCredito-{Text(note, "number")}.pdf", pdf.FileName);
        Assert.Equal("%PDF-", Encoding.ASCII.GetString(pdf.Content, 0, 5));

        // El RIDE descargable también es el de la nota.
        var ride = await owner.GetAsync($"/api/v1/invoicing/invoices/{noteId}/ride");
        Assert.Equal(HttpStatusCode.OK, ride.StatusCode);
        Assert.Equal($"NotaCredito-{Text(note, "number")}.pdf", ride.Content.Headers.ContentDisposition!.FileName!.Trim('"'));
    }

    [Fact]
    public async Task Partial_credit_notes_add_up_to_the_invoice_and_never_beyond()
    {
        var gym = await NewGymAsync();
        var fake = new FakeSriGateway();
        using var host = Host(fake, new RecordingEmailSender());
        var invoice = await AuthorizedInvoiceAsync(gym, host, fake);
        var owner = gym.Tenant.Owner;

        var preview = await owner.GetFromJsonAsync<JsonElement>($"/api/v1/invoicing/invoices/{invoice.Id}/credit-preview");
        Assert.True(preview.GetProperty("canIssue").GetBoolean());
        Assert.Equal(30m, preview.GetProperty("remainingTotal").GetDecimal());
        Assert.Equal(30m, preview.GetProperty("lines")[0].GetProperty("remainingAmount").GetDecimal());

        var first = await IssueNoteAsync(owner, new { invoiceId = invoice.Id, reason = "Descuento posterior", lines = new[] { new { lineId = invoice.LineId, amount = 11.50m } } });
        Assert.Equal(HttpStatusCode.Created, first.Status);
        Assert.Equal(11.50m, first.Body.GetProperty("calculation").GetProperty("total").GetDecimal());

        var middle = await owner.GetFromJsonAsync<JsonElement>($"/api/v1/invoicing/invoices/{invoice.Id}/credit-preview");
        Assert.Equal(18.50m, middle.GetProperty("remainingTotal").GetDecimal());
        Assert.Equal(11.50m, middle.GetProperty("creditedTotal").GetDecimal());

        // Más de lo que queda: se rechaza.
        var tooMuch = await IssueNoteAsync(owner, new { invoiceId = invoice.Id, reason = "Demasiado", lines = new[] { new { lineId = invoice.LineId, amount = 18.51m } } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooMuch.Status);
        Assert.Equal("invoicing.credit_amount_exceeds", Code(tooMuch.Body));

        // Todo lo que queda: la segunda nota cierra exactamente la factura.
        var second = await IssueNoteAsync(owner, new { invoiceId = invoice.Id, reason = "Resto de la devolución" });
        Assert.Equal(HttpStatusCode.Created, second.Status);
        Assert.Equal(18.50m, second.Body.GetProperty("calculation").GetProperty("total").GetDecimal());
        Assert.Equal("000000002", Text(second.Body, "number")[8..]);
        var closed = await GetAsync(owner, invoice.Id);
        Assert.Equal(30m, closed.GetProperty("creditedTotal").GetDecimal());

        var third = await IssueNoteAsync(owner, new { invoiceId = invoice.Id, reason = "Otra vez" });
        Assert.Equal(HttpStatusCode.Conflict, third.Status);
        Assert.Equal("invoicing.invoice_fully_credited", Code(third.Body));
        var preview2 = await owner.GetFromJsonAsync<JsonElement>($"/api/v1/invoicing/invoices/{invoice.Id}/credit-preview");
        Assert.False(preview2.GetProperty("canIssue").GetBoolean());
    }

    [Fact]
    public async Task Only_an_authorized_invoice_can_be_credited()
    {
        var gym = await NewGymAsync();
        var owner = gym.Tenant.Owner;
        var (_, obligation) = await PaidSaleAsync(owner, gym.Branch, 30m);
        var issued = await owner.PostAsJsonAsync("/api/v1/invoicing/invoices", new { sourceType = "obligation", sourceId = obligation });
        var id = (await issued.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var (status, problem) = await IssueNoteAsync(owner, new { invoiceId = id, reason = "Aún no autorizada" });

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("invoicing.credit_note_requires_authorized", Code(problem));
        var preview = await owner.GetFromJsonAsync<JsonElement>($"/api/v1/invoicing/invoices/{id}/credit-preview");
        Assert.False(preview.GetProperty("canIssue").GetBoolean());
        Assert.Equal("0", await ScalarAsync("SELECT count(*)::text FROM invoicing.invoices WHERE modified_invoice_id = @i", ("i", id)));
    }

    [Fact]
    public async Task A_credit_note_for_a_final_consumer_invoice_must_identify_the_receiver()
    {
        var gym = await NewGymAsync();
        var fake = new FakeSriGateway();
        using var host = Host(fake, new RecordingEmailSender());
        var invoice = await AuthorizedInvoiceAsync(gym, host, fake, amount: 20m, buyerIdType: "FinalConsumer");
        var owner = gym.Tenant.Owner;

        var missing = await IssueNoteAsync(owner, new { invoiceId = invoice.Id, reason = "Devolución" });
        Assert.Equal("invoicing.credit_note_buyer_required", Code(missing.Body));

        var stillFinal = await IssueNoteAsync(owner, new { invoiceId = invoice.Id, reason = "Devolución", buyer = new { idType = "FinalConsumer", idNumber = "9999999999999", legalName = "CONSUMIDOR FINAL" } });
        Assert.Equal("invoicing.credit_note_buyer_required", Code(stillFinal.Body));

        var invalid = await IssueNoteAsync(owner, new { invoiceId = invoice.Id, reason = "Devolución", buyer = new { idType = "Cedula", idNumber = "1712345670", legalName = "Luis Mora" } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.Status);
        Assert.Equal("invoicing.credit_note_buyer_invalid", Code(invalid.Body));
        Assert.Equal("0", await ScalarAsync("SELECT count(*)::text FROM invoicing.invoices WHERE modified_invoice_id = @i", ("i", invoice.Id)));

        var ok = await IssueNoteAsync(owner, new { invoiceId = invoice.Id, reason = "Devolución", buyer = new { idType = "Cedula", idNumber = "1712345675", legalName = "Luis Mora", email = "luis@correo.test" } });
        Assert.Equal(HttpStatusCode.Created, ok.Status);
        Assert.Equal("Cedula", ok.Body.GetProperty("buyer").GetProperty("idType").GetString());
        Assert.Equal("Luis Mora", ok.Body.GetProperty("buyer").GetProperty("legalName").GetString());
    }

    [Fact]
    public async Task A_buyer_cannot_be_changed_when_the_invoice_already_identified_one()
    {
        var gym = await NewGymAsync();
        var fake = new FakeSriGateway();
        using var host = Host(fake, new RecordingEmailSender());
        var invoice = await AuthorizedInvoiceAsync(gym, host, fake);

        var (status, problem) = await IssueNoteAsync(gym.Tenant.Owner, new { invoiceId = invoice.Id, reason = "Devolución", buyer = new { idType = "Cedula", idNumber = "1712345675", legalName = "Otro" } });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal("invoicing.credit_note_buyer_not_allowed", Code(problem));
    }

    [Fact]
    public async Task Simultaneous_requests_cannot_credit_the_same_invoice_twice()
    {
        var gym = await NewGymAsync();
        var fake = new FakeSriGateway();
        using var host = Host(fake, new RecordingEmailSender());
        var invoice = await AuthorizedInvoiceAsync(gym, host, fake);
        var owner = gym.Tenant.Owner;

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => IssueNoteAsync(owner, new { invoiceId = invoice.Id, reason = $"Solicitud {i}" })));

        Assert.Equal(1, results.Count(r => r.Status == HttpStatusCode.Created));
        Assert.All(results.Where(r => r.Status != HttpStatusCode.Created), r => Assert.Equal(HttpStatusCode.Conflict, r.Status));
        Assert.Equal("1", await ScalarAsync("SELECT count(*)::text FROM invoicing.invoices WHERE modified_invoice_id = @i", ("i", invoice.Id)));
        Assert.Equal("1", await ScalarAsync("SELECT max(sequential)::text FROM invoicing.invoices WHERE modified_invoice_id = @i", ("i", invoice.Id)));
    }

    [Fact]
    public async Task A_discarded_credit_note_frees_what_it_reserved_but_a_returned_one_keeps_it_reserved()
    {
        var gym = await NewGymAsync();
        var fake = new FakeSriGateway();
        using var host = Host(fake, new RecordingEmailSender());
        var invoice = await AuthorizedInvoiceAsync(gym, host, fake);
        var owner = gym.Tenant.Owner;

        var first = await IssueNoteAsync(owner, new { invoiceId = invoice.Id, reason = "Primera", submit = true });
        var firstId = first.Body.GetProperty("id").GetGuid();
        fake.ThenReceive(FakeSriGateway.Returned("35", "ARCHIVO NO CUMPLE ESTRUCTURA XML"));
        await RunSriAsync(host, gym);
        Assert.Equal("Rejected", Text(await GetAsync(owner, firstId), "status"));

        // Devuelta: puede reenviarse, así que sigue reservando el importe.
        var blocked = await IssueNoteAsync(owner, new { invoiceId = invoice.Id, reason = "Segunda" });
        Assert.Equal(HttpStatusCode.Conflict, blocked.Status);

        // Descartada: libera el importe y se puede emitir otra, con otro número.
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync($"/api/v1/invoicing/invoices/{firstId}/discard", new { reason = "Se corrige y se emite de nuevo" })).StatusCode);
        var again = await IssueNoteAsync(owner, new { invoiceId = invoice.Id, reason = "Segunda" });
        Assert.Equal(HttpStatusCode.Created, again.Status);
        Assert.NotEqual(Text(first.Body, "number"), Text(again.Body, "number"));
        var after = await GetAsync(owner, invoice.Id);
        Assert.Equal(30m, after.GetProperty("creditedTotal").GetDecimal());
    }

    [Fact]
    public async Task A_not_authorized_credit_note_frees_what_it_reserved()
    {
        var gym = await NewGymAsync();
        var fake = new FakeSriGateway();
        using var host = Host(fake, new RecordingEmailSender());
        var invoice = await AuthorizedInvoiceAsync(gym, host, fake);
        var owner = gym.Tenant.Owner;
        var (_, note) = await IssueNoteAsync(owner, new { invoiceId = invoice.Id, reason = "Primera", submit = true });
        var noteId = note.GetProperty("id").GetGuid();
        fake.ThenReceive(FakeSriGateway.Received());
        await RunSriAsync(host, gym);
        fake.ThenAuthorize(FakeSriGateway.NotAuthorized(Text(note, "accessKey")));
        await MakeDueAsync(noteId);
        await RunSriAsync(host, gym);
        Assert.Equal("NotAuthorized", Text(await GetAsync(owner, noteId), "status"));

        var again = await IssueNoteAsync(owner, new { invoiceId = invoice.Id, reason = "Segunda" });

        Assert.Equal(HttpStatusCode.Created, again.Status);
    }

    [Fact]
    public async Task Validation_permissions_and_isolation_between_gyms()
    {
        var gym = await NewGymAsync();
        var fake = new FakeSriGateway();
        using var host = Host(fake, new RecordingEmailSender());
        var invoice = await AuthorizedInvoiceAsync(gym, host, fake);
        var owner = gym.Tenant.Owner;
        var cashier = await StaffAsync(gym.Tenant, "Cajero", gym.Branch);
        var other = await NewGymAsync();
        using var anonymous = api.CreateClient();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await IssueNoteAsync(owner, new { invoiceId = invoice.Id, reason = "ab" })).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await IssueNoteAsync(owner, new { invoiceId = invoice.Id, reason = new string('x', 301) })).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await IssueNoteAsync(owner, new { invoiceId = invoice.Id, reason = "Importe cero", lines = new[] { new { lineId = invoice.LineId, amount = 0m } } })).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await IssueNoteAsync(owner, new { invoiceId = invoice.Id, reason = "Línea ajena", lines = new[] { new { lineId = Guid.NewGuid(), amount = 5m } } })).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await IssueNoteAsync(anonymous, new { invoiceId = invoice.Id, reason = "Sin sesión" })).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await IssueNoteAsync(cashier, new { invoiceId = invoice.Id, reason = "Sin permiso" })).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync($"/api/v1/invoicing/invoices/{invoice.Id}/credit-preview")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await IssueNoteAsync(other.Tenant.Owner, new { invoiceId = invoice.Id, reason = "Otro gimnasio" })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Tenant.Owner.GetAsync($"/api/v1/invoicing/invoices/{invoice.Id}/credit-preview")).StatusCode);
        Assert.Equal("0", await ScalarAsync("SELECT count(*)::text FROM invoicing.invoices WHERE modified_invoice_id = @i", ("i", invoice.Id)));
    }

    [Fact]
    public async Task The_list_filters_by_document_type_and_the_credit_note_shows_the_invoice_it_modifies()
    {
        var gym = await NewGymAsync();
        var fake = new FakeSriGateway();
        using var host = Host(fake, new RecordingEmailSender());
        var invoice = await AuthorizedInvoiceAsync(gym, host, fake);
        var owner = gym.Tenant.Owner;
        await IssueNoteAsync(owner, new { invoiceId = invoice.Id, reason = "Devolución" });

        var notes = await owner.GetFromJsonAsync<JsonElement>("/api/v1/invoicing/invoices?documentType=CreditNote");
        var invoices = await owner.GetFromJsonAsync<JsonElement>("/api/v1/invoicing/invoices?documentType=Invoice");
        var all = await owner.GetFromJsonAsync<JsonElement>("/api/v1/invoicing/invoices");

        Assert.Equal(1, notes.GetProperty("totalCount").GetInt32());
        Assert.Equal(invoice.Number, Text(notes.GetProperty("items")[0], "modifiedNumber"));
        Assert.Equal(1, invoices.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, all.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task Credit_notes_have_their_own_numbering_that_can_be_moved_forward_without_touching_the_invoices()
    {
        var gym = await NewGymAsync();
        var fake = new FakeSriGateway();
        using var host = Host(fake, new RecordingEmailSender());
        var invoice = await AuthorizedInvoiceAsync(gym, host, fake);
        var owner = gym.Tenant.Owner;
        var settings = await owner.GetFromJsonAsync<JsonElement>("/api/v1/invoicing/settings");
        var point = settings.GetProperty("emissionPoints")[0];
        var pointId = point.GetProperty("id").GetGuid();
        Assert.Equal(1, point.GetProperty("lastSequential").GetInt32());
        Assert.Equal(0, point.GetProperty("lastCreditNoteSequential").GetInt32());

        // El SRI ya tiene notas de crédito hasta la 500 de ese punto: se indica y la siguiente es la 501.
        var set = await owner.PutAsJsonAsync($"/api/v1/invoicing/emission-points/{pointId}/sequence", new { lastIssued = 500, documentType = "CreditNote" });
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        var after = (await set.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("emissionPoints")[0];
        Assert.Equal(1, after.GetProperty("lastSequential").GetInt32());
        Assert.Equal(500, after.GetProperty("lastCreditNoteSequential").GetInt32());

        var (_, note) = await IssueNoteAsync(owner, new { invoiceId = invoice.Id, reason = "Devolución" });
        Assert.EndsWith("000000501", Text(note, "number"), StringComparison.Ordinal);

        // Solo sube, y sin indicar el tipo sigue siendo el de la factura.
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PutAsJsonAsync($"/api/v1/invoicing/emission-points/{pointId}/sequence", new { lastIssued = 100, documentType = "CreditNote" })).StatusCode);
        var invoiceSet = await owner.PutAsJsonAsync($"/api/v1/invoicing/emission-points/{pointId}/sequence", new { lastIssued = 40 });
        Assert.Equal(40, (await invoiceSet.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("emissionPoints")[0].GetProperty("lastSequential").GetInt32());
    }

    // ---- la base de datos también lo impide -----------------------------------------------------------------------------------------

    [Fact]
    public async Task The_database_refuses_a_credit_note_on_an_unauthorized_invoice_and_one_that_credits_more_than_the_line()
    {
        var gym = await NewGymAsync();
        var fake = new FakeSriGateway();
        using var host = Host(fake, new RecordingEmailSender());
        var owner = gym.Tenant.Owner;

        // Sin autorizar.
        var (_, obligation) = await PaidSaleAsync(owner, gym.Branch, 30m);
        var pending = (await (await owner.PostAsJsonAsync("/api/v1/invoicing/invoices", new { sourceType = "obligation", sourceId = obligation })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal("FC132", await SqlStateAsync(CopyAsNote("@i", 900001), ("i", pending)));

        // Autorizada: una nota directa en la base que acredita la línea completa y otra que intenta acreditarla otra vez.
        var invoice = await AuthorizedInvoiceAsync(gym, host, fake);
        Assert.Null(await SqlStateAsync(CopyAsNote("@i", 900002), ("i", invoice.Id)));
        Assert.Null(await SqlStateAsync(CopyAsNoteLine(900002, invoice.LineId), ("i", invoice.Id)));
        Assert.Null(await SqlStateAsync(CopyAsNote("@i", 900003), ("i", invoice.Id)));
        Assert.Equal("FC132", await SqlStateAsync(CopyAsNoteLine(900003, invoice.LineId), ("i", invoice.Id)));
    }

    /// <summary>Copia la factura como una nota de crédito (otra clave y otro secuencial) saltándose el caso de uso, para probar las reglas de la base.</summary>
    private static string CopyAsNote(string invoiceParameter, int sequential) => $"""
        INSERT INTO invoicing.invoices
        SELECT (jsonb_populate_record(NULL::invoicing.invoices, to_jsonb(i) || jsonb_build_object(
            'id', gen_random_uuid(), 'document_type', 'CreditNote', 'status', 'PendingSignature', 'sequential', {sequential},
            'access_key', to_char(i.issue_date, 'DDMMYYYY') || '04' || i.issuer_ruc || CASE i.environment WHEN 'Production' THEN '2' ELSE '1' END || i.establishment_code || i.point_code || lpad({sequential}::text, 9, '0') || i.numeric_code || '1' || '0', 'modified_invoice_id', i.id, 'modified_document_number', '001-001-000000001',
            'modified_issue_date', i.issue_date, 'modification_reason', 'directo', 'source_type', 'invoice', 'source_id', i.id,
            'authorization_number', NULL, 'authorized_at', NULL))).*
        FROM invoicing.invoices i WHERE i.id = {invoiceParameter}
        """;

    /// <summary>Agrega otra línea que acredita la misma línea de la factura a la nota directa de la prueba anterior.</summary>
    private static string CopyAsNoteLine(int sequential, Guid originalLine) => $"""
        INSERT INTO invoicing.invoice_lines
        SELECT (jsonb_populate_record(NULL::invoicing.invoice_lines, to_jsonb(l) || jsonb_build_object(
            'id', gen_random_uuid(), 'invoice_id', n.id, 'modified_line_id', l.id, 'line_number', 1))).*
        FROM invoicing.invoice_lines l, invoicing.invoices n
        WHERE l.id = '{originalLine}' AND n.modified_invoice_id = @i AND n.sequential = {sequential}
        """;

    private async Task<HttpClient> StaffAsync(TestTenant tenant, string role, params Guid[] branches)
    {
        var email = $"{role.ToLowerInvariant()}-{Guid.NewGuid():N}"[..24] + "@cmp.test";
        await TestApi.CreateUserAsync(tenant.Owner, email, role, branches);
        return TestApi.Authenticated(api, await TestApi.LoginAsync(api, tenant.Slug, email, TestApi.UserPassword));
    }
}
