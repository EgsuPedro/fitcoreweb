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
/// «Descartar y volver a emitir»: un comprobante devuelto, no autorizado o sin enviar se reemplaza por uno nuevo (otra clave y otro secuencial) sin borrar nada, con motivo y constancia; uno en trámite
/// o autorizado no se puede descartar. También el resumen del panel.
/// </summary>
[Collection(ApiCollection2.Name)]
public class InvoicingDiscardTests(ApiFixture api)
{
    private sealed record Gym(TestTenant Tenant, Guid Branch);

    private async Task<Gym> NewGymAsync()
    {
        var tenant = await TestApi.NewTenantAsync(api, $"dsc-{Guid.NewGuid():N}"[..20]);
        var gym = new Gym(tenant, await TestApi.CreateBranchAsync(tenant.Owner, "UIO"));
        await ConfigureIssuerAsync(tenant.Owner);
        return gym;
    }

    private WebApplicationFactory<Program> Host(FakeSriGateway fake) =>
        api.CreateHostWithServices(services =>
        {
            services.RemoveAll<ISriGateway>();
            services.AddSingleton<ISriGateway>(fake);
        });

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

    private static async Task<(Guid Id, JsonElement Body)> IssueAsync(HttpClient client, Guid obligation, bool submit, HttpStatusCode expected = HttpStatusCode.Created)
    {
        var response = await client.PostAsJsonAsync("/api/v1/invoicing/invoices", new { sourceType = "obligation", sourceId = obligation, submit });
        Assert.Equal(expected, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("id").GetGuid(), body);
    }

    private static string Text(JsonElement e, string name) => e.GetProperty(name).GetString()!;

    private Task<HttpResponseMessage> DiscardAsync(HttpClient client, Guid id, string reason) =>
        client.PostAsJsonAsync($"/api/v1/invoicing/invoices/{id}/discard", new { reason });

    private async Task<(Gym Gym, Guid Obligation, Guid Id)> RejectedInvoiceAsync()
    {
        var gym = await NewGymAsync();
        var (_, obligation) = await PaidSaleAsync(gym.Tenant.Owner, gym.Branch, 30m);
        var (id, _) = await IssueAsync(gym.Tenant.Owner, obligation, submit: true);
        var fake = new FakeSriGateway().ThenReceive(FakeSriGateway.Returned("45", "ERROR SECUENCIAL REGISTRADO"));
        using var host = Host(fake);
        await host.Services.GetRequiredService<InvoiceTransmissionRunner>().RunForTenantAsync(gym.Tenant.Id, CancellationToken.None);
        Assert.Equal("Rejected", await ScalarAsync("SELECT status FROM invoicing.invoices WHERE id = @i", ("i", id)));
        return (gym, obligation, id);
    }

    [Fact]
    public async Task A_rejected_invoice_can_be_discarded_and_the_sale_invoiced_again_with_a_new_number()
    {
        var (gym, obligation, id) = await RejectedInvoiceAsync();
        var owner = gym.Tenant.Owner;
        var first = await owner.GetFromJsonAsync<JsonElement>($"/api/v1/invoicing/invoices/{id}");

        var response = await DiscardAsync(owner, id, "El secuencial ya existía en el SRI");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var discarded = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Discarded", Text(discarded, "status"));
        var last = discarded.GetProperty("events").EnumerateArray().Last();
        Assert.Equal("Discarded", Text(last, "type"));
        Assert.Equal("El secuencial ya existía en el SRI", Text(last, "detail"));
        Assert.NotEqual(Guid.Empty, last.GetProperty("actorId").GetGuid());

        // La venta se puede facturar otra vez: comprobante nuevo, otra clave y otro secuencial; el descartado sigue ahí.
        var (newId, again) = await IssueAsync(owner, obligation, submit: false);
        Assert.NotEqual(id, newId);
        Assert.NotEqual(Text(first, "accessKey"), Text(again, "accessKey"));
        Assert.NotEqual(Text(first, "number"), Text(again, "number"));
        Assert.Equal("PendingSignature", Text(again, "status"));
        Assert.Equal("2", await ScalarAsync("SELECT count(*)::text FROM invoicing.invoices WHERE source_id = @s", ("s", obligation)));
        Assert.Equal("Discarded", await ScalarAsync("SELECT status FROM invoicing.invoices WHERE id = @i", ("i", id)));

        // Y otra vez es idempotente sobre el comprobante vigente.
        var (sameId, _) = await IssueAsync(owner, obligation, submit: false, HttpStatusCode.OK);
        Assert.Equal(newId, sameId);
    }

    [Fact]
    public async Task A_discarded_invoice_cannot_be_sent_or_signed_again()
    {
        var (gym, _, id) = await RejectedInvoiceAsync();
        var owner = gym.Tenant.Owner;
        Assert.Equal(HttpStatusCode.OK, (await DiscardAsync(owner, id, "Se reemplaza por uno nuevo")).StatusCode);

        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsync($"/api/v1/invoicing/invoices/{id}/submit", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await DiscardAsync(owner, id, "Descartarlo otra vez no procede")).StatusCode);
        Assert.Equal("Discarded", await ScalarAsync("SELECT status FROM invoicing.invoices WHERE id = @i", ("i", id)));
        Assert.Equal("0", await ScalarAsync("SELECT count(*)::text FROM invoicing.invoice_transmissions WHERE invoice_id = @i", ("i", id)));
    }

    [Fact]
    public async Task An_invoice_that_was_never_sent_can_be_discarded_and_leaves_the_queue()
    {
        var gym = await NewGymAsync();
        var (_, obligation) = await PaidSaleAsync(gym.Tenant.Owner, gym.Branch, 30m);
        var (id, _) = await IssueAsync(gym.Tenant.Owner, obligation, submit: true);
        Assert.Equal("1", await ScalarAsync("SELECT count(*)::text FROM invoicing.invoice_transmissions WHERE invoice_id = @i", ("i", id)));

        var response = await DiscardAsync(gym.Tenant.Owner, id, "Se emitió con el punto equivocado");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("0", await ScalarAsync("SELECT count(*)::text FROM invoicing.invoice_transmissions WHERE invoice_id = @i", ("i", id)));
    }

    [Fact]
    public async Task An_invoice_in_process_with_the_SRI_or_authorized_cannot_be_discarded()
    {
        var gym = await NewGymAsync();
        var (_, obligation) = await PaidSaleAsync(gym.Tenant.Owner, gym.Branch, 30m);
        var (id, _) = await IssueAsync(gym.Tenant.Owner, obligation, submit: true);
        var fake = new FakeSriGateway().ThenReceive(FakeSriGateway.Received());
        using var host = Host(fake);
        await host.Services.GetRequiredService<InvoiceTransmissionRunner>().RunForTenantAsync(gym.Tenant.Id, CancellationToken.None);
        Assert.Equal("Received", await ScalarAsync("SELECT status FROM invoicing.invoices WHERE id = @i", ("i", id)));

        var response = await DiscardAsync(gym.Tenant.Owner, id, "Intento de descartar uno en trámite");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("Received", await ScalarAsync("SELECT status FROM invoicing.invoices WHERE id = @i", ("i", id)));
    }

    [Fact]
    public async Task The_database_itself_refuses_to_discard_an_authorized_invoice_or_to_bring_a_discarded_one_back()
    {
        var (gym, _, id) = await RejectedInvoiceAsync();
        Assert.Equal(HttpStatusCode.OK, (await DiscardAsync(gym.Tenant.Owner, id, "Reemplazo por error de numeración")).StatusCode);

        // Un descartado es terminal.
        Assert.Equal("FC131", await SqlStateAsync("UPDATE invoicing.invoices SET status = 'PendingSubmission' WHERE id = @i", ("i", id)));

        // Uno autorizado no pasa a descartado (aunque alguien con acceso directo lo intente).
        var other = await NewGymAsync();
        var (_, obligation) = await PaidSaleAsync(other.Tenant.Owner, other.Branch, 30m);
        var (okId, _) = await IssueAsync(other.Tenant.Owner, obligation, submit: true);
        var fake = new FakeSriGateway().ThenReceive(FakeSriGateway.Received());
        using var host = Host(fake);
        await host.Services.GetRequiredService<InvoiceTransmissionRunner>().RunForTenantAsync(other.Tenant.Id, CancellationToken.None);
        Assert.Equal("FC131", await SqlStateAsync("UPDATE invoicing.invoices SET status = 'Discarded' WHERE id = @i", ("i", okId)));
    }

    [Fact]
    public async Task Discarding_needs_a_reason_and_belongs_to_the_own_gym()
    {
        var (gym, _, id) = await RejectedInvoiceAsync();
        var owner = gym.Tenant.Owner;

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await DiscardAsync(owner, id, "corto")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await DiscardAsync(owner, id, "")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await DiscardAsync(owner, id, new string('x', 301))).StatusCode);
        Assert.Equal("Rejected", await ScalarAsync("SELECT status FROM invoicing.invoices WHERE id = @i", ("i", id)));

        var other = await NewGymAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await DiscardAsync(other.Tenant.Owner, id, "Intento desde otro gimnasio")).StatusCode);
    }

    [Fact]
    public async Task The_summary_counts_by_status_and_flags_what_needs_attention()
    {
        var (gym, _, id) = await RejectedInvoiceAsync();
        var owner = gym.Tenant.Owner;
        var (_, obligation) = await PaidSaleAsync(owner, gym.Branch, 25m);
        await IssueAsync(owner, obligation, submit: false);

        var summary = await owner.GetFromJsonAsync<JsonElement>("/api/v1/invoicing/invoices/summary");

        Assert.Equal(1, summary.GetProperty("countsByStatus").GetProperty("Rejected").GetInt32());
        Assert.Equal(1, summary.GetProperty("countsByStatus").GetProperty("PendingSignature").GetInt32());
        Assert.Equal(0, summary.GetProperty("countsByStatus").GetProperty("Authorized").GetInt32());
        Assert.Equal(1, summary.GetProperty("needsAttention").GetInt32());
        Assert.Equal(0, summary.GetProperty("inQueue").GetInt32());

        Assert.Equal(HttpStatusCode.OK, (await DiscardAsync(owner, id, "Reemplazo por error de numeración")).StatusCode);
        var after = await owner.GetFromJsonAsync<JsonElement>("/api/v1/invoicing/invoices/summary");
        Assert.Equal(1, after.GetProperty("countsByStatus").GetProperty("Discarded").GetInt32());
        Assert.Equal(0, after.GetProperty("needsAttention").GetInt32());

        // Otro gimnasio no ve estas cifras.
        var other = await NewGymAsync();
        var theirs = await other.Tenant.Owner.GetFromJsonAsync<JsonElement>("/api/v1/invoicing/invoices/summary");
        Assert.Equal(0, theirs.GetProperty("countsByStatus").GetProperty("Discarded").GetInt32());
    }
}
