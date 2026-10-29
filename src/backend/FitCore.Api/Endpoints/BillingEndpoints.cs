using FitCore.Api.Infrastructure;
using FitCore.Application.Core;
using FitCore.Platform.Domain;
using FitCore.Subscriptions.Application.Billing;
using Microsoft.AspNetCore.Mvc;

namespace FitCore.Api.Endpoints;

/// <summary>
/// Cobros comerciales de FitCore 360 (lo que la plataforma le cobra a un gimnasio por su suscripción). Dominio aparte de los pagos que un gimnasio
/// recibe de sus alumnos. Los documentos son internos: no son comprobantes tributarios. Las operaciones que mueven dinero exigen <c>Idempotency-Key</c>.
/// </summary>
internal static class BillingEndpoints
{
    public static IEndpointRouteBuilder MapBillingEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/platform/tenants/{id:guid}/subscription/invoices", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new IssueInvoiceCommand(id), ct)).ToHttp(r => Results.Created($"/api/v1/platform/billing/invoices/{r.Id}", r)))
            .WithTags("Platform · Cobros comerciales")
            .RequirePermission(Permissions.PlatformBillingManage)
            .Produces<InvoiceDto>(StatusCodes.Status201Created)
            .WithName("IssueSubscriptionInvoice");

        var group = routes.MapGroup("/platform/billing").WithTags("Platform · Cobros comerciales");

        group.MapGet("/invoices", async (
                IDispatcher dispatcher, CancellationToken ct, string? status = null, Guid? tenantId = null, string? search = null,
                DateOnly? from = null, DateOnly? to = null, bool overdue = false, int page = 1, int pageSize = 25) =>
                (await dispatcher.QueryAsync(new ListInvoicesQuery(new PageRequest(page, pageSize), status, tenantId, search, from, to, overdue), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.PlatformBillingRead)
            .Produces<PagedResult<InvoiceSummaryDto>>()
            .WithName("ListSubscriptionInvoices");

        group.MapGet("/invoices/{id:guid}", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.QueryAsync(new GetInvoiceQuery(id), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.PlatformBillingRead)
            .Produces<InvoiceDto>()
            .WithName("GetSubscriptionInvoice");

        group.MapPost("/invoices/{id:guid}/pay", async (Guid id, PayInvoiceRequest request, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new PayInvoiceCommand(id, request, idempotencyKey), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.PlatformBillingManage)
            .WithValidation<PayInvoiceRequest>()
            .Produces<InvoiceDto>()
            .WithName("PaySubscriptionInvoice");

        group.MapPost("/invoices/{id:guid}/attempts", async (Guid id, RecordFailedAttemptRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new RecordFailedAttemptCommand(id, request), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.PlatformBillingManage)
            .WithValidation<RecordFailedAttemptRequest>()
            .Produces<InvoiceDto>()
            .WithName("RecordFailedSubscriptionPaymentAttempt");

        group.MapPost("/invoices/{id:guid}/void", async (Guid id, VoidInvoiceRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new VoidInvoiceCommand(id, request), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.PlatformBillingManage)
            .WithValidation<VoidInvoiceRequest>()
            .Produces<InvoiceDto>()
            .WithName("VoidSubscriptionInvoice");

        group.MapGet("/payments", async (IDispatcher dispatcher, CancellationToken ct, Guid? tenantId = null, DateOnly? from = null, DateOnly? to = null, int page = 1, int pageSize = 25) =>
                (await dispatcher.QueryAsync(new ListSubscriptionPaymentsQuery(new PageRequest(page, pageSize), tenantId, from, to), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.PlatformBillingRead)
            .Produces<PagedResult<SubscriptionPaymentDto>>()
            .WithName("ListSubscriptionPayments");

        group.MapPost("/payments/{id:guid}/refund", async (Guid id, RefundPaymentRequest request, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new RefundPaymentCommand(id, request, idempotencyKey), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.PlatformBillingManage)
            .WithValidation<RefundPaymentRequest>()
            .Produces<InvoiceDto>()
            .WithName("RefundSubscriptionPayment");

        group.MapGet("/summary", async (IDispatcher dispatcher, CancellationToken ct, DateOnly? from = null, DateOnly? to = null) =>
                (await dispatcher.QueryAsync(new GetBillingSummaryQuery(from, to), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.PlatformBillingRead)
            .Produces<BillingSummaryDto>()
            .WithName("GetSubscriptionBillingSummary");

        return routes;
    }
}
