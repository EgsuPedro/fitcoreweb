using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FitCore.Api.IntegrationTests.Infrastructure;
using Npgsql;
using static FitCore.Api.IntegrationTests.Infrastructure.CommercialTestApi;

namespace FitCore.Api.IntegrationTests;

[Collection(ApiCollection2.Name)]
public class ProgressPhotoTests(ApiFixture api)
{
    private sealed record ConsentEventRow(Guid Id, string Action, string Source, int NoticeVersion, string? GuardianName);

    private sealed record ConsentRow(string Status, string? Source, int? NoticeVersion, string? GuardianName, int CurrentNoticeVersion, int PhotoCount, ConsentEventRow[] History);

    private sealed record RevokeRow(int DeletedPhotos, ConsentRow Consent);

    private sealed record PhotoRow(Guid Id, Guid MemberId, DateOnly TakenOn, string Angle, string ContentType, long SizeBytes);

    private sealed record TrainerRow(Guid Id);

    private sealed record Setup(TestTenant Tenant, Guid Branch, HttpClient Trainer, MemberDto Member, HttpClient Student);

    private static readonly byte[] Secret = "GPS-LAT-0.123-LON-78.456-SECRETO"u8.ToArray();

    private const string StudentPassword = "Nueva-Clave-2026x";

    private static DateOnly Today =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/Guayaquil")).DateTime);

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd");

    private static string Unique(string prefix) => $"{prefix}.{Guid.NewGuid():N}"[..22] + "@gym.test";

    // ---- Imágenes de prueba ----------------------------------------------------------------------------------------

    private static byte[] Segment(byte marker, byte[] payload) =>
        [0xFF, marker, (byte)((payload.Length + 2) >> 8), (byte)((payload.Length + 2) & 0xFF), .. payload];

    /// <summary>JPEG con EXIF (que trae un "GPS" secreto), datos comprimidos y un archivo añadido tras el final.</summary>
    private static byte[] Jpeg(int padding = 0, byte fill = 0x11) =>
    [
        0xFF, 0xD8,
        .. Segment(0xE0, "JFIF\0"u8.ToArray()),
        .. Segment(0xE1, [.. "Exif\0\0"u8, .. Secret]),
        .. Segment(0xDA, [0x01, 0x01, 0x00, 0x00, 0x3F, 0x00]),
        0x01, 0x02, 0x03, fill,
        .. new byte[padding],
        0xFF, 0xD9,
        .. Secret
    ];

    private static byte[] PngChunk(string type, byte[] data) =>
        [(byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length, .. System.Text.Encoding.ASCII.GetBytes(type), .. data, 0, 0, 0, 0];

    private static byte[] Png() =>
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        .. PngChunk("IHDR", new byte[13]),
        .. PngChunk("tEXt", [.. "GPS\0"u8, .. Secret]),
        .. PngChunk("IDAT", [1, 2, 3]),
        .. PngChunk("IEND", []),
        .. Secret
    ];

    private static MultipartFormDataContent Form(byte[] file, string? takenOn, string? angle, string mediaType = "image/jpeg", string fileName = "foto.jpg")
    {
        var content = new MultipartFormDataContent();
        var part = new ByteArrayContent(file);
        part.Headers.ContentType = new(mediaType);
        content.Add(part, "file", fileName);
        if (takenOn is not null)
        {
            content.Add(new StringContent(takenOn), "takenOn");
        }

        if (angle is not null)
        {
            content.Add(new StringContent(angle), "angle");
        }

        return content;
    }

    private static Task<HttpResponseMessage> UploadAsync(HttpClient client, Guid memberId, byte[] file, string? takenOn = null, string angle = "Front", string mediaType = "image/jpeg") =>
        client.PostAsync($"/api/v1/members/{memberId}/progress-photos", Form(file, takenOn ?? Iso(Today), angle, mediaType));

    private static async Task<PhotoRow> UploadOkAsync(HttpClient client, Guid memberId, byte[]? file = null, string? takenOn = null, string angle = "Front")
    {
        var response = await UploadAsync(client, memberId, file ?? Jpeg(), takenOn, angle);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"Respondió {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<PhotoRow>())!;
    }

    // ---- Preparación -----------------------------------------------------------------------------------------------

    private async Task<Setup> NewSetupAsync(string code, string birthDate = "1990-05-10")
    {
        var tenant = await TestApi.NewTenantAsync(api);
        var branch = await TestApi.CreateBranchAsync(tenant.Owner, code);
        var trainerEmail = Unique("entrena");
        var user = await TestApi.CreateUserAsync(tenant.Owner, trainerEmail, "Entrenador", branch);
        var profile = (await (await tenant.Owner.PostAsJsonAsync("/api/v1/trainers", new { userId = user.Id })).Content.ReadFromJsonAsync<TrainerRow>())!;
        var trainer = TestApi.Authenticated(api, await TestApi.LoginAsync(api, tenant.Slug, trainerEmail, TestApi.UserPassword));

        var studentEmail = Unique("alumno");
        var memberResponse = await tenant.Owner.PostAsJsonAsync("/api/v1/members", new
        {
            branchId = branch, firstName = "Diana", lastName = "Prueba", idType = "Cedula", idNumber = MemberTests.NewCedula(), email = studentEmail, phone = "0991234567", birthDate
        });
        Assert.Equal(HttpStatusCode.Created, memberResponse.StatusCode);
        var member = (await memberResponse.Content.ReadFromJsonAsync<MemberDto>())!;
        Assert.Equal(HttpStatusCode.Created, (await tenant.Owner.PostAsJsonAsync($"/api/v1/trainers/{profile.Id}/members", new { memberId = member.Id })).StatusCode);

        var access = await (await tenant.Owner.PostAsync($"/api/v1/members/{member.Id}/access", null)).Content.ReadFromJsonAsync<JsonElement>();
        var temporary = access.GetProperty("temporaryPassword").GetString()!;
        using var temp = TestApi.Authenticated(api, await TestApi.LoginAsync(api, tenant.Slug, studentEmail, temporary));
        Assert.Equal(HttpStatusCode.NoContent, (await temp.PostAsJsonAsync("/api/v1/auth/change-password", new { currentPassword = temporary, newPassword = StudentPassword })).StatusCode);
        var student = TestApi.Authenticated(api, await TestApi.LoginAsync(api, tenant.Slug, studentEmail, StudentPassword));
        return new Setup(tenant, branch, trainer, member, student);
    }

    private async Task<HttpClient> UserClientAsync(TestTenant tenant, Guid branch, string role)
    {
        var email = Unique(role.ToLowerInvariant());
        await TestApi.CreateUserAsync(tenant.Owner, email, role, branch);
        return TestApi.Authenticated(api, await TestApi.LoginAsync(api, tenant.Slug, email, TestApi.UserPassword));
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, await TestApi.ProblemCodeAsync(response));
    }

    private static async Task<ConsentRow> GrantAsync(HttpClient client, Guid memberId, string? guardianName = null)
    {
        var response = await client.PostAsJsonAsync($"/api/v1/members/{memberId}/photo-consent/grant", new { guardianName });
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Respondió {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<ConsentRow>())!;
    }

    private static async Task<ConsentRow> ConsentAsync(HttpClient client, Guid memberId) =>
        (await client.GetFromJsonAsync<ConsentRow>($"/api/v1/members/{memberId}/photo-consent"))!;

    private static async Task<PhotoRow[]> ListAsync(HttpClient client, Guid memberId) =>
        (await client.GetFromJsonAsync<PhotoRow[]>($"/api/v1/members/{memberId}/progress-photos"))!;

    /// <summary>Archivos que hay en el almacén privado para un gimnasio.</summary>
    private string[] StoredFiles(Guid tenantId)
    {
        var folder = Path.Combine(api.FilesRoot, "progress-photos", tenantId.ToString("N"));
        return Directory.Exists(folder) ? Directory.GetFiles(folder) : [];
    }

    private async Task SqlAsync(Guid tenantId, string sql, bool asApp = false)
    {
        await using var connection = new NpgsqlConnection(asApp ? api.AppConnection : api.AdminConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"SELECT set_config('app.tenant_id', '{tenantId}', false); {sql}", connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<long> CountPhotosAsync(Guid tenantId, Guid memberId)
    {
        await using var connection = new NpgsqlConnection(api.AdminConnection);
        await connection.OpenAsync();
        await using (var set = new NpgsqlCommand($"SELECT set_config('app.tenant_id', '{tenantId}', false)", connection))
        {
            await set.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand($"SELECT count(*) FROM wellness.progress_photos WHERE member_id = '{memberId}'", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static string InsertPhotoSql(Guid tenantId, Guid memberId, Guid consentId, string key = "progress-photos/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb") =>
        "INSERT INTO wellness.progress_photos (id, tenant_id, member_id, taken_on, angle, storage_key, content_type, size_bytes, consent_id, uploaded_by, created_at) " +
        $"VALUES (gen_random_uuid(), '{tenantId}', '{memberId}', current_date, 'Front', '{key}', 'image/jpeg', 100, '{consentId}', " +
        $"(SELECT id FROM identity.app_users WHERE tenant_id = '{tenantId}' LIMIT 1), now())";

    // ---- Flujo -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Sin_consentimiento_no_se_sube_y_con_el_la_foto_se_guarda_privada_y_sin_metadatos()
    {
        var s = await NewSetupAsync("FLU");
        var baseUrl = $"/api/v1/members/{s.Member.Id}";

        await AssertProblemAsync(await UploadAsync(s.Student, s.Member.Id, Jpeg()), HttpStatusCode.Conflict, "photos.consent_required");
        var none = await ConsentAsync(s.Student, s.Member.Id);
        Assert.Equal(("None", 1, 0), (none.Status, none.CurrentNoticeVersion, none.History.Length));

        var granted = await GrantAsync(s.Student, s.Member.Id);
        Assert.Equal(("Granted", "Member", 1), (granted.Status, granted.Source, granted.NoticeVersion));
        await AssertProblemAsync(await s.Student.PostAsJsonAsync($"{baseUrl}/photo-consent/grant", new { }), HttpStatusCode.Conflict, "photos.consent_already_granted");

        var original = Jpeg();
        var photo = await UploadOkAsync(s.Student, s.Member.Id, original, takenOn: Iso(Today.AddDays(-3)), angle: "Side");
        Assert.Equal(("image/jpeg", "Side", Today.AddDays(-3)), (photo.ContentType, photo.Angle, photo.TakenOn));
        Assert.True(photo.SizeBytes < original.Length);

        var content = await s.Student.GetAsync($"/api/v1/progress-photos/{photo.Id}/content");
        Assert.Equal(HttpStatusCode.OK, content.StatusCode);
        Assert.Equal("image/jpeg", content.Content.Headers.ContentType?.MediaType);
        Assert.Contains("no-store", content.Headers.CacheControl?.ToString());
        Assert.Equal("nosniff", content.Headers.GetValues("X-Content-Type-Options").Single());
        var bytes = await content.Content.ReadAsByteArrayAsync();
        Assert.Equal(photo.SizeBytes, bytes.Length);
        Assert.Equal([0xFF, 0xD8], bytes[..2]);
        Assert.Equal(-1, bytes.AsSpan().IndexOf(Secret));

        // El archivo vive en el almacén privado con un nombre aleatorio que no revela a la persona, y es el mismo que se entrega.
        var stored = Assert.Single(StoredFiles(s.Tenant.Id));
        Assert.Matches(new Regex("^[0-9a-f]{32}$"), Path.GetFileName(stored));
        Assert.DoesNotContain(s.Member.Id.ToString("N"), stored, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(stored));

        // El entrenador asignado también lo ve y puede subir fotos en nombre del alumno.
        Assert.Equal([photo.Id], (await ListAsync(s.Trainer, s.Member.Id)).Select(p => p.Id));
        Assert.Equal(HttpStatusCode.OK, (await s.Trainer.GetAsync($"/api/v1/progress-photos/{photo.Id}/content")).StatusCode);
        var png = await UploadOkAsync(s.Trainer, s.Member.Id, Png(), angle: "Back");
        Assert.Equal("image/png", png.ContentType);
        Assert.Equal([png.Id, photo.Id], (await ListAsync(s.Student, s.Member.Id)).Select(p => p.Id)); // más reciente primero
        Assert.Equal(2, (await ConsentAsync(s.Student, s.Member.Id)).PhotoCount);
    }

    [Fact]
    public async Task Retirar_el_consentimiento_borra_las_fotos_y_los_archivos_y_hay_que_consentir_de_nuevo()
    {
        var s = await NewSetupAsync("REV");
        await GrantAsync(s.Student, s.Member.Id);
        var first = await UploadOkAsync(s.Student, s.Member.Id);
        await UploadOkAsync(s.Student, s.Member.Id, Png());
        Assert.Equal(2, StoredFiles(s.Tenant.Id).Length);

        var revoke = await s.Student.PostAsync($"/api/v1/members/{s.Member.Id}/photo-consent/revoke", null);
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        var result = (await revoke.Content.ReadFromJsonAsync<RevokeRow>())!;
        Assert.Equal((2, "Revoked", 0), (result.DeletedPhotos, result.Consent.Status, result.Consent.PhotoCount));

        Assert.Empty(StoredFiles(s.Tenant.Id));
        Assert.Equal(0, await CountPhotosAsync(s.Tenant.Id, s.Member.Id));
        Assert.Empty(await ListAsync(s.Student, s.Member.Id));
        await AssertProblemAsync(await s.Student.GetAsync($"/api/v1/progress-photos/{first.Id}/content"), HttpStatusCode.NotFound, "photos.not_found");
        await AssertProblemAsync(await UploadAsync(s.Student, s.Member.Id, Jpeg()), HttpStatusCode.Conflict, "photos.consent_required");
        await AssertProblemAsync(await s.Student.PostAsync($"/api/v1/members/{s.Member.Id}/photo-consent/revoke", null), HttpStatusCode.Conflict, "photos.consent_not_granted");

        // El historial conserva el dar y el retirar (más reciente primero) y se puede consentir otra vez.
        var regrant = await GrantAsync(s.Trainer, s.Member.Id);
        Assert.Equal(["Granted", "Revoked", "Granted"], regrant.History.Select(h => h.Action));
        Assert.Equal(["Staff", "Member", "Member"], regrant.History.Select(h => h.Source));
        await UploadOkAsync(s.Student, s.Member.Id);
    }

    [Fact]
    public async Task El_personal_tambien_puede_retirar_el_consentimiento_en_nombre_del_alumno()
    {
        var s = await NewSetupAsync("RVS");
        await GrantAsync(s.Trainer, s.Member.Id);
        await UploadOkAsync(s.Trainer, s.Member.Id);

        var result = (await (await s.Trainer.PostAsync($"/api/v1/members/{s.Member.Id}/photo-consent/revoke", null)).Content.ReadFromJsonAsync<RevokeRow>())!;

        Assert.Equal((1, "Revoked", "Staff"), (result.DeletedPhotos, result.Consent.Status, result.Consent.History[0].Source));
        Assert.Empty(StoredFiles(s.Tenant.Id));
    }

    [Fact]
    public async Task El_consentimiento_de_un_menor_lo_registra_el_personal_con_su_representante()
    {
        var minor = await NewSetupAsync("MEN", birthDate: Iso(Today.AddYears(-12)));
        var adult = await NewSetupAsync("ADU");
        var url = $"/api/v1/members/{minor.Member.Id}/photo-consent/grant";

        await AssertProblemAsync(await minor.Student.PostAsJsonAsync(url, new { guardianName = "Rosa Pérez" }), HttpStatusCode.UnprocessableEntity, "photos.guardian_consent_by_staff");
        await AssertProblemAsync(await minor.Trainer.PostAsJsonAsync(url, new { }), HttpStatusCode.UnprocessableEntity, "photos.guardian_required");
        await AssertProblemAsync(await minor.Trainer.PostAsJsonAsync(url, new { guardianName = "Ro" }), HttpStatusCode.UnprocessableEntity, "photos.guardian_required");
        await AssertProblemAsync(await UploadAsync(minor.Student, minor.Member.Id, Jpeg()), HttpStatusCode.Conflict, "photos.consent_required");

        var granted = await GrantAsync(minor.Trainer, minor.Member.Id, "  Rosa Pérez  ");
        Assert.Equal(("Granted", "Staff", "Rosa Pérez"), (granted.Status, granted.Source, granted.GuardianName));
        await UploadOkAsync(minor.Student, minor.Member.Id);

        // En un adulto el representante no aplica: se ignora.
        Assert.Null((await GrantAsync(adult.Trainer, adult.Member.Id, "Alguien Más")).GuardianName);
    }

    // ---- Archivos --------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("texto_declarado_como_jpeg", "photos.file_invalid")]
    [InlineData("gif", "photos.file_invalid")]
    [InlineData("webp", "photos.file_invalid")]
    [InlineData("jpeg_cortado", "photos.file_invalid")]
    [InlineData("vacio", "photos.file_required")]
    [InlineData("demasiado_grande", "photos.file_too_large")]
    [InlineData("fecha_futura", "photos.date_invalid")]
    [InlineData("fecha_con_formato_raro", "photos.date_invalid")]
    [InlineData("sin_fecha", "photos.date_invalid")]
    [InlineData("angulo_desconocido", "photos.angle_invalid")]
    [InlineData("angulo_numerico", "photos.angle_invalid")]
    public async Task Los_archivos_y_datos_invalidos_se_rechazan_con_su_codigo_y_no_dejan_nada(string caso, string code)
    {
        var s = await NewSetupAsync("INV");
        await GrantAsync(s.Student, s.Member.Id);
        var url = $"/api/v1/members/{s.Member.Id}/progress-photos";
        var good = Jpeg();

        var content = caso switch
        {
            "texto_declarado_como_jpeg" => Form("<html><script>alert(1)</script></html>"u8.ToArray(), Iso(Today), "Front"),
            "gif" => Form("GIF89a\x01\0\x01\0"u8.ToArray(), Iso(Today), "Front", "image/gif", "foto.gif"),
            "webp" => Form("RIFF\0\0\0\0WEBPVP8 "u8.ToArray(), Iso(Today), "Front", "image/webp", "foto.webp"),
            "jpeg_cortado" => Form(good[..30], Iso(Today), "Front"),
            "vacio" => Form([], Iso(Today), "Front"),
            "demasiado_grande" => Form(Jpeg(padding: 8 * 1024 * 1024), Iso(Today), "Front"),
            "fecha_futura" => Form(good, Iso(Today.AddDays(2)), "Front"),
            "fecha_con_formato_raro" => Form(good, "15/06/2026", "Front"),
            "sin_fecha" => Form(good, null, "Front"),
            "angulo_desconocido" => Form(good, Iso(Today), "Diagonal"),
            _ => Form(good, Iso(Today), "99")
        };

        await AssertProblemAsync(await s.Student.PostAsync(url, content), HttpStatusCode.UnprocessableEntity, code);
        Assert.Empty(StoredFiles(s.Tenant.Id));
        Assert.Empty(await ListAsync(s.Student, s.Member.Id));
    }

    [Fact]
    public async Task Una_solicitud_que_no_es_un_formulario_se_rechaza()
    {
        var s = await NewSetupAsync("FRM");
        await GrantAsync(s.Student, s.Member.Id);

        var response = await s.Student.PostAsJsonAsync($"/api/v1/members/{s.Member.Id}/progress-photos", new { takenOn = Iso(Today) });

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "photos.file_required");
    }

    [Fact]
    public async Task Hay_un_maximo_de_fotos_por_alumno()
    {
        var s = await NewSetupAsync("MAX");
        var consent = await GrantAsync(s.Student, s.Member.Id);
        await SqlAsync(s.Tenant.Id,
            "INSERT INTO wellness.progress_photos (id, tenant_id, member_id, taken_on, angle, storage_key, content_type, size_bytes, consent_id, uploaded_by, created_at) " +
            $"SELECT gen_random_uuid(), '{s.Tenant.Id}', '{s.Member.Id}', current_date, 'Front', " +
            "'progress-photos/' || replace(gen_random_uuid()::text, '-', '') || '/' || replace(gen_random_uuid()::text, '-', ''), 'image/jpeg', 100, " +
            $"'{consent.History[0].Id}', (SELECT id FROM identity.app_users WHERE tenant_id = '{s.Tenant.Id}' LIMIT 1), now() FROM generate_series(1, 100)");

        await AssertProblemAsync(await UploadAsync(s.Student, s.Member.Id, Jpeg()), HttpStatusCode.Conflict, "photos.limit_reached");
        Assert.Equal(100, (await ListAsync(s.Student, s.Member.Id)).Length);
    }

    [Fact]
    public async Task Borrar_una_foto_quita_el_registro_y_el_archivo()
    {
        var s = await NewSetupAsync("DEL");
        var other = await NewSetupAsync("DE2");
        await GrantAsync(s.Student, s.Member.Id);
        var mine = await UploadOkAsync(s.Student, s.Member.Id);
        var byTrainer = await UploadOkAsync(s.Trainer, s.Member.Id, Png());
        Assert.Equal(2, StoredFiles(s.Tenant.Id).Length);

        Assert.Equal(HttpStatusCode.NotFound, (await other.Trainer.DeleteAsync($"/api/v1/progress-photos/{mine.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await s.Student.DeleteAsync($"/api/v1/progress-photos/{mine.Id}")).StatusCode);
        Assert.Single(StoredFiles(s.Tenant.Id));
        await AssertProblemAsync(await s.Student.DeleteAsync($"/api/v1/progress-photos/{mine.Id}"), HttpStatusCode.NotFound, "photos.not_found");
        Assert.Equal(HttpStatusCode.NoContent, (await s.Trainer.DeleteAsync($"/api/v1/progress-photos/{byTrainer.Id}")).StatusCode);

        Assert.Empty(StoredFiles(s.Tenant.Id));
        Assert.Empty(await ListAsync(s.Student, s.Member.Id));
        Assert.Equal("Granted", (await ConsentAsync(s.Student, s.Member.Id)).Status); // borrar fotos no retira el consentimiento
    }

    // ---- Acceso ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Solo_el_alumno_y_su_entrenador_acceden_y_el_propietario_el_administrador_y_los_ajenos_no()
    {
        var s = await NewSetupAsync("ACC");
        var other = await NewSetupAsync("AC2");
        await GrantAsync(s.Student, s.Member.Id);
        var photo = await UploadOkAsync(s.Student, s.Member.Id);
        var admin = await UserClientAsync(s.Tenant, s.Branch, "AdministradorSucursal");
        var strangerEmail = Unique("ajeno");
        var stranger = await TestApi.CreateUserAsync(s.Tenant.Owner, strangerEmail, "Entrenador", s.Branch);
        await s.Tenant.Owner.PostAsJsonAsync("/api/v1/trainers", new { userId = stranger.Id });
        var strangerClient = TestApi.Authenticated(api, await TestApi.LoginAsync(api, s.Tenant.Slug, strangerEmail, TestApi.UserPassword));
        var member = $"/api/v1/members/{s.Member.Id}";
        var content = $"/api/v1/progress-photos/{photo.Id}/content";

        // Sin permiso (los sensibles no se dan por defecto al propietario ni al administrador).
        foreach (var client in new[] { s.Tenant.Owner, admin })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{member}/photo-consent")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{member}/progress-photos")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(content)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"{member}/photo-consent/grant", new { })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"{member}/photo-consent/revoke", null)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await UploadAsync(client, s.Member.Id, Jpeg())).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync($"/api/v1/progress-photos/{photo.Id}")).StatusCode);
        }

        // Entrenador de la misma sucursal pero no asignado al alumno: responde como si no existiera.
        await AssertProblemAsync(await strangerClient.GetAsync($"{member}/photo-consent"), HttpStatusCode.NotFound, "photos.member_not_found");
        await AssertProblemAsync(await strangerClient.GetAsync($"{member}/progress-photos"), HttpStatusCode.NotFound, "photos.member_not_found");
        await AssertProblemAsync(await UploadAsync(strangerClient, s.Member.Id, Jpeg()), HttpStatusCode.NotFound, "photos.member_not_found");
        await AssertProblemAsync(await strangerClient.PostAsJsonAsync($"{member}/photo-consent/grant", new { }), HttpStatusCode.NotFound, "photos.member_not_found");
        await AssertProblemAsync(await strangerClient.GetAsync(content), HttpStatusCode.NotFound, "photos.not_found");
        await AssertProblemAsync(await strangerClient.DeleteAsync($"/api/v1/progress-photos/{photo.Id}"), HttpStatusCode.NotFound, "photos.not_found");

        // Otro gimnasio: ni el alumno ni el entrenador de allá ven nada de aquí.
        foreach (var client in new[] { other.Student, other.Trainer })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{member}/progress-photos")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(content)).StatusCode);
        }

        // Sin sesión.
        using var anonymous = api.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(content)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"{member}/progress-photos")).StatusCode);

        // El dueño de la foto sí, y la foto sigue ahí.
        Assert.Equal(HttpStatusCode.OK, (await s.Student.GetAsync(content)).StatusCode);
        Assert.Single(StoredFiles(s.Tenant.Id));
    }

    [Fact]
    public async Task Se_audita_el_hecho_sin_registrar_el_representante_ni_la_clave_del_archivo()
    {
        var s = await NewSetupAsync("AUD", birthDate: Iso(Today.AddYears(-14)));
        var guardian = $"Representante-Secreto-{Guid.NewGuid():N}"[..40];
        await GrantAsync(s.Trainer, s.Member.Id, guardian);
        var photo = await UploadOkAsync(s.Student, s.Member.Id);
        await s.Trainer.GetAsync($"/api/v1/members/{s.Member.Id}/photo-consent");
        await s.Trainer.GetAsync($"/api/v1/members/{s.Member.Id}/progress-photos");
        await s.Trainer.GetAsync($"/api/v1/progress-photos/{photo.Id}/content");
        var key = Path.GetFileName(Assert.Single(StoredFiles(s.Tenant.Id)));
        await s.Trainer.DeleteAsync($"/api/v1/progress-photos/{photo.Id}");
        await s.Trainer.PostAsync($"/api/v1/members/{s.Member.Id}/photo-consent/revoke", null);

        var audit = await s.Tenant.Owner.GetStringAsync("/api/v1/audit-logs?pageSize=200");

        foreach (var action in new[]
                 {
                     "Wellness.PhotoConsentGranted", "Wellness.PhotoUploaded", "Wellness.PhotoConsentRead", "Wellness.PhotosListed",
                     "Wellness.PhotoViewed", "Wellness.PhotoDeleted", "Wellness.PhotoConsentRevoked"
                 })
        {
            Assert.Contains(action, audit);
        }

        Assert.DoesNotContain(guardian, audit);
        Assert.DoesNotContain(key, audit);
    }

    // ---- Base de datos ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task La_base_no_deja_modificar_ni_borrar_consentimientos_ni_editar_fotos()
    {
        var s = await NewSetupAsync("TRG");
        await GrantAsync(s.Student, s.Member.Id);
        await UploadOkAsync(s.Student, s.Member.Id);
        var t = s.Tenant.Id;

        // Con el administrador de la base actúan los disparadores (con tenant fijado: sin él RLS oculta las filas y no habría nada que probar).
        foreach (var sql in new[]
                 {
                     $"UPDATE wellness.photo_consents SET notice_version = 2 WHERE member_id = '{s.Member.Id}'",
                     $"DELETE FROM wellness.photo_consents WHERE member_id = '{s.Member.Id}'",
                     $"UPDATE wellness.progress_photos SET angle = 'Back' WHERE member_id = '{s.Member.Id}'"
                 })
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => SqlAsync(t, sql));
            Assert.Equal("23514", ex.SqlState);
        }

        // El rol de la aplicación ni siquiera tiene el privilegio.
        foreach (var sql in new[]
                 {
                     "UPDATE wellness.photo_consents SET notice_version = 2",
                     "DELETE FROM wellness.photo_consents",
                     "UPDATE wellness.progress_photos SET angle = 'Back'"
                 })
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => SqlAsync(t, sql, asApp: true));
            Assert.Equal("42501", ex.SqlState);
        }

        Assert.Equal(1, await CountPhotosAsync(t, s.Member.Id));
    }

    [Fact]
    public async Task La_base_solo_acepta_una_foto_si_su_consentimiento_es_el_vigente_y_valida_la_clave()
    {
        var s = await NewSetupAsync("CNS");
        var t = s.Tenant.Id;
        var granted = (await GrantAsync(s.Student, s.Member.Id)).History[0].Id;
        await s.Student.PostAsync($"/api/v1/members/{s.Member.Id}/photo-consent/revoke", null);
        var revoked = (await ConsentAsync(s.Student, s.Member.Id)).History[0].Id;

        // Retirado: ni con el consentimiento dado antes ni con el registro de retiro.
        foreach (var consentId in new[] { granted, revoked })
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => SqlAsync(t, InsertPhotoSql(t, s.Member.Id, consentId)));
            Assert.Equal("23514", ex.SqlState);
        }

        // Vuelto a consentir: el consentimiento anterior ya no sirve; el nuevo sí (y la clave debe tener el formato aleatorio).
        var current = (await GrantAsync(s.Student, s.Member.Id)).History[0].Id;
        var stale = await Assert.ThrowsAsync<PostgresException>(() => SqlAsync(t, InsertPhotoSql(t, s.Member.Id, granted)));
        Assert.Equal("23514", stale.SqlState);
        var badKey = await Assert.ThrowsAsync<PostgresException>(() => SqlAsync(t, InsertPhotoSql(t, s.Member.Id, current, key: "progress-photos/juan-perez/foto1")));
        Assert.Equal("ck_progress_photos_key", badKey.ConstraintName);
        await SqlAsync(t, InsertPhotoSql(t, s.Member.Id, current));
        Assert.Equal(1, await CountPhotosAsync(t, s.Member.Id));
    }

    // ---- Concurrencia ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Subir_fotos_mientras_se_retira_el_consentimiento_nunca_deja_fotos_ni_archivos()
    {
        var s = await NewSetupAsync("RAC");
        await GrantAsync(s.Student, s.Member.Id);

        var uploads = Enumerable.Range(0, 6).Select(i => UploadAsync(s.Student, s.Member.Id, Jpeg(fill: (byte)(0x20 + i)))).ToList();
        var revoke = s.Trainer.PostAsync($"/api/v1/members/{s.Member.Id}/photo-consent/revoke", null);
        await Task.WhenAll([.. uploads, revoke]);

        Assert.Equal(HttpStatusCode.OK, (await revoke).StatusCode);
        foreach (var upload in uploads)
        {
            var status = (await upload).StatusCode;
            Assert.True(status is HttpStatusCode.Created or HttpStatusCode.Conflict, $"Respondió {(int)status}");
        }

        // Sea cual haya sido el orden, no queda ninguna foto, ni su registro ni su archivo.
        Assert.Equal(0, await CountPhotosAsync(s.Tenant.Id, s.Member.Id));
        Assert.Empty(StoredFiles(s.Tenant.Id));
        Assert.Equal("Revoked", (await ConsentAsync(s.Student, s.Member.Id)).Status);
    }
}
