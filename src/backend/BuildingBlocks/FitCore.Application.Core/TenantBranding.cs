namespace FitCore.Application.Core;

/// <summary>El logo ya validado y sin metadatos, listo para incrustarlo en un archivo (PNG o JPEG).</summary>
public sealed record TenantLogoFile(byte[] Content, string ContentType, int Width, int Height);

/// <summary>La marca de un gimnasio para sus documentos: su nombre, su zona horaria y, si lo cargó, su logo.</summary>
public sealed record TenantBrand(string Name, string TimeZone, TenantLogoFile? Logo);

/// <summary>
/// Contrato para que los módulos que producen documentos (exportaciones hoy; comprobantes mañana) usen la marca del gimnasio sin conocer cómo ni dónde se
/// guarda. Lo implementa Platform; siempre responde para el gimnasio de la solicitud en curso.
/// </summary>
public interface ITenantBranding
{
    Task<TenantBrand> GetAsync(CancellationToken cancellationToken);
}
