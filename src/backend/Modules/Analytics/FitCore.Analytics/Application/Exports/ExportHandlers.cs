using System.Text.Json;
using FitCore.Analytics.Domain;
using FitCore.Analytics.Persistence;
using FitCore.Analytics.Application.Reports;
using FitCore.Application.Core;
using FitCore.Platform.Application;
using FitCore.Platform.Domain;
using FitCore.Platform.Security;
using FitCore.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FitCore.Analytics.Application.Exports;

internal static class ExportSupport
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    public static string FiltersJson(ExportRequest request) => JsonSerializer.Serialize(request, Json);

    /// <summary>La clave del archivo en el almacén privado: aleatoria (el id), sin el nombre del reporte ni datos del alumno.</summary>
    public static string FileKey(Guid tenantId, Guid exportId) => $"analytics-exports/{tenantId:N}/{exportId:N}";

    /// <summary>Nombre seguro y genérico: ni nombres de alumnos ni nada escrito por usuarios.</summary>
    public static string FileName(string slug, DateTimeOffset requestedAt, Guid id, string format) => $"reporte-{slug}-{requestedAt:yyyyMMdd}-{id.ToString("N")[..8]}.{Extension(format)}";

    public static string Extension(string format) => format == "xlsx" ? "xlsx" : "csv";

    public static string ContentType(string format) =>
        format == "xlsx" ? "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" : "text/csv; charset=utf-8";

    /// <summary>Título legible del reporte para el encabezado del libro de Excel.</summary>
    public static string Title(ExportReport report) => report switch
    {
        ExportReport.Collections => "Cobros",
        ExportReport.PaymentMethods => "Cobros por medio de pago",
        ExportReport.OutstandingBalances => "Saldos pendientes",
        ExportReport.CashRegisters => "Cajas",
        ExportReport.Reversals => "Reversiones",
        ExportReport.MemberAcquisitions => "Membresías vendidas",
        ExportReport.MemberRetention => "Alumnos sin membresía vigente",
        ExportReport.MemberRenewals => "Membresías sin renovar",
        ExportReport.MemberExpirations => "Próximos vencimientos",
        ExportReport.Attendance => "Asistencia",
        ExportReport.ClassOccupancy => "Ocupación de clases",
        ExportReport.ClassCancellations => "Clases canceladas",
        _ => "Comparación de sucursales"
    };

    public static string PeriodText(ExportRequest request) => request.Preset switch
    {
        PeriodPreset.Today => "Hoy",
        PeriodPreset.Last7Days => "Últimos 7 días",
        PeriodPreset.Last30Days => "Últimos 30 días",
        PeriodPreset.CurrentMonth => "Mes actual",
        PeriodPreset.PreviousMonth => "Mes anterior",
        PeriodPreset.CurrentYear => "Año actual",
        _ => request.From is { } from && request.To is { } to ? $"{from:yyyy-MM-dd} a {to:yyyy-MM-dd}" : "Personalizado"
    };

    public static string Slug(ExportReport report) => report switch
    {
        ExportReport.Collections => "cobros",
        ExportReport.PaymentMethods => "medios-de-pago",
        ExportReport.OutstandingBalances => "saldos",
        ExportReport.CashRegisters => "caja",
        ExportReport.Reversals => "reversiones",
        ExportReport.MemberAcquisitions => "membresias-vendidas",
        ExportReport.MemberRetention => "alumnos-sin-membresia",
        ExportReport.MemberRenewals => "membresias-sin-renovar",
        ExportReport.MemberExpirations => "proximos-vencimientos",
        ExportReport.Attendance => "asistencia",
        ExportReport.ClassOccupancy => "ocupacion-de-clases",
        ExportReport.ClassCancellations => "clases-canceladas",
        _ => "comparacion-de-sucursales"
    };

    public static ExportDto ToDto(ReportExport e, DateTimeOffset now)
    {
        var report = Enum.TryParse<ExportReport>(e.Report, out var parsed) ? parsed : ExportReport.Collections;
        var status = e.FileKey is null ? "Purged" : e.IsExpired(now) ? "Expired" : "Available";
        return new ExportDto(
            e.Id, e.Report, e.Format, e.RequestedAt, e.RowCount, e.FileSize, e.ExpiresAt, status, e.DownloadCount, FileName(Slug(report), e.RequestedAt, e.Id, e.Format),
            $"/api/v1/reports/exports/{e.Id}/download", e.Filters);
    }

    /// <summary>¿Tiene el usuario los permisos para exportar ese reporte? Exportar exige además el permiso de exportar.</summary>
    public static bool CanExport(UserAccess user, ExportReport report) =>
        user.Has(Permissions.ReportsExport) && Exporters.Required(report) is { } required && required.Any(user.Has);

    /// <summary>
    /// Borra los archivos vencidos de este gimnasio (el registro queda: solo se anula la clave). Se hace al exportar, al listar y al descargar; el
    /// vencimiento se aplica siempre al descargar, haya o no pasado una purga.
    /// </summary>
    public static async Task PurgeExpiredAsync(AnalyticsDbContext db, IFileStore store, DateTimeOffset now, CancellationToken ct)
    {
        var expired = await db.Exports.Where(e => e.FileKey != null && e.ExpiresAt <= now).OrderBy(e => e.ExpiresAt).Take(50).ToListAsync(ct);
        foreach (var export in expired)
        {
            await store.DeleteAsync(export.FileKey!, ct);
            await db.Exports.Where(e => e.Id == export.Id).ExecuteUpdateAsync(s => s.SetProperty(e => e.FileKey, (string?)null).SetProperty(e => e.PurgedAt, now), ct);
        }
    }
}

internal sealed class CreateExportHandler(
    AnalyticsDbContext db, IAccessContext access, IDispatcher dispatcher, IFileStore store, IClock clock, IConfiguration configuration, ITenantBranding branding,
    ILogger<CreateExportHandler> logger)
    : ICommandHandler<CreateExportCommand, ExportDto>
{
    public async Task<Result<ExportDto>> HandleAsync(CreateExportCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var request = command.Request;
        if (!Enum.IsDefined(request.Report) || !Enum.IsDefined(request.Format))
        {
            return ExportErrors.ReportInvalid;
        }

        // Permiso de exportar Y el del reporte. Los casos de uso del reporte repiten su propia comprobación y su alcance de sucursales.
        if (!ExportSupport.CanExport(actor.Value, request.Report))
        {
            return PlatformErrors.Forbidden;
        }

        var now = clock.UtcNow;
        await ExportSupport.PurgeExpiredAsync(db, store, now, cancellationToken);

        var since = now.AddHours(-1);
        var recent = await db.Exports.CountAsync(e => e.RequestedBy == actor.Value.UserId && e.RequestedAt >= since, cancellationToken);
        if (recent >= ReportExport.MaxPerUserPerHour)
        {
            return ExportErrors.RateLimited;
        }

        // Tope de filas de una exportación síncrona (configurable para operar con volúmenes distintos; nunca por encima del límite de la base).
        var maxRows = Math.Clamp(configuration.GetValue("Analytics:Exports:MaxRows", ReportExport.MaxRows), 1, ReportExport.MaxRows);
        var table = await Exporters.BuildAsync(dispatcher, maxRows, request, cancellationToken);
        if (table.IsFailure)
        {
            return table.Error;
        }

        var format = request.Format == ExportFormat.Csv ? "csv" : "xlsx";
        byte[] bytes;
        if (request.Format == ExportFormat.Csv)
        {
            bytes = CsvWriter.Build(table.Value.Headers, table.Value.Rows);
        }
        else
        {
            // Excel lleva la marca del gimnasio: su nombre y, si lo cargó, su logo. Sin logo el libro sale igual, solo con el nombre.
            var brand = await branding.GetAsync(cancellationToken);
            bytes = XlsxExporter.Build(table.Value, brand, ExportSupport.Title(request.Report), ExportSupport.PeriodText(request), now);
        }

        var id = Guid.CreateVersion7();
        var key = ExportSupport.FileKey(actor.Value.TenantId, id);
        await using (var content = new MemoryStream(bytes))
        {
            await store.SaveAsync(key, content, cancellationToken);
        }

        var export = new ReportExport
        {
            Id = id,
            TenantId = actor.Value.TenantId,
            RequestedBy = actor.Value.UserId,
            RequestedAt = now,
            Report = request.Report.ToString(),
            Format = format,
            Filters = ExportSupport.FiltersJson(request),
            RowCount = table.Value.Rows.Count,
            FileSize = bytes.Length,
            FileKey = key,
            ExpiresAt = now.Add(ReportExport.Lifetime)
        };
        try
        {
            db.Exports.Add(export);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            // Sin registro no hay archivo: no se deja un archivo huérfano que nadie pueda descargar ni vencer.
            await store.DeleteAsync(key, CancellationToken.None);
            throw;
        }

        // Quién, cuándo, qué reporte y cuántas filas; nunca el contenido ni datos de alumnos.
        logger.LogInformation("Exportación {ExportId}: reporte {Report}, usuario {UserId}, {Rows} filas", id, request.Report, actor.Value.UserId, export.RowCount);
        return ExportSupport.ToDto(export, now);
    }
}

internal sealed class GetExportHandler(AnalyticsDbContext db, IAccessContext access, IClock clock)
    : IQueryHandler<GetExportQuery, ExportDto>
{
    public async Task<Result<ExportDto>> HandleAsync(GetExportQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequirePermissionAsync(Permissions.ReportsExport, cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        // Solo el que la pidió la ve; para cualquier otro, no existe.
        var export = await db.Exports.FirstOrDefaultAsync(e => e.Id == query.Id && e.RequestedBy == actor.Value.UserId, cancellationToken);
        return export is null ? ExportErrors.NotFound : ExportSupport.ToDto(export, clock.UtcNow);
    }
}

internal sealed class ListExportsHandler(AnalyticsDbContext db, IAccessContext access, IFileStore store, IClock clock)
    : IQueryHandler<ListExportsQuery, PagedResult<ExportDto>>
{
    public async Task<Result<PagedResult<ExportDto>>> HandleAsync(ListExportsQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequirePermissionAsync(Permissions.ReportsExport, cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var now = clock.UtcNow;
        await ExportSupport.PurgeExpiredAsync(db, store, now, cancellationToken);
        var paged = await db.Exports.Where(e => e.RequestedBy == actor.Value.UserId).OrderByDescending(e => e.RequestedAt).ThenBy(e => e.Id)
            .ToPagedAsync(query.Page, cancellationToken);
        return new PagedResult<ExportDto>([.. paged.Items.Select(e => ExportSupport.ToDto(e, now))], paged.Page, paged.PageSize, paged.TotalCount);
    }
}

internal sealed class DownloadExportHandler(AnalyticsDbContext db, IAccessContext access, IFileStore store, IClock clock)
    : IQueryHandler<DownloadExportQuery, ExportFile>
{
    public async Task<Result<ExportFile>> HandleAsync(DownloadExportQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequirePermissionAsync(Permissions.ReportsExport, cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var export = await db.Exports.FirstOrDefaultAsync(e => e.Id == query.Id && e.RequestedBy == actor.Value.UserId, cancellationToken);
        if (export is null)
        {
            return ExportErrors.NotFound;
        }

        // Los permisos se vuelven a comprobar al descargar: si le quitaron el acceso al reporte, el archivo ya generado tampoco se entrega.
        if (!Enum.TryParse<ExportReport>(export.Report, out var report) || !ExportSupport.CanExport(actor.Value, report))
        {
            return PlatformErrors.Forbidden;
        }

        var now = clock.UtcNow;
        if (export.FileKey is null || export.IsExpired(now))
        {
            await ExportSupport.PurgeExpiredAsync(db, store, now, cancellationToken);
            return ExportErrors.Expired;
        }

        var stream = await store.OpenReadAsync(export.FileKey, cancellationToken);
        if (stream is null)
        {
            return ExportErrors.Expired;
        }

        await db.Exports.Where(e => e.Id == export.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.DownloadCount, e => e.DownloadCount + 1).SetProperty(e => e.LastDownloadedAt, now), cancellationToken);
        return new ExportFile(stream, ExportSupport.FileName(ExportSupport.Slug(report), export.RequestedAt, export.Id, export.Format), export.FileSize, ExportSupport.ContentType(export.Format));
    }
}
