using System.Text.RegularExpressions;
using FitCore.SharedKernel;

namespace FitCore.Platform.Domain;

public enum TenantStatus
{
    Active = 1,
    Suspended = 2
}

/// <summary>Avance de la puesta en marcha del gimnasio (onboarding). Es independiente del estado administrativo y de la suscripción.</summary>
public enum TenantSetupStatus
{
    PendingSetup = 1,
    Ready = 2
}

public sealed record TenantCreated(Guid TenantId, string Slug, Guid EventId, DateTimeOffset OccurredAt)
    : DomainEvent(EventId, OccurredAt);

/// <summary>Gimnasio (empresa cliente del SaaS). Es la raíz del aislamiento multiempresa.</summary>
public sealed partial class Tenant : Entity<Guid>, IAuditable
{
    public const string PlatformSlug = "platform";
    public const string DefaultTimeZone = "America/Guayaquil";
    public const string DefaultLocale = "es-EC";

    private Tenant(Guid id)
        : base(id)
    {
    }

    public string Slug { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public TenantStatus Status { get; private set; }
    public string TimeZone { get; private set; } = DefaultTimeZone;
    public string DefaultCurrency { get; private set; } = Money.DefaultCurrency;
    public string Locale { get; private set; } = DefaultLocale;
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Los gimnasios creados por la plataforma o anteriores al onboarding nacen «Ready»; los del registro público, «PendingSetup». No bloquea el uso.</summary>
    public TenantSetupStatus SetupStatus { get; private set; } = TenantSetupStatus.Ready;

    /// <summary>Datos de contacto del gimnasio (los pide el asistente de configuración). Opcionales para los gimnasios anteriores a él.</summary>
    public string? ContactEmail { get; private set; }

    public string? ContactPhone { get; private set; }

    /// <summary>País en ISO 3166-1 alfa-2.</summary>
    public string Country { get; private set; } = "EC";

    public bool IsActive => Status == TenantStatus.Active;

    public static bool IsValidSlug(string slug) => SlugPattern().IsMatch(slug);

    public static Tenant Create(string slug, string name, string timeZone, string locale, DateTimeOffset now)
    {
        var tenant = new Tenant(Guid.CreateVersion7())
        {
            Slug = slug.Trim().ToLowerInvariant(),
            Name = name.Trim(),
            TimeZone = timeZone,
            Locale = locale,
            Status = TenantStatus.Active,
            CreatedAt = now
        };
        tenant.Raise(new TenantCreated(tenant.Id, tenant.Slug, Guid.CreateVersion7(), now));
        return tenant;
    }

    public void UpdateProfile(string name, string timeZone, string locale)
    {
        Name = name.Trim();
        TimeZone = timeZone;
        Locale = locale;
    }

    public void UpdateContact(string? contactEmail, string? contactPhone, string country)
    {
        ContactEmail = string.IsNullOrWhiteSpace(contactEmail) ? null : contactEmail.Trim();
        ContactPhone = string.IsNullOrWhiteSpace(contactPhone) ? null : contactPhone.Trim();
        Country = country.Trim().ToUpperInvariant();
    }

    public void MarkPendingSetup() => SetupStatus = TenantSetupStatus.PendingSetup;

    public void MarkReady() => SetupStatus = TenantSetupStatus.Ready;

    public void Suspend() => Status = TenantStatus.Suspended;

    public void Activate() => Status = TenantStatus.Active;

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{1,38})[a-z0-9]$")]
    private static partial Regex SlugPattern();
}
