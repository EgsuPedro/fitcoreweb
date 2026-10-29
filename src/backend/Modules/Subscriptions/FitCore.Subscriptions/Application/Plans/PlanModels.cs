using FitCore.Application.Core;
using FitCore.SharedKernel;
using FitCore.Subscriptions.Domain;
using FluentValidation;

namespace FitCore.Subscriptions.Application.Plans;

public static class SupportedCurrencies
{
    /// <summary>Monedas admitidas. Hoy el sistema opera solo en dólares (ADR 0003); agregar una es agregarla aquí.</summary>
    public static readonly IReadOnlyList<string> All = ["USD"];
}

public sealed record PlanLimitDto(string Code, long? Value);

public sealed record PlanVersionDto(
    int Number, string Currency, string BillingPeriod, decimal Price, int TrialDays, int? GraceDays,
    IReadOnlyList<string> Features, IReadOnlyList<PlanLimitDto> Limits, string? ChangeNote, DateTimeOffset CreatedAt, Guid? CreatedBy)
{
    internal static PlanVersionDto From(PlanVersion v) => new(
        v.Number, v.Currency, v.Period.ToString(), v.Price, v.TrialDays, v.GraceDays,
        [.. v.Features.Select(f => f.Code).OrderBy(c => c, StringComparer.Ordinal)],
        [.. v.Limits.OrderBy(l => l.Code, StringComparer.Ordinal).Select(l => new PlanLimitDto(l.Code, l.Value))],
        v.ChangeNote, v.CreatedAt, v.CreatedBy);
}

/// <summary>Resumen para listados: el plan con sus condiciones vigentes.</summary>
public sealed record PlanSummaryDto(
    Guid Id, string Code, string Name, string? Description, bool IsActive, PlanVersionDto Current, int VersionCount, DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt)
{
    internal static PlanSummaryDto From(Plan p) =>
        new(p.Id, p.Code, p.Name, p.Description, p.IsActive, PlanVersionDto.From(p.Current), p.Versions.Count, p.CreatedAt, p.UpdatedAt);
}

public sealed record PlanDto(
    Guid Id, string Code, string Name, string? Description, string? Rules, bool IsActive,
    PlanVersionDto Current, IReadOnlyList<PlanVersionDto> Versions, DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt)
{
    internal static PlanDto From(Plan p) => new(
        p.Id, p.Code, p.Name, p.Description, p.Rules, p.IsActive, PlanVersionDto.From(p.Current),
        [.. p.Versions.OrderByDescending(v => v.Number).Select(PlanVersionDto.From)], p.CreatedAt, p.UpdatedAt);
}

/// <summary>
/// Crea un plan con su primera versión. Todos los límites del catálogo deben indicarse (un valor nulo significa «sin límite»): un
/// límite olvidado nunca se interpreta como ilimitado.
/// </summary>
public sealed record CreatePlanRequest(
    string Code, string Name, string? Description, string? Rules, bool IsActive,
    string Currency, BillingPeriod BillingPeriod, decimal Price, int TrialDays, int? GraceDays,
    IReadOnlyList<string> Features, IReadOnlyList<PlanLimitDto> Limits, string? ChangeNote) : IPlanTermsRequest;

/// <summary>
/// Edita un plan. Nombre, descripción y reglas son descriptivos. Si cambia cualquier condición comercial (precio, moneda, periodicidad,
/// prueba, gracia, funciones o límites) se crea una versión nueva: las suscripciones existentes conservan la suya.
/// </summary>
public sealed record UpdatePlanRequest(
    string Name, string? Description, string? Rules,
    string Currency, BillingPeriod BillingPeriod, decimal Price, int TrialDays, int? GraceDays,
    IReadOnlyList<string> Features, IReadOnlyList<PlanLimitDto> Limits, string? ChangeNote) : IPlanTermsRequest;

public sealed record SetPlanStatusRequest(bool IsActive);

public sealed record ListPlansQuery(PageRequest Page, string? Search, bool? IsActive) : IQuery<PagedResult<PlanSummaryDto>>;

public sealed record GetPlanQuery(Guid Id) : IQuery<PlanDto>;

public sealed record GetPlanCatalogQuery : IQuery<PlanCatalogDto>;

public sealed record CreatePlanCommand(CreatePlanRequest Request) : ICommand<PlanDto>;

public sealed record UpdatePlanCommand(Guid Id, UpdatePlanRequest Request) : ICommand<PlanDto>;

public sealed record SetPlanStatusCommand(Guid Id, bool IsActive) : ICommand<PlanDto>;

/// <summary>Valores válidos para armar un plan: funciones, límites, periodicidades y monedas.</summary>
public sealed record PlanCatalogDto(
    IReadOnlyList<FeatureCatalogDto> Features, IReadOnlyList<LimitCatalogDto> Limits, IReadOnlyList<string> BillingPeriods, IReadOnlyList<string> Currencies);

public sealed record FeatureCatalogDto(string Code, string Name, string Description);

public sealed record LimitCatalogDto(string Code, string Name, string Description, string Unit);

internal static class PlanErrors
{
    public static readonly Error NotFound = Error.NotFound("plans.not_found", "Plan comercial no encontrado.");
    public static readonly Error CodeTaken = Error.Conflict("plans.code_taken", "Ya existe un plan comercial con ese código.");
}

/// <summary>Condiciones comerciales comunes a crear y editar (para validarlas con una sola regla).</summary>
internal interface IPlanTermsRequest
{
    string Currency { get; }
    BillingPeriod BillingPeriod { get; }
    decimal Price { get; }
    int TrialDays { get; }
    int? GraceDays { get; }
    IReadOnlyList<string> Features { get; }
    IReadOnlyList<PlanLimitDto> Limits { get; }
    string? ChangeNote { get; }
}

internal static class PlanRules
{
    public static void Terms<T>(AbstractValidator<T> v)
        where T : IPlanTermsRequest
    {
        v.RuleFor(x => x.Currency).Must(c => SupportedCurrencies.All.Contains(c)).WithMessage("La moneda no está admitida.");
        v.RuleFor(x => x.BillingPeriod).IsInEnum();
        v.RuleFor(x => x.Price).InclusiveBetween(0m, Plan.MaxPrice).PrecisionScale(12, 2, ignoreTrailingZeros: true);
        v.RuleFor(x => x.TrialDays).InclusiveBetween(0, 365);
        v.RuleFor(x => x.GraceDays).InclusiveBetween(0, 90).When(x => x.GraceDays is not null);
        v.RuleFor(x => x.Features).NotNull();
        v.RuleFor(x => x.Features).Must(f => f.All(Capabilities.IsFeature)).When(x => x.Features is not null)
            .WithMessage("Hay funcionalidades que no existen en el catálogo.");
        v.RuleFor(x => x.Features).Must(f => f.Distinct(StringComparer.Ordinal).Count() == f.Count).When(x => x.Features is not null)
            .WithMessage("Hay funcionalidades repetidas.");
        v.RuleFor(x => x.Limits).NotNull();
        v.RuleFor(x => x.Limits).Must(l => l.All(i => Capabilities.IsLimit(i.Code))).When(x => x.Limits is not null)
            .WithMessage("Hay límites que no existen en el catálogo.");
        v.RuleFor(x => x.Limits).Must(l => l.Select(i => i.Code).Distinct(StringComparer.Ordinal).Count() == l.Count).When(x => x.Limits is not null)
            .WithMessage("Hay límites repetidos.");
        v.RuleFor(x => x.Limits).Must(l => l.All(i => i.Value is null or >= 0)).When(x => x.Limits is not null)
            .WithMessage("Un límite no puede ser negativo (use vacío para «sin límite»).");
        v.RuleFor(x => x.Limits).Must(l => Capabilities.LimitCatalog.All(c => l.Any(i => i.Code == c.Code))).When(x => x.Limits is not null)
            .WithMessage("Debe indicar todos los límites del catálogo (vacío significa «sin límite»).");
        v.RuleFor(x => x.ChangeNote).MaximumLength(500);
    }

    public static PlanTerms ToTerms(string currency, BillingPeriod period, decimal price, int trialDays, int? graceDays, IReadOnlyList<string> features, IReadOnlyList<PlanLimitDto> limits) =>
        new(currency, period, price, trialDays, graceDays, features, limits.ToDictionary(l => l.Code, l => l.Value));
}

public sealed class CreatePlanRequestValidator : AbstractValidator<CreatePlanRequest>
{
    public CreatePlanRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().Must(c => Plan.IsValidCode(c.Trim().ToLowerInvariant())).WithMessage("El código usa 3 a 40 caracteres: letras minúsculas, números, guion y guion bajo.");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(120);
        RuleFor(x => x.Description).MaximumLength(1000);
        RuleFor(x => x.Rules).MaximumLength(2000);
        PlanRules.Terms(this);
    }
}

public sealed class UpdatePlanRequestValidator : AbstractValidator<UpdatePlanRequest>
{
    public UpdatePlanRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(120);
        RuleFor(x => x.Description).MaximumLength(1000);
        RuleFor(x => x.Rules).MaximumLength(2000);
        PlanRules.Terms(this);
    }
}
