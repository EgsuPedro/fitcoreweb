using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FitCore.Analytics.Persistence;

/// <summary>
/// Registro de una exportación: quién la pidió, cuándo, qué reporte y con qué filtros (nunca el contenido), dónde quedó el archivo y hasta cuándo
/// se puede descargar. El archivo vive en el almacén privado bajo una clave aleatoria; aquí no se guardan datos de alumnos.
/// </summary>
internal sealed class ReportExport : ITenantRow
{
    public const int MaxRows = 20_000;
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);
    public const int MaxPerUserPerHour = 10;

    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid RequestedBy { get; init; }
    public DateTimeOffset RequestedAt { get; init; }
    public string Report { get; init; } = string.Empty;
    public string Format { get; init; } = "csv";

    /// <summary>Los filtros aplicados, como JSON (período, sucursales, plan, estado…). Sirve para reproducir y auditar la solicitud.</summary>
    public string Filters { get; init; } = "{}";

    public int RowCount { get; init; }
    public long FileSize { get; init; }

    /// <summary>Clave del archivo en el almacén privado. Nula cuando el archivo ya se purgó.</summary>
    public string? FileKey { get; set; }

    public DateTimeOffset ExpiresAt { get; init; }
    public int DownloadCount { get; set; }
    public DateTimeOffset? LastDownloadedAt { get; set; }
    public DateTimeOffset? PurgedAt { get; set; }

    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;
}

internal sealed class ReportExportConfiguration : IEntityTypeConfiguration<ReportExport>
{
    public void Configure(EntityTypeBuilder<ReportExport> b)
    {
        b.ToTable("report_exports", AnalyticsDbContext.Schema);
        b.HasKey(x => x.Id);
        b.Property(x => x.Report).HasMaxLength(40).IsRequired();
        b.Property(x => x.Format).HasMaxLength(10).IsRequired();
        b.Property(x => x.Filters).HasColumnType("jsonb").IsRequired();
        b.Property(x => x.FileKey).HasMaxLength(200);
        b.HasIndex(x => new { x.TenantId, x.RequestedBy, x.RequestedAt });
        b.HasIndex(x => new { x.TenantId, x.ExpiresAt }).HasFilter("file_key IS NOT NULL");
    }
}
