using FitCore.Application.Core;
using FitCore.Commercial.Application.Members;
using FitCore.Commercial.Application.Memberships;
using FitCore.Commercial.Application.Settings;
using FitCore.Commercial.Domain;
using FitCore.Commercial.Persistence;
using FitCore.Platform.Security;
using FitCore.SharedKernel;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace FitCore.Commercial.Application.Billing;

/// <summary>Obligación por un concepto comercial distinto de una membresía (una clase suelta, un producto…). El importe incluye IVA.</summary>
public sealed record CreateObligationRequest(Guid MemberId, string Concept, decimal Amount, TaxKind TaxKind, DateOnly? DueDate);

public sealed record VoidObligationRequest(string Reason);

public sealed record CreateObligationCommand(CreateObligationRequest Request) : ICommand<ObligationDto>;

public sealed record VoidObligationCommand(Guid Id, VoidObligationRequest Request) : ICommand<ObligationDto>;

public sealed class CreateObligationRequestValidator : AbstractValidator<CreateObligationRequest>
{
    public CreateObligationRequestValidator()
    {
        RuleFor(x => x.MemberId).NotEmpty();
        RuleFor(x => x.Concept).NotEmpty().MinimumLength(3).MaximumLength(200);
        RuleFor(x => x.Amount).GreaterThan(0m).LessThanOrEqualTo(MembershipPlan.MaxPrice).PrecisionScale(12, 2, ignoreTrailingZeros: true);
        RuleFor(x => x.TaxKind).IsInEnum();
    }
}

public sealed class VoidObligationRequestValidator : AbstractValidator<VoidObligationRequest>
{
    public VoidObligationRequestValidator() => RuleFor(x => x.Reason).NotEmpty().MinimumLength(5).MaximumLength(300);
}

internal sealed class CreateObligationHandler(CommercialDbContext db, IAccessContext access, IClock clock, ICurrentUser currentUser)
    : ICommandHandler<CreateObligationCommand, ObligationDto>
{
    public async Task<Result<ObligationDto>> HandleAsync(CreateObligationCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var request = command.Request;
        var member = await db.Members.AsNoTracking().VisibleTo(actor.Value).FirstOrDefaultAsync(m => m.Id == request.MemberId, cancellationToken);
        if (member is null)
        {
            return MembershipErrorsApp.MemberNotFound;
        }

        var timeZone = await MembershipQueries.BranchTimeZoneAsync(db, member.BranchId, cancellationToken);
        var today = LocalDates.Today(clock, timeZone);
        var settings = await SettingsStore.GetAsync(db, member.TenantId, cancellationToken);
        var dueDate = request.DueDate ?? today.AddDays(settings.DueDays);
        if (dueDate < today)
        {
            return MembershipErrorsApp.DueInPast;
        }

        var rates = await TaxRateLookup.ForDateAsync(db, today, cancellationToken);
        var taxPercent = rates.TryGetValue(request.TaxKind, out var rate) ? rate.Percent : 0m;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var number = await db.NextCounterAsync(member.TenantId, "obligation-reference", cancellationToken);
        var obligation = PaymentObligation.Create(
            member.TenantId,
            member.BranchId,
            member.Id,
            null,
            $"OB-{number:D6}",
            request.Concept,
            today,
            dueDate,
            request.Amount,
            0m,
            request.TaxKind,
            taxPercent,
            currentUser.UserId,
            clock.UtcNow);
        db.Obligations.Add(obligation);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ObligationDto.From(obligation, member.Code, member.FullName, today);
    }
}

internal sealed class VoidObligationHandler(CommercialDbContext db, IAccessContext access, IClock clock, ICurrentUser currentUser)
    : ICommandHandler<VoidObligationCommand, ObligationDto>
{
    public async Task<Result<ObligationDto>> HandleAsync(VoidObligationCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await Ledger.LockAsync(db, [command.Id], cancellationToken);

        var obligation = await db.Obligations.VisibleTo(actor.Value).FirstOrDefaultAsync(o => o.Id == command.Id, cancellationToken);
        if (obligation is null)
        {
            return ObligationErrors.NotFound;
        }

        // Una obligación con pagos vigentes no se anula: primero se revierten los pagos (los pagos nunca se borran).
        if (await Ledger.HasLivePaymentsAsync(db, obligation.Id, cancellationToken))
        {
            return PaymentErrorsApp.ObligationHasPayments;
        }

        var voided = obligation.Void(command.Request.Reason, currentUser.UserId, clock.UtcNow);
        if (voided.IsFailure)
        {
            return voided.Error;
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var member = await db.Members.AsNoTracking().FirstAsync(m => m.Id == obligation.MemberId, cancellationToken);
        return ObligationDto.From(obligation, member.Code, member.FullName, LocalDates.Today(clock));
    }
}
