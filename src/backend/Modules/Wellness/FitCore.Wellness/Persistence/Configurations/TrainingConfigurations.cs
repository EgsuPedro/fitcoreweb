using FitCore.Wellness.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FitCore.Wellness.Persistence.Configurations;

internal sealed class TrainingPlanConfiguration : IEntityTypeConfiguration<TrainingPlan>
{
    public void Configure(EntityTypeBuilder<TrainingPlan> b)
    {
        b.ToTable("plans", WellnessDbContext.TrainingSchema);
        b.HasKey(x => x.Id);
        b.HasAlternateKey(x => new { x.TenantId, x.Id });
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(10);
        b.Property<uint>("Version").IsRowVersion();
        b.HasIndex(x => new { x.TenantId, x.Status });
    }
}

internal sealed class TrainingPlanVersionConfiguration : IEntityTypeConfiguration<TrainingPlanVersion>
{
    public void Configure(EntityTypeBuilder<TrainingPlanVersion> b)
    {
        b.ToTable("plan_versions", WellnessDbContext.TrainingSchema);
        b.HasKey(x => x.Id);
        b.HasAlternateKey(x => new { x.TenantId, x.Id });
        b.Property(x => x.Name).HasMaxLength(120).IsRequired();
        b.Property(x => x.Goal).HasMaxLength(300);
        b.Property(x => x.Description).HasMaxLength(2000);
        b.Property(x => x.Level).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.ChangeNote).HasMaxLength(300);
        b.HasOne<TrainingPlan>().WithMany().HasForeignKey(x => new { x.TenantId, x.PlanId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.PlanId, x.Number }).IsUnique();
    }
}

internal sealed class TrainingPlanDayConfiguration : IEntityTypeConfiguration<TrainingPlanDay>
{
    public void Configure(EntityTypeBuilder<TrainingPlanDay> b)
    {
        b.ToTable("plan_days", WellnessDbContext.TrainingSchema);
        b.HasKey(x => x.Id);
        b.HasAlternateKey(x => new { x.TenantId, x.Id });
        b.Property(x => x.Name).HasMaxLength(120).IsRequired();
        b.Property(x => x.Notes).HasMaxLength(500);
        b.HasOne<TrainingPlanVersion>().WithMany().HasForeignKey(x => new { x.TenantId, x.VersionId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.VersionId, x.Position }).IsUnique();
    }
}

internal sealed class TrainingPlanExerciseConfiguration : IEntityTypeConfiguration<TrainingPlanExercise>
{
    public void Configure(EntityTypeBuilder<TrainingPlanExercise> b)
    {
        b.ToTable("plan_exercises", WellnessDbContext.TrainingSchema);
        b.HasKey(x => x.Id);
        b.HasAlternateKey(x => new { x.TenantId, x.Id });
        b.Property(x => x.ExerciseName).HasMaxLength(120).IsRequired();
        b.Property(x => x.TrackingMode).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.RecommendedLoadKg).HasPrecision(7, 2);
        b.Property(x => x.Notes).HasMaxLength(500);
        b.HasOne<TrainingPlanDay>().WithMany().HasForeignKey(x => new { x.TenantId, x.DayId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Exercise>().WithMany().HasForeignKey(x => x.ExerciseId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.DayId, x.Position }).IsUnique();
        b.HasIndex(x => x.ExerciseId);
    }
}

internal sealed class TrainingAssignmentConfiguration : IEntityTypeConfiguration<TrainingAssignment>
{
    public void Configure(EntityTypeBuilder<TrainingAssignment> b)
    {
        b.ToTable("training_assignments", WellnessDbContext.TrainingSchema);
        b.HasKey(x => x.Id);
        b.HasAlternateKey(x => new { x.TenantId, x.Id });
        b.Ignore(x => x.LastDay);
        b.Property(x => x.EndReason).HasMaxLength(300);
        b.HasOne<TrainingPlan>().WithMany().HasForeignKey(x => new { x.TenantId, x.PlanId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<TrainingPlanVersion>().WithMany().HasForeignKey(x => new { x.TenantId, x.PlanVersionId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);

        // Un alumno tiene una sola rutina sin cerrar: lo decide la base si dos asignaciones llegan a la vez.
        b.HasIndex(x => x.MemberId).IsUnique().HasFilter("ended_on IS NULL").HasDatabaseName("ux_training_assignments_open_member");
        b.HasIndex(x => new { x.TenantId, x.MemberId, x.StartDate });
        b.HasIndex(x => new { x.TenantId, x.PlanId });
    }
}

internal sealed class WorkoutSessionConfiguration : IEntityTypeConfiguration<WorkoutSession>
{
    public void Configure(EntityTypeBuilder<WorkoutSession> b)
    {
        b.ToTable("workout_sessions", WellnessDbContext.TrainingSchema);
        b.HasKey(x => x.Id);
        b.HasAlternateKey(x => new { x.TenantId, x.Id });
        b.Property(x => x.MemberNotes).HasMaxLength(WorkoutSession.MaxNotesLength);
        b.Property(x => x.TrainerComment).HasMaxLength(WorkoutSession.MaxNotesLength);
        b.HasOne<TrainingAssignment>().WithMany().HasForeignKey(x => new { x.TenantId, x.AssignmentId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<TrainingPlanVersion>().WithMany().HasForeignKey(x => new { x.TenantId, x.PlanVersionId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<TrainingPlanDay>().WithMany().HasForeignKey(x => new { x.TenantId, x.DayId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.MemberId, x.PerformedOn });
    }
}

internal sealed class WorkoutSessionExerciseConfiguration : IEntityTypeConfiguration<WorkoutSessionExercise>
{
    public void Configure(EntityTypeBuilder<WorkoutSessionExercise> b)
    {
        b.ToTable("workout_session_exercises", WellnessDbContext.TrainingSchema);
        b.HasKey(x => x.Id);
        b.HasAlternateKey(x => new { x.TenantId, x.Id });
        b.Property(x => x.ExerciseName).HasMaxLength(120).IsRequired();
        b.Property(x => x.TrackingMode).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Notes).HasMaxLength(500);
        b.HasOne<WorkoutSession>().WithMany().HasForeignKey(x => new { x.TenantId, x.SessionId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<TrainingPlanExercise>().WithMany().HasForeignKey(x => new { x.TenantId, x.PlanExerciseId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.SessionId, x.PlanExerciseId }).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.ExerciseId });
    }
}

internal sealed class WorkoutSetConfiguration : IEntityTypeConfiguration<WorkoutSet>
{
    public void Configure(EntityTypeBuilder<WorkoutSet> b)
    {
        b.ToTable("workout_sets", WellnessDbContext.TrainingSchema);
        b.HasKey(x => x.Id);
        b.Property(x => x.LoadKg).HasPrecision(7, 2);
        b.HasOne<WorkoutSessionExercise>().WithMany().HasForeignKey(x => new { x.TenantId, x.SessionExerciseId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.SessionExerciseId, x.SetNumber }).IsUnique();
    }
}
