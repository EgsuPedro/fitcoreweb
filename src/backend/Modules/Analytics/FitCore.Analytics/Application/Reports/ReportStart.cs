using FitCore.Analytics.Domain;
using FitCore.Analytics.Persistence;
using FitCore.Application.Core;
using FitCore.Platform.Security;
using FitCore.SharedKernel;

namespace FitCore.Analytics.Application.Reports;

/// <summary>El comienzo común de todo reporte: permiso del área, sucursales autorizadas, zona horaria y período con su comparable.</summary>
internal static class ReportStart
{
    public static async Task<Result<(UserAccess Actor, AnalyticsScope Scope, PeriodPair Periods)>> BeginAsync(
        AnalyticsDbContext db, IAccessContext access, string permission, ReportFilter filter, IClock clock, CancellationToken cancellationToken)
    {
        var actor = await access.RequirePermissionAsync(permission, cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var request = await AnalyticsScopes.ResolveRequestAsync(db, actor.Value, filter.BranchIds, filter.Preset, filter.From, filter.To, clock, cancellationToken);
        if (request.IsFailure)
        {
            return request.Error;
        }

        return (actor.Value, request.Value.Scope, request.Value.Periods);
    }
}
