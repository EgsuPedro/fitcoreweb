namespace FitCore.Application.Core;

/// <summary>Plan que el sitio comercial ofrece hoy. Lo que se contrata sale siempre de aquí (del servidor), nunca de lo que envía el navegador.</summary>
public sealed record OfferedPlan(string Code, string Name, string Currency, decimal Price, int TrialDays, bool SelfService);

/// <summary>
/// Catálogo de planes públicos visto desde otros módulos (el registro de gimnasios vive en Platform, que no conoce Subscriptions).
/// Solo devuelve planes publicados, activos y vendibles.
/// </summary>
public interface IPublicPlanCatalog
{
    Task<OfferedPlan?> LookupAsync(string code, CancellationToken cancellationToken);
}
