using FitCore.Application.Core;
using FitCore.Commercial.Application.Members;
using FitCore.Commercial.Application.Memberships;
using FitCore.Commercial.Application.Settings;
using FitCore.Commercial.Domain;
using FitCore.Commercial.Persistence;
using FitCore.Platform.Application;
using FitCore.Platform.Persistence;
using FitCore.Platform.Security;
using FitCore.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace FitCore.Commercial.Application.Billing;

/// <summary>Efectos de un pago confirmado (o revertido) sobre las obligaciones y las membresías, dentro de la misma transacción.</summary>
internal static class PaymentEffects
{
    public static async Task RefreshAsync(
        CommercialDbContext db,
        IReadOnlyCollection<PaymentObligation> obligations,
        Guid paymentId,
        bool activateMemberships,
        Guid? actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (obligations.Count == 0)
        {
            return;
        }

        var balances = await Ledger.BalancesAsync(db, [.. obligations.Select(o => o.Id)], cancellationToken);
        foreach (var obligation in obligations)
        {
            obligation.ApplyConfirmedPayments(balances.GetValueOrDefault(obligation.Id).Paid, actor, now);
        }

        if (!activateMemberships)
        {
            return;
        }

        // Una membresía pasa de pendiente a activa cuando el pago cumple la política del gimnasio. Un reverso posterior no la
        // desactiva: el acceso se decide con los saldos vigentes (bloque de asistencia).
        var settings = await SettingsStore.GetAsync(db, obligations.First().TenantId, cancellationToken);
        var membershipIds = obligations.Where(o => o.MembershipId != null).Select(o => o.MembershipId!.Value).Distinct().ToList();
        var pending = await db.Memberships.Where(m => membershipIds.Contains(m.Id) && m.Status == MembershipStatus.PendingPayment).ToListAsync(cancellationToken);
        foreach (var membership in pending)
        {
            var obligation = obligations.First(o => o.MembershipId == membership.Id);
            var paid = balances.GetValueOrDefault(obligation.Id).Paid;
            if (!ActivationRules.IsSatisfied(settings.AccessPolicy, paid, obligation.PayableAmount, settings.MinPaymentPercent))
            {
                continue;
            }

            membership.Activate(actor, now);
            db.MembershipEvents.Add(MembershipQueries.NewEvent(membership, MembershipEventType.Activated, actor, now, new
            {
                paymentId,
                obligation = obligation.Reference,
                policy = settings.AccessPolicy.ToString(),
                paid,
                payable = obligation.PayableAmount
            }));
        }
    }
}

internal sealed class CreatePaymentHandler(CommercialDbContext db, IAccessContext access, IClock clock, ICurrentUser currentUser)
    : ICommandHandler<CreatePaymentCommand, PaymentDto>
{
    private const string Scope = "payments.create";

    public async Task<Result<PaymentDto>> HandleAsync(CreatePaymentCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        if (Idempotency.Validate(command.IdempotencyKey) is { } keyError)
        {
            return keyError;
        }

        var request = command.Request;
        var key = command.IdempotencyKey!.Trim();

        // El efectivo se habilita con la caja: exige una sesión de caja abierta para que el dinero quede cuadrado.
        if (request.Method == PaymentMethod.Cash)
        {
            return PaymentErrorsApp.CashRequiresOpenSession;
        }

        var member = await db.Members.AsNoTracking().VisibleTo(actor.Value).FirstOrDefaultAsync(m => m.Id == request.MemberId, cancellationToken);
        if (member is null)
        {
            return MembershipErrorsApp.MemberNotFound;
        }

        var now = clock.UtcNow;
        var paidAt = request.PaidAt ?? now;
        if (paidAt > now.AddMinutes(5) || paidAt < now.AddDays(-30))
        {
            return PaymentErrorsApp.PaidAtInvalid;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var begin = await Idempotency.BeginAsync<PaymentDto>(db, member.TenantId, key, Scope, Idempotency.Hash(request, currentUser.UserId), currentUser.UserId, now, cancellationToken);
        if (begin.IsFailure)
        {
            return begin.Error;
        }

        if (begin.Value.IsReplay)
        {
            return begin.Value.Response!;
        }

        // 1. Obligaciones a las que se aplicará el pago, bloqueadas antes de calcular nada.
        var explicitAllocations = request.Allocations is { Count: > 0 } ? request.Allocations : null;
        List<Guid> targetIds;
        if (explicitAllocations is not null)
        {
            targetIds = [.. explicitAllocations.Select(a => a.ObligationId)];
        }
        else
        {
            targetIds = await db.Obligations.AsNoTracking()
                .Where(o => o.MemberId == member.Id && (o.Status == ObligationStatus.Pending || o.Status == ObligationStatus.PartiallyPaid))
                .Select(o => o.Id).ToListAsync(cancellationToken);
        }

        await Ledger.LockAsync(db, targetIds, cancellationToken);
        var obligations = await db.Obligations.VisibleTo(actor.Value).Where(o => targetIds.Contains(o.Id)).ToListAsync(cancellationToken);
        if (explicitAllocations is not null && obligations.Count != targetIds.Distinct().Count())
        {
            return ObligationErrors.NotFound;
        }

        if (obligations.Any(o => o.MemberId != member.Id))
        {
            return ObligationErrors.NotFound;
        }

        // 2. Disponible de cada una = exigible − cobrado − reservado, con los datos ya bloqueados.
        var balances = await Ledger.BalancesAsync(db, [.. obligations.Select(o => o.Id)], cancellationToken);
        decimal Available(PaymentObligation o) => o.PayableAmount - balances.GetValueOrDefault(o.Id).Paid - balances.GetValueOrDefault(o.Id).Reserved;

        var lines = new List<(Guid ObligationId, decimal Amount)>();
        if (explicitAllocations is not null)
        {
            foreach (var allocation in explicitAllocations)
            {
                var obligation = obligations.First(o => o.Id == allocation.ObligationId);
                if (obligation.Status is ObligationStatus.Voided or ObligationStatus.Paid)
                {
                    return PaymentErrorsApp.ObligationNotPayable;
                }

                if (Money.Round(allocation.Amount) > Available(obligation))
                {
                    return PaymentErrorsApp.ExceedsBalance;
                }

                lines.Add((obligation.Id, Money.Round(allocation.Amount)));
            }

            if (lines.Sum(l => l.Amount) != Money.Round(request.Amount))
            {
                return PaymentErrorsApp.AllocationMismatch;
            }
        }
        else
        {
            if (obligations.Count == 0)
            {
                return PaymentErrorsApp.NothingToPay;
            }

            var remaining = Money.Round(request.Amount);
            foreach (var obligation in obligations.OrderBy(o => o.DueDate).ThenBy(o => o.IssueDate).ThenBy(o => o.Reference, StringComparer.Ordinal))
            {
                var take = Math.Min(remaining, Math.Max(0m, Available(obligation)));
                if (take > 0m)
                {
                    lines.Add((obligation.Id, take));
                    remaining -= take;
                }
            }

            if (remaining > 0m)
            {
                return PaymentErrorsApp.ExceedsBalance;
            }
        }

        // 3. El pago y sus asignaciones, juntos.
        var receipt = $"RC-{await db.NextCounterAsync(member.TenantId, "receipt", cancellationToken):D6}";
        var payment = Payment.Create(
            member.TenantId,
            member.BranchId,
            member.Id,
            receipt,
            new PaymentDetails(request.Method, request.MethodDetail, request.Reference, request.BankName, paidAt, request.Notes),
            lines,
            currentUser.UserId,
            now);
        db.Payments.Add(payment);
        await db.SaveChangesAsync(cancellationToken);

        // 4. Un pago confirmado actualiza el estado de las obligaciones y puede activar membresías.
        var affected = obligations.Where(o => lines.Any(l => l.ObligationId == o.Id)).ToList();
        if (payment.Status == PaymentStatus.Confirmed)
        {
            await PaymentEffects.RefreshAsync(db, affected, payment.Id, true, currentUser.UserId, now, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }

        var dto = await PaymentQueries.ToDtoAsync(db, payment, cancellationToken);
        await Idempotency.CompleteAsync(db, member.TenantId, key, 201, dto, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return dto;
    }
}

internal sealed class ConfirmPaymentHandler(CommercialDbContext db, IAccessContext access, IClock clock, ICurrentUser currentUser)
    : ICommandHandler<ConfirmPaymentCommand, PaymentDto>
{
    private const string Scope = "payments.confirm";

    public async Task<Result<PaymentDto>> HandleAsync(ConfirmPaymentCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        if (Idempotency.Validate(command.IdempotencyKey) is { } keyError)
        {
            return keyError;
        }

        var key = command.IdempotencyKey!.Trim();
        var now = clock.UtcNow;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var begin = await Idempotency.BeginAsync<PaymentDto>(db, actor.Value.TenantId, key, Scope, Idempotency.Hash(new { command.Id }, currentUser.UserId), currentUser.UserId, now, cancellationToken);
        if (begin.IsFailure)
        {
            return begin.Error;
        }

        if (begin.Value.IsReplay)
        {
            return begin.Value.Response!;
        }

        var payment = await db.Payments.Include(p => p.Allocations).VisibleTo(actor.Value).FirstOrDefaultAsync(p => p.Id == command.Id, cancellationToken);
        if (payment is null)
        {
            return PaymentErrorsApp.NotFound;
        }

        var obligationIds = payment.Allocations.Select(a => a.ObligationId).ToList();
        await Ledger.LockAsync(db, obligationIds, cancellationToken);

        var confirmed = payment.Confirm(currentUser.UserId, now);
        if (confirmed.IsFailure)
        {
            return confirmed.Error;
        }

        await db.SaveChangesAsync(cancellationToken);
        var obligations = await db.Obligations.Where(o => obligationIds.Contains(o.Id)).ToListAsync(cancellationToken);
        await PaymentEffects.RefreshAsync(db, obligations, payment.Id, true, currentUser.UserId, now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        var dto = await PaymentQueries.ToDtoAsync(db, payment, cancellationToken);
        await Idempotency.CompleteAsync(db, actor.Value.TenantId, key, 200, dto, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return dto;
    }
}

internal sealed class ReversePaymentHandler(CommercialDbContext db, IAccessContext access, IClock clock, ICurrentUser currentUser)
    : ICommandHandler<ReversePaymentCommand, PaymentDto>
{
    private const string Scope = "payments.reverse";

    public async Task<Result<PaymentDto>> HandleAsync(ReversePaymentCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        if (Idempotency.Validate(command.IdempotencyKey) is { } keyError)
        {
            return keyError;
        }

        var key = command.IdempotencyKey!.Trim();
        var now = clock.UtcNow;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var begin = await Idempotency.BeginAsync<PaymentDto>(db, actor.Value.TenantId, key, Scope, Idempotency.Hash(new { command.Id, command.Request.Reason }, currentUser.UserId), currentUser.UserId, now, cancellationToken);
        if (begin.IsFailure)
        {
            return begin.Error;
        }

        if (begin.Value.IsReplay)
        {
            return begin.Value.Response!;
        }

        var payment = await db.Payments.Include(p => p.Allocations).VisibleTo(actor.Value).FirstOrDefaultAsync(p => p.Id == command.Id, cancellationToken);
        if (payment is null)
        {
            return PaymentErrorsApp.NotFound;
        }

        var obligationIds = payment.Allocations.Select(a => a.ObligationId).ToList();
        await Ledger.LockAsync(db, obligationIds, cancellationToken);

        // Los pagos no se borran: el reverso cambia su estado, deja motivo y responsable, y libera el saldo.
        var reversed = payment.Reverse(command.Request.Reason, currentUser.UserId, now);
        if (reversed.IsFailure)
        {
            return reversed.Error;
        }

        await db.SaveChangesAsync(cancellationToken);
        var obligations = await db.Obligations.Where(o => obligationIds.Contains(o.Id)).ToListAsync(cancellationToken);
        await PaymentEffects.RefreshAsync(db, obligations, payment.Id, false, currentUser.UserId, now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        var dto = await PaymentQueries.ToDtoAsync(db, payment, cancellationToken);
        await Idempotency.CompleteAsync(db, actor.Value.TenantId, key, 200, dto, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return dto;
    }
}

// ---- Consultas -----------------------------------------------------------------------------------------------------

internal sealed class ListPaymentsHandler(CommercialDbContext db, IAccessContext access)
    : IQueryHandler<ListPaymentsQuery, PagedResult<PaymentDto>>
{
    private static readonly TimeZoneInfo Ecuador = TimeZoneInfo.FindSystemTimeZoneById(FitCore.Platform.Domain.Tenant.DefaultTimeZone);

    public async Task<Result<PagedResult<PaymentDto>>> HandleAsync(ListPaymentsQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        if (query.BranchId is { } requested && !actor.Value.CanAccessBranch(requested))
        {
            return PlatformErrors.BranchNotAllowed;
        }

        var payments = db.Payments.AsNoTracking().Include(p => p.Allocations).VisibleTo(actor.Value);
        if (query.MemberId is { } memberId)
        {
            payments = payments.Where(p => p.MemberId == memberId);
        }

        if (query.BranchId is { } branchId)
        {
            payments = payments.Where(p => p.BranchId == branchId);
        }

        if (query.Method is { } method)
        {
            payments = payments.Where(p => p.Method == method);
        }

        if (query.Status is { } status)
        {
            payments = payments.Where(p => p.Status == status);
        }

        if (query.From is { } from)
        {
            // PostgreSQL (timestamptz) solo recibe instantes en UTC: se convierte el inicio del día local de Ecuador.
            var start = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), Ecuador.GetUtcOffset(from.ToDateTime(TimeOnly.MinValue))).ToUniversalTime();
            payments = payments.Where(p => p.PaidAt >= start);
        }

        if (query.To is { } to)
        {
            var end = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), Ecuador.GetUtcOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue))).ToUniversalTime();
            payments = payments.Where(p => p.PaidAt < end);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var pattern = LikePattern.Contains(query.Search);
            payments = payments.Where(p =>
                EF.Functions.ILike(p.ReceiptNumber, pattern)
                || (p.Reference != null && EF.Functions.ILike(p.Reference, pattern))
                || db.Members.Any(m => m.Id == p.MemberId && (EF.Functions.ILike(m.FirstName + " " + m.LastName, pattern) || EF.Functions.ILike(m.Code, pattern) || EF.Functions.ILike(m.IdNumber, pattern))));
        }

        var paged = await payments.OrderByDescending(p => p.PaidAt).ThenByDescending(p => p.ReceiptNumber).ToPagedAsync(query.Page, cancellationToken);
        return new PagedResult<PaymentDto>(await PaymentQueries.ToDtosAsync(db, paged.Items, cancellationToken), paged.Page, paged.PageSize, paged.TotalCount);
    }
}

internal sealed class GetPaymentHandler(CommercialDbContext db, IAccessContext access) : IQueryHandler<GetPaymentQuery, PaymentDto>
{
    public async Task<Result<PaymentDto>> HandleAsync(GetPaymentQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var payment = await db.Payments.AsNoTracking().Include(p => p.Allocations).VisibleTo(actor.Value).FirstOrDefaultAsync(p => p.Id == query.Id, cancellationToken);
        return payment is null ? PaymentErrorsApp.NotFound : await PaymentQueries.ToDtoAsync(db, payment, cancellationToken);
    }
}

internal sealed class GetReceiptHandler(CommercialDbContext db, PlatformDbContext platformDb, IAccessContext access) : IQueryHandler<GetReceiptQuery, ReceiptDto>
{
    public async Task<Result<ReceiptDto>> HandleAsync(GetReceiptQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var payment = await db.Payments.AsNoTracking().Include(p => p.Allocations).VisibleTo(actor.Value).FirstOrDefaultAsync(p => p.Id == query.Id, cancellationToken);
        if (payment is null)
        {
            return PaymentErrorsApp.NotFound;
        }

        var member = await db.Members.AsNoTracking().FirstAsync(m => m.Id == payment.MemberId, cancellationToken);
        var branch = await db.BranchRefs.AsNoTracking().FirstAsync(b => b.Id == payment.BranchId, cancellationToken);
        var obligationIds = payment.Allocations.Select(a => a.ObligationId).ToList();
        var obligations = await db.Obligations.AsNoTracking().Where(o => obligationIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, cancellationToken);
        var balances = await Ledger.BalancesAsync(db, obligationIds, cancellationToken);
        var receivedBy = payment.CreatedBy is { } userId
            ? await platformDb.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.FullName).FirstOrDefaultAsync(cancellationToken)
            : null;

        return new ReceiptDto(
            payment.ReceiptNumber,
            "Comprobante interno de pago. No es una factura tributaria.",
            payment.PaidAt,
            payment.Status.ToString(),
            branch.Code,
            branch.Name,
            member.Code,
            member.FullName,
            payment.Method.ToString(),
            payment.MethodDetail,
            payment.Reference,
            payment.BankName,
            Payment.Currency,
            payment.Amount,
            receivedBy,
            [.. payment.Allocations.Select(a =>
            {
                var obligation = obligations[a.ObligationId];
                var paid = balances.GetValueOrDefault(obligation.Id).Paid;
                return new ReceiptLineDto(obligation.Reference, obligation.Concept, a.Amount, obligation.PayableAmount, obligation.Status == ObligationStatus.Voided ? 0m : obligation.PayableAmount - paid);
            }).OrderBy(l => l.ObligationReference, StringComparer.Ordinal)]);
    }
}
