using FitCore.Commercial.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FitCore.Commercial.Persistence.Configurations;

internal sealed class MembershipConfiguration : IEntityTypeConfiguration<Membership>
{
    public void Configure(EntityTypeBuilder<Membership> b)
    {
        b.ToTable("memberships", CommercialDbContext.MembersSchema);
        b.HasKey(x => x.Id);
        b.HasAlternateKey(x => new { x.TenantId, x.Id });
        b.Property(x => x.PlanName).HasMaxLength(100).IsRequired();
        b.Property(x => x.PlanPrice).HasPrecision(12, 2);
        b.Property(x => x.DurationUnit).HasConversion<string>().HasMaxLength(10);
        b.Property(x => x.TaxKind).HasConversion<string>().HasMaxLength(15);
        b.Property(x => x.TaxPercent).HasPrecision(5, 2);
        b.Property(x => x.AllowedAccess).HasMaxLength(500);
        b.Property(x => x.CommercialTerms).HasMaxLength(1000);
        b.Property(x => x.PromotionName).HasMaxLength(100);
        b.Property(x => x.PromotionCode).HasMaxLength(30);
        b.Property(x => x.DiscountAmount).HasPrecision(12, 2);
        b.Property(x => x.AgreedPrice).HasPrecision(12, 2);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.CancelReason).HasMaxLength(300);
        b.Property<uint>("Version").IsRowVersion();

        b.HasOne<Member>().WithMany().HasForeignKey(x => new { x.TenantId, x.MemberId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<MembershipPlan>().WithMany().HasForeignKey(x => new { x.TenantId, x.PlanId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Promotion>().WithMany().HasForeignKey(x => new { x.TenantId, x.PromotionId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => new { x.TenantId, x.MemberId, x.StartDate });
        b.HasIndex(x => new { x.TenantId, x.BranchId, x.Status, x.EndDate });
    }
}

internal sealed class MembershipEventConfiguration : IEntityTypeConfiguration<MembershipEvent>
{
    public void Configure(EntityTypeBuilder<MembershipEvent> b)
    {
        b.ToTable("membership_events", CommercialDbContext.MembersSchema);
        b.HasKey(x => x.Id);
        b.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Details).HasColumnType("jsonb");
        b.HasOne<Membership>().WithMany().HasForeignKey(x => new { x.TenantId, x.MembershipId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.MembershipId, x.OccurredAt });
    }
}

internal sealed class PaymentObligationConfiguration : IEntityTypeConfiguration<PaymentObligation>
{
    public void Configure(EntityTypeBuilder<PaymentObligation> b)
    {
        b.ToTable("payment_obligations", CommercialDbContext.BillingSchema);
        b.HasKey(x => x.Id);
        b.HasAlternateKey(x => new { x.TenantId, x.Id }).HasName("ak_payment_obligations_tenant_id_id");
        b.Property(x => x.Reference).HasMaxLength(20).IsRequired();
        b.Property(x => x.Concept).HasMaxLength(200).IsRequired();
        b.Property(x => x.OriginalAmount).HasPrecision(12, 2);
        b.Property(x => x.DiscountAmount).HasPrecision(12, 2);
        b.Property(x => x.PayableAmount).HasPrecision(12, 2);
        b.Property(x => x.Subtotal).HasPrecision(12, 2);
        b.Property(x => x.TaxAmount).HasPrecision(12, 2);
        b.Property(x => x.TaxKind).HasConversion<string>().HasMaxLength(15);
        b.Property(x => x.TaxPercent).HasPrecision(5, 2);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.VoidReason).HasMaxLength(300);
        b.Property<uint>("Version").IsRowVersion();

        b.HasOne<Member>().WithMany().HasForeignKey(x => new { x.TenantId, x.MemberId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Membership>().WithMany().HasForeignKey(x => new { x.TenantId, x.MembershipId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => new { x.TenantId, x.Reference }).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.MemberId, x.Status });
        b.HasIndex(x => new { x.TenantId, x.BranchId, x.Status, x.DueDate });
        b.HasIndex(x => new { x.TenantId, x.MembershipId });
    }
}
