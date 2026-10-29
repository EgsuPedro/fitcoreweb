using FitCore.SharedKernel;

namespace FitCore.Application.Core;

/// <summary>
/// Qué se le permite hacer hoy a un gimnasio según el estado de su suscripción comercial (ADR 0012).
/// <c>Full</c>: todo lo del plan. <c>Grace</c>: mora dentro del plazo de gracia (todo sigue, con avisos).
/// <c>Restricted</c>: suspendida, vencida o cancelada — se puede entrar, consultar, exportar, registrar cobros y asistencia de alumnos y
/// regularizar la suscripción, pero no crear sucursales, usuarios ni alumnos nuevos ni modificar las funciones premium.
/// </summary>
public enum AccessMode
{
    Full = 1,
    Grace = 2,
    Restricted = 3
}

/// <summary>
/// Servicio centralizado de capacidades comerciales. Los módulos de negocio lo consultan cuando una operación depende de una funcionalidad
/// o de un límite del plan; ninguno compara nombres de plan. Es la parte amable del control (mensajes claros): la garantía bajo
/// concurrencia la dan los disparadores de PostgreSQL.
/// </summary>
public interface ICapabilityEvaluator
{
    /// <summary>¿El plan del gimnasio incluye la funcionalidad? Si <paramref name="isWrite"/> y el gimnasio está restringido, las escrituras premium se rechazan.</summary>
    Task<Result> RequireFeatureAsync(string featureCode, bool isWrite, CancellationToken cancellationToken);

    /// <summary>¿Cabe crear <paramref name="additional"/> recursos más sin pasar el límite del plan? También rechaza si el gimnasio está restringido.</summary>
    Task<Result> RequireCapacityAsync(string limitCode, int additional, CancellationToken cancellationToken);
}

/// <summary>Códigos de error de las restricciones comerciales (estables: los usa la API, el frontend y las pruebas).</summary>
public static class CapabilityErrors
{
    public const string LimitReached = "subscription.limit_reached";
    public const string FeatureUnavailable = "subscription.feature_unavailable";
    public const string Restricted = "subscription.restricted";
    public const string NoSubscription = "subscription.not_found";

    public static Error Limit(string limitName, string unit, long limit, long used, string planName) => Error.Conflict(
        LimitReached,
        $"Su plan {planName} permite {limit} {unit} ({limitName}) y ya tiene {used}. Solicite un cambio de plan para agregar más.");

    public static Error Feature(string featureName, string planName) => Error.Forbidden(
        FeatureUnavailable,
        $"Su plan {planName} no incluye «{featureName}». Solicite un cambio de plan para usarla.");

    public static Error RestrictedMode(string statusText) => Error.Forbidden(
        Restricted,
        $"Su suscripción está {statusText}: puede consultar y exportar sus datos, pero no crear nuevos recursos ni modificar esta función. Regularice su suscripción en «Mi suscripción».");

    public static readonly Error None = Error.Forbidden(
        NoSubscription, "El gimnasio no tiene una suscripción comercial vigente. Contacte a FitCore 360.");
}
