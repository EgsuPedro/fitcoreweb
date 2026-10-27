namespace FitCore.Infrastructure.Core;

/// <summary>
/// Paso de inicialización de un módulo (migraciones y datos base). Lo ejecuta el inicializador de la base de datos
/// después de migrar el núcleo, porque los módulos dependen de sus tablas (tenants y sucursales).
/// </summary>
public interface IModuleInitializer
{
    Task InitializeAsync(string migrationsConnection, CancellationToken cancellationToken);
}
