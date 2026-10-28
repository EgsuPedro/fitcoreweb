using FitCore.SharedKernel;

namespace FitCore.Wellness.Domain;

public enum MuscleGroup
{
    Chest = 1,
    Back = 2,
    Shoulders = 3,
    Biceps = 4,
    Triceps = 5,
    Forearms = 6,
    Core = 7,
    Quadriceps = 8,
    Hamstrings = 9,
    Glutes = 10,
    Calves = 11,
    FullBody = 12
}

public enum ExerciseType
{
    Strength = 1,
    Cardio = 2,
    Mobility = 3,
    Flexibility = 4,
    Balance = 5,
    Plyometric = 6
}

public enum Equipment
{
    BodyWeight = 1,
    Barbell = 2,
    Dumbbell = 3,
    Kettlebell = 4,
    Machine = 5,
    Cable = 6,
    Band = 7,
    Bench = 8,
    PullUpBar = 9,
    CardioMachine = 10,
    Other = 11
}

public enum Difficulty
{
    Beginner = 1,
    Intermediate = 2,
    Advanced = 3
}

/// <summary>
/// Cómo se registra la ejecución del ejercicio. Las rutinas y las sesiones validan series, repeticiones, duración y carga según este modo
/// (por ejemplo, una plancha se mide en tiempo y no en repeticiones con peso).
/// </summary>
public enum TrackingMode
{
    /// <summary>Repeticiones con carga (barra, mancuernas, máquinas).</summary>
    RepsAndLoad = 1,

    /// <summary>Repeticiones sin carga externa (peso corporal).</summary>
    Reps = 2,

    /// <summary>Tiempo (planchas, cardio, estiramientos sostenidos).</summary>
    Duration = 3
}

/// <summary>
/// Ejercicio de la biblioteca. Con <see cref="TenantId"/> nulo es <b>global</b> (catálogo base de la plataforma, visible para todos los
/// gimnasios y editable solo por plataforma); con valor es del gimnasio. Nunca se borra: desactivarlo no lo quita de rutinas ni sesiones
/// históricas.
/// </summary>
public sealed class Exercise : Entity<Guid>, IAuditable
{
    public const int MaxSecondaryMuscles = 5;
    public const int MaxSteps = 20;
    public const int MaxStepLength = 500;
    public const int MaxMistakes = 10;
    public const int MaxMistakeLength = 300;

    private Exercise(Guid id)
        : base(id)
    {
    }

    /// <summary>Nulo = ejercicio global del catálogo base.</summary>
    public Guid? TenantId { get; private set; }

    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public MuscleGroup PrimaryMuscle { get; private set; }

    /// <summary>Nombres de <see cref="MuscleGroup"/>, sin repetir ni incluir el principal.</summary>
    public string[] SecondaryMuscles { get; private set; } = [];

    public ExerciseType Type { get; private set; }
    public Equipment Equipment { get; private set; }
    public Difficulty Difficulty { get; private set; }
    public TrackingMode TrackingMode { get; private set; }
    public string[] Instructions { get; private set; } = [];
    public string[] CommonMistakes { get; private set; } = [];

    /// <summary>Contraindicaciones o advertencias relevantes.</summary>
    public string? Warnings { get; private set; }

    /// <summary>Enlace https a un recurso externo aportado por quien lo registra. El sistema nunca inventa ni incorpora vídeos.</summary>
    public string? VideoUrl { get; private set; }

    public bool IsActive { get; private set; } = true;
    public DateTimeOffset CreatedAt { get; private set; }
    public Guid? CreatedBy { get; private set; }
    public DateTimeOffset? UpdatedAt { get; private set; }
    public Guid? UpdatedBy { get; private set; }

    public bool IsGlobal => TenantId is null;

    public static Exercise Create(Guid? tenantId, ExerciseDetails details, Guid? createdBy, DateTimeOffset now)
    {
        var exercise = new Exercise(Guid.CreateVersion7())
        {
            TenantId = tenantId,
            CreatedAt = now,
            CreatedBy = createdBy
        };
        exercise.Apply(details);
        return exercise;
    }

    public void Update(ExerciseDetails details, Guid? by, DateTimeOffset now)
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

    /// <summary>Quita vacíos y repetidos (sin distinguir mayúsculas), conservando el orden.</summary>
    public static string[] NormalizeList(IEnumerable<string>? values) =>
        [.. (values ?? []).Select(v => v.Trim()).Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];

    private void Apply(ExerciseDetails d)
    {
        Name = d.Name.Trim();
        Description = Clean(d.Description);
        PrimaryMuscle = d.PrimaryMuscle;
        SecondaryMuscles = [.. d.SecondaryMuscles.Where(m => m != d.PrimaryMuscle).Distinct().Select(m => m.ToString())];
        Type = d.Type;
        Equipment = d.Equipment;
        Difficulty = d.Difficulty;
        TrackingMode = d.TrackingMode;
        Instructions = [.. (d.Instructions ?? []).Select(s => s.Trim()).Where(s => s.Length > 0)];
        CommonMistakes = NormalizeList(d.CommonMistakes);
        Warnings = Clean(d.Warnings);
        VideoUrl = Clean(d.VideoUrl);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record ExerciseDetails(
    string Name,
    string? Description,
    MuscleGroup PrimaryMuscle,
    IReadOnlyList<MuscleGroup> SecondaryMuscles,
    ExerciseType Type,
    Equipment Equipment,
    Difficulty Difficulty,
    TrackingMode TrackingMode,
    IReadOnlyList<string>? Instructions,
    IReadOnlyList<string>? CommonMistakes,
    string? Warnings,
    string? VideoUrl);
