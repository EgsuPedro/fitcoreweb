using FitCore.Platform.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace FitCore.Api.Infrastructure;

public sealed class PermissionRequirement(string permission) : IAuthorizationRequirement
{
    public string Permission { get; } = permission;
}

/// <summary>Exige que el usuario exista, esté activo y su gimnasio no esté suspendido.</summary>
public sealed class ActiveUserRequirement : IAuthorizationRequirement;

internal sealed class PermissionHandler(IAccessContext access) : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        var current = await access.GetAsync();
        if (current is not null && current.Has(requirement.Permission))
        {
            context.Succeed(requirement);
        }
    }
}

internal sealed class ActiveUserHandler(IAccessContext access) : AuthorizationHandler<ActiveUserRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, ActiveUserRequirement requirement)
    {
        if (await access.GetAsync() is not null)
        {
            context.Succeed(requirement);
        }
    }
}

/// <summary>Crea al vuelo las políticas "perm:{codigo}" para <see cref="PermissionEndpointExtensions.RequirePermission"/>.</summary>
internal sealed class PermissionPolicyProvider(IOptions<AuthorizationOptions> options) : DefaultAuthorizationPolicyProvider(options)
{
    public const string Prefix = "perm:";

    public override Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (policyName.StartsWith(Prefix, StringComparison.Ordinal))
        {
            var policy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .AddRequirements(new ActiveUserRequirement(), new PermissionRequirement(policyName[Prefix.Length..]))
                .Build();
            return Task.FromResult<AuthorizationPolicy?>(policy);
        }

        return base.GetPolicyAsync(policyName);
    }
}

public static class PermissionEndpointExtensions
{
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permission)
        where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization($"{PermissionPolicyProvider.Prefix}{permission}");
}
