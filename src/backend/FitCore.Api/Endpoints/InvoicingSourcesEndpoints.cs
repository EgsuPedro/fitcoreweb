using FitCore.Api.Infrastructure;
using FitCore.Application.Core;
using FitCore.Invoicing.Application.Invoices;
using FitCore.Invoicing.Application.Sources;
using FitCore.Invoicing.Domain;
using FitCore.Platform.Domain;

namespace FitCore.Api.Endpoints;

/// <summary>
/// Comprobantes electrónicos y su origen. La vista previa muestra lo que se facturaría con una venta cobrada (comprador resuelto, cálculo del comprobante, punto de emisión y lo que
/// impide emitir) sin guardar nada; emitir crea el comprobante con su número y su clave de acceso. Ambas exigen <c>invoicing.issue</c>; consultar, <c>invoicing.read</c>. Todo respeta el alcance
/// de sucursales de quien consulta.
/// </summary>
internal static class InvoicingSourcesEndpoints
{
    public static IEndpointRouteBuilder MapInvoicingSourcesEndpoints(this IEndpointRouteBuilder routes)
    {
        var sources = routes.MapGroup("/invoicing/sources").WithTags("Facturación electrónica · Orígenes");

        sources.MapGet("/obligations/{id:guid}/preview", async (Guid id, SriBuyerIdType? buyerIdType, Guid? emissionPointId, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.QueryAsync(new PreviewInvoiceSourceQuery(InvoiceSourceTypes.Obligation, id, buyerIdType, emissionPointId), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.InvoicingIssue)
            .Produces<InvoicePreviewDto>()
            .WithName("PreviewObligationInvoice");

        var invoices = routes.MapGroup("/invoicing/invoices").WithTags("Facturación electrónica · Comprobantes");

        // Idempotente por venta: facturar dos veces la misma venta devuelve el mismo comprobante (200) en lugar de crear otro (201).
        invoices.MapPost("/", async (IssueInvoiceRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new IssueInvoiceCommand(request), ct)).ToHttp(r =>
                    r.Created ? Results.Created($"/api/v1/invoicing/invoices/{r.Invoice.Id}", r.Invoice) : Results.Ok(r.Invoice)))
            .RequirePermission(Permissions.InvoicingIssue)
            .WithValidation<IssueInvoiceRequest>()
            .Produces<InvoiceDto>(StatusCodes.Status201Created)
            .Produces<InvoiceDto>()
            .WithName("IssueInvoice");

        invoices.MapGet("/{id:guid}", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.QueryAsync(new GetInvoiceQuery(id), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.InvoicingRead)
            .Produces<InvoiceDto>()
            .WithName("GetInvoice");

        // Firma el comprobante (XML + XAdES-BES). Idempotente: si ya está firmado devuelve el mismo comprobante. Se exige el permiso de envío; el flujo automático llegará con el Worker.
        invoices.MapPost("/{id:guid}/sign", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new SignInvoiceCommand(id), ct)).ToHttp(r => Results.Ok(r.Invoice)))
            .RequirePermission(Permissions.InvoicingSend)
            .Produces<InvoiceDto>()
            .WithName("SignInvoice");

        // El XML contiene datos personales: se entrega solo con permiso, sin enlaces públicos y dejando constancia en el historial del comprobante.
        invoices.MapGet("/{id:guid}/xml", async (Guid id, bool? signed, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new DownloadInvoiceXmlCommand(id, signed ?? true), ct)).ToHttp(f =>
                    Results.File(System.Text.Encoding.UTF8.GetBytes(f.Content), "application/xml", f.FileName)))
            .RequirePermission(Permissions.InvoicingDownload)
            .Produces(StatusCodes.Status200OK, contentType: "application/xml")
            .WithName("DownloadInvoiceXml");

        invoices.MapGet("/", async (int? page, int? pageSize, InvoiceStatus? status, string? q, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.QueryAsync(new ListInvoicesQuery(page ?? 1, pageSize ?? 20, status, q), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.InvoicingRead)
            .Produces<PagedResult<InvoiceListItemDto>>()
            .WithName("ListInvoices");

        return routes;
    }
}
