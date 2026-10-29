using FitCore.Application.Core;
using FitCore.SharedKernel;
using FitCore.Subscriptions.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FitCore.Subscriptions.Application.Entitlements;

/// <summary>
/// Evalúa funcionalidades y límites del gimnasio del contexto actual a partir de sus derechos vigentes (<c>tenant_entitlements</c>).
/// Un gimnasio sin derechos registrados se trata como sin suscripción (falla cerrado), salvo el gimnasio de plataforma, que no tiene plan.
/// </summary>
internal sealed class CapabilityEvaluator(SubscriptionsDbContext db, ITenantContext tenantContext) : ICapabilityEvaluator
{
    public const string PlatformSlug = "platform";

    public async Task<Result> RequireFeatureAsync(string featureCode, bool isWrite, CancellationToken cancellationToken)
    {
        var entitlement = await LoadAsync(cancellationToken);
        if (entitlement.Unrestricted)
        {
            return Result.Success();
        }

        if (entitlement.Row is not { } row)
        {
            return Result.Failure(CapabilityErrors.None);
        }

        if (!row.Features.Contains(featureCode))
        {
            return Result.Failure(CapabilityErrors.Feature(FeatureName(featureCode), row.PlanName));
        }

        return isWrite && row.AccessMode == nameof(AccessMode.Restricted)
            ? Result.Failure(CapabilityErrors.RestrictedMode(StatusText(row.Status)))
            : Result.Success();
    }

    public async Task<Result> RequireCapacityAsync(string limitCode, int additional, CancellationToken cancellationToken)
    {
        var entitlement = await LoadAsync(cancellationToken);
        if (entitlement.Unrestricted)
        {
            return Result.Success();
        }

        if (entitlement.Row is not { } row)
        {
            return Result.Failure(CapabilityErrors.None);
        }

        if (row.AccessMode == nameof(AccessMode.Restricted))
        {
            return Result.Failure(CapabilityErrors.RestrictedMode(StatusText(row.Status)));
        }

        if (row.LimitOf(limitCode) is not { } limit)
        {
            return Result.Success();
        }

        if (await UsageAsync(db, limitCode, cancellationToken) is not { } used)
        {
            return Result.Success();
        }

        if (used + additional <= limit)
        {
            return Result.Success();
        }

        var descriptor = Capabilities.LimitCatalog.First(l => l.Code == limitCode);
        return Result.Failure(CapabilityErrors.Limit(descriptor.Name.ToLowerInvariant(), descriptor.Unit, limit, used, row.PlanName));
    }

    /// <summary>Consumo actual del gimnasio del contexto para un límite. Nulo = ese límite no se mide (hoy, el almacenamiento).</summary>
    public static async Task<long?> UsageAsync(SubscriptionsDbContext db, string limitCode, CancellationToken cancellationToken) => limitCode switch
    {
        Capabilities.Limits.Branches => await db.Branches.LongCountAsync(b => b.IsActive, cancellationToken),
        Capabilities.Limits.InternalUsers => await db.Users.LongCountAsync(u => u.IsActive && u.AccountKind == "Staff", cancellationToken),
        Capabilities.Limits.Members => await db.Members.LongCountAsync(cancellationToken),
        _ => null
    };

    public static string FeatureName(string code) => Capabilities.FeatureCatalog.FirstOrDefault(f => f.Code == code)?.Name ?? code;

    public static string StatusText(string status) => status switch
    {
        "Suspended" => "suspendida",
        "Expired" => "vencida",
        "Cancelled" => "cancelada",
        _ => status
    };

    private async Task<(TenantEntitlement? Row, bool Unrestricted)> LoadAsync(CancellationToken cancellationToken)
    {
        // Sin gimnasio en el contexto (procesos del sistema) no hay nada que evaluar.
        if (tenantContext.TenantId is not { } tenantId)
        {
            return (null, true);
        }

        var row = await db.Entitlements.AsNoTracking().FirstOrDefaultAsync(e => e.TenantId == tenantId, cancellationToken);
        if (row is not null)
        {
            return (row, false);
        }

        var isPlatform = await db.Tenants.AsNoTracking().AnyAsync(t => t.Id == tenantId && t.Slug == PlatformSlug, cancellationToken);
        return (null, isPlatform);
    }
}
