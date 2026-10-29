using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using FitCore.Api.IntegrationTests.Infrastructure;
using Npgsql;
using static FitCore.Api.IntegrationTests.Infrastructure.CommercialTestApi;

namespace FitCore.Api.IntegrationTests;

/// <summary>Logo del gimnasio (carga, validación, aislamiento y limpieza de metadatos) y libros de Excel con la marca del gimnasio.</summary>
[Collection(ApiCollection2.Name)]
public class BrandingTests(ApiFixture api)
{
    // ---- PNG de prueba: se arma byte a byte (sin bibliotecas de imágenes) ---------------------------------------------------

    private static uint[]? _crcTable;

    private static uint Crc(ReadOnlySpan<byte> data)
    {
        _crcTable ??= Enumerable.Range(0, 256).Select(n =>
        {
            var c = (uint)n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            return c;
        }).ToArray();
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc = _crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private static byte[] Chunk(string type, byte[] data)
    {
        var body = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        var length = new[] { (byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length };
        var crc = Crc(body);
        return [.. length, .. body, (byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc];
    }

    /// <summary>PNG RGB de un solo color, del tamaño pedido; opcionalmente con un fragmento de texto (tEXt) que simula metadatos del equipo.</summary>
    private static byte[] Png(int width, int height, byte red = 15, byte green = 118, byte blue = 110, string? textChunk = null)
    {
        var header = new byte[13];
        header[0] = (byte)(width >> 24); header[1] = (byte)(width >> 16); header[2] = (byte)(width >> 8); header[3] = (byte)width;
        header[4] = (byte)(height >> 24); header[5] = (byte)(height >> 16); header[6] = (byte)(height >> 8); header[7] = (byte)height;
        header[8] = 8; header[9] = 2; // 8 bits, RGB

        var raw = new byte[height * (1 + width * 3)];
        for (var y = 0; y < height; y++)
        {
            var row = y * (1 + width * 3);
            for (var x = 0; x < width; x++)
            {
                raw[row + 1 + x * 3] = red; raw[row + 2 + x * 3] = green; raw[row + 3 + x * 3] = blue;
            }
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        var parts = new List<byte[]> { new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, Chunk("IHDR", header) };
        if (textChunk is not null)
        {
            parts.Add(Chunk("tEXt", Encoding.ASCII.GetBytes(textChunk)));
        }

        parts.Add(Chunk("IDAT", compressed.ToArray()));
        parts.Add(Chunk("IEND", []));
        return parts.SelectMany(p => p).ToArray();
    }

    private static MultipartFormDataContent Upload(byte[] bytes, string fileName = "logo.png", string contentType = "image/png")
    {
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return new MultipartFormDataContent { { file, "file", fileName } };
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(api.AdminConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response) => response.IsSuccessStatusCode ? null : await TestApi.ProblemCodeAsync(response);

    // ---- Logo ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task El_propietario_carga_cambia_y_quita_el_logo_y_solo_el_ve_el_suyo()
    {
        var a = await TestApi.NewTenantAsync(api);
        var b = await TestApi.NewTenantAsync(api);

        var empty = await a.Owner.GetFromJsonAsync<JsonElement>("/api/v1/tenant/branding");
        Assert.False(empty.GetProperty("hasLogo").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await a.Owner.GetAsync("/api/v1/tenant/branding/logo")).StatusCode);

        var png = Png(120, 80);
        var saved = await a.Owner.PutAsync("/api/v1/tenant/branding/logo", Upload(png));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var branding = await saved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(branding.GetProperty("hasLogo").GetBoolean());
        Assert.Equal(120, branding.GetProperty("width").GetInt32());
        Assert.Equal(80, branding.GetProperty("height").GetInt32());
        var version1 = branding.GetProperty("version").GetString();

        // Se entrega con cabeceras seguras y es exactamente la imagen cargada (era un PNG limpio: no hay nada que quitar).
        var logo = await a.Owner.GetAsync("/api/v1/tenant/branding/logo");
        Assert.Equal("image/png", logo.Content.Headers.ContentType!.MediaType);
        Assert.Contains("nosniff", logo.Headers.GetValues("X-Content-Type-Options"));
        Assert.Equal(png, await logo.Content.ReadAsByteArrayAsync());

        // El otro gimnasio no ve el logo ajeno ni por la API ni por la base.
        Assert.Equal(HttpStatusCode.NotFound, (await b.Owner.GetAsync("/api/v1/tenant/branding/logo")).StatusCode);
        Assert.False((await b.Owner.GetFromJsonAsync<JsonElement>("/api/v1/tenant/branding")).GetProperty("hasLogo").GetBoolean());
        await using (var connection = new NpgsqlConnection(api.AppConnection))
        {
            await connection.OpenAsync();
            await using (var set = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, false)", connection))
            {
                set.Parameters.AddWithValue("t", b.Id.ToString());
                await set.ExecuteScalarAsync();
            }

            await using var count = new NpgsqlCommand("SELECT count(*) FROM organizations.tenant_logos", connection);
            Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
        }

        // Cambiarlo guarda el archivo nuevo, cambia la versión y borra el anterior del almacén.
        var oldKey = await ScalarAsync<string>($"SELECT storage_key FROM organizations.tenant_logos WHERE tenant_id = '{a.Id}'");
        var second = await a.Owner.PutAsync("/api/v1/tenant/branding/logo", Upload(Png(64, 64, 249, 115, 22)));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.NotEqual(version1, (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetString());
        var newKey = await ScalarAsync<string>($"SELECT storage_key FROM organizations.tenant_logos WHERE tenant_id = '{a.Id}'");
        Assert.NotEqual(oldKey, newKey);
        Assert.Equal(1L, await ScalarAsync<long>($"SELECT count(*) FROM organizations.tenant_logos WHERE tenant_id = '{a.Id}'"));

        // Quitarlo deja al gimnasio sin logo.
        Assert.Equal(HttpStatusCode.OK, (await a.Owner.DeleteAsync("/api/v1/tenant/branding/logo")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.Owner.GetAsync("/api/v1/tenant/branding/logo")).StatusCode);
        Assert.Equal("branding.logo_not_found", await CodeAsync(await a.Owner.DeleteAsync("/api/v1/tenant/branding/logo")));

        // Los cambios quedan auditados.
        var actions = await ScalarAsync<string[]>($"SELECT array_agg(DISTINCT action) FROM audit.audit_logs WHERE tenant_id = '{a.Id}' AND entity = 'TenantLogo'");
        Assert.Contains("Tenant.LogoChanged", actions);
        Assert.Contains("Tenant.LogoRemoved", actions);
    }

    [Fact]
    public async Task Solo_se_aceptan_imagenes_PNG_o_JPG_reales_dentro_de_los_limites()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        async Task<string?> TryAsync(byte[] bytes, string name = "logo.png", string type = "image/png") =>
            await CodeAsync(await tenant.Owner.PutAsync("/api/v1/tenant/branding/logo", Upload(bytes, name, type)));

        // Un SVG puede llevar scripts; un texto con extensión de imagen no es una imagen; un PNG truncado tampoco.
        Assert.Equal("branding.logo_invalid_type", await TryAsync(Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script></svg>"), "logo.svg", "image/svg+xml"));
        Assert.Equal("branding.logo_invalid_type", await TryAsync(Encoding.UTF8.GetBytes("esto no es una imagen"), "logo.png"));
        Assert.Equal("branding.logo_invalid_type", await TryAsync(Png(100, 100)[..40]));
        // El tipo lo decide el contenido, no lo que declare el cliente: un PNG real declarado como otra cosa se acepta como PNG.
        Assert.Null(await TryAsync(Png(100, 100), "foto.jpg", "image/jpeg"));

        // Medidas fuera de rango.
        Assert.Equal("branding.logo_invalid_size", await TryAsync(Png(16, 16)));
        Assert.Equal("branding.logo_invalid_size", await TryAsync(Png(2001, 40)));

        // Más de 1 MB.
        Assert.Equal("branding.logo_too_large", await TryAsync(Png(100, 100).Concat(new byte[1_100_000]).ToArray()));

        // Sin archivo.
        Assert.Equal("branding.logo_required", await CodeAsync(await tenant.Owner.PutAsync("/api/v1/tenant/branding/logo", new MultipartFormDataContent())));

        // Nada de lo rechazado quedó guardado.
        Assert.Equal(1L, await ScalarAsync<long>($"SELECT count(*) FROM organizations.tenant_logos WHERE tenant_id = '{tenant.Id}'"));
    }

    [Fact]
    public async Task Los_metadatos_de_la_imagen_se_eliminan_antes_de_guardarla()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var withMetadata = Png(100, 100, textChunk: "Comment\0ubicacion-gps -0.18,-78.46 equipo-privado");
        Assert.Contains("ubicacion-gps", Encoding.Latin1.GetString(withMetadata), StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.OK, (await tenant.Owner.PutAsync("/api/v1/tenant/branding/logo", Upload(withMetadata))).StatusCode);
        var stored = await (await tenant.Owner.GetAsync("/api/v1/tenant/branding/logo")).Content.ReadAsByteArrayAsync();
        Assert.DoesNotContain("ubicacion-gps", Encoding.Latin1.GetString(stored), StringComparison.Ordinal);
        Assert.True(stored.Length < withMetadata.Length);
        Assert.Equal(Png(100, 100), stored); // idéntica a la misma imagen sin el texto
    }

    /// <summary>JPEG mínimo con la estructura real (SOI, EXIF, SOF0, SOS, datos, EOI); no hace falta que se pueda decodificar para validar y limpiar.</summary>
    private static byte[] Jpeg(int width, int height, string exifText)
    {
        var exif = Encoding.ASCII.GetBytes("Exif  " + exifText);
        var app1 = new byte[] { 0xFF, 0xE1, (byte)((exif.Length + 2) >> 8), (byte)(exif.Length + 2) }.Concat(exif);
        var sof = new byte[]
        {
            0xFF, 0xC0, 0x00, 0x11, 0x08, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 0x03,
            0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01
        };
        var sos = new byte[] { 0xFF, 0xDA, 0x00, 0x0C, 0x03, 0x01, 0x00, 0x02, 0x11, 0x03, 0x11, 0x00, 0x3F, 0x00 };
        var scan = Enumerable.Repeat((byte)0x55, 64);
        return new byte[] { 0xFF, 0xD8 }.Concat(app1).Concat(sof).Concat(sos).Concat(scan).Concat(new byte[] { 0xFF, 0xD9 }).ToArray();
    }

    [Fact]
    public async Task Un_JPG_se_acepta_con_sus_medidas_y_pierde_el_EXIF()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var jpeg = Jpeg(300, 150, "GPS=-0.18,-78.46;Equipo=Telefono-Privado");
        Assert.Contains("Telefono-Privado", Encoding.Latin1.GetString(jpeg), StringComparison.Ordinal);

        var saved = await tenant.Owner.PutAsync("/api/v1/tenant/branding/logo", Upload(jpeg, "logo.jpg", "image/jpeg"));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var branding = await saved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("image/jpeg", branding.GetProperty("contentType").GetString());
        Assert.Equal(300, branding.GetProperty("width").GetInt32());
        Assert.Equal(150, branding.GetProperty("height").GetInt32());

        var stored = await (await tenant.Owner.GetAsync("/api/v1/tenant/branding/logo")).Content.ReadAsByteArrayAsync();
        Assert.DoesNotContain("Telefono-Privado", Encoding.Latin1.GetString(stored), StringComparison.Ordinal);
        Assert.True(stored.Length < jpeg.Length);
        Assert.Equal([0xFF, 0xD8, 0xFF], stored[..3]);
        Assert.Equal([0xFF, 0xD9], stored[^2..]);
    }

    [Fact]
    public async Task Sin_el_permiso_nadie_cambia_el_logo_y_la_plataforma_lo_carga_en_cualquier_gimnasio()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var branch = await TestApi.CreateBranchAsync(tenant.Owner, "UIO");
        var reception = await TestApi.CreateUserAsync(tenant.Owner, "recepcion@logo.test", "Recepcion", branch);
        var staff = TestApi.Authenticated(api, await TestApi.LoginAsync(api, tenant.Slug, "recepcion@logo.test", TestApi.UserPassword));
        Assert.NotEqual(Guid.Empty, reception.Id);

        Assert.Equal(HttpStatusCode.Forbidden, (await staff.PutAsync("/api/v1/tenant/branding/logo", Upload(Png(100, 100)))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.DeleteAsync("/api/v1/tenant/branding/logo")).StatusCode);
        // Quien puede ver los datos del gimnasio ve su marca (el menú la muestra); quien no, no.
        Assert.Equal(HttpStatusCode.OK, (await staff.GetAsync("/api/v1/tenant/branding")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().GetAsync("/api/v1/tenant/branding/logo")).StatusCode);

        // El propietario de un gimnasio no puede cargar el logo de otro por la ruta de plataforma.
        var other = await TestApi.NewTenantAsync(api);
        Assert.Equal(HttpStatusCode.Forbidden, (await tenant.Owner.PutAsync($"/api/v1/platform/tenants/{other.Id}/logo", Upload(Png(100, 100)))).StatusCode);

        // La plataforma sí, y el logo queda en ese gimnasio (no en el de la plataforma).
        using var platform = await TestApi.PlatformClientAsync(api);
        var saved = await platform.PutAsync($"/api/v1/platform/tenants/{other.Id}/logo", Upload(Png(100, 100)));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(other.Id.ToString(), await ScalarAsync<string>($"SELECT tenant_id::text FROM organizations.tenant_logos WHERE tenant_id = '{other.Id}'"));
        Assert.True((await other.Owner.GetFromJsonAsync<JsonElement>("/api/v1/tenant/branding")).GetProperty("hasLogo").GetBoolean());
        Assert.False((await tenant.Owner.GetFromJsonAsync<JsonElement>("/api/v1/tenant/branding")).GetProperty("hasLogo").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await platform.PutAsync($"/api/v1/platform/tenants/{Guid.NewGuid()}/logo", Upload(Png(100, 100)))).StatusCode);
    }

    [Fact]
    public async Task La_base_acota_el_logo_aunque_se_salte_la_aplicacion()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        await using var connection = new NpgsqlConnection(api.AdminConnection);
        await connection.OpenAsync();
        async Task<string> TryAsync(string values)
        {
            await using var command = new NpgsqlCommand($"INSERT INTO organizations.tenant_logos (tenant_id, storage_key, content_type, size_bytes, sha256, width, height, updated_at) VALUES ({values})", connection);
            try
            {
                await command.ExecuteNonQueryAsync();
                return "NO ERROR";
            }
            catch (PostgresException ex)
            {
                return ex.ConstraintName ?? ex.MessageText;
            }
        }

        var hash = new string('a', 64);
        Assert.Equal("ck_tenant_logos_type", await TryAsync($"'{tenant.Id}', 'k', 'image/svg+xml', 10, '{hash}', 100, 100, now()"));
        Assert.Equal("ck_tenant_logos_size", await TryAsync($"'{tenant.Id}', 'k', 'image/png', 2000000, '{hash}', 100, 100, now()"));
        Assert.Equal("ck_tenant_logos_dimensions", await TryAsync($"'{tenant.Id}', 'k', 'image/png', 10, '{hash}', 5000, 100, now()"));
        Assert.Equal("ck_tenant_logos_hash", await TryAsync($"'{tenant.Id}', 'k', 'image/png', 10, 'xyz', 100, 100, now()"));
        Assert.Equal("fk_tenant_logos_tenant", await TryAsync($"'{Guid.NewGuid()}', 'k', 'image/png', 10, '{hash}', 100, 100, now()"));
    }

    // ---- Excel con la marca del gimnasio ------------------------------------------------------------------------------------

    private async Task<TestTenant> GymWithPaymentAsync(string label)
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var branch = await TestApi.CreateBranchAsync(tenant.Owner, "UIO");
        var member = await (await tenant.Owner.PostAsJsonAsync("/api/v1/members", new
        {
            branchId = branch, firstName = "=CMD|' /C calc'!A0", lastName = label, idType = "Cedula", idNumber = MemberTests.NewCedula(), birthDate = "1995-04-12",
            email = $"m.{Guid.NewGuid():N}"[..20] + "@alumnos.test", phone = "0991234567"
        })).Content.ReadFromJsonAsync<JsonElement>();
        var memberId = member.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Created, (await tenant.Owner.PostAsJsonAsync("/api/v1/payment-obligations", new { memberId, concept = "Producto", amount = 20m, taxKind = "General" })).StatusCode);
        var payment = await PostPaymentAsync(tenant.Owner, PaymentBody(memberId, 20m, "Card", $"VCH-{Guid.NewGuid():N}"[..20]));
        Assert.True(payment.StatusCode == HttpStatusCode.Created, await payment.Content.ReadAsStringAsync());
        return tenant;
    }

    private static async Task<(JsonElement Export, byte[] File, HttpResponseMessage Response)> ExportAsync(HttpClient owner, string report, string? format = null)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-5));
        var body = new Dictionary<string, object?>
        {
            ["report"] = report, ["preset"] = "Custom", ["from"] = today.AddDays(-9).ToString("yyyy-MM-dd"), ["to"] = today.ToString("yyyy-MM-dd")
        };
        if (format is not null)
        {
            body["format"] = format;
        }

        var created = await owner.PostAsJsonAsync("/api/v1/reports/exports", body);
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var export = await created.Content.ReadFromJsonAsync<JsonElement>();
        var download = await owner.GetAsync($"/api/v1/reports/exports/{export.GetProperty("id").GetString()}/download");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        return (export, await download.Content.ReadAsByteArrayAsync(), download);
    }

    [Fact]
    public async Task La_exportacion_es_un_libro_de_Excel_ordenado_con_el_nombre_y_el_logo_del_gimnasio()
    {
        var gym = await GymWithPaymentAsync("Perez");
        Assert.Equal(HttpStatusCode.OK, (await gym.Owner.PutAsync("/api/v1/tenant/branding/logo", Upload(Png(200, 100)))).StatusCode);

        // Sin indicar formato, el valor por defecto es Excel.
        var (export, file, response) = await ExportAsync(gym.Owner, "Collections");
        Assert.Equal("xlsx", export.GetProperty("format").GetString());
        Assert.Matches(@"^reporte-cobros-\d{8}-[0-9a-f]{8}\.xlsx$", export.GetProperty("fileName").GetString());
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", response.Content.Headers.ContentType!.MediaType);
        Assert.Contains("nosniff", response.Headers.GetValues("X-Content-Type-Options"));
        Assert.Equal((byte)'P', file[0]); // un .xlsx es un zip («PK»)

        using var workbook = new XLWorkbook(new MemoryStream(file));
        var sheet = workbook.Worksheet(1);
        Assert.Equal(1, sheet.Pictures.Count);                                        // el logo del gimnasio está incrustado
        Assert.Equal($"Gimnasio {gym.Slug}", sheet.Cell(5, 1).GetString());             // y su nombre debajo
        Assert.Equal("Cobros", sheet.Cell(6, 1).GetString());
        Assert.Contains("Período:", sheet.Cell(7, 1).GetString(), StringComparison.Ordinal);

        // Tabla ordenada: encabezado de la marca, filtros, primera fila fija y los datos con su tipo real.
        var header = sheet.Row(9);
        Assert.Equal("Recibo", header.Cell(1).GetString());
        Assert.Equal("#0F766E", header.Cell(1).Style.Fill.BackgroundColor.Color.ToArgb() is var argb ? $"#{argb & 0xFFFFFF:X6}" : "");
        Assert.True(sheet.AutoFilter.IsEnabled);
        Assert.Equal(9, sheet.SheetView.SplitRow);
        var columns = Enumerable.Range(1, 12).Select(c => header.Cell(c).GetString()).ToList();
        var amountColumn = columns.FindIndex(c => c.StartsWith("Importe", StringComparison.Ordinal)) + 1;
        var dateColumn = columns.IndexOf("Fecha de pago") + 1;
        Assert.True(amountColumn > 0 && dateColumn > 0, string.Join(" | ", columns));
        var data = sheet.Row(10);
        Assert.Equal(XLDataType.Number, data.Cell(amountColumn).DataType);
        Assert.Equal(20m, (decimal)data.Cell(amountColumn).Value.GetNumber());
        Assert.Equal(XLDataType.DateTime, data.Cell(dateColumn).DataType);

        // Un nombre que parece fórmula queda como texto: ninguna celda del libro es una fórmula.
        var texts = sheet.CellsUsed().Where(c => c.DataType == XLDataType.Text).Select(c => c.GetString()).ToList();
        Assert.Contains(texts, t => t.StartsWith("=CMD", StringComparison.Ordinal));
        Assert.All(sheet.CellsUsed(), c => Assert.False(c.HasFormula, $"{c.Address} es una fórmula"));
    }

    [Fact]
    public async Task Sin_logo_el_libro_sale_igual_con_el_nombre_y_cada_gimnasio_solo_lleva_su_marca()
    {
        var withLogo = await GymWithPaymentAsync("Con");
        var without = await GymWithPaymentAsync("Sin");
        await withLogo.Owner.PutAsync("/api/v1/tenant/branding/logo", Upload(Png(100, 100)));

        var (_, plainFile, _) = await ExportAsync(without.Owner, "Collections", "Xlsx");
        using (var plain = new XLWorkbook(new MemoryStream(plainFile)))
        {
            var sheet = plain.Worksheet(1);
            Assert.Equal(0, sheet.Pictures.Count);
            Assert.Equal($"Gimnasio {without.Slug}", sheet.Cell(1, 1).GetString()); // sin logo, el texto sube al inicio
            Assert.DoesNotContain(withLogo.Slug, string.Join(" ", sheet.CellsUsed().Select(c => c.GetString())), StringComparison.Ordinal);
        }

        // Quitar el logo también lo quita de los libros nuevos.
        await withLogo.Owner.DeleteAsync("/api/v1/tenant/branding/logo");
        var (_, afterRemoval, _) = await ExportAsync(withLogo.Owner, "Collections", "Xlsx");
        using var after = new XLWorkbook(new MemoryStream(afterRemoval));
        Assert.Equal(0, after.Worksheet(1).Pictures.Count);
    }

    [Fact]
    public async Task Sin_datos_el_libro_dice_que_no_hay_filas_y_el_CSV_sigue_disponible()
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var (export, file, _) = await ExportAsync(tenant.Owner, "Collections", "Xlsx");
        Assert.Equal(0, export.GetProperty("rowCount").GetInt32());
        using (var workbook = new XLWorkbook(new MemoryStream(file)))
        {
            Assert.Contains("Sin datos", workbook.Worksheet(1).Cell(6, 1).GetString(), StringComparison.Ordinal); // sin logo, el encabezado va en la fila 5
        }

        var (csvExport, csv, response) = await ExportAsync(tenant.Owner, "Collections", "Csv");
        Assert.Equal("csv", csvExport.GetProperty("format").GetString());
        Assert.Equal("text/csv", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal([0xEF, 0xBB, 0xBF], csv[..3]);

        // Un formato inventado se rechaza sin generar nada.
        var bad = await tenant.Owner.PostAsJsonAsync("/api/v1/reports/exports", new { report = "Collections", preset = "Last30Days", format = "Pdf" });
        Assert.NotEqual(HttpStatusCode.Created, bad.StatusCode);
    }
}
