using FitCore.Commercial.Domain;

namespace FitCore.Commercial.UnitTests;

public class IdentificationValidatorTests
{
    [Theory]
    [InlineData("1712345675")] // Pichincha
    [InlineData("0912345675")] // Guayas
    [InlineData("171234 5675")] // se normalizan espacios
    [InlineData("171234-5675")] // y guiones
    public void Cedulas_validas(string number) => Assert.True(IdentificationValidator.IsValid(IdentificationType.Cedula, number));

    [Theory]
    [InlineData("1712345676")] // dígito verificador incorrecto
    [InlineData("1712345670")]
    [InlineData("9912345675")] // provincia inexistente
    [InlineData("1792345675")] // tercer dígito 9: no es persona natural
    [InlineData("171234567")] // corta
    [InlineData("17123456755")] // larga
    [InlineData("17123A5675")] // letras
    [InlineData("")]
    public void Cedulas_invalidas(string number) => Assert.False(IdentificationValidator.IsValid(IdentificationType.Cedula, number));

    [Theory]
    [InlineData("1712345675001")] // persona natural
    [InlineData("1790000028001")] // sociedad privada
    [InlineData("1760001550001")] // entidad pública
    public void Ruc_validos(string number) => Assert.True(IdentificationValidator.IsValid(IdentificationType.Ruc, number));

    [Theory]
    [InlineData("1712345675000")] // establecimiento 000
    [InlineData("1712345676001")] // cédula base inválida
    [InlineData("1790000029001")] // verificador de sociedad incorrecto
    [InlineData("1760001540001")] // verificador público incorrecto (el 9.º dígito debe ser 5)
    [InlineData("1760001550000")] // establecimiento público 0000
    [InlineData("1772345675001")] // tercer dígito 7: no existe
    [InlineData("171234567500")] // corto
    public void Ruc_invalidos(string number) => Assert.False(IdentificationValidator.IsValid(IdentificationType.Ruc, number));

    [Theory]
    [InlineData("AB123456")]
    [InlineData("pa-12345")]
    public void Pasaportes_validos(string number) => Assert.True(IdentificationValidator.IsValid(IdentificationType.Passport, number));

    [Theory]
    [InlineData("AB12")] // muy corto
    [InlineData("AB1234567890123456789")] // muy largo
    [InlineData("AB12#456")]
    public void Pasaportes_invalidos(string number) => Assert.False(IdentificationValidator.IsValid(IdentificationType.Passport, number));

    [Fact]
    public void Normaliza_a_mayusculas_sin_espacios_ni_guiones() =>
        Assert.Equal("AB123456", IdentificationValidator.Normalize(" ab-12 3456 "));
}
