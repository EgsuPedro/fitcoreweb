namespace FitCore.Application.Core;

/// <summary>
/// Almacén de archivos privados (fotos de progreso, documentos). Los archivos NUNCA se sirven directamente: la API los lee de aquí
/// solo después de autorizar a la persona y comprobar el consentimiento. Hoy hay una implementación en disco local; cambiarla por
/// almacenamiento de objetos (S3 compatible) no toca a los módulos.
/// </summary>
public interface IFileStore
{
    /// <summary>
    /// Guarda el contenido bajo la clave. La clave solo admite minúsculas, dígitos, <c>-</c>, <c>_</c> y <c>/</c> (sin puntos, así no hay
    /// rutas relativas). Falla si la clave ya existe: una clave nunca se reutiliza.
    /// </summary>
    Task SaveAsync(string key, Stream content, CancellationToken cancellationToken);

    /// <summary>Abre el archivo para lectura; nulo si no existe. Quien lo recibe debe liberarlo.</summary>
    Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken);

    /// <summary>Borra el archivo. Es idempotente: si no existe, no hace nada.</summary>
    Task DeleteAsync(string key, CancellationToken cancellationToken);
}
