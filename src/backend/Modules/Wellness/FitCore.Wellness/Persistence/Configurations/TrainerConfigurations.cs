using FitCore.Wellness.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FitCore.Wellness.Persistence.Configurations;

internal sealed class TrainerProfileConfiguration : IEntityTypeConfiguration<TrainerProfile>
{
    public void Configure(EntityTypeBuilder<TrainerProfile> b)
    {
        b.ToTable("trainer_profiles", WellnessDbContext.TrainingSchema);
        b.HasKey(x => x.Id);
        b.HasAlternateKey(x => new { x.TenantId, x.Id });
        b.Ignore(x => x.IsActive);
        b.Property(x => x.Specialties).HasColumnType("text[]");
        b.Property(x => x.Certifications).HasMaxLength(TrainerProfile.MaxCertificationsLength);
        b.Property(x => x.Bio).HasMaxLength(TrainerProfile.MaxBioLength);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(10);
        b.Property<uint>("Version").IsRowVersion();

        // Un usuario es entrenador una sola vez por gimnasio.
        b.HasIndex(x => new { x.TenantId, x.UserId }).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.Status });
    }
}

internal sealed class TrainerAvailabilityConfiguration : IEntityTypeConfiguration<TrainerAvailability>
{
    public void Configure(EntityTypeBuilder<TrainerAvailability> b)
    {
        b.ToTable("trainer_availability", WellnessDbContext.TrainingSchema);
        b.HasKey(x => x.Id);
        b.Property(x => x.Day).HasConversion<int>();
        b.HasOne<TrainerProfile>().WithMany().HasForeignKey(x => new { x.TenantId, x.TrainerId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.TenantId, x.TrainerId, x.Day, x.StartTime });
    }
}

internal sealed class TrainerAssignmentConfiguration : IEntityTypeConfiguration<TrainerAssignment>
{
    public void Configure(EntityTypeBuilder<TrainerAssignment> b)
    {
        b.ToTable("trainer_assignments", WellnessDbContext.TrainingSchema);
        b.HasKey(x => x.Id);
        b.HasAlternateKey(x => new { x.TenantId, x.Id });
        b.Ignore(x => x.IsActive);
        b.Property(x => x.EndReason).HasMaxLength(TrainerAssignment.MaxReasonLength);
        b.HasOne<TrainerProfile>().WithMany().HasForeignKey(x => new { x.TenantId, x.TrainerId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);

        // Un alumno tiene un solo entrenador activo: lo decide la base cuando dos asignaciones llegan a la vez.
        b.HasIndex(x => x.MemberId).IsUnique().HasFilter("ended_on IS NULL").HasDatabaseName("ux_trainer_assignments_active_member");
        b.HasIndex(x => new { x.TenantId, x.TrainerId, x.EndedOn });
        b.HasIndex(x => new { x.TenantId, x.MemberId, x.AssignedOn });
    }
}

internal sealed class TrainerNoteConfiguration : IEntityTypeConfiguration<TrainerNote>
{
    public void Configure(EntityTypeBuilder<TrainerNote> b)
    {
        b.ToTable("trainer_notes", WellnessDbContext.TrainingSchema);
        b.HasKey(x => x.Id);
        b.Property(x => x.Text).HasMaxLength(TrainerNote.MaxLength).IsRequired();
        b.HasOne<TrainerProfile>().WithMany().HasForeignKey(x => new { x.TenantId, x.TrainerId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.MemberId, x.CreatedAt });
    }
}

internal sealed class MemberRefConfiguration : IEntityTypeConfiguration<MemberRef>
{
    public void Configure(EntityTypeBuilder<MemberRef> b)
    {
        b.ToTable("members", "members", t => t.ExcludeFromMigrations());
        b.HasKey(x => x.Id);
        b.Ignore(x => x.FullName);
        b.Ignore(x => x.IsActive);
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
