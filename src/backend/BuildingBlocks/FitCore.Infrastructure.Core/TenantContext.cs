using FitCore.Application.Core;

namespace FitCore.Infrastructure.Core;

/// <summary>Tenant activo de la unidad de trabajo actual (un valor por scope de DI).</summary>
public sealed class TenantContext : ITenantContext
{
    public Guid? TenantId { get; private set; }

    public void Set(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("El tenant no puede ser vacío.", nameof(tenantId));
        }

        TenantId = tenantId;
    }

    public void Clear() => TenantId = null;
}
