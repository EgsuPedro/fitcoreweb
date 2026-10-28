using FitCore.Application.Core;
using FitCore.SharedKernel;
using FitCore.Wellness.Domain;
using FluentValidation;

namespace FitCore.Wellness.Application.Exercises;

public sealed record ExerciseDto(
    Guid Id,
    bool IsGlobal,
    string Name,
    string? Description,
    string PrimaryMuscle,
    IReadOnlyList<string> SecondaryMuscles,
    string Type,
    string Equipment,
    string Difficulty,
    string TrackingMode,
    IReadOnlyList<string> Instructions,
    IReadOnlyList<string> CommonMistakes,
    string? Warnings,
    string? VideoUrl,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt)
{
    public static ExerciseDto From(Exercise e) =>
        new(
            e.Id,
            e.IsGlobal,
            e.Name,
            e.Description,
            e.PrimaryMuscle.ToString(),
            e.SecondaryMuscles,
            e.Type.ToString(),
            e.Equipment.ToString(),
            e.Difficulty.ToString(),
            e.TrackingMode.ToString(),
            e.Instructions,
            e.CommonMistakes,
            e.Warnings,
            e.VideoUrl,
            e.IsActive,
            e.CreatedAt,
            e.UpdatedAt);
}

public sealed record ExerciseRequest(
    string Name,
    string? Description,
    MuscleGroup PrimaryMuscle,
    IReadOnlyList<MuscleGroup>? SecondaryMuscles,
    ExerciseType Type,
    Equipment Equipment,
    Difficulty Difficulty,
    TrackingMode TrackingMode,
    IReadOnlyList<string>? Instructions,
    IReadOnlyList<string>? CommonMistakes,
    string? Warnings,
    string? VideoUrl)
{
    public ExerciseDetails ToDetails() =>
        new(Name, Description, PrimaryMuscle, SecondaryMuscles ?? [], Type, Equipment, Difficulty, TrackingMode, Instructions, CommonMistakes, Warnings, VideoUrl);
}

public sealed record SetExerciseStatusRequest(bool IsActive);

/// <param name="Scope"><c>all</c> (por defecto), <c>global</c> (catálogo base) u <c>own</c> (del gimnasio).</param>
public sealed record ListExercisesQuery(
    PageRequest Page,
    string? Search,
    MuscleGroup? Muscle,
    Equipment? Equipment,
    Difficulty? Difficulty,
    ExerciseType? Type,
    string? Scope,
    bool? IsActive) : IQuery<PagedResult<ExerciseDto>>;

public sealed record GetExerciseQuery(Guid Id) : IQuery<ExerciseDto>;

/// <param name="Global">Verdadero solo desde la administración de plataforma: crea o edita el catálogo base.</param>
public sealed record CreateExerciseCommand(ExerciseRequest Request, bool Global) : ICommand<ExerciseDto>;

public sealed record UpdateExerciseCommand(Guid Id, ExerciseRequest Request, bool Global) : ICommand<ExerciseDto>;

public sealed record SetExerciseStatusCommand(Guid Id, SetExerciseStatusRequest Request, bool Global) : ICommand<ExerciseDto>;

public sealed record DuplicateExerciseCommand(Guid Id) : ICommand<ExerciseDto>;

public sealed class ExerciseRequestValidator : AbstractValidator<ExerciseRequest>
{
    public ExerciseRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MinimumLength(2).MaximumLength(120);
        RuleFor(x => x.Description).MaximumLength(1000);
        RuleFor(x => x.PrimaryMuscle).IsInEnum();
        RuleFor(x => x.Type).IsInEnum();
        RuleFor(x => x.Equipment).IsInEnum();
        RuleFor(x => x.Difficulty).IsInEnum();
        RuleFor(x => x.TrackingMode).IsInEnum();
        RuleForEach(x => x.SecondaryMuscles).IsInEnum();
        RuleFor(x => x.SecondaryMuscles)
            .Must(list => list is null || list.Distinct().Count() <= Exercise.MaxSecondaryMuscles)
            .WithMessage($"Máximo {Exercise.MaxSecondaryMuscles} grupos musculares secundarios.");
        RuleFor(x => x.Instructions)
            .Must(list => list is null || list.Count(s => !string.IsNullOrWhiteSpace(s)) <= Exercise.MaxSteps)
            .WithMessage($"Máximo {Exercise.MaxSteps} pasos.")
            .Must(list => list is null || list.All(s => s.Trim().Length <= Exercise.MaxStepLength))
            .WithMessage($"Cada paso admite hasta {Exercise.MaxStepLength} caracteres.");
        RuleFor(x => x.CommonMistakes)
            .Must(list => list is null || Exercise.NormalizeList(list).Length <= Exercise.MaxMistakes)
            .WithMessage($"Máximo {Exercise.MaxMistakes} errores comunes.")
            .Must(list => list is null || list.All(s => s.Trim().Length <= Exercise.MaxMistakeLength))
            .WithMessage($"Cada error común admite hasta {Exercise.MaxMistakeLength} caracteres.");
        RuleFor(x => x.Warnings).MaximumLength(1000);
        RuleFor(x => x.VideoUrl)
            .MaximumLength(500)
            .Must(ExerciseRules.IsHttpsUrl)
            .WithMessage("El enlace debe ser una dirección https válida.");
    }
}

public sealed class SetExerciseStatusRequestValidator : AbstractValidator<SetExerciseStatusRequest>;

public static class ExerciseRules
{
    /// <summary>Vacío es válido (el enlace es opcional); si hay valor debe ser una URL absoluta https.</summary>
    public static bool IsHttpsUrl(string? value) =>
        string.IsNullOrWhiteSpace(value)
        || (Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && !string.IsNullOrEmpty(uri.Host));
}

public static class ExerciseErrors
{
    public static readonly Error NotFound = Error.NotFound("exercises.not_found", "Ejercicio no encontrado.");
    public static readonly Error NameTaken = Error.Conflict("exercises.name_taken", "Ya existe un ejercicio con ese nombre.");
    public static readonly Error GlobalReadOnly = Error.Forbidden("exercises.global_readonly", "Los ejercicios del catálogo base los administra la plataforma: duplíquelo para adaptarlo.");
}
