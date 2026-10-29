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
    Cancelled = 8
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

/// <summary>Condiciones que se copian del plan al contratar: la suscripción no vuelve a consultar el precio del plan.</summary>
public sealed record PlanSnapshot(
    Guid PlanId, Guid PlanVersionId, int VersionNumber, string Code, string Name,
    string Currency, BillingPeriod Period, decimal Price, int TrialDays, int? GraceDays);

/// <summary>
/// Suscripción comercial: el contrato de uso de FitCore 360 de un gimnasio (no confundir con las membresías que el gimnasio vende a sus
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
    public DateTimeOffset CreatedAt { get; init; }
    public Guid? CreatedBy { get; init; }
    public DateTimeOffset? UpdatedAt { get; private set; }
    public Guid? UpdatedBy { get; private set; }

    public List<SubscriptionEvent> Events { get; init; } = [];

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

public static class SubscriptionErrors
{
    public static readonly Error PlanWithoutTrial = Error.Validation("subscriptions.plan_without_trial", "Ese plan no tiene prueba gratuita.");
    public static readonly Error ReasonRequired = Error.Validation("subscriptions.reason_required", "Indique el motivo.");
}
