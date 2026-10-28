using System.Text.RegularExpressions;
using FitCore.Application.Core;
using Microsoft.Extensions.DependencyInjection;

namespace FitCore.Infrastructure.Core;

/// <summary>
/// <see cref="IFileStore"/> sobre una carpeta privada del servidor (fuera de cualquier ruta que el servidor web publique). Escribe
/// en un archivo temporal y lo mueve al final, así nunca queda un archivo a medias bajo su clave definitiva.
/// </summary>
public sealed partial class LocalDiskFileStore : IFileStore
{
    private readonly string _root;

    public LocalDiskFileStore(string rootPath)
    {
        _root = Path.GetFullPath(rootPath);
        Directory.CreateDirectory(_root);
    }

    public async Task SaveAsync(string key, Stream content, CancellationToken cancellationToken)
    {
        var path = Resolve(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await content.CopyToAsync(file, cancellationToken);
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            File.Move(temp, path, overwrite: false);
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
    }

    public Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            return Task.FromResult<Stream?>(new FileStream(Resolve(key), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return Task.FromResult<Stream?>(null);
        }
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            File.Delete(Resolve(key));
        }
        catch (DirectoryNotFoundException)
        {
            // Si ni la carpeta existe, el archivo tampoco: borrar es idempotente.
        }

        return Task.CompletedTask;
    }

    /// <summary>Convierte la clave en una ruta dentro de la carpeta raíz; una clave fuera del formato permitido es un error de programación.</summary>
    private string Resolve(string key)
    {
        if (!KeyPattern().IsMatch(key))
        {
            throw new ArgumentException("La clave del archivo no es válida.", nameof(key));
        }

        var full = Path.GetFullPath(Path.Combine(_root, key.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException("La clave del archivo sale de la carpeta de almacenamiento.", nameof(key));
        }

        return full;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]*(/[a-z0-9][a-z0-9_-]*)*$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
}

public static class FileStoreExtensions
{
    /// <summary>Registra el almacén en disco local. La carpeta debe estar fuera de lo que se publica y con copia de seguridad.</summary>
    public static IServiceCollection AddLocalFileStore(this IServiceCollection services, string rootPath) =>
        services.AddSingleton<IFileStore>(new LocalDiskFileStore(rootPath));
}
