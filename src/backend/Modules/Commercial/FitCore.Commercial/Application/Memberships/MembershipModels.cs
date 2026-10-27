using System.Text.Json;
using FitCore.Application.Core;
using FitCore.Commercial.Application.Billing;
using FitCore.Commercial.Application.Settings;
using FitCore.Commercial.Domain;
using FitCore.Commercial.Persistence;
using FitCore.Platform.Application;
using FitCore.Platform.Security;
using FitCore.SharedKernel;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace FitCore.Commercial.Application.Memberships;

public sealed record MembershipDto(
    Guid Id,
    Guid MemberId,
    string MemberCode,
    string MemberName,
    Guid BranchId,
    Guid PlanId,
    string PlanName,
    decimal PlanPrice,
    int DurationValue,
    string DurationUnit,
    string TaxKind,
    decimal TaxPercent,
    string? AllowedAccess,
    string? CommercialTerms,
    bool AllowFreeze,
    int MaxFreezeDays,
    int FrozenDaysUsed,
    int RemainingFreezeDays,
    Guid? PromotionId,
    string? PromotionName,
    string? PromotionCode,
    decimal DiscountAmount,
    decimal AgreedPrice,
    DateOnly StartDate,
    DateOnly EndDate,
    string Status,
    DateOnly? FreezeStartedOn,
    string? CancelReason,
    DateTimeOffset? CancelledAt,
    Guid? RenewedFromId,
    ObligationDto? Obligation,
    DateTimeOffset CreatedAt,
    Guid? CreatedBy,
    DateTimeOffset? UpdatedAt,
    Guid? UpdatedBy)
{
    public static MembershipDto From(Membership m, string memberCode, string memberName, ObligationDto? obligation, DateOnly today) =>
        new(
            m.Id,
            m.MemberId,
            memberCode,
            memberName,
            m.BranchId,
            m.PlanId,
            m.PlanName,
            m.PlanPrice,
            m.DurationValue,
            m.DurationUnit.ToString(),
            m.TaxKind.ToString(),
            m.TaxPercent,
            m.AllowedAccess,
            m.CommercialTerms,
            m.AllowFreeze,
            m.MaxFreezeDays,
            m.FrozenDaysUsed,
            m.RemainingFreezeDays,
            m.PromotionId,
            m.PromotionName,
            m.PromotionCode,
            m.DiscountAmount,
            m.AgreedPrice,
            m.StartDate,
            m.EndDate,
            m.EffectiveStatus(today).ToString(),
            m.FreezeStartedOn,
            m.CancelReason,
            m.CancelledAt,
            m.RenewedFromId,
            obligation,
            m.CreatedAt,
            m.CreatedBy,
            m.UpdatedAt,
            m.UpdatedBy);
}

public sealed record MembershipEventDto(Guid Id, string Type, DateTimeOffset OccurredAt, Guid? ActorId, string? Details)
{
    public static MembershipEventDto From(MembershipEvent e) => new(e.Id, e.Type.ToString(), e.OccurredAt, e.ActorId, e.Details);
}

/// <summary>Inscribir a un alumno en un plan. El tenant y la sucursal salen del alumno y del token, nunca del cuerpo.</summary>
public sealed record EnrollRequest(Guid MemberId, Guid PlanId, DateOnly? StartDate, Guid? PromotionId, string? PromotionCode, DateOnly? DueDate);

/// <summary>Renovar: otra membresía que empieza al terminar la actual. Sin <c>PlanId</c> se repite el plan.</summary>
public sealed record RenewRequest(Guid? PlanId, Guid? PromotionId, string? PromotionCode, DateOnly? DueDate);

public sealed record FreezeRequest(string? Reason);

public sealed record CancelRequest(string Reason);

public sealed record ListMembershipsQuery(PageRequest Page, Guid? MemberId, Guid? BranchId, Guid? PlanId, MembershipStatus? Status, string? Search)
    : IQuery<PagedResult<MembershipDto>>;

public sealed record ExpiringMembershipsQuery(PageRequest Page, int Days, Guid? BranchId) : IQuery<PagedResult<MembershipDto>>;

public sealed record GetMembershipQuery(Guid Id) : IQuery<MembershipDto>;

public sealed record ListMembershipEventsQuery(Guid Id) : IQuery<IReadOnlyList<MembershipEventDto>>;

public sealed record EnrollCommand(EnrollRequest Request) : ICommand<MembershipDto>;

public sealed record RenewCommand(Guid Id, RenewRequest Request) : ICommand<MembershipDto>;

public sealed record FreezeCommand(Guid Id, FreezeRequest Request) : ICommand<MembershipDto>;

public sealed record ReactivateCommand(Guid Id) : ICommand<MembershipDto>;

public sealed record CancelCommand(Guid Id, CancelRequest Request) : ICommand<MembershipDto>;

public sealed class EnrollRequestValidator : AbstractValidator<EnrollRequest>
{
    public EnrollRequestValidator()
    {
        RuleFor(x => x.MemberId).NotEmpty();
        RuleFor(x => x.PlanId).NotEmpty();
        RuleFor(x => x.PromotionCode).MaximumLength(30);
        RuleFor(x => x).Must(x => x.PromotionId is null || string.IsNullOrWhiteSpace(x.PromotionCode))
            .WithName("promotionCode").WithMessage("Indique la promoción por identificador o por código, no ambos.");
    }
}

public sealed class RenewRequestValidator : AbstractValidator<RenewRequest>
{
    public RenewRequestValidator()
    {
        RuleFor(x => x.PromotionCode).MaximumLength(30);
        RuleFor(x => x).Must(x => x.PromotionId is null || string.IsNullOrWhiteSpace(x.PromotionCode))
            .WithName("promotionCode").WithMessage("Indique la promoción por identificador o por código, no ambos.");
    }
}

public sealed class FreezeRequestValidator : AbstractValidator<FreezeRequest>
{
    public FreezeRequestValidator() => RuleFor(x => x.Reason).MaximumLength(300);
}

public sealed class CancelRequestValidator : AbstractValidator<CancelRequest>
{
    public CancelRequestValidator() => RuleFor(x => x.Reason).NotEmpty().MinimumLength(5).MaximumLength(300);
}

internal static class MembershipErrorsApp
{
    public static readonly Error NotFound = Error.NotFound("memberships.not_found", "Membresía no encontrada.");
    public static readonly Error MemberNotFound = Error.NotFound("members.not_found", "Alumno no encontrado.");
    public static readonly Error MemberInactive = Error.Validation("members.inactive", "El alumno está inactivo.");
    public static readonly Error PlanNotFound = Error.NotFound("plans.not_found", "Plan no encontrado.");
    public static readonly Error PlanNotSellable = Error.Validation("plans.not_sellable", "El plan no está disponible para la venta en esta fecha o sucursal.");
    public static readonly Error StartInPast = Error.Validation("memberships.start_in_past", "La fecha de inicio no puede ser anterior a hoy.");
    public static readonly Error StartTooFar = Error.Validation("memberships.start_too_far", "La fecha de inicio no puede pasar de un año.");
    public static readonly Error Overlap = Error.Conflict("memberships.overlap", "El alumno ya tiene una membresía vigente o pendiente que coincide con esas fechas.");
    public static readonly Error ZeroAmount = Error.Validation("memberships.zero_amount", "El total a pagar no puede ser cero.");
    public static readonly Error DueInPast = Error.Validation("obligations.due_in_past", "La fecha de vencimiento no puede ser anterior a hoy.");
    public static readonly Error PromotionRequiresPermission = Error.Forbidden("promotions.apply_not_allowed", "No tiene permiso para aplicar promociones.");
    public static readonly Error PromotionNotFound = Error.NotFound("promotions.not_found", "Promoción no encontrada.");
    public static readonly Error PromotionNotAvailable = Error.Validation("promotions.not_available", "La promoción no está vigente.");
    public static readonly Error PromotionNotApplicable = Error.Validation("promotions.not_applicable", "La promoción no aplica a ese plan.");
    public static readonly Error PromotionExhausted = Error.Conflict("promotions.exhausted", "La promoción ya alcanzó su máximo de usos.");
    public static readonly Error PromotionAboveMaximum = Error.Validation("promotions.discount_above_maximum", "El descuento supera el máximo autorizado para el gimnasio.");
    public static readonly Error CannotRenew = Error.Conflict("memberships.cannot_renew", "Una membresía cancelada no se renueva: inscriba al alumno de nuevo.");
    public static readonly Error RenewFrozen = Error.Conflict("memberships.frozen", "Reactive la membresía congelada antes de renovarla.");
    public static readonly Error RenewPending = Error.Conflict("memberships.pending_payment", "La membresía aún no se ha pagado: cobre o cancele antes de renovar.");
    public static readonly Error AlreadyRenewed = Error.Conflict("memberships.already_renewed", "Esta membresía ya fue renovada.");
    public static readonly Error FreezeBlockedByRenewal = Error.Conflict("memberships.freeze_blocked_by_renewal", "No se puede congelar una membresía que ya tiene una renovación programada.");
}

internal static class MembershipQueries
{
    public static IQueryable<Membership> VisibleTo(this IQueryable<Membership> memberships, UserAccess actor) =>
        memberships.InScope(actor, m => m.BranchId);

    /// <summary>Filtro por estado efectivo (el vencido se deriva de la fecha de fin, no se guarda).</summary>
    public static IQueryable<Membership> WithEffectiveStatus(this IQueryable<Membership> memberships, MembershipStatus status, DateOnly today) =>
        status switch
        {
            MembershipStatus.Expired => memberships.Where(m => (m.Status == MembershipStatus.PendingPayment || m.Status == MembershipStatus.Active) && m.EndDate < today),
            MembershipStatus.Active or MembershipStatus.PendingPayment => memberships.Where(m => m.Status == status && m.EndDate >= today),
            _ => memberships.Where(m => m.Status == status)
        };

    /// <summary>Arma las respuestas con el nombre del alumno y la obligación de cada membresía, en dos consultas para toda la página.</summary>
    public static async Task<IReadOnlyList<MembershipDto>> ToDtosAsync(CommercialDbContext db, IReadOnlyList<Membership> memberships, DateOnly today, CancellationToken cancellationToken)
    {
        var memberIds = memberships.Select(m => m.MemberId).Distinct().ToList();
        var membershipIds = memberships.Select(m => m.Id).ToList();
        var members = await db.Members.AsNoTracking().Where(x => memberIds.Contains(x.Id))
            .Select(x => new { x.Id, x.Code, x.FirstName, x.LastName }).ToDictionaryAsync(x => x.Id, cancellationToken);
        var obligations = await db.Obligations.AsNoTracking().Where(o => o.MembershipId != null && membershipIds.Contains(o.MembershipId.Value))
            .OrderBy(o => o.CreatedAt).ToListAsync(cancellationToken);

        return
        [
            .. memberships.Select(m =>
            {
                var member = members[m.MemberId];
                var name = $"{member.FirstName} {member.LastName}";
                var obligation = obligations.LastOrDefault(o => o.MembershipId == m.Id);
                return MembershipDto.From(m, member.Code, name, obligation is null ? null : ObligationDto.From(obligation, member.Code, name, today), today);
            })
        ];
    }

    public static async Task<MembershipDto> ToDtoAsync(CommercialDbContext db, Membership membership, DateOnly today, CancellationToken cancellationToken) =>
        (await ToDtosAsync(db, [membership], today, cancellationToken))[0];

    public static MembershipEvent NewEvent(Membership m, MembershipEventType type, Guid? actor, DateTimeOffset now, object? details = null) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = m.TenantId,
            MembershipId = m.Id,
            Type = type,
            OccurredAt = now,
            ActorId = actor,
            Details = details is null ? null : JsonSerializer.Serialize(details)
        };

    public static async Task<string> BranchTimeZoneAsync(CommercialDbContext db, Guid branchId, CancellationToken cancellationToken) =>
        await db.BranchRefs.AsNoTracking().Where(b => b.Id == branchId).Select(b => b.TimeZone).FirstOrDefaultAsync(cancellationToken)
        ?? FitCore.Platform.Domain.Tenant.DefaultTimeZone;
}
