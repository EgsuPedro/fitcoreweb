using FitCore.Application.Core;
using FitCore.Classes.Domain;
using FitCore.Platform.Persistence;
using FitCore.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace FitCore.Classes.Persistence;

/// <summary>
/// Contexto del módulo de clases grupales (definiciones, sesiones, reservas, lista de espera, asistencia a clase y bandeja de notificaciones).
/// Mismo aislamiento que los demás módulos: filtro global de EF + Row-Level Security (cada conexión fija <c>app.tenant_id</c>).
/// </summary>
public sealed class ClassesDbContext(DbContextOptions<ClassesDbContext> options, ITenantContext tenantContext)
    : DbContext(options)
{
    public const string Schema = "classes";

    public DbSet<GroupClass> GroupClasses => Set<GroupClass>();
    public DbSet<ClassSeries> ClassSeries => Set<ClassSeries>();
    public DbSet<ClassSession> ClassSessions => Set<ClassSession>();
    public DbSet<ClassReservation> Reservations => Set<ClassReservation>();
    public DbSet<IdempotencyKey> IdempotencyKeys => Set<IdempotencyKey>();
    public DbSet<ClassAttendance> ClassAttendances => Set<ClassAttendance>();
    public DbSet<InboxNotification> Notifications => Set<InboxNotification>();
    public DbSet<NotificationPreference> NotificationPreferences => Set<NotificationPreference>();

    // Vistas de solo lectura de datos que pertenecen a otros módulos (no generan migraciones aquí).
    internal DbSet<TrainerRef> TrainerRefs => Set<TrainerRef>();
    internal DbSet<TrainerAvailabilityRef> TrainerAvailabilityRefs => Set<TrainerAvailabilityRef>();
    internal DbSet<MemberRef> MemberRefs => Set<MemberRef>();
    internal DbSet<GymEntryRef> GymEntryRefs => Set<GymEntryRef>();
    internal DbSet<MembershipRef> MembershipRefs => Set<MembershipRef>();
    internal DbSet<PlanBranchRef> PlanBranchRefs => Set<PlanBranchRef>();
    internal DbSet<ObligationRef> ObligationRefs => Set<ObligationRef>();
    internal DbSet<AllocationRef> AllocationRefs => Set<AllocationRef>();
    internal DbSet<PaymentRef> PaymentRefs => Set<PaymentRef>();
    internal DbSet<SettingsRef> SettingsRefs => Set<SettingsRef>();
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

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ClassesDbContext).Assembly);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes().ToList())
        {
            if (typeof(IHasDomainEvents).IsAssignableFrom(entityType.ClrType))
            {
                modelBuilder.Entity(entityType.ClrType).Ignore(nameof(IHasDomainEvents.DomainEvents));
            }

            if (typeof(ITenantOwned).IsAssignableFrom(entityType.ClrType))
            {
                typeof(ClassesDbContext)
                    .GetMethod(nameof(ApplyTenantFilter), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .MakeGenericMethod(entityType.ClrType)
                    .Invoke(this, [modelBuilder]);
            }
        }

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

/// <summary>Vista mínima de <c>training.trainer_profiles</c> (módulo de bienestar): el instructor de una clase es un entrenador.</summary>
internal sealed class TrainerRef : ITenantOwned
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid UserId { get; init; }
    public string Status { get; init; } = string.Empty;

    public bool IsActive => Status == "Active";
}

/// <summary>Vista mínima de <c>training.trainer_availability</c>: franjas semanales en las que el entrenador atiende.</summary>
internal sealed class TrainerAvailabilityRef : ITenantOwned
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid TrainerId { get; init; }
    public int Day { get; init; }
    public TimeOnly StartTime { get; init; }
    public TimeOnly EndTime { get; init; }
    public Guid? BranchId { get; init; }
}

/// <summary>Vista mínima de <c>members.members</c> (módulo comercial): quién reserva y con qué datos se decide su elegibilidad.</summary>
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
    public DateOnly? BirthDate { get; init; }

    public string FullName => $"{FirstName} {LastName}";

    public bool IsActive => Status == "Active";
}

/// <summary>Vista mínima de <c>access.attendance</c> (entradas al gimnasio): solo para contrastar la asistencia a una clase, nunca se escribe.</summary>
internal sealed class GymEntryRef : ITenantOwned
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid BranchId { get; init; }
    public Guid MemberId { get; init; }
    public DateTimeOffset CheckInAt { get; init; }
    public DateTimeOffset? CheckOutAt { get; init; }
}

/// <summary>Vista mínima de <c>members.memberships</c>.</summary>
internal sealed class MembershipRef : ITenantOwned
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid MemberId { get; init; }
    public Guid PlanId { get; init; }
    public string Status { get; init; } = string.Empty;
    public DateOnly StartDate { get; init; }
    public DateOnly EndDate { get; init; }
}

/// <summary>Vista mínima de <c>members.plan_branches</c>: sucursales en las que vale un plan (sin filas = todas).</summary>
internal sealed class PlanBranchRef : ITenantOwned
{
    public Guid TenantId { get; init; }
    public Guid PlanId { get; init; }
    public Guid BranchId { get; init; }
}

/// <summary>Vista mínima de <c>billing.payment_obligations</c>.</summary>
internal sealed class ObligationRef : ITenantOwned
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid? MembershipId { get; init; }
    public decimal PayableAmount { get; init; }
    public DateOnly DueDate { get; init; }
    public string Status { get; init; } = string.Empty;
}

/// <summary>Vista mínima de <c>billing.payment_allocations</c>.</summary>
internal sealed class AllocationRef : ITenantOwned
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid PaymentId { get; init; }
    public Guid ObligationId { get; init; }
    public decimal Amount { get; init; }
}

/// <summary>Vista mínima de <c>billing.payments</c>: solo el estado, para contar únicamente lo confirmado.</summary>
internal sealed class PaymentRef : ITenantOwned
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public string Status { get; init; } = string.Empty;
}

/// <summary>Vista mínima de <c>members.commercial_settings</c>: la política de acceso con deuda del gimnasio.</summary>
internal sealed class SettingsRef : ITenantOwned
{
    public Guid TenantId { get; init; }
    public string AccessPolicy { get; init; } = string.Empty;
    public int MinPaymentPercent { get; init; }
}

/// <summary>Vista mínima de <c>identity.app_users</c>.</summary>
internal sealed class UserRef : ITenantOwned
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public string FullName { get; init; } = string.Empty;
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
