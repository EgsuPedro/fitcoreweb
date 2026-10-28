using FitCore.Wellness.Application.Exercises;
using FitCore.Wellness.Domain;

namespace FitCore.Wellness.UnitTests;

public class ExerciseDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private static ExerciseDetails Details(
        string name = "  Remo con barra  ",
        MuscleGroup primary = MuscleGroup.Back,
        MuscleGroup[]? secondary = null,
        string[]? steps = null,
        string[]? mistakes = null,
        string? video = null) =>
        new(name, "  Tracción horizontal  ", primary, secondary ?? [MuscleGroup.Biceps, MuscleGroup.Back, MuscleGroup.Biceps], ExerciseType.Strength,
            Equipment.Barbell, Difficulty.Intermediate, TrackingMode.RepsAndLoad, steps ?? ["  Paso uno  ", "", "Paso dos"], mistakes ?? ["Error", "error", "  "], "  Cuidado  ", video);

    [Fact]
    public void Crear_normaliza_los_textos_y_los_musculos_secundarios()
    {
        var exercise = Exercise.Create(Guid.NewGuid(), Details(), Guid.NewGuid(), Now);

        Assert.Equal("Remo con barra", exercise.Name);
        Assert.Equal("Tracción horizontal", exercise.Description);
        // El músculo principal no se repite como secundario, y los repetidos se eliminan.
        Assert.Equal(["Biceps"], exercise.SecondaryMuscles);
        Assert.Equal(["Paso uno", "Paso dos"], exercise.Instructions);
        Assert.Equal(["Error"], exercise.CommonMistakes);
        Assert.Equal("Cuidado", exercise.Warnings);
        Assert.True(exercise.IsActive);
        Assert.False(exercise.IsGlobal);
    }

    [Fact]
    public void Sin_gimnasio_es_global()
    {
        Assert.True(Exercise.Create(null, Details(), null, Now).IsGlobal);
    }

    [Fact]
    public void Desactivar_y_actualizar_registra_quien_y_cuando_sin_cambiar_la_propiedad()
    {
        var tenant = Guid.NewGuid();
        var exercise = Exercise.Create(tenant, Details(), null, Now);
        var by = Guid.NewGuid();

        exercise.SetActive(false, by, Now.AddDays(1));
        exercise.Update(Details("Remo con mancuerna"), by, Now.AddDays(2));

        Assert.False(exercise.IsActive);
        Assert.Equal("Remo con mancuerna", exercise.Name);
        Assert.Equal(tenant, exercise.TenantId);
        Assert.Equal(by, exercise.UpdatedBy);
        Assert.Equal(Now.AddDays(2), exercise.UpdatedAt);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("https://ejemplo.com/video", true)]
    [InlineData("http://ejemplo.com/video", false)]
    [InlineData("ftp://ejemplo.com/a", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("//ejemplo.com/a", false)]
    [InlineData("no es una url", false)]
    public void El_enlace_multimedia_solo_acepta_https(string? url, bool expected) =>
        Assert.Equal(expected, ExerciseRules.IsHttpsUrl(url));

    private static ExerciseRequest Request(string name = "Ejercicio válido", string[]? steps = null, string? video = null, MuscleGroup[]? secondary = null) =>
        new(name, null, MuscleGroup.Chest, secondary, ExerciseType.Strength, Equipment.Dumbbell, Difficulty.Beginner, TrackingMode.RepsAndLoad, steps, null, null, video);

    [Fact]
    public void El_validador_acepta_una_peticion_completa_y_rechaza_los_excesos()
    {
        var validator = new ExerciseRequestValidator();

        Assert.True(validator.Validate(Request()).IsValid);
        Assert.False(validator.Validate(Request(name: "A")).IsValid);
        Assert.False(validator.Validate(Request(name: new string('x', 121))).IsValid);
        Assert.False(validator.Validate(Request(steps: [.. Enumerable.Range(1, 21).Select(i => $"Paso {i}")])).IsValid);
        Assert.False(validator.Validate(Request(steps: [new string('x', 501)])).IsValid);
        Assert.False(validator.Validate(Request(video: "http://inseguro.test/v")).IsValid);
        Assert.False(validator.Validate(Request(secondary: [MuscleGroup.Back, MuscleGroup.Biceps, MuscleGroup.Triceps, MuscleGroup.Core, MuscleGroup.Glutes, MuscleGroup.Calves])).IsValid);
    }

    // ---- Catálogo base ----

    [Fact]
    public void El_catalogo_base_tiene_al_menos_40_ejercicios_con_nombres_unicos()
    {
        Assert.True(ExerciseCatalog.All.Count >= 40, $"Hay {ExerciseCatalog.All.Count}");
        var names = ExerciseCatalog.All.Select(e => e.Name.ToUpperInvariant()).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Fact]
    public void Todo_ejercicio_del_catalogo_es_valido_y_no_trae_multimedia_ni_enlaces()
    {
        var validator = new ExerciseRequestValidator();
        foreach (var d in ExerciseCatalog.All)
        {
            // Sin vídeos ni enlaces: el sistema nunca inventa recursos multimedia.
            Assert.Null(d.VideoUrl);
            Assert.DoesNotContain("http", string.Join(' ', d.Instructions ?? []) + d.Description + d.Warnings, StringComparison.OrdinalIgnoreCase);
            var request = new ExerciseRequest(d.Name, d.Description, d.PrimaryMuscle, [.. d.SecondaryMuscles], d.Type, d.Equipment, d.Difficulty, d.TrackingMode, d.Instructions, d.CommonMistakes, d.Warnings, d.VideoUrl);
            var result = validator.Validate(request);
            Assert.True(result.IsValid, $"{d.Name}: {string.Join("; ", result.Errors.Select(e => e.ErrorMessage))}");
            Assert.NotEmpty(d.Instructions ?? []);
            Assert.False(string.IsNullOrWhiteSpace(d.Warnings));
            Assert.DoesNotContain(d.PrimaryMuscle, d.SecondaryMuscles);
        }
    }

    [Fact]
    public void Los_ejercicios_de_peso_corporal_no_piden_carga_y_la_cardio_se_mide_por_tiempo()
    {
        Assert.All(
            ExerciseCatalog.All.Where(e => e.Equipment == Equipment.BodyWeight && e.Type == ExerciseType.Strength && e.TrackingMode == TrackingMode.RepsAndLoad),
            e => Assert.Fail($"{e.Name} es de peso corporal y no debería pedir carga"));
        Assert.All(ExerciseCatalog.All.Where(e => e.Type == ExerciseType.Cardio), e => Assert.Equal(TrackingMode.Duration, e.TrackingMode));
        Assert.All(ExerciseCatalog.All.Where(e => e.Equipment is Equipment.Barbell or Equipment.Machine or Equipment.Cable && e.Type == ExerciseType.Strength),
            e => Assert.Equal(TrackingMode.RepsAndLoad, e.TrackingMode));
    }

    [Fact]
    public void El_catalogo_cubre_los_grupos_musculares_y_varios_niveles()
    {
        var primaries = ExerciseCatalog.All.Select(e => e.PrimaryMuscle).Distinct().ToList();
        foreach (var muscle in new[] { MuscleGroup.Chest, MuscleGroup.Back, MuscleGroup.Shoulders, MuscleGroup.Biceps, MuscleGroup.Triceps, MuscleGroup.Core, MuscleGroup.Quadriceps, MuscleGroup.Hamstrings, MuscleGroup.Glutes, MuscleGroup.Calves, MuscleGroup.FullBody })
        {
            Assert.Contains(muscle, primaries);
        }

        Assert.Equal(3, ExerciseCatalog.All.Select(e => e.Difficulty).Distinct().Count());
    }
}
