using FitCore.Classes.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FitCore.Classes.Persistence.Configurations;

internal sealed class GroupClassConfiguration : IEntityTypeConfiguration<GroupClass>
{
    public void Configure(EntityTypeBuilder<GroupClass> b)
    {
        b.ToTable("group_classes", ClassesDbContext.Schema);
        b.HasKey(x => x.Id);
        b.HasAlternateKey(x => new { x.TenantId, x.Id });
        b.Property(x => x.Name).HasMaxLength(GroupClass.MaxNameLength).IsRequired();
        b.Property(x => x.Description).HasMaxLength(GroupClass.MaxDescriptionLength);
        b.Property(x => x.Requirements).HasMaxLength(GroupClass.MaxRequirementsLength);
        b.Property(x => x.Activity).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Level).HasConversion<string>().HasMaxLength(20);
        b.Property<uint>("Version").IsRowVersion();

        b.HasIndex(x => new { x.TenantId, x.BranchId, x.IsActive });
        b.HasIndex(x => new { x.TenantId, x.InstructorId });
        b.HasIndex(x => new { x.TenantId, x.Activity });
    }
}
