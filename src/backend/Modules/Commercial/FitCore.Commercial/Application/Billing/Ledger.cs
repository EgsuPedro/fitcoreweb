using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FitCore.Commercial.Domain;
using FitCore.Commercial.Persistence;
using FitCore.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace FitCore.Commercial.Application.Billing;

/// <summary>Lo cobrado y lo reservado de una obligación. El saldo nunca se guarda: siempre sale de las asignaciones.</summary>
internal readonly record struct Balance(decimal Paid, decimal Reserved)
{
    public static readonly Balance None = new(0m, 0m);
}

/// <summary>
/// Única fuente de verdad de los saldos: suma de las asignaciones de pagos <b>confirmados</b> (cobrado) y de transferencias
/// <b>pendientes de verificar</b> (reservado). Un pago revertido no cuenta en ninguno de los dos.
/// </summary>
internal static class Ledger
{
    public static async Task<Dictionary<Guid, Balance>> BalancesAsync(CommercialDbContext db, IReadOnlyCollection<Guid> obligationIds, CancellationToken cancellationToken)
    {
        if (obligationIds.Count == 0)
        {
            return [];
        }

        var rows = await (from a in db.PaymentAllocations.AsNoTracking()
                          join p in db.Payments.AsNoTracking() on a.PaymentId equals p.Id
                          where obligationIds.Contains(a.ObligationId) && p.Status != PaymentStatus.Reversed
                          select new { a.ObligationId, p.Status, a.Amount }).ToListAsync(cancellationToken);

        return rows.GroupBy(r => r.ObligationId).ToDictionary(
            g => g.Key,
            g => new Balance(
                g.Where(r => r.Status == PaymentStatus.Confirmed).Sum(r => r.Amount),
                g.Where(r => r.Status == PaymentStatus.PendingVerification).Sum(r => r.Amount)));
    }

    /// <summary>
    /// Bloquea las filas de las obligaciones (en orden de id, para que dos pagos cruzados no se interbloqueen). Quien llegue
    /// segundo espera aquí y luego ve los saldos ya actualizados: nadie puede sobreasignar ni dejar un saldo negativo.
    /// </summary>
    public static async Task LockAsync(CommercialDbContext db, IEnumerable<Guid> obligationIds, CancellationToken cancellationToken)
    {
        var ids = obligationIds.Distinct().Order().ToArray();
        if (ids.Length == 0)
        {
            return;
        }

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM billing.payment_obligations WHERE id = ANY({ids}) ORDER BY id FOR UPDATE",
            cancellationToken);
    }

    /// <summary>¿La obligación tiene algún pago vigente (pendiente o confirmado)? Si lo tiene, no se anula: se revierte el pago.</summary>
    public static Task<bool> HasLivePaymentsAsync(CommercialDbContext db, Guid obligationId, CancellationToken cancellationToken) =>
        (from a in db.PaymentAllocations
         join p in db.Payments on a.PaymentId equals p.Id
         where a.ObligationId == obligationId && p.Status != PaymentStatus.Reversed
         select a.Id).AnyAsync(cancellationToken);
}

internal sealed record IdempotencyOutcome<T>(bool IsReplay, T? Response)
    where T : class;

/// <summary>
/// Claves de idempotencia: la primera solicitud con una clave se ejecuta y guarda su respuesta; repetirla (un reintento por
/// corte de red o doble clic) devuelve esa respuesta sin duplicar el efecto. Reusar la clave con otro contenido se rechaza.
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
        CommercialDbContext db,
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
            INSERT INTO billing.idempotency_keys (tenant_id, key, scope, request_hash, actor_id, created_at)
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

    public static async Task CompleteAsync<T>(CommercialDbContext db, Guid tenantId, string key, int status, T response, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(response);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE billing.idempotency_keys SET response_status = {status}, response_body = {body}::jsonb WHERE tenant_id = {tenantId} AND key = {key}",
            cancellationToken);
    }
}
