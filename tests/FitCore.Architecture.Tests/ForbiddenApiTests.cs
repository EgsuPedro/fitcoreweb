namespace FitCore.Architecture.Tests;

/// <summary>APIs que se saltan el filtro global de tenant: prohibidas en el código de la aplicación.</summary>
public class ForbiddenApiTests
{
    // FindAsync / FindByIdAsync leen por clave primaria y NO aplican los filtros globales de EF.
    // RLS en PostgreSQL lo cubriría igual, pero no se debe depender de una sola capa.
    private static readonly string[] Forbidden = ["FindByIdAsync(", ".FindAsync(", ".Find("];

    [Fact]
    public void El_codigo_no_usa_APIs_que_ignoran_el_filtro_de_tenant()
    {
        var root = FindRepositoryRoot();
        var offenders = Directory.EnumerateFiles(Path.Combine(root, "src", "backend"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(file => File.ReadLines(file)
                .Select((line, index) => (file, line, number: index + 1))
                .Where(x => !x.line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                .Where(x => Forbidden.Any(api => x.line.Contains(api, StringComparison.Ordinal))))
            .Select(x => $"{Path.GetRelativePath(root, x.file)}:{x.number}")
            .ToList();

        Assert.True(offenders.Count == 0, $"Use consultas sobre DbSet (con filtro de tenant) en lugar de Find: {string.Join("; ", offenders)}");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FitCore.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("No se encontró la raíz del repositorio (FitCore.sln).");
    }
}
