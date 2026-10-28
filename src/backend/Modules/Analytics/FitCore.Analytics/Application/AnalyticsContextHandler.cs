using FitCore.Analytics.Domain;
using FitCore.Analytics.Persistence;
using FitCore.Application.Core;
using FitCore.Platform.Application;
using FitCore.Platform.Domain;
using FitCore.Platform.Security;
using FitCore.SharedKernel;

namespace FitCore.Analytics.Application;

internal sealed class GetAnalyticsContextHandler(AnalyticsDbContext db, IAccessContext access, IClock clock)
    : IQueryHandler<GetAnalyticsContextQuery, AnalyticsContextDto>
{
    public async Task<Result<AnalyticsContextDto>> HandleAsync(GetAnalyticsContextQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequirePermissionAsync(Permissions.AnalyticsDashboardRead, cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var scope = await AnalyticsScopes.ResolveAsync(db, actor.Value, query.BranchIds, clock, cancellationToken);
        if (scope.IsFailure)
        {
            return scope.Error;
        }

        var periods = PeriodResolver.Resolve(query.Preset, query.From, query.To, scope.Value.Today, scope.Value.TimeZoneId);
        if (periods.IsFailure)
        {
            return periods.Error;
        }

        var user = actor.Value;
        return new AnalyticsContextDto(
            "USD",
            scope.Value.TimeZoneId,
            scope.Value.Today,
            periods.Value.Preset,
            PeriodDto.Of(periods.Value.Current),
            PeriodDto.Of(periods.Value.Previous),
            scope.Value.BranchIds,
            scope.Value.AuthorizedBranches,
            new AnalyticsSectionsDto(
                user.Has(Permissions.ReportsFinancialRead),
                user.Has(Permissions.ReportsMembersRead),
                user.Has(Permissions.ReportsOperationsRead),
                user.Has(Permissions.ReportsInstructorRead),
                user.Has(Permissions.ReportsCompare),
                user.Has(Permissions.ReportsExport)),
            PeriodResolver.MaxDays);
    }
}
