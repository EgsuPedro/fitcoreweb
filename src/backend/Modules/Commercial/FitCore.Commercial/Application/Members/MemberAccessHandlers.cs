using FitCore.Application.Core;
using FitCore.Commercial.Domain;
using FitCore.Commercial.Persistence;
using FitCore.Platform.Application.Users;
using FitCore.Platform.Security;
using FitCore.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FitCore.Commercial.Application.Members;

/// <summary>Resultado de dar o restablecer el acceso: la clave temporal se devuelve una sola vez y nunca se guarda en claro.</summary>
public sealed record MemberAccessDto(Guid MemberId, string Email, string TemporaryPassword);

public sealed record GrantMemberAccessCommand(Guid MemberId) : ICommand<MemberAccessDto>;

public sealed record ResetMemberAccessCommand(Guid MemberId) : ICommand<MemberAccessDto>;

public sealed record SetMemberAccessActiveRequest(bool Active);

public sealed record SetMemberAccessActiveCommand(Guid MemberId, SetMemberAccessActiveRequest Request) : ICommand<MemberDto>;

public static class MemberAccessErrors
{
    public static readonly Error AlreadyHasAccess = Error.Conflict("members.access_exists", "El alumno ya tiene acceso al portal.");
    public static readonly Error NoAccess = Error.Conflict("members.access_missing", "El alumno todavía no tiene acceso al portal.");
    public static readonly Error EmailRequired = Error.Validation("members.access_email_required", "Registre el correo del alumno para darle acceso.");
    public static readonly Error MemberInactive = Error.Conflict("members.access_member_inactive", "Un alumno inactivo no puede recibir acceso.");
}

internal sealed class GrantMemberAccessHandler(
    CommercialDbContext db,
    IAccessContext access,
    IMemberAccountService accounts,
    IClock clock,
    ICurrentUser currentUser)
    : ICommandHandler<GrantMemberAccessCommand, MemberAccessDto>
{
    public async Task<Result<MemberAccessDto>> HandleAsync(GrantMemberAccessCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var member = await db.Members.VisibleTo(actor.Value).FirstOrDefaultAsync(m => m.Id == command.MemberId, cancellationToken);
        if (member is null)
        {
            return MemberErrors.NotFound;
        }

        if (member.UserId is not null)
        {
            return MemberAccessErrors.AlreadyHasAccess;
        }

        if (!member.IsActive)
        {
            return MemberAccessErrors.MemberInactive;
        }

        if (string.IsNullOrWhiteSpace(member.Email))
        {
            return MemberAccessErrors.EmailRequired;
        }

        var account = await accounts.CreateAsync(member.Email, member.FullName, member.BranchId, cancellationToken);
        if (account.IsFailure)
        {
            return account.Error;
        }

        try
        {
            member.LinkUser(account.Value.UserId, currentUser.UserId, clock.UtcNow);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is DbUpdateException { InnerException: PostgresException } or DbUpdateConcurrencyException)
        {
            // La cuenta recién creada quedaría huérfana: se desactiva para que no pueda usarse.
            await accounts.SetActiveAsync(account.Value.UserId, false, CancellationToken.None);
            return Error.Conflict("members.access_conflict", "No se pudo vincular la cuenta; intente de nuevo.");
        }

        return new MemberAccessDto(member.Id, member.Email, account.Value.TemporaryPassword);
    }
}

internal sealed class ResetMemberAccessHandler(CommercialDbContext db, IAccessContext access, IMemberAccountService accounts)
    : ICommandHandler<ResetMemberAccessCommand, MemberAccessDto>
{
    public async Task<Result<MemberAccessDto>> HandleAsync(ResetMemberAccessCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var member = await db.Members.AsNoTracking().VisibleTo(actor.Value).FirstOrDefaultAsync(m => m.Id == command.MemberId, cancellationToken);
        if (member is null)
        {
            return MemberErrors.NotFound;
        }

        if (member.UserId is not { } userId)
        {
            return MemberAccessErrors.NoAccess;
        }

        var password = await accounts.ResetPasswordAsync(userId, cancellationToken);
        return password.IsFailure ? password.Error : new MemberAccessDto(member.Id, member.Email ?? string.Empty, password.Value);
    }
}

internal sealed class SetMemberAccessActiveHandler(CommercialDbContext db, IAccessContext access, IMemberAccountService accounts)
    : ICommandHandler<SetMemberAccessActiveCommand, MemberDto>
{
    public async Task<Result<MemberDto>> HandleAsync(SetMemberAccessActiveCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var member = await db.Members.AsNoTracking().VisibleTo(actor.Value).FirstOrDefaultAsync(m => m.Id == command.MemberId, cancellationToken);
        if (member is null)
        {
            return MemberErrors.NotFound;
        }

        if (member.UserId is not { } userId)
        {
            return MemberAccessErrors.NoAccess;
        }

        if (command.Request.Active && !member.IsActive)
        {
            return MemberAccessErrors.MemberInactive;
        }

        var result = await accounts.SetActiveAsync(userId, command.Request.Active, cancellationToken);
        return result.IsFailure ? result.Error : MemberDto.From(member);
    }
}
