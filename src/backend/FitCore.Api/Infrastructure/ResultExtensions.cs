using FitCore.SharedKernel;

namespace FitCore.Api.Infrastructure;

internal static class ResultExtensions
{
    /// <summary>Traduce un <see cref="Error"/> de negocio a ProblemDetails con código estable.</summary>
    public static IResult ToProblem(this Error error)
    {
        var status = error.Type switch
        {
            ErrorType.Validation => StatusCodes.Status422UnprocessableEntity,
            ErrorType.NotFound => StatusCodes.Status404NotFound,
            ErrorType.Conflict => StatusCodes.Status409Conflict,
            ErrorType.Forbidden => StatusCodes.Status403Forbidden,
            ErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
            _ => StatusCodes.Status500InternalServerError
        };

        return Results.Problem(
            statusCode: status,
            title: error.Description,
            extensions: new Dictionary<string, object?> { ["code"] = error.Code });
    }

    public static IResult ToHttp<T>(this Result<T> result, Func<T, IResult> onSuccess) =>
        result.IsSuccess ? onSuccess(result.Value) : result.Error.ToProblem();
}
