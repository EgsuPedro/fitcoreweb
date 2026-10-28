using FitCore.Application.Core;
using Microsoft.EntityFrameworkCore;

namespace FitCore.Analytics.Persistence;

/// <summary>
/// Contexto de solo lectura del módulo de analítica. Todo lo que lee pertenece a otros módulos y se mapea como vista mínima
/// (<c>ExcludeFromMigrations</c>): este módulo no crea ni modifica datos de negocio. El aislamiento es el de siempre: filtro global de EF
/// más Row-Level Security (la conexión fija <c>app.tenant_id</c>).
/// </summary>
public sealed class AnalyticsDbContext(DbContextOptions<AnalyticsDbContext> options, ITenantContext tenantContext)
    : DbContext(options)
{
    public const string Schema = "analytics";

    internal DbSet<TenantRef> TenantRefs => Set<TenantRef>();
    internal DbSet<BranchRef> BranchRefs => Set<BranchRef>();

    // Guid.Empty nunca coincide con un tenant real: sin tenant activo no se ve ninguna fila.
    private Guid CurrentTenantId => tenantContext.TenantId ?? Guid.Empty;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<TenantRef>(b =>
        {
            b.ToTable("tenants", "organizations", t => t.ExcludeFromMigrations());
            b.HasKey(x => x.Id);
            b.HasQueryFilter(x => x.Id == CurrentTenantId);
        });

        modelBuilder.Entity<BranchRef>(b =>
        {
            b.ToTable("branches", "organizations", t => t.ExcludeFromMigrations());
            b.HasKey(x => x.Id);
            b.HasQueryFilter(x => x.TenantId == CurrentTenantId);
        });
    }
}

/// <summary>Vista mínima de <c>organizations.tenants</c>: la zona horaria del gimnasio.</summary>
internal sealed class TenantRef
{
    public Guid Id { get; init; }
    public string TimeZone { get; init; } = string.Empty;
}

/// <summary>Vista mínima de <c>organizations.branches</c>.</summary>
internal sealed class BranchRef
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public string Code { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string TimeZone { get; init; } = string.Empty;
    public bool IsActive { get; init; }
}
