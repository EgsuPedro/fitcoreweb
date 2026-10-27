using FitCore.Commercial.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FitCore.Commercial.Persistence.Configurations;

internal sealed class MemberConfiguration : IEntityTypeConfiguration<Member>
{
    public void Configure(EntityTypeBuilder<Member> b)
    {
        b.ToTable("members", CommercialDbContext.MembersSchema);
        b.HasKey(x => x.Id);
        b.HasAlternateKey(x => new { x.TenantId, x.Id });
        b.Ignore(x => x.FullName);
        b.Ignore(x => x.IsActive);
        b.Property(x => x.Code).HasMaxLength(20).IsRequired();
        b.Property(x => x.FirstName).HasMaxLength(100).IsRequired();
        b.Property(x => x.LastName).HasMaxLength(100).IsRequired();
        b.Property(x => x.IdType).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.IdNumber).HasMaxLength(20).IsRequired();
        b.Property(x => x.Email).HasMaxLength(256);
        b.Property(x => x.Phone).HasMaxLength(30);
        b.Property(x => x.Address).HasMaxLength(300);
        b.Property(x => x.PhotoUrl).HasMaxLength(500);
        b.Property(x => x.EmergencyContactName).HasMaxLength(150);
        b.Property(x => x.EmergencyContactPhone).HasMaxLength(30);
        b.Property(x => x.EmergencyContactRelationship).HasMaxLength(50);
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property<uint>("Version").IsRowVersion();

        // Detección de duplicados dentro del gimnasio: el mismo documento no puede registrarse dos veces.
        b.HasIndex(x => new { x.TenantId, x.IdType, x.IdNumber }).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.BranchId, x.Status });
        b.HasIndex(x => new { x.TenantId, x.LastName, x.FirstName });
    }
}

internal sealed class BranchRefConfiguration : IEntityTypeConfiguration<BranchRef>
{
    public void Configure(EntityTypeBuilder<BranchRef> b)
    {
        b.ToTable("branches", "organizations", t => t.ExcludeFromMigrations());
        b.HasKey(x => x.Id);
    }
}

/// <summary>Contador por gimnasio y clave (código de alumno, número de recibo por sucursal…).</summary>
internal sealed class Counter : FitCore.SharedKernel.ITenantOwned
{
    public Guid TenantId { get; init; }
    public string Key { get; init; } = string.Empty;
    public long Value { get; set; }
}

internal sealed class CounterConfiguration : IEntityTypeConfiguration<Counter>
{
    public void Configure(EntityTypeBuilder<Counter> b)
    {
        b.ToTable("counters", CommercialDbContext.MembersSchema);
        b.HasKey(x => new { x.TenantId, x.Key });
        b.Property(x => x.Key).HasMaxLength(100);
    }
}
