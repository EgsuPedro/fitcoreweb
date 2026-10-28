using FitCore.Wellness.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FitCore.Wellness.Persistence.Configurations;

internal sealed class NutritionPlanConfiguration : IEntityTypeConfiguration<NutritionPlan>
{
    public void Configure(EntityTypeBuilder<NutritionPlan> b)
    {
        b.ToTable("plans", WellnessDbContext.NutritionSchema);
        b.HasKey(x => x.Id);
        b.HasAlternateKey(x => new { x.TenantId, x.Id });
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(10);
        b.Property<uint>("Version").IsRowVersion();
        b.HasIndex(x => new { x.TenantId, x.Status });
    }
}

internal sealed class NutritionPlanVersionConfiguration : IEntityTypeConfiguration<NutritionPlanVersion>
{
    public void Configure(EntityTypeBuilder<NutritionPlanVersion> b)
    {
        b.ToTable("plan_versions", WellnessDbContext.NutritionSchema);
        b.HasKey(x => x.Id);
        b.HasAlternateKey(x => new { x.TenantId, x.Id });
        b.Property(x => x.Name).HasMaxLength(120).IsRequired();
        b.Property(x => x.Goal).HasMaxLength(300);
        b.Property(x => x.Description).HasMaxLength(2000);
        b.Property(x => x.Recommendations).HasMaxLength(2000);
        b.Property(x => x.ChangeNote).HasMaxLength(300);
        b.HasOne<NutritionPlan>().WithMany().HasForeignKey(x => new { x.TenantId, x.PlanId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.PlanId, x.Number }).IsUnique();
    }
}

internal sealed class NutritionMealConfiguration : IEntityTypeConfiguration<NutritionMeal>
{
    public void Configure(EntityTypeBuilder<NutritionMeal> b)
    {
        b.ToTable("meals", WellnessDbContext.NutritionSchema);
        b.HasKey(x => x.Id);
        b.HasAlternateKey(x => new { x.TenantId, x.Id });
        b.Property(x => x.Name).HasMaxLength(80).IsRequired();
        b.Property(x => x.Notes).HasMaxLength(500);
        b.HasOne<NutritionPlanVersion>().WithMany().HasForeignKey(x => new { x.TenantId, x.VersionId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.VersionId, x.Position }).IsUnique();
    }
}

internal sealed class NutritionMealItemConfiguration : IEntityTypeConfiguration<NutritionMealItem>
{
    public void Configure(EntityTypeBuilder<NutritionMealItem> b)
    {
        b.ToTable("meal_items", WellnessDbContext.NutritionSchema);
        b.HasKey(x => x.Id);
        b.HasAlternateKey(x => new { x.TenantId, x.Id });
        b.Property(x => x.FoodName).HasMaxLength(120).IsRequired();
        b.Property(x => x.Quantity).HasPrecision(9, 2);
        b.Property(x => x.Unit).HasMaxLength(20).IsRequired();
        b.Property(x => x.ProteinG).HasPrecision(6, 1);
        b.Property(x => x.CarbsG).HasPrecision(6, 1);
        b.Property(x => x.FatG).HasPrecision(6, 1);
        b.Property(x => x.Notes).HasMaxLength(500);
        b.HasOne<NutritionMeal>().WithMany().HasForeignKey(x => new { x.TenantId, x.MealId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.MealId, x.Position }).IsUnique();
    }
}

internal sealed class NutritionAssignmentConfiguration : IEntityTypeConfiguration<NutritionAssignment>
{
    public void Configure(EntityTypeBuilder<NutritionAssignment> b)
    {
        b.ToTable("nutrition_assignments", WellnessDbContext.NutritionSchema);
        b.HasKey(x => x.Id);
        b.HasAlternateKey(x => new { x.TenantId, x.Id });
        b.Ignore(x => x.LastDay);
        b.Property(x => x.EndReason).HasMaxLength(300);
        b.HasOne<NutritionPlan>().WithMany().HasForeignKey(x => new { x.TenantId, x.PlanId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<NutritionPlanVersion>().WithMany().HasForeignKey(x => new { x.TenantId, x.PlanVersionId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);

        // Un alumno tiene un solo plan sin cerrar: lo decide la base si dos asignaciones llegan a la vez.
        b.HasIndex(x => x.MemberId).IsUnique().HasFilter("ended_on IS NULL").HasDatabaseName("ux_nutrition_assignments_open_member");
        b.HasIndex(x => new { x.TenantId, x.MemberId, x.StartDate });
        b.HasIndex(x => new { x.TenantId, x.PlanId });
    }
}

internal sealed class DietaryProfileConfiguration : IEntityTypeConfiguration<DietaryProfile>
{
    public void Configure(EntityTypeBuilder<DietaryProfile> b)
    {
        b.ToTable("dietary_profiles", WellnessDbContext.NutritionSchema);
        b.HasKey(x => x.Id);
        b.Property(x => x.Allergies).HasColumnType("text[]");
        b.Property(x => x.Intolerances).HasColumnType("text[]");
        b.Property(x => x.Restrictions).HasColumnType("text[]");
        b.Property(x => x.Notes).HasMaxLength(DietaryProfile.MaxNotesLength);
        b.HasIndex(x => new { x.TenantId, x.MemberId, x.RecordedAt });
    }
}

internal sealed class MealLogConfiguration : IEntityTypeConfiguration<MealLog>
{
    public void Configure(EntityTypeBuilder<MealLog> b)
    {
        b.ToTable("meal_logs", WellnessDbContext.NutritionSchema);
        b.HasKey(x => x.Id);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(10);
        b.Property(x => x.Notes).HasMaxLength(MealLog.MaxNotesLength);
        b.HasOne<NutritionAssignment>().WithMany().HasForeignKey(x => new { x.TenantId, x.AssignmentId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<NutritionPlanVersion>().WithMany().HasForeignKey(x => new { x.TenantId, x.PlanVersionId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<NutritionMeal>().WithMany().HasForeignKey(x => new { x.TenantId, x.MealId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);

        // Una comida se registra una vez por día (también lo impide la base si dos registros llegan a la vez).
        b.HasIndex(x => new { x.MemberId, x.MealId, x.LoggedOn }).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.MemberId, x.LoggedOn });
    }
}
