using FitCore.Application.Core;
using FitCore.Platform.Application;
using FitCore.Platform.Security;
using FitCore.SharedKernel;
using FitCore.Wellness.Domain;
using FitCore.Wellness.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FitCore.Wellness.Application.Trainers;

internal static class TrainerQueries
{
    /// <summary>
    /// Entrenadores que el actor puede ver: todos los del gimnasio o los que comparten alguna de sus sucursales.
    /// Un entrenador de otro gimnasio no existe para él (el filtro de tenant lo excluye).
    /// </summary>
    public static IQueryable<TrainerProfile> VisibleTo(this IQueryable<TrainerProfile> trainers, WellnessDbContext db, UserAccess actor)
    {
        if (actor.AllBranches)
        {
            return trainers;
        }

        var ids = actor.BranchIds.ToList();
        return trainers.Where(t => db.UserBranchRefs.Any(ub => ub.UserId == t.UserId && ids.Contains(ub.BranchId)));
    }

    public static async Task<IReadOnlyList<TrainerDto>> ToDtosAsync(WellnessDbContext db, IReadOnlyList<TrainerProfile> trainers, CancellationToken cancellationToken)
    {
        var userIds = trainers.Select(t => t.UserId).ToList();
        var trainerIds = trainers.Select(t => t.Id).ToList();
        var users = await db.UserRefs.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, cancellationToken);
        var branches = (await db.UserBranchRefs.AsNoTracking().Where(ub => userIds.Contains(ub.UserId)).ToListAsync(cancellationToken))
            .ToLookup(ub => ub.UserId, ub => ub.BranchId);
        var counts = await db.TrainerAssignments.AsNoTracking()
            .Where(a => trainerIds.Contains(a.TrainerId) && a.EndedOn == null)
            .GroupBy(a => a.TrainerId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);

        return
        [
            .. trainers.Select(t =>
            {
                users.TryGetValue(t.UserId, out var user);
                return new TrainerDto(
                    t.Id,
                    t.UserId,
                    user?.FullName ?? string.Empty,
                    user?.Email,
                    t.Specialties,
                    t.Certifications,
                    t.Bio,
                    t.Status.ToString(),
                    t.JoinedOn,
                    [.. branches[t.UserId]],
                    counts.GetValueOrDefault(t.Id),
                    t.CreatedAt,
                    t.UpdatedAt);
            })
        ];
    }

    public static async Task<IReadOnlyList<AssignmentDto>> ToAssignmentDtosAsync(WellnessDbContext db, IReadOnlyList<TrainerAssignment> assignments, CancellationToken cancellationToken)
    {
        var memberIds = assignments.Select(a => a.MemberId).Distinct().ToList();
        var trainerIds = assignments.Select(a => a.TrainerId).Distinct().ToList();
        var members = await db.MemberRefs.AsNoTracking().Where(m => memberIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, cancellationToken);
        var trainerUsers = await db.Trainers.AsNoTracking().Where(t => trainerIds.Contains(t.Id))
            .Join(db.UserRefs, t => t.UserId, u => u.Id, (t, u) => new { t.Id, u.FullName })
            .ToDictionaryAsync(x => x.Id, x => x.FullName, cancellationToken);

        return
        [
            .. assignments.Select(a =>
            {
                members.TryGetValue(a.MemberId, out var member);
                return new AssignmentDto(
                    a.Id,
                    a.TrainerId,
                    trainerUsers.GetValueOrDefault(a.TrainerId, string.Empty),
                    a.MemberId,
                    member?.Code ?? string.Empty,
                    member?.FullName ?? string.Empty,
                    a.BranchId,
                    a.AssignedOn,
                    a.EndedOn,
                    a.EndReason,
                    a.IsActive);
            })
        ];
    }

    public static AvailabilityDto ToDto(TrainerAvailability a) =>
        new(a.Id, a.Day.ToString(), a.StartTime.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture), a.EndTime.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture), a.BranchId);

    /// <summary>El perfil de entrenador del usuario autenticado, si lo tiene.</summary>
    public static Task<TrainerProfile?> OwnProfileAsync(WellnessDbContext db, UserAccess actor, CancellationToken cancellationToken) =>
        db.Trainers.FirstOrDefaultAsync(t => t.UserId == actor.UserId, cancellationToken);
}

internal sealed class ListTrainersHandler(WellnessDbContext db, IAccessContext access) : IQueryHandler<ListTrainersQuery, PagedResult<TrainerDto>>
{
    public async Task<Result<PagedResult<TrainerDto>>> HandleAsync(ListTrainersQuery query, CancellationToken cancellationToken)
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

        var trainers = db.Trainers.AsNoTracking().VisibleTo(db, actor.Value);
        if (query.Status is { } status)
        {
            trainers = trainers.Where(t => t.Status == status);
        }

        if (query.BranchId is { } branchId)
        {
            trainers = trainers.Where(t => db.UserBranchRefs.Any(ub => ub.UserId == t.UserId && ub.BranchId == branchId));
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var pattern = LikePattern.Contains(query.Search);
            trainers = trainers.Where(t => db.UserRefs.Any(u => u.Id == t.UserId && (EF.Functions.ILike(u.FullName, pattern) || (u.Email != null && EF.Functions.ILike(u.Email, pattern)))));
        }

        var paged = await trainers
            .OrderBy(t => db.UserRefs.Where(u => u.Id == t.UserId).Select(u => u.FullName).FirstOrDefault())
            .ThenBy(t => t.Id)
            .ToPagedAsync(query.Page, cancellationToken);
        return new PagedResult<TrainerDto>([.. await TrainerQueries.ToDtosAsync(db, paged.Items, cancellationToken)], paged.Page, paged.PageSize, paged.TotalCount);
    }
}

internal sealed class GetTrainerHandler(WellnessDbContext db, IAccessContext access) : IQueryHandler<GetTrainerQuery, TrainerDto>
{
    public async Task<Result<TrainerDto>> HandleAsync(GetTrainerQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var trainer = await db.Trainers.AsNoTracking().VisibleTo(db, actor.Value).FirstOrDefaultAsync(t => t.Id == query.Id, cancellationToken);
        return trainer is null ? TrainerErrors.NotFound : (await TrainerQueries.ToDtosAsync(db, [trainer], cancellationToken))[0];
    }
}

internal sealed class GetMyTrainerHandler(WellnessDbContext db, IAccessContext access) : IQueryHandler<GetMyTrainerQuery, TrainerDto>
{
    public async Task<Result<TrainerDto>> HandleAsync(GetMyTrainerQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var trainer = await TrainerQueries.OwnProfileAsync(db, actor.Value, cancellationToken);
        return trainer is null ? TrainerErrors.NoProfile : (await TrainerQueries.ToDtosAsync(db, [trainer], cancellationToken))[0];
    }
}

internal sealed class CreateTrainerHandler(WellnessDbContext db, IAccessContext access, IClock clock, ICurrentUser currentUser)
    : ICommandHandler<CreateTrainerCommand, TrainerDto>
{
    public async Task<Result<TrainerDto>> HandleAsync(CreateTrainerCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var request = command.Request;
        var branchIds = actor.Value.BranchIds.ToList();
        var user = await db.UserRefs.AsNoTracking().FirstOrDefaultAsync(u => u.Id == request.UserId, cancellationToken);
        var reachable = user is not null
            && (actor.Value.AllBranches
                || await db.UserBranchRefs.AnyAsync(ub => ub.UserId == user.Id && branchIds.Contains(ub.BranchId), cancellationToken));
        if (!reachable)
        {
            // Un usuario de otro gimnasio o de otra sucursal responde igual que uno inexistente.
            return TrainerErrors.UserNotFound;
        }

        if (!user!.IsActive)
        {
            return TrainerErrors.UserInactive;
        }

        if (await db.Trainers.AnyAsync(t => t.UserId == user.Id, cancellationToken))
        {
            return TrainerErrors.AlreadyExists;
        }

        var today = LocalDates.Today(clock, null);
        var trainer = TrainerProfile.Create(
            actor.Value.TenantId, user.Id, request.Specialties, request.Certifications, request.Bio, request.JoinedOn ?? today, currentUser.UserId, clock.UtcNow);
        db.Trainers.Add(trainer);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return TrainerErrors.AlreadyExists;
        }

        return (await TrainerQueries.ToDtosAsync(db, [trainer], cancellationToken))[0];
    }
}

internal sealed class UpdateTrainerHandler(WellnessDbContext db, IAccessContext access, IClock clock, ICurrentUser currentUser)
    : ICommandHandler<UpdateTrainerCommand, TrainerDto>
{
    public async Task<Result<TrainerDto>> HandleAsync(UpdateTrainerCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var trainer = await db.Trainers.VisibleTo(db, actor.Value).FirstOrDefaultAsync(t => t.Id == command.Id, cancellationToken);
        if (trainer is null)
        {
            return TrainerErrors.NotFound;
        }

        var request = command.Request;
        trainer.Update(request.Specialties, request.Certifications, request.Bio, request.JoinedOn, currentUser.UserId, clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        return (await TrainerQueries.ToDtosAsync(db, [trainer], cancellationToken))[0];
    }
}

internal sealed class SetTrainerStatusHandler(WellnessDbContext db, IAccessContext access, IClock clock, ICurrentUser currentUser)
    : ICommandHandler<SetTrainerStatusCommand, TrainerDto>
{
    public async Task<Result<TrainerDto>> HandleAsync(SetTrainerStatusCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var trainer = await db.Trainers.VisibleTo(db, actor.Value).FirstOrDefaultAsync(t => t.Id == command.Id, cancellationToken);
        if (trainer is null)
        {
            return TrainerErrors.NotFound;
        }

        if (!command.Request.IsActive && await db.TrainerAssignments.AnyAsync(a => a.TrainerId == trainer.Id && a.EndedOn == null, cancellationToken))
        {
            return TrainerErrors.HasActiveMembers;
        }

        trainer.SetStatus(command.Request.IsActive ? TrainerStatus.Active : TrainerStatus.Inactive, currentUser.UserId, clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        return (await TrainerQueries.ToDtosAsync(db, [trainer], cancellationToken))[0];
    }
}

internal sealed class GetAvailabilityHandler(WellnessDbContext db, IAccessContext access)
    : IQueryHandler<GetAvailabilityQuery, IReadOnlyList<AvailabilityDto>>
{
    public async Task<Result<IReadOnlyList<AvailabilityDto>>> HandleAsync(GetAvailabilityQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var visible = await db.Trainers.AsNoTracking().VisibleTo(db, actor.Value).AnyAsync(t => t.Id == query.TrainerId, cancellationToken);
        if (!visible)
        {
            return TrainerErrors.NotFound;
        }

        var slots = await db.TrainerAvailability.AsNoTracking().Where(a => a.TrainerId == query.TrainerId)
            .OrderBy(a => a.Day == DayOfWeek.Sunday ? 7 : (int)a.Day).ThenBy(a => a.StartTime).ToListAsync(cancellationToken);
        return slots.Select(TrainerQueries.ToDto).ToList();
    }
}

internal sealed class SetAvailabilityHandler(WellnessDbContext db, IAccessContext access)
    : ICommandHandler<SetAvailabilityCommand, IReadOnlyList<AvailabilityDto>>
{
    public async Task<Result<IReadOnlyList<AvailabilityDto>>> HandleAsync(SetAvailabilityCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var trainer = await db.Trainers.AsNoTracking().VisibleTo(db, actor.Value).FirstOrDefaultAsync(t => t.Id == command.TrainerId, cancellationToken);
        if (trainer is null)
        {
            return TrainerErrors.NotFound;
        }

        // Cada entrenador gestiona su propia disponibilidad; para la de otro hace falta poder administrar entrenadores.
        if (trainer.UserId != actor.Value.UserId && !actor.Value.Has(FitCore.Platform.Domain.Permissions.TrainersManage))
        {
            return TrainerErrors.NotYours;
        }

        var slots = (command.Request.Slots ?? [])
            .Select(s =>
            {
                // El validador ya comprobó el formato de ambas horas.
                return new AvailabilitySlot(s.Day, TrainerRules.ParseTime(s.Start), TrainerRules.ParseTime(s.End), s.BranchId);
            })
            .ToList();

        var problem = AvailabilityRules.Validate(slots);
        if (problem is not null)
        {
            return Error.Validation(problem, problem switch
            {
                "trainers.availability_too_many" => $"Máximo {AvailabilityRules.MaxSlots} franjas.",
                "trainers.availability_invalid_range" => "La hora de fin debe ser posterior a la de inicio.",
                _ => "Las franjas de un mismo día no pueden solaparse."
            });
        }

        var branchIds = slots.Where(s => s.BranchId is not null).Select(s => s.BranchId!.Value).Distinct().ToList();
        if (branchIds.Count > 0)
        {
            var valid = await db.BranchRefs.AsNoTracking().Where(b => branchIds.Contains(b.Id) && b.IsActive).Select(b => b.Id).ToListAsync(cancellationToken);
            if (valid.Count != branchIds.Count
                || !branchIds.All(id => actor.Value.CanAccessBranch(id) || trainer.UserId == actor.Value.UserId)
                || !await AllWorkableAsync(trainer.UserId, branchIds, cancellationToken))
            {
                return TrainerErrors.BranchInvalid;
            }
        }

        var existing = await db.TrainerAvailability.Where(a => a.TrainerId == trainer.Id).ToListAsync(cancellationToken);
        db.TrainerAvailability.RemoveRange(existing);
        var created = slots.Select(s => TrainerAvailability.Create(actor.Value.TenantId, trainer.Id, s.Day, s.Start, s.End, s.BranchId)).ToList();
        db.TrainerAvailability.AddRange(created);
        await db.SaveChangesAsync(cancellationToken);
        return created.OrderBy(a => a.Day == DayOfWeek.Sunday ? 7 : (int)a.Day).ThenBy(a => a.StartTime).Select(TrainerQueries.ToDto).ToList();
    }

    private async Task<bool> AllWorkableAsync(Guid userId, IReadOnlyList<Guid> branchIds, CancellationToken cancellationToken)
    {
        foreach (var branchId in branchIds)
        {
            if (!await BranchAccess.UserCanWorkAtAsync(db, userId, branchId, cancellationToken))
            {
                return false;
            }
        }

        return true;
    }
}

internal sealed class AssignMemberHandler(WellnessDbContext db, IAccessContext access, IClock clock, ICurrentUser currentUser)
    : ICommandHandler<AssignMemberCommand, AssignmentDto>
{
    public async Task<Result<AssignmentDto>> HandleAsync(AssignMemberCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var trainer = await db.Trainers.VisibleTo(db, actor.Value).FirstOrDefaultAsync(t => t.Id == command.TrainerId, cancellationToken);
        if (trainer is null)
        {
            return TrainerErrors.NotFound;
        }

        if (!trainer.IsActive)
        {
            return TrainerErrors.Inactive;
        }

        // Un alumno fuera de las sucursales del usuario o de otro gimnasio no existe para él.
        var member = await db.MemberRefs.AsNoTracking().FirstOrDefaultAsync(m => m.Id == command.Request.MemberId, cancellationToken);
        if (member is null || !actor.Value.CanAccessBranch(member.BranchId))
        {
            return TrainerErrors.MemberNotFound;
        }

        if (!member.IsActive)
        {
            return TrainerErrors.MemberInactive;
        }

        if (!await BranchAccess.UserCanWorkAtAsync(db, trainer.UserId, member.BranchId, cancellationToken))
        {
            return TrainerErrors.BranchMismatch;
        }

        var today = await LocalDates.TodayAtBranchAsync(db, clock, member.BranchId, cancellationToken);
        var current = await db.TrainerAssignments.FirstOrDefaultAsync(a => a.MemberId == member.Id && a.EndedOn == null, cancellationToken);
        if (current?.TrainerId == trainer.Id)
        {
            return TrainerErrors.AlreadyAssigned;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            if (current is not null)
            {
                // Cambio de entrenador: se cierra la asignación anterior (con su motivo) antes de abrir la nueva; el historial se conserva.
                var reason = string.IsNullOrWhiteSpace(command.Request.Reason) ? "Reasignado a otro entrenador" : command.Request.Reason;
                current.End(today, reason, currentUser.UserId);
                await db.SaveChangesAsync(cancellationToken);
            }

            var assignment = TrainerAssignment.Create(actor.Value.TenantId, trainer.Id, member.Id, member.BranchId, today, currentUser.UserId, clock.UtcNow);
            db.TrainerAssignments.Add(assignment);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (await TrainerQueries.ToAssignmentDtosAsync(db, [assignment], cancellationToken))[0];
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.CheckViolation })
        {
            return TrainerErrors.AssignmentConflict;
        }
    }
}

internal sealed class UnassignMemberHandler(WellnessDbContext db, IAccessContext access, IClock clock, ICurrentUser currentUser)
    : ICommandHandler<UnassignMemberCommand, AssignmentDto>
{
    public async Task<Result<AssignmentDto>> HandleAsync(UnassignMemberCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var trainer = await db.Trainers.AsNoTracking().VisibleTo(db, actor.Value).FirstOrDefaultAsync(t => t.Id == command.TrainerId, cancellationToken);
        if (trainer is null)
        {
            return TrainerErrors.NotFound;
        }

        var assignment = await db.TrainerAssignments
            .FirstOrDefaultAsync(a => a.TrainerId == trainer.Id && a.MemberId == command.MemberId && a.EndedOn == null, cancellationToken);
        if (assignment is null || !actor.Value.CanAccessBranch(assignment.BranchId))
        {
            return TrainerErrors.NoActiveAssignment;
        }

        assignment.End(await LocalDates.TodayAtBranchAsync(db, clock, assignment.BranchId, cancellationToken), command.Request.Reason, currentUser.UserId);
        await db.SaveChangesAsync(cancellationToken);
        return (await TrainerQueries.ToAssignmentDtosAsync(db, [assignment], cancellationToken))[0];
    }
}

internal sealed class ListTrainerMembersHandler(WellnessDbContext db, IAccessContext access)
    : IQueryHandler<ListTrainerMembersQuery, PagedResult<AssignmentDto>>
{
    public async Task<Result<PagedResult<AssignmentDto>>> HandleAsync(ListTrainerMembersQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var trainer = await db.Trainers.AsNoTracking().VisibleTo(db, actor.Value).FirstOrDefaultAsync(t => t.Id == query.TrainerId, cancellationToken);
        if (trainer is null)
        {
            return TrainerErrors.NotFound;
        }

        // Un entrenador solo ve a SUS alumnos; ver los de otro exige poder administrar entrenadores.
        if (trainer.UserId != actor.Value.UserId && !actor.Value.Has(FitCore.Platform.Domain.Permissions.TrainersManage))
        {
            return TrainerErrors.NotYours;
        }

        var assignments = db.TrainerAssignments.AsNoTracking().Where(a => a.TrainerId == trainer.Id);
        if (query.ActiveOnly)
        {
            assignments = assignments.Where(a => a.EndedOn == null);
        }

        var paged = await assignments.OrderByDescending(a => a.AssignedOn).ThenBy(a => a.Id).ToPagedAsync(query.Page, cancellationToken);
        return new PagedResult<AssignmentDto>([.. await TrainerQueries.ToAssignmentDtosAsync(db, paged.Items, cancellationToken)], paged.Page, paged.PageSize, paged.TotalCount);
    }
}

internal sealed class ListMemberAssignmentsHandler(WellnessDbContext db, IAccessContext access)
    : IQueryHandler<ListMemberAssignmentsQuery, IReadOnlyList<AssignmentDto>>
{
    public async Task<Result<IReadOnlyList<AssignmentDto>>> HandleAsync(ListMemberAssignmentsQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var member = await db.MemberRefs.AsNoTracking().FirstOrDefaultAsync(m => m.Id == query.MemberId, cancellationToken);
        if (member is null || !actor.Value.CanAccessBranch(member.BranchId))
        {
            return TrainerErrors.MemberNotFound;
        }

        var assignments = await db.TrainerAssignments.AsNoTracking().Where(a => a.MemberId == member.Id)
            .OrderByDescending(a => a.AssignedOn).ThenByDescending(a => a.CreatedAt).ToListAsync(cancellationToken);
        return (await TrainerQueries.ToAssignmentDtosAsync(db, assignments, cancellationToken)).ToList();
    }
}

internal static class NoteAccess
{
    /// <summary>
    /// Las observaciones son de acceso restringido: solo las ve y escribe el entrenador que tiene hoy asignado al alumno.
    /// Ni siquiera quien administra entrenadores las lee (no es un permiso de supervisión).
    /// </summary>
    public static async Task<Result<TrainerProfile>> RequireAssignedTrainerAsync(
        WellnessDbContext db,
        UserAccess actor,
        Guid memberId,
        CancellationToken cancellationToken)
    {
        var trainer = await TrainerQueries.OwnProfileAsync(db, actor, cancellationToken);
        if (trainer is null)
        {
            return TrainerErrors.NoProfile;
        }

        var member = await db.MemberRefs.AsNoTracking().FirstOrDefaultAsync(m => m.Id == memberId, cancellationToken);
        if (member is null)
        {
            return TrainerErrors.MemberNotFound;
        }

        var assigned = await db.TrainerAssignments.AsNoTracking()
            .AnyAsync(a => a.MemberId == memberId && a.TrainerId == trainer.Id && a.EndedOn == null, cancellationToken);
        return assigned && trainer.IsActive ? trainer : TrainerErrors.NotAssigned;
    }
}

internal sealed class CreateTrainerNoteHandler(WellnessDbContext db, IAccessContext access, IClock clock, SecurityAudit audit)
    : ICommandHandler<CreateTrainerNoteCommand, TrainerNoteDto>
{
    public async Task<Result<TrainerNoteDto>> HandleAsync(CreateTrainerNoteCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var trainer = await NoteAccess.RequireAssignedTrainerAsync(db, actor.Value, command.MemberId, cancellationToken);
        if (trainer.IsFailure)
        {
            return trainer.Error;
        }

        var note = TrainerNote.Create(actor.Value.TenantId, command.MemberId, trainer.Value.Id, command.Request.Text, actor.Value.UserId, clock.UtcNow);
        db.TrainerNotes.Add(note);
        await db.SaveChangesAsync(cancellationToken);
        // Se audita el hecho (quién y sobre quién), nunca el texto de la observación.
        await audit.RecordAccessAsync("Wellness.TrainerNoteCreated", "Member", command.MemberId.ToString(), cancellationToken);

        var name = await db.UserRefs.AsNoTracking().Where(u => u.Id == trainer.Value.UserId).Select(u => u.FullName).FirstOrDefaultAsync(cancellationToken);
        return new TrainerNoteDto(note.Id, note.MemberId, note.TrainerId, name ?? string.Empty, note.Text, note.CreatedAt);
    }
}

internal sealed class ListTrainerNotesHandler(WellnessDbContext db, IAccessContext access, SecurityAudit audit)
    : IQueryHandler<ListTrainerNotesQuery, PagedResult<TrainerNoteDto>>
{
    public async Task<Result<PagedResult<TrainerNoteDto>>> HandleAsync(ListTrainerNotesQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var trainer = await NoteAccess.RequireAssignedTrainerAsync(db, actor.Value, query.MemberId, cancellationToken);
        if (trainer.IsFailure)
        {
            return trainer.Error;
        }

        var paged = await db.TrainerNotes.AsNoTracking().Where(n => n.MemberId == query.MemberId)
            .OrderByDescending(n => n.CreatedAt).ThenBy(n => n.Id).ToPagedAsync(query.Page, cancellationToken);
        var authorIds = paged.Items.Select(n => n.TrainerId).Distinct().ToList();
        var authors = await db.Trainers.AsNoTracking().Where(t => authorIds.Contains(t.Id))
            .Join(db.UserRefs, t => t.UserId, u => u.Id, (t, u) => new { t.Id, u.FullName })
            .ToDictionaryAsync(x => x.Id, x => x.FullName, cancellationToken);

        await audit.RecordAccessAsync("Wellness.TrainerNotesRead", "Member", query.MemberId.ToString(), cancellationToken);
        var items = paged.Items.Select(n => new TrainerNoteDto(n.Id, n.MemberId, n.TrainerId, authors.GetValueOrDefault(n.TrainerId, string.Empty), n.Text, n.CreatedAt));
        return new PagedResult<TrainerNoteDto>([.. items], paged.Page, paged.PageSize, paged.TotalCount);
    }
}
