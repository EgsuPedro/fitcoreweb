using FitCore.Commercial.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FitCore.Commercial.Persistence.Configurations;

internal sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> b)
    {
        b.ToTable("payments", CommercialDbContext.BillingSchema);
        b.HasKey(x => x.Id);
        b.HasAlternateKey(x => new { x.TenantId, x.Id });
        b.Property(x => x.ReceiptNumber).HasMaxLength(20).IsRequired();
        b.Property(x => x.Method).HasConversion<string>().HasMaxLength(15);
        b.Property(x => x.MethodDetail).HasMaxLength(100);
        b.Property(x => x.Amount).HasPrecision(12, 2);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Reference).HasMaxLength(100);
        b.Property(x => x.BankName).HasMaxLength(100);
        b.Property(x => x.Notes).HasMaxLength(500);
        b.Property(x => x.ReversalReason).HasMaxLength(300);
        b.Property<uint>("Version").IsRowVersion();

        b.HasOne<Member>().WithMany().HasForeignKey(x => new { x.TenantId, x.MemberId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Allocations).WithOne()
            .HasForeignKey(x => new { x.TenantId, x.PaymentId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id })
            .OnDelete(DeleteBehavior.Restrict);
        b.Navigation(x => x.Allocations).UsePropertyAccessMode(PropertyAccessMode.Field);

        b.HasIndex(x => new { x.TenantId, x.ReceiptNumber }).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.MemberId, x.PaidAt });
        b.HasIndex(x => new { x.TenantId, x.BranchId, x.PaidAt });
        b.HasIndex(x => new { x.TenantId, x.Status });
    }
}

internal sealed class PaymentAllocationConfiguration : IEntityTypeConfiguration<PaymentAllocation>
{
    public void Configure(EntityTypeBuilder<PaymentAllocation> b)
    {
        b.ToTable("payment_allocations", CommercialDbContext.BillingSchema);
        b.HasKey(x => x.Id);
        b.Property(x => x.Amount).HasPrecision(12, 2);
        b.HasOne<PaymentObligation>().WithMany()
            .HasForeignKey(x => new { x.TenantId, x.ObligationId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id })
            .OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.PaymentId, x.ObligationId }).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.ObligationId });
    }
}

internal sealed class IdempotencyKeyConfiguration : IEntityTypeConfiguration<IdempotencyKey>
{
    public void Configure(EntityTypeBuilder<IdempotencyKey> b)
    {
        b.ToTable("idempotency_keys", CommercialDbContext.BillingSchema);
        b.HasKey(x => new { x.TenantId, x.Key });
        b.Property(x => x.Key).HasMaxLength(100);
        b.Property(x => x.Scope).HasMaxLength(50).IsRequired();
        b.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
        b.Property(x => x.ResponseBody).HasColumnType("jsonb");
    }
}
