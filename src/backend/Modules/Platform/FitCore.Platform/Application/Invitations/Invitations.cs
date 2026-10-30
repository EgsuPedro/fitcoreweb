using FitCore.Application.Core;
using FitCore.Platform.Application.AccountTokens;
using FitCore.Platform.Application.Email;
using FitCore.Platform.Application.Registration;
using FitCore.Platform.Application.Users;
using FitCore.Platform.Domain;
using FitCore.Platform.Identity;
using FitCore.Platform.Persistence;
using FitCore.Platform.Security;
using FitCore.SharedKernel;
using FluentValidation;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FitCore.Platform.Application.Invitations;

public static class InvitationStatus
{
    public const string Pending = "Pending";
    public const string Accepted = "Accepted";
    public const string Revoked = "Revoked";

    /// <summary>Solo se muestra: una invitación pendiente cuyo plazo pasó.</summary>
    public const string Expired = "Expired";
}

/// <summary>
/// Invitación para que una persona se una al personal de un gimnasio con un rol y unas sucursales ya decididos por quien invita. Aislada por gimnasio (RLS).
/// El enlace de aceptación vive en <c>identity.account_tokens</c> (solo su hash) y apunta a esta fila: el gimnasio destino sale de aquí, nunca del navegador.
/// </summary>
public sealed class Invitation : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; init; }
    public string Email { get; set; } = string.Empty;
    public string EmailNormalized { get; set; } = string.Empty;
    public Guid RoleId { get; set; }
    public Guid[] BranchIds { get; set; } = [];
    public string Status { get; set; } = InvitationStatus.Pending;
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset LastSentAt { get; set; }
    public int SendCount { get; set; } = 1;
    public DateTimeOffset? AcceptedAt { get; set; }
    public Guid? AcceptedUserId { get; set; }
}

public sealed record InvitationDto(
    Guid Id, string Email, Guid RoleId, string RoleName, IReadOnlyList<Guid> BranchIds, string Status, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

public sealed record CreateInvitationRequest(string Email, Guid RoleId, IReadOnlyList<Guid>? BranchIds);

public sealed record InvitationPreviewDto(string GymName, string Email, string RoleName, DateTimeOffset ExpiresAt);

public sealed record AcceptedInvitationDto(string TenantSlug, string Email);

public sealed record PreviewInvitationRequest(string Token);

public sealed record AcceptInvitationRequest(string Token, string FullName, string Password);

public sealed record ListInvitationsQuery : IQuery<IReadOnlyList<InvitationDto>>;

public sealed record CreateInvitationCommand(CreateInvitationRequest Request) : ICommand<InvitationDto>;

public sealed record RevokeInvitationCommand(Guid Id) : ICommand<InvitationDto>;

public sealed record ResendInvitationCommand(Guid Id) : ICommand<InvitationDto>;

public sealed record PreviewInvitationQuery(string Token) : IQuery<InvitationPreviewDto>;

public sealed record AcceptInvitationCommand(AcceptInvitationRequest Request) : ICommand<AcceptedInvitationDto>;

public static class InvitationErrors
{
    public static readonly Error NotFound = Error.NotFound("invitations.not_found", "Invitación no encontrada.");
    public static readonly Error EmailUnavailable = Error.Unavailable("invitations.email_unavailable", "El envío de correo no está disponible: no se pueden enviar invitaciones por ahora.");
    public static readonly Error AlreadyPending = Error.Conflict("invitations.already_pending", "Ya hay una invitación pendiente para ese correo. Reenvíela o revóquela.");
    public static readonly Error DailyLimit = Error.Conflict("invitations.daily_limit", "Se alcanzó el máximo de invitaciones por día. Intente mañana.");
    public static readonly Error NotPending = Error.Conflict("invitations.not_pending", "La invitación ya no está pendiente.");
    public static readonly Error Invalid = Error.Validation("invitations.token_invalid", "La invitación no es válida, ya se usó o venció. Pida una nueva.");
    public static readonly Error RoleUnavailable = Error.Validation("invitations.role_unavailable", "El rol de esta invitación ya no existe. Pida una nueva.");
    public static readonly Error ResendTooSoon = Error.Conflict("invitations.resend_too_soon", "Espere un minuto antes de reenviar, o se alcanzó el máximo de reenvíos.");
}

public sealed class CreateInvitationRequestValidator : AbstractValidator<CreateInvitationRequest>
{
    public CreateInvitationRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(x => x.RoleId).NotEmpty();
    }
}

public sealed class PreviewInvitationRequestValidator : AbstractValidator<PreviewInvitationRequest>
{
    public PreviewInvitationRequestValidator() => RuleFor(x => x.Token).NotEmpty().MaximumLength(200);
}

public sealed class AcceptInvitationRequestValidator : AbstractValidator<AcceptInvitationRequest>
{
    public AcceptInvitationRequestValidator()
    {
        RuleFor(x => x.Token).NotEmpty().MaximumLength(200);
        RuleFor(x => x.FullName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Password).NotEmpty().MaximumLength(200);
    }
}

internal static class InvitationRules
{
    public static readonly TimeSpan Validity = TimeSpan.FromDays(7);
    public static readonly TimeSpan ResendInterval = TimeSpan.FromSeconds(60);
    public const int MaxSends = 6;
    public const int MaxPerDay = 20;

    public static string StatusOf(Invitation i, DateTimeOffset now) =>
        i.Status == InvitationStatus.Pending && i.ExpiresAt <= now ? InvitationStatus.Expired : i.Status;

    public static async Task<IReadOnlyList<InvitationDto>> ToDtosAsync(PlatformDbContext db, IReadOnlyList<Invitation> items, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var roleIds = items.Select(i => i.RoleId).Distinct().ToList();
        var roles = await db.Roles.AsNoTracking().Where(r => roleIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Name!, cancellationToken);
        return [.. items.Select(i => new InvitationDto(
            i.Id, i.Email, i.RoleId, roles.GetValueOrDefault(i.RoleId, "—"), [.. i.BranchIds], StatusOf(i, now), i.CreatedAt, i.ExpiresAt))];
    }

    public static Task QueueAsync(
        EmailQueue queue, PublicSiteOptions site, Invitation invitation, string token, string gymName, string inviterName, string locale, CancellationToken cancellationToken) =>
        queue.EnqueueAsync(
            invitation.Email, EmailTemplateCodes.Invitation, locale == "en" ? "en" : "es",
            new Dictionary<string, string> { ["gym"] = gymName, ["inviter"] = inviterName, ["link"] = site.BuildLink("/aceptar-invitacion", token) },
            cancellationToken);
}

internal sealed class ListInvitationsHandler(PlatformDbContext db, IAccessContext access, IClock clock) : IQueryHandler<ListInvitationsQuery, IReadOnlyList<InvitationDto>>
{
    public async Task<Result<IReadOnlyList<InvitationDto>>> HandleAsync(ListInvitationsQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var items = await db.Invitations.AsNoTracking().OrderByDescending(i => i.CreatedAt).Take(200).ToListAsync(cancellationToken);
        return Result.Success(await InvitationRules.ToDtosAsync(db, items, clock.UtcNow, cancellationToken));
    }
}

internal sealed class CreateInvitationHandler(
    PlatformDbContext db,
    UserManager<AppUser> users,
    IAccessContext access,
    UserAdministration administration,
    ICapabilityEvaluator capabilities,
    IEmailSender sender,
    EmailQueue queue,
    AccountTokenService tokens,
    SecurityAudit audit,
    IOptions<PublicSiteOptions> site,
    IClock clock) : ICommandHandler<CreateInvitationCommand, InvitationDto>
{
    public async Task<Result<InvitationDto>> HandleAsync(CreateInvitationCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        if (!sender.IsConfigured)
        {
            return InvitationErrors.EmailUnavailable;
        }

        var request = command.Request;
        var branchIds = request.BranchIds?.Distinct().ToList() ?? [];

        // Mismas reglas que crear un usuario a mano: el rol y las sucursales deben existir y el actor no puede dar más de lo que tiene (nunca un rol de plataforma).
        var invalid = await administration.ValidateAccessAsync(actor.Value, [request.RoleId], branchIds, cancellationToken);
        if (invalid is not null)
        {
            return invalid;
        }

        var email = request.Email.Trim();
        var normalized = EmailNormalizer.Normalize(email);
        var now = clock.UtcNow;

        if (await users.FindByEmailAsync(email) is not null)
        {
            return Error.Conflict("users.email_taken", "Ya existe un usuario con ese correo.");
        }

        if (await db.Invitations.AnyAsync(i => i.EmailNormalized == normalized && i.Status == InvitationStatus.Pending && i.ExpiresAt > now, cancellationToken))
        {
            return InvitationErrors.AlreadyPending;
        }

        if (await db.Invitations.CountAsync(i => i.CreatedAt > now.AddDays(-1), cancellationToken) >= InvitationRules.MaxPerDay)
        {
            return InvitationErrors.DailyLimit;
        }

        // Las invitaciones vigentes reservan plaza: no se puede invitar a más personas de las que caben en el plan.
        var reserved = await db.Invitations.CountAsync(i => i.Status == InvitationStatus.Pending && i.ExpiresAt > now, cancellationToken);
        var capacity = await capabilities.RequireCapacityAsync(FitCore.SharedKernel.Capabilities.Limits.InternalUsers, reserved + 1, cancellationToken);
        if (capacity.IsFailure)
        {
            return capacity.Error;
        }

        // Una invitación vencida del mismo correo se reemplaza por la nueva.
        var stale = await db.Invitations.Where(i => i.EmailNormalized == normalized && i.Status == InvitationStatus.Pending).ToListAsync(cancellationToken);
        foreach (var old in stale)
        {
            old.Status = InvitationStatus.Revoked;
        }

        var invitation = new Invitation
        {
            Id = Guid.CreateVersion7(),
            TenantId = actor.Value.TenantId,
            Email = email,
            EmailNormalized = normalized,
            RoleId = request.RoleId,
            BranchIds = [.. branchIds],
            Status = InvitationStatus.Pending,
            CreatedBy = actor.Value.UserId,
            CreatedAt = now,
            ExpiresAt = now + InvitationRules.Validity,
            LastSentAt = now
        };
        db.Invitations.Add(invitation);
        audit.Record("Invitation.Created", nameof(Invitation), invitation.Id.ToString(), actor.Value.UserId, new { role = request.RoleId, branches = branchIds.Count });
        await db.SaveChangesAsync(cancellationToken);

        var token = await tokens.IssueAsync(AccountTokenPurposes.Invitation, invitation.Id, invitation.TenantId, InvitationRules.Validity, cancellationToken);
        var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == invitation.TenantId, cancellationToken);
        var inviter = await db.Users.AsNoTracking().Where(u => u.Id == actor.Value.UserId).Select(u => u.FullName).FirstAsync(cancellationToken);
        await InvitationRules.QueueAsync(queue, site.Value, invitation, token, tenant.Name, inviter, tenant.Locale, cancellationToken);

        return (await InvitationRules.ToDtosAsync(db, [invitation], now, cancellationToken))[0];
    }
}

internal sealed class RevokeInvitationHandler(PlatformDbContext db, IAccessContext access, AccountTokenService tokens, SecurityAudit audit, IClock clock)
    : ICommandHandler<RevokeInvitationCommand, InvitationDto>
{
    public async Task<Result<InvitationDto>> HandleAsync(RevokeInvitationCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var invitation = await db.Invitations.FirstOrDefaultAsync(i => i.Id == command.Id, cancellationToken);
        if (invitation is null)
        {
            return InvitationErrors.NotFound;
        }

        if (invitation.Status != InvitationStatus.Pending)
        {
            return InvitationErrors.NotPending;
        }

        invitation.Status = InvitationStatus.Revoked;
        await tokens.RevokeAsync(AccountTokenPurposes.Invitation, invitation.Id, cancellationToken);
        audit.Record("Invitation.Revoked", nameof(Invitation), invitation.Id.ToString(), actor.Value.UserId);
        await db.SaveChangesAsync(cancellationToken);
        return (await InvitationRules.ToDtosAsync(db, [invitation], clock.UtcNow, cancellationToken))[0];
    }
}

internal sealed class ResendInvitationHandler(
    PlatformDbContext db,
    IAccessContext access,
    IEmailSender sender,
    EmailQueue queue,
    AccountTokenService tokens,
    SecurityAudit audit,
    IOptions<PublicSiteOptions> site,
    IClock clock) : ICommandHandler<ResendInvitationCommand, InvitationDto>
{
    public async Task<Result<InvitationDto>> HandleAsync(ResendInvitationCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        if (!sender.IsConfigured)
        {
            return InvitationErrors.EmailUnavailable;
        }

        var invitation = await db.Invitations.FirstOrDefaultAsync(i => i.Id == command.Id, cancellationToken);
        if (invitation is null)
        {
            return InvitationErrors.NotFound;
        }

        var now = clock.UtcNow;
        if (invitation.Status != InvitationStatus.Pending)
        {
            return InvitationErrors.NotPending;
        }

        if (now - invitation.LastSentAt < InvitationRules.ResendInterval || invitation.SendCount >= InvitationRules.MaxSends)
        {
            return InvitationErrors.ResendTooSoon;
        }

        invitation.LastSentAt = now;
        invitation.SendCount++;
        invitation.ExpiresAt = now + InvitationRules.Validity;
        audit.Record("Invitation.Resent", nameof(Invitation), invitation.Id.ToString(), actor.Value.UserId);
        await db.SaveChangesAsync(cancellationToken);

        var token = await tokens.IssueAsync(AccountTokenPurposes.Invitation, invitation.Id, invitation.TenantId, InvitationRules.Validity, cancellationToken);
        var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == invitation.TenantId, cancellationToken);
        var inviter = await db.Users.AsNoTracking().Where(u => u.Id == actor.Value.UserId).Select(u => u.FullName).FirstAsync(cancellationToken);
        await InvitationRules.QueueAsync(queue, site.Value, invitation, token, tenant.Name, inviter, tenant.Locale, cancellationToken);
        return (await InvitationRules.ToDtosAsync(db, [invitation], now, cancellationToken))[0];
    }
}

internal sealed class PreviewInvitationHandler(PlatformDbContext db, AccountTokenService tokens, IClock clock) : IQueryHandler<PreviewInvitationQuery, InvitationPreviewDto>
{
    public async Task<Result<InvitationPreviewDto>> HandleAsync(PreviewInvitationQuery query, CancellationToken cancellationToken)
    {
        var peeked = await tokens.PeekAsync(AccountTokenPurposes.Invitation, query.Token, cancellationToken);
        if (peeked?.TenantId is not { } tenantId)
        {
            return InvitationErrors.Invalid;
        }

        await db.UseTenantAsync(tenantId, cancellationToken);
        var invitation = await db.Invitations.AsNoTracking().FirstOrDefaultAsync(i => i.Id == peeked.SubjectId, cancellationToken);
        var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken);
        if (invitation is null || tenant is null || !tenant.IsActive || invitation.Status != InvitationStatus.Pending || invitation.ExpiresAt <= clock.UtcNow)
        {
            return InvitationErrors.Invalid;
        }

        var role = await db.Roles.AsNoTracking().Where(r => r.Id == invitation.RoleId).Select(r => r.Name).FirstOrDefaultAsync(cancellationToken);
        return role is null ? InvitationErrors.Invalid : new InvitationPreviewDto(tenant.Name, invitation.Email, role, invitation.ExpiresAt);
    }
}

internal sealed class AcceptInvitationHandler(
    PlatformDbContext db,
    UserManager<AppUser> users,
    AccountTokenService tokens,
    ICapabilityEvaluator capabilities,
    SecurityAudit audit,
    IClock clock) : ICommandHandler<AcceptInvitationCommand, AcceptedInvitationDto>
{
    public async Task<Result<AcceptedInvitationDto>> HandleAsync(AcceptInvitationCommand command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var now = clock.UtcNow;

        // Todo en una transacción: si la contraseña no cumple la política o el plan no tiene plaza, el enlace NO se gasta.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var consumed = await tokens.ConsumeAsync(AccountTokenPurposes.Invitation, request.Token, cancellationToken);
        if (consumed?.TenantId is not { } tenantId)
        {
            return InvitationErrors.Invalid;
        }

        // El gimnasio sale del enlace y de la fila de la invitación, jamás de lo que envía el navegador.
        await db.UseTenantAsync(tenantId, cancellationToken);
        var invitation = await db.Invitations
            .FromSqlInterpolated($"SELECT * FROM identity.invitations WHERE id = {consumed.SubjectId} AND tenant_id = {tenantId} FOR UPDATE")
            .FirstOrDefaultAsync(cancellationToken);
        var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken);
        if (invitation is null || tenant is null || !tenant.IsActive || invitation.Status != InvitationStatus.Pending || invitation.ExpiresAt <= now)
        {
            return InvitationErrors.Invalid;
        }

        // Quien invitó debe seguir activo: una invitación de alguien dado de baja no se honra.
        if (!await db.Users.AnyAsync(u => u.Id == invitation.CreatedBy && u.IsActive, cancellationToken))
        {
            return InvitationErrors.Invalid;
        }

        var role = await db.Roles.AsNoTracking().FirstOrDefaultAsync(r => r.Id == invitation.RoleId, cancellationToken);
        if (role is null)
        {
            return InvitationErrors.RoleUnavailable;
        }

        if (await users.FindByEmailAsync(invitation.Email) is not null)
        {
            return Error.Conflict("users.email_taken", "Ya existe un usuario con ese correo.");
        }

        var capacity = await capabilities.RequireCapacityAsync(FitCore.SharedKernel.Capabilities.Limits.InternalUsers, 1, cancellationToken);
        if (capacity.IsFailure)
        {
            return capacity.Error;
        }

        var user = new AppUser
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            UserName = invitation.Email,
            Email = invitation.Email,
            EmailConfirmed = true,
            FullName = request.FullName.Trim(),
            IsActive = true,
            CreatedAt = now
        };
        var created = await users.CreateAsync(user, request.Password);
        if (!created.Succeeded)
        {
            return created.ToError();
        }

        db.UserRoles.Add(new AppUserRole { TenantId = tenantId, UserId = user.Id, RoleId = role.Id });
        var existingBranches = await db.Branches.AsNoTracking().Where(b => invitation.BranchIds.Contains(b.Id)).Select(b => b.Id).ToListAsync(cancellationToken);
        foreach (var branchId in existingBranches)
        {
            db.UserBranches.Add(new UserBranch { TenantId = tenantId, UserId = user.Id, BranchId = branchId });
        }

        invitation.Status = InvitationStatus.Accepted;
        invitation.AcceptedAt = now;
        invitation.AcceptedUserId = user.Id;
        audit.Record("Invitation.Accepted", nameof(Invitation), invitation.Id.ToString(), user.Id);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new AcceptedInvitationDto(tenant.Slug, invitation.Email);
    }
}
