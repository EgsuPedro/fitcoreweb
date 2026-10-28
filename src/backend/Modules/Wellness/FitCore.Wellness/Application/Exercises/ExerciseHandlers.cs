using FitCore.Application.Core;
using FitCore.Platform.Security;
using FitCore.SharedKernel;
using FitCore.Wellness.Domain;
using FitCore.Wellness.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FitCore.Wellness.Application.Exercises;

internal static class ExerciseQueries
{
    /// <summary>Nombre libre en el ámbito dado (catálogo base o gimnasio), sin distinguir mayúsculas.</summary>
    public static async Task<bool> NameTakenAsync(WellnessDbContext db, Guid? tenantId, string name, Guid? exceptId, CancellationToken cancellationToken)
    {
        var pattern = LikePattern.Exact(name);
        return await db.Exercises.AsNoTracking()
            .AnyAsync(e => e.TenantId == tenantId && e.Id != exceptId && EF.Functions.ILike(e.Name, pattern), cancellationToken);
    }
}

internal sealed class ListExercisesHandler(WellnessDbContext db, IAccessContext access)
    : IQueryHandler<ListExercisesQuery, PagedResult<ExerciseDto>>
{
    public async Task<Result<PagedResult<ExerciseDto>>> HandleAsync(ListExercisesQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        // El filtro de la base ya limita a globales + los de este gimnasio.
        var exercises = db.Exercises.AsNoTracking().AsQueryable();
        exercises = query.Scope?.ToLowerInvariant() switch
        {
            "global" => exercises.Where(e => e.TenantId == null),
            "own" => exercises.Where(e => e.TenantId != null),
            _ => exercises
        };

        if (query.IsActive is { } active)
        {
            exercises = exercises.Where(e => e.IsActive == active);
        }

        if (query.Muscle is { } muscle)
        {
            var name = muscle.ToString();
            exercises = exercises.Where(e => e.PrimaryMuscle == muscle || e.SecondaryMuscles.Contains(name));
        }

        if (query.Equipment is { } equipment)
        {
            exercises = exercises.Where(e => e.Equipment == equipment);
        }

        if (query.Difficulty is { } difficulty)
        {
            exercises = exercises.Where(e => e.Difficulty == difficulty);
        }

        if (query.Type is { } type)
        {
            exercises = exercises.Where(e => e.Type == type);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var pattern = LikePattern.Contains(query.Search);
            exercises = exercises.Where(e => EF.Functions.ILike(e.Name, pattern) || (e.Description != null && EF.Functions.ILike(e.Description, pattern)));
        }

        var paged = await exercises.OrderBy(e => e.Name).ThenBy(e => e.Id).ToPagedAsync(query.Page, cancellationToken);
        return new PagedResult<ExerciseDto>([.. paged.Items.Select(ExerciseDto.From)], paged.Page, paged.PageSize, paged.TotalCount);
    }
}

internal sealed class GetExerciseHandler(WellnessDbContext db, IAccessContext access) : IQueryHandler<GetExerciseQuery, ExerciseDto>
{
    public async Task<Result<ExerciseDto>> HandleAsync(GetExerciseQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var exercise = await db.Exercises.AsNoTracking().FirstOrDefaultAsync(e => e.Id == query.Id, cancellationToken);
        return exercise is null ? ExerciseErrors.NotFound : ExerciseDto.From(exercise);
    }
}

internal sealed class CreateExerciseHandler(WellnessDbContext db, IAccessContext access, IClock clock, ICurrentUser currentUser)
    : ICommandHandler<CreateExerciseCommand, ExerciseDto>
{
    public async Task<Result<ExerciseDto>> HandleAsync(CreateExerciseCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        Guid? tenantId = command.Global ? null : actor.Value.TenantId;
        var details = command.Request.ToDetails();
        if (await ExerciseQueries.NameTakenAsync(db, tenantId, details.Name, null, cancellationToken))
        {
            return ExerciseErrors.NameTaken;
        }

        var exercise = Exercise.Create(tenantId, details, currentUser.UserId, clock.UtcNow);
        db.Exercises.Add(exercise);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return ExerciseErrors.NameTaken;
        }

        return ExerciseDto.From(exercise);
    }
}

internal sealed class UpdateExerciseHandler(WellnessDbContext db, IAccessContext access, IClock clock, ICurrentUser currentUser)
    : ICommandHandler<UpdateExerciseCommand, ExerciseDto>
{
    public async Task<Result<ExerciseDto>> HandleAsync(UpdateExerciseCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var exercise = await db.Exercises.FirstOrDefaultAsync(e => e.Id == command.Id, cancellationToken);
        if (exercise is null || (command.Global && !exercise.IsGlobal))
        {
            return ExerciseErrors.NotFound;
        }

        // Un gimnasio no edita el catálogo base: lo duplica. Solo plataforma lo modifica.
        if (exercise.IsGlobal && !command.Global)
        {
            return ExerciseErrors.GlobalReadOnly;
        }

        var details = command.Request.ToDetails();
        if (await ExerciseQueries.NameTakenAsync(db, exercise.TenantId, details.Name, exercise.Id, cancellationToken))
        {
            return ExerciseErrors.NameTaken;
        }

        exercise.Update(details, currentUser.UserId, clock.UtcNow);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return ExerciseErrors.NameTaken;
        }

        return ExerciseDto.From(exercise);
    }
}

internal sealed class SetExerciseStatusHandler(WellnessDbContext db, IAccessContext access, IClock clock, ICurrentUser currentUser)
    : ICommandHandler<SetExerciseStatusCommand, ExerciseDto>
{
    public async Task<Result<ExerciseDto>> HandleAsync(SetExerciseStatusCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var exercise = await db.Exercises.FirstOrDefaultAsync(e => e.Id == command.Id, cancellationToken);
        if (exercise is null || (command.Global && !exercise.IsGlobal))
        {
            return ExerciseErrors.NotFound;
        }

        if (exercise.IsGlobal && !command.Global)
        {
            return ExerciseErrors.GlobalReadOnly;
        }

        // Desactivar solo lo oculta de las nuevas rutinas: las rutinas y sesiones históricas siguen mostrándolo.
        exercise.SetActive(command.Request.IsActive, currentUser.UserId, clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        return ExerciseDto.From(exercise);
    }
}

internal sealed class DuplicateExerciseHandler(WellnessDbContext db, IAccessContext access, IClock clock, ICurrentUser currentUser)
    : ICommandHandler<DuplicateExerciseCommand, ExerciseDto>
{
    public async Task<Result<ExerciseDto>> HandleAsync(DuplicateExerciseCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var source = await db.Exercises.AsNoTracking().FirstOrDefaultAsync(e => e.Id == command.Id, cancellationToken);
        if (source is null)
        {
            return ExerciseErrors.NotFound;
        }

        var name = $"{source.Name} (copia)";
        for (var i = 2; await ExerciseQueries.NameTakenAsync(db, actor.Value.TenantId, name, null, cancellationToken); i++)
        {
            name = $"{source.Name} (copia {i})";
        }

        var details = new ExerciseDetails(
            name,
            source.Description,
            source.PrimaryMuscle,
            [.. source.SecondaryMuscles.Select(Enum.Parse<MuscleGroup>)],
            source.Type,
            source.Equipment,
            source.Difficulty,
            source.TrackingMode,
            source.Instructions,
            source.CommonMistakes,
            source.Warnings,
            source.VideoUrl);
        var copy = Exercise.Create(actor.Value.TenantId, details, currentUser.UserId, clock.UtcNow);
        db.Exercises.Add(copy);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return ExerciseErrors.NameTaken;
        }

        return ExerciseDto.From(copy);
    }
}
