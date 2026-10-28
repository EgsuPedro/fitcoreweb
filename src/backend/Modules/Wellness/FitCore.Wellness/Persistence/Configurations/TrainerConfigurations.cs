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

internal sealed class ExerciseConfiguration : IEntityTypeConfiguration<Exercise>
{
    public void Configure(EntityTypeBuilder<Exercise> b)
    {
        b.ToTable("exercises", WellnessDbContext.TrainingSchema);
        b.HasKey(x => x.Id);
        b.Ignore(x => x.IsGlobal);
        b.Property(x => x.Name).HasMaxLength(120).IsRequired();
        b.Property(x => x.Description).HasMaxLength(1000);
        b.Property(x => x.PrimaryMuscle).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Equipment).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Difficulty).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.TrackingMode).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.SecondaryMuscles).HasColumnType("text[]");
        b.Property(x => x.Instructions).HasColumnType("text[]");
        b.Property(x => x.CommonMistakes).HasColumnType("text[]");
        b.Property(x => x.Warnings).HasMaxLength(1000);
        b.Property(x => x.VideoUrl).HasMaxLength(500);
        b.Property<uint>("Version").IsRowVersion();

        // Nombres únicos sin distinguir mayúsculas: entre los globales, y dentro de cada gimnasio.
        b.HasIndex(x => x.Name).IsUnique().HasFilter("tenant_id IS NULL").HasDatabaseName("ux_exercises_global_name");
        b.HasIndex(x => new { x.TenantId, x.Name }).IsUnique().HasFilter("tenant_id IS NOT NULL").HasDatabaseName("ux_exercises_tenant_name");
        b.HasIndex(x => new { x.TenantId, x.PrimaryMuscle });
        b.HasIndex(x => new { x.TenantId, x.IsActive });
    }
}
