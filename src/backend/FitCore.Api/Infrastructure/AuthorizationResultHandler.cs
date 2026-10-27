using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace FitCore.Api.Infrastructure;

/// <summary>
/// Un usuario desactivado o de un gimnasio suspendido sigue teniendo un JWT firmado y vigente, pero su sesión
/// ya no es válida: responde 401 (no 403) para que el cliente cierre la sesión.
/// </summary>
internal sealed class SessionAwareAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Forbidden &&
            authorizeResult.AuthorizationFailure?.FailedRequirements.OfType<ActiveUserRequirement>().Any() == true)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        }

        return _default.HandleAsync(next, context, policy, authorizeResult);
    }
}
