using FitCore.Application.Core;
using FitCore.Commercial.Domain;
using FitCore.Platform.Persistence;
using FitCore.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace FitCore.Commercial.Persistence;

/// <summary>
/// Contexto del módulo comercial. Mismo aislamiento que Platform: filtro global de EF + Row-Level Security
/// (cada conexión fija <c>app.tenant_id</c> con <see cref="TenantConnectionInterceptor"/>).
/// </summary>
public sealed class CommercialDbContext(DbContextOptions<CommercialDbContext> options, ITenantContext tenantContext)
    : DbContext(options)
{
    public const string MembersSchema = "members";
    public const string BillingSchema = "billing";
    public const string CashSchema = "cash";
    public const string AccessSchema = "access";

    public DbSet<Member> Members => Set<Member>();

    /// <summary>Sucursales del núcleo (solo lectura: se crean y editan en Platform).</summary>
    internal DbSet<BranchRef> BranchRefs => Set<BranchRef>();

    // Guid.Empty nunca coincide con un tenant real: sin tenant activo no se ve ninguna fila.
    private Guid CurrentTenantId => tenantContext.TenantId ?? Guid.Empty;

    /// <summary>Siguiente valor de un contador por gimnasio (código de alumno, número de recibo…). Atómico y sin huecos por concurrencia.</summary>
    public async Task<long> NextCounterAsync(Guid tenantId, string key, CancellationToken cancellationToken)
    {
        var values = await Database
            .SqlQuery<long>($"""
                INSERT INTO members.counters (tenant_id, key, value) VALUES ({tenantId}, {key}, 1)
                ON CONFLICT (tenant_id, key) DO UPDATE SET value = counters.value + 1
                RETURNING value AS "Value"
                """)
            .ToListAsync(cancellationToken);
        return values.Single();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CommercialDbContext).Assembly);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes().ToList())
        {
            if (typeof(IHasDomainEvents).IsAssignableFrom(entityType.ClrType))
            {
                modelBuilder.Entity(entityType.ClrType).Ignore(nameof(IHasDomainEvents.DomainEvents));
            }

            if (typeof(ITenantOwned).IsAssignableFrom(entityType.ClrType))
            {
                typeof(CommercialDbContext)
                    .GetMethod(nameof(ApplyTenantFilter), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .MakeGenericMethod(entityType.ClrType)
                    .Invoke(this, [modelBuilder]);
            }
        }

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

    private void ApplyTenantFilter<T>(ModelBuilder builder)
        where T : class, ITenantOwned =>
        builder.Entity<T>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
}

/// <summary>Vista mínima de <c>organizations.branches</c> para validar sucursales sin depender del DbContext de Platform.</summary>
internal sealed class BranchRef : ITenantOwned
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public string Code { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public bool IsActive { get; init; }
}
