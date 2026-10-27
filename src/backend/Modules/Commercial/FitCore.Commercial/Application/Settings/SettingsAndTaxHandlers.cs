using FitCore.Application.Core;
using FitCore.Commercial.Domain;
using FitCore.Commercial.Persistence;
using FitCore.Platform.Application;
using FitCore.Platform.Security;
using FitCore.Platform.Domain;
using FitCore.SharedKernel;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace FitCore.Commercial.Application.Settings;

public sealed record CommercialSettingsDto(string AccessPolicy, int MinPaymentPercent, int DueDays, int MaxDiscountPercent, DateTimeOffset? UpdatedAt)
{
    public static CommercialSettingsDto From(CommercialSettings s) =>
        new(s.AccessPolicy.ToString(), s.MinPaymentPercent, s.DueDays, s.MaxDiscountPercent, s.UpdatedAt);
}

public sealed record UpdateCommercialSettingsRequest(AccessPolicy AccessPolicy, int MinPaymentPercent, int DueDays, int MaxDiscountPercent);

public sealed record GetCommercialSettingsQuery : IQuery<CommercialSettingsDto>;

public sealed record UpdateCommercialSettingsCommand(UpdateCommercialSettingsRequest Request) : ICommand<CommercialSettingsDto>;

public sealed class UpdateCommercialSettingsRequestValidator : AbstractValidator<UpdateCommercialSettingsRequest>
{
    public UpdateCommercialSettingsRequestValidator()
    {
        RuleFor(x => x.AccessPolicy).IsInEnum();
        RuleFor(x => x.MinPaymentPercent).InclusiveBetween(1, 100);
        RuleFor(x => x.DueDays).InclusiveBetween(0, 90);
        RuleFor(x => x.MaxDiscountPercent).InclusiveBetween(0, 100);
    }
}

internal static class SettingsStore
{
    /// <summary>Políticas del gimnasio, o los valores por defecto si todavía no las ha configurado (no se persisten por leerlas).</summary>
    public static async Task<CommercialSettings> GetAsync(CommercialDbContext db, Guid tenantId, CancellationToken cancellationToken) =>
        await db.Settings.FirstOrDefaultAsync(cancellationToken) ?? CommercialSettings.CreateDefault(tenantId);
}

internal sealed class GetCommercialSettingsHandler(CommercialDbContext db, IAccessContext access)
    : IQueryHandler<GetCommercialSettingsQuery, CommercialSettingsDto>
{
    public async Task<Result<CommercialSettingsDto>> HandleAsync(GetCommercialSettingsQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        return CommercialSettingsDto.From(await SettingsStore.GetAsync(db, actor.Value.TenantId, cancellationToken));
    }
}

internal sealed class UpdateCommercialSettingsHandler(CommercialDbContext db, IAccessContext access, IClock clock, FitCore.Application.Core.ICurrentUser currentUser)
    : ICommandHandler<UpdateCommercialSettingsCommand, CommercialSettingsDto>
{
    public async Task<Result<CommercialSettingsDto>> HandleAsync(UpdateCommercialSettingsCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        // Es una política de todo el gimnasio: solo quien accede a todas las sucursales puede cambiarla.
        if (!actor.Value.AllBranches)
        {
            return PlatformErrors.AllBranchesRequired;
        }

        var settings = await db.Settings.FirstOrDefaultAsync(cancellationToken);
        if (settings is null)
        {
            settings = CommercialSettings.CreateDefault(actor.Value.TenantId);
            db.Settings.Add(settings);
        }

        var request = command.Request;
        settings.Update(request.AccessPolicy, request.MinPaymentPercent, request.DueDays, request.MaxDiscountPercent, currentUser.UserId, clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        return CommercialSettingsDto.From(settings);
    }
}

// ---- Tarifas de impuesto -------------------------------------------------------------------------------------------

public sealed record TaxRateDto(Guid Id, string Kind, decimal Percent, DateOnly ValidFrom, DateOnly? ValidUntil, bool IsCurrent)
{
    public static TaxRateDto From(TaxRate r, DateOnly today) => new(r.Id, r.Kind.ToString(), r.Percent, r.ValidFrom, r.ValidUntil, r.AppliesOn(today));
}

/// <summary>Nueva tarifa general de IVA por decreto. Empieza en <c>ValidFrom</c> y cierra la anterior el día previo.</summary>
public sealed record CreateTaxRateRequest(decimal Percent, DateOnly ValidFrom);

public sealed record ListTaxRatesQuery : IQuery<IReadOnlyList<TaxRateDto>>;

public sealed record GetCurrentTaxRatesQuery : IQuery<IReadOnlyList<TaxRateDto>>;

public sealed record CreateTaxRateCommand(CreateTaxRateRequest Request) : ICommand<TaxRateDto>;

public sealed class CreateTaxRateRequestValidator : AbstractValidator<CreateTaxRateRequest>
{
    public CreateTaxRateRequestValidator()
    {
        RuleFor(x => x.Percent).InclusiveBetween(0m, 100m).PrecisionScale(5, 2, ignoreTrailingZeros: true);
        RuleFor(x => x.ValidFrom).NotEmpty();
    }
}

internal static class TaxRateLookup
{
    /// <summary>Tarifa vigente de cada tipo de impuesto en la fecha dada.</summary>
    public static async Task<Dictionary<TaxKind, TaxRate>> ForDateAsync(CommercialDbContext db, DateOnly date, CancellationToken cancellationToken)
    {
        var rates = await db.TaxRates.AsNoTracking()
            .Where(r => r.ValidFrom <= date && (r.ValidUntil == null || r.ValidUntil >= date))
            .ToListAsync(cancellationToken);
        return rates.GroupBy(r => r.Kind).ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.ValidFrom).First());
    }
}

internal static class LocalDates
{
    /// <summary>Fecha de hoy en la zona horaria por defecto del país (Ecuador continental).</summary>
    public static DateOnly Today(IClock clock) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(Tenant.DefaultTimeZone)).DateTime);
}

internal sealed class ListTaxRatesHandler(CommercialDbContext db, IClock clock) : IQueryHandler<ListTaxRatesQuery, IReadOnlyList<TaxRateDto>>
{
    public async Task<Result<IReadOnlyList<TaxRateDto>>> HandleAsync(ListTaxRatesQuery query, CancellationToken cancellationToken)
    {
        var today = LocalDates.Today(clock);
        var rates = await db.TaxRates.AsNoTracking().OrderBy(r => r.Kind).ThenByDescending(r => r.ValidFrom).ToListAsync(cancellationToken);
        return Result.Success<IReadOnlyList<TaxRateDto>>([.. rates.Select(r => TaxRateDto.From(r, today))]);
    }
}

internal sealed class GetCurrentTaxRatesHandler(CommercialDbContext db, IAccessContext access, IClock clock)
    : IQueryHandler<GetCurrentTaxRatesQuery, IReadOnlyList<TaxRateDto>>
{
    public async Task<Result<IReadOnlyList<TaxRateDto>>> HandleAsync(GetCurrentTaxRatesQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var today = LocalDates.Today(clock);
        var current = await TaxRateLookup.ForDateAsync(db, today, cancellationToken);
        return Result.Success<IReadOnlyList<TaxRateDto>>([.. current.Values.OrderBy(r => r.Kind).Select(r => TaxRateDto.From(r, today))]);
    }
}

internal sealed class CreateTaxRateHandler(CommercialDbContext db, IClock clock, FitCore.Application.Core.ICurrentUser currentUser)
    : ICommandHandler<CreateTaxRateCommand, TaxRateDto>
{
    public async Task<Result<TaxRateDto>> HandleAsync(CreateTaxRateCommand command, CancellationToken cancellationToken)
    {
        var today = LocalDates.Today(clock);
        var request = command.Request;

        // Un decreto rige hacia adelante: no se reescribe lo ya vigente (los cobros ya emitidos congelaron su tarifa).
        if (request.ValidFrom < today)
        {
            return Error.Validation("taxes.rate_in_past", "La nueva tarifa no puede empezar en una fecha pasada.");
        }

        var rates = await db.TaxRates.Where(r => r.Kind == TaxKind.General).ToListAsync(cancellationToken);
        var latest = rates.OrderByDescending(r => r.ValidFrom).FirstOrDefault();
        if (latest is not null && request.ValidFrom <= latest.ValidFrom)
        {
            return Error.Conflict("taxes.rate_overlap", "Ya existe una tarifa que empieza en esa fecha o después. La nueva debe empezar más tarde.");
        }

        var open = rates.FirstOrDefault(r => r.ValidUntil is null);
        if (open is not null)
        {
            open.ValidUntil = request.ValidFrom.AddDays(-1);
        }

        var rate = new TaxRate
        {
            Id = Guid.CreateVersion7(),
            Kind = TaxKind.General,
            Percent = request.Percent,
            ValidFrom = request.ValidFrom,
            CreatedAt = clock.UtcNow,
            CreatedBy = currentUser.UserId
        };
        db.TaxRates.Add(rate);
        await db.SaveChangesAsync(cancellationToken);
        return TaxRateDto.From(rate, today);
    }
}
