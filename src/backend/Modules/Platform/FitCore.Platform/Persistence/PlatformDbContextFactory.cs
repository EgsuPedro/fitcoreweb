using FitCore.Infrastructure.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace FitCore.Platform.Persistence;

/// <summary>Solo para <c>dotnet ef</c>. Requiere la variable FITCORE_MIGRATIONS_CONNECTION (nunca un secreto en el código).</summary>
internal sealed class PlatformDbContextFactory : IDesignTimeDbContextFactory<PlatformDbContext>
{
    public PlatformDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("FITCORE_MIGRATIONS_CONNECTION")
            ?? throw new InvalidOperationException("Defina FITCORE_MIGRATIONS_CONNECTION para ejecutar migraciones.");

        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseNpgsql(connection)
            .UseSnakeCaseNamingConvention()
            .Options;

        return new PlatformDbContext(options, new TenantContext());
    }
}
