using FitCore.Analytics.Application.Reports;
using FitCore.Analytics.Application.Reports.Financial;
using FitCore.Analytics.Application.Reports.Members;
using FitCore.Analytics.Domain;
using FitCore.Application.Core;

namespace FitCore.Analytics.Application.Exports;

/// <summary>
/// Qué reporte exportar y con qué filtros (los mismos que usa el reporte en pantalla). Se exporta exactamente lo que el reporte mostraría para este
/// usuario: sus sucursales, su rol y sus reglas; el archivo nunca trae más.
/// </summary>
public sealed record ExportRequest(
    ExportReport Report,
    PeriodPreset Preset = PeriodPreset.Last30Days,
    DateOnly? From = null,
    DateOnly? To = null,
    IReadOnlyList<Guid>? BranchIds = null,
    Guid? PlanId = null,
    PaymentStatusFilter? Status = null,
    string? Method = null,
    BalanceView? View = null,
    bool? OverdueOnly = null,
    MembershipKind? Kind = null,
    int? Days = null,
    bool? HideRenewed = null,
    bool? OnlyWithDifference = null,
    string? Activity = null,
    Guid? ClassId = null,
    ExportFormat Format = ExportFormat.Xlsx);

/// <param name="Status">Available (se puede descargar), Expired (venció el plazo) o Purged (el archivo ya se borró).</param>
/// <param name="DownloadPath">Ruta de descarga (requiere sesión y permisos; no es un enlace público).</param>
public sealed record ExportDto(
    Guid Id,
    string Report,
    string Format,
    DateTimeOffset RequestedAt,
    int RowCount,
    long FileSize,
    DateTimeOffset ExpiresAt,
    string Status,
    int DownloadCount,
    string FileName,
    string DownloadPath,
    string Filters);

public sealed record ExportFile(Stream Content, string FileName, long Length, string ContentType);

public sealed record CreateExportCommand(ExportRequest Request) : ICommand<ExportDto>;

public sealed record GetExportQuery(Guid Id) : IQuery<ExportDto>;

public sealed record ListExportsQuery(PageRequest Page) : IQuery<PagedResult<ExportDto>>;

public sealed record DownloadExportQuery(Guid Id) : IQuery<ExportFile>;
