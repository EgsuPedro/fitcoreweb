using FitCore.Application.Core;
using FitCore.Platform.Application.AccountTokens;
using FitCore.Platform.Application.Email;
using FitCore.Platform.Domain;
using FitCore.Platform.Identity;
using FitCore.Platform.Persistence;
using FitCore.SharedKernel;
using FluentValidation;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FitCore.Platform.Application.Auth;

/// <summary>Pide un enlace para elegir una contraseña nueva. Indica el gimnasio porque el mismo correo puede existir en varios.</summary>
public sealed record ForgotPasswordRequest(string TenantSlug, string Email);

public sealed record ResetPasswordRequest(string Token, string NewPassword);

/// <summary>Respuesta única de «olvidé mi contraseña»: igual exista o no la cuenta, para no revelar quién está registrado.</summary>
public sealed record PasswordRecoveryAccepted(string Status = "check_email");

public sealed record ForgotPasswordCommand(ForgotPasswordRequest Request) : ICommand<PasswordRecoveryAccepted>;

public sealed record ResetPasswordCommand(ResetPasswordRequest Request) : ICommand<Unit>;

public static class PasswordRecoveryErrors
{
    public static readonly Error EmailUnavailable = Error.Unavailable("auth.recovery_email_unavailable", "El envío de correo no está disponible por ahora. Intente más tarde o pida a su administrador que restablezca su contraseña.");
    public static readonly Error TokenInvalid = Error.Validation("auth.reset_token_invalid", "El enlace no es válido, ya se usó o venció. Solicite uno nuevo.");
}

public sealed class ForgotPasswordRequestValidator : AbstractValidator<ForgotPasswordRequest>
{
    public ForgotPasswordRequestValidator()
    {
        RuleFor(x => x.TenantSlug).NotEmpty().MaximumLength(40);
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
    }
}

public sealed class ResetPasswordRequestValidator : AbstractValidator<ResetPasswordRequest>
{
    public ResetPasswordRequestValidator()
    {
        RuleFor(x => x.Token).NotEmpty().MaximumLength(200);
        RuleFor(x => x.NewPassword).NotEmpty().MaximumLength(200);
    }
}

internal static class PasswordRecoveryRules
{
    public static readonly TimeSpan Validity = TimeSpan.FromHours(1);
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(60);
    public const int MaxPerHour = 3;
}

internal sealed class ForgotPasswordHandler(
    PlatformDbContext db,
    UserManager<AppUser> users,
    IEmailSender sender,
    EmailQueue queue,
    AccountTokenService tokens,
    SecurityAudit audit,
    IOptions<PublicSiteOptions> site,
    IClock clock) : ICommandHandler<ForgotPasswordCommand, PasswordRecoveryAccepted>
{
    public async Task<Result<PasswordRecoveryAccepted>> HandleAsync(ForgotPasswordCommand command, CancellationToken cancellationToken)
    {
        // Sin correo no hay recuperación: se dice con claridad (es un estado del sistema, no un dato de ninguna cuenta).
        if (!sender.IsConfigured)
        {
            return PasswordRecoveryErrors.EmailUnavailable;
        }

        var accepted = new PasswordRecoveryAccepted();
        var slug = command.Request.TenantSlug.Trim().ToLowerInvariant();

        // El gimnasio de la plataforma queda fuera: su administrador se recupera con la configuración del servidor, no por correo.
        var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Slug == slug, cancellationToken);
        if (tenant is null || !tenant.IsActive || tenant.Slug == Tenant.PlatformSlug)
        {
            return accepted;
        }

        await db.UseTenantAsync(tenant.Id, cancellationToken);
        var user = await users.FindByEmailAsync(command.Request.Email.Trim());
        if (user is null || !user.IsActive)
        {
            return accepted;
        }

        // Límite por cuenta: un tercero no puede llenar la bandeja de otra persona con enlaces.
        var now = clock.UtcNow;
        var recent = await db.AccountTokens
            .Where(t => t.Purpose == AccountTokenPurposes.PasswordReset && t.SubjectId == user.Id && t.CreatedAt > now.AddHours(-1))
            .Select(t => t.CreatedAt)
            .ToListAsync(cancellationToken);
        if (recent.Count >= PasswordRecoveryRules.MaxPerHour || recent.Any(created => now - created < PasswordRecoveryRules.MinInterval))
        {
            return accepted;
        }

        var token = await tokens.IssueAsync(AccountTokenPurposes.PasswordReset, user.Id, tenant.Id, PasswordRecoveryRules.Validity, cancellationToken);
        await queue.EnqueueAsync(
            user.Email!, EmailTemplateCodes.PasswordReset, tenant.Locale == "en" ? "en" : "es",
            new Dictionary<string, string> { ["name"] = user.FullName, ["link"] = site.Value.BuildLink("/restablecer-clave", token) },
            cancellationToken);
        audit.Record("Auth.PasswordResetRequested", nameof(AppUser), user.Id.ToString(), user.Id);
        await db.SaveChangesAsync(cancellationToken);
        return accepted;
    }
}

internal sealed class ResetPasswordHandler(
    PlatformDbContext db,
    UserManager<AppUser> users,
    IPasswordHasher<AppUser> hasher,
    AccountTokenService tokens,
    SessionService sessions,
    EmailQueue queue,
    IEmailSender sender,
    SecurityAudit audit) : ICommandHandler<ResetPasswordCommand, Unit>
{
    public async Task<Result<Unit>> HandleAsync(ResetPasswordCommand command, CancellationToken cancellationToken)
    {
        // Todo en una transacción: si la contraseña nueva no cumple la política, el enlace NO se gasta y la persona puede intentarlo de nuevo.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var consumed = await tokens.ConsumeAsync(AccountTokenPurposes.PasswordReset, command.Request.Token, cancellationToken);
        if (consumed?.TenantId is not { } tenantId)
        {
            return PasswordRecoveryErrors.TokenInvalid;
        }

        await db.UseTenantAsync(tenantId, cancellationToken);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == consumed.SubjectId, cancellationToken);
        var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken);
        if (user is null || !user.IsActive || tenant is null || !tenant.IsActive)
        {
            return PasswordRecoveryErrors.TokenInvalid;
        }

        foreach (var validator in users.PasswordValidators)
        {
            var valid = await validator.ValidateAsync(users, user, command.Request.NewPassword);
            if (!valid.Succeeded)
            {
                return Error.Validation("users.password_invalid", string.Join(" ", valid.Errors.Select(e => e.Description)));
            }
        }

        user.PasswordHash = hasher.HashPassword(user, command.Request.NewPassword);
        user.MustChangePassword = false;
        var updated = await users.UpdateAsync(user);
        if (!updated.Succeeded)
        {
            return Error.Failure("auth.reset_failed", "No se pudo cambiar la contraseña.");
        }

        await users.UpdateSecurityStampAsync(user);
        await users.ResetAccessFailedCountAsync(user);
        await users.SetLockoutEndDateAsync(user, null);

        // Quien tenía la cuenta abierta (incluido un intruso) queda fuera, y cualquier otro enlace de recuperación pendiente deja de servir.
        await sessions.RevokeAllForUserAsync(user.Id, cancellationToken);
        await tokens.RevokeAsync(AccountTokenPurposes.PasswordReset, user.Id, cancellationToken);
        audit.Record("Auth.PasswordResetCompleted", nameof(AppUser), user.Id.ToString(), user.Id);

        if (sender.IsConfigured && !string.IsNullOrWhiteSpace(user.Email))
        {
            await queue.EnqueueAsync(
                user.Email, EmailTemplateCodes.PasswordChanged, tenant.Locale == "en" ? "en" : "es",
                new Dictionary<string, string> { ["name"] = user.FullName }, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new Unit();
    }
}
