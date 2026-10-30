using FitCore.Api.Infrastructure;
using FitCore.Application.Core;
using FitCore.Platform.Application.Contact;
using FitCore.Platform.Application.Invitations;
using FitCore.Platform.Application.Registration;
using FitCore.Subscriptions.Application.Public;

namespace FitCore.Api.Endpoints;

/// <summary>
/// Rutas públicas del sitio comercial: sin sesión, con límite de frecuencia por IP y sin datos privados de ningún gimnasio.
/// Los planes se leen del catálogo real; nunca hay precios escritos a mano en el sitio.
/// </summary>
internal static class PublicEndpoints
{
    public static IEndpointRouteBuilder MapPublicEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/public").WithTags("Sitio público");

        group.MapGet("/plans", async (IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
            {
                var result = await dispatcher.QueryAsync(new ListPublicPlansQuery(), ct);
                if (result.IsSuccess)
                {
                    CachePublic(http);
                }

                return result.ToHttp(r => Results.Ok(r));
            })
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.PublicRead)
            .Produces<IReadOnlyList<PublicPlanDto>>()
            .WithName("ListPublicPlans");

        group.MapGet("/plans/{code}", async (string code, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
            {
                var result = await dispatcher.QueryAsync(new GetPublicPlanQuery(code), ct);
                if (result.IsSuccess)
                {
                    CachePublic(http);
                }

                return result.ToHttp(r => Results.Ok(r));
            })
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.PublicRead)
            .Produces<PublicPlanDto>()
            .WithName("GetPublicPlan");

        // Registro de un gimnasio: la respuesta es siempre la misma (202) exista o no el correo, para no revelar quién está registrado.
        group.MapPost("/registrations", async (RegisterGymRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new RegisterGymCommand(request), ct)).ToHttp(r => Results.Json(r, statusCode: StatusCodes.Status202Accepted)))
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.Register)
            .WithValidation<RegisterGymRequest>()
            .Produces<RegistrationAccepted>(StatusCodes.Status202Accepted)
            .WithName("RegisterGym");

        group.MapPost("/registrations/verify-email", async (VerifyRegistrationRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new VerifyRegistrationCommand(request.Token), ct)).ToHttp(r => Results.Ok(r)))
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.Register)
            .WithValidation<VerifyRegistrationRequest>()
            .Produces<VerifiedRegistrationDto>()
            .WithName("VerifyRegistrationEmail");

        group.MapPost("/registrations/resend-verification", async (ResendVerificationRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new ResendVerificationCommand(request.Email), ct)).ToHttp(r => Results.Json(r, statusCode: StatusCodes.Status202Accepted)))
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.Register)
            .WithValidation<ResendVerificationRequest>()
            .Produces<RegistrationAccepted>(StatusCodes.Status202Accepted)
            .WithName("ResendRegistrationVerification");

        // Contacto y demostraciones: respuesta siempre igual (202), con campo trampa, límite por IP y por correo. Sin CAPTCHA (punto de extensión pendiente).
        group.MapPost("/contact", async (ContactRequestBody request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new SubmitContactCommand(ContactKinds.Contact, request), ct)).ToHttp(r => Results.Json(r, statusCode: StatusCodes.Status202Accepted)))
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.Register)
            .WithValidation<ContactRequestBody>()
            .Produces<ContactReceived>(StatusCodes.Status202Accepted)
            .WithName("SubmitContact");

        group.MapPost("/demo-requests", async (ContactRequestBody request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new SubmitContactCommand(ContactKinds.Demo, request), ct)).ToHttp(r => Results.Json(r, statusCode: StatusCodes.Status202Accepted)))
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.Register)
            .WithValidation<ContactRequestBody>()
            .Produces<ContactReceived>(StatusCodes.Status202Accepted)
            .WithName("SubmitDemoRequest");

        // Invitaciones de personal: ver de qué es el enlace y aceptarlo. El gimnasio sale del enlace, nunca del cuerpo.
        group.MapPost("/invitations/preview", async (PreviewInvitationRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.QueryAsync(new PreviewInvitationQuery(request.Token), ct)).ToHttp(r => Results.Ok(r)))
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.Register)
            .WithValidation<PreviewInvitationRequest>()
            .Produces<InvitationPreviewDto>()
            .WithName("PreviewInvitation");

        group.MapPost("/invitations/accept", async (AcceptInvitationRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new AcceptInvitationCommand(request), ct)).ToHttp(r => Results.Ok(r)))
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.Register)
            .WithValidation<AcceptInvitationRequest>()
            .Produces<AcceptedInvitationDto>()
            .WithName("AcceptInvitation");

        return routes;
    }

    // Un minuto de caché compartida: un cambio de precio se ve en menos de un minuto y el sitio resiste picos de visitas.
    private static void CachePublic(HttpContext http) =>
        http.Response.Headers.CacheControl = "public, max-age=60";
}
