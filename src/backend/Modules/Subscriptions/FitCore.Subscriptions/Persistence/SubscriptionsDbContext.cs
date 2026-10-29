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

    // Guid.Empty nunca coincide con un tenant real: sin tenant activo no se ve ninguna fila de gimnasio.
    internal Guid CurrentTenantId => tenantContext.TenantId ?? Guid.Empty;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfiguration(new PlanConfiguration());
        modelBuilder.ApplyConfiguration(new PlanVersionConfiguration());
        modelBuilder.ApplyConfiguration(new PlanFeatureConfiguration());
        modelBuilder.ApplyConfiguration(new PlanLimitConfiguration());

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
