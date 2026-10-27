using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using FitCore.Platform.Identity;
using FitCore.SharedKernel;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace FitCore.Platform.Security;

public static class ClaimNames
{
    public const string Subject = "sub";
    public const string TenantId = "tid";
    public const string Email = "email";
    public const string Name = "name";
    public const string TokenId = "jti";
}

public sealed record AccessToken(string Value, int ExpiresInSeconds);

public sealed record NewRefreshToken(string Value, string Hash, DateTimeOffset ExpiresAt);

public interface ITokenService
{
    AccessToken CreateAccessToken(AppUser user);

    NewRefreshToken CreateRefreshToken(Guid tenantId);

    string HashRefreshToken(string secret);

    /// <summary>El token de refresh tiene la forma <c>{tenantId}.{secreto}</c>: el prefijo permite activar RLS antes de buscarlo.</summary>
    bool TryParseRefreshToken(string? token, out Guid tenantId, out string secret);
}

internal sealed class TokenService(IOptions<JwtOptions> options, IClock clock) : ITokenService
{
    private readonly JwtOptions _options = options.Value;

    public AccessToken CreateAccessToken(AppUser user)
    {
        var now = clock.UtcNow.UtcDateTime;
        var lifetime = TimeSpan.FromMinutes(_options.AccessTokenMinutes);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.Add(lifetime),
            Subject = new ClaimsIdentity(
            [
                new Claim(ClaimNames.Subject, user.Id.ToString()),
                new Claim(ClaimNames.TenantId, user.TenantId.ToString()),
                new Claim(ClaimNames.Email, user.Email ?? string.Empty),
                new Claim(ClaimNames.Name, user.FullName),
                new Claim(ClaimNames.TokenId, Guid.NewGuid().ToString())
            ]),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey)),
                SecurityAlgorithms.HmacSha256)
        };

        return new AccessToken(new JsonWebTokenHandler().CreateToken(descriptor), (int)lifetime.TotalSeconds);
    }

    public NewRefreshToken CreateRefreshToken(Guid tenantId)
    {
        var secret = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        var value = $"{tenantId:N}.{secret}";
        return new NewRefreshToken(value, HashRefreshToken(secret), clock.UtcNow.AddDays(_options.RefreshTokenDays));
    }

    public string HashRefreshToken(string secret) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    public bool TryParseRefreshToken(string? token, out Guid tenantId, out string secret)
    {
        tenantId = Guid.Empty;
        secret = string.Empty;

        var parts = token?.Split('.');
        if (parts is not { Length: 2 } || parts[1].Length == 0)
        {
            return false;
        }

        if (!Guid.TryParseExact(parts[0], "N", out tenantId))
        {
            return false;
        }

        secret = parts[1];
        return true;
    }
}
