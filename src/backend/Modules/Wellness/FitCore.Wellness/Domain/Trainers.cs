using FitCore.SharedKernel;

namespace FitCore.Wellness.Domain;

public enum TrainerStatus
{
    Active = 1,
    Inactive = 2
}

/// <summary>Perfil profesional de un entrenador. Está ligado a un usuario del gimnasio (sus sucursales son las del usuario).</summary>
public sealed class TrainerProfile : Entity<Guid>, ITenantOwned, IAuditable
{
    public const int MaxSpecialties = 10;
    public const int MaxSpecialtyLength = 60;
    public const int MaxCertificationsLength = 500;
    public const int MaxBioLength = 1500;

    private TrainerProfile(Guid id)
        : base(id)
    {
    }

    public Guid TenantId { get; private set; }
    public Guid UserId { get; private set; }
    public string[] Specialties { get; private set; } = [];
    public string? Certifications { get; private set; }
    public string? Bio { get; private set; }
    public TrainerStatus Status { get; private set; } = TrainerStatus.Active;
    public DateOnly JoinedOn { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public Guid? CreatedBy { get; private set; }
    public DateTimeOffset? UpdatedAt { get; private set; }
    public Guid? UpdatedBy { get; private set; }

    public bool IsActive => Status == TrainerStatus.Active;

    public static TrainerProfile Create(
        Guid tenantId,
        Guid userId,
        IEnumerable<string>? specialties,
        string? certifications,
        string? bio,
        DateOnly joinedOn,
        Guid? createdBy,
        DateTimeOffset now)
    {
        var profile = new TrainerProfile(Guid.CreateVersion7())
        {
            TenantId = tenantId,
            UserId = userId,
            JoinedOn = joinedOn,
            CreatedAt = now,
            CreatedBy = createdBy
        };
        profile.Apply(specialties, certifications, bio);
        return profile;
    }

    public void Update(IEnumerable<string>? specialties, string? certifications, string? bio, DateOnly joinedOn, Guid? by, DateTimeOffset now)
    {
        Apply(specialties, certifications, bio);
        JoinedOn = joinedOn;
        UpdatedAt = now;
        UpdatedBy = by;
    }

    public void SetStatus(TrainerStatus status, Guid? by, DateTimeOffset now)
    {
        Status = status;
        UpdatedAt = now;
        UpdatedBy = by;
    }

    /// <summary>Sin espacios sobrantes, sin vacíos ni repetidos (sin distinguir mayúsculas), conservando el orden.</summary>
    public static string[] NormalizeSpecialties(IEnumerable<string>? specialties) =>
        [.. (specialties ?? []).Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];

    private void Apply(IEnumerable<string>? specialties, string? certifications, string? bio)
    {
        Specialties = NormalizeSpecialties(specialties);
        Certifications = string.IsNullOrWhiteSpace(certifications) ? null : certifications.Trim();
        Bio = string.IsNullOrWhiteSpace(bio) ? null : bio.Trim();
    }
}

/// <summary>Franja semanal en la que el entrenador atiende, opcionalmente en una sucursal concreta.</summary>
public sealed class TrainerAvailability : Entity<Guid>, ITenantOwned
{
    private TrainerAvailability(Guid id)
        : base(id)
    {
    }

    public Guid TenantId { get; private set; }
    public Guid TrainerId { get; private set; }
    public DayOfWeek Day { get; private set; }
    public TimeOnly StartTime { get; private set; }
    public TimeOnly EndTime { get; private set; }
    public Guid? BranchId { get; private set; }

    public static TrainerAvailability Create(Guid tenantId, Guid trainerId, DayOfWeek day, TimeOnly start, TimeOnly end, Guid? branchId) =>
        new(Guid.CreateVersion7())
        {
            TenantId = tenantId,
            TrainerId = trainerId,
            Day = day,
            StartTime = start,
            EndTime = end,
            BranchId = branchId
        };
}

public readonly record struct AvailabilitySlot(DayOfWeek Day, TimeOnly Start, TimeOnly End, Guid? BranchId);

public static class AvailabilityRules
{
    public const int MaxSlots = 28;

    /// <summary>Código del primer problema de las franjas (fin antes del inicio, solapes, demasiadas), o null si son válidas.</summary>
    public static string? Validate(IReadOnlyList<AvailabilitySlot> slots)
    {
        if (slots.Count > MaxSlots)
        {
            return "trainers.availability_too_many";
        }

        if (slots.Any(s => s.End <= s.Start))
        {
            return "trainers.availability_invalid_range";
        }

        foreach (var day in slots.GroupBy(s => s.Day))
        {
            var ordered = day.OrderBy(s => s.Start).ToList();
            for (var i = 1; i < ordered.Count; i++)
            {
                if (ordered[i].Start < ordered[i - 1].End)
                {
                    return "trainers.availability_overlap";
                }
            }
        }

        return null;
    }
}

/// <summary>
/// Relación entre un entrenador y un alumno. Un alumno tiene un solo entrenador activo; cambiar de entrenador cierra la asignación
/// anterior (con su motivo) y abre otra, así que el historial nunca se pierde.
/// </summary>
public sealed class TrainerAssignment : Entity<Guid>, ITenantOwned, IAuditable
{
    public const int MaxReasonLength = 300;

    private TrainerAssignment(Guid id)
        : base(id)
    {
    }

    public Guid TenantId { get; private set; }
    public Guid TrainerId { get; private set; }
    public Guid MemberId { get; private set; }
    public Guid BranchId { get; private set; }
    public DateOnly AssignedOn { get; private set; }
    public Guid? AssignedBy { get; private set; }
    public DateOnly? EndedOn { get; private set; }
    public Guid? EndedBy { get; private set; }
    public string? EndReason { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public bool IsActive => EndedOn is null;

    public static TrainerAssignment Create(Guid tenantId, Guid trainerId, Guid memberId, Guid branchId, DateOnly today, Guid? by, DateTimeOffset now) =>
        new(Guid.CreateVersion7())
        {
            TenantId = tenantId,
            TrainerId = trainerId,
            MemberId = memberId,
            BranchId = branchId,
            AssignedOn = today,
            AssignedBy = by,
            CreatedAt = now
        };

    public void End(DateOnly today, string reason, Guid? by)
    {
        EndedOn = today;
        EndedBy = by;
        EndReason = reason.Trim();
    }
}

/// <summary>
/// Observación profesional del entrenador sobre un alumno. Es de acceso restringido y solo se agrega: nunca se edita ni se borra.
/// No es <see cref="IAuditable"/> a propósito: la auditoría automática copiaría el texto; el sistema audita el hecho, no el contenido.
/// </summary>
public sealed class TrainerNote : Entity<Guid>, ITenantOwned
{
    public const int MaxLength = 2000;

    private TrainerNote(Guid id)
        : base(id)
    {
    }

    public Guid TenantId { get; private set; }
    public Guid MemberId { get; private set; }
    public Guid TrainerId { get; private set; }
    public string Text { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }
    public Guid CreatedBy { get; private set; }

    public static TrainerNote Create(Guid tenantId, Guid memberId, Guid trainerId, string text, Guid createdBy, DateTimeOffset now) =>
        new(Guid.CreateVersion7())
        {
            TenantId = tenantId,
            MemberId = memberId,
            TrainerId = trainerId,
            Text = text.Trim(),
            CreatedAt = now,
            CreatedBy = createdBy
        };
}
