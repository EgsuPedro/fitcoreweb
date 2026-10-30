using FitCore.Api.Infrastructure;
using FitCore.Application.Core;
using FitCore.Invoicing.Application.Sources;
using FitCore.Invoicing.Domain;
using FitCore.Platform.Domain;

namespace FitCore.Api.Endpoints;

/// <summary>
/// Vista previa de lo que se facturaría con una venta ya cobrada: comprador resuelto con los tipos de identificación del SRI, líneas, impuestos congelados, pagos y lo que
/// impide emitir. No guarda nada. Exige <c>invoicing.issue</c> y respeta el alcance de sucursales de quien consulta.
/// </summary>
internal static class InvoicingSourcesEndpoints
{
    public static IEndpointRouteBuilder MapInvoicingSourcesEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/invoicing/sources").WithTags("Facturación electrónica · Orígenes");

        group.MapGet("/obligations/{id:guid}/preview", async (Guid id, SriBuyerIdType? buyerIdType, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.QueryAsync(new PreviewInvoiceSourceQuery(InvoiceSourceTypes.Obligation, id, buyerIdType), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.InvoicingIssue)
            .Produces<InvoicePreviewDto>()
            .WithName("PreviewObligationInvoice");

        return routes;
    }
}
