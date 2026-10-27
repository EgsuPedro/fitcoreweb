using FitCore.Platform.Persistence;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FitCore.Api.Infrastructure;

/// <summary>Convierte excepciones no controladas en ProblemDetails (RFC 9457) sin filtrar detalles internos.</summary>
internal sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger, IProblemDetailsService problemDetails)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title, code) = Classify(exception);

        if (status >= StatusCodes.Status500InternalServerError)
        {
            logger.LogError(exception, "Excepción no controlada en {Method} {Path}", httpContext.Request.Method, httpContext.Request.Path);
        }
        else
        {
            logger.LogWarning(exception, "Solicitud rechazada en {Method} {Path}: {Code}", httpContext.Request.Method, httpContext.Request.Path, code);
        }

        httpContext.Response.StatusCode = status;

        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = title,
                Type = $"https://fitcore360.dev/errors/{code}",
                Extensions = { ["code"] = code }
            }
        });
    }

    private static (int Status, string Title, string Code) Classify(Exception exception) => exception switch
    {
        // Dos solicitudes simultáneas crearon el mismo registro único (correo, código de sucursal, etc.).
        DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } } =>
            (StatusCodes.Status409Conflict, "Ya existe un registro con esos datos.", "conflict.duplicate"),
        DbUpdateConcurrencyException =>
            (StatusCodes.Status409Conflict, "El registro fue modificado por otra persona. Recárguelo e intente de nuevo.", "conflict.concurrency"),
        // Cuerpo ilegible (JSON mal formado, UTF-8 inválido): error del cliente, no del servidor.
        BadHttpRequestException =>
            (StatusCodes.Status400BadRequest, "Los datos de la solicitud no son válidos.", "request.invalid_body"),
        TenantViolationException =>
            (StatusCodes.Status500InternalServerError, "Error interno del servidor", "server.tenant_violation"),
        _ => (StatusCodes.Status500InternalServerError, "Error interno del servidor", "server.internal_error")
    };
}
