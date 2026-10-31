using FitCore.Api.Infrastructure;
using FitCore.Application.Core;
using FitCore.Invoicing.Application.Settings;
using FitCore.Platform.Domain;
using Microsoft.AspNetCore.Mvc;

namespace FitCore.Api.Endpoints;

/// <summary>
/// Configuración tributaria del gimnasio para la facturación electrónica: emisor, puntos de emisión y certificado de firma. Ver exige <c>invoicing.read</c>; cambiar,
/// <c>invoicing.configure</c> (y acceso a todas las sucursales). Nada de lo que devuelve incluye el archivo del certificado ni su contraseña.
/// </summary>
internal static class InvoicingSettingsEndpoints
{
    public static IEndpointRouteBuilder MapInvoicingSettingsEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/invoicing").WithTags("Facturación electrónica · Configuración");

        group.MapGet("/settings", async (IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.QueryAsync(new GetInvoicingSettingsQuery(), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.InvoicingRead)
            .Produces<InvoicingSettingsDto>()
            .WithName("GetInvoicingSettings");

        group.MapPut("/issuer", async (SaveIssuerRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new SaveIssuerCommand(request), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.InvoicingConfigure)
            .WithValidation<SaveIssuerRequest>()
            .Produces<InvoicingSettingsDto>()
            .WithName("SaveInvoicingIssuer");

        group.MapPost("/emission-points", async (SaveEmissionPointRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new CreateEmissionPointCommand(request), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.InvoicingConfigure)
            .WithValidation<SaveEmissionPointRequest>()
            .Produces<InvoicingSettingsDto>()
            .WithName("CreateEmissionPoint");

        group.MapPut("/emission-points/{id:guid}", async (Guid id, UpdateEmissionPointRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new UpdateEmissionPointCommand(id, request), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.InvoicingConfigure)
            .WithValidation<UpdateEmissionPointRequest>()
            .Produces<InvoicingSettingsDto>()
            .WithName("UpdateEmissionPoint");

        group.MapPatch("/emission-points/{id:guid}/status", async (Guid id, SetEmissionPointStatusRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new SetEmissionPointStatusCommand(id, request.IsActive), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.InvoicingConfigure)
            .Produces<InvoicingSettingsDto>()
            .WithName("SetEmissionPointStatus");

        // Sube el último secuencial usado de un punto (el SRI ya puede tener números de otro sistema o de pruebas anteriores). Nunca baja.
        group.MapPut("/emission-points/{id:guid}/sequence", async (Guid id, SetSequenceRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new SetEmissionPointSequenceCommand(id, request), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.InvoicingConfigure)
            .WithValidation<SetSequenceRequest>()
            .Produces<InvoicingSettingsDto>()
            .WithName("SetEmissionPointSequence");

        // El certificado llega como formulario (archivo + contraseña). Se lee en memoria, se valida, se cifra y se descarta: no se guarda en disco ni en registros.
        group.MapPut("/certificate", async (HttpContext http, IDispatcher dispatcher, CancellationToken ct) =>
            {
                if (!http.Request.HasFormContentType)
                {
                    return InvoicingErrors.CertificateRequired.ToProblem();
                }

                var form = await http.Request.ReadFormAsync(ct);
                var file = form.Files["file"];
                var password = form["password"].ToString();
                if (file is null || file.Length == 0 || string.IsNullOrEmpty(password))
                {
                    return InvoicingErrors.CertificateRequired.ToProblem();
                }

                if (file.Length > InvoicingErrors.MaxCertificateBytes)
                {
                    return InvoicingErrors.CertificateTooLarge.ToProblem();
                }

                await using var stream = file.OpenReadStream();
                using var memory = new MemoryStream((int)file.Length);
                await stream.CopyToAsync(memory, ct);
                return (await dispatcher.SendAsync(new UploadCertificateCommand(memory.ToArray(), password), ct)).ToHttp(r => Results.Ok(r));
            })
            .RequirePermission(Permissions.InvoicingConfigure)
            .DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(InvoicingErrors.MaxCertificateBytes + 16 * 1024))
            .Produces<InvoicingSettingsDto>()
            .WithName("UploadSigningCertificate");

        group.MapDelete("/certificate", async (IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new RetireCertificateCommand(), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.InvoicingConfigure)
            .Produces<InvoicingSettingsDto>()
            .WithName("RetireSigningCertificate");

        return routes;
    }
}
