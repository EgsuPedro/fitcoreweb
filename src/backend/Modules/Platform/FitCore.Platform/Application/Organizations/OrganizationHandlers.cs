using FitCore.Application.Core;
using FitCore.Platform.Application.Tenants;
using FitCore.Platform.Persistence;
using FitCore.Platform.Security;
using FitCore.SharedKernel;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace FitCore.Platform.Application.Organizations;

public sealed record UpdateOrganizationRequest(string Name, string TimeZone, string Locale);

public sealed record GetOrganizationQuery : IQuery<TenantDto>;

public sealed record UpdateOrganizationCommand(UpdateOrganizationRequest Request) : ICommand<TenantDto>;

public sealed class UpdateOrganizationRequestValidator : AbstractValidator<UpdateOrganizationRequest>
{
    public UpdateOrganizationRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.TimeZone).NotEmpty().Must(SupportedValues.IsValidTimeZone).WithMessage("Zona horaria no válida.");
        RuleFor(x => x.Locale).Must(l => SupportedValues.Locales.Contains(l)).WithMessage("Idioma no soportado.");
    }
}

internal sealed class GetOrganizationHandler(PlatformDbContext db, IAccessContext access) : IQueryHandler<GetOrganizationQuery, TenantDto>
{
    public async Task<Result<TenantDto>> HandleAsync(GetOrganizationQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == actor.Value.TenantId, cancellationToken);
        return TenantDto.From(tenant);
    }
}

internal sealed class UpdateOrganizationHandler(PlatformDbContext db, IAccessContext access)
    : ICommandHandler<UpdateOrganizationCommand, TenantDto>
{
    public async Task<Result<TenantDto>> HandleAsync(UpdateOrganizationCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        if (!actor.Value.AllBranches)
        {
            return PlatformErrors.AllBranchesRequired;
        }

        var tenant = await db.Tenants.FirstAsync(t => t.Id == actor.Value.TenantId, cancellationToken);
        tenant.UpdateProfile(command.Request.Name, command.Request.TimeZone, command.Request.Locale);
        await db.SaveChangesAsync(cancellationToken);
        return TenantDto.From(tenant);
    }
}
