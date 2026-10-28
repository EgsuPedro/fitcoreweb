using FitCore.SharedKernel;

namespace FitCore.Classes.Domain;

/// <summary>Tipo de actividad de una clase. «Otro» cubre lo que el gimnasio no encuentre aquí (el nombre de la clase lo precisa).</summary>
public enum ActivityType
{
    Yoga = 1,
    Spinning = 2,
    Functional = 3,
    Zumba = 4,
    Pilates = 5,
    Stretching = 6,
    Boxing = 7,
    Dance = 8,
    Other = 9
}

public enum ClassLevel
{
    AllLevels = 1,
    Beginner = 2,
    Intermediate = 3,
    Advanced = 4
}

/// <summary>Datos configurables de una clase (todo lo que se envía al crearla o editarla).</summary>
public sealed record GroupClassDetails(
    string Name,
    string? Description,
    ActivityType Activity,
    Guid InstructorId,
    int DurationMinutes,
    int Capacity,
    ClassLevel Level,
    string? Requirements,
    int MinBookingNoticeMinutes,
    int MaxBookingAdvanceDays,
    int CancellationDeadlineMinutes,
    bool WaitlistEnabled,
    int WaitlistCapacity,
    int OfferWindowMinutes,
    bool RequireActiveMembership,
    bool ApplyPaymentPolicy,
    int? MinAge);

/// <summary>
/// Definición de una clase grupal (qué se ofrece y con qué reglas). Cada fecha concreta es una <c>ClassSession</c> aparte. Nunca se borra:
/// desactivarla solo la saca de la programación nueva, sin tocar sesiones ni reservas históricas.
/// </summary>
public sealed class GroupClass : Entity<Guid>, ITenantOwned, IAuditable
{
    public const int MinNameLength = 2;
    public const int MaxNameLength = 120;
    public const int MaxDescriptionLength = 1000;
    public const int MaxRequirementsLength = 500;
    public const int MinDurationMinutes = 5;
    public const int MaxDurationMinutes = 480;
    public const int MaxCapacity = 500;
    public const int MaxNoticeMinutes = 7 * 24 * 60;
    public const int MaxAdvanceDays = 365;
    public const int MaxCancellationMinutes = 7 * 24 * 60;
    public const int MaxWaitlistCapacity = 200;
    public const int MinOfferWindowMinutes = 5;
    public const int MaxOfferWindowMinutes = 24 * 60;
    public const int DefaultOfferWindowMinutes = 30;
    public const int MaxMinAge = 100;

    private GroupClass(Guid id)
        : base(id)
    {
    }

    public Guid TenantId { get; private set; }
    public Guid BranchId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public ActivityType Activity { get; private set; }

    /// <summary>Entrenador responsable por defecto (<c>training.trainer_profiles</c>). Cada sesión puede tener otro.</summary>
    public Guid InstructorId { get; private set; }

    public int DurationMinutes { get; private set; }
    public int Capacity { get; private set; }
    public ClassLevel Level { get; private set; }

    /// <summary>Equipamiento o requisitos para participar (ropa, toalla, experiencia previa…).</summary>
    public string? Requirements { get; private set; }

    /// <summary>Se deja de poder reservar cuando faltan menos de estos minutos para el inicio (0 = hasta el inicio).</summary>
    public int MinBookingNoticeMinutes { get; private set; }

    /// <summary>Se puede reservar con hasta estos días de anticipación.</summary>
    public int MaxBookingAdvanceDays { get; private set; }

    /// <summary>Se puede cancelar mientras falten al menos estos minutos para el inicio (0 = hasta el inicio).</summary>
    public int CancellationDeadlineMinutes { get; private set; }

    public bool WaitlistEnabled { get; private set; }

    /// <summary>Máximo de alumnos en espera por sesión (0 cuando la lista de espera está desactivada).</summary>
    public int WaitlistCapacity { get; private set; }

    /// <summary>Tiempo que tiene el alumno en espera para aceptar un cupo que se le ofrece.</summary>
    public int OfferWindowMinutes { get; private set; } = DefaultOfferWindowMinutes;

    public bool RequireActiveMembership { get; private set; } = true;

    /// <summary>Además de la membresía activa, aplica la política de acceso con deuda del gimnasio.</summary>
    public bool ApplyPaymentPolicy { get; private set; } = true;

    public int? MinAge { get; private set; }

    public bool IsActive { get; private set; } = true;
    public DateTimeOffset CreatedAt { get; private set; }
    public Guid? CreatedBy { get; private set; }
    public DateTimeOffset? UpdatedAt { get; private set; }
    public Guid? UpdatedBy { get; private set; }

    public static GroupClass Create(Guid tenantId, Guid branchId, GroupClassDetails details, Guid? by, DateTimeOffset now)
    {
        var groupClass = new GroupClass(Guid.CreateVersion7())
        {
            TenantId = tenantId,
            BranchId = branchId,
            CreatedAt = now,
            CreatedBy = by
        };
        groupClass.Apply(details);
        return groupClass;
    }

    public void Update(GroupClassDetails details, Guid? by, DateTimeOffset now)
    {
        Apply(details);
        UpdatedAt = now;
        UpdatedBy = by;
    }

    public void SetActive(bool active, Guid? by, DateTimeOffset now)
    {
        IsActive = active;
        UpdatedAt = now;
        UpdatedBy = by;
    }

    /// <summary>
    /// Coherencia entre las configuraciones. Devuelve el primer problema (código estable) o <c>null</c> si son coherentes. Los rangos
    /// individuales los valida el validador de la solicitud; esto cubre lo que depende de varios campos.
    /// </summary>
    public static Error? CheckCoherence(GroupClassDetails d)
    {
        if (d.MinBookingNoticeMinutes >= d.MaxBookingAdvanceDays * 24 * 60)
        {
            return GroupClassErrors.BookingWindowIncoherent;
        }

        if (d.WaitlistEnabled && d.WaitlistCapacity < 1)
        {
            return GroupClassErrors.WaitlistCapacityRequired;
        }

        if (!d.WaitlistEnabled && d.WaitlistCapacity != 0)
        {
            return GroupClassErrors.WaitlistDisabledWithCapacity;
        }

        if (d.ApplyPaymentPolicy && !d.RequireActiveMembership)
        {
            return GroupClassErrors.PaymentPolicyNeedsMembership;
        }

        return null;
    }

    private void Apply(GroupClassDetails d)
    {
        Name = d.Name.Trim();
        Description = Clean(d.Description);
        Activity = d.Activity;
        InstructorId = d.InstructorId;
        DurationMinutes = d.DurationMinutes;
        Capacity = d.Capacity;
        Level = d.Level;
        Requirements = Clean(d.Requirements);
        MinBookingNoticeMinutes = d.MinBookingNoticeMinutes;
        MaxBookingAdvanceDays = d.MaxBookingAdvanceDays;
        CancellationDeadlineMinutes = d.CancellationDeadlineMinutes;
        WaitlistEnabled = d.WaitlistEnabled;
        WaitlistCapacity = d.WaitlistEnabled ? d.WaitlistCapacity : 0;
        OfferWindowMinutes = d.OfferWindowMinutes;
        RequireActiveMembership = d.RequireActiveMembership;
        ApplyPaymentPolicy = d.ApplyPaymentPolicy;
        MinAge = d.MinAge;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public static class GroupClassErrors
{
    public static readonly Error NotFound = Error.NotFound("classes.not_found", "Clase no encontrada.");
    public static readonly Error NameTaken = Error.Conflict("classes.name_taken", "Ya existe una clase con ese nombre en la sucursal.");
    public static readonly Error BranchRequired = Error.Validation("classes.branch_required", "Indique la sucursal de la clase.");
    public static readonly Error BranchImmutable = Error.Validation("classes.branch_immutable", "La sucursal de una clase no se puede cambiar: cree otra clase en la otra sucursal.");
    public static readonly Error InstructorInvalid = Error.Validation("classes.instructor_invalid", "El instructor debe ser un entrenador activo del gimnasio.");
    public static readonly Error InstructorNotInBranch = Error.Validation("classes.instructor_not_in_branch", "El instructor no trabaja en la sucursal de la clase.");
    public static readonly Error BookingWindowIncoherent = Error.Validation("classes.booking_window_incoherent", "La anticipación mínima para reservar debe ser menor que la máxima.");
    public static readonly Error WaitlistCapacityRequired = Error.Validation("classes.waitlist_capacity_required", "Con la lista de espera activada indique cuántos alumnos pueden esperar (al menos 1).");
    public static readonly Error WaitlistDisabledWithCapacity = Error.Validation("classes.waitlist_disabled_with_capacity", "Sin lista de espera, su capacidad debe ser 0.");
    public static readonly Error PaymentPolicyNeedsMembership = Error.Validation("classes.payment_policy_needs_membership", "La política de pagos solo aplica si se exige membresía activa.");
}
