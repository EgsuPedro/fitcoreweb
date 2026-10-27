using System.Reflection;
using FitCore.SharedKernel;

namespace FitCore.Api.Endpoints;

internal static class SystemEndpoints
{
    public sealed record SystemInfoResponse(string Name, string Version, string Environment, DateTimeOffset UtcNow);

    public static IEndpointRouteBuilder MapSystemEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/system/info", (IWebHostEnvironment env, IClock clock) =>
                new SystemInfoResponse(
                    "FitCore 360",
                    Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0",
                    env.EnvironmentName,
                    clock.UtcNow))
            .WithName("GetSystemInfo")
            .WithTags("System")
            .AllowAnonymous();

        return routes;
    }
}
