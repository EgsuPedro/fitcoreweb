using FitCore.Application.Core;
using FitCore.Platform.Persistence;
using FitCore.SharedKernel;
using FitCore.Wellness.Domain;
using Microsoft.EntityFrameworkCore;

namespace FitCore.Wellness.Persistence;

/// <summary>
/// Contexto del módulo de bienestar (entrenadores, rutinas, nutrición, evaluaciones y progreso). Mismo aislamiento que los demás
/// módulos: filtro global de EF + Row-Level Security (cada conexión fija <c>app.tenant_id</c>).
/// </summary>
public sealed class WellnessDbContext(DbContextOptions<WellnessDbContext> options, ITenantContext tenantContext)
    : DbContext(options)
{
    public const string TrainingSchema = "training";

    public DbSet<TrainerProfile> Trainers => Set<TrainerProfile>();
    public DbSet<TrainerAvailability> TrainerAvailability => Set<TrainerAvailability>();
    public DbSet<TrainerAssignment> TrainerAssignments => Set<TrainerAssignment>();
    public DbSet<TrainerNote> TrainerNotes => Set<TrainerNote>();
    public DbSet<Exercise> Exercises => Set<Exercise>();
    public DbSet<TrainingPlan> Plans => Set<TrainingPlan>();
    public DbSet<TrainingPlanVersion> PlanVersions => Set<TrainingPlanVersion>();
    public DbSet<TrainingPlanDay> PlanDays => Set<TrainingPlanDay>();
    public DbSet<TrainingPlanExercise> PlanExercises => Set<TrainingPlanExercise>();
    public DbSet<TrainingAssignment> TrainingAssignments => Set<TrainingAssignment>();
    public DbSet<WorkoutSession> WorkoutSessions => Set<WorkoutSession>();
    public DbSet<WorkoutSessionExercise> WorkoutSessionExercises => Set<WorkoutSessionExercise>();
    public DbSet<WorkoutSet> WorkoutSets => Set<WorkoutSet>();

    // Vistas de solo lectura de datos que pertenecen a otros módulos (no generan migraciones aquí).
    internal DbSet<MemberRef> MemberRefs => Set<MemberRef>();
    internal DbSet<UserRef> UserRefs => Set<UserRef>();
    internal DbSet<UserBranchRef> UserBranchRefs => Set<UserBranchRef>();
    internal DbSet<BranchRef> BranchRefs => Set<BranchRef>();
    internal DbSet<UserRoleRef> UserRoleRefs => Set<UserRoleRef>();
    internal DbSet<RoleRef> RoleRefs => Set<RoleRef>();

    // Guid.Empty nunca coincide con un tenant real: sin tenant activo no se ve ninguna fila.
    private Guid CurrentTenantId => tenantContext.TenantId ?? Guid.Empty;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(WellnessDbContext).Assembly);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes().ToList())
        {
            if (typeof(IHasDomainEvents).IsAssignableFrom(entityType.ClrType))
            {
                modelBuilder.Entity(entityType.ClrType).Ignore(nameof(IHasDomainEvents.DomainEvents));
            }

            if (typeof(ITenantOwned).IsAssignableFrom(entityType.ClrType))
            {
                typeof(WellnessDbContext)
                    .GetMethod(nameof(ApplyTenantFilter), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .MakeGenericMethod(entityType.ClrType)
                    .Invoke(this, [modelBuilder]);
            }
        }

        // Los ejercicios globales (TenantId nulo) los ven todos los gimnasios; los demás, solo su dueño.
        modelBuilder.Entity<Exercise>().HasQueryFilter(e => e.TenantId == null || e.TenantId == CurrentTenantId);

        // La auditoría y la outbox pertenecen a Platform: aquí solo se mapean para que el interceptor compartido escriba en ellas
        // dentro de la misma transacción. Nunca generan migraciones de este módulo.
        modelBuilder.Entity<AuditLog>(b =>
        {
            b.ToTable("audit_logs", "audit", t => t.ExcludeFromMigrations());
            b.HasKey(x => x.Id);
            b.Property(x => x.Before).HasColumnType("jsonb");
            b.Property(x => x.After).HasColumnType("jsonb");
            b.HasQueryFilter(x => x.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<OutboxMessage>(b =>
        {
            b.ToTable("outbox_messages", "messaging", t => t.ExcludeFromMigrations());
            b.HasKey(x => x.Id);
            b.Property(x => x.Payload).HasColumnType("jsonb");
        });
    }

    private void ApplyTenantFilter<T>(ModelBuilder builder)
        where T : class, ITenantOwned =>
        builder.Entity<T>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
}

/// <summary>Vista mínima de <c>members.members</c> (módulo de alumnos) para validar y mostrar alumnos.</summary>
internal sealed class MemberRef : ITenantOwned
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid BranchId { get; init; }
    public string Code { get; init; } = string.Empty;
    public string FirstName { get; init; } = string.Empty;
    public string LastName { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public Guid? UserId { get; init; }

    public string FullName => $"{FirstName} {LastName}";

    public bool IsActive => Status == "Active";
}

/// <summary>Vista mínima de <c>identity.app_users</c>.</summary>
internal sealed class UserRef : ITenantOwned
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public string FullName { get; init; } = string.Empty;
    public string? Email { get; init; }
    public bool IsActive { get; init; }
}

/// <summary>Vista mínima de <c>identity.user_branches</c> (sucursales a las que tiene acceso un usuario).</summary>
internal sealed class UserBranchRef : ITenantOwned
{
    public Guid TenantId { get; init; }
    public Guid UserId { get; init; }
    public Guid BranchId { get; init; }
}

/// <summary>Vista mínima de <c>identity.user_roles</c>.</summary>
internal sealed class UserRoleRef : ITenantOwned
{
    public Guid TenantId { get; init; }
    public Guid UserId { get; init; }
    public Guid RoleId { get; init; }
}

/// <summary>Vista mínima de <c>identity.app_roles</c> (si el rol abarca todas las sucursales).</summary>
internal sealed class RoleRef : ITenantOwned
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public bool GrantsAllBranches { get; init; }
}

/// <summary>Vista mínima de <c>organizations.branches</c>.</summary>
internal sealed class BranchRef : ITenantOwned
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string TimeZone { get; init; } = string.Empty;
    public bool IsActive { get; init; }
}
