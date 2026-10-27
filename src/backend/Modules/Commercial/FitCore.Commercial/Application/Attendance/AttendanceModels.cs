using FitCore.Application.Core;
using FitCore.Commercial.Application.Billing;
using FitCore.Commercial.Application.Cash;
using FitCore.Commercial.Application.Memberships;
using FitCore.Commercial.Application.Settings;
using FitCore.Commercial.Domain;
using FitCore.Commercial.Persistence;
using FitCore.Platform.Persistence;
using FitCore.Platform.Security;
using FitCore.SharedKernel;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace FitCore.Commercial.Application.Attendance;

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

/// <summary>Resultado de evaluar si un alumno puede entrar hoy a una sucursal. Es solo la decisión: no registra nada.</summary>
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

/// <summary>Entrada. Se identifica al alumno por UNA de tres vías; la hora la pone siempre el servidor.</summary>
public sealed record CheckInRequest(Guid BranchId, Guid? MemberId, string? MemberCode, string? QrToken, string? OverrideReason);

public sealed record CheckOutRequest(Guid? MemberId, string? MemberCode, string? QrToken);

public sealed record ValidateAccessRequest(Guid BranchId, Guid? MemberId, string? MemberCode, string? QrToken);

public sealed record ListAttendanceQuery(
    PageRequest Page,
    Guid? MemberId,
    Guid? BranchId,
    AttendanceMethod? Method,
    bool? Open,
    bool? Exception,
    DateOnly? From,
    DateOnly? To,
    string? Search) : IQuery<PagedResult<AttendanceDto>>;

public sealed record GetAttendanceQuery(Guid Id) : IQuery<AttendanceDto>;

public sealed record ValidateAccessQuery(ValidateAccessRequest Request) : IQuery<AccessDecisionDto>;

public sealed record CheckInCommand(CheckInRequest Request) : ICommand<AttendanceDto>;

public sealed record CheckOutCommand(CheckOutRequest Request) : ICommand<AttendanceDto>;

public sealed record IssueMemberQrCommand(Guid MemberId) : ICommand<QrTokenDto>;

public sealed class CheckInRequestValidator : AbstractValidator<CheckInRequest>
{
    public CheckInRequestValidator()
    {
        RuleFor(x => x.BranchId).NotEmpty();
        RuleFor(x => x).Must(x => MemberIdentity.ExactlyOne(x.MemberId, x.MemberCode, x.QrToken))
            .WithName("memberId").WithMessage("Indique al alumno por una sola vía: identificador, código o código QR.");
        RuleFor(x => x.MemberCode).MaximumLength(20);
        RuleFor(x => x.QrToken).MaximumLength(100);
        RuleFor(x => x.OverrideReason).MinimumLength(5).MaximumLength(300).When(x => !string.IsNullOrWhiteSpace(x.OverrideReason))
            .WithMessage("Explique el motivo de la excepción (mínimo 5 caracteres).");
    }
}

public sealed class CheckOutRequestValidator : AbstractValidator<CheckOutRequest>
{
    public CheckOutRequestValidator()
    {
        RuleFor(x => x).Must(x => MemberIdentity.ExactlyOne(x.MemberId, x.MemberCode, x.QrToken))
            .WithName("memberId").WithMessage("Indique al alumno por una sola vía: identificador, código o código QR.");
        RuleFor(x => x.MemberCode).MaximumLength(20);
        RuleFor(x => x.QrToken).MaximumLength(100);
    }
}

public sealed class ValidateAccessRequestValidator : AbstractValidator<ValidateAccessRequest>
{
    public ValidateAccessRequestValidator()
    {
        RuleFor(x => x.BranchId).NotEmpty();
        RuleFor(x => x).Must(x => MemberIdentity.ExactlyOne(x.MemberId, x.MemberCode, x.QrToken))
            .WithName("memberId").WithMessage("Indique al alumno por una sola vía: identificador, código o código QR.");
        RuleFor(x => x.MemberCode).MaximumLength(20);
        RuleFor(x => x.QrToken).MaximumLength(100);
    }
}

internal static class MemberIdentity
{
    public static bool ExactlyOne(Guid? memberId, string? code, string? qr) =>
        new[] { memberId is not null, !string.IsNullOrWhiteSpace(code), !string.IsNullOrWhiteSpace(qr) }.Count(x => x) == 1;

    /// <summary>
    /// Busca al alumno en todo el gimnasio (no solo en la sucursal del usuario): un alumno puede entrenar en otra sucursal
    /// si su plan lo permite. Solo se devuelve lo mínimo para dar acceso. El QR se valida contra su huella guardada.
    /// </summary>
    public static async Task<Result<Member>> ResolveAsync(CommercialDbContext db, Guid? memberId, string? code, string? qrToken, CancellationToken cancellationToken)
    {
        Member? member;
        if (!string.IsNullOrWhiteSpace(qrToken))
        {
            if (!QrTokens.LooksValid(qrToken.Trim()))
            {
                return AttendanceErrorsApp.InvalidQr;
            }

            var hash = QrTokens.Hash(qrToken);
            member = await db.Members.AsNoTracking().FirstOrDefaultAsync(m => m.QrTokenHash == hash, cancellationToken);
            return member is null ? AttendanceErrorsApp.InvalidQr : member;
        }

        if (memberId is { } id)
        {
            member = await db.Members.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, cancellationToken);
        }
        else
        {
            var normalized = code!.Trim().ToUpperInvariant();
            member = await db.Members.AsNoTracking().FirstOrDefaultAsync(m => m.Code == normalized, cancellationToken);
        }

        return member is null ? AttendanceErrorsApp.MemberNotFound : member;
    }
}

internal sealed record AccessDenial(string Code, string Message, bool Overridable)
{
    public Error ToError() => Error.Forbidden(Code, Message);
}

internal sealed record AccessEvaluation(AccessDenial? Denial, Membership? Membership, string? PlanName, decimal Owed)
{
    public bool Allowed => Denial is null;
}

internal static class AttendanceErrorsApp
{
    public static readonly Error NotFound = Error.NotFound("attendance.not_found", "Registro de asistencia no encontrado.");
    public static readonly Error MemberNotFound = Error.NotFound("members.not_found", "Alumno no encontrado.");
    public static readonly Error InvalidQr = Error.NotFound("attendance.invalid_qr", "El código QR no es válido o fue reemplazado.");
    public static readonly Error AlreadyCheckedIn = Error.Conflict("attendance.already_checked_in", "El alumno ya tiene una entrada abierta en esta sucursal.");
    public static readonly Error OpenEntryElsewhere = Error.Conflict("attendance.open_entry_elsewhere", "El alumno tiene una entrada abierta en otra sucursal: registre su salida primero.");
    public static readonly Error OverrideNotAllowed = Error.Forbidden("attendance.override_not_allowed", "No tiene permiso para autorizar accesos por excepción.");

    public static readonly AccessDenial MemberInactive = new("attendance.member_inactive", "El alumno está inactivo.", false);
    public static readonly AccessDenial NoActiveMembership = new("attendance.no_active_membership", "El alumno no tiene una membresía vigente.", true);
    public static readonly AccessDenial MembershipExpired = new("attendance.membership_expired", "La membresía del alumno está vencida.", true);
    public static readonly AccessDenial MembershipNotStarted = new("attendance.membership_not_started", "La membresía del alumno aún no ha comenzado.", true);
    public static readonly AccessDenial MembershipFrozen = new("attendance.membership_frozen", "La membresía del alumno está congelada.", true);
    public static readonly AccessDenial MembershipPendingPayment = new("attendance.membership_pending_payment", "La membresía del alumno está pendiente de pago.", true);
    public static readonly AccessDenial PlanBranchRestricted = new("attendance.plan_branch_restricted", "El plan del alumno no permite entrenar en esta sucursal.", true);
    public static readonly AccessDenial DebtPending = new(AccessRules.DebtPending, "El alumno tiene saldo pendiente y su gimnasio exige el pago completo.", true);
    public static readonly AccessDenial DebtOverdue = new(AccessRules.DebtOverdue, "El alumno tiene una cuota vencida sin pagar.", true);
    public static readonly AccessDenial MinimumPaymentNotMet = new(AccessRules.MinimumPaymentNotMet, "El alumno no ha pagado el mínimo que exige su gimnasio.", true);

    public static AccessDenial ForDebt(string code) => code switch
    {
        AccessRules.DebtPending => DebtPending,
        AccessRules.MinimumPaymentNotMet => MinimumPaymentNotMet,
        _ => DebtOverdue
    };
}

/// <summary>
/// Decide si un alumno puede entrar hoy a una sucursal: alumno activo, membresía vigente (fechas y estado), restricciones
/// del plan por sucursal y política de acceso con deuda según los saldos de hoy.
/// </summary>
internal static class AccessEvaluator
{
    public static async Task<AccessEvaluation> EvaluateAsync(CommercialDbContext db, Member member, Guid branchId, DateOnly today, CancellationToken cancellationToken)
    {
        if (!member.IsActive)
        {
            return new AccessEvaluation(AttendanceErrorsApp.MemberInactive, null, null, 0m);
        }

        var memberships = await db.Memberships.AsNoTracking()
            .Where(m => m.MemberId == member.Id && m.Status != MembershipStatus.Cancelled)
            .OrderByDescending(m => m.StartDate).Take(20).ToListAsync(cancellationToken);

        var current = memberships.FirstOrDefault(m => m.Status == MembershipStatus.Active && m.StartDate <= today && today <= m.EndDate);
        if (current is null)
        {
            return new AccessEvaluation(WhyNoMembership(memberships, today), memberships.FirstOrDefault(), memberships.FirstOrDefault()?.PlanName, 0m);
        }

        var plan = await db.Plans.AsNoTracking().Include(p => p.Branches).FirstOrDefaultAsync(p => p.Id == current.PlanId, cancellationToken);
        if (plan is { Branches.Count: > 0 } && plan.Branches.All(b => b.BranchId != branchId))
        {
            return new AccessEvaluation(AttendanceErrorsApp.PlanBranchRestricted, current, current.PlanName, 0m);
        }

        var obligations = await db.Obligations.AsNoTracking()
            .Where(o => o.MembershipId == current.Id && o.Status != ObligationStatus.Voided).ToListAsync(cancellationToken);
        var balances = await Ledger.BalancesAsync(db, [.. obligations.Select(o => o.Id)], cancellationToken);
        var standings = obligations.Select(o => new ObligationStanding(o.PayableAmount, balances.GetValueOrDefault(o.Id).Paid, o.DueDate)).ToList();
        var owed = standings.Sum(s => s.Owed);

        var settings = await SettingsStore.GetAsync(db, member.TenantId, cancellationToken);
        var debt = AccessRules.DebtDenial(settings.AccessPolicy, settings.MinPaymentPercent, today, standings);
        return debt is null
            ? new AccessEvaluation(null, current, current.PlanName, owed)
            : new AccessEvaluation(AttendanceErrorsApp.ForDebt(debt), current, current.PlanName, owed);
    }

    private static AccessDenial WhyNoMembership(IReadOnlyList<Membership> memberships, DateOnly today)
    {
        if (memberships.Any(m => m.Status == MembershipStatus.Frozen))
        {
            return AttendanceErrorsApp.MembershipFrozen;
        }

        if (memberships.Any(m => m.Status == MembershipStatus.PendingPayment && m.EndDate >= today))
        {
            return AttendanceErrorsApp.MembershipPendingPayment;
        }

        if (memberships.Any(m => m.Status == MembershipStatus.Active && m.StartDate > today))
        {
            return AttendanceErrorsApp.MembershipNotStarted;
        }

        return memberships.Any(m => m.EndDate < today) ? AttendanceErrorsApp.MembershipExpired : AttendanceErrorsApp.NoActiveMembership;
    }
}

internal static class AttendanceQueries
{
    public static IQueryable<AttendanceEntry> VisibleTo(this IQueryable<AttendanceEntry> entries, UserAccess actor) =>
        entries.InScope(actor, a => a.BranchId);

    public static async Task<IReadOnlyList<AttendanceDto>> ToDtosAsync(
        CommercialDbContext db,
        PlatformDbContext platformDb,
        IReadOnlyList<AttendanceEntry> entries,
        CancellationToken cancellationToken)
    {
        var memberIds = entries.Select(e => e.MemberId).Distinct().ToList();
        var branchIds = entries.Select(e => e.BranchId).Distinct().ToList();
        var membershipIds = entries.Where(e => e.MembershipId != null).Select(e => e.MembershipId!.Value).Distinct().ToList();
        var members = await db.Members.AsNoTracking().Where(m => memberIds.Contains(m.Id))
            .Select(m => new { m.Id, m.Code, m.FirstName, m.LastName }).ToDictionaryAsync(m => m.Id, cancellationToken);
        var branches = await db.BranchRefs.AsNoTracking().Where(b => branchIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, b => b.Name, cancellationToken);
        var plans = await db.Memberships.AsNoTracking().Where(m => membershipIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, m => m.PlanName, cancellationToken);
        var names = await CashQueries.UserNamesAsync(platformDb, entries.Select(e => (Guid?)e.RegisteredBy), cancellationToken);

        return
        [
            .. entries.Select(e =>
            {
                var member = members[e.MemberId];
                return new AttendanceDto(
                    e.Id,
                    e.BranchId,
                    branches.GetValueOrDefault(e.BranchId, string.Empty),
                    e.MemberId,
                    member.Code,
                    $"{member.FirstName} {member.LastName}",
                    e.MembershipId,
                    e.MembershipId is { } mid ? plans.GetValueOrDefault(mid) : null,
                    e.CheckInAt,
                    e.CheckOutAt,
                    e.CheckOutAt is { } outAt ? (int)Math.Round((outAt - e.CheckInAt).TotalMinutes) : null,
                    e.Method.ToString(),
                    e.CheckOutMethod?.ToString(),
                    e.IsException,
                    e.ExceptionReason,
                    e.RegisteredBy,
                    names.GetValueOrDefault(e.RegisteredBy),
                    e.CheckOutBy,
                    e.IsOpen);
            })
        ];
    }

    public static async Task<AttendanceDto> ToDtoAsync(CommercialDbContext db, PlatformDbContext platformDb, AttendanceEntry entry, CancellationToken cancellationToken) =>
        (await ToDtosAsync(db, platformDb, [entry], cancellationToken))[0];
}
