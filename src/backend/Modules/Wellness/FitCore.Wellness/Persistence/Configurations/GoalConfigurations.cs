using FitCore.Wellness.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FitCore.Wellness.Persistence.Configurations;

internal sealed class GoalConfiguration : IEntityTypeConfiguration<Goal>
{
    public void Configure(EntityTypeBuilder<Goal> b)
    {
        b.ToTable("goals", WellnessDbContext.WellnessSchema);
        b.HasKey(x => x.Id);
        b.Property(x => x.Title).HasMaxLength(Goal.MaxTitleLength).IsRequired();
        b.Property(x => x.Description).HasMaxLength(Goal.MaxTextLength);
        b.Property(x => x.Category).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Metric).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(10);
        b.Property(x => x.TargetValue).HasPrecision(7, 1);
        b.Property(x => x.BaselineValue).HasPrecision(7, 1);
        b.Property(x => x.CloseNote).HasMaxLength(Goal.MaxTextLength);
        b.HasIndex(x => new { x.TenantId, x.MemberId, x.Status });
    }
}
