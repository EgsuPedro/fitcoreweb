using FitCore.Commercial.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FitCore.Commercial.Persistence.Configurations;

internal sealed class MembershipPlanConfiguration : IEntityTypeConfiguration<MembershipPlan>
{
    public void Configure(EntityTypeBuilder<MembershipPlan> b)
    {
        b.ToTable("membership_plans", CommercialDbContext.MembersSchema);
        b.HasKey(x => x.Id);
        b.HasAlternateKey(x => new { x.TenantId, x.Id });
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.Description).HasMaxLength(500);
        b.Property(x => x.Price).HasPrecision(12, 2);
        b.Property(x => x.DurationUnit).HasConversion<string>().HasMaxLength(10);
        b.Property(x => x.AllowedAccess).HasMaxLength(500);
        b.Property(x => x.Restrictions).HasMaxLength(500);
        b.Property(x => x.CommercialTerms).HasMaxLength(1000);
        b.Property(x => x.TaxKind).HasConversion<string>().HasMaxLength(15);
        b.Property<uint>("Version").IsRowVersion();
        b.HasIndex(x => new { x.TenantId, x.IsActive });

        b.HasMany(x => x.Branches).WithOne()
            .HasForeignKey(x => new { x.TenantId, x.PlanId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id })
            .OnDelete(DeleteBehavior.Cascade);
        b.Navigation(x => x.Branches).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class PlanBranchConfiguration : IEntityTypeConfiguration<PlanBranch>
{
    public void Configure(EntityTypeBuilder<PlanBranch> b)
    {
        b.ToTable("plan_branches", CommercialDbContext.MembersSchema);
        b.HasKey(x => new { x.PlanId, x.BranchId });
        b.HasIndex(x => x.TenantId);
    }
}

internal sealed class PromotionConfiguration : IEntityTypeConfiguration<Promotion>
{
    public void Configure(EntityTypeBuilder<Promotion> b)
    {
        b.ToTable("promotions", CommercialDbContext.MembersSchema);
        b.HasKey(x => x.Id);
        b.HasAlternateKey(x => new { x.TenantId, x.Id });
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.Code).HasMaxLength(30);
        b.Property(x => x.DiscountType).HasConversion<string>().HasMaxLength(15);
        b.Property(x => x.Value).HasPrecision(12, 2);
        b.Property(x => x.Restrictions).HasMaxLength(500);
        b.Property<uint>("Version").IsRowVersion();
        b.HasIndex(x => new { x.TenantId, x.Code }).IsUnique().HasFilter("code IS NOT NULL");

        b.HasMany(x => x.Plans).WithOne()
            .HasForeignKey(x => new { x.TenantId, x.PromotionId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id })
            .OnDelete(DeleteBehavior.Cascade);
        b.Navigation(x => x.Plans).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class PromotionPlanConfiguration : IEntityTypeConfiguration<PromotionPlan>
{
    public void Configure(EntityTypeBuilder<PromotionPlan> b)
    {
        b.ToTable("promotion_plans", CommercialDbContext.MembersSchema);
        b.HasKey(x => new { x.PromotionId, x.PlanId });
        b.HasOne<MembershipPlan>().WithMany()
            .HasForeignKey(x => new { x.TenantId, x.PlanId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id })
            .OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.TenantId);
    }
}

internal sealed class CommercialSettingsConfiguration : IEntityTypeConfiguration<CommercialSettings>
{
    public void Configure(EntityTypeBuilder<CommercialSettings> b)
    {
        b.ToTable("commercial_settings", CommercialDbContext.MembersSchema);
        b.HasKey(x => x.TenantId);
        b.Property(x => x.AccessPolicy).HasConversion<string>().HasMaxLength(30);
        b.Property(x => x.DueDays).HasColumnName("default_due_days");
        b.Ignore(x => x.StaffPhones);
        b.Property(x => x.StaffAlertPhones).HasMaxLength(200).IsRequired().HasDefaultValue(string.Empty);
        b.Property(x => x.NotificationsEnabled).HasDefaultValue(false);
    }
}

internal sealed class TaxRateConfiguration : IEntityTypeConfiguration<TaxRate>
{
    public void Configure(EntityTypeBuilder<TaxRate> b)
    {
        b.ToTable("tax_rates", CommercialDbContext.BillingSchema);
        b.HasKey(x => x.Id);
        b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(15);
        b.Property(x => x.Percent).HasPrecision(5, 2);
        b.HasIndex(x => new { x.Kind, x.ValidFrom }).IsUnique();
    }
}
