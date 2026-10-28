using FitCore.Application.Core;
using FitCore.Platform.Application;
using FitCore.Platform.Security;
using FitCore.SharedKernel;
using FitCore.Wellness.Domain;
using FitCore.Wellness.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FitCore.Wellness.Application.Nutrition;

internal sealed record NutritionMemberScope(MemberRef Member, bool IsSelf);

internal static class NutritionScope
{
    /// <summary>
    /// Resuelve a qué alumno puede acceder el usuario, SIEMPRE desde su sesión: un alumno solo accede a sí mismo; el personal necesita
    /// acceso a la sucursal del alumno (y el permiso, sensible, que el endpoint exige y que ni el propietario ni el administrador tienen
    /// por defecto). Un entrenador no tiene permisos de nutrición. Todo lo demás responde como si el alumno no existiera.
    /// </summary>
    public static async Task<Result<NutritionMemberScope>> ResolveAsync(WellnessDbContext db, UserAccess actor, Guid memberId, CancellationToken cancellationToken)
    {
        var own = await db.MemberRefs.AsNoTracking().FirstOrDefaultAsync(m => m.UserId == actor.UserId, cancellationToken);
        if (own is not null)
        {
            return own.Id == memberId ? new NutritionMemberScope(own, true) : NutritionErrors.MemberNotFound;
        }

        var member = await db.MemberRefs.AsNoTracking().FirstOrDefaultAsync(m => m.Id == memberId, cancellationToken);
        return member is null || !actor.CanAccessBranch(member.BranchId) ? NutritionErrors.MemberNotFound : new NutritionMemberScope(member, false);
    }
}

internal static class NutritionAssignmentMapper
{
    public static string StatusOf(NutritionAssignment a, DateOnly today) =>
        a.StartDate > today ? "Scheduled" : a.EndedOn is not null && a.EndedOn < today ? "Ended" : a.EndDate is { } end && end < today ? "Expired" : "Current";

    public static async Task<IReadOnlyList<NutritionAssignmentDto>> ToDtosAsync(WellnessDbContext db, IReadOnlyList<NutritionAssignment> assignments, DateOnly today, CancellationToken cancellationToken)
    {
        var versionIds = assignments.Select(a => a.PlanVersionId).Distinct().ToList();
        var memberIds = assignments.Select(a => a.MemberId).Distinct().ToList();
        var versions = await db.NutritionVersions.AsNoTracking().Where(v => versionIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, cancellationToken);
        var members = await db.MemberRefs.AsNoTracking().Where(m => memberIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, cancellationToken);
        return
        [
            .. assignments.Select(a =>
            {
                versions.TryGetValue(a.PlanVersionId, out var version);
                members.TryGetValue(a.MemberId, out var member);
                return new NutritionAssignmentDto(
                    a.Id, a.PlanId, a.PlanVersionId, version?.Name ?? string.Empty, version?.Number ?? 0, a.MemberId,
                    member?.FullName ?? string.Empty, member?.Code ?? string.Empty, a.StartDate, a.EndDate, a.EndedOn, a.EndReason, StatusOf(a, today));
            })
        ];
    }

    /// <summary>La asignación que rige en una fecha: la más reciente entre las vigentes.</summary>
    public static async Task<NutritionAssignment?> CurrentAsync(WellnessDbContext db, Guid memberId, DateOnly day, CancellationToken cancellationToken)
    {
        var candidates = await db.NutritionAssignments.AsNoTracking()
            .Where(a => a.MemberId == memberId && a.StartDate <= day)
            .OrderByDescending(a => a.StartDate).ThenByDescending(a => a.CreatedAt)
            .Take(10)
            .ToListAsync(cancellationToken);
        return candidates.FirstOrDefault(a => a.IsCurrentOn(day));
    }

    public static DietaryProfileDto ToDto(DietaryProfile p) =>
        new(p.Id, p.MemberId, p.Allergies, p.Intolerances, p.Restrictions, p.Notes, p.RecordedAt, p.RecordedBy);
}

internal sealed class AssignNutritionPlanHandler(WellnessDbContext db, IAccessContext access, IClock clock, SecurityAudit audit)
    : ICommandHandler<AssignNutritionPlanCommand, NutritionAssignmentDto>
{
    public async Task<Result<NutritionAssignmentDto>> HandleAsync(AssignNutritionPlanCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var plan = await db.NutritionPlans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == command.PlanId, cancellationToken);
        var version = plan is null ? null : await db.NutritionVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == plan.CurrentVersionId, cancellationToken);
        if (plan is null || version is null)
        {
            return NutritionErrors.PlanNotFound;
        }

        var scope = await NutritionScope.ResolveAsync(db, actor.Value, command.Request.MemberId, cancellationToken);
        if (scope.IsFailure)
        {
            return scope.Error;
        }

        var member = scope.Value.Member;
        if (scope.Value.IsSelf)
        {
            return NutritionErrors.MemberNotFound;
        }

        if (plan.Status == PlanStatus.Archived)
        {
            return NutritionErrors.PlanArchived;
        }

        if (!member.IsActive)
        {
            return NutritionErrors.MemberInactive;
        }

        var today = await LocalDates.TodayAtBranchAsync(db, clock, member.BranchId, cancellationToken);
        if (version.ValidUntil is { } validUntil && validUntil < today)
        {
            return NutritionErrors.PlanExpired;
        }

        var start = command.Request.StartDate ?? today;
        var end = command.Request.EndDate;
        if (start < today || (end is { } e && (e < start || e.DayNumber - start.DayNumber > NutritionAssignment.MaxDurationDays)))
        {
            return NutritionErrors.InvalidDates;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            // Cambiar de plan cierra el anterior (con su motivo) y deja su historial intacto.
            var open = await db.NutritionAssignments.FirstOrDefaultAsync(a => a.MemberId == member.Id && a.EndedOn == null, cancellationToken);
            if (open is not null)
            {
                var lastDay = start.AddDays(-1) < open.StartDate.AddDays(-1) ? open.StartDate.AddDays(-1) : start.AddDays(-1);
                open.End(lastDay, "Reemplazado por otro plan");
                await db.SaveChangesAsync(cancellationToken);
            }

            var assignment = NutritionAssignment.Create(actor.Value.TenantId, plan.Id, version.Id, member.Id, member.BranchId, start, end, actor.Value.UserId, clock.UtcNow);
            db.NutritionAssignments.Add(assignment);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            await audit.RecordAccessAsync("Wellness.NutritionPlanAssigned", "Member", member.Id.ToString(), cancellationToken);
            return (await NutritionAssignmentMapper.ToDtosAsync(db, [assignment], today, cancellationToken))[0];
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return Error.Conflict("nutrition.assignment_conflict", "El alumno cambió de plan al mismo tiempo; intente de nuevo.");
        }
    }
}

internal sealed class EndNutritionAssignmentHandler(WellnessDbContext db, IAccessContext access, IClock clock)
    : ICommandHandler<EndNutritionAssignmentCommand, NutritionAssignmentDto>
{
    public async Task<Result<NutritionAssignmentDto>> HandleAsync(EndNutritionAssignmentCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var assignment = await db.NutritionAssignments.FirstOrDefaultAsync(a => a.Id == command.Id, cancellationToken);
        if (assignment is null)
        {
            return NutritionErrors.AssignmentNotFound;
        }

        var scope = await NutritionScope.ResolveAsync(db, actor.Value, assignment.MemberId, cancellationToken);
        if (scope.IsFailure || scope.Value.IsSelf)
        {
            return NutritionErrors.AssignmentNotFound;
        }

        if (assignment.EndedOn is not null)
        {
            return NutritionErrors.AssignmentEnded;
        }

        var today = await LocalDates.TodayAtBranchAsync(db, clock, assignment.BranchId, cancellationToken);
        var lastDay = today.AddDays(-1) < assignment.StartDate.AddDays(-1) ? assignment.StartDate.AddDays(-1) : today.AddDays(-1);
        assignment.End(lastDay, command.Request.Reason);
        await db.SaveChangesAsync(cancellationToken);
        return (await NutritionAssignmentMapper.ToDtosAsync(db, [assignment], today, cancellationToken))[0];
    }
}

internal sealed class GetMemberNutritionPlanHandler(WellnessDbContext db, IAccessContext access, IClock clock, SecurityAudit audit)
    : IQueryHandler<GetMemberNutritionPlanQuery, MemberNutritionPlanDto>
{
    public async Task<Result<MemberNutritionPlanDto>> HandleAsync(GetMemberNutritionPlanQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var scope = await NutritionScope.ResolveAsync(db, actor.Value, query.MemberId, cancellationToken);
        if (scope.IsFailure)
        {
            return scope.Error;
        }

        var today = await LocalDates.TodayAtBranchAsync(db, clock, scope.Value.Member.BranchId, cancellationToken);
        var assignment = await NutritionAssignmentMapper.CurrentAsync(db, query.MemberId, today, cancellationToken);
        if (assignment is null)
        {
            return NutritionErrors.NoActivePlan;
        }

        var plan = await db.NutritionPlans.AsNoTracking().FirstAsync(p => p.Id == assignment.PlanId, cancellationToken);
        var version = await db.NutritionVersions.AsNoTracking().FirstAsync(v => v.Id == assignment.PlanVersionId, cancellationToken);
        var profile = await db.DietaryProfiles.AsNoTracking().Where(p => p.MemberId == query.MemberId).OrderByDescending(p => p.RecordedAt).FirstOrDefaultAsync(cancellationToken);

        if (!scope.Value.IsSelf)
        {
            // Se audita el hecho de la lectura (quién y sobre quién), nunca el contenido.
            await audit.RecordAccessAsync("Wellness.NutritionPlanRead", "Member", query.MemberId.ToString(), cancellationToken);
        }

        var assignmentDto = (await NutritionAssignmentMapper.ToDtosAsync(db, [assignment], today, cancellationToken))[0];
        return new MemberNutritionPlanDto(
            assignmentDto,
            await NutritionContent.ToDetailAsync(db, plan, version, cancellationToken),
            profile is null ? null : NutritionAssignmentMapper.ToDto(profile));
    }
}

internal sealed class ListMemberNutritionAssignmentsHandler(WellnessDbContext db, IAccessContext access, IClock clock, SecurityAudit audit)
    : IQueryHandler<ListMemberNutritionAssignmentsQuery, IReadOnlyList<NutritionAssignmentDto>>
{
    public async Task<Result<IReadOnlyList<NutritionAssignmentDto>>> HandleAsync(ListMemberNutritionAssignmentsQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var scope = await NutritionScope.ResolveAsync(db, actor.Value, query.MemberId, cancellationToken);
        if (scope.IsFailure)
        {
            return scope.Error;
        }

        var today = await LocalDates.TodayAtBranchAsync(db, clock, scope.Value.Member.BranchId, cancellationToken);
        var assignments = await db.NutritionAssignments.AsNoTracking().Where(a => a.MemberId == query.MemberId)
            .OrderByDescending(a => a.StartDate).ThenByDescending(a => a.CreatedAt).ToListAsync(cancellationToken);
        if (!scope.Value.IsSelf)
        {
            await audit.RecordAccessAsync("Wellness.NutritionHistoryRead", "Member", query.MemberId.ToString(), cancellationToken);
        }

        return (await NutritionAssignmentMapper.ToDtosAsync(db, assignments, today, cancellationToken)).ToList();
    }
}

internal sealed class SaveDietaryProfileHandler(WellnessDbContext db, IAccessContext access, IClock clock, SecurityAudit audit)
    : ICommandHandler<SaveDietaryProfileCommand, DietaryProfileDto>
{
    public async Task<Result<DietaryProfileDto>> HandleAsync(SaveDietaryProfileCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var scope = await NutritionScope.ResolveAsync(db, actor.Value, command.MemberId, cancellationToken);
        if (scope.IsFailure || scope.Value.IsSelf)
        {
            return NutritionErrors.MemberNotFound;
        }

        var r = command.Request;
        var profile = DietaryProfile.Create(actor.Value.TenantId, command.MemberId, r.Allergies, r.Intolerances, r.Restrictions, r.Notes, actor.Value.UserId, clock.UtcNow);
        if (profile.Allergies.Length + profile.Intolerances.Length + profile.Restrictions.Length == 0 && profile.Notes is null)
        {
            return NutritionErrors.ProfileEmpty;
        }

        db.DietaryProfiles.Add(profile);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAccessAsync("Wellness.DietaryProfileSaved", "Member", command.MemberId.ToString(), cancellationToken);
        return NutritionAssignmentMapper.ToDto(profile);
    }
}

internal sealed class GetDietaryProfileHandler(WellnessDbContext db, IAccessContext access, SecurityAudit audit)
    : IQueryHandler<GetDietaryProfileQuery, IReadOnlyList<DietaryProfileDto>>
{
    public async Task<Result<IReadOnlyList<DietaryProfileDto>>> HandleAsync(GetDietaryProfileQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var scope = await NutritionScope.ResolveAsync(db, actor.Value, query.MemberId, cancellationToken);
        if (scope.IsFailure)
        {
            return scope.Error;
        }

        var profiles = db.DietaryProfiles.AsNoTracking().Where(p => p.MemberId == query.MemberId).OrderByDescending(p => p.RecordedAt);
        var list = query.History ? await profiles.Take(50).ToListAsync(cancellationToken) : await profiles.Take(1).ToListAsync(cancellationToken);
        if (!scope.Value.IsSelf)
        {
            await audit.RecordAccessAsync("Wellness.DietaryProfileRead", "Member", query.MemberId.ToString(), cancellationToken);
        }

        return list.Select(NutritionAssignmentMapper.ToDto).ToList();
    }
}
