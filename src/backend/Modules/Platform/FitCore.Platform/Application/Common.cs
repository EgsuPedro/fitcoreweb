using FitCore.Application.Core;
using FitCore.Platform.Persistence;
using FitCore.Platform.Security;
using FitCore.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace FitCore.Platform.Application;

public static class PlatformErrors
{
    public static readonly Error Unauthenticated = Error.Unauthorized("auth.unauthenticated", "Debe iniciar sesión.");
    public static readonly Error InvalidCredentials = Error.Unauthorized("auth.invalid_credentials", "Credenciales inválidas.");
    public static readonly Error LockedOut = Error.Unauthorized("auth.locked_out", "Cuenta bloqueada temporalmente por intentos fallidos.");
    public static readonly Error InvalidRefresh = Error.Unauthorized("auth.invalid_refresh", "La sesión no es válida. Inicie sesión nuevamente.");
    public static readonly Error RefreshReused = Error.Unauthorized("auth.refresh_reused", "La sesión fue revocada por seguridad. Inicie sesión nuevamente.");
    public static readonly Error Forbidden = Error.Forbidden("auth.forbidden", "No tiene permiso para realizar esta acción.");
    public static readonly Error BranchNotAllowed = Error.Forbidden("branches.not_allowed", "No tiene acceso a esta sucursal.");
    public static readonly Error AllBranchesRequired = Error.Forbidden("auth.all_branches_required", "Solo quienes acceden a todas las sucursales pueden hacer esto.");
}

/// <summary>Escribe eventos de seguridad en la auditoría del tenant activo (el llamador hace SaveChanges).</summary>
public sealed class SecurityAudit(PlatformDbContext db, ITenantContext tenantContext, IClock clock, ICurrentUser currentUser, IRequestInfo requestInfo)
{
    public void Record(string action, string entity, string? entityId, Guid? userId = null, object? details = null)
    {
        if (tenantContext.TenantId is not { } tenantId)
        {
            return;
        }

        db.AuditLogs.Add(new AuditLog
        {
            TenantId = tenantId,
            UserId = userId ?? currentUser.UserId,
            Action = action,
            Entity = entity,
            EntityId = entityId,
            After = details is null ? null : System.Text.Json.JsonSerializer.Serialize(details),
            IpAddress = requestInfo.IpAddress,
            CorrelationId = requestInfo.CorrelationId,
            OccurredAt = clock.UtcNow
        });
    }
}

internal static class IdentityResultExtensions
{
    /// <summary>Traduce los errores de ASP.NET Identity a códigos de negocio estables.</summary>
    public static Error ToError(this Microsoft.AspNetCore.Identity.IdentityResult result)
    {
        var codes = result.Errors.Select(e => e.Code).ToList();
        var description = string.Join(" ", result.Errors.Select(e => e.Description));

        if (codes.Any(c => c is "DuplicateEmail" or "DuplicateUserName"))
        {
            return Error.Conflict("users.email_taken", "Ya existe un usuario con ese correo.");
        }

        if (codes.Any(c => c.StartsWith("Password", StringComparison.Ordinal)))
        {
            return Error.Validation("users.password_invalid", description);
        }

        return Error.Validation("users.invalid", description);
    }
}

internal static class QueryExtensions
{
    public static async Task<PagedResult<T>> ToPagedAsync<T>(this IQueryable<T> query, PageRequest page, CancellationToken cancellationToken)
    {
        var total = await query.CountAsync(cancellationToken);
        var items = await query.Skip(page.Skip).Take(page.SafePageSize).ToListAsync(cancellationToken);
        return new PagedResult<T>(items, page.SafePage, page.SafePageSize, total);
    }
}

internal static class AccessGuards
{
    public static async Task<Result<UserAccess>> RequireAccessAsync(this IAccessContext access, CancellationToken cancellationToken)
    {
        var current = await access.GetAsync(cancellationToken);
        return current is null ? PlatformErrors.Unauthenticated : current;
    }
}
