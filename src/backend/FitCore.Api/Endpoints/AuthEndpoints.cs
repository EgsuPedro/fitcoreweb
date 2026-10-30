using FitCore.Api.Infrastructure;
using FitCore.Application.Core;
using FitCore.Platform.Application.Auth;
using FitCore.Platform.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace FitCore.Api.Endpoints;

internal static class AuthEndpoints
{
    private const string RefreshCookie = "fc_refresh";
    private const string RefreshCookiePath = "/api/v1/auth";

    public sealed record TokenResponse(
        string AccessToken,
        string TokenType,
        int ExpiresIn,
        string? RefreshToken,
        DateTimeOffset RefreshTokenExpiresAt);

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/auth").WithTags("Auth");

        group.MapPost("/login", async (LoginRequest request, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
            {
                var result = await dispatcher.SendAsync(new LoginCommand(request), ct);
                return result.IsSuccess ? ToTokenResponse(http, result.Value, request.UseCookie) : result.Error.ToProblem();
            })
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.Login)
            .WithValidation<LoginRequest>()
            .Produces<TokenResponse>()
            .WithName("Login");

        // Recuperación de contraseña: la respuesta es siempre la misma exista o no la cuenta.
        group.MapPost("/forgot-password", async (ForgotPasswordRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new ForgotPasswordCommand(request), ct)).ToHttp(r => Results.Json(r, statusCode: StatusCodes.Status202Accepted)))
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.Recovery)
            .WithValidation<ForgotPasswordRequest>()
            .Produces<PasswordRecoveryAccepted>(StatusCodes.Status202Accepted)
            .WithName("ForgotPassword");

        group.MapPost("/reset-password", async (ResetPasswordRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new ResetPasswordCommand(request), ct)).ToHttp(_ => Results.NoContent()))
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.Recovery)
            .WithValidation<ResetPasswordRequest>()
            .Produces(StatusCodes.Status204NoContent)
            .WithName("ResetPassword");

        // El refresh token llega en el cuerpo (móvil) o en la cookie HttpOnly (web).
        group.MapPost("/refresh", async ([FromBody] RefreshRequest? request, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
            {
                var fromCookie = string.IsNullOrWhiteSpace(request?.RefreshToken);
                var token = fromCookie ? http.Request.Cookies[RefreshCookie] : request!.RefreshToken;

                var result = await dispatcher.SendAsync(new RefreshCommand(token), ct);
                if (result.IsFailure)
                {
                    ClearCookie(http);
                    return result.Error.ToProblem();
                }

                return ToTokenResponse(http, result.Value, useCookie: fromCookie);
            })
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.Session)
            .Produces<TokenResponse>()
            .WithName("RefreshToken");

        group.MapPost("/logout", async ([FromBody] RefreshRequest? request, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
            {
                var token = string.IsNullOrWhiteSpace(request?.RefreshToken) ? http.Request.Cookies[RefreshCookie] : request!.RefreshToken;
                await dispatcher.SendAsync(new LogoutCommand(token), ct);
                ClearCookie(http);
                return Results.NoContent();
            })
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.Session)
            .WithName("Logout");

        group.MapPost("/change-password", async (ChangePasswordRequest request, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
            {
                var result = await dispatcher.SendAsync(new ChangePasswordCommand(request), ct);
                if (result.IsSuccess)
                {
                    ClearCookie(http);
                }

                return result.ToHttp(_ => Results.NoContent());
            })
            .RequireAuthorization()
            .WithValidation<ChangePasswordRequest>()
            .WithName("ChangePassword");

        group.MapGet("/me", async (IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.QueryAsync(new MeQuery(), ct)).ToHttp(me => Results.Ok(me)))
            .RequireAuthorization()
            .Produces<MeResponse>()
            .WithName("GetMe");

        return routes;
    }

    private static IResult ToTokenResponse(HttpContext http, AuthTokens tokens, bool useCookie)
    {
        if (useCookie)
        {
            var environment = http.RequestServices.GetRequiredService<IWebHostEnvironment>();
            http.Response.Cookies.Append(RefreshCookie, tokens.RefreshToken, new CookieOptions
            {
                HttpOnly = true,
                Secure = !environment.IsDevelopment(),
                SameSite = SameSiteMode.Strict,
                Path = RefreshCookiePath,
                Expires = tokens.RefreshTokenExpiresAt
            });
        }

        return Results.Ok(new TokenResponse(
            tokens.AccessToken,
            "Bearer",
            tokens.ExpiresIn,
            useCookie ? null : tokens.RefreshToken,
            tokens.RefreshTokenExpiresAt));
    }

    private static void ClearCookie(HttpContext http) =>
        http.Response.Cookies.Delete(RefreshCookie, new CookieOptions { Path = RefreshCookiePath });
}
