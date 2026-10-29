using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FitCore.Subscriptions.Persistence;

/// <summary>
/// Derechos vigentes de un gimnasio: lo que su suscripción actual le permite hoy. La base la mantiene sola (disparador sobre
/// <c>subscriptions</c>), en la misma transacción que cualquier cambio de la suscripción; la leen los disparadores de límites y el
/// servicio de capacidades. Nadie la escribe desde la aplicación.
/// </summary>
internal sealed class TenantEntitlement
{
    public Guid TenantId { get; init; }
    public Guid SubscriptionId { get; init; }
    public string PlanCode { get; init; } = string.Empty;
    public string PlanName { get; init; } = string.Empty;
    public int PlanVersion { get; init; }
    public string Status { get; init; } = string.Empty;

    /// <summary>Full, Grace o Restricted (ver <c>AccessMode</c>).</summary>
    public string AccessMode { get; init; } = string.Empty;

    public string[] Features { get; init; } = [];

    /// <summary>Límites por código como JSON; un valor nulo o ausente significa «sin límite».</summary>
    public string Limits { get; init; } = "{}";

    public DateTimeOffset UpdatedAt { get; init; }

    public long? LimitOf(string code)
    {
        using var document = System.Text.Json.JsonDocument.Parse(Limits);
        return document.RootElement.TryGetProperty(code, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.Number
            ? value.GetInt64()
            : null;
    }
}

internal sealed class TenantEntitlementConfiguration : IEntityTypeConfiguration<TenantEntitlement>
{
    public void Configure(EntityTypeBuilder<TenantEntitlement> b)
    {
        b.ToTable("tenant_entitlements", SubscriptionsDbContext.Schema);
        b.HasKey(x => x.TenantId);
        b.Property(x => x.TenantId).ValueGeneratedNever();
        b.Property(x => x.PlanCode).HasMaxLength(40).IsRequired();
        b.Property(x => x.PlanName).HasMaxLength(120).IsRequired();
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.Property(x => x.AccessMode).HasMaxLength(20).IsRequired();
        b.Property(x => x.Features).HasColumnType("text[]");
        b.Property(x => x.Limits).HasColumnType("jsonb").IsRequired();
        b.HasIndex(x => x.SubscriptionId);
    }
}

/// <summary>Sucursal (solo lectura): para medir el consumo del límite de sucursales.</summary>
internal sealed class BranchRef
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public bool IsActive { get; init; }
}

/// <summary>Usuario (solo lectura): para medir el consumo del límite de usuarios internos. Los alumnos con acceso al portal no cuentan.</summary>
internal sealed class UserRef
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public bool IsActive { get; init; }
    public string AccountKind { get; init; } = string.Empty;
}

/// <summary>Alumno (solo lectura): para medir el consumo del límite de alumnos registrados.</summary>
internal sealed class MemberRef
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
}
