using System.Diagnostics;
using System.Security.Claims;
using FitCore.Application.Core;
using FitCore.Platform.Security;

namespace FitCore.Api.Infrastructure;

internal sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    public Guid? UserId => ReadGuid(ClaimNames.Subject);

    public Guid? TenantId => ReadGuid(ClaimNames.TenantId);

    private Guid? ReadGuid(string claim) =>
        Guid.TryParse(Principal?.FindFirstValue(claim), out var value) ? value : null;
}

internal sealed class HttpRequestInfo(IHttpContextAccessor accessor) : IRequestInfo
{
    public string? IpAddress => accessor.HttpContext?.Connection.RemoteIpAddress?.ToString();

    public string? UserAgent => accessor.HttpContext?.Request.Headers.UserAgent.ToString() is { Length: > 0 } agent ? agent : null;

    public string? CorrelationId => Activity.Current?.TraceId.ToString() ?? accessor.HttpContext?.TraceIdentifier;
}
