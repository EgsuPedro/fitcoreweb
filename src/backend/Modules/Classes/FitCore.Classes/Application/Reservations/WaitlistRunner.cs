using FitCore.Application.Core;
using FitCore.Classes.Domain;
using FitCore.Classes.Persistence;
using FitCore.Platform.Domain;
using FitCore.Platform.Persistence;
using FitCore.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FitCore.Classes.Application.Reservations;

/// <param name="OffersExpired">Invitaciones que vencieron sin respuesta.</param>
/// <param name="SeatsOffered">Cupos ofrecidos a la fila en el barrido (los de las invitaciones vencidas ya se ofrecieron al vencer).</param>
/// <param name="WaitlistsClosed">Reservas en espera u ofrecidas de sesiones que ya empezaron: vencen, porque ya no hay a qué entrar.</param>
public sealed record WaitlistRunSummary(int Tenants, int OffersExpired, int SeatsOffered, int WaitlistsClosed)
{
    public static readonly WaitlistRunSummary Empty = new(0, 0, 0, 0);

    public WaitlistRunSummary Add(WaitlistRunSummary other) =>
        new(Tenants + other.Tenants, OffersExpired + other.OffersExpired, SeatsOffered + other.SeatsOffered, WaitlistsClosed + other.WaitlistsClosed);
}

/// <summary>
/// Mantenimiento de la lista de espera que ejecuta el Worker. Es una red de seguridad además de lo que ya hacen las solicitudes: vence las
/// invitaciones cuyo plazo terminó (y ofrece el cupo al siguiente), completa ofertas que no se pudieron hacer en el momento (por ejemplo, un
/// alumno en espera que ya pagó su cuota) y cierra la fila de las sesiones que empezaron. Cada gimnasio se procesa por separado, con su
/// propio contexto de tenant, y un fallo en uno no frena a los demás.
/// </summary>
public sealed class WaitlistRunner(IServiceScopeFactory scopes, IClock clock, ILogger<WaitlistRunner> logger)
{
    private const int BatchSize = 200;

    public async Task<WaitlistRunSummary> RunAsync(CancellationToken cancellationToken)
    {
        List<Guid> tenants;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var platform = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            tenants = await platform.Tenants.AsNoTracking()
                .Where(t => t.Status == TenantStatus.Active && t.Slug != Tenant.PlatformSlug)
                .Select(t => t.Id)
                .ToListAsync(cancellationToken);
        }

        var total = WaitlistRunSummary.Empty;
        foreach (var tenantId in tenants)
        {
            try
            {
                total = total.Add(await RunTenantAsync(tenantId, cancellationToken));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Falló el mantenimiento de la lista de espera del gimnasio {TenantId}", tenantId);
            }
        }

        return total with { Tenants = tenants.Count };
    }

    private async Task<WaitlistRunSummary> RunTenantAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<ClassesDbContext>();
        var now = clock.UtcNow;

        // 1. Invitaciones vencidas: cada una libera su cupo y se ofrece al siguiente elegible.
        var expiredIds = await db.Reservations.AsNoTracking()
            .Where(r => r.Status == ReservationStatus.Offered && r.OfferExpiresAt <= now)
            .OrderBy(r => r.OfferExpiresAt)
            .Select(r => r.Id)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);
        var expired = 0;
        foreach (var id in expiredIds)
        {
            if (await Waitlist.ExpireOfferAsync(db, clock, id, cancellationToken))
            {
                expired++;
            }
        }

        // 2. Sesiones con gente esperando y cupo libre (lo que no se pudo ofrecer en su momento).
        var sessionIds = await db.Reservations.AsNoTracking()
            .Where(r => r.Status == ReservationStatus.Waitlisted && db.ClassSessions.Any(s => s.Id == r.SessionId && s.Status == SessionStatus.Scheduled && s.StartsAt > now))
            .Select(r => r.SessionId)
            .Distinct()
            .Take(BatchSize)
            .ToListAsync(cancellationToken);
        var offered = 0;
        foreach (var sessionId in sessionIds)
        {
            offered += await Waitlist.PromoteSessionAsync(db, clock, sessionId, null, cancellationToken);
        }

        // 3. Sesiones que ya empezaron: nadie puede seguir esperando ni aceptar.
        db.ChangeTracker.Clear();
        var stale = await db.Reservations
            .Where(r => (r.Status == ReservationStatus.Waitlisted || r.Status == ReservationStatus.Offered)
                        && db.ClassSessions.Any(s => s.Id == r.SessionId && s.StartsAt <= now))
            .Take(BatchSize)
            .ToListAsync(cancellationToken);
        foreach (var reservation in stale)
        {
            reservation.Expire(null, now);
        }

        if (stale.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return new WaitlistRunSummary(0, expired, offered, stale.Count);
    }
}
