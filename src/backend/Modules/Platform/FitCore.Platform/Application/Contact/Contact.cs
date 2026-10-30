using FitCore.Application.Core;
using FitCore.Platform.Application.Email;
using FitCore.Platform.Application.Registration;
using FitCore.Platform.Persistence;
using FitCore.SharedKernel;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FitCore.Platform.Application.Contact;

public static class ContactKinds
{
    public const string Contact = "Contact";
    public const string Demo = "Demo";
}

public static class ContactStatus
{
    public const string New = "New";
    public const string Attended = "Attended";
}

/// <summary>Destino del aviso al equipo comercial (sección <c>Contact</c>). Sin correo configurado las solicitudes quedan solo en la pantalla de plataforma.</summary>
public sealed class ContactOptions
{
    public const string SectionName = "Contact";

    public string NotifyEmail { get; set; } = string.Empty;
}

/// <summary>
/// Solicitud del formulario de contacto o de demostración del sitio público. Guarda lo mínimo (nombre, correo, teléfono y gimnasio opcionales, mensaje) y la versión de la
/// política de privacidad aceptada. No guarda IP ni agente de navegador. Se elimina a los 180 días. Sin RLS: no pertenece a ningún gimnasio.
/// </summary>
public sealed class ContactRequest
{
    public Guid Id { get; set; }
    public string Kind { get; set; } = ContactKinds.Contact;
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string EmailNormalized { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string? GymName { get; set; }
    public string Message { get; set; } = string.Empty;
    public string PrivacyVersion { get; set; } = string.Empty;
    public string Status { get; set; } = ContactStatus.New;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? AttendedAt { get; set; }
    public Guid? AttendedBy { get; set; }
}

/// <summary>Cuerpo del formulario. <c>Website</c> es una trampa para robots: una persona nunca lo ve ni lo llena.</summary>
public sealed record ContactRequestBody(
    string Name,
    string Email,
    string? Phone,
    string? GymName,
    string Message,
    bool AcceptPrivacy,
    string PrivacyVersion,
    string? Website = null,
    string? Locale = "es");

public sealed record ContactReceived(string Status = "received");

public sealed record ContactRequestDto(
    Guid Id, string Kind, string Name, string Email, string? Phone, string? GymName, string Message, string Status, DateTimeOffset CreatedAt, DateTimeOffset? AttendedAt)
{
    internal static ContactRequestDto From(ContactRequest r) =>
        new(r.Id, r.Kind, r.Name, r.Email, r.Phone, r.GymName, r.Message, r.Status, r.CreatedAt, r.AttendedAt);
}

public sealed record SetContactAttendedRequest(bool Attended);

public sealed record SubmitContactCommand(string Kind, ContactRequestBody Body) : ICommand<ContactReceived>;

public sealed record ListContactRequestsQuery(PageRequest Page, string? Status, string? Kind) : IQuery<PagedResult<ContactRequestDto>>;

public sealed record SetContactAttendedCommand(Guid Id, bool Attended) : ICommand<ContactRequestDto>;

public static class ContactErrors
{
    public static readonly Error NotFound = Error.NotFound("contact.not_found", "Solicitud no encontrada.");
}

public sealed class ContactRequestBodyValidator : AbstractValidator<ContactRequestBody>
{
    public ContactRequestBodyValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(120);
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(x => x.Phone).Matches(@"^[+0-9][0-9 ()\-]{6,28}$").When(x => !string.IsNullOrWhiteSpace(x.Phone)).WithMessage("Teléfono no válido.");
        RuleFor(x => x.GymName).MaximumLength(200);
        RuleFor(x => x.Message).NotEmpty().MinimumLength(10).MaximumLength(1000);
        RuleFor(x => x.AcceptPrivacy).Equal(true).WithMessage("Debe aceptar la política de privacidad para enviar el formulario.");
        RuleFor(x => x.PrivacyVersion).NotEmpty().MaximumLength(60);
        RuleFor(x => x.Locale).Must(l => l is null or "es" or "en").WithMessage("Idioma no soportado.");
    }
}

internal sealed class SubmitContactHandler(
    PlatformDbContext db,
    IOptions<LegalOptions> legal,
    IOptions<ContactOptions> contact,
    IEmailSender sender,
    EmailQueue queue,
    IClock clock) : ICommandHandler<SubmitContactCommand, ContactReceived>
{
    private const int MaxPerEmailPerHour = 3;

    public async Task<Result<ContactReceived>> HandleAsync(SubmitContactCommand command, CancellationToken cancellationToken)
    {
        var body = command.Body;
        var received = new ContactReceived();

        // Robot: el campo trampa viene lleno. Se responde igual y no se guarda nada.
        if (!string.IsNullOrWhiteSpace(body.Website))
        {
            return received;
        }

        if (body.PrivacyVersion != legal.Value.PrivacyVersion)
        {
            return RegistrationErrors.LegalVersionMismatch;
        }

        var now = clock.UtcNow;
        var normalized = EmailNormalizer.Normalize(body.Email);

        // Demasiados mensajes del mismo correo en una hora: respuesta igual, sin guardar (frena el relleno masivo y no revela nada).
        if (await db.ContactRequests.CountAsync(r => r.EmailNormalized == normalized && r.CreatedAt > now.AddHours(-1), cancellationToken) >= MaxPerEmailPerHour)
        {
            return received;
        }

        var request = new ContactRequest
        {
            Id = Guid.CreateVersion7(),
            Kind = command.Kind,
            Name = body.Name.Trim(),
            Email = body.Email.Trim(),
            EmailNormalized = normalized,
            Phone = string.IsNullOrWhiteSpace(body.Phone) ? null : body.Phone.Trim(),
            GymName = string.IsNullOrWhiteSpace(body.GymName) ? null : body.GymName.Trim(),
            Message = body.Message.Trim(),
            PrivacyVersion = body.PrivacyVersion,
            CreatedAt = now
        };
        db.ContactRequests.Add(request);
        await db.SaveChangesAsync(cancellationToken);

        // Aviso al equipo comercial solo si hay destinatario y correo configurados; si no, la solicitud queda en la pantalla de plataforma.
        if (!string.IsNullOrWhiteSpace(contact.Value.NotifyEmail) && sender.IsConfigured)
        {
            await queue.EnqueueAsync(
                contact.Value.NotifyEmail.Trim(), EmailTemplateCodes.DemoRequestNotification, "es",
                new Dictionary<string, string>
                {
                    ["kind"] = request.Kind == ContactKinds.Demo ? "Demostración" : "Contacto",
                    ["name"] = request.Name,
                    ["email"] = request.Email,
                    ["phone"] = request.Phone ?? "—",
                    ["gym"] = request.GymName ?? "—",
                    ["message"] = request.Message
                },
                cancellationToken);
        }

        return received;
    }
}

internal sealed class ListContactRequestsHandler(PlatformDbContext db) : IQueryHandler<ListContactRequestsQuery, PagedResult<ContactRequestDto>>
{
    public async Task<Result<PagedResult<ContactRequestDto>>> HandleAsync(ListContactRequestsQuery query, CancellationToken cancellationToken)
    {
        var requests = db.ContactRequests.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            requests = requests.Where(r => r.Status == query.Status);
        }

        if (!string.IsNullOrWhiteSpace(query.Kind))
        {
            requests = requests.Where(r => r.Kind == query.Kind);
        }

        var paged = await requests.OrderByDescending(r => r.CreatedAt).ToPagedAsync(query.Page, cancellationToken);
        return new PagedResult<ContactRequestDto>([.. paged.Items.Select(ContactRequestDto.From)], paged.Page, paged.PageSize, paged.TotalCount);
    }
}

internal sealed class SetContactAttendedHandler(PlatformDbContext db, ICurrentUser currentUser, IClock clock) : ICommandHandler<SetContactAttendedCommand, ContactRequestDto>
{
    public async Task<Result<ContactRequestDto>> HandleAsync(SetContactAttendedCommand command, CancellationToken cancellationToken)
    {
        var request = await db.ContactRequests.FirstOrDefaultAsync(r => r.Id == command.Id, cancellationToken);
        if (request is null)
        {
            return ContactErrors.NotFound;
        }

        request.Status = command.Attended ? ContactStatus.Attended : ContactStatus.New;
        request.AttendedAt = command.Attended ? clock.UtcNow : null;
        request.AttendedBy = command.Attended ? currentUser.UserId : null;
        await db.SaveChangesAsync(cancellationToken);
        return ContactRequestDto.From(request);
    }
}

/// <summary>Elimina las solicitudes con más de 180 días (lo ejecuta el Worker).</summary>
public static class ContactMaintenance
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(180);

    public static Task<int> PurgeAsync(PlatformDbContext db, IClock clock, CancellationToken cancellationToken)
    {
        var limit = clock.UtcNow - Retention;
        return db.ContactRequests.Where(r => r.CreatedAt < limit).ExecuteDeleteAsync(cancellationToken);
    }
}
