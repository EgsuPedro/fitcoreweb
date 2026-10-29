using FitCore.Application.Core;
using FitCore.Platform.Persistence;
using FitCore.Subscriptions.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FitCore.Subscriptions.Persistence;

/// <summary>
/// Contexto del módulo de suscripciones SaaS. Los planes son globales (los administra el equipo de plataforma); lo que dependa de un
/// gimnasio llevará <c>tenant_id</c>, filtro global y Row-Level Security como en el resto de módulos.
/// </summary>
public sealed class SubscriptionsDbContext(DbContextOptions<SubscriptionsDbContext> options, ITenantContext tenantContext)
    : DbContext(options)
{
    public const string Schema = "subscriptions";

    internal DbSet<Plan> Plans => Set<Plan>();
    internal DbSet<PlanVersion> PlanVersions => Set<PlanVersion>();
    internal DbSet<PlanFeature> PlanFeatures => Set<PlanFeature>();
    internal DbSet<PlanLimit> PlanLimits => Set<PlanLimit>();
    internal DbSet<Subscription> Subscriptions => Set<Subscription>();
    internal DbSet<SubscriptionEvent> SubscriptionEvents => Set<SubscriptionEvent>();
    internal DbSet<SubscriptionPlanChange> PlanChanges => Set<SubscriptionPlanChange>();
    internal DbSet<TenantEntitlement> Entitlements => Set<TenantEntitlement>();

    // Vistas de solo lectura de otros módulos, para medir el consumo de los límites (RLS y filtro limitan a un solo gimnasio).
    internal DbSet<BranchRef> Branches => Set<BranchRef>();
    internal DbSet<UserRef> Users => Set<UserRef>();
    internal DbSet<MemberRef> Members => Set<MemberRef>();

    /// <summary>Gimnasios (solo lectura, sin filtro: el equipo de plataforma los consulta a todos; el aislamiento de las suscripciones lo da RLS).</summary>
    internal DbSet<TenantRef> Tenants => Set<TenantRef>();

    // Guid.Empty nunca coincide con un tenant real: sin tenant activo no se ve ninguna fila de gimnasio.
    internal Guid CurrentTenantId => tenantContext.TenantId ?? Guid.Empty;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfiguration(new PlanConfiguration());
        modelBuilder.ApplyConfiguration(new PlanVersionConfiguration());
        modelBuilder.ApplyConfiguration(new PlanFeatureConfiguration());
        modelBuilder.ApplyConfiguration(new PlanLimitConfiguration());
        modelBuilder.ApplyConfiguration(new SubscriptionConfiguration());
        modelBuilder.ApplyConfiguration(new SubscriptionEventConfiguration());
        modelBuilder.ApplyConfiguration(new SubscriptionPlanChangeConfiguration());
        modelBuilder.Entity<SubscriptionPlanChange>().HasQueryFilter(x => x.TenantId == CurrentTenantId);
        modelBuilder.ApplyConfiguration(new TenantEntitlementConfiguration());
        modelBuilder.Entity<TenantEntitlement>().HasQueryFilter(x => x.TenantId == CurrentTenantId);

        modelBuilder.Entity<BranchRef>(b =>
        {
            b.ToTable("branches", "organizations", t => t.ExcludeFromMigrations());
            b.HasKey(x => x.Id);
            b.HasQueryFilter(x => x.TenantId == CurrentTenantId);
        });
        modelBuilder.Entity<UserRef>(b =>
        {
            b.ToTable("app_users", "identity", t => t.ExcludeFromMigrations());
            b.HasKey(x => x.Id);
            b.HasQueryFilter(x => x.TenantId == CurrentTenantId);
        });
        modelBuilder.Entity<MemberRef>(b =>
        {
            b.ToTable("members", "members", t => t.ExcludeFromMigrations());
            b.HasKey(x => x.Id);
            b.HasQueryFilter(x => x.TenantId == CurrentTenantId);
        });
        // Filtro por gimnasio del contexto actual; las operaciones de plataforma (entre gimnasios) usan IgnoreQueryFilters() y la base
        // (RLS) decide: solo el gimnasio «platform» ve y escribe las suscripciones de todos.
        modelBuilder.Entity<Subscription>().HasQueryFilter(x => x.TenantId == CurrentTenantId);
        modelBuilder.Entity<SubscriptionEvent>().HasQueryFilter(x => x.TenantId == CurrentTenantId);

        modelBuilder.Entity<TenantRef>(b =>
        {
            b.ToTable("tenants", "organizations", t => t.ExcludeFromMigrations());
            b.HasKey(x => x.Id);
        });

        // La auditoría y la outbox pertenecen a Platform: aquí solo se mapean para que el interceptor compartido escriba en ellas
        // dentro de la misma transacción. Nunca generan migraciones de este módulo.
        modelBuilder.Entity<AuditLog>(b =>
        {
            b.ToTable("audit_logs", "audit", t => t.ExcludeFromMigrations());
            b.HasKey(x => x.Id);
            b.Property(x => x.Before).HasColumnType("jsonb");
            b.Property(x => x.After).HasColumnType("jsonb");
            b.HasQueryFilter(x => x.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<OutboxMessage>(b =>
        {
            b.ToTable("outbox_messages", "messaging", t => t.ExcludeFromMigrations());
            b.HasKey(x => x.Id);
            b.Property(x => x.Payload).HasColumnType("jsonb");
        });
    }
}

internal sealed class PlanConfiguration : IEntityTypeConfiguration<Plan>
{
    public void Configure(EntityTypeBuilder<Plan> b)
    {
        b.ToTable("plans", SubscriptionsDbContext.Schema);
        b.HasKey(x => x.Id);
        // El identificador lo asigna la aplicación: sin esto EF toma una versión nueva (con Id ya asignado) por una fila existente.
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Code).HasMaxLength(40).IsRequired();
        b.Property(x => x.Name).HasMaxLength(120).IsRequired();
        b.Property(x => x.Description).HasMaxLength(1000);
        b.Property(x => x.Rules).HasMaxLength(2000);
        b.HasIndex(x => x.Code).IsUnique();
        b.HasMany(x => x.Versions).WithOne().HasForeignKey(v => v.PlanId).OnDelete(DeleteBehavior.Restrict);
        b.Navigation(x => x.Versions).AutoInclude();
        b.Ignore(x => x.Current);

        // Control de concurrencia (xmin de PostgreSQL): dos ediciones simultáneas no se pisan, la segunda recibe 409.
        b.Property<uint>("Version").IsRowVersion();
    }
}

internal sealed class PlanVersionConfiguration : IEntityTypeConfiguration<PlanVersion>
{
    public void Configure(EntityTypeBuilder<PlanVersion> b)
    {
        b.ToTable("plan_versions", SubscriptionsDbContext.Schema);
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Currency).HasMaxLength(3).IsRequired();
        b.Property(x => x.Period).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Price).HasPrecision(12, 2);
        b.Property(x => x.ChangeNote).HasMaxLength(500);
        b.HasIndex(x => new { x.PlanId, x.Number }).IsUnique();
        b.HasMany(x => x.Features).WithOne().HasForeignKey(f => f.PlanVersionId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Limits).WithOne().HasForeignKey(l => l.PlanVersionId).OnDelete(DeleteBehavior.Restrict);
        b.Navigation(x => x.Features).AutoInclude();
        b.Navigation(x => x.Limits).AutoInclude();
    }
}

internal sealed class PlanFeatureConfiguration : IEntityTypeConfiguration<PlanFeature>
{
    public void Configure(EntityTypeBuilder<PlanFeature> b)
    {
        b.ToTable("plan_features", SubscriptionsDbContext.Schema);
        b.HasKey(x => new { x.PlanVersionId, x.Code });
        b.Property(x => x.Code).HasMaxLength(40).IsRequired();
    }
}

internal sealed class PlanLimitConfiguration : IEntityTypeConfiguration<PlanLimit>
{
    public void Configure(EntityTypeBuilder<PlanLimit> b)
    {
        b.ToTable("plan_limits", SubscriptionsDbContext.Schema);
        b.HasKey(x => new { x.PlanVersionId, x.Code });
        b.Property(x => x.Code).HasMaxLength(40).IsRequired();
    }
}

/// <summary>Vista mínima de <c>organizations.tenants</c> (solo lectura): nombre, identificador, zona horaria y estado administrativo.</summary>
internal sealed class TenantRef
{
    public Guid Id { get; init; }
    public string Slug { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string TimeZone { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
}

internal sealed class SubscriptionConfiguration : IEntityTypeConfiguration<Subscription>
{
    public void Configure(EntityTypeBuilder<Subscription> b)
    {
        b.ToTable("subscriptions", SubscriptionsDbContext.Schema);
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.PlanCode).HasMaxLength(40).IsRequired();
        b.Property(x => x.PlanName).HasMaxLength(120).IsRequired();
        b.Property(x => x.Price).HasPrecision(12, 2);
        b.Property(x => x.Currency).HasMaxLength(3).IsRequired();
        b.Property(x => x.Period).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.StatusBeforeSuspension).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.CancelReason).HasMaxLength(500);
        b.Property(x => x.SuspensionReason).HasMaxLength(500);
        b.Property(x => x.ScheduledCancelReason).HasMaxLength(500);
        b.HasIndex(x => new { x.TenantId, x.CreatedAt });
        b.HasIndex(x => new { x.Status, x.NextDueOn });
        b.HasIndex(x => x.PlanVersionId);
        // Una sola suscripción vigente (no cancelada) por gimnasio: dos altas simultáneas no pueden duplicarla.
        b.HasIndex(x => x.TenantId).IsUnique().HasFilter("status <> 'Cancelled'").HasDatabaseName("ux_subscriptions_current");
        b.HasMany(x => x.Events).WithOne().HasForeignKey(e => e.SubscriptionId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.PlanChanges).WithOne().HasForeignKey(c => c.SubscriptionId).OnDelete(DeleteBehavior.Restrict);

        // Control de concurrencia (xmin): dos cambios de estado simultáneos no se pisan, el segundo recibe 409.
        b.Property<uint>("Version").IsRowVersion();
    }
}

internal sealed class SubscriptionEventConfiguration : IEntityTypeConfiguration<SubscriptionEvent>
{
    public void Configure(EntityTypeBuilder<SubscriptionEvent> b)
    {
        b.ToTable("subscription_events", SubscriptionsDbContext.Schema);
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Type).HasConversion<string>().HasMaxLength(30);
        b.Property(x => x.FromStatus).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.ToStatus).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Detail).HasMaxLength(500);
        b.HasIndex(x => new { x.SubscriptionId, x.OccurredAt });
        b.HasIndex(x => new { x.TenantId, x.OccurredAt });
    }
}

internal sealed class SubscriptionPlanChangeConfiguration : IEntityTypeConfiguration<SubscriptionPlanChange>
{
    public void Configure(EntityTypeBuilder<SubscriptionPlanChange> b)
    {
        b.ToTable("subscription_plan_changes", SubscriptionsDbContext.Schema);
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Note).HasMaxLength(500);
        b.HasIndex(x => new { x.SubscriptionId, x.RequestedAt });
        b.HasIndex(x => new { x.TenantId, x.Status });
    }
}
