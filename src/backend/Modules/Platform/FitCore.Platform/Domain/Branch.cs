using FitCore.SharedKernel;

namespace FitCore.Platform.Domain;

/// <summary>Sucursal de un gimnasio. Cada sucursal tiene su zona horaria y su código de establecimiento SRI.</summary>
public sealed class Branch : Entity<Guid>, ITenantOwned, IAuditable
{
    private Branch(Guid id)
        : base(id)
    {
    }

    public Guid TenantId { get; private set; }
    public string Code { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public string TimeZone { get; private set; } = Tenant.DefaultTimeZone;

    /// <summary>Código de establecimiento SRI (3 dígitos). Se usará en la facturación electrónica (Fase 8).</summary>
    public string? SriEstablishmentCode { get; private set; }

    public string? Address { get; private set; }
    public bool IsActive { get; private set; } = true;
    public DateTimeOffset CreatedAt { get; private set; }

    public static Branch Create(
        Guid tenantId,
        string code,
        string name,
        string timeZone,
        string? sriEstablishmentCode,
        string? address,
        DateTimeOffset now) =>
        new(Guid.CreateVersion7())
        {
            TenantId = tenantId,
            Code = code.Trim().ToUpperInvariant(),
            Name = name.Trim(),
            TimeZone = timeZone,
            SriEstablishmentCode = sriEstablishmentCode,
            Address = address?.Trim(),
            CreatedAt = now
        };

    public void Update(string name, string timeZone, string? sriEstablishmentCode, string? address, bool isActive)
    {
        Name = name.Trim();
        TimeZone = timeZone;
        SriEstablishmentCode = sriEstablishmentCode;
        Address = address?.Trim();
        IsActive = isActive;
    }
}
