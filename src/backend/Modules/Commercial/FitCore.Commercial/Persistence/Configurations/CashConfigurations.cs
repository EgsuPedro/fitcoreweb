using FitCore.Commercial.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FitCore.Commercial.Persistence.Configurations;

internal sealed class CashRegisterConfiguration : IEntityTypeConfiguration<CashRegister>
{
    public void Configure(EntityTypeBuilder<CashRegister> b)
    {
        b.ToTable("cash_registers", CommercialDbContext.CashSchema);
        b.HasKey(x => x.Id);
        b.HasAlternateKey(x => new { x.TenantId, x.Id }).HasName("ak_cash_registers_tenant_id_id");
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.HasIndex(x => new { x.TenantId, x.BranchId, x.IsActive });
    }
}

internal sealed class CashSessionConfiguration : IEntityTypeConfiguration<CashSession>
{
    public void Configure(EntityTypeBuilder<CashSession> b)
    {
        b.ToTable("cash_sessions", CommercialDbContext.CashSchema);
        b.HasKey(x => x.Id);
        b.HasAlternateKey(x => new { x.TenantId, x.Id }).HasName("ak_cash_sessions_tenant_id_id");
        b.Property(x => x.OpeningAmount).HasPrecision(12, 2);
        b.Property(x => x.CountedAmount).HasPrecision(12, 2);
        b.Property(x => x.ExpectedAtClose).HasPrecision(12, 2);
        b.Property(x => x.Difference).HasPrecision(12, 2);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(10);
        b.Property(x => x.OpenNotes).HasMaxLength(500);
        b.Property(x => x.CloseNotes).HasMaxLength(500);
        b.Property<uint>("Version").IsRowVersion();

        b.HasOne<CashRegister>().WithMany().HasForeignKey(x => new { x.TenantId, x.RegisterId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.RegisterId, x.Status });
        b.HasIndex(x => new { x.TenantId, x.OpenedBy, x.Status });
        b.HasIndex(x => new { x.TenantId, x.BranchId, x.OpenedAt });
    }
}

internal sealed class CashMovementConfiguration : IEntityTypeConfiguration<CashMovement>
{
    public void Configure(EntityTypeBuilder<CashMovement> b)
    {
        b.ToTable("cash_movements", CommercialDbContext.CashSchema);
        b.HasKey(x => x.Id);
        b.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Direction).HasConversion<string>().HasMaxLength(5);
        b.Property(x => x.Amount).HasPrecision(12, 2);
        b.Property(x => x.Reason).HasMaxLength(300);

        b.HasOne<CashSession>().WithMany().HasForeignKey(x => new { x.TenantId, x.SessionId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Payment>().WithMany().HasForeignKey(x => new { x.TenantId, x.PaymentId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.SessionId, x.CreatedAt });
        b.HasIndex(x => new { x.TenantId, x.PaymentId });
    }
}
