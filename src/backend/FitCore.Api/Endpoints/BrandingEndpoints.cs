using FitCore.Api.Infrastructure;
using FitCore.Application.Core;
using FitCore.Platform.Application.Branding;
using FitCore.Platform.Domain;
using FitCore.SharedKernel;

namespace FitCore.Api.Endpoints;

/// <summary>
/// Marca del gimnasio (su logo), que aparece en sus reportes y exportaciones y, más adelante, en sus comprobantes. Cada gimnasio cambia solo el suyo
/// (<c>branding.manage</c>); la plataforma puede cargarlo al dar de alta un gimnasio o por soporte. Solo PNG o JPEG de hasta 1 MB, sin metadatos.
/// </summary>
internal static class BrandingEndpoints
{
    private const long RequestLimit = LogoImage.MaxBytes + 256 * 1024;

    public static IEndpointRouteBuilder MapBrandingEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/tenant/branding").WithTags("Marca del gimnasio");

        group.MapGet("/", async (IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.QueryAsync(new GetBrandingQuery(), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.OrganizationRead)
            .Produces<BrandingDto>()
            .WithName("GetTenantBranding");

        // El logo no es un dato sensible, pero sí del gimnasio: exige el permiso de ver los datos del gimnasio y entrega solo el del gimnasio del token. La versión (hash) permite que el
        // navegador lo reutilice; cambiarlo cambia la versión.
        group.MapGet("/logo", async (HttpContext http, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.QueryAsync(new GetLogoQuery(), ct)).ToHttp(file =>
                {
                    http.Response.Headers.CacheControl = "private, max-age=3600";
                    http.Response.Headers.XContentTypeOptions = "nosniff";
                    http.Response.Headers.ETag = $"\"{file.Version}\"";
                    return Results.Stream(file.Content, file.ContentType);
                }))
            .RequirePermission(Permissions.OrganizationRead)
            .Produces(StatusCodes.Status200OK, contentType: "image/png")
            .WithName("GetTenantLogo");

        group.MapPut("/logo", async (HttpContext http, IDispatcher dispatcher, CancellationToken ct) =>
            {
                return await ReadFileAsync(http, ct) is { } content
                    ? (await dispatcher.SendAsync(new SetOwnLogoCommand(content), ct)).ToHttp(r => Results.Ok(r))
                    : LogoImage.Required.ToProblem();
            })
            .RequirePermission(Permissions.BrandingManage)
            .DisableAntiforgery()
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(RequestLimit))
            .Produces<BrandingDto>()
            .WithName("SetTenantLogo");

        group.MapDelete("/logo", async (IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new RemoveOwnLogoCommand(), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.BrandingManage)
            .Produces<BrandingDto>()
            .WithName("RemoveTenantLogo");

        routes.MapPut("/platform/tenants/{id:guid}/logo", async (Guid id, HttpContext http, IDispatcher dispatcher, CancellationToken ct) =>
            {
                return await ReadFileAsync(http, ct) is { } content
                    ? (await dispatcher.SendAsync(new SetTenantLogoCommand(id, content), ct)).ToHttp(r => Results.Ok(r))
                    : LogoImage.Required.ToProblem();
            })
            .RequirePermission(Permissions.PlatformTenantsManage)
            .DisableAntiforgery()
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(RequestLimit))
            .Produces<BrandingDto>()
            .WithTags("Platform · Gimnasios")
            .WithName("SetPlatformTenantLogo");

        return routes;
    }

    /// <summary>Lee el archivo del formulario (campo <c>file</c>) hasta el máximo permitido más uno: uno más grande se rechaza sin cargarse entero.</summary>
    private static async Task<byte[]?> ReadFileAsync(HttpContext http, CancellationToken ct)
    {
        if (!http.Request.HasFormContentType)
        {
            return null;
        }

        IFormCollection form;
        try
        {
            form = await http.Request.ReadFormAsync(ct);
        }
        catch (InvalidDataException)
        {
            return null; // formulario vacío o mal formado: es lo mismo que no haber elegido archivo
        }

        if (form.Files.GetFile("file") is not { Length: > 0 } file)
        {
            return null;
        }

        await using var stream = file.OpenReadStream();
        var buffer = new byte[LogoImage.MaxBytes + 1];
        var total = 0;
        int read;
        while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total), ct)) > 0)
        {
            total += read;
        }

        return buffer.AsSpan(0, total).ToArray();
    }
}
