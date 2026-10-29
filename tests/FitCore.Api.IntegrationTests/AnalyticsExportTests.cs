using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FitCore.Api.IntegrationTests.Infrastructure;
using Npgsql;
using static FitCore.Api.IntegrationTests.Infrastructure.CommercialTestApi;

namespace FitCore.Api.IntegrationTests;

/// <summary>Exportaciones CSV (bloque 8): mismos filtros y alcance que el reporte, archivo privado con vencimiento, descarga protegida y CSV sin fórmulas.</summary>
[Collection(ApiCollection4.Name)]
public partial class AnalyticsExportTests(ApiFixture api)
{
    private static readonly TimeSpan Ecuador = TimeSpan.FromHours(-5);

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.Add(Ecuador));

    private static readonly string[] AnalyticsPermissions =
    [
        "analytics.dashboard.read", "reports.financial.read", "reports.members.read", "reports.operations.read", "reports.instructor.read", "reports.compare", "reports.export"
    ];

    private sealed record Gym(TestTenant Tenant, Guid Uio, Guid Gye, PlanDto Plan);

    private async Task<Gym> NewGymAsync()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var uio = await TestApi.CreateBranchAsync(tenant.Owner, "UIO");
        var gye = await TestApi.CreateBranchAsync(tenant.Owner, "GYE");
        return new Gym(tenant, uio, gye, await CreatePlanAsync(tenant.Owner, "Mensual", 30m));
    }

    private static Dictionary<string, object?> Body(string report, object? extra = null)
    {
        var dictionary = new Dictionary<string, object?>
        {
            ["report"] = report,
            ["format"] = "Csv", // estas pruebas verifican el CSV; el Excel tiene sus propias pruebas
            ["preset"] = "Custom",
            ["from"] = Today.AddDays(-9).ToString("yyyy-MM-dd"),
            ["to"] = Today.ToString("yyyy-MM-dd")
        };
        if (extra is not null)
        {
            foreach (var property in extra.GetType().GetProperties())
            {
                dictionary[property.Name] = property.GetValue(extra);
            }
        }

        return dictionary;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<JsonElement> ExportOkAsync(HttpClient client, string report, object? extra = null)
    {
        var response = await client.PostAsJsonAsync("/api/v1/reports/exports", Body(report, extra));
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"Respondió {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await Json(response);
    }

    private static async Task<(string[] Lines, byte[] Bytes, HttpResponseMessage Response)> DownloadAsync(HttpClient client, string id)
    {
        var response = await client.GetAsync($"/api/v1/reports/exports/{id}/download");
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Respondió {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        var bytes = await response.Content.ReadAsByteArrayAsync();
        var text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        return (text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries), bytes, response);
    }

    private async Task SqlAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(api.AdminConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static int _members;

    private static async Task<MemberDto> NewMemberAsync(HttpClient owner, Guid branch)
    {
        var n = Interlocked.Increment(ref _members);
        var response = await owner.PostAsJsonAsync("/api/v1/members", new
        {
            branchId = branch, firstName = $"Socio{n}", lastName = "Exporta", idType = "Cedula", idNumber = MemberTests.NewCedula(), birthDate = "1992-03-04",
            email = $"e{n}.{Guid.NewGuid():N}"[..24] + "@alumnos.test", phone = "0991234567"
        });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<MemberDto>())!;
    }

    private async Task<HttpClient> StaffAsync(Gym gym, string email, string role, params Guid[] branches)
    {
        await TestApi.CreateUserAsync(gym.Tenant.Owner, email, role, branches);
        return TestApi.Authenticated(api, await TestApi.LoginAsync(api, gym.Tenant.Slug, email, TestApi.UserPassword));
    }

    /// <summary>Quito: A paga 30 con tarjeta y C 10 como «Otro» con un detalle malicioso. Guayaquil: B paga 20 con tarjeta. D (Quito) paga y se revierte.</summary>
    private async Task<(Gym Gym, PaymentDto A, PaymentDto B)> BuildAsync()
    {
        var gym = await NewGymAsync();
        var owner = gym.Tenant.Owner;
        var a = await NewMemberAsync(owner, gym.Uio);
        await owner.PostAsJsonAsync("/api/v1/memberships", new { memberId = a.Id, planId = gym.Plan.Id });
        var paymentA = await PayAsync(owner, a.Id, 30m, "Card", "V-A");
        var b = await NewMemberAsync(owner, gym.Gye);
        var obligationB = await owner.PostAsJsonAsync("/api/v1/payment-obligations", new { memberId = b.Id, concept = "Camiseta", amount = 20m, taxKind = "General" });
        Assert.Equal(HttpStatusCode.Created, obligationB.StatusCode);
        var paymentB = await PayAsync(owner, b.Id, 20m, "Card", "V-B");
        var c = await NewMemberAsync(owner, gym.Uio);
        var extra = (await (await owner.PostAsJsonAsync("/api/v1/payment-obligations", new { memberId = c.Id, concept = "Producto", amount = 10m, taxKind = "General" })).Content.ReadFromJsonAsync<ObligationDto>())!;
        Assert.Equal(HttpStatusCode.Created, (await PostPaymentAsync(owner, PaymentBody(c.Id, 10m, "Other", "L-1", allocations: [new { obligationId = extra.Id, amount = 10m }], methodDetail: "=HYPERLINK(\"http://malo\",\"clic\")"))).StatusCode);
        var d = await NewMemberAsync(owner, gym.Uio);
        var reversedObligation = (await (await owner.PostAsJsonAsync("/api/v1/payment-obligations", new { memberId = d.Id, concept = "Producto", amount = 5m, taxKind = "General" })).Content.ReadFromJsonAsync<ObligationDto>())!;
        var reversed = await PayAsync(owner, d.Id, 5m, "Card", "V-D", allocations: [new { obligationId = reversedObligation.Id, amount = 5m }]);
        Assert.Equal(HttpStatusCode.OK, (await PostWithKeyAsync(owner, $"/api/v1/payments/{reversed.Id}/reverse", new { reason = "Cobro duplicado" })).StatusCode);
        return (gym, paymentA, paymentB);
    }

    [GeneratedRegex(@"^reporte-cobros-\d{8}-[0-9a-f]{8}\.csv$")]
    private static partial Regex CollectionsFileName();

    [Fact]
    public async Task La_exportacion_respeta_los_filtros_se_guarda_con_vencimiento_y_se_descarga_con_cabeceras_seguras()
    {
        var (gym, paymentA, paymentB) = await BuildAsync();
        var owner = gym.Tenant.Owner;

        var export = await ExportOkAsync(owner, "Collections", new { method = "Card" });
        var id = export.GetProperty("id").GetString()!;
        Assert.Equal("Available", export.GetProperty("status").GetString());
        Assert.Equal(2, export.GetProperty("rowCount").GetInt32());
        Assert.Equal("csv", export.GetProperty("format").GetString());
        var remaining = export.GetProperty("expiresAt").GetDateTimeOffset() - DateTimeOffset.UtcNow;
        Assert.InRange(remaining.TotalHours, 23.9, 24.1);
        Assert.Matches(CollectionsFileName(), export.GetProperty("fileName").GetString());
        Assert.Contains("\"Card\"", export.GetProperty("filters").GetString());

        // La descarga trae exactamente lo filtrado: los dos pagos con tarjeta confirmados (no el «Otro» ni el revertido).
        var (lines, bytes, response) = await DownloadAsync(owner, id);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        Assert.Equal("text/csv", response.Content.Headers.ContentType!.MediaType);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        Assert.Contains("nosniff", response.Headers.GetValues("X-Content-Type-Options"));
        Assert.Equal(export.GetProperty("fileName").GetString(), response.Content.Headers.ContentDisposition!.FileName!.Trim('"'));
        Assert.Equal(3, lines.Length); // encabezado + 2 pagos
        Assert.StartsWith("\"Recibo\",\"Fecha de pago\"", lines[0], StringComparison.Ordinal);
        Assert.Contains(lines, l => l.Contains(paymentA.ReceiptNumber, StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains(paymentB.ReceiptNumber, StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("HYPERLINK", StringComparison.Ordinal));
        foreach (var forbiddenPart in new[] { "Socio", "Exporta", "@" })
        {
            Assert.DoesNotContain(forbiddenPart, export.GetProperty("fileName").GetString()!, StringComparison.Ordinal);
        }

        // Cada descarga se cuenta.
        var status = await Json(await owner.GetAsync($"/api/v1/reports/exports/{id}"));
        Assert.Equal(1, status.GetProperty("downloadCount").GetInt32());

        // Sin filtro de medio y con todos los estados: aparecen los 4 pagos, y el detalle malicioso queda neutralizado como texto.
        var all = await ExportOkAsync(owner, "Collections", new { status = "All" });
        var (allLines, _, _) = await DownloadAsync(owner, all.GetProperty("id").GetString()!);
        Assert.Equal(5, allLines.Length);
        var hyperlink = allLines.Single(l => l.Contains("HYPERLINK", StringComparison.Ordinal));
        Assert.Contains("\"'=HYPERLINK(", hyperlink, StringComparison.Ordinal);
        Assert.DoesNotContain(allLines, l => l.Contains(",\"=", StringComparison.Ordinal) || l.StartsWith("\"=", StringComparison.Ordinal));

        // El historial propio, del más reciente al más antiguo.
        var list = await Json(await owner.GetAsync("/api/v1/reports/exports"));
        Assert.Equal(2, list.GetProperty("totalCount").GetInt32());
        Assert.Equal(all.GetProperty("id").GetString(), list.GetProperty("items")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task Cada_exportacion_hereda_el_alcance_del_usuario_y_exige_su_permiso_y_el_de_exportar()
    {
        var (gym, _, _) = await BuildAsync();

        // El gerente de Quito exporta solo lo de Quito (no ve el pago de Guayaquil) y no puede pedir Guayaquil.
        var manager = await StaffAsync(gym, "gerente@gym.test", "AdministradorSucursal", gym.Uio);
        var managerExport = await ExportOkAsync(manager, "Collections");
        Assert.Equal(2, managerExport.GetProperty("rowCount").GetInt32()); // A (tarjeta) y C (otro) de Quito; el de Guayaquil y el revertido no
        var rows = await Json(await manager.GetAsync("/api/v1/reports/financial/collections?preset=Custom&from=" + Today.AddDays(-9).ToString("yyyy-MM-dd") + "&to=" + Today.ToString("yyyy-MM-dd")));
        Assert.Equal(managerExport.GetProperty("rowCount").GetInt32(), rows.GetProperty("payments").GetProperty("totalCount").GetInt32());
        var forbidden = await manager.PostAsJsonAsync("/api/v1/reports/exports", Body("Collections", new { branchIds = new[] { gym.Gye } }));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal("branches.not_allowed", await TestApi.ProblemCodeAsync(forbidden));

        // El contador exporta lo financiero, pero no las clases (no tiene operación ni instructor).
        var accountant = await StaffAsync(gym, "contador@gym.test", "Contador", gym.Uio);
        await ExportOkAsync(accountant, "OutstandingBalances");
        var noArea = await accountant.PostAsJsonAsync("/api/v1/reports/exports", Body("ClassOccupancy"));
        Assert.Equal(HttpStatusCode.Forbidden, noArea.StatusCode);

        // Sin el permiso de exportar (cajero, recepción, entrenador) o sin sesión: nada.
        foreach (var (email, role) in new[] { ("cajero@gym.test", "Cajero"), ("recep@gym.test", "Recepcion"), ("entrenador@gym.test", "Entrenador") })
        {
            var client = await StaffAsync(gym, email, role, gym.Uio);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/v1/reports/exports", Body("Collections"))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/reports/exports")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().PostAsJsonAsync("/api/v1/reports/exports", Body("Collections"))).StatusCode);

        // Un reporte que no existe se rechaza con su código.
        var invalid = await gym.Tenant.Owner.PostAsJsonAsync("/api/v1/reports/exports", Body("Collections", new { report = 99 }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        Assert.Equal("analytics.export_report_invalid", await TestApi.ProblemCodeAsync(invalid));
    }

    [Fact]
    public async Task Solo_quien_la_pidio_puede_verla_y_descargarla_y_otro_gimnasio_no_la_ve()
    {
        var (gym, _, _) = await BuildAsync();
        var other = await NewGymAsync();
        var owner = gym.Tenant.Owner;
        var export = await ExportOkAsync(owner, "PaymentMethods");
        var id = export.GetProperty("id").GetString()!;

        // Otro usuario del mismo gimnasio con permiso de exportar: para él no existe.
        var accountant = await StaffAsync(gym, "contador@gym.test", "Contador", gym.Uio);
        Assert.Equal(HttpStatusCode.NotFound, (await accountant.GetAsync($"/api/v1/reports/exports/{id}")).StatusCode);
        var download = await accountant.GetAsync($"/api/v1/reports/exports/{id}/download");
        Assert.Equal(HttpStatusCode.NotFound, download.StatusCode);
        Assert.Equal("analytics.export_not_found", await TestApi.ProblemCodeAsync(download));
        Assert.Equal(0, (await Json(await accountant.GetAsync("/api/v1/reports/exports"))).GetProperty("totalCount").GetInt32());

        // Otro gimnasio y sin sesión: tampoco.
        Assert.Equal(HttpStatusCode.NotFound, (await other.Tenant.Owner.GetAsync($"/api/v1/reports/exports/{id}/download")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().GetAsync($"/api/v1/reports/exports/{id}/download")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/v1/reports/exports/{Guid.NewGuid()}/download")).StatusCode);

        // Y a quien se le quita el permiso del reporte ya no se le entrega el archivo que había generado.
        var roleName = "Rol-financiero";
        var roleId = await CreateRoleAsync(owner, roleName, false, "analytics.dashboard.read", "reports.financial.read", "reports.export");
        await TestApi.CreateUserAsync(owner, "analista@gym.test", roleName, gym.Uio);
        var analyst = TestApi.Authenticated(api, await TestApi.LoginAsync(api, gym.Tenant.Slug, "analista@gym.test", TestApi.UserPassword));
        var mine = await ExportOkAsync(analyst, "Collections");
        await DownloadAsync(analyst, mine.GetProperty("id").GetString()!);
        var update = await owner.PutAsJsonAsync($"/api/v1/roles/{roleId}", new { name = roleName, description = "Sin reportes financieros", grantsAllBranches = false, permissions = new[] { "analytics.dashboard.read", "reports.export" } });
        Assert.True(update.IsSuccessStatusCode, await update.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await analyst.GetAsync($"/api/v1/reports/exports/{mine.GetProperty("id").GetString()}/download")).StatusCode);
    }

    [Fact]
    public async Task Una_exportacion_vencida_no_se_descarga_y_su_archivo_se_purga()
    {
        var (gym, _, _) = await BuildAsync();
        var owner = gym.Tenant.Owner;
        var export = await ExportOkAsync(owner, "Collections");
        var id = export.GetProperty("id").GetString()!;
        await DownloadAsync(owner, id);

        // Se adelanta el vencimiento en la base (el disparador impide cambiarlo desde la aplicación).
        await SqlAsync($"SET session_replication_role = replica; UPDATE analytics.report_exports SET requested_at = now() - interval '2 days', expires_at = now() - interval '1 hour' WHERE id = '{id}'");

        var expired = await owner.GetAsync($"/api/v1/reports/exports/{id}/download");
        Assert.Equal(HttpStatusCode.NotFound, expired.StatusCode);
        Assert.Equal("analytics.export_expired", await TestApi.ProblemCodeAsync(expired));

        // El registro permanece (quién, cuándo, qué), pero el archivo se borró y ya no tiene clave.
        var status = await Json(await owner.GetAsync($"/api/v1/reports/exports/{id}"));
        Assert.Equal("Purged", status.GetProperty("status").GetString());
        Assert.Equal(1, status.GetProperty("downloadCount").GetInt32());
        var again = await owner.GetAsync($"/api/v1/reports/exports/{id}/download");
        Assert.Equal("analytics.export_expired", await TestApi.ProblemCodeAsync(again));
    }

    [Fact]
    public async Task Una_exportacion_demasiado_grande_se_rechaza_sin_recortar_y_hay_un_limite_por_hora()
    {
        var (gym, _, _) = await BuildAsync();

        // Con el tope en 2 filas, un reporte con 4 pagos se rechaza entero (no se entrega un archivo recortado).
        var capped = api.CreateClientWith(("Analytics:Exports:MaxRows", "2"));
        capped.DefaultRequestHeaders.Authorization = gym.Tenant.Owner.DefaultRequestHeaders.Authorization;
        var tooLarge = await capped.PostAsJsonAsync("/api/v1/reports/exports", Body("Collections", new { status = "All" }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooLarge.StatusCode);
        Assert.Equal("analytics.export_too_large", await TestApi.ProblemCodeAsync(tooLarge));
        Assert.Equal(0, (await Json(await gym.Tenant.Owner.GetAsync("/api/v1/reports/exports"))).GetProperty("totalCount").GetInt32());

        // Diez exportaciones por hora y por usuario; la undécima espera.
        var user = await StaffAsync(gym, "analista@gym.test", "Contador", gym.Uio);
        for (var i = 0; i < 10; i++)
        {
            await ExportOkAsync(user, "PaymentMethods");
        }

        var limited = await user.PostAsJsonAsync("/api/v1/reports/exports", Body("PaymentMethods"));
        Assert.Equal(HttpStatusCode.Conflict, limited.StatusCode);
        Assert.Equal("analytics.export_rate_limited", await TestApi.ProblemCodeAsync(limited));
    }

    [Fact]
    public async Task Todos_los_reportes_se_pueden_exportar_con_su_encabezado_y_el_mismo_numero_de_filas_que_el_reporte()
    {
        var (gym, _, _) = await BuildAsync();
        var owner = gym.Tenant.Owner;

        // Dos usuarios con todos los permisos de analítica para repartir las 13 exportaciones (el límite es por usuario).
        var second = await ClientWithRoleAsync(api, gym.Tenant, "analista@gym.test", true, AnalyticsPermissions);
        string[] reports =
        [
            "Collections", "PaymentMethods", "OutstandingBalances", "CashRegisters", "Reversals", "MemberAcquisitions", "MemberRetention",
            "MemberRenewals", "MemberExpirations", "Attendance", "ClassOccupancy", "ClassCancellations", "BranchComparison"
        ];

        var counts = new Dictionary<string, (int Rows, int Columns)>();
        for (var i = 0; i < reports.Length; i++)
        {
            var client = i < 7 ? owner : second;
            var export = await ExportOkAsync(client, reports[i]);
            var (lines, bytes, _) = await DownloadAsync(client, export.GetProperty("id").GetString()!);
            Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
            Assert.Equal(export.GetProperty("rowCount").GetInt32(), lines.Length - 1);
            counts[reports[i]] = (lines.Length - 1, lines[0].Split("\",\"").Length);
        }

        // Cuadran con los reportes: 3 pagos confirmados (A, B y C) en cobros y 4 medios fijos.
        Assert.Equal(3, counts["Collections"].Rows);
        Assert.Equal(4, counts["PaymentMethods"].Rows);
        Assert.Equal(3, counts["BranchComparison"].Rows); // Quito, Guayaquil y el total
        Assert.Equal(1, counts["Reversals"].Rows);
        Assert.All(counts.Values, c => Assert.True(c.Columns >= 4));
    }
}
