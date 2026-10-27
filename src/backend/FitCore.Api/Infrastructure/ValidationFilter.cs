using FluentValidation;

namespace FitCore.Api.Infrastructure;

/// <summary>Valida el cuerpo de la solicitud con FluentValidation y responde 422 con el detalle por campo.</summary>
internal sealed class ValidationFilter<T>(IValidator<T> validator) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var argument = context.Arguments.OfType<T>().FirstOrDefault();
        if (argument is null)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "El cuerpo de la solicitud es obligatorio.",
                extensions: new Dictionary<string, object?> { ["code"] = "request.body_required" });
        }

        var result = await validator.ValidateAsync(argument, context.HttpContext.RequestAborted);
        if (!result.IsValid)
        {
            var errors = result.Errors
                .GroupBy(e => char.ToLowerInvariant(e.PropertyName[0]) + e.PropertyName[1..])
                .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray());

            return Results.ValidationProblem(
                errors,
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "Hay errores de validación.",
                extensions: new Dictionary<string, object?> { ["code"] = "validation.failed" });
        }

        return await next(context);
    }
}

internal static class ValidationExtensions
{
    public static RouteHandlerBuilder WithValidation<T>(this RouteHandlerBuilder builder) =>
        builder.AddEndpointFilter<ValidationFilter<T>>();
}
