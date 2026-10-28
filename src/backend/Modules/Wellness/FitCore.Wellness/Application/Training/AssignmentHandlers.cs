using FitCore.Application.Core;
using FitCore.Platform.Security;
using FitCore.SharedKernel;
using FitCore.Wellness.Domain;
using FitCore.Wellness.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FitCore.Wellness.Application.Training;

/// <summary>El alumno sobre el que se actúa y si quien actúa es el propio alumno.</summary>
internal sealed record MemberAccessScope(MemberRef Member, bool IsSelf);

internal static class MemberScope
{
    /// <summary>
    /// Resuelve a qué alumno puede acceder el usuario, SIEMPRE desde su sesión (nunca confiando en un identificador del cliente):
    /// un alumno solo accede a sí mismo; el personal necesita acceso a la sucursal del alumno y ser su entrenador asignado o tener
    /// <c>wellness.members.all</c>. Todo lo demás responde como si el alumno no existiera.
    /// </summary>
    public static async Task<Result<MemberAccessScope>> ResolveAsync(WellnessDbContext db, UserAccess actor, Guid memberId, CancellationToken cancellationToken)
    {
        var own = await db.MemberRefs.AsNoTracking().FirstOrDefaultAsync(m => m.UserId == actor.UserId, cancellationToken);
        if (own is not null)
        {
            return own.Id == memberId ? new MemberAccessScope(own, true) : TrainingErrors.MemberNotFound;
        }

        var member = await db.MemberRefs.AsNoTracking().FirstOrDefaultAsync(m => m.Id == memberId, cancellationToken);
        if (member is null || !actor.CanAccessBranch(member.BranchId))
        {
            return TrainingErrors.MemberNotFound;
        }

        if (actor.Has(FitCore.Platform.Domain.Permissions.WellnessMembersAll))
        {
            return new MemberAccessScope(member, false);
        }

        var assigned = await db.TrainerAssignments.AsNoTracking()
            .AnyAsync(a => a.MemberId == memberId && a.EndedOn == null && db.Trainers.Any(t => t.Id == a.TrainerId && t.UserId == actor.UserId), cancellationToken);
        return assigned ? new MemberAccessScope(member, false) : TrainingErrors.MemberNotFound;
    }
}

internal static class AssignmentMapper
{
    public static string StatusOf(TrainingAssignment a, DateOnly today) =>
        a.StartDate > today ? "Scheduled" : a.EndedOn is not null && a.EndedOn < today ? "Ended" : a.EndDate is { } end && end < today ? "Expired" : "Current";

    public static async Task<IReadOnlyList<TrainingAssignmentDto>> ToDtosAsync(WellnessDbContext db, IReadOnlyList<TrainingAssignment> assignments, DateOnly today, CancellationToken cancellationToken)
    {
        var versionIds = assignments.Select(a => a.PlanVersionId).Distinct().ToList();
        var memberIds = assignments.Select(a => a.MemberId).Distinct().ToList();
        var versions = await db.PlanVersions.AsNoTracking().Where(v => versionIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, cancellationToken);
        var members = await db.MemberRefs.AsNoTracking().Where(m => memberIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, cancellationToken);
        return
        [
            .. assignments.Select(a =>
            {
                versions.TryGetValue(a.PlanVersionId, out var version);
                members.TryGetValue(a.MemberId, out var member);
                return new TrainingAssignmentDto(
                    a.Id, a.PlanId, a.PlanVersionId, version?.Name ?? string.Empty, version?.Number ?? 0, a.MemberId,
                    member?.FullName ?? string.Empty, member?.Code ?? string.Empty, a.StartDate, a.EndDate, a.EndedOn, a.EndReason, StatusOf(a, today));
            })
        ];
    }

    /// <summary>La asignación que rige en una fecha: la más reciente entre las vigentes (si dos se solapan un día, manda la nueva).</summary>
    public static async Task<TrainingAssignment?> CurrentAsync(WellnessDbContext db, Guid memberId, DateOnly day, CancellationToken cancellationToken)
    {
        var candidates = await db.TrainingAssignments.AsNoTracking()
            .Where(a => a.MemberId == memberId && a.StartDate <= day)
            .OrderByDescending(a => a.StartDate).ThenByDescending(a => a.CreatedAt)
            .Take(10)
            .ToListAsync(cancellationToken);
        return candidates.FirstOrDefault(a => a.IsCurrentOn(day));
    }
}

internal sealed class AssignPlanHandler(WellnessDbContext db, IAccessContext access, IClock clock)
    : ICommandHandler<AssignPlanCommand, TrainingAssignmentDto>
{
    public async Task<Result<TrainingAssignmentDto>> HandleAsync(AssignPlanCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var plan = await db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == command.PlanId, cancellationToken);
        var version = plan is null ? null : await db.PlanVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == plan.CurrentVersionId, cancellationToken);
        if (plan is null || version is null)
        {
            return TrainingErrors.PlanNotFound;
        }

        var scope = await MemberScope.ResolveAsync(db, actor.Value, command.Request.MemberId, cancellationToken);
        if (scope.IsFailure)
        {
            return scope.Error;
        }

        var member = scope.Value.Member;
        if (plan.Status == PlanStatus.Archived)
        {
            return TrainingErrors.PlanArchived;
        }

        if (!member.IsActive)
        {
            return TrainingErrors.MemberInactive;
        }

        var today = await LocalDates.TodayAtBranchAsync(db, clock, member.BranchId, cancellationToken);
        if (version.ValidUntil is { } validUntil && validUntil < today)
        {
            return TrainingErrors.PlanExpired;
        }

        var start = command.Request.StartDate ?? today;
        var end = command.Request.EndDate;
        if (start < today || (end is { } e && (e < start || e.DayNumber - start.DayNumber > TrainingAssignment.MaxDurationDays)))
        {
            return TrainingErrors.InvalidDates;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            // Cambiar de rutina cierra la anterior (con su motivo) y deja su historial intacto.
            var open = await db.TrainingAssignments.FirstOrDefaultAsync(a => a.MemberId == member.Id && a.EndedOn == null, cancellationToken);
            if (open is not null)
            {
                // La anterior rige hasta el día previo al inicio de la nueva (o nunca, si ni siquiera había empezado).
                var lastDay = start.AddDays(-1) < open.StartDate.AddDays(-1) ? open.StartDate.AddDays(-1) : start.AddDays(-1);
                open.End(lastDay, "Reemplazada por otra rutina");
                await db.SaveChangesAsync(cancellationToken);
            }

            var assignment = TrainingAssignment.Create(actor.Value.TenantId, plan.Id, version.Id, member.Id, member.BranchId, start, end, actor.Value.UserId, clock.UtcNow);
            db.TrainingAssignments.Add(assignment);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (await AssignmentMapper.ToDtosAsync(db, [assignment], today, cancellationToken))[0];
        }
        // Dos asignaciones simultáneas: la base deja pasar a una. A la otra le llega un índice único violado (dos abiertas) o el disparador de
        // «asignación cerrada no se modifica» (la otra ya cerró la vigente); en ambos casos es un conflicto, no un error del servidor.
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.CheckViolation })
        {
            return Error.Conflict("training.assignment_conflict", "El alumno cambió de rutina al mismo tiempo; intente de nuevo.");
        }
    }
}

internal sealed class EndAssignmentHandler(WellnessDbContext db, IAccessContext access, IClock clock)
    : ICommandHandler<EndAssignmentCommand, TrainingAssignmentDto>
{
    public async Task<Result<TrainingAssignmentDto>> HandleAsync(EndAssignmentCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var assignment = await db.TrainingAssignments.FirstOrDefaultAsync(a => a.Id == command.Id, cancellationToken);
        if (assignment is null)
        {
            return TrainingErrors.AssignmentNotFound;
        }

        var scope = await MemberScope.ResolveAsync(db, actor.Value, assignment.MemberId, cancellationToken);
        if (scope.IsFailure)
        {
            return TrainingErrors.AssignmentNotFound;
        }

        if (assignment.EndedOn is not null)
        {
            return TrainingErrors.AssignmentEnded;
        }

        var today = await LocalDates.TodayAtBranchAsync(db, clock, assignment.BranchId, cancellationToken);
        // Cerrar es inmediato: ayer fue el último día (o ninguno, si todavía no había empezado).
        var lastDay = today.AddDays(-1) < assignment.StartDate.AddDays(-1) ? assignment.StartDate.AddDays(-1) : today.AddDays(-1);
        assignment.End(lastDay, command.Request.Reason);
        await db.SaveChangesAsync(cancellationToken);
        return (await AssignmentMapper.ToDtosAsync(db, [assignment], today, cancellationToken))[0];
    }
}

internal sealed class GetMemberPlanHandler(WellnessDbContext db, IAccessContext access, IClock clock)
    : IQueryHandler<GetMemberPlanQuery, MemberTrainingPlanDto>
{
    public async Task<Result<MemberTrainingPlanDto>> HandleAsync(GetMemberPlanQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var scope = await MemberScope.ResolveAsync(db, actor.Value, query.MemberId, cancellationToken);
        if (scope.IsFailure)
        {
            return scope.Error;
        }

        var today = await LocalDates.TodayAtBranchAsync(db, clock, scope.Value.Member.BranchId, cancellationToken);
        var assignment = await AssignmentMapper.CurrentAsync(db, query.MemberId, today, cancellationToken);
        if (assignment is null)
        {
            return TrainingErrors.NoActivePlan;
        }

        var version = await db.PlanVersions.AsNoTracking().FirstAsync(v => v.Id == assignment.PlanVersionId, cancellationToken);
        var days = await db.PlanDays.AsNoTracking().Where(d => d.VersionId == version.Id).OrderBy(d => d.Position).ToListAsync(cancellationToken);
        var dayIds = days.Select(d => d.Id).ToList();
        var exercises = await db.PlanExercises.AsNoTracking().Where(e => dayIds.Contains(e.DayId)).OrderBy(e => e.Position).ToListAsync(cancellationToken);
        var exerciseIds = exercises.Select(e => e.ExerciseId).Distinct().ToList();
        var guides = await db.Exercises.AsNoTracking().Where(e => exerciseIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, cancellationToken);
        var byDay = exercises.ToLookup(e => e.DayId);

        // La guía (instrucciones, errores, advertencias) viene de la biblioteca viva; el nombre y la prescripción, de la versión asignada.
        var dtos = (await AssignmentMapper.ToDtosAsync(db, [assignment], today, cancellationToken))[0];
        return new MemberTrainingPlanDto(
            dtos,
            version.Goal,
            version.Description,
            version.Level.ToString(),
            version.EstimatedMinutes,
            [
                .. days.Select(d => new MemberPlanDayDto(
                    d.Id,
                    d.Position,
                    d.Name,
                    d.Notes,
                    [.. byDay[d.Id].Select(e => new MemberPlanExerciseDto(PlanContent.ToDto(e), guides.TryGetValue(e.ExerciseId, out var g) ? Guide(g) : null))]))
            ]);
    }

    private static ExerciseGuideDto Guide(Exercise e) =>
        new(e.Id, e.Name, e.Description, e.PrimaryMuscle.ToString(), e.Equipment.ToString(), e.Instructions, e.CommonMistakes, e.Warnings, e.VideoUrl);
}

internal sealed class ListMemberTrainingAssignmentsHandler(WellnessDbContext db, IAccessContext access, IClock clock)
    : IQueryHandler<ListMemberTrainingAssignmentsQuery, IReadOnlyList<TrainingAssignmentDto>>
{
    public async Task<Result<IReadOnlyList<TrainingAssignmentDto>>> HandleAsync(ListMemberTrainingAssignmentsQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var scope = await MemberScope.ResolveAsync(db, actor.Value, query.MemberId, cancellationToken);
        if (scope.IsFailure)
        {
            return scope.Error;
        }

        var today = await LocalDates.TodayAtBranchAsync(db, clock, scope.Value.Member.BranchId, cancellationToken);
        var assignments = await db.TrainingAssignments.AsNoTracking().Where(a => a.MemberId == query.MemberId)
            .OrderByDescending(a => a.StartDate).ThenByDescending(a => a.CreatedAt).ToListAsync(cancellationToken);
        return (await AssignmentMapper.ToDtosAsync(db, assignments, today, cancellationToken)).ToList();
    }
}
