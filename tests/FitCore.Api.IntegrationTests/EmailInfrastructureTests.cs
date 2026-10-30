using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FitCore.Api.IntegrationTests.Infrastructure;
using FitCore.Application.Core;
using FitCore.Infrastructure.Core;
using FitCore.Platform;
using FitCore.Platform.Application.AccountTokens;
using FitCore.Platform.Application.Email;
using FitCore.SharedKernel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FitCore.Api.IntegrationTests;

/// <summary>Emisor de prueba: cuenta cada entrega y puede fallar a pedido. No existe en el código de producción.</summary>
internal sealed class RecordingEmailSender : IEmailSender
{
    public ConcurrentBag<EmailMessage> Sent { get; } = [];
    public Func<EmailMessage, Result> Behavior { get; set; } = _ => Result.Success();
    public bool IsConfigured { get; set; } = true;

    public Task<Result> SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        var result = Behavior(message);
        if (result.IsSuccess)
        {
            Sent.Add(message);
        }

        return Task.FromResult(result);
    }
}

/// <summary>Cola de correo con reintentos y tokens de cuenta de un solo uso, contra PostgreSQL real con el rol limitado.</summary>
[Collection(ApiCollection4.Name)]
public class EmailInfrastructureTests(ApiFixture api)
{
    private (ServiceProvider Provider, RecordingEmailSender Sender) Build()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = api.AppConnection })
            .Build();
        var sender = new RecordingEmailSender();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IClock, SystemClock>();
        services.AddPlatformPersistence(config);
        services.AddEmailQueue(config);
        services.AddSingleton<IEmailSender>(sender);
        return (services.BuildServiceProvider(), sender);
    }

    private static EmailQueueProcessor ProcessorFor(ServiceProvider provider) =>
        new(provider.GetRequiredService<IServiceScopeFactory>(), new SystemClock(), NullLogger<EmailQueueProcessor>.Instance);

    private static async Task EnqueueAsync(ServiceProvider provider, string to)
    {
        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<EmailQueue>().EnqueueAsync(
            to, EmailTemplateCodes.VerifyEmail, "es",
            new Dictionary<string, string> { ["name"] = "Ana", ["link"] = "https://fit360.test/verificar?token=abc" },
            CancellationToken.None);
    }

    /// <summary>Procesa lotes hasta que no queden correos vencidos y pendientes (la base la comparten otras pruebas de la colección).</summary>
    private static async Task DrainAsync(EmailQueueProcessor processor)
    {
        for (var i = 0; i < 20 && await processor.ProcessBatchAsync(CancellationToken.None) > 0; i++)
        {
        }
    }

    private async Task<(string Status, int Attempts, string? Text, string? Html)> RowAsync(string to)
    {
        await using var conn = new NpgsqlConnection(api.AdminConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT status, attempts, text_body, html_body FROM messaging.emails WHERE to_address = @to", conn);
        cmd.Parameters.AddWithValue("to", to);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetString(0), reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    private async Task ExecAsync(string sql, params (string Name, object Value)[] args)
    {
        await using var conn = new NpgsqlConnection(api.AdminConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in args)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        await cmd.ExecuteNonQueryAsync();
    }

    // ---- Cola de correo ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Queue_sends_the_email_and_discards_the_bodies_that_carry_the_link()
    {
        var (provider, sender) = Build();
        var to = $"ok-{Guid.NewGuid():N}@cola.test";
        await EnqueueAsync(provider, to);
        await DrainAsync(ProcessorFor(provider));

        var sent = Assert.Single(sender.Sent, m => m.To == to);
        Assert.Contains("https://fit360.test/verificar?token=abc", sent.TextBody);
        var row = await RowAsync(to);
        Assert.Equal("Sent", row.Status);
        Assert.Equal(1, row.Attempts);
        Assert.Null(row.Text);
        Assert.Null(row.Html);
    }

    [Fact]
    public async Task A_failed_delivery_is_retried_later_and_then_given_up_after_five_attempts()
    {
        var (provider, sender) = Build();
        sender.Behavior = _ => Error.Failure("email.send_failed", "rechazado");
        var to = $"fail-{Guid.NewGuid():N}@cola.test";
        await EnqueueAsync(provider, to);
        var processor = ProcessorFor(provider);

        await DrainAsync(processor);
        var first = await RowAsync(to);
        Assert.Equal("Pending", first.Status);
        Assert.Equal(1, first.Attempts);
        Assert.NotNull(first.Text); // aún se reintentará: conserva el contenido

        // El siguiente intento está en el futuro: otra vuelta inmediata no lo toca.
        await DrainAsync(processor);
        Assert.Equal(1, (await RowAsync(to)).Attempts);

        for (var attempt = 2; attempt <= 5; attempt++)
        {
            await ExecAsync("UPDATE messaging.emails SET next_attempt_at = now() - interval '1 second' WHERE to_address = @to", ("to", to));
            await DrainAsync(processor);
        }

        var last = await RowAsync(to);
        Assert.Equal("Failed", last.Status);
        Assert.Equal(5, last.Attempts);
        Assert.Null(last.Text);
        Assert.Null(last.Html);
        Assert.Empty(sender.Sent.Where(m => m.To == to));
    }

    [Fact]
    public async Task A_delivery_that_succeeds_on_retry_is_marked_sent()
    {
        var (provider, sender) = Build();
        var failures = 0;
        sender.Behavior = _ => failures++ < 1 ? Error.Failure("email.send_failed", "x") : Result.Success();
        var to = $"retry-{Guid.NewGuid():N}@cola.test";
        await EnqueueAsync(provider, to);
        var processor = ProcessorFor(provider);

        await DrainAsync(processor);
        await ExecAsync("UPDATE messaging.emails SET next_attempt_at = now() - interval '1 second' WHERE to_address = @to", ("to", to));
        await DrainAsync(processor);

        Assert.Equal("Sent", (await RowAsync(to)).Status);
        Assert.Single(sender.Sent, m => m.To == to);
    }

    [Fact]
    public async Task An_invalid_recipient_fails_at_once_without_retries()
    {
        var (provider, sender) = Build();
        sender.Behavior = _ => Error.Validation("email.invalid_recipient", "mal");
        var to = $"bad-{Guid.NewGuid():N}@cola.test";
        await EnqueueAsync(provider, to);

        await DrainAsync(ProcessorFor(provider));

        var row = await RowAsync(to);
        Assert.Equal("Failed", row.Status);
        Assert.Equal(1, row.Attempts);
    }

    [Fact]
    public async Task An_abandoned_send_is_recovered_when_its_lock_expires()
    {
        var (provider, sender) = Build();
        var to = $"stuck-{Guid.NewGuid():N}@cola.test";
        await EnqueueAsync(provider, to);
        // Simula un Worker que reclamó el correo y murió: queda «Sending» con el candado vencido.
        await ExecAsync(
            "UPDATE messaging.emails SET status = 'Sending', attempts = 1, locked_until = now() - interval '1 minute' WHERE to_address = @to",
            ("to", to));

        await DrainAsync(ProcessorFor(provider));

        Assert.Single(sender.Sent, m => m.To == to);
        Assert.Equal("Sent", (await RowAsync(to)).Status);
    }

    [Fact]
    public async Task A_send_still_locked_by_another_worker_is_not_taken()
    {
        var (provider, sender) = Build();
        var to = $"locked-{Guid.NewGuid():N}@cola.test";
        await EnqueueAsync(provider, to);
        await ExecAsync(
            "UPDATE messaging.emails SET status = 'Sending', attempts = 1, locked_until = now() + interval '5 minutes' WHERE to_address = @to",
            ("to", to));

        await DrainAsync(ProcessorFor(provider));

        Assert.Empty(sender.Sent.Where(m => m.To == to));
        Assert.Equal("Sending", (await RowAsync(to)).Status);
    }

    [Fact]
    public async Task A_stale_send_that_used_all_attempts_is_marked_failed_not_retried_forever()
    {
        var (provider, sender) = Build();
        var to = $"exhausted-{Guid.NewGuid():N}@cola.test";
        await EnqueueAsync(provider, to);
        await ExecAsync(
            "UPDATE messaging.emails SET status = 'Sending', attempts = 5, locked_until = now() - interval '1 minute' WHERE to_address = @to",
            ("to", to));

        await DrainAsync(ProcessorFor(provider));

        Assert.Empty(sender.Sent.Where(m => m.To == to));
        var row = await RowAsync(to);
        Assert.Equal("Failed", row.Status);
        Assert.Null(row.Text);
    }

    [Fact]
    public async Task Two_workers_never_send_the_same_email_twice()
    {
        var (provider, sender) = Build();
        var tag = Guid.NewGuid().ToString("N")[..8];
        var recipients = Enumerable.Range(0, 12).Select(i => $"par{i}-{tag}@cola.test").ToList();
        foreach (var to in recipients)
        {
            await EnqueueAsync(provider, to);
        }

        var a = ProcessorFor(provider);
        var b = ProcessorFor(provider);
        await Task.WhenAll(DrainAsync(a), DrainAsync(b), DrainAsync(a), DrainAsync(b));
        await DrainAsync(a);

        foreach (var to in recipients)
        {
            Assert.Single(sender.Sent, m => m.To == to);
        }
    }

    [Fact]
    public async Task Without_smtp_configured_nothing_is_sent_and_no_attempt_is_spent()
    {
        var (provider, sender) = Build();
        sender.IsConfigured = false;
        var to = $"nosmtp-{Guid.NewGuid():N}@cola.test";
        await EnqueueAsync(provider, to);

        Assert.Equal(0, await ProcessorFor(provider).ProcessBatchAsync(CancellationToken.None));

        var row = await RowAsync(to);
        Assert.Equal("Pending", row.Status);
        Assert.Equal(0, row.Attempts);
        Assert.Empty(sender.Sent.Where(m => m.To == to));
    }

    // ---- Tokens de cuenta -------------------------------------------------------------------------------------------------------

    private static async Task<T> WithTokensAsync<T>(ServiceProvider provider, Func<AccountTokenService, Task<T>> action)
    {
        await using var scope = provider.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<AccountTokenService>());
    }

    [Fact]
    public async Task A_token_works_once_and_only_for_its_purpose()
    {
        var (provider, _) = Build();
        var subject = Guid.NewGuid();
        var token = await WithTokensAsync(provider, s => s.IssueAsync(AccountTokenPurposes.VerifyEmail, subject, null, TimeSpan.FromHours(1), default));

        Assert.Null(await WithTokensAsync(provider, s => s.ConsumeAsync(AccountTokenPurposes.PasswordReset, token, default)));
        var first = await WithTokensAsync(provider, s => s.ConsumeAsync(AccountTokenPurposes.VerifyEmail, token, default));
        var second = await WithTokensAsync(provider, s => s.ConsumeAsync(AccountTokenPurposes.VerifyEmail, token, default));

        Assert.Equal(subject, first?.SubjectId);
        Assert.Null(second);
    }

    [Fact]
    public async Task An_expired_or_unknown_token_is_rejected()
    {
        var (provider, _) = Build();
        var expired = await WithTokensAsync(provider,
            s => s.IssueAsync(AccountTokenPurposes.PasswordReset, Guid.NewGuid(), null, TimeSpan.FromSeconds(-1), default));

        Assert.Null(await WithTokensAsync(provider, s => s.ConsumeAsync(AccountTokenPurposes.PasswordReset, expired, default)));
        Assert.Null(await WithTokensAsync(provider, s => s.ConsumeAsync(AccountTokenPurposes.PasswordReset, "no-existe", default)));
        Assert.Null(await WithTokensAsync(provider, s => s.ConsumeAsync(AccountTokenPurposes.PasswordReset, "", default)));
        Assert.Null(await WithTokensAsync(provider, s => s.ConsumeAsync(AccountTokenPurposes.PasswordReset, new string('a', 500), default)));
    }

    [Fact]
    public async Task Issuing_again_revokes_the_previous_token()
    {
        var (provider, _) = Build();
        var subject = Guid.NewGuid();
        var old = await WithTokensAsync(provider, s => s.IssueAsync(AccountTokenPurposes.VerifyEmail, subject, null, TimeSpan.FromHours(1), default));
        var fresh = await WithTokensAsync(provider, s => s.IssueAsync(AccountTokenPurposes.VerifyEmail, subject, null, TimeSpan.FromHours(1), default));

        Assert.Null(await WithTokensAsync(provider, s => s.ConsumeAsync(AccountTokenPurposes.VerifyEmail, old, default)));
        Assert.NotNull(await WithTokensAsync(provider, s => s.ConsumeAsync(AccountTokenPurposes.VerifyEmail, fresh, default)));
    }

    [Fact]
    public async Task A_revoked_token_is_rejected()
    {
        var (provider, _) = Build();
        var subject = Guid.NewGuid();
        var token = await WithTokensAsync(provider, s => s.IssueAsync(AccountTokenPurposes.Invitation, subject, Guid.NewGuid(), TimeSpan.FromDays(7), default));

        Assert.Equal(1, await WithTokensAsync(provider, s => s.RevokeAsync(AccountTokenPurposes.Invitation, subject, default)));

        Assert.Null(await WithTokensAsync(provider, s => s.ConsumeAsync(AccountTokenPurposes.Invitation, token, default)));
    }

    [Fact]
    public async Task Simultaneous_attempts_with_the_same_token_let_exactly_one_win()
    {
        var (provider, _) = Build();
        var token = await WithTokensAsync(provider,
            s => s.IssueAsync(AccountTokenPurposes.PasswordReset, Guid.NewGuid(), null, TimeSpan.FromHours(1), default));

        var results = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => WithTokensAsync(provider, s => s.ConsumeAsync(AccountTokenPurposes.PasswordReset, token, default))));

        Assert.Equal(1, results.Count(r => r is not null));
    }

    [Fact]
    public async Task Only_the_hash_of_the_token_is_stored()
    {
        var (provider, _) = Build();
        var subject = Guid.NewGuid();
        var token = await WithTokensAsync(provider, s => s.IssueAsync(AccountTokenPurposes.VerifyEmail, subject, null, TimeSpan.FromHours(1), default));

        await using var conn = new NpgsqlConnection(api.AdminConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT t::text FROM identity.account_tokens t WHERE subject_id = @s", conn);
        cmd.Parameters.AddWithValue("s", subject);
        var rowText = (string)(await cmd.ExecuteScalarAsync())!;

        Assert.DoesNotContain(token, rowText);
        Assert.Contains(AccountTokenService.Hash(token), rowText);
        Assert.Equal(64, AccountTokenService.Hash(token).Length);
        Assert.True(token.Length >= 43); // 32 bytes en base64url
    }

    [Fact]
    public async Task Purge_removes_only_old_finished_tokens()
    {
        var (provider, _) = Build();
        var oldSubject = Guid.NewGuid();
        var liveSubject = Guid.NewGuid();
        await WithTokensAsync(provider, s => s.IssueAsync(AccountTokenPurposes.VerifyEmail, oldSubject, null, TimeSpan.FromSeconds(-1), default));
        var live = await WithTokensAsync(provider, s => s.IssueAsync(AccountTokenPurposes.VerifyEmail, liveSubject, null, TimeSpan.FromHours(1), default));
        await ExecAsync("UPDATE identity.account_tokens SET expires_at = now() - interval '30 days' WHERE subject_id = @s", ("s", oldSubject));

        await WithTokensAsync(provider, s => s.PurgeAsync(TimeSpan.FromDays(7), default));

        await using var conn = new NpgsqlConnection(api.AdminConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM identity.account_tokens WHERE subject_id = ANY(@ids)", conn);
        cmd.Parameters.AddWithValue("ids", new[] { oldSubject, liveSubject });
        Assert.Equal(1L, await cmd.ExecuteScalarAsync());
        Assert.NotNull(await WithTokensAsync(provider, s => s.ConsumeAsync(AccountTokenPurposes.VerifyEmail, live, default)));
    }

    // ---- Plantillas -------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(EmailTemplateCodes.VerifyEmail, "es", "Confirme su correo")]
    [InlineData(EmailTemplateCodes.VerifyEmail, "en", "Confirm your email")]
    [InlineData(EmailTemplateCodes.PasswordReset, "es", "Restablezca")]
    [InlineData(EmailTemplateCodes.PasswordReset, "en", "Reset your password")]
    [InlineData(EmailTemplateCodes.Invitation, "es", "Lo invitaron")]
    [InlineData(EmailTemplateCodes.Invitation, "en", "You were invited")]
    [InlineData(EmailTemplateCodes.DemoRequestNotification, "es", "Nueva solicitud")]
    [InlineData(EmailTemplateCodes.DemoRequestNotification, "xx", "Nueva solicitud")]
    public void Templates_exist_in_both_languages_and_default_to_spanish(string template, string locale, string expectedSubject)
    {
        var content = EmailTemplates.Render(template, locale, Values());

        Assert.Contains(expectedSubject, content.Subject);
        Assert.False(string.IsNullOrWhiteSpace(content.TextBody));
        Assert.StartsWith("<!doctype html>", content.HtmlBody);
    }

    [Fact]
    public void Values_typed_by_people_cannot_inject_html_or_headers()
    {
        var values = Values();
        values["gym"] = "Gym <script>alert(1)</script>\r\nBcc: x@y.z";
        values["name"] = "<img src=x onerror=alert(1)>";
        values["message"] = "<b>hola</b>";

        var invitation = EmailTemplates.Render(EmailTemplateCodes.Invitation, "es", values);
        var verify = EmailTemplates.Render(EmailTemplateCodes.VerifyEmail, "es", values);
        var lead = EmailTemplates.Render(EmailTemplateCodes.DemoRequestNotification, "es", values);

        Assert.DoesNotContain("<script>", invitation.HtmlBody);
        Assert.DoesNotContain("<img", verify.HtmlBody);
        Assert.DoesNotContain("<b>hola</b>", lead.HtmlBody);
        Assert.DoesNotContain('\n', invitation.Subject);
        Assert.DoesNotContain('\r', invitation.Subject);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://x/y")]
    [InlineData("no-es-un-enlace")]
    public void A_link_that_is_not_http_is_refused(string link)
    {
        var values = Values();
        values["link"] = link;

        Assert.Throws<ArgumentException>(() => EmailTemplates.Render(EmailTemplateCodes.VerifyEmail, "es", values));
    }

    [Fact]
    public void Public_links_are_built_from_the_configured_site_address()
    {
        var options = new PublicSiteOptions { BaseUrl = "https://fit360.example/" };

        Assert.Equal("https://fit360.example/verificar-correo?token=a%2Bb", options.BuildLink("/verificar-correo", "a+b"));
    }

    private static Dictionary<string, string> Values() => new()
    {
        ["name"] = "Ana",
        ["link"] = "https://fit360.test/x?token=abc",
        ["gym"] = "Gym Uno",
        ["inviter"] = "Luis",
        ["kind"] = "demo",
        ["email"] = "ana@x.test",
        ["phone"] = "0999999999",
        ["message"] = "Hola"
    };

    // ---- Emisor SMTP ------------------------------------------------------------------------------------------------------------

    private static SmtpEmailSender SmtpSender(SmtpOptions options) =>
        new(Options.Create(options), NullLogger<SmtpEmailSender>.Instance);

    [Fact]
    public async Task The_smtp_sender_without_host_reports_not_configured_and_sends_nothing()
    {
        var sender = SmtpSender(new SmtpOptions());

        Assert.False(sender.IsConfigured);
        var result = await sender.SendAsync(new EmailMessage("a@b.test", "Hola", "x"), default);
        Assert.Equal("email.not_configured", result.Error.Code);
    }

    [Theory]
    [InlineData("a@b.test\r\nBcc: x@y.test", "Hola")]
    [InlineData("a@b.test", "Hola\nBcc: x@y.test")]
    public async Task The_smtp_sender_refuses_line_breaks_in_headers(string to, string subject)
    {
        var sender = SmtpSender(new SmtpOptions { Host = "localhost", From = "no-reply@fit360.test" });

        var result = await sender.SendAsync(new EmailMessage(to, subject, "x"), default);

        Assert.Equal("email.invalid_header", result.Error.Code);
    }

    [Fact]
    public async Task The_smtp_sender_refuses_an_invalid_recipient()
    {
        var sender = SmtpSender(new SmtpOptions { Host = "localhost", From = "no-reply@fit360.test" });

        var result = await sender.SendAsync(new EmailMessage("no es un correo", "Hola", "x"), default);

        Assert.Equal("email.invalid_recipient", result.Error.Code);
    }

    [Fact]
    public async Task The_smtp_sender_reports_an_unreachable_server_without_throwing()
    {
        var sender = SmtpSender(new SmtpOptions { Host = "127.0.0.1", Port = 1, Security = "None", From = "no-reply@fit360.test", TimeoutSeconds = 3 });

        var result = await sender.SendAsync(new EmailMessage("a@b.test", "Hola", "x"), default);

        Assert.Equal("email.send_failed", result.Error.Code);
    }

    [Fact]
    public void Recipients_are_masked_in_logs()
    {
        Assert.Equal("j***@correo.com", SmtpEmailSender.Mask("juan.perez@correo.com"));
        Assert.Equal("***", SmtpEmailSender.Mask("sin-arroba"));
    }

    /// <summary>Entrega REAL por SMTP a un servidor de correo de verdad (Mailpit en un contenedor) y lectura del mensaje recibido.</summary>
    [Fact]
    public async Task The_smtp_sender_delivers_a_real_message_to_a_real_mail_server()
    {
        await using var mailpit = new ContainerBuilder("axllent/mailpit:v1.21")
            .WithPortBinding(1025, true)
            .WithPortBinding(8025, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8025).ForPath("/livez")))
            .Build();
        await mailpit.StartAsync();

        var sender = SmtpSender(new SmtpOptions
        {
            Host = mailpit.Hostname,
            Port = mailpit.GetMappedPublicPort(1025),
            Security = "None",
            From = "no-reply@fit360.test",
            FromName = "FIT360"
        });
        var content = EmailTemplates.Render(EmailTemplateCodes.VerifyEmail, "es", Values());

        var result = await sender.SendAsync(new EmailMessage("ana@cliente.test", content.Subject, content.TextBody, content.HtmlBody), default);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Code : null);
        using var http = new HttpClient { BaseAddress = new Uri($"http://{mailpit.Hostname}:{mailpit.GetMappedPublicPort(8025)}") };
        var list = await http.GetFromJsonAsync<JsonElement>("/api/v1/messages");
        Assert.Equal(1, list.GetProperty("total").GetInt32());
        var message = list.GetProperty("messages")[0];
        Assert.Equal("ana@cliente.test", message.GetProperty("To")[0].GetProperty("Address").GetString());
        Assert.Equal("no-reply@fit360.test", message.GetProperty("From").GetProperty("Address").GetString());
        Assert.Contains("Confirme su correo", message.GetProperty("Subject").GetString());
        var detail = await http.GetFromJsonAsync<JsonElement>($"/api/v1/message/{message.GetProperty("ID").GetString()}");
        Assert.Contains("https://fit360.test/x?token=abc", detail.GetProperty("Text").GetString());
        Assert.Contains("https://fit360.test/x?token=abc", detail.GetProperty("HTML").GetString());
    }
}
