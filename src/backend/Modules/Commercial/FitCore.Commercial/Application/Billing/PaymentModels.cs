using FitCore.Application.Core;
using FitCore.Commercial.Domain;
using FitCore.Commercial.Persistence;
using FitCore.Platform.Security;
using FitCore.SharedKernel;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace FitCore.Commercial.Application.Billing;

public sealed record PaymentLineDto(Guid ObligationId, string ObligationReference, string Concept, decimal Amount);

public sealed record PaymentDto(
    Guid Id,
    string ReceiptNumber,
    Guid BranchId,
    Guid MemberId,
    string MemberCode,
    string MemberName,
    string Method,
    string? MethodDetail,
    decimal Amount,
    string Currency,
    string Status,
    string? Reference,
    string? BankName,
    DateTimeOffset PaidAt,
    string? Notes,
    IReadOnlyList<PaymentLineDto> Allocations,
    DateTimeOffset? ConfirmedAt,
    Guid? ConfirmedBy,
    DateTimeOffset? ReversedAt,
    Guid? ReversedBy,
    string? ReversalReason,
    DateTimeOffset CreatedAt,
    Guid? CreatedBy);

/// <summary>Una parte del pago aplicada a una obligación. El importe total del pago es siempre la suma de sus partes.</summary>
public sealed record AllocationRequest(Guid ObligationId, decimal Amount);

/// <summary>
/// Registrar un pago. Si no se indican <c>Allocations</c>, el sistema aplica el importe a las obligaciones abiertas del
/// alumno, de la más antigua a la más nueva. Nunca se acepta un saldo ni un "total pagado" del cliente: el backend lo recalcula.
/// </summary>
public sealed record CreatePaymentRequest(
    Guid MemberId,
    PaymentMethod Method,
    decimal Amount,
    string? Reference,
    string? BankName,
    string? MethodDetail,
    DateTimeOffset? PaidAt,
    string? Notes,
    IReadOnlyList<AllocationRequest>? Allocations);

public sealed record ReversePaymentRequest(string Reason);

public sealed record ReceiptLineDto(string ObligationReference, string Concept, decimal Amount, decimal ObligationPayable, decimal ObligationBalance);

/// <summary>Recibo interno. No es una factura tributaria: la facturación electrónica llega en otra fase.</summary>
public sealed record ReceiptDto(
    string ReceiptNumber,
    string Notice,
    DateTimeOffset IssuedAt,
    string Status,
    string BranchCode,
    string BranchName,
    string MemberCode,
    string MemberName,
    string Method,
    string? MethodDetail,
    string? Reference,
    string? BankName,
    string Currency,
    decimal Total,
    string? ReceivedBy,
    IReadOnlyList<ReceiptLineDto> Lines);

public sealed record ListPaymentsQuery(
    PageRequest Page,
    Guid? MemberId,
    Guid? BranchId,
    PaymentMethod? Method,
    PaymentStatus? Status,
    DateOnly? From,
    DateOnly? To,
    string? Search) : IQuery<PagedResult<PaymentDto>>;

public sealed record GetPaymentQuery(Guid Id) : IQuery<PaymentDto>;

public sealed record GetReceiptQuery(Guid Id) : IQuery<ReceiptDto>;

public sealed record CreatePaymentCommand(CreatePaymentRequest Request, string? IdempotencyKey) : ICommand<PaymentDto>;

public sealed record ConfirmPaymentCommand(Guid Id, string? IdempotencyKey) : ICommand<PaymentDto>;

public sealed record ReversePaymentCommand(Guid Id, ReversePaymentRequest Request, string? IdempotencyKey) : ICommand<PaymentDto>;

public sealed class CreatePaymentRequestValidator : AbstractValidator<CreatePaymentRequest>
{
    public CreatePaymentRequestValidator()
    {
        RuleFor(x => x.MemberId).NotEmpty();
        RuleFor(x => x.Method).IsInEnum();
        RuleFor(x => x.Amount).GreaterThan(0m).LessThanOrEqualTo(MembershipPlan.MaxPrice).PrecisionScale(12, 2, ignoreTrailingZeros: true);
        RuleFor(x => x.Reference).MaximumLength(100);
        RuleFor(x => x.Reference).NotEmpty().When(x => x.Method is PaymentMethod.BankTransfer or PaymentMethod.Card)
            .WithMessage("Indique la referencia de la transferencia o el voucher del datáfono.");
        RuleFor(x => x.Reference).Must(r => !CardNumber.LooksLikeCardNumber(r!)).When(x => !string.IsNullOrWhiteSpace(x.Reference))
            .WithMessage("No registre números de tarjeta: indique solo la referencia del voucher.");
        RuleFor(x => x.BankName).MaximumLength(100);
        RuleFor(x => x.MethodDetail).MaximumLength(100);
        RuleFor(x => x.MethodDetail).NotEmpty().When(x => x.Method == PaymentMethod.Other).WithMessage("Indique el medio de pago.");
        RuleFor(x => x.Notes).MaximumLength(500);
        RuleForEach(x => x.Allocations).ChildRules(a =>
        {
            a.RuleFor(x => x.ObligationId).NotEmpty();
            a.RuleFor(x => x.Amount).GreaterThan(0m).LessThanOrEqualTo(MembershipPlan.MaxPrice).PrecisionScale(12, 2, ignoreTrailingZeros: true);
        });
        RuleFor(x => x.Allocations).Must(a => a!.Select(x => x.ObligationId).Distinct().Count() == a!.Count)
            .When(x => x.Allocations is not null).WithMessage("Una obligación aparece más de una vez.");
    }
}

public sealed class ReversePaymentRequestValidator : AbstractValidator<ReversePaymentRequest>
{
    public ReversePaymentRequestValidator() => RuleFor(x => x.Reason).NotEmpty().MinimumLength(5).MaximumLength(300);
}

/// <summary>Detecta números de tarjeta (13 a 19 dígitos que cumplen Luhn) para no guardarlos jamás como "referencia".</summary>
internal static class CardNumber
{
    public static bool LooksLikeCardNumber(string value)
    {
        var digits = new string([.. value.Where(c => !char.IsWhiteSpace(c) && c != '-')]);
        if (digits.Length is < 13 or > 19 || !digits.All(char.IsAsciiDigit))
        {
            return false;
        }

        var sum = 0;
        var alternate = false;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var d = digits[i] - '0';
            if (alternate)
            {
                d *= 2;
                if (d > 9)
                {
                    d -= 9;
                }
            }

            sum += d;
            alternate = !alternate;
        }

        return sum % 10 == 0;
    }
}

internal static class PaymentErrorsApp
{
    public static readonly Error NotFound = Error.NotFound("payments.not_found", "Pago no encontrado.");
    public static readonly Error CashRequiresOpenSession = Error.Validation("payments.cash_requires_open_cash_session", "Los pagos en efectivo requieren una caja abierta.");
    public static readonly Error ExceedsBalance = Error.Validation("payments.exceeds_balance", "El importe supera el saldo pendiente de las obligaciones.");
    public static readonly Error AllocationMismatch = Error.Validation("payments.allocation_mismatch", "La suma de las asignaciones no coincide con el importe del pago.");
    public static readonly Error ObligationNotPayable = Error.Validation("payments.obligation_not_payable", "La obligación está anulada o ya está pagada.");
    public static readonly Error NothingToPay = Error.Validation("payments.nothing_to_pay", "El alumno no tiene obligaciones pendientes.");
    public static readonly Error PaidAtInvalid = Error.Validation("payments.paid_at_invalid", "La fecha del pago no puede ser futura ni anterior a 30 días.");
    public static readonly Error ObligationHasPayments = Error.Conflict("obligations.has_payments", "La obligación tiene pagos vigentes: revierta los pagos antes de anularla.");
}

internal static class PaymentQueries
{
    public static IQueryable<Payment> VisibleTo(this IQueryable<Payment> payments, UserAccess actor) => payments.InScope(actor, p => p.BranchId);

    /// <summary>Arma las respuestas con el alumno y las obligaciones de cada pago en pocas consultas para toda la página.</summary>
    public static async Task<IReadOnlyList<PaymentDto>> ToDtosAsync(CommercialDbContext db, IReadOnlyList<Payment> payments, CancellationToken cancellationToken)
    {
        var memberIds = payments.Select(p => p.MemberId).Distinct().ToList();
        var obligationIds = payments.SelectMany(p => p.Allocations.Select(a => a.ObligationId)).Distinct().ToList();
        var members = await db.Members.AsNoTracking().Where(m => memberIds.Contains(m.Id))
            .Select(m => new { m.Id, m.Code, m.FirstName, m.LastName }).ToDictionaryAsync(m => m.Id, cancellationToken);
        var obligations = await db.Obligations.AsNoTracking().Where(o => obligationIds.Contains(o.Id))
            .Select(o => new { o.Id, o.Reference, o.Concept }).ToDictionaryAsync(o => o.Id, cancellationToken);

        return
        [
            .. payments.Select(p =>
            {
                var member = members[p.MemberId];
                return new PaymentDto(
                    p.Id,
                    p.ReceiptNumber,
                    p.BranchId,
                    p.MemberId,
                    member.Code,
                    $"{member.FirstName} {member.LastName}",
                    p.Method.ToString(),
                    p.MethodDetail,
                    p.Amount,
                    Payment.Currency,
                    p.Status.ToString(),
                    p.Reference,
                    p.BankName,
                    p.PaidAt,
                    p.Notes,
                    [.. p.Allocations.OrderBy(a => obligations[a.ObligationId].Reference).Select(a =>
                        new PaymentLineDto(a.ObligationId, obligations[a.ObligationId].Reference, obligations[a.ObligationId].Concept, a.Amount))],
                    p.ConfirmedAt,
                    p.ConfirmedBy,
                    p.ReversedAt,
                    p.ReversedBy,
                    p.ReversalReason,
                    p.CreatedAt,
                    p.CreatedBy);
            })
        ];
    }

    public static async Task<PaymentDto> ToDtoAsync(CommercialDbContext db, Payment payment, CancellationToken cancellationToken) =>
        (await ToDtosAsync(db, [payment], cancellationToken))[0];
}
