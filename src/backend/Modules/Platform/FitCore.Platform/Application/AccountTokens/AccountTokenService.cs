using System.Security.Cryptography;
using System.Text;
using FitCore.Platform.Persistence;
using FitCore.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace FitCore.Platform.Application.AccountTokens;

public static class AccountTokenPurposes
{
    public const string VerifyEmail = "verify_email";
    public const string PasswordReset = "password_reset";
    public const string Invitation = "invitation";
}

/// <summary>
/// Token de cuenta de un solo uso (verificar correo, recuperar clave, aceptar invitación). En la base solo existe el SHA-256 del
/// token: quien lea la tabla no puede usarlo. Sin RLS: se consume antes de saber a qué gimnasio pertenece; el destino sale de la fila.
/// </summary>
public sealed class AccountToken
{
    public Guid Id { get; set; }
    public string Purpose { get; set; } = string.Empty;
    public Guid SubjectId { get; set; }
    public Guid? TenantId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed record ConsumedToken(Guid SubjectId, Guid? TenantId);

/// <summary>
/// Emite y consume tokens de cuenta. Los tokens son aleatorios de 32 bytes (base64url). Consumir es una sola sentencia atómica:
/// dos peticiones simultáneas con el mismo token no pueden ganar las dos.
/// </summary>
public sealed class AccountTokenService(PlatformDbContext db, IClock clock)
{
    /// <summary>Crea un token nuevo y revoca los vigentes del mismo propósito y sujeto (un reenvío invalida el correo anterior).</summary>
    public async Task<string> IssueAsync(string purpose, Guid subjectId, Guid? tenantId, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        await db.AccountTokens
            .Where(t => t.Purpose == purpose && t.SubjectId == subjectId && t.UsedAt == null && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), cancellationToken);

        var raw = Base64Url(RandomNumberGenerator.GetBytes(32));
        db.AccountTokens.Add(new AccountToken
        {
            Id = Guid.NewGuid(),
            Purpose = purpose,
            SubjectId = subjectId,
            TenantId = tenantId,
            TokenHash = Hash(raw),
            CreatedAt = now,
            ExpiresAt = now + lifetime
        });
        await db.SaveChangesAsync(cancellationToken);
        return raw;
    }

    /// <summary>Marca el token como usado y devuelve su sujeto; nulo si no existe, ya se usó, se revocó, venció o es de otro propósito.</summary>
    public async Task<ConsumedToken?> ConsumeAsync(string purpose, string rawToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rawToken) || rawToken.Length > 200)
        {
            return null;
        }

        var rows = await db.Database.SqlQuery<ConsumedRow>($"""
            UPDATE identity.account_tokens
            SET used_at = {clock.UtcNow}
            WHERE token_hash = {Hash(rawToken)} AND purpose = {purpose}
              AND used_at IS NULL AND revoked_at IS NULL AND expires_at > {clock.UtcNow}
            RETURNING subject_id, tenant_id
            """).ToListAsync(cancellationToken);
        return rows.Count == 0 ? null : new ConsumedToken(rows[0].SubjectId, rows[0].TenantId);
    }

    /// <summary>Como <see cref="ConsumeAsync"/> pero sin gastar el token: sirve para mostrar de qué es el enlace antes de que la persona decida.</summary>
    public async Task<ConsumedToken?> PeekAsync(string purpose, string rawToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rawToken) || rawToken.Length > 200)
        {
            return null;
        }

        var hash = Hash(rawToken);
        var now = clock.UtcNow;
        var token = await db.AccountTokens.AsNoTracking()
            .Where(t => t.TokenHash == hash && t.Purpose == purpose && t.UsedAt == null && t.RevokedAt == null && t.ExpiresAt > now)
            .Select(t => new ConsumedToken(t.SubjectId, t.TenantId))
            .FirstOrDefaultAsync(cancellationToken);
        return token;
    }

    /// <summary>Revoca los tokens vigentes del sujeto (por ejemplo, al cancelar una invitación).</summary>
    public Task<int> RevokeAsync(string purpose, Guid subjectId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        return db.AccountTokens
            .Where(t => t.Purpose == purpose && t.SubjectId == subjectId && t.UsedAt == null && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), cancellationToken);
    }

    /// <summary>Borra los tokens vencidos, usados o revocados hace más de <paramref name="retention"/>.</summary>
    public Task<int> PurgeAsync(TimeSpan retention, CancellationToken cancellationToken)
    {
        var limit = clock.UtcNow - retention;
        return db.AccountTokens
            .Where(t => t.ExpiresAt < limit || (t.UsedAt != null && t.UsedAt < limit) || (t.RevokedAt != null && t.RevokedAt < limit))
            .ExecuteDeleteAsync(cancellationToken);
    }

    public static string Hash(string rawToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken))).ToLowerInvariant();

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record ConsumedRow(Guid SubjectId, Guid? TenantId);
}
