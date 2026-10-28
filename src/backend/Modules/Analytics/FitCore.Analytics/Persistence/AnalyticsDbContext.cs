using FitCore.Application.Core;
using Microsoft.EntityFrameworkCore;

namespace FitCore.Analytics.Persistence;

/// <summary>
/// Contexto de solo lectura del módulo de analítica. Todo lo que lee pertenece a otros módulos y se mapea como vista mínima
/// (<c>ExcludeFromMigrations</c>): este módulo no crea ni modifica datos de negocio. El aislamiento es el de siempre: filtro global de EF
/// más Row-Level Security (la conexión fija <c>app.tenant_id</c>).
/// </summary>
public sealed class AnalyticsDbContext(DbContextOptions<AnalyticsDbContext> options, ITenantContext tenantContext)
    : DbContext(options)
{
    public const string Schema = "analytics";

    internal DbSet<TenantRef> TenantRefs => Set<TenantRef>();
    internal DbSet<BranchRef> BranchRefs => Set<BranchRef>();
    internal DbSet<MemberRef> Members => Set<MemberRef>();
    internal DbSet<PaymentRef> Payments => Set<PaymentRef>();
    internal DbSet<AllocationRef> Allocations => Set<AllocationRef>();
    internal DbSet<ObligationRef> Obligations => Set<ObligationRef>();
    internal DbSet<MembershipRef> Memberships => Set<MembershipRef>();
    internal DbSet<MembershipEventRef> MembershipEvents => Set<MembershipEventRef>();
    internal DbSet<GymEntryRef> GymEntries => Set<GymEntryRef>();
    internal DbSet<TrainerRef> Trainers => Set<TrainerRef>();
    internal DbSet<ClassSessionRef> ClassSessions => Set<ClassSessionRef>();
    internal DbSet<ReservationRef> Reservations => Set<ReservationRef>();
    internal DbSet<ClassAttendanceRef> ClassAttendances => Set<ClassAttendanceRef>();
    internal DbSet<UserRef> Users => Set<UserRef>();
    internal DbSet<CashRegisterRef> CashRegisters => Set<CashRegisterRef>();
    internal DbSet<CashSessionRef> CashSessions => Set<CashSessionRef>();
    internal DbSet<CashMovementRef> CashMovements => Set<CashMovementRef>();

    // Guid.Empty nunca coincide con un tenant real: sin tenant activo no se ve ninguna fila.
    private Guid CurrentTenantId => tenantContext.TenantId ?? Guid.Empty;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<TenantRef>(b =>
        {
            b.ToTable("tenants", "organizations", t => t.ExcludeFromMigrations());
            b.HasKey(x => x.Id);
            b.HasQueryFilter(x => x.Id == CurrentTenantId);
        });

        View<BranchRef>(modelBuilder, "branches", "organizations");
        View<MemberRef>(modelBuilder, "members", "members");
        View<PaymentRef>(modelBuilder, "payments", "billing");
        View<ObligationRef>(modelBuilder, "payment_obligations", "billing");
        View<MembershipRef>(modelBuilder, "memberships", "members");
        View<GymEntryRef>(modelBuilder, "attendance", "access");
        View<TrainerRef>(modelBuilder, "trainer_profiles", "training");
        View<ClassSessionRef>(modelBuilder, "class_sessions", "classes");
        View<ReservationRef>(modelBuilder, "class_reservations", "classes");
        View<ClassAttendanceRef>(modelBuilder, "class_attendance", "classes");
        View<UserRef>(modelBuilder, "app_users", "identity");
        View<CashRegisterRef>(modelBuilder, "cash_registers", "cash");
        View<CashSessionRef>(modelBuilder, "cash_sessions", "cash");
        View<CashMovementRef>(modelBuilder, "cash_movements", "cash");

        // Sin clave propia en la fuente (se identifican por sus columnas): se leen solo para sumar.
        modelBuilder.Entity<AllocationRef>(b =>
        {
            b.ToTable("payment_allocations", "billing", t => t.ExcludeFromMigrations());
            b.HasKey(x => x.Id);
            b.HasQueryFilter(x => x.TenantId == CurrentTenantId);
        });
        modelBuilder.Entity<MembershipEventRef>(b =>
        {
            b.ToTable("membership_events", "members", t => t.ExcludeFromMigrations());
            b.HasKey(x => x.Id);
            b.HasQueryFilter(x => x.TenantId == CurrentTenantId);
        });
    }

    private void View<T>(ModelBuilder modelBuilder, string table, string schema)
        where T : class, ITenantRow
    {
        modelBuilder.Entity<T>(b =>
        {
            b.ToTable(table, schema, t => t.ExcludeFromMigrations());
            b.HasKey(x => x.Id);
            b.HasQueryFilter(x => x.TenantId == CurrentTenantId);
        });
    }
}

internal interface ITenantRow
{
    Guid Id { get; }

    Guid TenantId { get; }
}

/// <summary>Vista mínima de <c>organizations.tenants</c>: la zona horaria del gimnasio.</summary>
internal sealed class TenantRef
{
    public Guid Id { get; init; }
    public string TimeZone { get; init; } = string.Empty;
}

internal sealed class BranchRef : ITenantRow
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public string Code { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string TimeZone { get; init; } = string.Empty;
    public bool IsActive { get; init; }
}

/// <summary>Alumno: solo lo necesario para contar y rotular (nunca documento, teléfono ni correo).</summary>
internal sealed class MemberRef : ITenantRow
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid BranchId { get; init; }
    public string Code { get; init; } = string.Empty;
    public string FirstName { get; init; } = string.Empty;
    public string LastName { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public DateOnly EnrolledOn { get; init; }
}

/// <summary>Pago. El importe se suma desde aquí y nunca uniendo las asignaciones (un pago con varias asignaciones contaría varias veces).</summary>
internal sealed class PaymentRef : ITenantRow
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid BranchId { get; init; }
    public Guid MemberId { get; init; }
    public string ReceiptNumber { get; init; } = string.Empty;
    public string Method { get; init; } = string.Empty;
    public string? MethodDetail { get; init; }
    public decimal Amount { get; init; }
    public string Status { get; init; } = string.Empty;
    public DateTimeOffset PaidAt { get; init; }
    public DateTimeOffset? ReversedAt { get; init; }
    public Guid? ReversedBy { get; init; }
    public string? ReversalReason { get; init; }
}

internal sealed class AllocationRef
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid PaymentId { get; init; }
    public Guid ObligationId { get; init; }
    public decimal Amount { get; init; }
}

internal sealed class ObligationRef : ITenantRow
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid BranchId { get; init; }
    public Guid MemberId { get; init; }
    public Guid? MembershipId { get; init; }
    public string Reference { get; init; } = string.Empty;
    public string Concept { get; init; } = string.Empty;
    public DateOnly IssueDate { get; init; }
    public DateOnly DueDate { get; init; }
    public decimal PayableAmount { get; init; }
    public string Status { get; init; } = string.Empty;
    public DateTimeOffset? VoidedAt { get; init; }
    public Guid? VoidedBy { get; init; }
    public string? VoidReason { get; init; }
}

internal sealed class MembershipRef : ITenantRow
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid BranchId { get; init; }
    public Guid MemberId { get; init; }
    public Guid PlanId { get; init; }
    public string PlanName { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public DateOnly StartDate { get; init; }
    public DateOnly EndDate { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? CancelledAt { get; init; }
    public Guid? RenewedFromId { get; init; }
    public decimal AgreedPrice { get; init; }
}

internal sealed class MembershipEventRef
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid MembershipId { get; init; }
    public string Type { get; init; } = string.Empty;
    public DateTimeOffset OccurredAt { get; init; }
}

/// <summary>Entrada al gimnasio (no confundir con la asistencia a una clase).</summary>
internal sealed class GymEntryRef : ITenantRow
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid BranchId { get; init; }
    public Guid MemberId { get; init; }
    public DateTimeOffset CheckInAt { get; init; }
}

internal sealed class TrainerRef : ITenantRow
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid UserId { get; init; }
}

internal sealed class ClassSessionRef : ITenantRow
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid BranchId { get; init; }
    public Guid ClassId { get; init; }
    public Guid InstructorId { get; init; }
    public DateTimeOffset StartsAt { get; init; }
    public DateTimeOffset EndsAt { get; init; }
    public int Capacity { get; init; }
    public string Status { get; init; } = string.Empty;
}

internal sealed class ReservationRef : ITenantRow
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid BranchId { get; init; }
    public Guid SessionId { get; init; }
    public Guid MemberId { get; init; }
    public string Status { get; init; } = string.Empty;
    public bool LateCancellation { get; init; }
}

internal sealed class ClassAttendanceRef : ITenantRow
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid BranchId { get; init; }
    public Guid SessionId { get; init; }
    public Guid ReservationId { get; init; }
    public string Outcome { get; init; } = string.Empty;
}

internal sealed class UserRef : ITenantRow
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public string FullName { get; init; } = string.Empty;
}

internal sealed class CashRegisterRef : ITenantRow
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid BranchId { get; init; }
    public string Name { get; init; } = string.Empty;
}

/// <summary>Turno de caja. El esperado, el contado y la diferencia son los que se guardaron al cerrar; nunca se recalculan.</summary>
internal sealed class CashSessionRef : ITenantRow
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid BranchId { get; init; }
    public Guid RegisterId { get; init; }
    public Guid OpenedBy { get; init; }
    public DateTimeOffset OpenedAt { get; init; }
    public decimal OpeningAmount { get; init; }
    public string Status { get; init; } = string.Empty;
    public Guid? ClosedBy { get; init; }
    public DateTimeOffset? ClosedAt { get; init; }
    public decimal? CountedAmount { get; init; }
    public decimal? ExpectedAtClose { get; init; }
    public decimal? Difference { get; init; }
}

internal sealed class CashMovementRef : ITenantRow
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid BranchId { get; init; }
    public Guid SessionId { get; init; }
    public string Type { get; init; } = string.Empty;
    public string Direction { get; init; } = string.Empty;
    public decimal Amount { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
