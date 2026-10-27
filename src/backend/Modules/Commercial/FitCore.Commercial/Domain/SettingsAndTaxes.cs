using FitCore.SharedKernel;

namespace FitCore.Commercial.Domain;

public enum AccessPolicy
{
    /// <summary>Solo entra quien tiene la obligación saldada.</summary>
    RequireFullPayment = 1,

    /// <summary>Entra con saldo pendiente mientras la obligación no esté vencida.</summary>
    AllowUntilOverdue = 2,

    /// <summary>Entra si ha pagado al menos el porcentaje mínimo configurado.</summary>
    AllowWithMinimumPayment = 3
}

/// <summary>Políticas comerciales del gimnasio. Si no existe fila, se usan los valores por defecto.</summary>
public sealed class CommercialSettings : ITenantOwned, IAuditable
{
    public const AccessPolicy DefaultAccessPolicy = AccessPolicy.AllowUntilOverdue;
    public const int DefaultMinPaymentPercent = 50;
    public const int DefaultDueDays = 7;
    public const int DefaultMaxDiscountPercent = 30;

    public Guid TenantId { get; init; }
    public AccessPolicy AccessPolicy { get; private set; } = DefaultAccessPolicy;
    public int MinPaymentPercent { get; private set; } = DefaultMinPaymentPercent;
    public int DueDays { get; private set; } = DefaultDueDays;
    public int MaxDiscountPercent { get; private set; } = DefaultMaxDiscountPercent;
    /// <summary>Activa los avisos de WhatsApp por vencimiento. Apagado por defecto: nada se envía hasta que el gimnasio lo decide.</summary>
    public bool NotificationsEnabled { get; private set; }

    /// <summary>Teléfonos (formato internacional, separados por coma) del personal que recibe el aviso previo al vencimiento.</summary>
    public string StaffAlertPhones { get; private set; } = string.Empty;

    public DateTimeOffset? UpdatedAt { get; private set; }
    public Guid? UpdatedBy { get; private set; }

    public IReadOnlyList<string> StaffPhones => StaffAlertPhones.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public void UpdateNotifications(bool enabled, IEnumerable<string> staffPhones, Guid? updatedBy, DateTimeOffset now)
    {
        NotificationsEnabled = enabled;
        StaffAlertPhones = string.Join(',', staffPhones);
        UpdatedAt = now;
        UpdatedBy = updatedBy;
    }

    public static CommercialSettings CreateDefault(Guid tenantId) => new() { TenantId = tenantId };

    public void Update(AccessPolicy policy, int minPaymentPercent, int defaultDueDays, int maxDiscountPercent, Guid? updatedBy, DateTimeOffset now)
    {
        AccessPolicy = policy;
        MinPaymentPercent = minPaymentPercent;
        DueDays = defaultDueDays;
        MaxDiscountPercent = maxDiscountPercent;
        UpdatedAt = now;
        UpdatedBy = updatedBy;
    }
}

/// <summary>
/// Tarifa de impuesto con vigencia (ADR 0003). Es global: la administra el equipo de plataforma y cada obligación de pago
/// congela la tarifa aplicada, así que un cambio por decreto nunca altera comprobantes ya emitidos.
/// </summary>
public sealed class TaxRate : IAuditable
{
    public Guid Id { get; init; }
    public TaxKind Kind { get; init; }
    public decimal Percent { get; init; }
    public DateOnly ValidFrom { get; init; }
    public DateOnly? ValidUntil { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public Guid? CreatedBy { get; init; }

    public bool AppliesOn(DateOnly date) => date >= ValidFrom && (ValidUntil is null || date <= ValidUntil);

    /// <summary>Desglosa un precio que incluye impuesto en subtotal e impuesto (el impuesto es el residuo, para que sumen exacto).</summary>
    public static (decimal Subtotal, decimal Tax) Split(decimal priceWithTax, decimal percent)
    {
        var subtotal = Money.Round(priceWithTax / (1m + (percent / 100m)));
        return (subtotal, priceWithTax - subtotal);
    }
}
