namespace FitCore.Api.Infrastructure;

internal static class RateLimitPolicies
{
    /// <summary>Iniciar sesión: el punto que un atacante usaría para adivinar contraseñas.</summary>
    public const string Login = "auth-login";

    /// <summary>Renovar y cerrar sesión: se invocan en cada recarga de página, por eso el límite es más alto.</summary>
    public const string Session = "auth-session";

    /// <summary>Webhooks del proveedor de pagos: llegan de pocas IP y a ritmo bajo; el límite frena a quien intente adivinar firmas.</summary>
    public const string Webhooks = "webhooks";
}
