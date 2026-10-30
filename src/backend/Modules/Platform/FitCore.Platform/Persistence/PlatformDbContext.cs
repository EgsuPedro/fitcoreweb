using System.Data;
using FitCore.Application.Core;
using FitCore.Platform.Domain;
using FitCore.Platform.Identity;
using FitCore.SharedKernel;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace FitCore.Platform.Persistence;

/// <summary>
/// Contexto del núcleo de la plataforma: organizaciones, identidad, auditoría y outbox.
/// Aislamiento por tenant: filtro global de EF + Row-Level Security en PostgreSQL.
/// </summary>
public sealed class PlatformDbContext(DbContextOptions<PlatformDbContext> options, ITenantContext tenantContext)
    : IdentityDbContext<AppUser, AppRole, Guid, IdentityUserClaim<Guid>, AppUserRole, IdentityUserLogin<Guid>, IdentityRoleClaim<Guid>, IdentityUserToken<Guid>>(options)
{
    public const string OrganizationsSchema = "organizations";
    public const string IdentitySchema = "identity";
    public const string AuditSchema = "audit";
    public const string MessagingSchema = "messaging";

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Branch> Branches => Set<Branch>();
    public DbSet<TenantLogo> TenantLogos => Set<TenantLogo>();
    public DbSet<PermissionEntity> PermissionCatalog => Set<PermissionEntity>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<UserBranch> UserBranches => Set<UserBranch>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<FitCore.Platform.Application.Email.QueuedEmail> QueuedEmails => Set<FitCore.Platform.Application.Email.QueuedEmail>();
    public DbSet<FitCore.Platform.Application.Registration.PendingRegistration> PendingRegistrations => Set<FitCore.Platform.Application.Registration.PendingRegistration>();
    public DbSet<FitCore.Platform.Application.Registration.TrialClaim> TrialClaims => Set<FitCore.Platform.Application.Registration.TrialClaim>();
    public DbSet<FitCore.Platform.Application.Registration.LegalAcceptance> LegalAcceptances => Set<FitCore.Platform.Application.Registration.LegalAcceptance>();
    public DbSet<FitCore.Platform.Application.Invitations.Invitation> Invitations => Set<FitCore.Platform.Application.Invitations.Invitation>();
    public DbSet<FitCore.Platform.Application.Onboarding.TenantOnboarding> Onboardings => Set<FitCore.Platform.Application.Onboarding.TenantOnboarding>();
    public DbSet<FitCore.Platform.Application.AccountTokens.AccountToken> AccountTokens => Set<FitCore.Platform.Application.AccountTokens.AccountToken>();

    // Guid.Empty nunca coincide con un tenant real: sin tenant activo no se ve ninguna fila.
    private Guid CurrentTenantId => tenantContext.TenantId ?? Guid.Empty;

    /// <summary>Activa el tenant y lo propaga a PostgreSQL aunque la conexión ya esté abierta.</summary>
    public async Task UseTenantAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        tenantContext.Set(tenantId);

        var connection = Database.GetDbConnection();
        if (connection.State == ConnectionState.Open)
        {
            await TenantConnectionInterceptor.ApplyTenantAsync(connection, tenantId, cancellationToken);
        }
    }

    /// <summary>Quita el tenant activo: sin tenant, RLS no devuelve ninguna fila.</summary>
    public async Task ClearTenantAsync(CancellationToken cancellationToken = default)
    {
        tenantContext.Clear();

        var connection = Database.GetDbConnection();
        if (connection.State == ConnectionState.Open)
        {
            await TenantConnectionInterceptor.ApplyTenantAsync(connection, null, cancellationToken);
        }
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // Tablas de Identity que no usamos (no tienen tenant_id y no pueden protegerse con RLS).
        builder.Ignore<IdentityUserClaim<Guid>>();
        builder.Ignore<IdentityUserLogin<Guid>>();
        builder.Ignore<IdentityRoleClaim<Guid>>();
        builder.Ignore<IdentityUserToken<Guid>>();

        ConfigureOrganizations(builder);
        ConfigureIdentity(builder);
        ConfigurePersistence(builder);

        foreach (var entityType in builder.Model.GetEntityTypes().ToList())
        {
            if (typeof(IHasDomainEvents).IsAssignableFrom(entityType.ClrType))
            {
                builder.Entity(entityType.ClrType).Ignore(nameof(IHasDomainEvents.DomainEvents));
            }

            if (typeof(ITenantOwned).IsAssignableFrom(entityType.ClrType))
            {
                typeof(PlatformDbContext)
                    .GetMethod(nameof(ApplyTenantFilter), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .MakeGenericMethod(entityType.ClrType)
                    .Invoke(this, [builder]);
            }
        }
    }

    private void ApplyTenantFilter<T>(ModelBuilder builder)
        where T : class, ITenantOwned =>
        builder.Entity<T>().HasQueryFilter(e => e.TenantId == CurrentTenantId);

    private static void ConfigureOrganizations(ModelBuilder builder)
    {
        builder.Entity<TenantLogo>(b =>
        {
            b.ToTable("tenant_logos", OrganizationsSchema);
            b.HasKey(x => x.TenantId);
            b.Property(x => x.TenantId).ValueGeneratedNever();
            b.Property(x => x.StorageKey).HasMaxLength(120).IsRequired();
            b.Property(x => x.ContentType).HasMaxLength(20).IsRequired();
            b.Property(x => x.Sha256).HasMaxLength(64).IsRequired();
        });

        // Registro público: anterior a la existencia del gimnasio, por eso sin RLS (la API nunca lo expone; solo guarda la contraseña ya hasheada hasta confirmar el correo).
        builder.Entity<FitCore.Platform.Application.Registration.PendingRegistration>(b =>
        {
            b.ToTable("pending_registrations", OrganizationsSchema);
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Email).HasMaxLength(256).IsRequired();
            b.Property(x => x.EmailNormalized).HasMaxLength(256).IsRequired();
            b.Property(x => x.OwnerFullName).HasMaxLength(200).IsRequired();
            b.Property(x => x.PasswordHash).HasMaxLength(400).IsRequired();
            b.Property(x => x.GymName).HasMaxLength(200).IsRequired();
            b.Property(x => x.Slug).HasMaxLength(40).IsRequired();
            b.Property(x => x.PlanCode).HasMaxLength(40).IsRequired();
            b.Property(x => x.Locale).HasMaxLength(10).IsRequired();
            b.Property(x => x.TimeZone).HasMaxLength(64).IsRequired();
            b.Property(x => x.TermsVersion).HasMaxLength(60).IsRequired();
            b.Property(x => x.PrivacyVersion).HasMaxLength(60).IsRequired();
            b.Property(x => x.Status).HasMaxLength(20).IsRequired();
            b.HasIndex(x => new { x.EmailNormalized, x.Status });
            b.HasIndex(x => new { x.Slug, x.Status });
            b.HasIndex(x => x.CreatedAt);
        });

        builder.Entity<FitCore.Platform.Application.Registration.TrialClaim>(b =>
        {
            b.ToTable("trial_claims", OrganizationsSchema);
            b.HasKey(x => x.EmailNormalized);
            b.Property(x => x.EmailNormalized).HasMaxLength(256).ValueGeneratedNever();
            b.Property(x => x.PlanCode).HasMaxLength(40).IsRequired();
        });

        builder.Entity<FitCore.Platform.Application.Registration.LegalAcceptance>(b =>
        {
            b.ToTable("legal_acceptances", OrganizationsSchema);
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Document).HasMaxLength(30).IsRequired();
            b.Property(x => x.Version).HasMaxLength(60).IsRequired();
            b.HasIndex(x => new { x.TenantId, x.UserId });
        });

        builder.Entity<FitCore.Platform.Application.Invitations.Invitation>(b =>
        {
            b.ToTable("invitations", IdentitySchema);
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Email).HasMaxLength(256).IsRequired();
            b.Property(x => x.EmailNormalized).HasMaxLength(256).IsRequired();
            b.Property(x => x.Status).HasMaxLength(20).IsRequired();
            b.Property(x => x.BranchIds).HasColumnType("uuid[]");
            b.HasIndex(x => new { x.TenantId, x.Status, x.CreatedAt });
            b.HasIndex(x => new { x.TenantId, x.EmailNormalized }).HasFilter("status = 'Pending'").IsUnique();
        });

        builder.Entity<FitCore.Platform.Application.Onboarding.TenantOnboarding>(b =>
        {
            b.ToTable("tenant_onboarding", OrganizationsSchema);
            b.HasKey(x => x.TenantId);
            b.Property(x => x.TenantId).ValueGeneratedNever();
        });

        builder.Entity<Tenant>(b =>
        {
            b.ToTable("tenants", OrganizationsSchema);
            b.HasKey(t => t.Id);
            b.Property(t => t.Slug).HasMaxLength(40).IsRequired();
            b.Property(t => t.Name).HasMaxLength(200).IsRequired();
            b.Property(t => t.TimeZone).HasMaxLength(64).IsRequired();
            b.Property(t => t.DefaultCurrency).HasMaxLength(3).IsRequired();
            b.Property(t => t.Locale).HasMaxLength(10).IsRequired();
            b.Property(t => t.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(t => t.ContactEmail).HasMaxLength(256);
            b.Property(t => t.ContactPhone).HasMaxLength(30);
            b.Property(t => t.Country).HasMaxLength(2).IsFixedLength().HasDefaultValue("EC");
            b.Property(t => t.SetupStatus).HasConversion<string>().HasMaxLength(20).HasDefaultValue(TenantSetupStatus.Ready);
            b.HasIndex(t => t.Slug).IsUnique();
            AddXminConcurrency(b);
        });

        builder.Entity<Branch>(b =>
        {
            b.ToTable("branches", OrganizationsSchema);
            b.HasKey(x => x.Id);
            b.HasAlternateKey(x => new { x.TenantId, x.Id });
            b.Property(x => x.Code).HasMaxLength(20).IsRequired();
            b.Property(x => x.Name).HasMaxLength(200).IsRequired();
            b.Property(x => x.TimeZone).HasMaxLength(64).IsRequired();
            b.Property(x => x.SriEstablishmentCode).HasMaxLength(3).IsFixedLength();
            b.Property(x => x.Address).HasMaxLength(300);
            b.Property(x => x.Phone).HasMaxLength(30);
            b.Property(x => x.Hours).HasMaxLength(1000);
            b.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
            b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            AddXminConcurrency(b);
        });
    }

    private static void ConfigureIdentity(ModelBuilder builder)
    {
        builder.Entity<AppUser>(b =>
        {
            b.ToTable("app_users", IdentitySchema);
            RemoveGlobalUniqueIndex(b.Metadata, nameof(AppUser.NormalizedUserName));
            b.HasAlternateKey(u => new { u.TenantId, u.Id });
            b.Property(u => u.FullName).HasMaxLength(200).IsRequired();
            b.Property(u => u.AccountKind).HasConversion<string>().HasMaxLength(10).HasDefaultValue(AccountKind.Staff);
            b.HasIndex(u => new { u.TenantId, u.NormalizedEmail }).IsUnique();
            b.HasIndex(u => new { u.TenantId, u.NormalizedUserName }).IsUnique();
            b.HasOne<Tenant>().WithMany().HasForeignKey(u => u.TenantId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<AppRole>(b =>
        {
            b.ToTable("app_roles", IdentitySchema);
            RemoveGlobalUniqueIndex(b.Metadata, nameof(AppRole.NormalizedName));
            b.HasAlternateKey(r => new { r.TenantId, r.Id });
            b.Property(r => r.Description).HasMaxLength(300);
            b.HasIndex(r => new { r.TenantId, r.NormalizedName }).IsUnique();
            b.HasOne<Tenant>().WithMany().HasForeignKey(r => r.TenantId).OnDelete(DeleteBehavior.Restrict);
        });

        // Las FK compuestas (tenant_id, user_id) y (tenant_id, role_id) de user_roles se crean por SQL en la migración de RLS.
        builder.Entity<AppUserRole>(b =>
        {
            b.ToTable("user_roles", IdentitySchema);
            b.HasIndex(x => x.TenantId);
        });

        builder.Entity<PermissionEntity>(b =>
        {
            b.ToTable("permissions", IdentitySchema);
            b.HasKey(p => p.Code);
            b.Property(p => p.Code).HasMaxLength(100);
            b.Property(p => p.Module).HasMaxLength(50).IsRequired();
            b.Property(p => p.Description).HasMaxLength(300).IsRequired();
        });

        builder.Entity<RolePermission>(b =>
        {
            b.ToTable("role_permissions", IdentitySchema);
            b.HasKey(x => new { x.RoleId, x.PermissionCode });
            b.Property(x => x.PermissionCode).HasMaxLength(100);
            b.HasOne<AppRole>().WithMany().HasForeignKey(x => new { x.TenantId, x.RoleId })
                .HasPrincipalKey(r => new { r.TenantId, r.Id }).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<PermissionEntity>().WithMany().HasForeignKey(x => x.PermissionCode).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => x.TenantId);
        });

        builder.Entity<UserBranch>(b =>
        {
            b.ToTable("user_branches", IdentitySchema);
            b.HasKey(x => new { x.UserId, x.BranchId });
            b.HasOne<AppUser>().WithMany().HasForeignKey(x => new { x.TenantId, x.UserId })
                .HasPrincipalKey(u => new { u.TenantId, u.Id }).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Branch>().WithMany().HasForeignKey(x => new { x.TenantId, x.BranchId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => x.TenantId);
        });

        builder.Entity<RefreshToken>(b =>
        {
            b.ToTable("refresh_tokens", IdentitySchema);
            b.HasKey(x => x.Id);
            b.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
            b.Property(x => x.CreatedByIp).HasMaxLength(64);
            b.Property(x => x.UserAgent).HasMaxLength(300);
            b.HasIndex(x => x.TokenHash).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.FamilyId });
            b.HasOne<AppUser>().WithMany().HasForeignKey(x => new { x.TenantId, x.UserId })
                .HasPrincipalKey(u => new { u.TenantId, u.Id }).OnDelete(DeleteBehavior.Cascade);
            AddXminConcurrency(b);
        });
    }

    private void ConfigurePersistence(ModelBuilder builder)
    {
        builder.Entity<AuditLog>(b =>
        {
            b.ToTable("audit_logs", AuditSchema);
            b.HasKey(x => x.Id);
            b.Property(x => x.Action).HasMaxLength(100).IsRequired();
            b.Property(x => x.Entity).HasMaxLength(100).IsRequired();
            b.Property(x => x.EntityId).HasMaxLength(100);
            b.Property(x => x.Before).HasColumnType("jsonb");
            b.Property(x => x.After).HasColumnType("jsonb");
            b.Property(x => x.IpAddress).HasMaxLength(64);
            b.Property(x => x.CorrelationId).HasMaxLength(100);
            b.HasIndex(x => new { x.TenantId, x.OccurredAt });
            b.HasIndex(x => new { x.TenantId, x.Entity, x.EntityId });
            b.HasQueryFilter(x => x.TenantId == CurrentTenantId);
        });

        // Sin RLS: es una tabla interna que la API nunca expone; el Worker la procesa entre tenants.
        builder.Entity<OutboxMessage>(b =>
        {
            b.ToTable("outbox_messages", MessagingSchema);
            b.HasKey(x => x.Id);
            b.Property(x => x.Type).HasMaxLength(200).IsRequired();
            b.Property(x => x.Payload).HasColumnType("jsonb").IsRequired();
            b.Property(x => x.Error).HasMaxLength(2000);
            b.HasIndex(x => x.OccurredAt).HasFilter("processed_at IS NULL");
        });

        builder.Entity<FitCore.Platform.Application.Email.QueuedEmail>(b =>
        {
            b.ToTable("emails", MessagingSchema);
            b.HasKey(x => x.Id);
            b.Property(x => x.ToAddress).HasMaxLength(320).IsRequired();
            b.Property(x => x.Template).HasMaxLength(60).IsRequired();
            b.Property(x => x.Subject).HasMaxLength(300).IsRequired();
            b.Property(x => x.Status).HasMaxLength(20).IsRequired();
            b.Property(x => x.LastError).HasMaxLength(200);
            b.HasIndex(x => new { x.Status, x.NextAttemptAt });
            b.HasIndex(x => x.CreatedAt);
        });

        // Sin RLS: se consumen antes de saber a qué gimnasio pertenecen. Solo guardan el hash del token.
        builder.Entity<FitCore.Platform.Application.AccountTokens.AccountToken>(b =>
        {
            b.ToTable("account_tokens", IdentitySchema);
            b.HasKey(x => x.Id);
            b.Property(x => x.Purpose).HasMaxLength(40).IsRequired();
            b.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
            b.HasIndex(x => x.TokenHash).IsUnique();
            b.HasIndex(x => new { x.Purpose, x.SubjectId });
            b.HasIndex(x => x.ExpiresAt);
        });
    }

    private static void AddXminConcurrency<T>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<T> b)
        where T : class =>
        b.Property<uint>("Version").IsRowVersion();

    private static void RemoveGlobalUniqueIndex(IMutableEntityType entityType, string propertyName)
    {
        var index = entityType.GetIndexes().FirstOrDefault(i => i.Properties.Count == 1 && i.Properties[0].Name == propertyName);
        if (index is not null)
        {
            entityType.RemoveIndex(index);
        }
    }
}
