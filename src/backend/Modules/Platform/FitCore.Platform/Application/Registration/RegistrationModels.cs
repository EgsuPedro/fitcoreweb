using FitCore.Application.Core;
using FitCore.Platform.Application.Tenants;
using FitCore.Platform.Domain;
using FitCore.SharedKernel;
using FluentValidation;

namespace FitCore.Platform.Application.Registration;

/// <summary>
/// Registro público de un gimnasio y su propietario. El cliente solo elige el <b>código</b> del plan: precio, moneda, periodicidad, prueba y versión salen siempre del servidor.
/// Los campos de importe existen solo para rechazarlos explícitamente si alguien intenta enviarlos.
/// </summary>
public sealed record RegisterGymRequest(
    string OwnerFullName,
    string Email,
    string Password,
    string GymName,
    string Slug,
    string PlanCode,
    string TermsVersion,
    string PrivacyVersion,
    string? Locale = "es",
    string? TimeZone = null,
    decimal? Price = null,
    decimal? Amount = null,
    string? Currency = null);

public sealed record VerifyRegistrationRequest(string Token);

public sealed record ResendVerificationRequest(string Email);

/// <summary>Respuesta única del registro y del reenvío: igual exista o no la cuenta, para no revelar qué correos están registrados.</summary>
public sealed record RegistrationAccepted(string Status = "check_email");

public sealed record VerifiedRegistrationDto(string TenantSlug, string OwnerEmail, string PlanName, int TrialDays, string SetupStatus);

public sealed record RegisterGymCommand(RegisterGymRequest Request) : ICommand<RegistrationAccepted>;

public sealed record VerifyRegistrationCommand(string Token) : ICommand<VerifiedRegistrationDto>;

public sealed record ResendVerificationCommand(string Email) : ICommand<RegistrationAccepted>;

public static class RegistrationErrors
{
    public static readonly Error EmailUnavailable = Error.Unavailable("registration.email_unavailable", "El envío de correo no está disponible por ahora. Intente más tarde.");
    public static readonly Error LegalVersionMismatch = Error.Validation("registration.legal_version_mismatch", "Los términos o la política de privacidad cambiaron. Recargue la página y acéptelos de nuevo.");
    public static readonly Error PlanUnavailable = Error.Validation("registration.plan_unavailable", "El plan elegido no está disponible.");
    public static readonly Error PlanRequiresSales = Error.Validation("registration.plan_requires_sales", "Este plan se contrata con el equipo comercial. Escríbanos desde la página de contacto.");
    public static readonly Error SlugTaken = Error.Conflict("registration.slug_taken", "Ese identificador ya está en uso. Elija otro.");
    public static readonly Error TokenInvalid = Error.Validation("registration.token_invalid", "El enlace no es válido o ya se usó. Solicite uno nuevo.");
    public static readonly Error Expired = Error.Validation("registration.expired", "El enlace venció. Solicite uno nuevo.");
    public static readonly Error TrialAlreadyUsed = Error.Conflict("registration.trial_already_used", "Este correo ya aprovechó la prueba gratuita. Escríbanos desde la página de contacto para ver las opciones.");
    public static readonly Error ProvisioningFailed = Error.Failure("registration.provisioning_failed", "No pudimos terminar de crear su gimnasio. Nuestro equipo fue avisado; escríbanos desde la página de contacto.");
}

public sealed class RegisterGymRequestValidator : AbstractValidator<RegisterGymRequest>
{
    private static readonly string[] ReservedSlugs = ["platform", "admin", "api", "www", "app", "soporte", "support"];

    public RegisterGymRequestValidator()
    {
        RuleFor(x => x.OwnerFullName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(x => x.Password).NotEmpty().MaximumLength(200);
        RuleFor(x => x.GymName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Slug)
            .NotEmpty()
            .Must(s => Tenant.IsValidSlug((s ?? string.Empty).Trim().ToLowerInvariant())).WithMessage("Use 3 a 40 caracteres: minúsculas, números y guiones.")
            .Must(s => !ReservedSlugs.Contains((s ?? string.Empty).Trim().ToLowerInvariant())).WithMessage("Ese identificador está reservado.");
        RuleFor(x => x.PlanCode).NotEmpty().MaximumLength(40);
        RuleFor(x => x.TermsVersion).NotEmpty().MaximumLength(60);
        RuleFor(x => x.PrivacyVersion).NotEmpty().MaximumLength(60);
        RuleFor(x => x.Locale).Must(l => l is null or "es" or "en").WithMessage("Idioma no soportado.");
        RuleFor(x => x.TimeZone).Must(z => z is null || SupportedValues.IsValidTimeZone(z)).WithMessage("Zona horaria no válida.");
        RuleFor(x => x.Price).Null().WithMessage("El precio lo define el servidor, no se acepta desde el navegador.");
        RuleFor(x => x.Amount).Null().WithMessage("El importe lo define el servidor, no se acepta desde el navegador.");
        RuleFor(x => x.Currency).Null().WithMessage("La moneda la define el servidor, no se acepta desde el navegador.");
    }
}

public sealed class VerifyRegistrationRequestValidator : AbstractValidator<VerifyRegistrationRequest>
{
    public VerifyRegistrationRequestValidator() => RuleFor(x => x.Token).NotEmpty().MaximumLength(200);
}

public sealed class ResendVerificationRequestValidator : AbstractValidator<ResendVerificationRequest>
{
    public ResendVerificationRequestValidator() => RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
}
