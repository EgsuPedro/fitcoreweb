using FitCore.SharedKernel;

namespace FitCore.Wellness.Domain;

/// <summary>
/// Plan nutricional. Igual que las rutinas, la cabecera solo apunta a su versión vigente: el contenido (comidas y alimentos) vive en
/// versiones inmutables, así que modificar el plan crea una versión nueva y nada de lo asignado o registrado cambia.
/// </summary>
public sealed class NutritionPlan : Entity<Guid>, ITenantOwned, IAuditable
{
    private NutritionPlan(Guid id)
        : base(id)
    {
    }

    public Guid TenantId { get; private set; }
    public PlanStatus Status { get; private set; } = PlanStatus.Active;
    public int CurrentVersion { get; private set; }
    public Guid? CurrentVersionId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public Guid? CreatedBy { get; private set; }
    public DateTimeOffset? UpdatedAt { get; private set; }
    public Guid? UpdatedBy { get; private set; }

    public static NutritionPlan Create(Guid tenantId, Guid? createdBy, DateTimeOffset now) =>
        new(Guid.CreateVersion7()) { TenantId = tenantId, CreatedAt = now, CreatedBy = createdBy };

    public void PublishVersion(NutritionPlanVersion version, Guid? by, DateTimeOffset now)
    {
        CurrentVersion = version.Number;
        CurrentVersionId = version.Id;
        UpdatedAt = now;
        UpdatedBy = by;
    }

    public void SetStatus(PlanStatus status, Guid? by, DateTimeOffset now)
    {
        Status = status;
        UpdatedAt = now;
        UpdatedBy = by;
    }
}

/// <summary>Objetivos orientativos del plan (el profesional los define; el sistema no los calcula ni los presenta como exactos).</summary>
public sealed record NutritionTargets(int? Calories, int? ProteinG, int? CarbsG, int? FatG);

public sealed record NutritionHeader(
    string Name,
    string? Goal,
    string? Description,
    NutritionTargets Targets,
    string? Recommendations,
    DateOnly? ValidFrom,
    DateOnly? ValidUntil);

/// <summary>Contenido inmutable de un plan nutricional en un momento dado.</summary>
public sealed class NutritionPlanVersion : Entity<Guid>, ITenantOwned
{
    public const int MaxMeals = 10;
    public const int MaxItemsPerMeal = 30;

    private NutritionPlanVersion(Guid id)
        : base(id)
    {
    }

    public Guid TenantId { get; private set; }
    public Guid PlanId { get; private set; }
    public int Number { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string? Goal { get; private set; }
    public string? Description { get; private set; }
    public int? TargetCalories { get; private set; }
    public int? TargetProteinG { get; private set; }
    public int? TargetCarbsG { get; private set; }
    public int? TargetFatG { get; private set; }
    public string? Recommendations { get; private set; }
    public DateOnly? ValidFrom { get; private set; }
    public DateOnly? ValidUntil { get; private set; }
    public string? ChangeNote { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public Guid? CreatedBy { get; private set; }

    public static NutritionPlanVersion Create(Guid tenantId, Guid planId, int number, NutritionHeader header, string? changeNote, Guid? createdBy, DateTimeOffset now) =>
        new(Guid.CreateVersion7())
        {
            TenantId = tenantId,
            PlanId = planId,
            Number = number,
            Name = header.Name.Trim(),
            Goal = Clean(header.Goal),
            Description = Clean(header.Description),
            TargetCalories = header.Targets.Calories,
            TargetProteinG = header.Targets.ProteinG,
            TargetCarbsG = header.Targets.CarbsG,
            TargetFatG = header.Targets.FatG,
            Recommendations = Clean(header.Recommendations),
            ValidFrom = header.ValidFrom,
            ValidUntil = header.ValidUntil,
            ChangeNote = Clean(changeNote),
            CreatedAt = now,
            CreatedBy = createdBy
        };

    internal static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class NutritionMeal : Entity<Guid>, ITenantOwned
{
    private NutritionMeal(Guid id)
        : base(id)
    {
    }

    public Guid TenantId { get; private set; }
    public Guid VersionId { get; private set; }
    public int Position { get; private set; }
    public string Name { get; private set; } = string.Empty;

    /// <summary>Hora sugerida (opcional).</summary>
    public TimeOnly? SuggestedTime { get; private set; }

    public string? Notes { get; private set; }

    public static NutritionMeal Create(Guid tenantId, Guid versionId, int position, string name, TimeOnly? suggestedTime, string? notes) =>
        new(Guid.CreateVersion7())
        {
            TenantId = tenantId,
            VersionId = versionId,
            Position = position,
            Name = name.Trim(),
            SuggestedTime = suggestedTime,
            Notes = NutritionPlanVersion.Clean(notes)
        };
}

/// <summary>Valores nutricionales de un alimento: todos opcionales, porque el profesional puede no conocerlos con precisión.</summary>
public sealed record FoodNutrients(int? Calories, decimal? ProteinG, decimal? CarbsG, decimal? FatG);

public sealed record FoodPortion(string FoodName, decimal Quantity, string Unit, FoodNutrients Nutrients, string? Notes);

public sealed class NutritionMealItem : Entity<Guid>, ITenantOwned
{
    private NutritionMealItem(Guid id)
        : base(id)
    {
    }

    public Guid TenantId { get; private set; }
    public Guid MealId { get; private set; }
    public int Position { get; private set; }
    public string FoodName { get; private set; } = string.Empty;
    public decimal Quantity { get; private set; }
    public string Unit { get; private set; } = string.Empty;
    public int? Calories { get; private set; }
    public decimal? ProteinG { get; private set; }
    public decimal? CarbsG { get; private set; }
    public decimal? FatG { get; private set; }
    public string? Notes { get; private set; }

    public static NutritionMealItem Create(Guid tenantId, Guid mealId, int position, FoodPortion portion) =>
        new(Guid.CreateVersion7())
        {
            TenantId = tenantId,
            MealId = mealId,
            Position = position,
            FoodName = portion.FoodName.Trim(),
            Quantity = portion.Quantity,
            Unit = portion.Unit.Trim(),
            Calories = portion.Nutrients.Calories,
            ProteinG = portion.Nutrients.ProteinG,
            CarbsG = portion.Nutrients.CarbsG,
            FatG = portion.Nutrients.FatG,
            Notes = NutritionPlanVersion.Clean(portion.Notes)
        };
}

/// <summary>Plan nutricional asignado a un alumno, fijado a una versión concreta. Cambiar de plan cierra el anterior; nada se borra.</summary>
public sealed class NutritionAssignment : Entity<Guid>, ITenantOwned, IAuditable
{
    public const int MaxDurationDays = 730;

    private NutritionAssignment(Guid id)
        : base(id)
    {
    }

    public Guid TenantId { get; private set; }
    public Guid PlanId { get; private set; }
    public Guid PlanVersionId { get; private set; }
    public Guid MemberId { get; private set; }
    public Guid BranchId { get; private set; }
    public DateOnly StartDate { get; private set; }
    public DateOnly? EndDate { get; private set; }

    /// <summary>Último día en que rigió, si se cerró de forma explícita (puede ser el día anterior al inicio).</summary>
    public DateOnly? EndedOn { get; private set; }

    public string? EndReason { get; private set; }
    public Guid AssignedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public DateOnly? LastDay => (EndDate, EndedOn) switch
    {
        ({ } scheduled, { } closed) => scheduled < closed ? scheduled : closed,
        ({ } scheduled, null) => scheduled,
        (null, { } closed) => closed,
        _ => null
    };

    public bool IsCurrentOn(DateOnly day) => StartDate <= day && (LastDay is not { } last || last >= day);

    public static NutritionAssignment Create(
        Guid tenantId,
        Guid planId,
        Guid planVersionId,
        Guid memberId,
        Guid branchId,
        DateOnly startDate,
        DateOnly? endDate,
        Guid assignedBy,
        DateTimeOffset now) =>
        new(Guid.CreateVersion7())
        {
            TenantId = tenantId,
            PlanId = planId,
            PlanVersionId = planVersionId,
            MemberId = memberId,
            BranchId = branchId,
            StartDate = startDate,
            EndDate = endDate,
            AssignedBy = assignedBy,
            CreatedAt = now
        };

    public void End(DateOnly day, string reason)
    {
        EndedOn = day;
        EndReason = reason.Trim();
    }
}

/// <summary>
/// Alergias, intolerancias y restricciones alimentarias de un alumno. Es un dato sensible y solo se agrega: cada actualización
/// es un registro nuevo y el vigente es el más reciente. No es <see cref="IAuditable"/> a propósito: la auditoría automática
/// copiaría el contenido; el sistema audita el hecho (quién y sobre quién), no el texto.
/// </summary>
public sealed class DietaryProfile : Entity<Guid>, ITenantOwned
{
    public const int MaxEntries = 20;
    public const int MaxEntryLength = 80;
    public const int MaxNotesLength = 1000;

    private DietaryProfile(Guid id)
        : base(id)
    {
    }

    public Guid TenantId { get; private set; }
    public Guid MemberId { get; private set; }
    public string[] Allergies { get; private set; } = [];
    public string[] Intolerances { get; private set; } = [];
    public string[] Restrictions { get; private set; } = [];
    public string? Notes { get; private set; }
    public Guid RecordedBy { get; private set; }
    public DateTimeOffset RecordedAt { get; private set; }

    public static DietaryProfile Create(
        Guid tenantId,
        Guid memberId,
        IEnumerable<string>? allergies,
        IEnumerable<string>? intolerances,
        IEnumerable<string>? restrictions,
        string? notes,
        Guid recordedBy,
        DateTimeOffset now) =>
        new(Guid.CreateVersion7())
        {
            TenantId = tenantId,
            MemberId = memberId,
            Allergies = Normalize(allergies),
            Intolerances = Normalize(intolerances),
            Restrictions = Normalize(restrictions),
            Notes = NutritionPlanVersion.Clean(notes),
            RecordedBy = recordedBy,
            RecordedAt = now
        };

    /// <summary>Quita vacíos y repetidos (sin distinguir mayúsculas), conservando el orden.</summary>
    public static string[] Normalize(IEnumerable<string>? values) =>
        [.. (values ?? []).Select(v => v.Trim()).Where(v => v.Length > 0).DistinctBy(v => v.ToUpperInvariant())];
}

public enum MealLogStatus
{
    Completed = 1,
    Partial = 2,
    Skipped = 3
}

/// <summary>Cumplimiento de una comida del plan en un día: lo que el alumno reporta haber hecho. Solo se agrega.</summary>
public sealed class MealLog : Entity<Guid>, ITenantOwned
{
    public const int MaxNotesLength = 500;

    private MealLog(Guid id)
        : base(id)
    {
    }

    public Guid TenantId { get; private set; }
    public Guid MemberId { get; private set; }
    public Guid AssignmentId { get; private set; }
    public Guid PlanVersionId { get; private set; }
    public Guid MealId { get; private set; }
    public DateOnly LoggedOn { get; private set; }
    public MealLogStatus Status { get; private set; }
    public string? Notes { get; private set; }
    public Guid RecordedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static MealLog Create(
        Guid tenantId,
        Guid memberId,
        Guid assignmentId,
        Guid planVersionId,
        Guid mealId,
        DateOnly loggedOn,
        MealLogStatus status,
        string? notes,
        Guid recordedBy,
        DateTimeOffset now) =>
        new(Guid.CreateVersion7())
        {
            TenantId = tenantId,
            MemberId = memberId,
            AssignmentId = assignmentId,
            PlanVersionId = planVersionId,
            MealId = mealId,
            LoggedOn = loggedOn,
            Status = status,
            Notes = NutritionPlanVersion.Clean(notes),
            RecordedBy = recordedBy,
            CreatedAt = now
        };
}

/// <summary>Coherencia de los datos nutricionales. Los valores son orientativos: el sistema no diagnostica ni prescribe.</summary>
public static class NutritionRules
{
    public const decimal MaxQuantity = 100000m;
    public const int MaxItemCalories = 5000;
    public const decimal MaxItemGrams = 1000m;
    public const int MinTargetCalories = 300;
    public const int MaxTargetCalories = 10000;
    public const int MaxTargetGrams = 1000;

    /// <summary>Código del primer problema de un alimento del plan, o null si es válido.</summary>
    public static string? ValidatePortion(FoodPortion p)
    {
        if (p.Quantity is <= 0 or > MaxQuantity)
        {
            return "nutrition.quantity_invalid";
        }

        if (string.IsNullOrWhiteSpace(p.Unit))
        {
            return "nutrition.unit_required";
        }

        var n = p.Nutrients;
        return n.Calories is < 0 or > MaxItemCalories
            || n.ProteinG is < 0 or > MaxItemGrams
            || n.CarbsG is < 0 or > MaxItemGrams
            || n.FatG is < 0 or > MaxItemGrams
            ? "nutrition.nutrients_invalid"
            : null;
    }

    public static string? ValidateTargets(NutritionTargets t) =>
        t.Calories is < MinTargetCalories or > MaxTargetCalories
        || t.ProteinG is < 0 or > MaxTargetGrams
        || t.CarbsG is < 0 or > MaxTargetGrams
        || t.FatG is < 0 or > MaxTargetGrams
            ? "nutrition.targets_invalid"
            : null;

    /// <summary>
    /// Calorías estimadas de una comida: suma de los alimentos que tienen el dato. Nulo si ninguno lo tiene (no se inventa un cero).
    /// </summary>
    public static int? EstimatedCalories(IEnumerable<int?> itemCalories)
    {
        var known = itemCalories.Where(c => c is not null).Select(c => c!.Value).ToList();
        return known.Count == 0 ? null : known.Sum();
    }
}
