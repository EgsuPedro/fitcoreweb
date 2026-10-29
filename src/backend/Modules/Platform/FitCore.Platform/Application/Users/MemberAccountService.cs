using System.Security.Cryptography;
using FitCore.Application.Core;
using FitCore.Platform.Application.Auth;
using FitCore.Platform.Identity;
using FitCore.Platform.Persistence;
using FitCore.Platform.Security;
using FitCore.SharedKernel;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace FitCore.Platform.Application.Users;

public sealed record ProvisionedAccount(Guid UserId, string TemporaryPassword);

/// <summary>
/// Cuentas de acceso de los alumnos a su portal. Las usa el módulo de alumnos: la cuenta es un usuario normal (rol «Alumno») con una
/// clave temporal que se muestra una sola vez y que debe cambiarse al entrar. Mientras no la cambie, la cuenta no tiene ningún permiso.
/// </summary>
public interface IMemberAccountService
{
    Task<Result<ProvisionedAccount>> CreateAsync(string email, string fullName, Guid branchId, CancellationToken cancellationToken);

    /// <summary>Nueva clave temporal: cierra las sesiones abiertas del alumno y vuelve a exigirle cambiarla.</summary>
    Task<Result<string>> ResetPasswordAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Activa o desactiva la cuenta (al desactivarla se cierran sus sesiones).</summary>
    Task<Result<Unit>> SetActiveAsync(Guid userId, bool active, CancellationToken cancellationToken);
}

public static class MemberAccountErrors
{
    public static readonly Error RoleMissing = Error.Failure("members.access_role_missing", "El gimnasio no tiene el rol Alumno. Restablezca los roles de plantilla.");
    public static readonly Error UserNotFound = Error.NotFound("users.not_found", "Usuario no encontrado.");
}

internal sealed class MemberAccountService(
    PlatformDbContext db,
    UserManager<AppUser> users,
    IAccessContext access,
    SessionService sessions,
    SecurityAudit audit,
    IClock clock) : IMemberAccountService
{
    private const string StudentRole = "Alumno";

    public async Task<Result<ProvisionedAccount>> CreateAsync(string email, string fullName, Guid branchId, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var role = await db.Roles.AsNoTracking().FirstOrDefaultAsync(r => r.Name == StudentRole, cancellationToken);
        if (role is null)
        {
            return MemberAccountErrors.RoleMissing;
        }

        email = email.Trim();
        if (await users.FindByEmailAsync(email) is not null)
        {
            return Error.Conflict("users.email_taken", "Ya existe un usuario con ese correo.");
        }

        var password = GenerateTemporaryPassword();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var user = new AppUser
        {
            Id = Guid.CreateVersion7(),
            TenantId = actor.Value.TenantId,
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            FullName = fullName.Trim(),
            IsActive = true,
            MustChangePassword = true,
            AccountKind = AccountKind.Member,
            CreatedAt = clock.UtcNow
        };

        var created = await users.CreateAsync(user, password);
        if (!created.Succeeded)
        {
            return created.ToError();
        }

        db.UserRoles.Add(new AppUserRole { TenantId = user.TenantId, UserId = user.Id, RoleId = role.Id });
        db.UserBranches.Add(new UserBranch { TenantId = user.TenantId, UserId = user.Id, BranchId = branchId });
        audit.Record("Members.AccessGranted", nameof(AppUser), user.Id.ToString());
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new ProvisionedAccount(user.Id, password);
    }

    public async Task<Result<string>> ResetPasswordAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await FindAsync(userId, cancellationToken);
        if (user is null)
        {
            return MemberAccountErrors.UserNotFound;
        }

        var password = GenerateTemporaryPassword();
        // Sin proveedores de token (no hay correo): se reemplaza la clave directamente, validándola contra la política.
        var removed = await users.RemovePasswordAsync(user);
        var reset = removed.Succeeded ? await users.AddPasswordAsync(user, password) : removed;
        if (!reset.Succeeded)
        {
            return reset.ToError();
        }

        user.MustChangePassword = true;
        await users.UpdateAsync(user);
        await sessions.RevokeAllForUserAsync(user.Id, cancellationToken);
        audit.Record("Members.AccessReset", nameof(AppUser), user.Id.ToString());
        await db.SaveChangesAsync(cancellationToken);
        return password;
    }

    public async Task<Result<Unit>> SetActiveAsync(Guid userId, bool active, CancellationToken cancellationToken)
    {
        var user = await FindAsync(userId, cancellationToken);
        if (user is null)
        {
            return MemberAccountErrors.UserNotFound;
        }

        user.IsActive = active;
        if (!active)
        {
            await sessions.RevokeAllForUserAsync(user.Id, cancellationToken);
        }

        audit.Record(active ? "Members.AccessEnabled" : "Members.AccessDisabled", nameof(AppUser), user.Id.ToString());
        await db.SaveChangesAsync(cancellationToken);
        return new Unit();
    }

    // Solo cuentas del rol Alumno: esta vía nunca toca usuarios del personal.
    private async Task<AppUser?> FindAsync(Guid userId, CancellationToken cancellationToken) =>
        await db.Users.FirstOrDefaultAsync(
            u => u.Id == userId && db.UserRoles.Any(ur => ur.UserId == u.Id && db.Roles.Any(r => r.Id == ur.RoleId && r.Name == StudentRole)),
            cancellationToken);

    /// <summary>14 caracteres sin ambiguos (0/O, 1/l/I), con mayúscula, minúscula y dígito: cumple la política de claves.</summary>
    internal static string GenerateTemporaryPassword()
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string lower = "abcdefghijkmnpqrstuvwxyz";
        const string digits = "23456789";
        var all = upper + lower + digits;
        var chars = new List<char> { Pick(upper), Pick(lower), Pick(digits) };
        while (chars.Count < 14)
        {
            chars.Add(Pick(all));
        }

        for (var i = chars.Count - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }

        return new string([.. chars]);
    }

    private static char Pick(string alphabet) => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
}
