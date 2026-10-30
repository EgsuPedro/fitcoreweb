using System.Text.Json;
using System.Text.RegularExpressions;
using FitCore.Application.Core;
using FitCore.Platform.Application.Tenants;
using FitCore.Platform.Domain;
using FitCore.Platform.Persistence;
using FitCore.Platform.Security;
using FitCore.SharedKernel;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace FitCore.Platform.Application.Onboarding;

public static class OnboardingSteps
{
    public const string Profile = "profile";
    public const string Branch = "branch";
    public const string Settings = "settings";
    public const string Team = "team";

    /// <summary>Orden del asistente. «profile» y «branch» son obligatorios; «settings» y «team» se pueden omitir.</summary>
    public static readonly IReadOnlyList<string> All = [Profile, Branch, Settings, Team];

    public static bool IsOptional(string step) => step is Settings or Team;
}

/// <summary>
/// Avance del asistente de configuración de un gimnasio. Vive en el servidor (no en el navegador): se puede cerrar sesión y retomar. Cada paso guarda cuándo se
/// hizo o se omitió; repetir un paso actualiza sus datos, nunca duplica.
/// </summary>
public sealed class TenantOnboarding : ITenantOwned
{
    public Guid TenantId { get; init; }
    public DateTimeOffset? ProfileCompletedAt { get; set; }
    public DateTimeOffset? BranchCompletedAt { get; set; }
    public DateTimeOffset? SettingsCompletedAt { get; set; }
    public DateTimeOffset? SettingsSkippedAt { get; set; }
    public DateTimeOffset? TeamCompletedAt { get; set; }
    public DateTimeOffset? TeamSkippedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DoneAt(string step) => step switch
    {
        OnboardingSteps.Profile => ProfileCompletedAt,
        OnboardingSteps.Branch => BranchCompletedAt,
        OnboardingSteps.Settings => SettingsCompletedAt,
        OnboardingSteps.Team => TeamCompletedAt,
        _ => null
    };

    public DateTimeOffset? SkippedAt(string step) => step switch
    {
        OnboardingSteps.Settings => SettingsSkippedAt,
        OnboardingSteps.Team => TeamSkippedAt,
        _ => null
    };

    public bool CanComplete => ProfileCompletedAt is not null && BranchCompletedAt is not null;
}

public sealed record OnboardingStepDto(string Code, bool Required, string Status, DateTimeOffset? At);

public sealed record OnboardingProfileDto(string Name, string? ContactEmail, string? ContactPhone, string Country, string TimeZone, string Locale, string Currency, bool HasLogo);

public sealed record DayHoursDto(int Day, bool Closed, string? Open, string? Close);

public sealed record OnboardingBranchDto(Guid Id, string Name, string? Address, string? Phone, string TimeZone, IReadOnlyList<DayHoursDto> Hours);

public sealed record OnboardingDto(
    string SetupStatus,
    bool Completed,
    string CurrentStep,
    bool CanComplete,
    IReadOnlyList<OnboardingStepDto> Steps,
    OnboardingProfileDto Profile,
    OnboardingBranchDto? Branch);

public sealed record OnboardingProfileRequest(string Name, string? ContactEmail, string? ContactPhone, string Country, string TimeZone, string Locale);

public sealed record OnboardingBranchRequest(string Name, string? Address, string? Phone, string TimeZone, IReadOnlyList<DayHoursDto> Hours);

public sealed record GetOnboardingQuery : IQuery<OnboardingDto>;

public sealed record SaveOnboardingProfileCommand(OnboardingProfileRequest Request) : ICommand<OnboardingDto>;

public sealed record SaveOnboardingBranchCommand(OnboardingBranchRequest Request) : ICommand<OnboardingDto>;

/// <summary>Marca un paso opcional como revisado («settings») o resuelto («team»), o lo omite.</summary>
public sealed record ResolveOnboardingStepCommand(string Step, bool Skip) : ICommand<OnboardingDto>;

public sealed record CompleteOnboardingCommand : ICommand<OnboardingDto>;

public static class OnboardingErrors
{
    public static readonly Error UnknownStep = Error.Validation("onboarding.unknown_step", "Ese paso no existe o no se puede resolver así.");
    public static readonly Error NotReady = Error.Conflict("onboarding.incomplete", "Faltan pasos obligatorios: los datos del gimnasio y la sucursal principal.");
}

public sealed class OnboardingProfileRequestValidator : AbstractValidator<OnboardingProfileRequest>
{
    public OnboardingProfileRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.ContactEmail).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(x => x.ContactPhone).Matches(OnboardingRules.PhonePattern).When(x => !string.IsNullOrWhiteSpace(x.ContactPhone)).WithMessage("Teléfono no válido.");
        RuleFor(x => x.Country).NotEmpty().Matches("^[A-Za-z]{2}$").WithMessage("Use el código de país de 2 letras.");
        RuleFor(x => x.TimeZone).NotEmpty().Must(SupportedValues.IsValidTimeZone).WithMessage("Zona horaria no válida.");
        RuleFor(x => x.Locale).Must(l => SupportedValues.Locales.Contains(l)).WithMessage("Idioma no soportado.");
    }
}

public sealed class OnboardingBranchRequestValidator : AbstractValidator<OnboardingBranchRequest>
{
    public OnboardingBranchRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Address).NotEmpty().MaximumLength(300);
        RuleFor(x => x.Phone).Matches(OnboardingRules.PhonePattern).When(x => !string.IsNullOrWhiteSpace(x.Phone)).WithMessage("Teléfono no válido.");
        RuleFor(x => x.TimeZone).NotEmpty().Must(SupportedValues.IsValidTimeZone).WithMessage("Zona horaria no válida.");
        RuleFor(x => x.Hours).NotNull().Must(OnboardingRules.ValidHours).WithMessage("El horario no es válido: un día una sola vez, de 0 (domingo) a 6, con apertura anterior al cierre (HH:mm).");
    }
}

public static partial class OnboardingRules
{
    public const string PhonePattern = @"^[+0-9][0-9 ()\-]{6,28}$";

    private static readonly Regex TimePattern = TimeRegex();

    public static bool ValidHours(IReadOnlyList<DayHoursDto>? hours)
    {
        if (hours is null || hours.Count > 7 || hours.Select(h => h.Day).Distinct().Count() != hours.Count)
        {
            return false;
        }

        return hours.All(h => h.Day is >= 0 and <= 6
            && (h.Closed || (h.Open is not null && h.Close is not null && TimePattern.IsMatch(h.Open) && TimePattern.IsMatch(h.Close)
                             && string.CompareOrdinal(h.Open, h.Close) < 0)));
    }

    /// <summary>Normaliza el horario (orden por día, cerrados sin horas) para guardarlo; nulo si no hay ninguno.</summary>
    public static string? SerializeHours(IReadOnlyList<DayHoursDto> hours) =>
        hours.Count == 0
            ? null
            : JsonSerializer.Serialize(hours.OrderBy(h => h.Day).Select(h => h.Closed ? new DayHoursDto(h.Day, true, null, null) : new DayHoursDto(h.Day, false, h.Open, h.Close)), JsonSerializerOptions.Web);

    public static IReadOnlyList<DayHoursDto> ParseHours(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<DayHoursDto>>(json, JsonSerializerOptions.Web) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    [GeneratedRegex("^([01][0-9]|2[0-3]):[0-5][0-9]$")]
    private static partial Regex TimeRegex();
}

/// <summary>Carga, crea y arma el estado del asistente para el gimnasio del token.</summary>
public sealed class OnboardingStore(PlatformDbContext db, IClock clock)
{
    public async Task<(Tenant Tenant, TenantOnboarding Row)> LoadAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var tenant = await db.Tenants.FirstAsync(t => t.Id == tenantId, cancellationToken);
        var row = await db.Onboardings.FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            // Un gimnasio anterior al asistente (ya «Ready») nace con el asistente cerrado: no se le exige nada.
            row = new TenantOnboarding { TenantId = tenantId, UpdatedAt = clock.UtcNow, CompletedAt = tenant.SetupStatus == TenantSetupStatus.Ready ? clock.UtcNow : null };
            db.Onboardings.Add(row);
            await db.SaveChangesAsync(cancellationToken);
        }

        return (tenant, row);
    }

    public async Task<Branch?> MainBranchAsync(CancellationToken cancellationToken) =>
        await db.Branches.OrderBy(b => b.CreatedAt).ThenBy(b => b.Code).FirstOrDefaultAsync(cancellationToken);

    public async Task<OnboardingDto> BuildAsync(Tenant tenant, TenantOnboarding row, CancellationToken cancellationToken)
    {
        var branch = await MainBranchAsync(cancellationToken);
        var hasLogo = await db.TenantLogos.AnyAsync(cancellationToken);
        var steps = OnboardingSteps.All.Select(code =>
        {
            var done = row.DoneAt(code);
            var skipped = row.SkippedAt(code);
            return new OnboardingStepDto(code, !OnboardingSteps.IsOptional(code), done is not null ? "Done" : skipped is not null ? "Skipped" : "Pending", done ?? skipped);
        }).ToList();
        var current = row.CompletedAt is not null
            ? "finish"
            : steps.FirstOrDefault(s => s.Status == "Pending")?.Code ?? "finish";

        return new OnboardingDto(
            tenant.SetupStatus.ToString(),
            row.CompletedAt is not null,
            current,
            row.CanComplete,
            steps,
            new OnboardingProfileDto(tenant.Name, tenant.ContactEmail, tenant.ContactPhone, tenant.Country, tenant.TimeZone, tenant.Locale, tenant.DefaultCurrency, hasLogo),
            branch is null ? null : new OnboardingBranchDto(branch.Id, branch.Name, branch.Address, branch.Phone, branch.TimeZone, OnboardingRules.ParseHours(branch.Hours)));
    }
}

internal sealed class GetOnboardingHandler(IAccessContext access, OnboardingStore store) : IQueryHandler<GetOnboardingQuery, OnboardingDto>
{
    public async Task<Result<OnboardingDto>> HandleAsync(GetOnboardingQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var (tenant, row) = await store.LoadAsync(actor.Value.TenantId, cancellationToken);
        return await store.BuildAsync(tenant, row, cancellationToken);
    }
}

internal sealed class SaveOnboardingProfileHandler(
    PlatformDbContext db, IAccessContext access, OnboardingStore store, SecurityAudit audit, IClock clock)
    : ICommandHandler<SaveOnboardingProfileCommand, OnboardingDto>
{
    public async Task<Result<OnboardingDto>> HandleAsync(SaveOnboardingProfileCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var (tenant, row) = await store.LoadAsync(actor.Value.TenantId, cancellationToken);
        var r = command.Request;
        tenant.UpdateProfile(r.Name, r.TimeZone, r.Locale);
        tenant.UpdateContact(r.ContactEmail, r.ContactPhone, r.Country);

        var now = clock.UtcNow;
        row.ProfileCompletedAt ??= now;
        row.UpdatedAt = now;
        row.UpdatedBy = actor.Value.UserId;
        audit.Record("Onboarding.ProfileSaved", nameof(Tenant), tenant.Id.ToString(), actor.Value.UserId);
        await db.SaveChangesAsync(cancellationToken);
        return await store.BuildAsync(tenant, row, cancellationToken);
    }
}

internal sealed class SaveOnboardingBranchHandler(
    PlatformDbContext db, IAccessContext access, OnboardingStore store, SecurityAudit audit, IClock clock, ICapabilityEvaluator capabilities)
    : ICommandHandler<SaveOnboardingBranchCommand, OnboardingDto>
{
    private const string MainCode = "PRINCIPAL";

    public async Task<Result<OnboardingDto>> HandleAsync(SaveOnboardingBranchCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        if (!actor.Value.AllBranches)
        {
            return PlatformErrors.AllBranchesRequired;
        }

        var (tenant, row) = await store.LoadAsync(actor.Value.TenantId, cancellationToken);
        var r = command.Request;
        var hours = OnboardingRules.SerializeHours(r.Hours);
        var now = clock.UtcNow;

        // La sucursal principal es la primera del gimnasio. Repetir el paso la actualiza; solo se crea si todavía no hay ninguna.
        var branch = await store.MainBranchAsync(cancellationToken);
        if (branch is null)
        {
            var capacity = await capabilities.RequireCapacityAsync(FitCore.SharedKernel.Capabilities.Limits.Branches, 1, cancellationToken);
            if (capacity.IsFailure)
            {
                return capacity.Error;
            }

            branch = Branch.Create(tenant.Id, MainCode, r.Name, r.TimeZone, null, r.Address, now);
            db.Branches.Add(branch);
        }
        else
        {
            branch.Update(r.Name, r.TimeZone, branch.SriEstablishmentCode, r.Address, branch.IsActive);
        }

        branch.UpdateContact(r.Phone, hours);
        row.BranchCompletedAt ??= now;
        row.UpdatedAt = now;
        row.UpdatedBy = actor.Value.UserId;
        audit.Record("Onboarding.BranchSaved", nameof(Branch), branch.Id.ToString(), actor.Value.UserId);
        await db.SaveChangesAsync(cancellationToken);
        return await store.BuildAsync(tenant, row, cancellationToken);
    }
}

internal sealed class ResolveOnboardingStepHandler(
    PlatformDbContext db, IAccessContext access, OnboardingStore store, SecurityAudit audit, IClock clock)
    : ICommandHandler<ResolveOnboardingStepCommand, OnboardingDto>
{
    public async Task<Result<OnboardingDto>> HandleAsync(ResolveOnboardingStepCommand command, CancellationToken cancellationToken)
    {
        if (!OnboardingSteps.IsOptional(command.Step))
        {
            return OnboardingErrors.UnknownStep;
        }

        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var (tenant, row) = await store.LoadAsync(actor.Value.TenantId, cancellationToken);
        var now = clock.UtcNow;
        if (command.Step == OnboardingSteps.Settings)
        {
            if (command.Skip) { row.SettingsSkippedAt ??= now; } else { row.SettingsCompletedAt ??= now; }
        }
        else if (command.Skip)
        {
            row.TeamSkippedAt ??= now;
        }
        else
        {
            row.TeamCompletedAt ??= now;
        }

        row.UpdatedAt = now;
        row.UpdatedBy = actor.Value.UserId;
        audit.Record(command.Skip ? "Onboarding.StepSkipped" : "Onboarding.StepCompleted", nameof(TenantOnboarding), command.Step, actor.Value.UserId);
        await db.SaveChangesAsync(cancellationToken);
        return await store.BuildAsync(tenant, row, cancellationToken);
    }
}

internal sealed class CompleteOnboardingHandler(
    PlatformDbContext db, IAccessContext access, OnboardingStore store, SecurityAudit audit, IClock clock)
    : ICommandHandler<CompleteOnboardingCommand, OnboardingDto>
{
    public async Task<Result<OnboardingDto>> HandleAsync(CompleteOnboardingCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var (tenant, row) = await store.LoadAsync(actor.Value.TenantId, cancellationToken);
        if (row.CompletedAt is null)
        {
            if (!row.CanComplete)
            {
                return OnboardingErrors.NotReady;
            }

            // Lo opcional que no se resolvió queda como omitido: se puede hacer después desde sus pantallas normales.
            var now = clock.UtcNow;
            if (row.SettingsCompletedAt is null) { row.SettingsSkippedAt ??= now; }
            if (row.TeamCompletedAt is null) { row.TeamSkippedAt ??= now; }
            row.CompletedAt = now;
            row.UpdatedAt = now;
            row.UpdatedBy = actor.Value.UserId;
            tenant.MarkReady();
            audit.Record("Onboarding.Completed", nameof(Tenant), tenant.Id.ToString(), actor.Value.UserId);
            await db.SaveChangesAsync(cancellationToken);
        }

        return await store.BuildAsync(tenant, row, cancellationToken);
    }
}
