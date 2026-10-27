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
    decimal PaidAmount,
    decimal ReservedAmount,
    decimal Balance,
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

public sealed record PaymentLineDto(Guid ObligationId, string ObligationReference, string Concept, decimal Amount);

public sealed record PaymentDto(
    Guid Id,
    string ReceiptNumber,
    Guid BranchId,
    Guid MemberId,
    string MemberName,
    string Method,
    string? MethodDetail,
    decimal Amount,
    string Status,
    string? Reference,
    DateTimeOffset PaidAt,
    Guid? CashSessionId,
    PaymentLineDto[] Allocations,
    DateTimeOffset? ConfirmedAt,
    Guid? ConfirmedBy,
    DateTimeOffset? ReversedAt,
    string? ReversalReason,
    Guid? CreatedBy);

public sealed record ReceiptLineDto(string ObligationReference, string Concept, decimal Amount, decimal ObligationPayable, decimal ObligationBalance);

public sealed record ReceiptDto(
    string ReceiptNumber,
    string Notice,
    string Status,
    string BranchCode,
    string MemberName,
    string Method,
    decimal Total,
    string? ReceivedBy,
    ReceiptLineDto[] Lines);
public sealed record CashSessionDto(
    Guid Id,
    Guid RegisterId,
    string RegisterName,
    Guid BranchId,
    string Status,
    Guid OpenedBy,
    string? OpenedByName,
    DateTimeOffset OpenedAt,
    decimal OpeningAmount,
    decimal TotalIn,
    decimal TotalOut,
    decimal ExpectedAmount,
    decimal? CountedAmount,
    decimal? Difference,
    Guid? ClosedBy,
    string? ClosedByName,
    DateTimeOffset? ClosedAt,
    string? OpenNotes,
    string? CloseNotes,
    int MovementCount);

public sealed record CashRegisterDto(
    Guid Id,
    Guid BranchId,
    string BranchCode,
    string BranchName,
    string Name,
    bool IsActive,
    bool AllowMultipleOpenSessions,
    CashSessionDto[] OpenSessions);

public sealed record CashMovementDto(
    Guid Id,
    Guid SessionId,
    string Type,
    string Direction,
    decimal Amount,
    string? Reason,
    Guid? PaymentId,
    string? ReceiptNumber,
    DateTimeOffset CreatedAt,
    Guid? CreatedBy,
    string? CreatedByName);

public sealed record AgingBucketDto(string Label, int Count, decimal Amount);

public sealed record ReceivablesBranchDto(Guid BranchId, string BranchCode, string BranchName, decimal Outstanding, decimal Overdue, int OpenCount, int OverdueCount);

public sealed record DebtorDto(Guid MemberId, string MemberCode, string MemberName, string? Phone, decimal Outstanding, decimal Overdue, DateOnly OldestDueDate, int OpenObligations);

public sealed record ReceivablesSummaryDto(
    string Currency,
    DateOnly AsOf,
    decimal Outstanding,
    decimal Overdue,
    int OpenCount,
    int OverdueCount,
    AgingBucketDto[] Aging,
    ReceivablesBranchDto[] ByBranch,
    DebtorDto[] TopDebtors);
public sealed record AttendanceDto(
    Guid Id,
    Guid BranchId,
    string BranchName,
    Guid MemberId,
    string MemberCode,
    string MemberName,
    Guid? MembershipId,
    string? PlanName,
    DateTimeOffset CheckInAt,
    DateTimeOffset? CheckOutAt,
    int? DurationMinutes,
    string Method,
    string? CheckOutMethod,
    bool IsException,
    string? ExceptionReason,
    Guid RegisteredBy,
    string? RegisteredByName,
    Guid? CheckOutBy,
    bool IsOpen);

public sealed record AccessDecisionDto(
    bool Allowed,
    string? Code,
    string? Message,
    bool Overridable,
    bool AlreadyInside,
    Guid MemberId,
    string MemberCode,
    string MemberName,
    string MemberStatus,
    Guid? MembershipId,
    string? PlanName,
    DateOnly? MembershipEnd,
    decimal OwedAmount,
    string Policy);

public sealed record QrTokenDto(string Token, DateTimeOffset IssuedAt);
public sealed record MembershipEventDto(Guid Id, string Type, DateTimeOffset OccurredAt, Guid? ActorId, string? Details);

/// <summary>Utilidades para pruebas del módulo comercial.</summary>
public static class CommercialTestApi
{
    /// <summary>Registra un pago con su <c>Idempotency-Key</c>. Sin clave explícita se usa una nueva (cada llamada es un pago distinto).</summary>
    public static async Task<HttpResponseMessage> PostPaymentAsync(HttpClient client, object body, string? key = null, bool sendKey = true)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/payments") { Content = JsonContent.Create(body) };
        if (sendKey)
        {
            request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString());
        }

        return await client.SendAsync(request);
    }

    public static async Task<HttpResponseMessage> PostWithKeyAsync(HttpClient client, string url, object? body, string? key = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString());
        return await client.SendAsync(request);
    }

    public static object PaymentBody(
        Guid memberId,
        decimal amount,
        string method = "Card",
        string? reference = "VCH-0001",
        object[]? allocations = null,
        string? methodDetail = null,
        DateTimeOffset? paidAt = null) => new
        {
            memberId,
            method,
            amount,
            reference,
            bankName = method == "BankTransfer" ? "Banco Pichincha" : null,
            methodDetail,
            paidAt,
            notes = "Pago de prueba",
            allocations
        };

    public static async Task<PaymentDto> PayAsync(HttpClient client, Guid memberId, decimal amount, string method = "Card", string? reference = "VCH-0001", object[]? allocations = null)
    {
        var response = await PostPaymentAsync(client, PaymentBody(memberId, amount, method, reference, allocations));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PaymentDto>())!;
    }

    public static async Task<CashRegisterDto> CreateRegisterAsync(HttpClient owner, Guid branchId, string name = "Caja 1", bool allowMultiple = false)
    {
        var response = await owner.PostAsJsonAsync("/api/v1/cash-registers", new { branchId, name, allowMultipleOpenSessions = allowMultiple });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CashRegisterDto>())!;
    }

    public static Task<HttpResponseMessage> OpenCashAsync(HttpClient client, Guid registerId, decimal opening = 50m, string? key = null) =>
        PostWithKeyAsync(client, $"/api/v1/cash-registers/{registerId}/open", new { openingAmount = opening, notes = "Apertura de prueba" }, key);

    public static async Task<CashSessionDto> OpenOkAsync(HttpClient client, Guid registerId, decimal opening = 50m)
    {
        var response = await OpenCashAsync(client, registerId, opening);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CashSessionDto>())!;
    }

    public static Task<HttpResponseMessage> CloseCashAsync(HttpClient client, Guid registerId, decimal counted, string? notes = null, Guid? sessionId = null, string? key = null) =>
        PostWithKeyAsync(client, $"/api/v1/cash-registers/{registerId}/close", new { sessionId, countedAmount = counted, notes }, key);

    public static Task<HttpResponseMessage> CashMovementAsync(HttpClient client, Guid registerId, string type, decimal amount, string reason = "Motivo de prueba", string? key = null) =>
        PostWithKeyAsync(client, $"/api/v1/cash-registers/{registerId}/movements", new { type, amount, reason }, key);

    public static Task<HttpResponseMessage> CheckInAsync(HttpClient client, Guid branchId, Guid? memberId = null, string? memberCode = null, string? qrToken = null, string? overrideReason = null) =>
        client.PostAsJsonAsync("/api/v1/attendance/check-in", new { branchId, memberId, memberCode, qrToken, overrideReason });

    public static Task<HttpResponseMessage> CheckOutAsync(HttpClient client, Guid? memberId = null, string? memberCode = null, string? qrToken = null) =>
        client.PostAsJsonAsync("/api/v1/attendance/check-out", new { memberId, memberCode, qrToken });

    public static Task<HttpResponseMessage> ValidateAccessAsync(HttpClient client, Guid branchId, Guid? memberId = null, string? memberCode = null, string? qrToken = null) =>
        client.PostAsJsonAsync("/api/v1/attendance/validate-access", new { branchId, memberId, memberCode, qrToken });

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
