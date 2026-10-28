using FitCore.Wellness.Domain;

namespace FitCore.Wellness.UnitTests;

public class AssessmentDomainTests
{
    private static Measurements M(decimal? weight = null, decimal? height = null, decimal? waist = null, decimal? hip = null, decimal? fat = null, BodyFatMethod? method = null) =>
        new(weight, height, waist, hip, null, null, null, fat, method);

    // ---- IMC ----

    [Theory]
    [InlineData(70, 175, 22.9)]
    [InlineData(90, 170, 31.1)]
    [InlineData(50, 180, 15.4)]
    [InlineData(64, 160, 25.0)]
    public void El_imc_es_peso_sobre_estatura_al_cuadrado_con_un_decimal(double weight, double height, double expected) =>
        Assert.Equal((decimal)expected, AssessmentRules.Bmi((decimal)weight, (decimal)height));

    [Fact]
    public void Sin_peso_o_sin_estatura_valida_no_hay_imc_y_nunca_se_divide_por_cero()
    {
        Assert.Null(AssessmentRules.Bmi(null, 170m));
        Assert.Null(AssessmentRules.Bmi(70m, null));
        Assert.Null(AssessmentRules.Bmi(70m, 0m));
        Assert.Null(AssessmentRules.Bmi(70m, -170m));
        Assert.Null(AssessmentRules.Bmi(70m, 49m));
        Assert.Null(AssessmentRules.Bmi(1m, 170m));
        Assert.Null(AssessmentRules.Bmi(600m, 170m));
    }

    [Fact]
    public void El_rango_de_referencia_solo_se_informa_para_adultos_y_no_es_un_diagnostico()
    {
        Assert.Equal("Below", AssessmentRules.BmiReferenceRange(18.4m, 30));
        Assert.Equal("Reference", AssessmentRules.BmiReferenceRange(18.5m, 30));
        Assert.Equal("Reference", AssessmentRules.BmiReferenceRange(24.9m, 30));
        Assert.Equal("Above", AssessmentRules.BmiReferenceRange(25m, 30));
        Assert.Equal("Above", AssessmentRules.BmiReferenceRange(35m, 18));
        Assert.Null(AssessmentRules.BmiReferenceRange(22m, 17));
        Assert.Null(AssessmentRules.BmiReferenceRange(22m, null));
        Assert.Null(AssessmentRules.BmiReferenceRange(null, 30));
    }

    [Fact]
    public void La_edad_se_calcula_en_la_fecha_de_la_evaluacion()
    {
        var birth = new DateOnly(2000, 6, 15);

        Assert.Equal(25, AssessmentRules.AgeOn(birth, new DateOnly(2026, 6, 14)));
        Assert.Equal(26, AssessmentRules.AgeOn(birth, new DateOnly(2026, 6, 15)));
        Assert.Null(AssessmentRules.AgeOn(null, new DateOnly(2026, 1, 1)));
        Assert.Null(AssessmentRules.AgeOn(birth, new DateOnly(1999, 1, 1)));
    }

    [Fact]
    public void La_relacion_cintura_cadera_necesita_ambas_medidas()
    {
        Assert.Equal(0.85m, AssessmentRules.WaistHipRatio(85m, 100m));
        Assert.Null(AssessmentRules.WaistHipRatio(85m, null));
        Assert.Null(AssessmentRules.WaistHipRatio(null, 100m));
        Assert.Null(AssessmentRules.WaistHipRatio(85m, 0m));
    }

    // ---- Validación ----

    [Fact]
    public void Una_evaluacion_necesita_al_menos_una_medicion()
    {
        Assert.Equal("assessment.empty", AssessmentRules.Validate(M()));
        Assert.Null(AssessmentRules.Validate(M(weight: 70m)));
        Assert.Null(AssessmentRules.Validate(M(waist: 80m)));
    }

    [Fact]
    public void Los_rangos_de_peso_estatura_y_perimetros_se_respetan()
    {
        Assert.Equal("assessment.weight_invalid", AssessmentRules.Validate(M(weight: 1.9m)));
        Assert.Equal("assessment.weight_invalid", AssessmentRules.Validate(M(weight: 501m)));
        Assert.Equal("assessment.height_invalid", AssessmentRules.Validate(M(height: 49m)));
        Assert.Equal("assessment.height_invalid", AssessmentRules.Validate(M(height: 261m)));
        Assert.Equal("assessment.circumference_invalid", AssessmentRules.Validate(M(waist: 4m)));
        Assert.Equal("assessment.circumference_invalid", AssessmentRules.Validate(M(weight: 70m, hip: 251m)));
        Assert.Null(AssessmentRules.Validate(M(weight: 2m, height: 50m, waist: 5m, hip: 250m)));
    }

    [Fact]
    public void La_grasa_corporal_solo_se_registra_medida_y_con_su_metodo()
    {
        Assert.Null(AssessmentRules.Validate(M(fat: 18.5m, method: BodyFatMethod.Caliper)));
        Assert.Equal("assessment.body_fat_method_required", AssessmentRules.Validate(M(fat: 18.5m)));
        Assert.Equal("assessment.body_fat_method_without_value", AssessmentRules.Validate(M(weight: 70m, method: BodyFatMethod.Dexa)));
        Assert.Equal("assessment.body_fat_invalid", AssessmentRules.Validate(M(fat: 1m, method: BodyFatMethod.Caliper)));
        Assert.Equal("assessment.body_fat_invalid", AssessmentRules.Validate(M(fat: 71m, method: BodyFatMethod.Caliper)));
    }

    // ---- Entidad ----

    [Fact]
    public void La_evaluacion_guarda_las_mediciones_la_fuente_y_limpia_la_nota()
    {
        var a = BodyAssessment.Create(
            Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 3, 1), M(weight: 70.5m, height: 175m, fat: 20m, method: BodyFatMethod.Bioimpedance),
            MeasurementSource.Scale, "  En ayunas  ", null, Guid.NewGuid(), DateTimeOffset.UnixEpoch);

        Assert.Equal((70.5m, 175m, 20m), (a.WeightKg, a.HeightCm, a.BodyFatPercent));
        Assert.Equal((BodyFatMethod.Bioimpedance, MeasurementSource.Scale, "En ayunas"), (a.BodyFatMethod, a.Source, a.Notes));
    }

    [Fact]
    public void Sin_porcentaje_de_grasa_el_metodo_no_se_guarda()
    {
        var a = BodyAssessment.Create(
            Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 3, 1), M(weight: 70m, method: BodyFatMethod.Dexa), MeasurementSource.Manual, null, null, Guid.NewGuid(), DateTimeOffset.UnixEpoch);

        Assert.Null(a.BodyFatMethod);
    }
}
