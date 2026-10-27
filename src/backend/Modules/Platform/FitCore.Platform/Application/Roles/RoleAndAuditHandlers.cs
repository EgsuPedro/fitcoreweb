using FitCore.Application.Core;
using FitCore.Platform.Domain;
using FitCore.Platform.Persistence;
using FitCore.Platform.Security;
using FitCore.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace FitCore.Platform.Application.Roles;

public sealed record RoleDto(
    Guid Id,
    string Name,
    string? Description,
    bool IsSystem,
    bool GrantsAllBranches,
    IReadOnlyList<string> Permissions);

public sealed record PermissionDto(string Code, string Module, string Description);

public sealed record AuditLogDto(
    long Id,
    Guid? UserId,
    string Action,
    string Entity,
    string? EntityId,
    string? Before,
    string? After,
    string? IpAddress,
    DateTimeOffset OccurredAt);

public sealed record ListRolesQuery : IQuery<IReadOnlyList<RoleDto>>;

public sealed record ListPermissionsQuery : IQuery<IReadOnlyList<PermissionDto>>;

public sealed record ListAuditLogsQuery(PageRequest Page, string? Entity, string? EntityId, DateTimeOffset? From, DateTimeOffset? To)
    : IQuery<PagedResult<AuditLogDto>>;

internal sealed class ListRolesHandler(PlatformDbContext db, IAccessContext access) : IQueryHandler<ListRolesQuery, IReadOnlyList<RoleDto>>
{
    public async Task<Result<IReadOnlyList<RoleDto>>> HandleAsync(ListRolesQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var roles = await db.Roles.AsNoTracking().OrderBy(r => r.Name).ToListAsync(cancellationToken);
        var permissions = await db.RolePermissions.AsNoTracking()
            .Select(rp => new { rp.RoleId, rp.PermissionCode })
            .ToListAsync(cancellationToken);

        IReadOnlyList<RoleDto> result =
        [
            .. roles.Select(r => new RoleDto(
                r.Id,
                r.Name!,
                r.Description,
                r.IsSystem,
                r.GrantsAllBranches,
                [.. permissions.Where(p => p.RoleId == r.Id).Select(p => p.PermissionCode).Order()]))
        ];
        return Result.Success(result);
    }
}

internal sealed class ListPermissionsHandler(PlatformDbContext db, IAccessContext access)
    : IQueryHandler<ListPermissionsQuery, IReadOnlyList<PermissionDto>>
{
    public async Task<Result<IReadOnlyList<PermissionDto>>> HandleAsync(ListPermissionsQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var catalog = await db.PermissionCatalog.AsNoTracking().OrderBy(p => p.Module).ThenBy(p => p.Code).ToListAsync(cancellationToken);

        // Los permisos de plataforma solo los ve quien los tiene.
        IReadOnlyList<PermissionDto> result =
        [
            .. catalog
                .Where(p => !Permissions.IsPlatform(p.Code) || actor.Value.Has(p.Code))
                .Select(p => new PermissionDto(p.Code, p.Module, p.Description))
        ];
        return Result.Success(result);
    }
}

internal sealed class ListAuditLogsHandler(PlatformDbContext db, IAccessContext access)
    : IQueryHandler<ListAuditLogsQuery, PagedResult<AuditLogDto>>
{
    public async Task<Result<PagedResult<AuditLogDto>>> HandleAsync(ListAuditLogsQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var logs = db.AuditLogs.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.Entity))
        {
            var entity = query.Entity.Trim();
            logs = logs.Where(l => l.Entity == entity);
        }

        if (!string.IsNullOrWhiteSpace(query.EntityId))
        {
            var entityId = query.EntityId.Trim();
            logs = logs.Where(l => l.EntityId == entityId);
        }

        if (query.From is { } from)
        {
            logs = logs.Where(l => l.OccurredAt >= from);
        }

        if (query.To is { } to)
        {
            logs = logs.Where(l => l.OccurredAt <= to);
        }

        var paged = await logs.OrderByDescending(l => l.Id).ToPagedAsync(query.Page, cancellationToken);
        return new PagedResult<AuditLogDto>(
            [.. paged.Items.Select(l => new AuditLogDto(l.Id, l.UserId, l.Action, l.Entity, l.EntityId, l.Before, l.After, l.IpAddress, l.OccurredAt))],
            paged.Page,
            paged.PageSize,
            paged.TotalCount);
    }
}
