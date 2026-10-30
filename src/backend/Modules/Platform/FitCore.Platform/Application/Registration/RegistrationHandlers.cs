using FitCore.Application.Core;
using FitCore.Platform.Application.AccountTokens;
using FitCore.Platform.Application.Email;
using FitCore.Platform.Application.Tenants;
using FitCore.Platform.Domain;
using FitCore.Platform.Identity;
using FitCore.Platform.Persistence;
using FitCore.SharedKernel;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FitCore.Platform.Application.Registration;

internal static class RegistrationRules
{
    public static readonly TimeSpan Validity = TimeSpan.FromHours(24);
    public static readonly TimeSpan ResendInterval = TimeSpan.FromSeconds(60);
    public const int MaxSends = 6;
    public const int MaxRegistrationsPerEmailPerHour = 3;

    public static string TenantLocale(string? locale) => locale == "en" ? "en" : "es-EC";

    public static Task QueueVerificationAsync(
        EmailQueue queue, PublicSiteOptions site, PendingRegistration registration, string token, CancellationToken cancellationToken) =>
        queue.EnqueueAsync(
            registration.Email, EmailTemplateCodes.VerifyEmail, registration.Locale,
            new Dictionary<string, string> { ["name"] = registration.OwnerFullName, ["link"] = site.BuildLink("/verificar-correo", token) },
            cancellationToken);
}

internal sealed class RegisterGymHandler(
    PlatformDbContext db,
    UserManager<AppUser> users,
    IPublicPlanCatalog plans,
    IEmailSender sender,
    EmailQueue queue,
    AccountTokenService tokens,
    IPasswordHasher<AppUser> hasher,
    IOptions<LegalOptions> legal,
    IOptions<PublicSiteOptions> site,
    IClock clock) : ICommandHandler<RegisterGymCommand, RegistrationAccepted>
{
    public async Task<Result<RegistrationAccepted>> HandleAsync(RegisterGymCommand command, CancellationToken cancellationToken)
    {
        var r = command.Request;

        // Sin correo no se crea nada: el registro no puede quedar «pendiente» de un mensaje que nunca saldrá.
        if (!sender.IsConfigured)
        {
            return RegistrationErrors.EmailUnavailable;
        }

        if (r.TermsVersion != legal.Value.TermsVersion || r.PrivacyVersion != legal.Value.PrivacyVersion)
        {
            return RegistrationErrors.LegalVersionMismatch;
        }

        var email = r.Email.Trim();
        var probe = new AppUser { UserName = email, Email = email };
        foreach (var validator in users.PasswordValidators)
        {
            var valid = await validator.ValidateAsync(users, probe, r.Password);
            if (!valid.Succeeded)
            {
                return Error.Validation("users.password_invalid", string.Join(" ", valid.Errors.Select(e => e.Description)));
            }
        }

        var plan = await plans.LookupAsync(r.PlanCode, cancellationToken);
        if (plan is null)
        {
            return RegistrationErrors.PlanUnavailable;
        }

        if (!plan.SelfService)
        {
            return RegistrationErrors.PlanRequiresSales;
        }

        var now = clock.UtcNow;
        var slug = r.Slug.Trim().ToLowerInvariant();
        var normalized = EmailNormalizer.Normalize(email);

        if (await db.Tenants.AnyAsync(t => t.Slug == slug, cancellationToken)
            || await db.PendingRegistrations.AnyAsync(
                p => p.Slug == slug && p.Status == RegistrationStatus.Pending && p.ExpiresAt > now && p.EmailNormalized != normalized, cancellationToken))
        {
            return RegistrationErrors.SlugTaken;
        }

        // Demasiados registros del mismo correo en una hora: se responde igual, pero no se crea ni se envía nada (frena el correo masivo a terceros).
        var recent = await db.PendingRegistrations.CountAsync(p => p.EmailNormalized == normalized && p.CreatedAt > now.AddHours(-1), cancellationToken);
        if (recent >= RegistrationRules.MaxRegistrationsPerEmailPerHour)
        {
            return new RegistrationAccepted();
        }

        // Un registro nuevo del mismo correo reemplaza al anterior: el enlace viejo deja de servir.
        var previous = await db.PendingRegistrations
            .Where(p => p.EmailNormalized == normalized && p.Status == RegistrationStatus.Pending)
            .ToListAsync(cancellationToken);
        foreach (var old in previous)
        {
            old.Status = RegistrationStatus.Superseded;
            old.PasswordHash = string.Empty;
            await tokens.RevokeAsync(AccountTokenPurposes.VerifyEmail, old.Id, cancellationToken);
        }

        var registration = new PendingRegistration
        {
            Id = Guid.CreateVersion7(),
            Email = email,
            EmailNormalized = normalized,
            OwnerFullName = r.OwnerFullName.Trim(),
            PasswordHash = hasher.HashPassword(probe, r.Password),
            GymName = r.GymName.Trim(),
            Slug = slug,
            PlanCode = plan.Code,
            Locale = r.Locale ?? "es",
            TimeZone = r.TimeZone ?? Tenant.DefaultTimeZone,
            TermsVersion = r.TermsVersion,
            PrivacyVersion = r.PrivacyVersion,
            Status = RegistrationStatus.Pending,
            CreatedAt = now,
            ExpiresAt = now + RegistrationRules.Validity,
            LastSentAt = now
        };
        db.PendingRegistrations.Add(registration);
        await db.SaveChangesAsync(cancellationToken);

        var token = await tokens.IssueAsync(AccountTokenPurposes.VerifyEmail, registration.Id, null, RegistrationRules.Validity, cancellationToken);
        await RegistrationRules.QueueVerificationAsync(queue, site.Value, registration, token, cancellationToken);
        return new RegistrationAccepted();
    }
}

internal sealed class ResendVerificationHandler(
    PlatformDbContext db,
    IEmailSender sender,
    EmailQueue queue,
    AccountTokenService tokens,
    IOptions<PublicSiteOptions> site,
    IClock clock) : ICommandHandler<ResendVerificationCommand, RegistrationAccepted>
{
    public async Task<Result<RegistrationAccepted>> HandleAsync(ResendVerificationCommand command, CancellationToken cancellationToken)
    {
        if (!sender.IsConfigured)
        {
            return RegistrationErrors.EmailUnavailable;
        }

        var now = clock.UtcNow;
        var normalized = EmailNormalizer.Normalize(command.Email);
        var registration = await db.PendingRegistrations
            .Where(p => p.EmailNormalized == normalized && p.Status == RegistrationStatus.Pending && p.ExpiresAt > now)
            .OrderByDescending(p => p.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        // Respuesta idéntica exista o no el registro, y también cuando el reenvío se frena por límite.
        if (registration is null || now - registration.LastSentAt < RegistrationRules.ResendInterval || registration.SendCount >= RegistrationRules.MaxSends)
        {
            return new RegistrationAccepted();
        }

        registration.LastSentAt = now;
        registration.SendCount++;
        registration.ExpiresAt = now + RegistrationRules.Validity;
        await db.SaveChangesAsync(cancellationToken);

        var token = await tokens.IssueAsync(AccountTokenPurposes.VerifyEmail, registration.Id, null, RegistrationRules.Validity, cancellationToken);
        await RegistrationRules.QueueVerificationAsync(queue, site.Value, registration, token, cancellationToken);
        return new RegistrationAccepted();
    }
}

internal sealed class VerifyRegistrationHandler(
    PlatformDbContext db,
    AccountTokenService tokens,
    TenantProvisioningService provisioning,
    IPublicPlanCatalog plans,
    IEnumerable<ITenantProvisioningHook> hooks,
    SecurityAudit audit,
    IClock clock,
    ILogger<VerifyRegistrationHandler> logger) : ICommandHandler<VerifyRegistrationCommand, VerifiedRegistrationDto>
{
    public async Task<Result<VerifiedRegistrationDto>> HandleAsync(VerifyRegistrationCommand command, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var hookList = hooks.ToList();
        Guid tenantId;
        Guid registrationId;
        OfferedPlan plan;
        PendingRegistration registration;

        // Paso A, todo en una transacción: gastar el token, crear gimnasio + roles + propietario, constancias legales y de prueba. Si algo falla, no queda nada
        // (ni siquiera el token gastado) y se puede reintentar con el mismo enlace.
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            var consumed = await tokens.ConsumeAsync(AccountTokenPurposes.VerifyEmail, command.Token, cancellationToken);
            if (consumed is null)
            {
                return RegistrationErrors.TokenInvalid;
            }

            registrationId = consumed.SubjectId;
            var locked = await db.PendingRegistrations
                .FromSqlInterpolated($"SELECT * FROM organizations.pending_registrations WHERE id = {registrationId} FOR UPDATE")
                .FirstOrDefaultAsync(cancellationToken);
            if (locked is null || locked.Status != RegistrationStatus.Pending)
            {
                return RegistrationErrors.TokenInvalid;
            }

            registration = locked;
            if (registration.ExpiresAt <= now)
            {
                return RegistrationErrors.Expired;
            }

            var offered = await plans.LookupAsync(registration.PlanCode, cancellationToken);
            if (offered is null || !offered.SelfService)
            {
                return RegistrationErrors.PlanUnavailable;
            }

            plan = offered;
            var selection = new TenantPlanSelection(plan.Code, plan.TrialDays > 0);
            foreach (var hook in hookList)
            {
                var valid = await hook.ValidateAsync(selection, cancellationToken);
                if (valid.IsFailure)
                {
                    return valid.Error;
                }
            }

            // Una prueba gratuita por propietario. Quien confirma su correo ya demostró ser su dueño, por eso aquí sí se le informa.
            if (plan.TrialDays > 0 && await db.TrialClaims.AnyAsync(c => c.EmailNormalized == registration.EmailNormalized, cancellationToken))
            {
                Close(registration, RegistrationStatus.Rejected, now);
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return RegistrationErrors.TrialAlreadyUsed;
            }

            var request = new CreateTenantRequest(
                registration.Slug, registration.GymName, registration.TimeZone, RegistrationRules.TenantLocale(registration.Locale),
                registration.Email, registration.OwnerFullName, string.Empty, plan.Code, selection.StartTrial);
            var provisioned = await provisioning.ProvisionDetailedAsync(
                request, isPlatformTenant: false, new ProvisioningOptions(registration.PasswordHash, PendingSetup: true), cancellationToken);
            if (provisioned.IsFailure)
            {
                if (provisioned.Error.Code == "tenants.slug_taken")
                {
                    Close(registration, RegistrationStatus.Rejected, now);
                    await db.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    return RegistrationErrors.SlugTaken;
                }

                return provisioned.Error;
            }

            tenantId = provisioned.Value.Tenant.Id;
            await db.UseTenantAsync(tenantId, cancellationToken);
            db.LegalAcceptances.Add(NewAcceptance(tenantId, provisioned.Value.OwnerId, LegalDocuments.Terms, registration.TermsVersion, now));
            db.LegalAcceptances.Add(NewAcceptance(tenantId, provisioned.Value.OwnerId, LegalDocuments.Privacy, registration.PrivacyVersion, now));
            audit.Record("tenant.self_registered", "Tenant", tenantId.ToString(), provisioned.Value.OwnerId, new { plan = plan.Code, trial = selection.StartTrial });

            if (plan.TrialDays > 0)
            {
                db.TrialClaims.Add(new TrialClaim { EmailNormalized = registration.EmailNormalized, TenantId = tenantId, PlanCode = plan.Code, ClaimedAt = now });
            }

            registration.Status = RegistrationStatus.Provisioning;
            registration.TenantId = tenantId;
            registration.PasswordHash = string.Empty;
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (plan.TrialDays > 0)
            {
                // Dos confirmaciones simultáneas del mismo propietario: gana una; la otra no crea nada.
                logger.LogInformation(ex, "Reclamación de prueba duplicada para el registro {Id}", registrationId);
                return RegistrationErrors.TrialAlreadyUsed;
            }

            await db.ClearTenantAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        // Paso B: abrir la suscripción del gimnasio. Vive en otro módulo y otra conexión, por eso va después y con compensación (ver docs).
        var applied = await ApplyHooksAsync(hookList, tenantId, new TenantPlanSelection(plan.Code, plan.TrialDays > 0), cancellationToken);
        if (applied.IsFailure)
        {
            await CompensateAsync(registrationId, tenantId, applied.Error, cancellationToken);
            return RegistrationErrors.ProvisioningFailed;
        }

        await db.ClearTenantAsync(cancellationToken);
        await db.PendingRegistrations.Where(p => p.Id == registrationId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, RegistrationStatus.Verified).SetProperty(p => p.CompletedAt, now), cancellationToken);
        return new VerifiedRegistrationDto(registration.Slug, registration.Email, plan.Name, plan.TrialDays, TenantSetupStatus.PendingSetup.ToString());
    }

    private async Task<Result> ApplyHooksAsync(
        List<ITenantProvisioningHook> hookList, Guid tenantId, TenantPlanSelection selection, CancellationToken cancellationToken)
    {
        // Las suscripciones solo se escriben desde el contexto de plataforma.
        var platformId = await db.Tenants.AsNoTracking().Where(t => t.Slug == Tenant.PlatformSlug).Select(t => t.Id).FirstAsync(cancellationToken);
        await db.UseTenantAsync(platformId, cancellationToken);
        Result last = Result.Success();
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            last = Result.Success();
            try
            {
                foreach (var hook in hookList)
                {
                    last = await hook.OnProvisionedAsync(tenantId, selection, cancellationToken);
                    if (last.IsFailure)
                    {
                        break;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Falló la apertura de la suscripción del gimnasio {TenantId} (intento {Attempt})", tenantId, attempt);
                last = Error.Failure("registration.provisioning_failed", "Falló la apertura de la suscripción.");
            }

            if (last.IsSuccess)
            {
                break;
            }
        }

        return last;
    }

    /// <summary>Sin suscripción el gimnasio no debe quedar usable: se suspende y el registro se marca «Failed» para que la plataforma lo revise.</summary>
    private async Task CompensateAsync(Guid registrationId, Guid tenantId, Error cause, CancellationToken cancellationToken)
    {
        logger.LogError("Registro {RegistrationId}: gimnasio {TenantId} creado sin suscripción ({Code}); se suspende para revisión", registrationId, tenantId, cause.Code);
        await db.Tenants.Where(t => t.Id == tenantId).ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, TenantStatus.Suspended), cancellationToken);
        await db.PendingRegistrations.Where(p => p.Id == registrationId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, RegistrationStatus.Failed), cancellationToken);
        await db.ClearTenantAsync(cancellationToken);
    }

    private static void Close(PendingRegistration registration, string status, DateTimeOffset now)
    {
        registration.Status = status;
        registration.PasswordHash = string.Empty;
        registration.CompletedAt = now;
    }

    private static LegalAcceptance NewAcceptance(Guid tenantId, Guid userId, string document, string version, DateTimeOffset now) =>
        new() { Id = Guid.CreateVersion7(), TenantId = tenantId, UserId = userId, Document = document, Version = version, AcceptedAt = now };
}

/// <summary>Mantenimiento periódico de los registros (lo ejecuta el Worker): vence los pendientes, borra la contraseña de los terminados y elimina los antiguos.</summary>
public static class RegistrationMaintenance
{
    public static async Task<int> RunAsync(PlatformDbContext db, IClock clock, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var expired = await db.PendingRegistrations
            .Where(p => p.Status == RegistrationStatus.Pending && p.ExpiresAt <= now)
            .ExecuteUpdateAsync(
                s => s.SetProperty(p => p.Status, RegistrationStatus.Expired).SetProperty(p => p.PasswordHash, string.Empty).SetProperty(p => p.CompletedAt, now),
                cancellationToken);
        var limit = now.AddDays(-30);
        var deleted = await db.PendingRegistrations
            .Where(p => p.Status != RegistrationStatus.Pending && p.Status != RegistrationStatus.Provisioning && p.Status != RegistrationStatus.Failed && p.CreatedAt < limit)
            .ExecuteDeleteAsync(cancellationToken);
        return expired + deleted;
    }
}
