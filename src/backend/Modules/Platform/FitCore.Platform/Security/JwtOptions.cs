namespace FitCore.Platform.Security;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = "fitcore360";
    public string Audience { get; set; } = "fitcore360-clients";

    /// <summary>Secreto de firma (HS256). Mínimo 32 caracteres; viene de variables de entorno o gestor de secretos.</summary>
    public string SigningKey { get; set; } = string.Empty;

    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 14;
}
