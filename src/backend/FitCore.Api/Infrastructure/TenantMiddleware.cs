using System.Security.Claims;
using FitCore.Application.Core;
using FitCore.Platform.Security;

namespace FitCore.Api.Infrastructure;

/// <summary>
/// Fija el tenant de la solicitud a partir del token. Es la ÚNICA fuente del tenant: ninguna ruta,
/// cuerpo ni cabecera puede cambiarlo.
/// </summary>
internal sealed class TenantContextMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context, ITenantContext tenantContext)
    {
        if (context.User.Identity?.IsAuthenticated == true &&
            Guid.TryParse(context.User.FindFirstValue(ClaimNames.TenantId), out var tenantId) &&
            tenantId != Guid.Empty)
        {
            tenantContext.Set(tenantId);
        }

        return next(context);
    }
}

/// <summary>Si la solicitud trae X-Branch-Id, valida que el usuario tenga acceso a esa sucursal.</summary>
internal sealed class BranchHeaderMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Branch-Id";

    public async Task InvokeAsync(HttpContext context, IAccessContext access)
    {
        if (context.User.Identity?.IsAuthenticated == true &&
            context.Request.Headers.TryGetValue(HeaderName, out var value) &&
            !string.IsNullOrWhiteSpace(value))
        {
            if (!Guid.TryParse(value, out var branchId))
            {
                await Results.Problem(
                        statusCode: StatusCodes.Status400BadRequest,
                        title: "El encabezado X-Branch-Id no es válido.",
                        extensions: new Dictionary<string, object?> { ["code"] = "branches.invalid_header" })
                    .ExecuteAsync(context);
                return;
            }

            var current = await access.GetAsync(context.RequestAborted);
            if (current is not null && !current.CanAccessBranch(branchId))
            {
                await Results.Problem(
                        statusCode: StatusCodes.Status403Forbidden,
                        title: "No tiene acceso a esta sucursal.",
                        extensions: new Dictionary<string, object?> { ["code"] = "branches.not_allowed" })
                    .ExecuteAsync(context);
                return;
            }

            context.Items[HeaderName] = branchId;
        }

        await next(context);
    }
}
