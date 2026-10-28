using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FitCore.Classes.Domain;
using FitCore.Classes.Persistence;
using FitCore.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace FitCore.Classes.Application.Reservations;

internal sealed record IdempotencyOutcome<T>(bool IsReplay, T? Response)
    where T : class;

/// <summary>
/// Claves de idempotencia de las reservas: la primera solicitud con una clave se ejecuta y guarda su respuesta; repetirla (un reintento
/// por corte de red o doble clic) devuelve esa respuesta sin reservar otra vez. Reusar la clave con otro contenido se rechaza.
/// Mismo patrón que pagos y caja.
/// </summary>
internal static class Idempotency
{
    public const int MaxKeyLength = 100;

    public static readonly Error KeyRequired = Error.Validation("idempotency.key_required", "Falta la cabecera Idempotency-Key (hasta 100 caracteres).");
    public static readonly Error KeyReused = Error.Validation("idempotency.key_reused", "Esa Idempotency-Key ya se usó con una solicitud distinta.");

    public static Error? Validate(string? key) =>
        string.IsNullOrWhiteSpace(key) || key.Trim().Length > MaxKeyLength ? KeyRequired : null;

    public static string Hash(object request, Guid? actor)
    {
        var json = JsonSerializer.Serialize(request, request.GetType()) + "|" + actor;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }

    /// <summary>Dentro de la transacción del llamador. Si otra solicitud con la misma clave está en curso, espera a que termine.</summary>
    public static async Task<Result<IdempotencyOutcome<T>>> BeginAsync<T>(
        ClassesDbContext db,
        Guid tenantId,
        string key,
        string scope,
        string hash,
        Guid? actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
        where T : class
    {
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO classes.idempotency_keys (tenant_id, key, scope, request_hash, actor_id, created_at)
            VALUES ({tenantId}, {key}, {scope}, {hash}, {actor}, {now})
            ON CONFLICT (tenant_id, key) DO NOTHING
            """,
            cancellationToken);
        if (inserted == 1)
        {
            return new IdempotencyOutcome<T>(false, null);
        }

        var existing = await db.IdempotencyKeys.AsNoTracking().FirstOrDefaultAsync(k => k.Key == key, cancellationToken);
        if (existing is null || existing.Scope != scope || existing.RequestHash != hash || existing.ActorId != actor || existing.ResponseBody is null)
        {
            return KeyReused;
        }

        return new IdempotencyOutcome<T>(true, JsonSerializer.Deserialize<T>(existing.ResponseBody));
    }

    public static async Task CompleteAsync<T>(ClassesDbContext db, Guid tenantId, string key, int status, T response, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(response);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE classes.idempotency_keys SET response_status = {status}, response_body = {body}::jsonb WHERE tenant_id = {tenantId} AND key = {key}",
            cancellationToken);
    }
}
