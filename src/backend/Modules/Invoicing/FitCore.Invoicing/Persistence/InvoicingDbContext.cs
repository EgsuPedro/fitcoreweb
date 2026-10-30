using FitCore.Application.Core;
using FitCore.Invoicing.Domain;
using FitCore.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FitCore.Invoicing.Persistence;

/// <summary>
/// Contexto del módulo de facturación electrónica. Todo lleva <c>tenant_id</c>, filtro global y Row-Level Security forzada, como el resto de módulos de
/// negocio. Sus secretos (certificados) se guardan cifrados y nunca se auditan ni se devuelven.
/// </summary>
public sealed class InvoicingDbContext(DbContextOptions<InvoicingDbContext> options, ITenantContext tenantContext) : DbContext(options)
{
    public const string Schema = "invoicing";

    internal DbSet<IssuerProfile> Issuers => Set<IssuerProfile>();
    internal DbSet<EmissionPoint> EmissionPoints => Set<EmissionPoint>();
    internal DbSet<SigningCertificate> Certificates => Set<SigningCertificate>();
    internal DbSet<BranchRef> Branches => Set<BranchRef>();

    // Guid.Empty nunca coincide con un gimnasio real: sin tenant activo no se ve ninguna fila.
    internal Guid CurrentTenantId => tenantContext.TenantId ?? Guid.Empty;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<IssuerProfile>(b =>
        {
            b.ToTable("issuer_profiles");
            b.HasKey(x => x.TenantId);
            b.Property(x => x.TenantId).ValueGeneratedNever();
            b.Property(x => x.Ruc).HasMaxLength(13).IsFixedLength().IsRequired();
            b.Property(x => x.LegalName).HasMaxLength(300).IsRequired();
            b.Property(x => x.TradeName).HasMaxLength(300);
            b.Property(x => x.HeadOfficeAddress).HasMaxLength(300).IsRequired();
            b.Property(x => x.TaxRegime).HasConversion<string>().HasMaxLength(30);
            b.Property(x => x.SpecialTaxpayerNumber).HasMaxLength(13);
            b.Property(x => x.WithholdingAgentNumber).HasMaxLength(13);
            b.Property(x => x.Environment).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.EmissionType).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.NotificationEmail).HasMaxLength(256);
            b.HasQueryFilter(x => x.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<EmissionPoint>(b =>
        {
            b.ToTable("emission_points");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.EstablishmentCode).HasMaxLength(3).IsFixedLength().IsRequired();
            b.Property(x => x.PointCode).HasMaxLength(3).IsFixedLength().IsRequired();
            b.Property(x => x.EstablishmentAddress).HasMaxLength(300).IsRequired();
            b.HasIndex(x => new { x.TenantId, x.EstablishmentCode, x.PointCode }).IsUnique();
            b.HasQueryFilter(x => x.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<SigningCertificate>(b =>
        {
            b.ToTable("signing_certificates");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Subject).HasMaxLength(500).IsRequired();
            b.Property(x => x.Issuer).HasMaxLength(500).IsRequired();
            b.Property(x => x.SerialNumber).HasMaxLength(100).IsRequired();
            b.Property(x => x.Thumbprint).HasMaxLength(64).IsFixedLength().IsRequired();
            b.Property(x => x.SubjectRuc).HasMaxLength(13);
            b.Property(x => x.EncryptedP12).HasColumnType("bytea");
            b.Property(x => x.EncryptedPassword).HasColumnType("bytea");
            b.HasIndex(x => x.TenantId).HasFilter("is_active").IsUnique();
            b.HasQueryFilter(x => x.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<BranchRef>(b =>
        {
            b.ToTable("branches", "organizations", t => t.ExcludeFromMigrations());
            b.HasKey(x => x.Id);
            b.HasQueryFilter(x => x.TenantId == CurrentTenantId);
        });

        // La auditoría y la outbox pertenecen a Platform: aquí solo se mapean para que el interceptor compartido escriba en ellas dentro de la misma transacción.
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

/// <summary>Sucursal (solo lectura): para mostrar el nombre de la sucursal de un punto de emisión y comprobar que existe.</summary>
internal sealed class BranchRef
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public string Code { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? SriEstablishmentCode { get; init; }
    public bool IsActive { get; init; }
}
