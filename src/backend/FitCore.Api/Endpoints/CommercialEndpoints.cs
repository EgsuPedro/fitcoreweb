using FitCore.Api.Infrastructure;
using FitCore.Application.Core;
using FitCore.Commercial.Application.Billing;
using FitCore.Commercial.Application.Members;
using FitCore.Commercial.Application.Memberships;
using FitCore.Commercial.Application.Plans;
using FitCore.Commercial.Application.Promotions;
using FitCore.Commercial.Application.Settings;
using FitCore.Commercial.Domain;
using FitCore.Platform.Domain;

namespace FitCore.Api.Endpoints;

internal static class CommercialEndpoints
{
    public static IEndpointRouteBuilder MapCommercialEndpoints(this IEndpointRouteBuilder routes)
    {
        MapMembers(routes.MapGroup("/members").WithTags("Members"));
        MapPlans(routes.MapGroup("/membership-plans").WithTags("Membership plans"));
        MapPromotions(routes.MapGroup("/promotions").WithTags("Promotions"));
        MapMemberships(routes.MapGroup("/memberships").WithTags("Memberships"));
        MapObligations(routes.MapGroup("/payment-obligations").WithTags("Payment obligations"));
        MapSettingsAndTaxes(routes);
        return routes;
    }

    private static void MapMembers(RouteGroupBuilder group)
    {
        group.MapGet("/", async (IDispatcher dispatcher, CancellationToken ct, string? search = null, MemberStatus? status = null, Guid? branchId = null, int page = 1, int pageSize = 25) =>
                (await dispatcher.QueryAsync(new ListMembersQuery(new PageRequest(page, pageSize), search, status, branchId), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.MembersRead)
            .Produces<PagedResult<MemberDto>>()
            .WithName("ListMembers");

        group.MapGet("/{id:guid}", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.QueryAsync(new GetMemberQuery(id), ct)).ToHttp(m => Results.Ok(m)))
            .RequirePermission(Permissions.MembersRead)
            .Produces<MemberDto>()
            .WithName("GetMember");

        group.MapPost("/", async (CreateMemberRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new CreateMemberCommand(request), ct)).ToHttp(m => Results.Created($"/api/v1/members/{m.Id}", m)))
            .RequirePermission(Permissions.MembersCreate)
            .WithValidation<CreateMemberRequest>()
            .Produces<MemberDto>(StatusCodes.Status201Created)
            .WithName("CreateMember");

        group.MapPut("/{id:guid}", async (Guid id, UpdateMemberRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new UpdateMemberCommand(id, request), ct)).ToHttp(m => Results.Ok(m)))
            .RequirePermission(Permissions.MembersUpdate)
            .WithValidation<UpdateMemberRequest>()
            .Produces<MemberDto>()
            .WithName("UpdateMember");

        group.MapPatch("/{id:guid}/status", async (Guid id, SetMemberStatusRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new SetMemberStatusCommand(id, request), ct)).ToHttp(m => Results.Ok(m)))
            .RequirePermission(Permissions.MembersStatus)
            .Produces<MemberDto>()
            .WithName("SetMemberStatus");
    }

    private static void MapPlans(RouteGroupBuilder group)
    {
        group.MapGet("/", async (IDispatcher dispatcher, CancellationToken ct, string? search = null, bool? isActive = null, Guid? branchId = null, int page = 1, int pageSize = 25) =>
                (await dispatcher.QueryAsync(new ListPlansQuery(new PageRequest(page, pageSize), search, isActive, branchId), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.PlansRead)
            .Produces<PagedResult<PlanDto>>()
            .WithName("ListMembershipPlans");

        group.MapGet("/{id:guid}", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.QueryAsync(new GetPlanQuery(id), ct)).ToHttp(p => Results.Ok(p)))
            .RequirePermission(Permissions.PlansRead)
            .Produces<PlanDto>()
            .WithName("GetMembershipPlan");

        group.MapPost("/", async (PlanRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new CreatePlanCommand(request), ct)).ToHttp(p => Results.Created($"/api/v1/membership-plans/{p.Id}", p)))
            .RequirePermission(Permissions.PlansManage)
            .WithValidation<PlanRequest>()
            .Produces<PlanDto>(StatusCodes.Status201Created)
            .WithName("CreateMembershipPlan");

        group.MapPut("/{id:guid}", async (Guid id, PlanRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new UpdatePlanCommand(id, request), ct)).ToHttp(p => Results.Ok(p)))
            .RequirePermission(Permissions.PlansManage)
            .WithValidation<PlanRequest>()
            .Produces<PlanDto>()
            .WithName("UpdateMembershipPlan");
    }

    private static void MapPromotions(RouteGroupBuilder group)
    {
        group.MapGet("/", async (IDispatcher dispatcher, CancellationToken ct, string? search = null, PromotionStatus? status = null, Guid? planId = null, int page = 1, int pageSize = 25) =>
                (await dispatcher.QueryAsync(new ListPromotionsQuery(new PageRequest(page, pageSize), search, status, planId), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.PromotionsRead)
            .Produces<PagedResult<PromotionDto>>()
            .WithName("ListPromotions");

        group.MapGet("/{id:guid}", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.QueryAsync(new GetPromotionQuery(id), ct)).ToHttp(p => Results.Ok(p)))
            .RequirePermission(Permissions.PromotionsRead)
            .Produces<PromotionDto>()
            .WithName("GetPromotion");

        group.MapPost("/", async (PromotionRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new CreatePromotionCommand(request), ct)).ToHttp(p => Results.Created($"/api/v1/promotions/{p.Id}", p)))
            .RequirePermission(Permissions.PromotionsManage)
            .WithValidation<PromotionRequest>()
            .Produces<PromotionDto>(StatusCodes.Status201Created)
            .WithName("CreatePromotion");

        group.MapPut("/{id:guid}", async (Guid id, PromotionRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new UpdatePromotionCommand(id, request), ct)).ToHttp(p => Results.Ok(p)))
            .RequirePermission(Permissions.PromotionsManage)
            .WithValidation<PromotionRequest>()
            .Produces<PromotionDto>()
            .WithName("UpdatePromotion");
    }

    private static void MapMemberships(RouteGroupBuilder group)
    {
        group.MapGet("/", async (IDispatcher dispatcher, CancellationToken ct, Guid? memberId = null, Guid? branchId = null, Guid? planId = null, MembershipStatus? status = null, string? search = null, int page = 1, int pageSize = 25) =>
                (await dispatcher.QueryAsync(new ListMembershipsQuery(new PageRequest(page, pageSize), memberId, branchId, planId, status, search), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.MembershipsRead)
            .Produces<PagedResult<MembershipDto>>()
            .WithName("ListMemberships");

        // Próximas expiraciones: membresías activas que terminan dentro de los próximos N días.
        group.MapGet("/expiring", async (IDispatcher dispatcher, CancellationToken ct, int days = 7, Guid? branchId = null, int page = 1, int pageSize = 25) =>
                (await dispatcher.QueryAsync(new ExpiringMembershipsQuery(new PageRequest(page, pageSize), days, branchId), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.MembershipsRead)
            .Produces<PagedResult<MembershipDto>>()
            .WithName("ListExpiringMemberships");

        group.MapGet("/{id:guid}", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.QueryAsync(new GetMembershipQuery(id), ct)).ToHttp(m => Results.Ok(m)))
            .RequirePermission(Permissions.MembershipsRead)
            .Produces<MembershipDto>()
            .WithName("GetMembership");

        group.MapGet("/{id:guid}/events", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.QueryAsync(new ListMembershipEventsQuery(id), ct)).ToHttp(e => Results.Ok(e)))
            .RequirePermission(Permissions.MembershipsRead)
            .Produces<IReadOnlyList<MembershipEventDto>>()
            .WithName("ListMembershipEvents");

        group.MapPost("/", async (EnrollRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new EnrollCommand(request), ct)).ToHttp(m => Results.Created($"/api/v1/memberships/{m.Id}", m)))
            .RequirePermission(Permissions.MembershipsCreate)
            .WithValidation<EnrollRequest>()
            .Produces<MembershipDto>(StatusCodes.Status201Created)
            .WithName("EnrollMember");

        group.MapPost("/{id:guid}/renew", async (Guid id, RenewRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new RenewCommand(id, request), ct)).ToHttp(m => Results.Created($"/api/v1/memberships/{m.Id}", m)))
            .RequirePermission(Permissions.MembershipsRenew)
            .WithValidation<RenewRequest>()
            .Produces<MembershipDto>(StatusCodes.Status201Created)
            .WithName("RenewMembership");

        group.MapPost("/{id:guid}/freeze", async (Guid id, FreezeRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new FreezeCommand(id, request), ct)).ToHttp(m => Results.Ok(m)))
            .RequirePermission(Permissions.MembershipsFreeze)
            .WithValidation<FreezeRequest>()
            .Produces<MembershipDto>()
            .WithName("FreezeMembership");

        group.MapPost("/{id:guid}/reactivate", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new ReactivateCommand(id), ct)).ToHttp(m => Results.Ok(m)))
            .RequirePermission(Permissions.MembershipsFreeze)
            .Produces<MembershipDto>()
            .WithName("ReactivateMembership");

        group.MapPost("/{id:guid}/cancel", async (Guid id, CancelRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new CancelCommand(id, request), ct)).ToHttp(m => Results.Ok(m)))
            .RequirePermission(Permissions.MembershipsCancel)
            .WithValidation<CancelRequest>()
            .Produces<MembershipDto>()
            .WithName("CancelMembership");
    }

    private static void MapObligations(RouteGroupBuilder group)
    {
        group.MapGet("/", async (IDispatcher dispatcher, CancellationToken ct, Guid? memberId = null, Guid? membershipId = null, Guid? branchId = null, ObligationStatus? status = null, string? search = null, int page = 1, int pageSize = 25) =>
                (await dispatcher.QueryAsync(new ListObligationsQuery(new PageRequest(page, pageSize), memberId, membershipId, branchId, status, search), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.ObligationsRead)
            .Produces<PagedResult<ObligationDto>>()
            .WithName("ListPaymentObligations");

        group.MapGet("/{id:guid}", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.QueryAsync(new GetObligationQuery(id), ct)).ToHttp(o => Results.Ok(o)))
            .RequirePermission(Permissions.ObligationsRead)
            .Produces<ObligationDto>()
            .WithName("GetPaymentObligation");
    }
    private static void MapSettingsAndTaxes(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/commercial-settings", async (IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.QueryAsync(new GetCommercialSettingsQuery(), ct)).ToHttp(s => Results.Ok(s)))
            .RequirePermission(Permissions.PlansRead)
            .Produces<CommercialSettingsDto>()
            .WithTags("Commercial settings")
            .WithName("GetCommercialSettings");

        routes.MapPut("/commercial-settings", async (UpdateCommercialSettingsRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new UpdateCommercialSettingsCommand(request), ct)).ToHttp(s => Results.Ok(s)))
            .RequirePermission(Permissions.CommercialSettingsManage)
            .WithValidation<UpdateCommercialSettingsRequest>()
            .Produces<CommercialSettingsDto>()
            .WithTags("Commercial settings")
            .WithName("UpdateCommercialSettings");

        // Tarifas vigentes hoy, para que los gimnasios muestren el desglose de IVA de sus planes.
        routes.MapGet("/tax-rates/current", async (IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.QueryAsync(new GetCurrentTaxRatesQuery(), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.PlansRead)
            .Produces<IReadOnlyList<TaxRateDto>>()
            .WithTags("Tax rates")
            .WithName("GetCurrentTaxRates");

        // Administración global de tarifas por decreto: solo el equipo de plataforma.
        var platform = routes.MapGroup("/platform/tax-rates").WithTags("Platform");
        platform.MapGet("/", async (IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.QueryAsync(new ListTaxRatesQuery(), ct)).ToHttp(r => Results.Ok(r)))
            .RequirePermission(Permissions.PlatformTaxesManage)
            .Produces<IReadOnlyList<TaxRateDto>>()
            .WithName("ListTaxRates");

        platform.MapPost("/", async (CreateTaxRateRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.SendAsync(new CreateTaxRateCommand(request), ct)).ToHttp(r => Results.Created($"/api/v1/platform/tax-rates/{r.Id}", r)))
            .RequirePermission(Permissions.PlatformTaxesManage)
            .WithValidation<CreateTaxRateRequest>()
            .Produces<TaxRateDto>(StatusCodes.Status201Created)
            .WithName("CreateTaxRate");
    }
}
