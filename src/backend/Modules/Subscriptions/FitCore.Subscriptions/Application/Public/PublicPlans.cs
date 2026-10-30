using FitCore.Application.Core;
using FitCore.SharedKernel;
using FitCore.Subscriptions.Application.Portal;
using FitCore.Subscriptions.Domain;
using FitCore.Subscriptions.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FitCore.Subscriptions.Application.Public;

public sealed record PublicFeatureDto(string Code, string Name, string Description);

public sealed record PublicLimitDto(string Code, string Name, string Unit, long? Value);

/// <summary>
/// Plan tal como lo ve un visitante del sitio. Solo condiciones comerciales: sin identificadores internos, versiones, días de gracia,
/// reglas internas ni datos de otros gimnasios. El precio sale siempre de la versión vigente en el servidor.
/// </summary>
public sealed record PublicPlanDto(
    string Code, string Name, string? Description, string Currency, string BillingPeriod, decimal Price, int TrialDays,
    bool SelfService, IReadOnlyList<PublicFeatureDto> Features, IReadOnlyList<PublicLimitDto> Limits)
{
    internal static PublicPlanDto From(Plan plan)
    {
        var v = plan.Current;
        var features = v.Features.Select(f => f.Code).ToHashSet(StringComparer.Ordinal);
        return new PublicPlanDto(
            plan.Code, plan.Name, plan.Description, v.Currency, v.Period.ToString(), v.Price, v.TrialDays,
            SelfService: PublicPlanRules.AllowsSelfService(v),
            [.. Capabilities.FeatureCatalog.Where(f => features.Contains(f.Code)).Select(f => new PublicFeatureDto(f.Code, f.Name, f.Description))],
            [.. Capabilities.LimitCatalog
                .Select(c => (Catalog: c, Limit: v.Limits.FirstOrDefault(l => l.Code == c.Code)))
                .Where(x => x.Limit is not null)
                .Select(x => new PublicLimitDto(x.Catalog.Code, x.Catalog.Name, x.Catalog.Unit, x.Limit!.Value))]);
    }
}

public static class PublicPlanRules
{
    /// <summary>
    /// Solo se contrata sola (por registro) un plan con prueba gratuita o de precio 0: no hay proveedor de pagos, así que ningún registro
    /// puede activar un plan de pago. Los demás se piden por contacto con ventas.
    /// </summary>
    public static bool AllowsSelfService(PlanVersion version) => version.TrialDays > 0 || version.Price == 0m;
}

public sealed record ListPublicPlansQuery : IQuery<IReadOnlyList<PublicPlanDto>>;

public sealed record GetPublicPlanQuery(string Code) : IQuery<PublicPlanDto>;

internal static class PublicPlanErrors
{
    // Igual para «no existe», «no es público», «está inactivo» y «es el plan heredado»: no se revela cuáles planes existen.
    public static readonly Error NotFound = Error.NotFound("plans.not_found", "El plan no existe.");
}

internal static class PublicPlanStore
{
    public static IQueryable<Plan> Offered(SubscriptionsDbContext db) =>
        db.Plans.AsNoTracking().Where(p => p.IsPublic && p.IsActive && p.Code != PortalStore.LegacyPlanCode);
}

internal sealed class ListPublicPlansHandler(SubscriptionsDbContext db) : IQueryHandler<ListPublicPlansQuery, IReadOnlyList<PublicPlanDto>>
{
    public async Task<Result<IReadOnlyList<PublicPlanDto>>> HandleAsync(ListPublicPlansQuery query, CancellationToken cancellationToken)
    {
        var plans = await PublicPlanStore.Offered(db).ToListAsync(cancellationToken);
        // El orden se arma en memoria: el precio vive en la versión vigente (la de mayor número).
        IReadOnlyList<PublicPlanDto> result = [.. plans.Select(PublicPlanDto.From).OrderBy(p => p.Price).ThenBy(p => p.Name, StringComparer.Ordinal)];
        return Result.Success(result);
    }
}

internal sealed class GetPublicPlanHandler(SubscriptionsDbContext db) : IQueryHandler<GetPublicPlanQuery, PublicPlanDto>
{
    public async Task<Result<PublicPlanDto>> HandleAsync(GetPublicPlanQuery query, CancellationToken cancellationToken)
    {
        var code = (query.Code ?? string.Empty).Trim().ToLowerInvariant();
        var plan = await PublicPlanStore.Offered(db).FirstOrDefaultAsync(p => p.Code == code, cancellationToken);
        return plan is null ? PublicPlanErrors.NotFound : PublicPlanDto.From(plan);
    }
}
