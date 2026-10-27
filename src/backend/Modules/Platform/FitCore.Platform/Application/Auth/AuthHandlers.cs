using FitCore.Application.Core;
using FitCore.Platform.Domain;
using FitCore.Platform.Identity;
using FitCore.Platform.Persistence;
using FitCore.Platform.Security;
using FitCore.SharedKernel;
using FluentValidation;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FitCore.Platform.Application.Auth;

public sealed record AuthTokens(string AccessToken, int ExpiresIn, string RefreshToken, DateTimeOffset RefreshTokenExpiresAt);

public sealed record LoginRequest(string TenantSlug, string Email, string Password, bool UseCookie = false);

public sealed record RefreshRequest(string? RefreshToken);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record LoginCommand(LoginRequest Request) : ICommand<AuthTokens>;

public sealed record RefreshCommand(string? RefreshToken) : ICommand<AuthTokens>;

public sealed record LogoutCommand(string? RefreshToken) : ICommand<Unit>;

public sealed record ChangePasswordCommand(ChangePasswordRequest Request) : ICommand<Unit>;

public sealed record MeQuery : IQuery<MeResponse>;

public sealed record MeResponse(
    Guid UserId,
    Guid TenantId,
    string TenantSlug,
    string Email,
    string FullName,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<Guid> BranchIds,
    bool AllBranches,
    bool MustChangePassword = false);

public sealed class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.TenantSlug).NotEmpty().MaximumLength(40);
        RuleFor(x => x.Email).NotEmpty().MaximumLength(256);
        RuleFor(x => x.Password).NotEmpty().MaximumLength(200);
    }
}

public sealed class ChangePasswordRequestValidator : AbstractValidator<ChangePasswordRequest>
{
    public ChangePasswordRequestValidator()
    {
        RuleFor(x => x.CurrentPassword).NotEmpty();
        RuleFor(x => x.NewPassword).NotEmpty().MaximumLength(200);
    }
}

/// <summary>Emite pares access/refresh y revoca familias de refresh tokens.</summary>
internal sealed class SessionService(PlatformDbContext db, ITokenService tokens, IClock clock, IRequestInfo request)
{
    public AuthTokens Issue(AppUser user, Guid familyId, out Guid refreshTokenId)
    {
        var access = tokens.CreateAccessToken(user);
        var refresh = tokens.CreateRefreshToken(user.TenantId);

        var entity = new RefreshToken
        {
            Id = Guid.CreateVersion7(),
            TenantId = user.TenantId,
            UserId = user.Id,
            FamilyId = familyId,
            TokenHash = refresh.Hash,
            CreatedAt = clock.UtcNow,
            ExpiresAt = refresh.ExpiresAt,
            CreatedByIp = request.IpAddress,
            UserAgent = request.UserAgent is { Length: > 300 } agent ? agent[..300] : request.UserAgent
        };
        db.RefreshTokens.Add(entity);

        refreshTokenId = entity.Id;
        return new AuthTokens(access.Value, access.ExpiresInSeconds, refresh.Value, refresh.ExpiresAt);
    }

    public Task RevokeFamilyAsync(Guid familyId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        return db.RefreshTokens
            .Where(t => t.FamilyId == familyId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), cancellationToken);
    }

    public Task RevokeAllForUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        return db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), cancellationToken);
    }
}

internal sealed class LoginHandler(
    PlatformDbContext db,
    UserManager<AppUser> users,
    SessionService sessions,
    SecurityAudit audit,
    ILogger<LoginHandler> logger) : ICommandHandler<LoginCommand, AuthTokens>
{
    public async Task<Result<AuthTokens>> HandleAsync(LoginCommand command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var slug = request.TenantSlug.Trim().ToLowerInvariant();

        var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Slug == slug, cancellationToken);
        if (tenant is null || !tenant.IsActive)
        {
            logger.LogWarning("Login rechazado: gimnasio {Slug} inexistente o suspendido", slug);
            return PlatformErrors.InvalidCredentials;
        }

        await db.UseTenantAsync(tenant.Id, cancellationToken);

        var user = await users.FindByEmailAsync(request.Email.Trim());
        if (user is null || !user.IsActive)
        {
            audit.Record("Auth.LoginFailed", nameof(AppUser), user?.Id.ToString(), user?.Id, new { reason = "unknown_or_inactive" });
            await db.SaveChangesAsync(cancellationToken);
            return PlatformErrors.InvalidCredentials;
        }

        if (await users.IsLockedOutAsync(user))
        {
            audit.Record("Auth.LoginBlocked", nameof(AppUser), user.Id.ToString(), user.Id, new { reason = "locked_out" });
            await db.SaveChangesAsync(cancellationToken);
            return PlatformErrors.LockedOut;
        }

        if (!await users.CheckPasswordAsync(user, request.Password))
        {
            await users.AccessFailedAsync(user);
            audit.Record("Auth.LoginFailed", nameof(AppUser), user.Id.ToString(), user.Id, new { reason = "bad_password" });
            await db.SaveChangesAsync(cancellationToken);
            return await users.IsLockedOutAsync(user) ? PlatformErrors.LockedOut : PlatformErrors.InvalidCredentials;
        }

        await users.ResetAccessFailedCountAsync(user);

        var tokens = sessions.Issue(user, Guid.CreateVersion7(), out _);
        audit.Record("Auth.LoginSucceeded", nameof(AppUser), user.Id.ToString(), user.Id);
        await db.SaveChangesAsync(cancellationToken);

        return tokens;
    }
}

internal sealed class RefreshHandler(
    PlatformDbContext db,
    ITokenService tokenService,
    SessionService sessions,
    SecurityAudit audit,
    IClock clock) : ICommandHandler<RefreshCommand, AuthTokens>
{
    public async Task<Result<AuthTokens>> HandleAsync(RefreshCommand command, CancellationToken cancellationToken)
    {
        if (!tokenService.TryParseRefreshToken(command.RefreshToken, out var tenantId, out var secret))
        {
            return PlatformErrors.InvalidRefresh;
        }

        await db.UseTenantAsync(tenantId, cancellationToken);

        var hash = tokenService.HashRefreshToken(secret);
        var tenantActive = await db.Tenants.AsNoTracking().AnyAsync(t => t.Id == tenantId && t.Status == TenantStatus.Active, cancellationToken);
        var stored = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);
        if (!tenantActive || stored is null)
        {
            return PlatformErrors.InvalidRefresh;
        }

        // Un token ya rotado o revocado que vuelve a presentarse indica robo: se revoca toda la familia.
        if (stored.RevokedAt is not null)
        {
            return await RejectReuseAsync(stored, cancellationToken);
        }

        if (stored.ExpiresAt <= clock.UtcNow)
        {
            return PlatformErrors.InvalidRefresh;
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == stored.UserId && u.IsActive, cancellationToken);
        if (user is null)
        {
            await sessions.RevokeFamilyAsync(stored.FamilyId, cancellationToken);
            return PlatformErrors.InvalidRefresh;
        }

        var tokens = sessions.Issue(user, stored.FamilyId, out var newTokenId);
        stored.RevokedAt = clock.UtcNow;
        stored.ReplacedById = newTokenId;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Dos refresh simultáneos con el mismo token: el segundo se trata como reutilización.
            db.ChangeTracker.Clear();
            return await RejectReuseAsync(stored, cancellationToken);
        }

        return tokens;
    }

    private async Task<Result<AuthTokens>> RejectReuseAsync(RefreshToken stored, CancellationToken cancellationToken)
    {
        await sessions.RevokeFamilyAsync(stored.FamilyId, cancellationToken);
        audit.Record("Auth.RefreshReuseDetected", nameof(RefreshToken), stored.FamilyId.ToString(), stored.UserId);
        await db.SaveChangesAsync(cancellationToken);
        return PlatformErrors.RefreshReused;
    }
}

internal sealed class LogoutHandler(PlatformDbContext db, ITokenService tokenService, SessionService sessions)
    : ICommandHandler<LogoutCommand, Unit>
{
    public async Task<Result<Unit>> HandleAsync(LogoutCommand command, CancellationToken cancellationToken)
    {
        // Idempotente y sin revelar si el token existía.
        if (tokenService.TryParseRefreshToken(command.RefreshToken, out var tenantId, out var secret))
        {
            await db.UseTenantAsync(tenantId, cancellationToken);
            var hash = tokenService.HashRefreshToken(secret);
            var familyId = await db.RefreshTokens
                .Where(t => t.TokenHash == hash)
                .Select(t => (Guid?)t.FamilyId)
                .FirstOrDefaultAsync(cancellationToken);

            if (familyId is { } family)
            {
                await sessions.RevokeFamilyAsync(family, cancellationToken);
            }
        }

        return new Unit();
    }
}

internal sealed class ChangePasswordHandler(
    PlatformDbContext db,
    UserManager<AppUser> users,
    SessionService sessions,
    SecurityAudit audit,
    IAccessContext access) : ICommandHandler<ChangePasswordCommand, Unit>
{
    public async Task<Result<Unit>> HandleAsync(ChangePasswordCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var user = await db.Users.FirstAsync(u => u.Id == actor.Value.UserId, cancellationToken);
        var result = await users.ChangePasswordAsync(user, command.Request.CurrentPassword, command.Request.NewPassword);
        if (!result.Succeeded)
        {
            return Error.Validation("auth.password_change_failed", string.Join(" ", result.Errors.Select(e => e.Description)));
        }

        user.MustChangePassword = false;
        await users.UpdateAsync(user);
        await sessions.RevokeAllForUserAsync(user.Id, cancellationToken);
        audit.Record("Auth.PasswordChanged", nameof(AppUser), user.Id.ToString(), user.Id);
        await db.SaveChangesAsync(cancellationToken);
        return new Unit();
    }
}

internal sealed class MeHandler(PlatformDbContext db, IAccessContext access) : IQueryHandler<MeQuery, MeResponse>
{
    public async Task<Result<MeResponse>> HandleAsync(MeQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var current = actor.Value;
        var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == current.UserId, cancellationToken);
        var slug = await db.Tenants.AsNoTracking().Where(t => t.Id == current.TenantId).Select(t => t.Slug).FirstAsync(cancellationToken);
        var roles = await db.UserRoles
            .Where(ur => ur.UserId == current.UserId)
            .Join(db.Roles, ur => ur.RoleId, r => r.Id, (_, r) => r.Name!)
            .OrderBy(n => n)
            .ToListAsync(cancellationToken);

        return new MeResponse(
            user.Id,
            current.TenantId,
            slug,
            user.Email ?? string.Empty,
            user.FullName,
            roles,
            [.. current.Permissions.Order()],
            [.. current.BranchIds],
            current.AllBranches,
            current.MustChangePassword);
    }
}
