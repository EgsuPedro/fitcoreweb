using FitCore.SharedKernel;

namespace FitCore.Subscriptions.Domain;

/// <summary>
/// Nota administrativa del equipo de plataforma sobre una organización cliente. Es solo para uso interno (el gimnasio no la ve) y debe ser no
/// sensible: no se escriben datos personales ni clínicos de alumnos ni datos de pago. Solo se agregan; no se editan ni se borran.
/// </summary>
public sealed class PlatformNote : IAuditable
{
    public const int MaxLength = 1000;

    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public string Text { get; init; } = string.Empty;
    public Guid? AuthorId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }

    public static Result<PlatformNote> Create(Guid tenantId, string text, Guid? author, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Error.Validation("organizations.note_empty", "Escriba la nota.");
        }

        if (text.Trim().Length > MaxLength)
        {
            return Error.Validation("organizations.note_too_long", $"La nota no puede superar {MaxLength} caracteres.");
        }

        if (CardDataGuard.LooksLikeCardNumber(text))
        {
            return Error.Validation("organizations.note_sensitive", "La nota parece contener un número de tarjeta: las notas son solo para información no sensible.");
        }

        return new PlatformNote { Id = Guid.CreateVersion7(), TenantId = tenantId, Text = text.Trim(), AuthorId = author, CreatedAt = now };
    }
}

/// <summary>
/// Consumo agregado de una organización en un momento dado: solo conteos (sucursales activas, usuarios internos activos y alumnos registrados), nunca
/// datos de las personas. Lo mide el proceso de vencimientos y se puede refrescar a pedido; sirve para listar organizaciones sin consultar sus tablas.
/// </summary>
public sealed class TenantUsageSnapshot
{
    public Guid TenantId { get; init; }
    public int Branches { get; set; }
    public int InternalUsers { get; set; }
    public int Members { get; set; }
    public DateTimeOffset MeasuredAt { get; set; }
}
