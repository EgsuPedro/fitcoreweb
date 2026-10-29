using FitCore.Api.Infrastructure;
using FitCore.Application.Core;
using FitCore.SharedKernel;
using FitCore.Subscriptions.Application.Providers;

namespace FitCore.Api.Endpoints;

/// <summary>
/// Entrada de los webhooks del proveedor de pagos. No usa sesión: se autentica por la <b>firma</b> del cuerpo (HMAC con un secreto compartido y una marca de
/// tiempo firmada), que se verifica antes de leer nada más. Sin proveedor configurado responde 503 y no procesa nada. Una firma inválida o vencida es un 401
/// idéntico (no revela cuál falló). Los eventos válidos son idempotentes; los que no concuerdan con la factura se registran y no cambian nada.
/// </summary>
internal static class WebhookEndpoints
{
    public static IEndpointRouteBuilder MapWebhookEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/webhooks/payments", async (HttpContext http, IPaymentProviderGateway gateway, IDispatcher dispatcher, IClock clock, CancellationToken ct) =>
            {
                if (!gateway.IsConfigured)
                {
                    return Reply(StatusCodes.Status503ServiceUnavailable, ProviderErrors.NotConfigured);
                }

                var max = gateway.MaxBodyBytes;
                if (http.Request.ContentLength > max)
                {
                    return Reply(StatusCodes.Status413PayloadTooLarge, ProviderErrors.PayloadInvalid);
                }

                // Se lee a lo sumo max+1 bytes: un cuerpo sin Content-Length (chunked) tampoco puede pasar del límite.
                var buffer = new byte[max + 1];
                var total = 0;
                int read;
                while (total < buffer.Length && (read = await http.Request.Body.ReadAsync(buffer.AsMemory(total), ct)) > 0)
                {
                    total += read;
                }

                if (total > max)
                {
                    return Reply(StatusCodes.Status413PayloadTooLarge, ProviderErrors.PayloadInvalid);
                }

                var verified = gateway.VerifyAndParse(http.Request.Headers[SignedWebhookGateway.SignatureHeader].ToString(), buffer.AsSpan(0, total), clock.UtcNow);
                if (verified.IsFailure)
                {
                    // Firma o marca de tiempo inválidas: misma respuesta, sin pistas. Un cuerpo ilegible pero bien firmado es un 400.
                    return verified.Error.Code == ProviderErrors.PayloadInvalid.Code
                        ? Reply(StatusCodes.Status400BadRequest, ProviderErrors.PayloadInvalid)
                        : Reply(StatusCodes.Status401Unauthorized, ProviderErrors.SignatureInvalid);
                }

                var processed = await dispatcher.SendAsync(new ProcessProviderEventCommand(verified.Value), ct);
                return processed.IsFailure
                    ? Reply(StatusCodes.Status500InternalServerError, processed.Error)
                    : Results.Ok(processed.Value);
            })
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.Webhooks)
            .WithTags("Webhooks")
            .Produces<WebhookReceiptDto>()
            .WithName("ReceivePaymentWebhook");

        return routes;
    }

    private static IResult Reply(int status, Error error) => Results.Json(new { code = error.Code, message = error.Description }, statusCode: status);
}
