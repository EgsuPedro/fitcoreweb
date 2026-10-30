namespace FitCore.Api.Infrastructure;

internal static class RateLimitPolicies
{
    /// <summary>Iniciar sesión: el punto que un atacante usaría para adivinar contraseñas.</summary>
    public const string Login = "auth-login";

    /// <summary>Renovar y cerrar sesión: se invocan en cada recarga de página, por eso el límite es más alto.</summary>
    public const string Session = "auth-session";

    /// <summary>Webhooks del proveedor de pagos: llegan de pocas IP y a ritmo bajo; el límite frena a quien intente adivinar firmas.</summary>
    public const string Webhooks = "webhooks";

    /// <summary>Lectura pública del sitio comercial (planes): sin sesión, por eso se limita por IP.</summary>
    public const string PublicRead = "public-read";

    /// <summary>Registro, confirmación y reenvío del registro público: acciones que envían correo o crean gimnasios, con un límite más estricto.</summary>
    public const string Register = "public-register";

    /// <summary>Pedir y usar el enlace de recuperación de contraseña: envía correo y cambia credenciales, por eso va aparte del inicio de sesión.</summary>
    public const string Recovery = "auth-recovery";
}
