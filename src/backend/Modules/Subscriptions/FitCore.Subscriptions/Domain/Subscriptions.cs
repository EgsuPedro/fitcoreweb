using FitCore.SharedKernel;

namespace FitCore.Subscriptions.Domain;

public enum SubscriptionStatus
{
    Trialing = 1,
    Active = 2,
    PastDue = 3,
    Suspended = 4,
    Cancelled = 5,
    Expired = 6
}

public enum SubscriptionEventType
{
    Created = 1,
    TrialStarted = 2,
    Activated = 3,
    PastDue = 4,
    Suspended = 5,
    Reactivated = 6,
    Expired = 7,
    Cancelled = 8,
    CancellationScheduled = 9,
    CancellationResumed = 10,
    Renewed = 11,
    PlanChanged = 12,
    PlanChangeScheduled = 13,
    Extended = 14,
    InvoiceIssued = 15,
    PaymentConfirmed = 16,
    PaymentFailed = 17,
    InvoiceVoided = 18,
    PaymentRefunded = 19
}

/// <summary>
/// Transiciones válidas del ciclo de vida de una suscripción comercial (ADR 0012). La base de datos repite esta tabla en un disparador:
/// una transición inválida se rechaza aunque alguien escriba directamente en las tablas.
/// </summary>
public static class SubscriptionTransitions
{
    private static readonly Dictionary<SubscriptionStatus, SubscriptionStatus[]> Allowed = new()
    {
        [SubscriptionStatus.Trialing] = [SubscriptionStatus.Active, SubscriptionStatus.Expired, SubscriptionStatus.Cancelled, SubscriptionStatus.Suspended],
        [SubscriptionStatus.Active] = [SubscriptionStatus.PastDue, SubscriptionStatus.Cancelled, SubscriptionStatus.Suspended, SubscriptionStatus.Expired],
        [SubscriptionStatus.PastDue] = [SubscriptionStatus.Active, SubscriptionStatus.Suspended, SubscriptionStatus.Expired, SubscriptionStatus.Cancelled],
        // Reactivar devuelve a la situación previa a la suspensión (en prueba, al día o en mora).
        [SubscriptionStatus.Suspended] = [SubscriptionStatus.Active, SubscriptionStatus.Trialing, SubscriptionStatus.PastDue, SubscriptionStatus.Cancelled],
        [SubscriptionStatus.Expired] = [SubscriptionStatus.Active, SubscriptionStatus.Cancelled],
        [SubscriptionStatus.Cancelled] = []
    };

    public static bool IsAllowed(SubscriptionStatus from, SubscriptionStatus to) => Allowed[from].Contains(to);

    public static IReadOnlyList<SubscriptionStatus> From(SubscriptionStatus from) => Allowed[from];

    /// <summary>Una suscripción cancelada no vuelve: se contrata una nueva. Todas las demás son «la suscripción vigente» del gimnasio.</summary>
    public static bool IsCurrent(SubscriptionStatus status) => status != SubscriptionStatus.Cancelled;
}

/// <summary>Un cambio de plan es una mejora si el precio por mes del plan nuevo es mayor, una baja si es menor y lateral si es igual.</summary>
public enum PlanChangeKind
{
    Upgrade = 1,
    Downgrade = 2,
    Lateral = 3
}

public enum PlanChangeStatus
{
    /// <summary>Ya está en vigor.</summary>
    Applied = 1,

    /// <summary>Entra en vigor al terminar el período actual.</summary>
    Scheduled = 2,

    /// <summary>Se reemplazó por otro cambio antes de entrar en vigor.</summary>
    Superseded = 3
}

/// <summary>Qué le toca hacer al proceso automático de vencimientos con una suscripción en un día dado.</summary>
public enum LifecycleAction
{
    None = 0,
    ExpireTrial = 1,
    RenewFree = 2,
    CancelAtPeriodEnd = 3,
    MarkPastDue = 4,
    ExpireGrace = 5
}

/// <summary>Reglas puras del proceso de vencimientos (testeables sin base de datos): dado el estado y el día local, qué corresponde.</summary>
public static class SubscriptionLifecycle
{
    public static LifecycleAction NextAction(Subscription s, DateOnly today)
    {
        switch (s.Status)
        {
            case SubscriptionStatus.Trialing when today >= s.CurrentPeriodEnd:
                return s.CancelAtPeriodEnd ? LifecycleAction.CancelAtPeriodEnd : LifecycleAction.ExpireTrial;
            case SubscriptionStatus.Active when today >= s.CurrentPeriodEnd:
                if (s.CancelAtPeriodEnd)
                {
                    return LifecycleAction.CancelAtPeriodEnd;
                }

                // Un plan de precio 0 no se cobra: su período se renueva solo. Uno de pago pasa a mora hasta que se confirme el pago.
                return s.IsFree ? LifecycleAction.RenewFree : LifecycleAction.MarkPastDue;
            case SubscriptionStatus.PastDue when s.GraceEndsOn is { } grace && today >= grace:
                return LifecycleAction.ExpireGrace;
            default:
                return LifecycleAction.None;
        }
    }
}

/// <summary>Condiciones que se copian del plan al contratar: la suscripción no vuelve a consultar el precio del plan.</summary>
public sealed record PlanSnapshot(
    Guid PlanId, Guid PlanVersionId, int VersionNumber, string Code, string Name,
    string Currency, BillingPeriod Period, decimal Price, int TrialDays, int? GraceDays);

/// <summary>
/// Suscripción comercial: el contrato de uso de FIT360 de un gimnasio (no confundir con las membresías que el gimnasio vende a sus
/// alumnos). Guarda el estado actual y una copia de las condiciones contratadas; el historial vive en <see cref="SubscriptionEvent"/>.
/// Los períodos son fechas locales del gimnasio, con el fin exclusivo: el período siguiente empieza el día que termina el anterior.
/// </summary>
public sealed class Subscription : IAuditable
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid PlanId { get; private set; }
    public Guid PlanVersionId { get; private set; }
    public int PlanVersionNumber { get; private set; }
    public string PlanCode { get; private set; } = string.Empty;
    public string PlanName { get; private set; } = string.Empty;

    /// <summary>Precio acordado, copiado al contratar. Un cambio de precio del plan no lo toca.</summary>
    public decimal Price { get; private set; }

    public string Currency { get; private set; } = string.Empty;
    public BillingPeriod Period { get; private set; }
    public SubscriptionStatus Status { get; private set; }
    public DateTimeOffset StartedAt { get; init; }
    public DateOnly CurrentPeriodStart { get; private set; }

    /// <summary>Fin exclusivo del período actual: el día en que vence y se debe el siguiente cobro.</summary>
    public DateOnly CurrentPeriodEnd { get; private set; }

    public DateOnly? TrialEndsOn { get; private set; }
    public DateOnly NextDueOn { get; private set; }
    public DateOnly? GraceEndsOn { get; private set; }
    public bool CancelAtPeriodEnd { get; private set; }
    public DateTimeOffset? CancelledAt { get; private set; }
    public string? CancelReason { get; private set; }
    public DateTimeOffset? SuspendedAt { get; private set; }
    public string? SuspensionReason { get; private set; }
    public SubscriptionStatus? StatusBeforeSuspension { get; private set; }

    /// <summary>Plan al que pasará la suscripción al terminar el período (una baja de plan programada). Nulo si no hay cambio pendiente.</summary>
    public Guid? PendingPlanId { get; private set; }

    public Guid? PendingPlanVersionId { get; private set; }

    /// <summary>Motivo de una cancelación programada para el fin del período (la suscripción sigue vigente hasta entonces).</summary>
    public string? ScheduledCancelReason { get; private set; }
    public DateTimeOffset CreatedAt { get; init; }
    public Guid? CreatedBy { get; init; }
    public DateTimeOffset? UpdatedAt { get; private set; }
    public Guid? UpdatedBy { get; private set; }

    public List<SubscriptionEvent> Events { get; init; } = [];
    public List<SubscriptionPlanChange> PlanChanges { get; init; } = [];

    public bool IsCurrent => SubscriptionTransitions.IsCurrent(Status);

    /// <summary>Un plan de precio 0 no genera cobros: su período se renueva solo (lo hace el proceso de vencimientos).</summary>
    public bool IsFree => Price == 0m;

    /// <summary>Contrata un plan: en prueba gratuita (si el plan la tiene) o activa desde hoy.</summary>
    public static Result<Subscription> Start(
        Guid tenantId, PlanSnapshot plan, bool trial, DateOnly today, Guid? actor, DateTimeOffset now, string? note)
    {
        if (trial && plan.TrialDays <= 0)
        {
            return SubscriptionErrors.PlanWithoutTrial;
        }

        var subscription = new Subscription
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            StartedAt = now,
            CreatedAt = now,
            CreatedBy = actor,
            Status = trial ? SubscriptionStatus.Trialing : SubscriptionStatus.Active,
            CurrentPeriodStart = today
        };
        subscription.ApplyPlan(plan);

        if (trial)
        {
            subscription.CurrentPeriodEnd = today.AddDays(plan.TrialDays);
            subscription.TrialEndsOn = subscription.CurrentPeriodEnd;
        }
        else
        {
            subscription.CurrentPeriodEnd = AddPeriod(today, plan.Period);
        }

        subscription.NextDueOn = subscription.CurrentPeriodEnd;
        subscription.Record(SubscriptionEventType.Created, actor, now, null, subscription.Status, Detail(note, $"{plan.Code} v{plan.VersionNumber}"));
        if (trial)
        {
            subscription.Record(SubscriptionEventType.TrialStarted, actor, now, null, SubscriptionStatus.Trialing, $"{plan.TrialDays} días");
        }

        return subscription;
    }

    /// <summary>Termina la prueba y empieza el primer período de pago hoy (la activación la autoriza el equipo de plataforma).</summary>
    public Result Activate(DateOnly today, Guid? actor, DateTimeOffset now, string? note)
    {
        if (Status != SubscriptionStatus.Trialing)
        {
            return InvalidTransition(SubscriptionStatus.Active);
        }

        var from = Status;
        Status = SubscriptionStatus.Active;
        CurrentPeriodStart = today;
        CurrentPeriodEnd = AddPeriod(today, Period);
        NextDueOn = CurrentPeriodEnd;
        TrialEndsOn = null;
        GraceEndsOn = null;
        Record(SubscriptionEventType.Activated, actor, now, from, Status, note);
        Touch(actor, now);
        return Result.Success();
    }

    public Result Suspend(string reason, Guid? actor, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return Result.Failure(SubscriptionErrors.ReasonRequired);
        }

        if (!SubscriptionTransitions.IsAllowed(Status, SubscriptionStatus.Suspended))
        {
            return InvalidTransition(SubscriptionStatus.Suspended);
        }

        var from = Status;
        StatusBeforeSuspension = from;
        Status = SubscriptionStatus.Suspended;
        SuspendedAt = now;
        SuspensionReason = reason.Trim();
        Record(SubscriptionEventType.Suspended, actor, now, from, Status, SuspensionReason);
        Touch(actor, now);
        return Result.Success();
    }

    /// <summary>
    /// Levanta la suspensión comercial y vuelve a la situación previa si el período sigue vigente; si ya terminó, queda en mora con un
    /// plazo de gracia nuevo para que el cliente pueda regularizar.
    /// </summary>
    public Result Reactivate(DateOnly today, int graceDays, Guid? actor, DateTimeOffset now, string? note)
    {
        if (Status != SubscriptionStatus.Suspended || StatusBeforeSuspension is not { } before)
        {
            return InvalidTransition(SubscriptionStatus.Active);
        }

        var from = Status;
        if (CurrentPeriodEnd > today)
        {
            Status = before;
        }
        else
        {
            Status = SubscriptionStatus.PastDue;
            GraceEndsOn = today.AddDays(graceDays);
        }

        StatusBeforeSuspension = null;
        SuspendedAt = null;
        SuspensionReason = null;
        Record(SubscriptionEventType.Reactivated, actor, now, from, Status, note);
        Touch(actor, now);
        return Result.Success();
    }

    /// <summary>Vencimiento sin pago: pasa a mora y el plazo de gracia corre desde el fin del período.</summary>
    public Result MarkPastDue(int graceDays, Guid? actor, DateTimeOffset now)
    {
        if (Status != SubscriptionStatus.Active)
        {
            return InvalidTransition(SubscriptionStatus.PastDue);
        }

        var from = Status;
        Status = SubscriptionStatus.PastDue;
        GraceEndsOn = CurrentPeriodEnd.AddDays(graceDays);
        Record(SubscriptionEventType.PastDue, actor, now, from, Status, $"Gracia hasta {GraceEndsOn:yyyy-MM-dd}");
        Touch(actor, now);
        return Result.Success();
    }

    /// <summary>Se acabó el plazo (prueba sin convertir o mora sin regularizar). No se borra nada: solo cambia el modo de acceso.</summary>
    public Result Expire(Guid? actor, DateTimeOffset now)
    {
        if (!SubscriptionTransitions.IsAllowed(Status, SubscriptionStatus.Expired))
        {
            return InvalidTransition(SubscriptionStatus.Expired);
        }

        var from = Status;
        Status = SubscriptionStatus.Expired;
        Record(SubscriptionEventType.Expired, actor, now, from, Status, null);
        Touch(actor, now);
        return Result.Success();
    }

    /// <summary>Cancelación inmediata. Es definitiva: para volver a contratar se crea una suscripción nueva.</summary>
    public Result Cancel(string reason, Guid? actor, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return Result.Failure(SubscriptionErrors.ReasonRequired);
        }

        if (!SubscriptionTransitions.IsAllowed(Status, SubscriptionStatus.Cancelled))
        {
            return InvalidTransition(SubscriptionStatus.Cancelled);
        }

        var from = Status;
        Status = SubscriptionStatus.Cancelled;
        CancelledAt = now;
        CancelReason = reason.Trim();
        CancelAtPeriodEnd = false;
        Record(SubscriptionEventType.Cancelled, actor, now, from, Status, CancelReason);
        Touch(actor, now);
        return Result.Success();
    }

    /// <summary>
    /// Renueva un período. Empieza donde terminó el anterior (sin huecos ni solapes); si la suscripción ya había vencido, o el período resultante
    /// ya pasó por completo, empieza hoy: no se cobra por días que el cliente no tuvo. No hay prorrateo. Si había una baja de plan programada,
    /// entra en vigor aquí y el período nuevo ya se cobra con el precio del plan nuevo.
    /// </summary>
    public Result Renew(PlanSnapshot? pendingPlan, DateOnly today, int windowDays, Guid? actor, DateTimeOffset now, string? note, bool automatic = false)
    {
        if (Status is not (SubscriptionStatus.Active or SubscriptionStatus.PastDue or SubscriptionStatus.Expired))
        {
            return InvalidTransition(SubscriptionStatus.Active);
        }

        if (CancelAtPeriodEnd)
        {
            return Result.Failure(Error.Conflict("subscriptions.cancellation_scheduled", "La suscripción tiene una cancelación programada: retírela antes de renovar."));
        }

        // Renovar con demasiada anticipación acumularía períodos pagados por adelantado sin control.
        if (Status == SubscriptionStatus.Active && CurrentPeriodEnd > today.AddDays(windowDays))
        {
            return Result.Failure(Error.Conflict("subscriptions.renewal_too_early", $"Solo se puede renovar dentro de los {windowDays} días previos al vencimiento ({CurrentPeriodEnd:yyyy-MM-dd})."));
        }

        var from = Status;
        if (pendingPlan is not null)
        {
            ApplyPlan(pendingPlan);
            PendingPlanId = null;
            PendingPlanVersionId = null;
            foreach (var scheduled in PlanChanges.Where(c => c.Status == PlanChangeStatus.Scheduled))
            {
                scheduled.MarkApplied(now);
            }

            Record(SubscriptionEventType.PlanChanged, actor, now, from, from, $"{pendingPlan.Code} v{pendingPlan.VersionNumber} · entra en vigor al renovar");
        }

        var start = Status == SubscriptionStatus.Expired ? today : CurrentPeriodEnd;
        if (AddPeriod(start, Period) <= today)
        {
            start = today;
        }

        CurrentPeriodStart = start;
        CurrentPeriodEnd = AddPeriod(start, Period);
        NextDueOn = CurrentPeriodEnd;
        TrialEndsOn = null;
        GraceEndsOn = null;
        Status = SubscriptionStatus.Active;
        Record(SubscriptionEventType.Renewed, actor, now, from, Status, Detail(note, automatic ? "Renovación automática (plan gratuito)" : $"Período {CurrentPeriodStart:yyyy-MM-dd} a {CurrentPeriodEnd:yyyy-MM-dd}"));
        Touch(actor, now);
        return Result.Success();
    }

    /// <summary>
    /// Cambia de plan. Una mejora (o un cambio lateral) entra en vigor de inmediato y no cambia el período ni genera cobro ni crédito. Una baja se
    /// programa para el fin del período, para que el cliente use lo que ya pagó. En prueba, siempre es inmediato.
    /// </summary>
    public Result<SubscriptionPlanChange> ChangePlan(PlanSnapshot target, DateOnly today, Guid? actor, DateTimeOffset now, string? note)
    {
        if (Status is not (SubscriptionStatus.Trialing or SubscriptionStatus.Active or SubscriptionStatus.PastDue))
        {
            return Error.Conflict("subscriptions.invalid_state", $"Una suscripción {Status} no admite cambio de plan.");
        }

        if (target.PlanVersionId == PlanVersionId || (target.PlanId == PlanId && target.VersionNumber == PlanVersionNumber))
        {
            return Error.Conflict("subscriptions.same_plan", "La suscripción ya tiene ese plan.");
        }

        var kind = Classify(MonthlyPrice(Price, Period), MonthlyPrice(target.Price, target.Period));
        var immediate = Status == SubscriptionStatus.Trialing || kind != PlanChangeKind.Downgrade;

        // Un cambio programado anterior pierde vigencia: manda el último.
        foreach (var pending in PlanChanges.Where(c => c.Status == PlanChangeStatus.Scheduled))
        {
            pending.Supersede();
        }

        var change = new SubscriptionPlanChange
        {
            Id = Guid.CreateVersion7(),
            SubscriptionId = Id,
            TenantId = TenantId,
            FromPlanVersionId = PlanVersionId,
            ToPlanId = target.PlanId,
            ToPlanVersionId = target.PlanVersionId,
            Kind = kind,
            Status = immediate ? PlanChangeStatus.Applied : PlanChangeStatus.Scheduled,
            RequestedAt = now,
            EffectiveOn = immediate ? today : CurrentPeriodEnd,
            AppliedAt = immediate ? now : null,
            RequestedBy = actor,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim()
        };
        PlanChanges.Add(change);

        var detail = Detail(note, $"{PlanCode} v{PlanVersionNumber} → {target.Code} v{target.VersionNumber} ({kind})");
        if (immediate)
        {
            var current = Status;
            ApplyPlan(target);
            PendingPlanId = null;
            PendingPlanVersionId = null;
            Record(SubscriptionEventType.PlanChanged, actor, now, current, current, detail);
        }
        else
        {
            PendingPlanId = target.PlanId;
            PendingPlanVersionId = target.PlanVersionId;
            Record(SubscriptionEventType.PlanChangeScheduled, actor, now, Status, Status, $"{detail} · entra en vigor el {CurrentPeriodEnd:yyyy-MM-dd}");
        }

        Touch(actor, now);
        return change;
    }

    /// <summary>Programa la cancelación para el fin del período: sigue vigente y con todo su acceso hasta entonces.</summary>
    public Result ScheduleCancellation(string reason, Guid? actor, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return Result.Failure(SubscriptionErrors.ReasonRequired);
        }

        if (Status is not (SubscriptionStatus.Trialing or SubscriptionStatus.Active))
        {
            return Result.Failure(Error.Conflict("subscriptions.invalid_state", $"Una suscripción {Status} no admite cancelación al final del período: cancélela de inmediato."));
        }

        if (CancelAtPeriodEnd)
        {
            return Result.Failure(Error.Conflict("subscriptions.cancellation_already_scheduled", "La cancelación ya está programada."));
        }

        CancelAtPeriodEnd = true;
        ScheduledCancelReason = reason.Trim();
        Record(SubscriptionEventType.CancellationScheduled, actor, now, Status, Status, $"{ScheduledCancelReason} · se cancela el {CurrentPeriodEnd:yyyy-MM-dd}");
        Touch(actor, now);
        return Result.Success();
    }

    public Result ResumeCancellation(Guid? actor, DateTimeOffset now, string? note)
    {
        if (!CancelAtPeriodEnd)
        {
            return Result.Failure(Error.Conflict("subscriptions.no_cancellation_scheduled", "La suscripción no tiene una cancelación programada."));
        }

        CancelAtPeriodEnd = false;
        ScheduledCancelReason = null;
        Record(SubscriptionEventType.CancellationResumed, actor, now, Status, Status, note);
        Touch(actor, now);
        return Result.Success();
    }

    /// <summary>La cancelación programada llegó a su fecha: se cancela con el motivo que se dio al programarla.</summary>
    public Result CompleteScheduledCancellation(Guid? actor, DateTimeOffset now) =>
        Cancel(ScheduledCancelReason ?? "Cancelación al final del período", actor, now);

    /// <summary>
    /// Prórroga administrativa: regala días. En prueba alarga la prueba; activa, alarga el período vigente; en mora, alarga la gracia. Exige motivo y
    /// queda en el historial y en la auditoría.
    /// </summary>
    public Result Extend(int days, string reason, Guid? actor, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return Result.Failure(SubscriptionErrors.ReasonRequired);
        }

        if (days is < 1 or > MaxExtensionDays)
        {
            return Result.Failure(Error.Validation("subscriptions.extension_days_invalid", $"La prórroga debe ser de 1 a {MaxExtensionDays} días."));
        }

        switch (Status)
        {
            case SubscriptionStatus.Trialing:
                CurrentPeriodEnd = CurrentPeriodEnd.AddDays(days);
                TrialEndsOn = CurrentPeriodEnd;
                NextDueOn = CurrentPeriodEnd;
                break;
            case SubscriptionStatus.Active:
                CurrentPeriodEnd = CurrentPeriodEnd.AddDays(days);
                NextDueOn = CurrentPeriodEnd;
                break;
            case SubscriptionStatus.PastDue when GraceEndsOn is { } grace:
                GraceEndsOn = grace.AddDays(days);
                break;
            default:
                return Result.Failure(Error.Conflict("subscriptions.invalid_state", $"Una suscripción {Status} no admite prórroga."));
        }

        Record(SubscriptionEventType.Extended, actor, now, Status, Status, $"{days} días · {reason.Trim()}");
        Touch(actor, now);
        return Result.Success();
    }

    public const int MaxExtensionDays = 90;

    /// <summary>Precio normalizado a un mes para comparar planes de periodicidad distinta.</summary>
    public static decimal MonthlyPrice(decimal price, BillingPeriod period) => price / BillingPeriods.Months(period);

    public static PlanChangeKind Classify(decimal currentMonthly, decimal targetMonthly) =>
        targetMonthly > currentMonthly ? PlanChangeKind.Upgrade : targetMonthly < currentMonthly ? PlanChangeKind.Downgrade : PlanChangeKind.Lateral;

    /// <summary>Las condiciones que tiene hoy contratadas, como se copiarían de un plan (sin prueba ni gracia: no aplican a una suscripción en curso).</summary>
    public PlanSnapshot ToSnapshot() => new(PlanId, PlanVersionId, PlanVersionNumber, PlanCode, PlanName, Currency, Period, Price, 0, null);

    /// <summary>Fin exclusivo de un período que empieza en <paramref name="start"/> (meses calendario, con tope al último día del mes).</summary>
    public static DateOnly AddPeriod(DateOnly start, BillingPeriod period) => start.AddMonths(BillingPeriods.Months(period));

    private void ApplyPlan(PlanSnapshot plan)
    {
        PlanId = plan.PlanId;
        PlanVersionId = plan.PlanVersionId;
        PlanVersionNumber = plan.VersionNumber;
        PlanCode = plan.Code;
        PlanName = plan.Name;
        Price = plan.Price;
        Currency = plan.Currency;
        Period = plan.Period;
    }

    private void Touch(Guid? actor, DateTimeOffset now)
    {
        UpdatedAt = now;
        UpdatedBy = actor;
    }

    /// <summary>Deja en el historial de la suscripción un hecho de cobro (factura emitida, pago confirmado o fallido, anulación, reembolso).</summary>
    public void RecordBilling(SubscriptionEventType type, Guid? actor, DateTimeOffset now, string? detail) => Record(type, actor, now, Status, Status, detail);

    private void Record(SubscriptionEventType type, Guid? actor, DateTimeOffset now, SubscriptionStatus? from, SubscriptionStatus? to, string? detail) =>
        Events.Add(new SubscriptionEvent
        {
            Id = Guid.CreateVersion7(),
            SubscriptionId = Id,
            TenantId = TenantId,
            Type = type,
            OccurredAt = now,
            ActorId = actor,
            FromStatus = from,
            ToStatus = to,
            Detail = string.IsNullOrWhiteSpace(detail) ? null : detail.Trim()
        });

    private Result InvalidTransition(SubscriptionStatus to) =>
        Result.Failure(Error.Conflict("subscriptions.invalid_transition", $"Una suscripción {Status} no puede pasar a {to}."));

    private static string? Detail(string? note, string plan) => string.IsNullOrWhiteSpace(note) ? plan : $"{plan} · {note.Trim()}";
}

/// <summary>Hecho del historial de una suscripción. Es inmutable: lo ocurrido no se reescribe.</summary>
public sealed class SubscriptionEvent
{
    public Guid Id { get; init; }
    public Guid SubscriptionId { get; init; }
    public Guid TenantId { get; init; }
    public SubscriptionEventType Type { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
    public Guid? ActorId { get; init; }
    public SubscriptionStatus? FromStatus { get; init; }
    public SubscriptionStatus? ToStatus { get; init; }
    public string? Detail { get; init; }
}

/// <summary>Cambio de plan pedido o aplicado a una suscripción: de qué versión a cuál, de qué tipo, cuándo entra en vigor y quién lo hizo.</summary>
public sealed class SubscriptionPlanChange : IAuditable
{
    public Guid Id { get; init; }
    public Guid SubscriptionId { get; init; }
    public Guid TenantId { get; init; }
    public Guid FromPlanVersionId { get; init; }
    public Guid ToPlanId { get; init; }
    public Guid ToPlanVersionId { get; init; }
    public PlanChangeKind Kind { get; init; }
    public PlanChangeStatus Status { get; internal set; }
    public DateTimeOffset RequestedAt { get; init; }
    public DateOnly EffectiveOn { get; init; }
    public DateTimeOffset? AppliedAt { get; internal set; }
    public Guid? RequestedBy { get; init; }
    public string? Note { get; init; }

    internal void MarkApplied(DateTimeOffset now)
    {
        Status = PlanChangeStatus.Applied;
        AppliedAt = now;
    }

    internal void Supersede() => Status = PlanChangeStatus.Superseded;
}

public static class SubscriptionErrors
{
    public static readonly Error PlanWithoutTrial = Error.Validation("subscriptions.plan_without_trial", "Ese plan no tiene prueba gratuita.");
    public static readonly Error ReasonRequired = Error.Validation("subscriptions.reason_required", "Indique el motivo.");
}
