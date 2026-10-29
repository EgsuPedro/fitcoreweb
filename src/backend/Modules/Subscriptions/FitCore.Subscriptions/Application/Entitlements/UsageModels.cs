using FitCore.Application.Core;
using FitCore.SharedKernel;
using FitCore.Subscriptions.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FitCore.Subscriptions.Application.Entitlements;

public sealed record FeatureAvailabilityDto(string Code, string Name, string Description, bool Enabled);

/// <summary>Un límite del plan con su consumo. <c>Measured</c> es falso cuando el sistema no mide ese recurso (hoy, el almacenamiento).</summary>
public sealed record LimitUsageDto(string Code, string Name, string Description, string Unit, long? Limit, long? Used, long? Remaining, bool Reached, bool Measured);

public sealed record UsageDto(
    string PlanCode, string PlanName, int PlanVersion, string Status, string AccessMode,
    IReadOnlyList<FeatureAvailabilityDto> Features, IReadOnlyList<LimitUsageDto> Limits);

public sealed record GetMyUsageQuery : IQuery<UsageDto>;

internal sealed class GetMyUsageHandler(SubscriptionsDbContext db, ITenantContext tenantContext) : IQueryHandler<GetMyUsageQuery, UsageDto>
{
    public async Task<Result<UsageDto>> HandleAsync(GetMyUsageQuery query, CancellationToken cancellationToken)
    {
        if (tenantContext.TenantId is not { } tenantId)
        {
            return CapabilityErrors.None;
        }

        var entitlement = await db.Entitlements.AsNoTracking().FirstOrDefaultAsync(e => e.TenantId == tenantId, cancellationToken);
        if (entitlement is null)
        {
            return CapabilityErrors.None;
        }

        var limits = new List<LimitUsageDto>();
        foreach (var descriptor in Capabilities.LimitCatalog)
        {
            var limit = entitlement.LimitOf(descriptor.Code);
            var used = await CapabilityEvaluator.UsageAsync(db, descriptor.Code, cancellationToken);
            limits.Add(new LimitUsageDto(
                descriptor.Code, descriptor.Name, descriptor.Description, descriptor.Unit, limit, used,
                limit is { } max && used is { } u ? Math.Max(0, max - u) : null,
                limit is { } cap && used is { } now && now >= cap,
                used is not null));
        }

        return new UsageDto(
            entitlement.PlanCode, entitlement.PlanName, entitlement.PlanVersion, entitlement.Status, entitlement.AccessMode,
            [.. Capabilities.FeatureCatalog.Select(f => new FeatureAvailabilityDto(f.Code, f.Name, f.Description, entitlement.Features.Contains(f.Code)))],
            limits);
    }
}
