using System.Net;
using System.Net.Http.Json;

namespace FitCore.Api.IntegrationTests.Infrastructure;

public sealed record PlanDto(
    Guid Id,
    string Name,
    decimal Price,
    string Currency,
    int DurationValue,
    string DurationUnit,
    string TaxKind,
    decimal TaxPercent,
    decimal Subtotal,
    decimal TaxAmount,
    bool IsActive,
    bool AllowFreeze,
    int MaxFreezeDays,
    Guid[] BranchIds,
    Guid? CreatedBy,
    Guid? UpdatedBy);

public sealed record PromotionDto(
    Guid Id,
    string Name,
    string? Code,
    string DiscountType,
    decimal Value,
    DateOnly ValidFrom,
    DateOnly ValidUntil,
    int? MaxUses,
    int UsedCount,
    bool IsActive,
    string Status,
    Guid[] PlanIds);

public sealed record SettingsDto(string AccessPolicy, int MinPaymentPercent, int DueDays, int MaxDiscountPercent);

public sealed record TaxRateDto(Guid Id, string Kind, decimal Percent, DateOnly ValidFrom, DateOnly? ValidUntil, bool IsCurrent);

public sealed record ObligationDto(
    Guid Id,
    string Reference,
    Guid BranchId,
    Guid MemberId,
    Guid? MembershipId,
    string Concept,
    DateOnly IssueDate,
    DateOnly DueDate,
    decimal OriginalAmount,
    decimal DiscountAmount,
    decimal PayableAmount,
    decimal Subtotal,
    decimal TaxAmount,
    decimal TaxPercent,
    string Currency,
    string Status,
    string? VoidReason);

public sealed record MembershipDto(
    Guid Id,
    Guid MemberId,
    string MemberCode,
    string MemberName,
    Guid BranchId,
    Guid PlanId,
    string PlanName,
    decimal PlanPrice,
    decimal TaxPercent,
    bool AllowFreeze,
    int MaxFreezeDays,
    int FrozenDaysUsed,
    int RemainingFreezeDays,
    string? PromotionCode,
    decimal DiscountAmount,
    decimal AgreedPrice,
    DateOnly StartDate,
    DateOnly EndDate,
    string Status,
    DateOnly? FreezeStartedOn,
    string? CancelReason,
    Guid? RenewedFromId,
    ObligationDto? Obligation,
    Guid? CreatedBy,
    Guid? UpdatedBy);

public sealed record MembershipEventDto(Guid Id, string Type, DateTimeOffset OccurredAt, Guid? ActorId, string? Details);

/// <summary>Utilidades para pruebas del módulo comercial.</summary>
public static class CommercialTestApi
{
    public static async Task<Guid> CreateRoleAsync(HttpClient owner, string name, bool allBranches, params string[] permissions)
    {
        var response = await owner.PostAsJsonAsync("/api/v1/roles", new { name, description = $"Rol {name}", grantsAllBranches = allBranches, permissions });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<RoleDto>())!.Id;
    }

    /// <summary>Usuario con un rol propio (permisos exactos) y las sucursales indicadas, ya autenticado.</summary>
    public static async Task<HttpClient> ClientWithRoleAsync(
        ApiFixture api,
        TestTenant tenant,
        string email,
        bool allBranches,
        string[] permissions,
        params Guid[] branchIds)
    {
        var roleName = $"Rol-{email.Split('@')[0]}";
        await CreateRoleAsync(tenant.Owner, roleName, allBranches, permissions);
        await TestApi.CreateUserAsync(tenant.Owner, email, roleName, branchIds);
        return TestApi.Authenticated(api, await TestApi.LoginAsync(api, tenant.Slug, email, TestApi.UserPassword));
    }

    public static object PlanBody(
        string name,
        decimal price = 30m,
        int durationValue = 1,
        string durationUnit = "Month",
        string taxKind = "General",
        bool isActive = true,
        bool allowFreeze = false,
        int maxFreezeDays = 0,
        Guid[]? branchIds = null,
        string? validFrom = null,
        string? validUntil = null) => new
        {
            name,
            description = "Plan de prueba",
            price,
            durationValue,
            durationUnit,
            allowedAccess = "Sala de pesas",
            restrictions = "Solo horario regular",
            commercialTerms = "Sin devoluciones",
            taxKind,
            isActive,
            validFrom,
            validUntil,
            allowFreeze,
            maxFreezeDays,
            branchIds = branchIds ?? []
        };

    public static async Task<PlanDto> CreatePlanAsync(HttpClient client, string name, decimal price = 30m, Guid[]? branchIds = null, string durationUnit = "Month", int durationValue = 1)
    {
        var response = await client.PostAsJsonAsync("/api/v1/membership-plans", PlanBody(name, price, durationValue, durationUnit, branchIds: branchIds));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PlanDto>())!;
    }

    public static object PromotionBody(
        string name,
        string discountType = "Percentage",
        decimal value = 10m,
        string? code = null,
        DateOnly? validFrom = null,
        DateOnly? validUntil = null,
        int? maxUses = null,
        bool isActive = true,
        Guid[]? planIds = null) => new
        {
            name,
            code,
            discountType,
            value,
            validFrom = validFrom ?? DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)),
            validUntil = validUntil ?? DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)),
            maxUses,
            restrictions = "No acumulable",
            isActive,
            planIds = planIds ?? []
        };

    public static async Task<PromotionDto> CreatePromotionAsync(HttpClient client, string name, string? code = null, decimal value = 10m, Guid[]? planIds = null)
    {
        var response = await client.PostAsJsonAsync("/api/v1/promotions", PromotionBody(name, code: code, value: value, planIds: planIds));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PromotionDto>())!;
    }
}
