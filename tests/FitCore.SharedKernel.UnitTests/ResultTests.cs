using FitCore.SharedKernel;

namespace FitCore.SharedKernel.UnitTests;

public class ResultTests
{
    [Fact]
    public void Success_no_tiene_error()
    {
        var result = Result.Success(42);

        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Value);
        Assert.Equal(Error.None, result.Error);
    }

    [Fact]
    public void Failure_expone_el_error_y_bloquea_el_valor()
    {
        var error = Error.NotFound("member.not_found", "Alumno no encontrado");
        Result<int> result = error;

        Assert.True(result.IsFailure);
        Assert.Equal(error, result.Error);
        Assert.Throws<InvalidOperationException>(() => result.Value);
    }

    [Fact]
    public void Result_exitoso_con_error_es_invalido()
    {
        Assert.Throws<ArgumentException>(() => Result.Failure(Error.None));
    }
}
