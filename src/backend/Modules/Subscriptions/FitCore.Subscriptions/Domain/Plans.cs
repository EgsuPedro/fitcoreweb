using System.Text.RegularExpressions;
using FitCore.SharedKernel;

namespace FitCore.Subscriptions.Domain;

public enum BillingPeriod
{
    Monthly = 1,
    Quarterly = 2,
    Yearly = 3
}

public static class BillingPeriods
{
    /// <summary>Meses calendario que cubre un período de cobro.</summary>
    public static int Months(BillingPeriod period) => period switch
    {
        BillingPeriod.Monthly => 1,
        BillingPeriod.Quarterly => 3,
        BillingPeriod.Yearly => 12,
        _ => throw new ArgumentOutOfRangeException(nameof(period), period, "Periodicidad no soportada.")
    };
}

/// <summary>
/// Condiciones comerciales de un plan en un momento dado. Es lo que una suscripción contrata y copia: cambiar cualquiera de estos valores
/// en el plan crea una versión nueva, nunca altera una anterior.
/// </summary>
public sealed record PlanTerms(
    string Currency,
    BillingPeriod Period,
    decimal Price,
    int TrialDays,
    int? GraceDays,
    IReadOnlyCollection<string> Features,
    IReadOnlyDictionary<string, long?> Limits)
{
    /// <summary>¿Son las mismas condiciones (sin importar el orden de funciones y límites)?</summary>
    public bool SameAs(PlanTerms other) =>
        Currency == other.Currency
        && Period == other.Period
        && Price == other.Price
        && TrialDays == other.TrialDays
        && GraceDays == other.GraceDays
        && Features.ToHashSet(StringComparer.Ordinal).SetEquals(other.Features)
        && Limits.Count == other.Limits.Count
        && Limits.All(l => other.Limits.TryGetValue(l.Key, out var value) && value == l.Value);
}

/// <summary>
/// Plan comercial de FIT360 (lo que la plataforma le vende a un gimnasio; no tiene relación con las membresías que el gimnasio
/// vende a sus alumnos). El código es único y estable. Un plan nunca se borra: se desactiva, y las condiciones viven en
/// <see cref="PlanVersion"/> para que los cambios de precio no alteren a quien ya contrató.
/// </summary>
public sealed partial class Plan : IAuditable
{
    public const int MaxPrice = 1_000_000;

    public Guid Id { get; init; }
    public string Code { get; init; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }

    /// <summary>Reglas de uso en texto libre (políticas de uso justo, restricciones). No se usa para decidir autorizaciones.</summary>
    public string? Rules { get; private set; }

    /// <summary>Un plan inactivo no se puede asignar a suscripciones nuevas; las existentes siguen con él.</summary>
    public bool IsActive { get; private set; }

    /// <summary>
    /// Se ofrece en el sitio comercial. Lo decide el equipo de plataforma de forma explícita: ningún plan activo se publica solo.
    /// Un plan público que se desactiva deja de mostrarse sin perder la marca.
    /// </summary>
    public bool IsPublic { get; private set; }

    public DateTimeOffset CreatedAt { get; init; }
    public Guid? CreatedBy { get; init; }
    public DateTimeOffset? UpdatedAt { get; private set; }
    public Guid? UpdatedBy { get; private set; }

    public List<PlanVersion> Versions { get; init; } = [];

    /// <summary>La versión vigente es siempre la de mayor número (las anteriores se conservan intactas).</summary>
    public PlanVersion Current => Versions.OrderByDescending(v => v.Number).First();

    public static bool IsValidCode(string code) => CodePattern().IsMatch(code);

    public static Plan Create(
        string code, string name, string? description, string? rules, bool isActive, PlanTerms terms, string? note, Guid? actor, DateTimeOffset now)
    {
        var plan = new Plan
        {
            Id = Guid.CreateVersion7(),
            Code = code.Trim().ToLowerInvariant(),
            Name = name.Trim(),
            Description = Clean(description),
            Rules = Clean(rules),
            IsActive = isActive,
            CreatedAt = now,
            CreatedBy = actor
        };
        plan.Versions.Add(PlanVersion.Create(plan.Id, 1, terms, note, actor, now));
        return plan;
    }

    /// <summary>Datos descriptivos: se editan sin crear versión porque no cambian lo contratado.</summary>
    public void UpdateDescriptive(string name, string? description, string? rules, Guid? actor, DateTimeOffset now)
    {
        Name = name.Trim();
        Description = Clean(description);
        Rules = Clean(rules);
        Touch(actor, now);
    }

    public void SetActive(bool active, Guid? actor, DateTimeOffset now)
    {
        IsActive = active;
        Touch(actor, now);
    }

    public void SetPublic(bool isPublic, Guid? actor, DateTimeOffset now)
    {
        IsPublic = isPublic;
        Touch(actor, now);
    }

    /// <summary>Agrega una versión nueva con las condiciones dadas. Quien la pide ya verificó que cambian.</summary>
    public PlanVersion AddVersion(PlanTerms terms, string? note, Guid? actor, DateTimeOffset now)
    {
        var version = PlanVersion.Create(Id, Current.Number + 1, terms, note, actor, now);
        Versions.Add(version);
        Touch(actor, now);
        return version;
    }

    private void Touch(Guid? actor, DateTimeOffset now)
    {
        UpdatedAt = now;
        UpdatedBy = actor;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{1,38}[a-z0-9]$")]
    private static partial Regex CodePattern();
}

/// <summary>Condiciones comerciales inmutables de un plan. Una suscripción apunta a una versión concreta.</summary>
public sealed class PlanVersion : IAuditable
{
    public Guid Id { get; init; }
    public Guid PlanId { get; init; }
    public int Number { get; init; }
    public string Currency { get; init; } = string.Empty;
    public BillingPeriod Period { get; init; }
    public decimal Price { get; init; }
    public int TrialDays { get; init; }

    /// <summary>Días de gracia tras un vencimiento sin pago. Nulo = el valor de plataforma.</summary>
    public int? GraceDays { get; init; }

    public string? ChangeNote { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public Guid? CreatedBy { get; init; }

    public List<PlanFeature> Features { get; init; } = [];
    public List<PlanLimit> Limits { get; init; } = [];

    public PlanTerms ToTerms() =>
        new(Currency, Period, Price, TrialDays, GraceDays, [.. Features.Select(f => f.Code)], Limits.ToDictionary(l => l.Code, l => l.Value));

    internal static PlanVersion Create(Guid planId, int number, PlanTerms terms, string? note, Guid? actor, DateTimeOffset now)
    {
        var id = Guid.CreateVersion7();
        return new PlanVersion
        {
            Id = id,
            PlanId = planId,
            Number = number,
            Currency = terms.Currency,
            Period = terms.Period,
            Price = terms.Price,
            TrialDays = terms.TrialDays,
            GraceDays = terms.GraceDays,
            ChangeNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            CreatedAt = now,
            CreatedBy = actor,
            Features = [.. terms.Features.Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal).Select(c => new PlanFeature { PlanVersionId = id, Code = c })],
            Limits = [.. terms.Limits.OrderBy(l => l.Key, StringComparer.Ordinal).Select(l => new PlanLimit { PlanVersionId = id, Code = l.Key, Value = l.Value })]
        };
    }
}

/// <summary>Funcionalidad habilitada en una versión de plan (código del catálogo <see cref="Capabilities.Features"/>).</summary>
public sealed class PlanFeature : IAuditable
{
    public Guid PlanVersionId { get; init; }
    public string Code { get; init; } = string.Empty;
}

/// <summary>Límite cuantitativo de una versión de plan. <c>Value</c> nulo = sin límite.</summary>
public sealed class PlanLimit : IAuditable
{
    public Guid PlanVersionId { get; init; }
    public string Code { get; init; } = string.Empty;
    public long? Value { get; init; }
}
