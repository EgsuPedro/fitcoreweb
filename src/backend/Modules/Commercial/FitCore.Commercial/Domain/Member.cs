using FitCore.SharedKernel;

namespace FitCore.Commercial.Domain;

public enum MemberStatus
{
    Active = 1,
    Inactive = 2
}

/// <summary>Datos personales de un alumno que edita quien tiene permiso (todo salvo identidad de tenant, código y sucursal).</summary>
public sealed record MemberProfile(
    string FirstName,
    string LastName,
    IdentificationType IdType,
    string IdNumber,
    DateOnly? BirthDate,
    string? Email,
    string? Phone,
    string? Address,
    string? PhotoUrl,
    string? EmergencyContactName,
    string? EmergencyContactPhone,
    string? EmergencyContactRelationship,
    string? Notes);

/// <summary>Alumno de un gimnasio. Pertenece a la sucursal donde se inscribió y nunca cambia de tenant.</summary>
public sealed class Member : Entity<Guid>, ITenantOwned, IAuditable
{
    private Member(Guid id)
        : base(id)
    {
    }

    public Guid TenantId { get; private set; }
    public Guid BranchId { get; private set; }
    public string Code { get; private set; } = string.Empty;
    public string FirstName { get; private set; } = string.Empty;
    public string LastName { get; private set; } = string.Empty;
    public IdentificationType IdType { get; private set; }

    /// <summary>Número normalizado (sin espacios ni guiones, en mayúsculas).</summary>
    public string IdNumber { get; private set; } = string.Empty;

    public DateOnly? BirthDate { get; private set; }
    public string? Email { get; private set; }
    public string? Phone { get; private set; }
    public string? Address { get; private set; }
    public string? PhotoUrl { get; private set; }
    public string? EmergencyContactName { get; private set; }
    public string? EmergencyContactPhone { get; private set; }
    public string? EmergencyContactRelationship { get; private set; }
    public DateOnly EnrolledOn { get; private set; }
    public MemberStatus Status { get; private set; } = MemberStatus.Active;
    public string? Notes { get; private set; }

    /// <summary>Huella (SHA-256) del token del código QR vigente. El token nunca se guarda.</summary>
    public string? QrTokenHash { get; private set; }
    public DateTimeOffset? QrTokenIssuedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public Guid? CreatedBy { get; private set; }
    public DateTimeOffset? UpdatedAt { get; private set; }
    public Guid? UpdatedBy { get; private set; }

    public string FullName => $"{FirstName} {LastName}";

    public bool IsActive => Status == MemberStatus.Active;

    public static Member Create(
        Guid tenantId,
        Guid branchId,
        string code,
        MemberProfile profile,
        DateOnly enrolledOn,
        Guid? createdBy,
        DateTimeOffset now)
    {
        var member = new Member(Guid.CreateVersion7())
        {
            TenantId = tenantId,
            BranchId = branchId,
            Code = code,
            EnrolledOn = enrolledOn,
            CreatedAt = now,
            CreatedBy = createdBy
        };
        member.Apply(profile);
        return member;
    }

    public void Update(MemberProfile profile, Guid? updatedBy, DateTimeOffset now)
    {
        Apply(profile);
        UpdatedAt = now;
        UpdatedBy = updatedBy;
    }

    /// <summary>Emite un código QR nuevo: el anterior deja de servir.</summary>
    public void IssueQrToken(string tokenHash, Guid? updatedBy, DateTimeOffset now)
    {
        QrTokenHash = tokenHash;
        QrTokenIssuedAt = now;
        UpdatedAt = now;
        UpdatedBy = updatedBy;
    }

    public void SetStatus(MemberStatus status, Guid? updatedBy, DateTimeOffset now)
    {
        Status = status;
        UpdatedAt = now;
        UpdatedBy = updatedBy;
    }

    private void Apply(MemberProfile profile)
    {
        FirstName = profile.FirstName.Trim();
        LastName = profile.LastName.Trim();
        IdType = profile.IdType;
        IdNumber = IdentificationValidator.Normalize(profile.IdNumber);
        BirthDate = profile.BirthDate;
        Email = Clean(profile.Email)?.ToLowerInvariant();
        Phone = Clean(profile.Phone);
        Address = Clean(profile.Address);
        PhotoUrl = Clean(profile.PhotoUrl);
        EmergencyContactName = Clean(profile.EmergencyContactName);
        EmergencyContactPhone = Clean(profile.EmergencyContactPhone);
        EmergencyContactRelationship = Clean(profile.EmergencyContactRelationship);
        Notes = Clean(profile.Notes);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
