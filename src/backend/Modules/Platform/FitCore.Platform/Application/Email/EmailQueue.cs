using FitCore.Platform.Persistence;
using FitCore.SharedKernel;

namespace FitCore.Platform.Application.Email;

/// <summary>Dirección pública del sitio (sección <c>PublicSite</c>): con ella se arman los enlaces de los correos.</summary>
public sealed class PublicSiteOptions
{
    public const string SectionName = "PublicSite";

    public string BaseUrl { get; set; } = "http://localhost:8081";

    /// <summary>Arma un enlace absoluto a una ruta del sitio con un parámetro <c>token</c> (en la consulta).</summary>
    public string BuildLink(string path, string token) =>
        $"{BaseUrl.TrimEnd('/')}/{path.TrimStart('/')}?token={Uri.EscapeDataString(token)}";
}

/// <summary>Pone correos en la cola. El envío real lo hace el Worker (<see cref="EmailQueueProcessor"/>) con reintentos.</summary>
public sealed class EmailQueue(PlatformDbContext db, IClock clock)
{
    public async Task EnqueueAsync(
        string to, string template, string? locale, IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken)
    {
        var content = EmailTemplates.Render(template, locale, values);
        var now = clock.UtcNow;
        db.QueuedEmails.Add(new QueuedEmail
        {
            Id = Guid.NewGuid(),
            ToAddress = to.Trim(),
            Template = template,
            Subject = content.Subject,
            TextBody = content.TextBody,
            HtmlBody = content.HtmlBody,
            Status = EmailStatus.Pending,
            NextAttemptAt = now,
            CreatedAt = now
        });
        await db.SaveChangesAsync(cancellationToken);
    }
}
