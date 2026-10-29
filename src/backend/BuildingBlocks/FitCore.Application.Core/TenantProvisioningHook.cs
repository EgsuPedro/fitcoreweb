using FitCore.SharedKernel;

namespace FitCore.Application.Core;

/// <summary>Plan comercial elegido al dar de alta un gimnasio. Sin código se asigna el plan por defecto de la plataforma.</summary>
public sealed record TenantPlanSelection(string? PlanCode, bool StartTrial);

/// <summary>
/// Punto de extensión del alta de gimnasios: otros módulos (hoy, suscripciones) validan la elección antes de crear nada y reaccionan
/// cuando el gimnasio ya existe, sin que Platform conozca sus tipos.
/// </summary>
public interface ITenantProvisioningHook
{
    /// <summary>Se llama antes de crear el gimnasio; si falla, no se crea nada.</summary>
    Task<Result> ValidateAsync(TenantPlanSelection selection, CancellationToken cancellationToken);

    /// <summary>Se llama con el gimnasio ya creado (el contexto sigue siendo el del equipo de plataforma).</summary>
    Task<Result> OnProvisionedAsync(Guid tenantId, TenantPlanSelection selection, CancellationToken cancellationToken);
}
