using FitCore.Application.Core;
using FitCore.Platform.Application.Tenants;
using FitCore.Platform.Domain;
using FitCore.Platform.Persistence;
using FitCore.Platform.Security;
using FitCore.SharedKernel;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace FitCore.Platform.Application.Branches;

public sealed record BranchDto(
    Guid Id,
    string Code,
    string Name,
    string TimeZone,
    string? SriEstablishmentCode,
    string? Address,
    bool IsActive,
    DateTimeOffset CreatedAt)
{
    public static BranchDto From(Branch b) =>
        new(b.Id, b.Code, b.Name, b.TimeZone, b.SriEstablishmentCode, b.Address, b.IsActive, b.CreatedAt);
}

public sealed record CreateBranchRequest(string Code, string Name, string TimeZone, string? SriEstablishmentCode, string? Address);

public sealed record UpdateBranchRequest(string Name, string TimeZone, string? SriEstablishmentCode, string? Address, bool IsActive);

public sealed record ListBranchesQuery(PageRequest Page, string? Search, bool? IsActive) : IQuery<PagedResult<BranchDto>>;

public sealed record GetBranchQuery(Guid Id) : IQuery<BranchDto>;

public sealed record CreateBranchCommand(CreateBranchRequest Request) : ICommand<BranchDto>;

public sealed record UpdateBranchCommand(Guid Id, UpdateBranchRequest Request) : ICommand<BranchDto>;

public sealed class CreateBranchRequestValidator : AbstractValidator<CreateBranchRequest>
{
    public CreateBranchRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(20).Matches("^[A-Za-z0-9_-]+$");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.TimeZone).NotEmpty().Must(SupportedValues.IsValidTimeZone).WithMessage("Zona horaria no válida.");
        RuleFor(x => x.SriEstablishmentCode).Matches("^[0-9]{3}$").When(x => x.SriEstablishmentCode is not null);
        RuleFor(x => x.Address).MaximumLength(300);
    }
}

public sealed class UpdateBranchRequestValidator : AbstractValidator<UpdateBranchRequest>
{
    public UpdateBranchRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.TimeZone).NotEmpty().Must(SupportedValues.IsValidTimeZone).WithMessage("Zona horaria no válida.");
        RuleFor(x => x.SriEstablishmentCode).Matches("^[0-9]{3}$").When(x => x.SriEstablishmentCode is not null);
        RuleFor(x => x.Address).MaximumLength(300);
    }
}

internal static class BranchScope
{
    /// <summary>Sucursales visibles para el usuario: todas las del tenant (filtro global) o solo las asignadas.</summary>
    public static IQueryable<Branch> VisibleTo(this IQueryable<Branch> branches, UserAccess access)
    {
        if (access.AllBranches)
        {
            return branches;
        }

        var ids = access.BranchIds.ToList();
        return branches.Where(b => ids.Contains(b.Id));
    }
}

internal sealed class ListBranchesHandler(PlatformDbContext db, IAccessContext access)
    : IQueryHandler<ListBranchesQuery, PagedResult<BranchDto>>
{
    public async Task<Result<PagedResult<BranchDto>>> HandleAsync(ListBranchesQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var branches = db.Branches.AsNoTracking().VisibleTo(actor.Value);
        if (query.IsActive is { } isActive)
        {
            branches = branches.Where(b => b.IsActive == isActive);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var pattern = $"%{LikePattern.Escape(query.Search.Trim())}%";
            branches = branches.Where(b => EF.Functions.ILike(b.Name, pattern) || EF.Functions.ILike(b.Code, pattern));
        }

        var paged = await branches.OrderBy(b => b.Code).ToPagedAsync(query.Page, cancellationToken);
        return new PagedResult<BranchDto>([.. paged.Items.Select(BranchDto.From)], paged.Page, paged.PageSize, paged.TotalCount);
    }
}

internal sealed class GetBranchHandler(PlatformDbContext db, IAccessContext access) : IQueryHandler<GetBranchQuery, BranchDto>
{
    public async Task<Result<BranchDto>> HandleAsync(GetBranchQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        // Una sucursal ajena (de otro tenant o fuera del alcance del usuario) responde 404: no se revela su existencia.
        var branch = await db.Branches.AsNoTracking().VisibleTo(actor.Value).FirstOrDefaultAsync(b => b.Id == query.Id, cancellationToken);
        return branch is null ? BranchErrors.NotFound : BranchDto.From(branch);
    }
}

internal sealed class CreateBranchHandler(PlatformDbContext db, IAccessContext access, IClock clock)
    : ICommandHandler<CreateBranchCommand, BranchDto>
{
    public async Task<Result<BranchDto>> HandleAsync(CreateBranchCommand command, CancellationToken cancellationToken)
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

        var request = command.Request;
        var code = request.Code.Trim().ToUpperInvariant();
        if (await db.Branches.AnyAsync(b => b.Code == code, cancellationToken))
        {
            return Error.Conflict("branches.code_taken", "Ya existe una sucursal con ese código.");
        }

        var branch = Branch.Create(
            actor.Value.TenantId,
            code,
            request.Name,
            request.TimeZone,
            request.SriEstablishmentCode,
            request.Address,
            clock.UtcNow);

        db.Branches.Add(branch);
        await db.SaveChangesAsync(cancellationToken);
        return BranchDto.From(branch);
    }
}

internal sealed class UpdateBranchHandler(PlatformDbContext db, IAccessContext access)
    : ICommandHandler<UpdateBranchCommand, BranchDto>
{
    public async Task<Result<BranchDto>> HandleAsync(UpdateBranchCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var branch = await db.Branches.VisibleTo(actor.Value).FirstOrDefaultAsync(b => b.Id == command.Id, cancellationToken);
        if (branch is null)
        {
            return BranchErrors.NotFound;
        }

        var request = command.Request;
        branch.Update(request.Name, request.TimeZone, request.SriEstablishmentCode, request.Address, request.IsActive);
        await db.SaveChangesAsync(cancellationToken);
        return BranchDto.From(branch);
    }
}

internal static class BranchErrors
{
    public static readonly Error NotFound = Error.NotFound("branches.not_found", "Sucursal no encontrada.");
}

internal static class LikePattern
{
    public static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
}
