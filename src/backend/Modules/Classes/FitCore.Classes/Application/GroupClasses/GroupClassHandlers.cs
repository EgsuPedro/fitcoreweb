using FitCore.Application.Core;
using FitCore.Classes.Domain;
using FitCore.Classes.Persistence;
using FitCore.Platform.Application;
using FitCore.Platform.Security;
using FitCore.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FitCore.Classes.Application.GroupClasses;

internal static class GroupClassQueries
{
    /// <summary>Clases de las sucursales a las que el usuario tiene acceso (las demás no existen para él).</summary>
    public static IQueryable<GroupClass> VisibleTo(this IQueryable<GroupClass> classes, UserAccess actor)
    {
        if (actor.AllBranches)
        {
            return classes;
        }

        var branchIds = actor.BranchIds.ToList();
        return classes.Where(c => branchIds.Contains(c.BranchId));
    }

    public static async Task<IReadOnlyList<GroupClassDto>> ToDtosAsync(ClassesDbContext db, IReadOnlyList<GroupClass> classes, CancellationToken cancellationToken)
    {
        var trainerIds = classes.Select(c => c.InstructorId).Distinct().ToList();
        var branchIds = classes.Select(c => c.BranchId).Distinct().ToList();
        var instructorNames = await db.TrainerRefs.AsNoTracking()
            .Where(t => trainerIds.Contains(t.Id))
            .Join(db.UserRefs.AsNoTracking(), t => t.UserId, u => u.Id, (t, u) => new { t.Id, u.FullName })
            .ToDictionaryAsync(x => x.Id, x => x.FullName, cancellationToken);
        var branchNames = await db.BranchRefs.AsNoTracking()
            .Where(b => branchIds.Contains(b.Id))
            .ToDictionaryAsync(b => b.Id, b => b.Name, cancellationToken);

        return [.. classes.Select(c => GroupClassDto.From(c, instructorNames.GetValueOrDefault(c.InstructorId, string.Empty), branchNames.GetValueOrDefault(c.BranchId, string.Empty)))];
    }

    public static async Task<GroupClassDto> ToDtoAsync(ClassesDbContext db, GroupClass groupClass, CancellationToken cancellationToken) =>
        (await ToDtosAsync(db, [groupClass], cancellationToken))[0];

    /// <summary>El instructor debe ser un entrenador activo, con usuario activo, que trabaje en la sucursal de la clase.</summary>
    public static async Task<Error?> CheckInstructorAsync(ClassesDbContext db, Guid instructorId, Guid branchId, CancellationToken cancellationToken)
    {
        var trainer = await db.TrainerRefs.AsNoTracking().FirstOrDefaultAsync(t => t.Id == instructorId, cancellationToken);
        if (trainer is null || !trainer.IsActive || !await db.UserRefs.AsNoTracking().AnyAsync(u => u.Id == trainer.UserId && u.IsActive, cancellationToken))
        {
            return GroupClassErrors.InstructorInvalid;
        }

        return await BranchAccess.UserCanWorkAtAsync(db, trainer.UserId, branchId, cancellationToken) ? null : GroupClassErrors.InstructorNotInBranch;
    }

    public static async Task<bool> NameTakenAsync(ClassesDbContext db, Guid branchId, string name, Guid? exceptId, CancellationToken cancellationToken)
    {
        var pattern = LikePattern.Exact(name);
        return await db.GroupClasses.AsNoTracking()
            .AnyAsync(c => c.BranchId == branchId && c.Id != exceptId && EF.Functions.ILike(c.Name, pattern), cancellationToken);
    }
}

internal sealed class ListGroupClassesHandler(ClassesDbContext db, IAccessContext access)
    : IQueryHandler<ListGroupClassesQuery, PagedResult<GroupClassDto>>
{
    public async Task<Result<PagedResult<GroupClassDto>>> HandleAsync(ListGroupClassesQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        if (query.BranchId is { } requested && !actor.Value.CanAccessBranch(requested))
        {
            return PlatformErrors.BranchNotAllowed;
        }

        var classes = db.GroupClasses.AsNoTracking().VisibleTo(actor.Value);
        if (query.BranchId is { } branchId)
        {
            classes = classes.Where(c => c.BranchId == branchId);
        }

        if (query.Activity is { } activity)
        {
            classes = classes.Where(c => c.Activity == activity);
        }

        if (query.Level is { } level)
        {
            classes = classes.Where(c => c.Level == level);
        }

        if (query.InstructorId is { } instructorId)
        {
            classes = classes.Where(c => c.InstructorId == instructorId);
        }

        if (query.IsActive is { } active)
        {
            classes = classes.Where(c => c.IsActive == active);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var pattern = LikePattern.Contains(query.Search);
            classes = classes.Where(c => EF.Functions.ILike(c.Name, pattern) || (c.Description != null && EF.Functions.ILike(c.Description, pattern)));
        }

        var paged = await classes.OrderBy(c => c.Name).ThenBy(c => c.Id).ToPagedAsync(query.Page, cancellationToken);
        return new PagedResult<GroupClassDto>([.. await GroupClassQueries.ToDtosAsync(db, paged.Items, cancellationToken)], paged.Page, paged.PageSize, paged.TotalCount);
    }
}

internal sealed class GetGroupClassHandler(ClassesDbContext db, IAccessContext access) : IQueryHandler<GetGroupClassQuery, GroupClassDto>
{
    public async Task<Result<GroupClassDto>> HandleAsync(GetGroupClassQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        // Una clase de una sucursal a la que no tiene acceso responde igual que una inexistente: no se revela que existe.
        var groupClass = await db.GroupClasses.AsNoTracking().VisibleTo(actor.Value).FirstOrDefaultAsync(c => c.Id == query.Id, cancellationToken);
        return groupClass is null ? GroupClassErrors.NotFound : await GroupClassQueries.ToDtoAsync(db, groupClass, cancellationToken);
    }
}

internal sealed class CreateGroupClassHandler(ClassesDbContext db, IAccessContext access, IClock clock, ICurrentUser currentUser)
    : ICommandHandler<CreateGroupClassCommand, GroupClassDto>
{
    public async Task<Result<GroupClassDto>> HandleAsync(CreateGroupClassCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        if (command.Request.BranchId is not { } branchId)
        {
            return GroupClassErrors.BranchRequired;
        }

        if (!actor.Value.CanAccessBranch(branchId) || !await db.BranchRefs.AsNoTracking().AnyAsync(b => b.Id == branchId && b.IsActive, cancellationToken))
        {
            return PlatformErrors.BranchNotAllowed;
        }

        var details = command.Request.ToDetails();
        if (GroupClass.CheckCoherence(details) is { } incoherent)
        {
            return incoherent;
        }

        if (await GroupClassQueries.CheckInstructorAsync(db, details.InstructorId, branchId, cancellationToken) is { } instructorError)
        {
            return instructorError;
        }

        if (await GroupClassQueries.NameTakenAsync(db, branchId, details.Name, null, cancellationToken))
        {
            return GroupClassErrors.NameTaken;
        }

        var groupClass = GroupClass.Create(actor.Value.TenantId, branchId, details, currentUser.UserId, clock.UtcNow);
        db.GroupClasses.Add(groupClass);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return GroupClassErrors.NameTaken;
        }

        return await GroupClassQueries.ToDtoAsync(db, groupClass, cancellationToken);
    }
}

internal sealed class UpdateGroupClassHandler(ClassesDbContext db, IAccessContext access, IClock clock, ICurrentUser currentUser)
    : ICommandHandler<UpdateGroupClassCommand, GroupClassDto>
{
    public async Task<Result<GroupClassDto>> HandleAsync(UpdateGroupClassCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var groupClass = await db.GroupClasses.VisibleTo(actor.Value).FirstOrDefaultAsync(c => c.Id == command.Id, cancellationToken);
        if (groupClass is null)
        {
            return GroupClassErrors.NotFound;
        }

        if (command.Request.BranchId is { } branchId && branchId != groupClass.BranchId)
        {
            return GroupClassErrors.BranchImmutable;
        }

        var details = command.Request.ToDetails();
        if (GroupClass.CheckCoherence(details) is { } incoherent)
        {
            return incoherent;
        }

        if (await GroupClassQueries.CheckInstructorAsync(db, details.InstructorId, groupClass.BranchId, cancellationToken) is { } instructorError)
        {
            return instructorError;
        }

        if (await GroupClassQueries.NameTakenAsync(db, groupClass.BranchId, details.Name, groupClass.Id, cancellationToken))
        {
            return GroupClassErrors.NameTaken;
        }

        groupClass.Update(details, currentUser.UserId, clock.UtcNow);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return GroupClassErrors.NameTaken;
        }

        return await GroupClassQueries.ToDtoAsync(db, groupClass, cancellationToken);
    }
}

internal sealed class SetGroupClassStatusHandler(ClassesDbContext db, IAccessContext access, IClock clock, ICurrentUser currentUser)
    : ICommandHandler<SetGroupClassStatusCommand, GroupClassDto>
{
    public async Task<Result<GroupClassDto>> HandleAsync(SetGroupClassStatusCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var groupClass = await db.GroupClasses.VisibleTo(actor.Value).FirstOrDefaultAsync(c => c.Id == command.Id, cancellationToken);
        if (groupClass is null)
        {
            return GroupClassErrors.NotFound;
        }

        // Desactivar solo la saca de la programación nueva: nunca se borra, así las sesiones y reservas históricas conservan su origen.
        groupClass.SetActive(command.Request.IsActive, currentUser.UserId, clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        return await GroupClassQueries.ToDtoAsync(db, groupClass, cancellationToken);
    }
}
