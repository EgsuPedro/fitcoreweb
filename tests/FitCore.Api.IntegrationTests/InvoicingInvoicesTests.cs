using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FitCore.Api.IntegrationTests.Infrastructure;
using Npgsql;
using static FitCore.Api.IntegrationTests.Infrastructure.InvoicingTestApi;

namespace FitCore.Api.IntegrationTests;

/// <summary>
/// Motor del comprobante contra API y PostgreSQL reales: emisión con número y clave de acceso, secuenciales atómicos sin huecos, idempotencia por venta (también bajo concurrencia),
/// snapshot inmutable, selección del punto de emisión, permisos, alcance de sucursales y aislamiento entre gimnasios.
/// </summary>
[Collection(ApiCollection2.Name)]
public class InvoicingInvoicesTests(ApiFixture api)
{
    private static string NewSlug() => $"cmp-{Guid.NewGuid():N}"[..20];

    private sealed record Gym(TestTenant Tenant, Guid Branch);

    private async Task<Gym> NewGymAsync(bool configured = true)
    {
        var tenant = await TestApi.NewTenantAsync(api, NewSlug());
        var gym = new Gym(tenant, await TestApi.CreateBranchAsync(tenant.Owner, "UIO"));
        if (configured)
        {
            await ConfigureIssuerAsync(tenant.Owner);
        }

        return gym;
    }

    private async Task<HttpClient> StaffAsync(TestTenant tenant, string role, params Guid[] branches)
    {
        var email = $"{role.ToLowerInvariant()}-{Guid.NewGuid():N}"[..24] + "@cmp.test";
        await TestApi.CreateUserAsync(tenant.Owner, email, role, branches);
        return TestApi.Authenticated(api, await TestApi.LoginAsync(api, tenant.Slug, email, TestApi.UserPassword));
    }

    private async Task<string?> ScalarAsync(string sql, params (string Name, object Value)[] args)
    {
        await using var conn = new NpgsqlConnection(api.AdminConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in args)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        return (await cmd.ExecuteScalarAsync())?.ToString();
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> IssueAsync(HttpClient client, Guid obligationId, string? buyerIdType = null, Guid? emissionPointId = null)
    {
        var response = await client.PostAsJsonAsync("/api/v1/invoicing/invoices", new { sourceType = "obligation", sourceId = obligationId, buyerIdType, emissionPointId });
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, string.IsNullOrEmpty(text) ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private static string Text(JsonElement e, string name) => e.GetProperty(name).GetString()!;

    // ---- emisión ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Issuing_a_paid_sale_creates_a_numbered_invoice_with_a_valid_access_key_and_everything_frozen()
    {
        var gym = await NewGymAsync();
        var owner = gym.Tenant.Owner;
        var (_, obligation) = await PaidSaleAsync(owner, gym.Branch, 30m);

        var (status, invoice) = await IssueAsync(owner, obligation);

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal("001-001-000000001", Text(invoice, "number"));
        Assert.Equal("Invoice", Text(invoice, "documentType"));
        Assert.Equal("PendingSignature", Text(invoice, "status"));
        Assert.Equal("Test", Text(invoice, "environment"));
        var key = Text(invoice, "accessKey");
        Assert.Matches("^[0-9]{49}$", key);
        Assert.Equal(Ruc, key.Substring(10, 13));
        Assert.Equal("001001000000001", key.Substring(24, 15));
        Assert.Equal("01", key.Substring(8, 2));
        Assert.Equal(DateTime.UtcNow.AddHours(-5).ToString("ddMMyyyy"), key[..8]);

        var calculation = invoice.GetProperty("calculation");
        Assert.Equal(26.09m, calculation.GetProperty("subtotalWithoutTaxes").GetDecimal());
        Assert.Equal(3.91m, calculation.GetProperty("totalTax").GetDecimal());
        Assert.Equal(30m, calculation.GetProperty("total").GetDecimal());
        Assert.Equal(0m, calculation.GetProperty("roundingDifference").GetDecimal());
        var line = Assert.Single(calculation.GetProperty("lines").EnumerateArray());
        Assert.Equal("4", Text(line, "taxRateCode"));
        var tax = Assert.Single(calculation.GetProperty("taxes").EnumerateArray());
        Assert.Equal("2", Text(tax, "taxCode"));
        var payment = Assert.Single(calculation.GetProperty("payments").EnumerateArray());
        Assert.Equal("19", Text(payment, "methodCode")); // tarjeta de crédito por defecto
        Assert.Equal(30m, payment.GetProperty("amount").GetDecimal());

        Assert.Equal("Fitness Quito S.A.", Text(invoice.GetProperty("issuer"), "legalName"));
        Assert.Equal("05", Text(invoice.GetProperty("buyer"), "idTypeCode"));
        Assert.Equal("Ana Pérez", Text(invoice.GetProperty("buyer"), "legalName"));

        var events = invoice.GetProperty("events").EnumerateArray().ToList();
        var created = Assert.Single(events);
        Assert.Equal("Created", Text(created, "type"));
        Assert.Equal("PendingSignature", Text(created, "toStatus"));

        // Lo mismo se consulta por su id y aparece en el listado.
        var id = invoice.GetProperty("id").GetGuid();
        var read = await owner.GetFromJsonAsync<JsonElement>($"/api/v1/invoicing/invoices/{id}");
        Assert.Equal(key, Text(read, "accessKey"));
        var list = await owner.GetFromJsonAsync<JsonElement>("/api/v1/invoicing/invoices");
        Assert.Equal(1, list.GetProperty("totalCount").GetInt32());
        Assert.Equal("001-001-000000001", Text(list.GetProperty("items")[0], "number"));
    }

    [Fact]
    public async Task The_card_the_gym_usually_accepts_decides_the_SRI_payment_code()
    {
        var gym = await NewGymAsync();
        var owner = gym.Tenant.Owner;
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/v1/invoicing/issuer", IssuerBody(cardPaymentMethod: "Debit"))).StatusCode);
        var (_, card) = await PaidSaleAsync(owner, gym.Branch, 20m, "Card");
        var (_, transfer) = await PaidSaleAsync(owner, gym.Branch, 20m, "BankTransfer");

        var debit = (await IssueAsync(owner, card)).Body.GetProperty("calculation").GetProperty("payments")[0];
        // La transferencia queda pendiente de verificar: todavía no se puede facturar.
        var pending = await IssueAsync(owner, transfer);

        Assert.Equal("16", Text(debit, "methodCode"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, pending.Status);
        Assert.Equal("invoicing.cannot_issue", Text(pending.Body, "code"));
    }

    [Fact]
    public async Task A_sale_that_cannot_be_invoiced_creates_nothing_and_does_not_consume_a_number()
    {
        var gym = await NewGymAsync();
        var owner = gym.Tenant.Owner;
        var member = await NewMemberAsync(owner, gym.Branch);
        var unpaid = await NewObligationAsync(owner, member, 30m);

        var (status, body) = await IssueAsync(owner, unpaid);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal("invoicing.cannot_issue", Text(body, "code"));
        Assert.Contains("source_not_paid", body.ToString(), StringComparison.Ordinal);
        Assert.Equal("0", await ScalarAsync("SELECT count(*)::text FROM invoicing.invoices WHERE tenant_id = @t", ("t", gym.Tenant.Id)));
        Assert.Equal("0", await ScalarAsync("SELECT count(*)::text FROM invoicing.emission_sequences WHERE tenant_id = @t", ("t", gym.Tenant.Id)));

        // El siguiente comprobante real sigue siendo el número 1.
        var (_, paid) = await PaidSaleAsync(owner, gym.Branch, 15m);
        Assert.Equal("001-001-000000001", Text((await IssueAsync(owner, paid)).Body, "number"));
    }

    [Fact]
    public async Task Without_tax_configuration_nothing_is_issued()
    {
        var gym = await NewGymAsync(configured: false);
        var (_, obligation) = await PaidSaleAsync(gym.Tenant.Owner, gym.Branch, 30m);

        var (status, body) = await IssueAsync(gym.Tenant.Owner, obligation);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Contains("issuer_not_ready", body.ToString(), StringComparison.Ordinal);
    }

    // ---- idempotencia y secuenciales -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Invoicing_the_same_sale_twice_returns_the_same_invoice_and_does_not_burn_a_number()
    {
        var gym = await NewGymAsync();
        var owner = gym.Tenant.Owner;
        var (_, first) = await PaidSaleAsync(owner, gym.Branch, 30m);
        var (_, second) = await PaidSaleAsync(owner, gym.Branch, 40m);

        var created = await IssueAsync(owner, first);
        var again = await IssueAsync(owner, first);
        var other = await IssueAsync(owner, second);

        Assert.Equal(HttpStatusCode.Created, created.Status);
        Assert.Equal(HttpStatusCode.OK, again.Status);
        Assert.Equal(created.Body.GetProperty("id").GetGuid(), again.Body.GetProperty("id").GetGuid());
        Assert.Equal(Text(created.Body, "accessKey"), Text(again.Body, "accessKey"));
        Assert.Equal("001-001-000000002", Text(other.Body, "number"));
        Assert.Equal("2", await ScalarAsync("SELECT count(*)::text FROM invoicing.invoices WHERE tenant_id = @t", ("t", gym.Tenant.Id)));
    }

    [Fact]
    public async Task Parallel_requests_for_the_same_sale_produce_exactly_one_invoice_and_one_number()
    {
        var gym = await NewGymAsync();
        var owner = gym.Tenant.Owner;
        var (_, obligation) = await PaidSaleAsync(owner, gym.Branch, 30m);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => IssueAsync(owner, obligation)));

        Assert.Equal(1, results.Count(r => r.Status == HttpStatusCode.Created));
        Assert.Equal(7, results.Count(r => r.Status == HttpStatusCode.OK));
        Assert.Single(results.Select(r => r.Body.GetProperty("id").GetGuid()).Distinct());
        Assert.Equal("1", await ScalarAsync("SELECT count(*)::text FROM invoicing.invoices WHERE tenant_id = @t", ("t", gym.Tenant.Id)));
        // Ningún número se perdió en las solicitudes que perdieron la carrera.
        Assert.Equal("1", await ScalarAsync("SELECT last_value::text FROM invoicing.emission_sequences WHERE tenant_id = @t", ("t", gym.Tenant.Id)));
    }

    [Fact]
    public async Task Parallel_sales_get_consecutive_numbers_without_gaps_or_repeats()
    {
        var gym = await NewGymAsync();
        var owner = gym.Tenant.Owner;
        var sales = new List<Guid>();
        for (var i = 0; i < 10; i++)
        {
            sales.Add((await PaidSaleAsync(owner, gym.Branch, 10m + i)).ObligationId);
        }

        var results = await Task.WhenAll(sales.Select(s => IssueAsync(owner, s)));

        Assert.All(results, r => Assert.Equal(HttpStatusCode.Created, r.Status));
        var numbers = results.Select(r => Text(r.Body, "number")).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.Equal(Enumerable.Range(1, 10).Select(n => $"001-001-{n:D9}"), numbers);
        Assert.Equal(10, results.Select(r => Text(r.Body, "accessKey")).Distinct().Count());
    }

    [Fact]
    public async Task Each_emission_point_and_each_environment_has_its_own_numbering()
    {
        var gym = await NewGymAsync();
        var owner = gym.Tenant.Owner;
        var secondPoint = await AddEmissionPointAsync(owner, "001", "002");
        var firstPoint = Guid.Parse((await ScalarAsync("SELECT id::text FROM invoicing.emission_points WHERE tenant_id = @t AND point_code = '001'", ("t", gym.Tenant.Id)))!);

        var a = await IssueAsync(owner, (await PaidSaleAsync(owner, gym.Branch, 11m)).ObligationId, emissionPointId: firstPoint);
        var b = await IssueAsync(owner, (await PaidSaleAsync(owner, gym.Branch, 12m)).ObligationId, emissionPointId: secondPoint);
        var c = await IssueAsync(owner, (await PaidSaleAsync(owner, gym.Branch, 13m)).ObligationId, emissionPointId: secondPoint);

        Assert.Equal("001-001-000000001", Text(a.Body, "number"));
        Assert.Equal("001-002-000000001", Text(b.Body, "number"));
        Assert.Equal("001-002-000000002", Text(c.Body, "number"));

        // Al pasar a producción la numeración empieza de nuevo: los números de prueba no contaminan la serie real.
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/v1/invoicing/issuer", IssuerBody("Production", confirmProduction: true))).StatusCode);
        var real = await IssueAsync(owner, (await PaidSaleAsync(owner, gym.Branch, 14m)).ObligationId, emissionPointId: firstPoint);
        Assert.Equal("001-001-000000001", Text(real.Body, "number"));
        Assert.Equal("Production", Text(real.Body, "environment"));
        Assert.Equal("2", Text(real.Body, "accessKey").Substring(23, 1));
    }

    // ---- punto de emisión -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_emission_point_follows_the_branch_of_the_sale_and_never_uses_one_from_another_branch()
    {
        var gym = await NewGymAsync(configured: false);
        var owner = gym.Tenant.Owner;
        var guayaquil = await TestApi.CreateBranchAsync(owner, "GYE");
        await ConfigureIssuerAsync(owner, "001", "001", branchId: gym.Branch);
        var (_, quito) = await PaidSaleAsync(owner, gym.Branch, 20m);
        var (_, gye) = await PaidSaleAsync(owner, guayaquil, 20m);

        // Guayaquil no tiene punto propio ni uno general: no se factura con el de Quito.
        var blocked = await IssueAsync(owner, gye);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, blocked.Status);
        Assert.Contains("emission_point_missing", blocked.Body.ToString(), StringComparison.Ordinal);

        Assert.Equal("001-001-000000001", Text((await IssueAsync(owner, quito)).Body, "number"));

        // Con un punto propio de Guayaquil, esa venta lo usa.
        await AddEmissionPointAsync(owner, "001", "002", guayaquil);
        Assert.Equal("001-002-000000001", Text((await IssueAsync(owner, gye)).Body, "number"));
    }

    [Fact]
    public async Task A_requested_emission_point_must_exist_be_active_and_belong_to_the_branch_of_the_sale()
    {
        var gym = await NewGymAsync(configured: false);
        var owner = gym.Tenant.Owner;
        var guayaquil = await TestApi.CreateBranchAsync(owner, "GYE");
        await ConfigureIssuerAsync(owner, "001", "001", branchId: gym.Branch);
        var gyePoint = await AddEmissionPointAsync(owner, "001", "002", guayaquil);
        var (_, quito) = await PaidSaleAsync(owner, gym.Branch, 20m);

        var wrongBranch = await IssueAsync(owner, quito, emissionPointId: gyePoint);
        var unknown = await IssueAsync(owner, quito, emissionPointId: Guid.NewGuid());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, wrongBranch.Status);
        Assert.Contains("emission_point_invalid", wrongBranch.Body.ToString(), StringComparison.Ordinal);
        Assert.Contains("emission_point_invalid", unknown.Body.ToString(), StringComparison.Ordinal);
    }

    // ---- comprador ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_final_consumer_invoice_carries_the_fixed_identification_and_none_of_the_students_data()
    {
        var gym = await NewGymAsync();
        var owner = gym.Tenant.Owner;
        var (_, obligation) = await PaidSaleAsync(owner, gym.Branch, 30m);

        var (status, invoice) = await IssueAsync(owner, obligation, "FinalConsumer");

        Assert.Equal(HttpStatusCode.Created, status);
        var buyer = invoice.GetProperty("buyer");
        Assert.Equal("07", Text(buyer, "idTypeCode"));
        Assert.Equal("9999999999999", Text(buyer, "idNumber"));
        Assert.Equal("CONSUMIDOR FINAL", Text(buyer, "legalName"));
        Assert.Equal(JsonValueKind.Null, buyer.GetProperty("email").ValueKind);
        Assert.Equal(JsonValueKind.Null, buyer.GetProperty("address").ValueKind);
    }

    [Fact]
    public async Task A_big_sale_cannot_be_issued_to_final_consumer()
    {
        var gym = await NewGymAsync();
        var owner = gym.Tenant.Owner;
        var (_, obligation) = await PaidSaleAsync(owner, gym.Branch, 80m);

        var (status, body) = await IssueAsync(owner, obligation, "FinalConsumer");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Contains("final_consumer_over_limit", body.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_invoice_keeps_the_buyer_as_it_was_when_issued_even_if_the_student_changes()
    {
        var gym = await NewGymAsync();
        var owner = gym.Tenant.Owner;
        var (member, obligation) = await PaidSaleAsync(owner, gym.Branch, 30m);
        var issued = (await IssueAsync(owner, obligation)).Body;

        await ScalarAsync("UPDATE members.members SET first_name = 'Otro', last_name = 'Nombre', email = 'otro@correo.test', address = 'Otra calle' WHERE id = @m", ("m", member));
        var read = await owner.GetFromJsonAsync<JsonElement>($"/api/v1/invoicing/invoices/{issued.GetProperty("id").GetGuid()}");

        var buyer = read.GetProperty("buyer");
        Assert.Equal("Ana Pérez", Text(buyer, "legalName"));
        Assert.Equal("ana.perez@correo.test", Text(buyer, "email"));
        Assert.Equal("Av. Principal 123", Text(buyer, "address"));
    }

    // ---- integridad en la base ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_database_refuses_to_rewrite_or_delete_an_invoice_but_lets_its_status_change()
    {
        var gym = await NewGymAsync();
        var owner = gym.Tenant.Owner;
        var (_, obligation) = await PaidSaleAsync(owner, gym.Branch, 30m);
        var id = (await IssueAsync(owner, obligation)).Body.GetProperty("id").GetGuid();

        async Task<string?> Try(string sql)
        {
            try
            {
                await ScalarAsync(sql, ("i", id));
                return null;
            }
            catch (PostgresException ex)
            {
                return ex.SqlState;
            }
        }

        Assert.Equal("FC130", await Try("UPDATE invoicing.invoices SET total = 31, subtotal_without_taxes = 27.09 WHERE id = @i"));
        Assert.Equal("FC130", await Try("UPDATE invoicing.invoices SET buyer_name = 'Otra persona' WHERE id = @i"));
        Assert.Equal("FC130", await Try("DELETE FROM invoicing.invoices WHERE id = @i"));
        Assert.Equal("FC130", await Try("UPDATE invoicing.invoice_lines SET subtotal = 1 WHERE invoice_id = @i"));
        Assert.Equal("FC130", await Try("DELETE FROM invoicing.invoice_taxes WHERE invoice_id = @i"));
        Assert.Equal("FC130", await Try("DELETE FROM invoicing.invoice_payments WHERE invoice_id = @i"));
        Assert.Equal("FC130", await Try("DELETE FROM invoicing.invoice_events WHERE invoice_id = @i"));
        // El estado no puede saltar a «firmado» sin su XML firmado (ver InvoicingSigningTests); un cambio de estado válido sí se permite.
        Assert.Equal("FC131", await Try("UPDATE invoicing.invoices SET status = 'SignedDocument', updated_at = now() WHERE id = @i"));
    }

    [Fact]
    public async Task The_database_rejects_an_access_key_that_does_not_match_the_invoice_and_inconsistent_totals()
    {
        var gym = await NewGymAsync();
        var owner = gym.Tenant.Owner;
        var (_, obligation) = await PaidSaleAsync(owner, gym.Branch, 30m);
        var id = (await IssueAsync(owner, obligation)).Body.GetProperty("id").GetGuid();

        // Copia el comprobante cambiando una sola cosa; la base debe rechazar lo que no cuadra. Las restricciones se evalúan antes que los índices únicos.
        async Task<string?> InsertCopyAsync(string sequential, string total)
        {
            try
            {
                await ScalarAsync($"""
                    INSERT INTO invoicing.invoices (id, tenant_id, document_type, status, source_type, source_id, source_reference, branch_id, emission_point_id, establishment_code, point_code, sequential,
                        environment, issue_date, access_key, numeric_code, issuer_ruc, issuer_legal_name, issuer_head_office_address, issuer_establishment_address, tax_regime, accounting_required,
                        buyer_id_type, buyer_id_number, buyer_name, currency, subtotal_without_taxes, total_discount, total_tax, total, rounding_difference, created_at)
                    SELECT gen_random_uuid(), tenant_id, document_type, status, source_type, gen_random_uuid(), source_reference, branch_id, emission_point_id, establishment_code, point_code, {sequential},
                        environment, issue_date, access_key, numeric_code, issuer_ruc, issuer_legal_name, issuer_head_office_address, issuer_establishment_address, tax_regime, accounting_required,
                        buyer_id_type, buyer_id_number, buyer_name, currency, subtotal_without_taxes, total_discount, total_tax, {total}, rounding_difference, created_at
                    FROM invoicing.invoices WHERE id = @i
                    """, ("i", id));
                return null;
            }
            catch (PostgresException ex)
            {
                return ex.ConstraintName ?? ex.SqlState;
            }
        }

        // El número 99 no coincide con el que dice la clave de acceso (000000001).
        Assert.Equal("ck_invoices_access_key", await InsertCopyAsync("99", "total"));
        // total ≠ subtotal + IVA.
        Assert.Equal("ck_invoices_amounts", await InsertCopyAsync("sequential", "total + 1"));
        // Sin cambios la restricción de unicidad de la clave protege contra duplicados (lo único que falla es el índice).
        Assert.Equal("ix_invoices_access_key", await InsertCopyAsync("sequential", "total"));
    }

    // ---- permisos, alcance y aislamiento ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Only_staff_who_can_issue_can_issue_and_only_within_their_branches()
    {
        var gym = await NewGymAsync();
        var owner = gym.Tenant.Owner;
        var otherBranch = await TestApi.CreateBranchAsync(owner, "GYE");
        var (_, obligation) = await PaidSaleAsync(owner, gym.Branch, 30m);

        var trainer = await StaffAsync(gym.Tenant, "Entrenador", gym.Branch);
        var reception = await StaffAsync(gym.Tenant, "Recepcion", gym.Branch);
        var elsewhere = await StaffAsync(gym.Tenant, "Cajero", otherBranch);
        var cashier = await StaffAsync(gym.Tenant, "Cajero", gym.Branch);
        using var anonymous = api.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await IssueAsync(anonymous, obligation)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await IssueAsync(trainer, obligation)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await IssueAsync(reception, obligation)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await IssueAsync(elsewhere, obligation)).Status);
        var issued = await IssueAsync(cashier, obligation);
        Assert.Equal(HttpStatusCode.Created, issued.Status);
        var id = issued.Body.GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.OK, (await cashier.GetAsync($"/api/v1/invoicing/invoices/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await elsewhere.GetAsync($"/api/v1/invoicing/invoices/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await trainer.GetAsync($"/api/v1/invoicing/invoices/{id}")).StatusCode);
        Assert.Equal(0, (await elsewhere.GetFromJsonAsync<JsonElement>("/api/v1/invoicing/invoices")).GetProperty("totalCount").GetInt32());
        Assert.Equal(1, (await cashier.GetFromJsonAsync<JsonElement>("/api/v1/invoicing/invoices")).GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task One_gym_cannot_issue_read_or_list_the_invoices_of_another()
    {
        var gym = await NewGymAsync();
        var (_, obligation) = await PaidSaleAsync(gym.Tenant.Owner, gym.Branch, 30m);
        var id = (await IssueAsync(gym.Tenant.Owner, obligation)).Body.GetProperty("id").GetGuid();
        var stranger = await NewGymAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await IssueAsync(stranger.Tenant.Owner, obligation)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.Tenant.Owner.GetAsync($"/api/v1/invoicing/invoices/{id}")).StatusCode);
        Assert.Equal(0, (await stranger.Tenant.Owner.GetFromJsonAsync<JsonElement>("/api/v1/invoicing/invoices")).GetProperty("totalCount").GetInt32());
    }

    // ---- listado y auditoría ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_list_filters_by_status_number_and_buyer_and_pages_newest_first()
    {
        var gym = await NewGymAsync();
        var owner = gym.Tenant.Owner;
        for (var i = 0; i < 3; i++)
        {
            await IssueAsync(owner, (await PaidSaleAsync(owner, gym.Branch, 10m + i)).ObligationId);
        }

        var all = await owner.GetFromJsonAsync<JsonElement>("/api/v1/invoicing/invoices?pageSize=2");
        Assert.Equal(3, all.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, all.GetProperty("items").GetArrayLength());
        Assert.Equal("001-001-000000003", Text(all.GetProperty("items")[0], "number"));

        var byNumber = await owner.GetFromJsonAsync<JsonElement>("/api/v1/invoicing/invoices?q=001-001-000000002");
        Assert.Equal(1, byNumber.GetProperty("totalCount").GetInt32());
        var byBuyer = await owner.GetFromJsonAsync<JsonElement>("/api/v1/invoicing/invoices?q=ana p");
        Assert.Equal(3, byBuyer.GetProperty("totalCount").GetInt32());
        var nobody = await owner.GetFromJsonAsync<JsonElement>("/api/v1/invoicing/invoices?q=zzzz");
        Assert.Equal(0, nobody.GetProperty("totalCount").GetInt32());
        var pending = await owner.GetFromJsonAsync<JsonElement>("/api/v1/invoicing/invoices?status=PendingSignature");
        Assert.Equal(3, pending.GetProperty("totalCount").GetInt32());
        var authorized = await owner.GetFromJsonAsync<JsonElement>("/api/v1/invoicing/invoices?status=Authorized");
        Assert.Equal(0, authorized.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task Issuing_is_audited()
    {
        var gym = await NewGymAsync();
        var (_, obligation) = await PaidSaleAsync(gym.Tenant.Owner, gym.Branch, 30m);
        await IssueAsync(gym.Tenant.Owner, obligation);

        var actions = await ScalarAsync("SELECT string_agg(DISTINCT action, ',' ORDER BY action) FROM audit.audit_logs WHERE tenant_id = @t AND action LIKE 'Invoice.%'", ("t", gym.Tenant.Id));

        Assert.Equal("Invoice.Created", actions);
    }

    [Fact]
    public async Task The_preview_shows_the_same_calculation_that_issuing_stores_and_flags_an_already_invoiced_sale()
    {
        var gym = await NewGymAsync();
        var owner = gym.Tenant.Owner;
        var (_, obligation) = await PaidSaleAsync(owner, gym.Branch, 30m);
        var url = $"/api/v1/invoicing/sources/obligations/{obligation}/preview";

        var before = await owner.GetFromJsonAsync<JsonElement>(url);
        Assert.True(before.GetProperty("canIssue").GetBoolean());
        Assert.Equal("001-001", Text(before, "emissionPoint"));
        Assert.Equal(JsonValueKind.Null, before.GetProperty("existingInvoiceId").ValueKind);

        var issued = (await IssueAsync(owner, obligation)).Body;
        Assert.Equal(before.GetProperty("calculation").GetProperty("total").GetDecimal(), issued.GetProperty("calculation").GetProperty("total").GetDecimal());
        Assert.Equal(before.GetProperty("calculation").GetProperty("totalTax").GetDecimal(), issued.GetProperty("calculation").GetProperty("totalTax").GetDecimal());

        var after = await owner.GetFromJsonAsync<JsonElement>(url);
        Assert.False(after.GetProperty("canIssue").GetBoolean());
        Assert.Equal(issued.GetProperty("id").GetGuid(), after.GetProperty("existingInvoiceId").GetGuid());
        Assert.Contains(after.GetProperty("issues").EnumerateArray(), i => Text(i, "code") == "already_invoiced");
    }
}
