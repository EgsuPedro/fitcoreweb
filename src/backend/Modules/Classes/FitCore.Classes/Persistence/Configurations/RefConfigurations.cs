using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FitCore.Classes.Persistence.Configurations;

internal sealed class TrainerRefConfiguration : IEntityTypeConfiguration<TrainerRef>
{
    public void Configure(EntityTypeBuilder<TrainerRef> b)
    {
        b.ToTable("trainer_profiles", "training", t => t.ExcludeFromMigrations());
        b.HasKey(x => x.Id);
        b.Ignore(x => x.IsActive);
    }
}

internal sealed class TrainerAvailabilityRefConfiguration : IEntityTypeConfiguration<TrainerAvailabilityRef>
{
    public void Configure(EntityTypeBuilder<TrainerAvailabilityRef> b)
    {
        b.ToTable("trainer_availability", "training", t => t.ExcludeFromMigrations());
        b.HasKey(x => x.Id);
    }
}

internal sealed class UserRefConfiguration : IEntityTypeConfiguration<UserRef>
{
    public void Configure(EntityTypeBuilder<UserRef> b)
    {
        b.ToTable("app_users", "identity", t => t.ExcludeFromMigrations());
        b.HasKey(x => x.Id);
    }
}

internal sealed class UserBranchRefConfiguration : IEntityTypeConfiguration<UserBranchRef>
{
    public void Configure(EntityTypeBuilder<UserBranchRef> b)
    {
        b.ToTable("user_branches", "identity", t => t.ExcludeFromMigrations());
        b.HasKey(x => new { x.UserId, x.BranchId });
    }
}

internal sealed class UserRoleRefConfiguration : IEntityTypeConfiguration<UserRoleRef>
{
    public void Configure(EntityTypeBuilder<UserRoleRef> b)
    {
        b.ToTable("user_roles", "identity", t => t.ExcludeFromMigrations());
        b.HasKey(x => new { x.UserId, x.RoleId });
    }
}

internal sealed class RoleRefConfiguration : IEntityTypeConfiguration<RoleRef>
{
    public void Configure(EntityTypeBuilder<RoleRef> b)
    {
        b.ToTable("app_roles", "identity", t => t.ExcludeFromMigrations());
        b.HasKey(x => x.Id);
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
