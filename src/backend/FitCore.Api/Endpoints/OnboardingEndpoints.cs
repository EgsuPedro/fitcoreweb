using FitCore.Api.Infrastructure;
using FitCore.Application.Core;
using FitCore.Platform.Application.Onboarding;
using FitCore.Platform.Domain;

namespace FitCore.Api.Endpoints;

/// <summary>
/// Asistente de configuración inicial del gimnasio. El avance se guarda en el servidor, así que se puede cerrar sesión y retomar. Siempre actúa sobre el gimnasio del token.
/// </summary>
internal static class OnboardingEndpoints
{
    public static IEndpointRouteBuilder MapOnboardingEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/onboarding").WithTags("Configuración inicial");

        group.MapGet("/", async (IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.QueryAsync(new GetOnboardingQuery(), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.OrganizationRead)
            .Produces<OnboardingDto>()
            .WithName("GetOnboarding");

        group.MapPut("/profile", async (OnboardingProfileRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new SaveOnboardingProfileCommand(request), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.OrganizationUpdate)
            .WithValidation<OnboardingProfileRequest>()
            .Produces<OnboardingDto>()
            .WithName("SaveOnboardingProfile");

        group.MapPut("/branch", async (OnboardingBranchRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new SaveOnboardingBranchCommand(request), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.OrganizationUpdate)
            .WithValidation<OnboardingBranchRequest>()
            .Produces<OnboardingDto>()
            .WithName("SaveOnboardingBranch");

        group.MapPost("/steps/{step}/complete", async (string step, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new ResolveOnboardingStepCommand(step, Skip: false), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.OrganizationUpdate)
            .Produces<OnboardingDto>()
            .WithName("CompleteOnboardingStep");

        group.MapPost("/steps/{step}/skip", async (string step, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new ResolveOnboardingStepCommand(step, Skip: true), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.OrganizationUpdate)
            .Produces<OnboardingDto>()
            .WithName("SkipOnboardingStep");

        group.MapPost("/complete", async (IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new CompleteOnboardingCommand(), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.OrganizationUpdate)
            .Produces<OnboardingDto>()
            .WithName("CompleteOnboarding");

        return routes;
    }
}
