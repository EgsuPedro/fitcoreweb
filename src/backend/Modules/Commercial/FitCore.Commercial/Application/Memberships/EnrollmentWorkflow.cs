using System.Globalization;
using FitCore.Commercial.Application.Settings;
using FitCore.Commercial.Domain;
using FitCore.Commercial.Persistence;
using FitCore.Platform.Domain;
using FitCore.Platform.Security;
using FitCore.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace FitCore.Commercial.Application.Memberships;

internal sealed record EnrollmentInput(
    Member Member,
    MembershipPlan Plan,
    DateOnly? StartDate,
    Guid? PromotionId,
    string? PromotionCode,
    DateOnly? DueDate,
    Membership? RenewedFrom);

/// <summary>
/// Crea una membresía y su obligación de pago en UNA transacción: o quedan las dos, o ninguna. Lo comparten la
/// inscripción y la renovación, así que las reglas (vigencia del plan, tope de descuento, usos de la promoción,
/// solapamiento) son idénticas en ambos caminos.
/// </summary>
internal static class EnrollmentWorkflow
{
    private const int MaxDaysAhead = 365;

    public static async Task<Result<Membership>> RunAsync(
        CommercialDbContext db,
        FitCore.SharedKernel.IClock clock,
        FitCore.Application.Core.ICurrentUser currentUser,
        UserAccess actor,
        EnrollmentInput input,
        CancellationToken cancellationToken)
    {
        var (member, plan) = (input.Member, input.Plan);
        var timeZone = await MembershipQueries.BranchTimeZoneAsync(db, member.BranchId, cancellationToken);
        var today = LocalDates.Today(clock, timeZone);
        var now = clock.UtcNow;

        if (!member.IsActive)
        {
            return MembershipErrorsApp.MemberInactive;
        }

        if (!plan.IsSellableOn(today, member.BranchId))
        {
            return MembershipErrorsApp.PlanNotSellable;
        }

        var start = input.StartDate ?? today;
        if (start < today)
        {
            return MembershipErrorsApp.StartInPast;
        }

        if (start > today.AddDays(MaxDaysAhead))
        {
            return MembershipErrorsApp.StartTooFar;
        }

        var end = plan.CalculateEndDate(start);

        var overlaps = await db.Memberships.AnyAsync(
            m => m.MemberId == member.Id
                && (m.Status == MembershipStatus.PendingPayment || m.Status == MembershipStatus.Active || m.Status == MembershipStatus.Frozen)
                && m.StartDate <= end
                && m.EndDate >= start,
            cancellationToken);
        if (overlaps)
        {
            return MembershipErrorsApp.Overlap;
        }

        var settings = await SettingsStore.GetAsync(db, member.TenantId, cancellationToken);
        var dueDate = input.DueDate ?? today.AddDays(settings.DueDays);
        if (dueDate < today)
        {
            return MembershipErrorsApp.DueInPast;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var promotion = await ResolvePromotionAsync(db, actor, input, plan, settings, today, cancellationToken);
        if (promotion.IsFailure)
        {
            return promotion.Error;
        }

        var discount = promotion.Value?.DiscountFor(plan.Price) ?? 0m;
        if (plan.Price - discount <= 0m)
        {
            return MembershipErrorsApp.ZeroAmount;
        }

        // El IVA vigente al contratar queda congelado en la membresía y en la obligación.
        var rates = await TaxRateLookup.ForDateAsync(db, today, cancellationToken);
        var taxPercent = rates.TryGetValue(plan.TaxKind, out var rate) ? rate.Percent : 0m;

        var terms = new MembershipTerms(
            plan.Id,
            plan.Name,
            plan.Price,
            plan.DurationValue,
            plan.DurationUnit,
            plan.TaxKind,
            taxPercent,
            plan.AllowedAccess,
            plan.CommercialTerms,
            plan.AllowFreeze,
            plan.MaxFreezeDays,
            promotion.Value?.Id,
            promotion.Value?.Name,
            promotion.Value?.Code,
            discount);

        var membership = Membership.Create(member.TenantId, member.BranchId, member.Id, terms, start, end, input.RenewedFrom?.Id, currentUser.UserId, now);

        var number = await db.NextCounterAsync(member.TenantId, "obligation-reference", cancellationToken);
        var obligation = PaymentObligation.Create(
            member.TenantId,
            member.BranchId,
            member.Id,
            membership.Id,
            $"OB-{number:D6}",
            string.Create(CultureInfo.InvariantCulture, $"Membresía {plan.Name} ({start:dd/MM/yyyy} - {end:dd/MM/yyyy})"),
            today,
            dueDate,
            plan.Price,
            discount,
            plan.TaxKind,
            taxPercent,
            currentUser.UserId,
            now);

        db.Memberships.Add(membership);
        db.Obligations.Add(obligation);
        db.MembershipEvents.Add(MembershipQueries.NewEvent(membership, MembershipEventType.Created, currentUser.UserId, now, new
        {
            planId = plan.Id,
            plan = plan.Name,
            start,
            end,
            planPrice = plan.Price,
            discount,
            agreedPrice = membership.AgreedPrice,
            promotion = promotion.Value?.Code,
            obligation = obligation.Reference,
            renewedFrom = input.RenewedFrom?.Id
        }));

        if (input.RenewedFrom is { } previous)
        {
            // La membresía anterior no se modifica: solo queda registrado en su historial que fue renovada.
            db.MembershipEvents.Add(MembershipQueries.NewEvent(previous, MembershipEventType.Renewed, currentUser.UserId, now, new
            {
                newMembershipId = membership.Id,
                newStart = start,
                newEnd = end
            }));
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return membership;
    }

    /// <summary>Promoción vigente que aplica al plan, dentro del tope autorizado y con un uso consumido de forma atómica.</summary>
    private static async Task<Result<Promotion?>> ResolvePromotionAsync(
        CommercialDbContext db,
        UserAccess actor,
        EnrollmentInput input,
        MembershipPlan plan,
        CommercialSettings settings,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        var code = string.IsNullOrWhiteSpace(input.PromotionCode) ? null : input.PromotionCode.Trim().ToUpperInvariant();
        if (input.PromotionId is null && code is null)
        {
            return Result.Success<Promotion?>(null);
        }

        if (!actor.Has(Permissions.PromotionsApply))
        {
            return MembershipErrorsApp.PromotionRequiresPermission;
        }

        var promotion = await db.Promotions.AsNoTracking().Include(p => p.Plans)
            .FirstOrDefaultAsync(p => input.PromotionId != null ? p.Id == input.PromotionId : p.Code == code, cancellationToken);
        if (promotion is null)
        {
            return MembershipErrorsApp.PromotionNotFound;
        }

        if (promotion.StatusOn(today) is PromotionStatus.Exhausted)
        {
            return MembershipErrorsApp.PromotionExhausted;
        }

        if (promotion.StatusOn(today) != PromotionStatus.Active)
        {
            return MembershipErrorsApp.PromotionNotAvailable;
        }

        if (!promotion.AppliesToPlan(plan.Id))
        {
            return MembershipErrorsApp.PromotionNotApplicable;
        }

        if (promotion.DiscountFor(plan.Price) > Money.Round(plan.Price * settings.MaxDiscountPercent / 100m))
        {
            return MembershipErrorsApp.PromotionAboveMaximum;
        }

        // Consumir un uso es atómico: dos inscripciones simultáneas no pueden pasar del máximo.
        var consumed = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE members.promotions SET used_count = used_count + 1
            WHERE id = {promotion.Id} AND (max_uses IS NULL OR used_count < max_uses)
            """,
            cancellationToken);
        if (consumed == 0)
        {
            return MembershipErrorsApp.PromotionExhausted;
        }

        return promotion;
    }
}
