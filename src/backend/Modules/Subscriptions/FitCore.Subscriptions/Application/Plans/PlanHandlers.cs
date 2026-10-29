using FitCore.Application.Core;
using FitCore.SharedKernel;
using FitCore.Subscriptions.Domain;
using FitCore.Subscriptions.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FitCore.Subscriptions.Application.Plans;

// La autorización de plataforma (platform.plans.read / platform.plans.manage) se exige en el endpoint, como el resto de rutas /platform.

internal sealed class ListPlansHandler(SubscriptionsDbContext db) : IQueryHandler<ListPlansQuery, PagedResult<PlanSummaryDto>>
{
    public async Task<Result<PagedResult<PlanSummaryDto>>> HandleAsync(ListPlansQuery query, CancellationToken cancellationToken)
    {
        var plans = db.Plans.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var pattern = LikePattern.Contains(query.Search);
            plans = plans.Where(p => EF.Functions.ILike(p.Name, pattern, "\\") || EF.Functions.ILike(p.Code, pattern, "\\"));
        }

        if (query.IsActive is { } active)
        {
            plans = plans.Where(p => p.IsActive == active);
        }

        var paged = await plans.OrderBy(p => p.Name).ThenBy(p => p.Code).ToPagedAsync(query.Page, cancellationToken);
        return new PagedResult<PlanSummaryDto>([.. paged.Items.Select(PlanSummaryDto.From)], paged.Page, paged.PageSize, paged.TotalCount);
    }
}

internal sealed class GetPlanHandler(SubscriptionsDbContext db) : IQueryHandler<GetPlanQuery, PlanDto>
{
    public async Task<Result<PlanDto>> HandleAsync(GetPlanQuery query, CancellationToken cancellationToken)
    {
        var plan = await db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == query.Id, cancellationToken);
        return plan is null ? PlanErrors.NotFound : PlanDto.From(plan);
    }
}

internal sealed class GetPlanCatalogHandler : IQueryHandler<GetPlanCatalogQuery, PlanCatalogDto>
{
    public Task<Result<PlanCatalogDto>> HandleAsync(GetPlanCatalogQuery query, CancellationToken cancellationToken) =>
        Task.FromResult<Result<PlanCatalogDto>>(new PlanCatalogDto(
            [.. Capabilities.FeatureCatalog.Select(f => new FeatureCatalogDto(f.Code, f.Name, f.Description))],
            [.. Capabilities.LimitCatalog.Select(l => new LimitCatalogDto(l.Code, l.Name, l.Description, l.Unit))],
            [.. Enum.GetNames<BillingPeriod>()],
            SupportedCurrencies.All));
}

internal sealed class CreatePlanHandler(SubscriptionsDbContext db, IClock clock, ICurrentUser currentUser) : ICommandHandler<CreatePlanCommand, PlanDto>
{
    public async Task<Result<PlanDto>> HandleAsync(CreatePlanCommand command, CancellationToken cancellationToken)
    {
        var r = command.Request;
        var code = r.Code.Trim().ToLowerInvariant();
        if (await db.Plans.AnyAsync(p => p.Code == code, cancellationToken))
        {
            return PlanErrors.CodeTaken;
        }

        var terms = PlanRules.ToTerms(r.Currency, r.BillingPeriod, r.Price, r.TrialDays, r.GraceDays, r.Features, r.Limits);
        var plan = Plan.Create(code, r.Name, r.Description, r.Rules, r.IsActive, terms, r.ChangeNote, currentUser.UserId, clock.UtcNow);
        db.Plans.Add(plan);
        await db.SaveChangesAsync(cancellationToken);
        return PlanDto.From(plan);
    }
}

internal sealed class UpdatePlanHandler(SubscriptionsDbContext db, IClock clock, ICurrentUser currentUser) : ICommandHandler<UpdatePlanCommand, PlanDto>
{
    public async Task<Result<PlanDto>> HandleAsync(UpdatePlanCommand command, CancellationToken cancellationToken)
    {
        // Se bloquea la fila del plan: dos ediciones simultáneas se atienden una tras otra y cada versión nueva toma el número siguiente
        // de verdad (sin saltos ni duplicados). La base lo respalda con un único (plan, número) y un disparador de numeración.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM subscriptions.plans WHERE id = {command.Id} FOR UPDATE", cancellationToken);

        var plan = await db.Plans.FirstOrDefaultAsync(p => p.Id == command.Id, cancellationToken);
        if (plan is null)
        {
            return PlanErrors.NotFound;
        }

        var r = command.Request;
        var now = clock.UtcNow;
        plan.UpdateDescriptive(r.Name, r.Description, r.Rules, currentUser.UserId, now);

        // Un cambio comercial nunca reescribe la versión vigente: crea otra. Quien ya contrató conserva sus condiciones.
        var terms = PlanRules.ToTerms(r.Currency, r.BillingPeriod, r.Price, r.TrialDays, r.GraceDays, r.Features, r.Limits);
        if (!terms.SameAs(plan.Current.ToTerms()))
        {
            plan.AddVersion(terms, r.ChangeNote, currentUser.UserId, now);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return PlanDto.From(plan);
    }
}

internal sealed class SetPlanStatusHandler(SubscriptionsDbContext db, IClock clock, ICurrentUser currentUser) : ICommandHandler<SetPlanStatusCommand, PlanDto>
{
    public async Task<Result<PlanDto>> HandleAsync(SetPlanStatusCommand command, CancellationToken cancellationToken)
    {
        var plan = await db.Plans.FirstOrDefaultAsync(p => p.Id == command.Id, cancellationToken);
        if (plan is null)
        {
            return PlanErrors.NotFound;
        }

        plan.SetActive(command.IsActive, currentUser.UserId, clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        return PlanDto.From(plan);
    }
}
