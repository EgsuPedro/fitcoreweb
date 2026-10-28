using FitCore.Application.Core;
using FitCore.Platform.Application;
using FitCore.Platform.Security;
using FitCore.SharedKernel;
using FitCore.Wellness.Application.Training;
using FitCore.Wellness.Domain;
using FitCore.Wellness.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FitCore.Wellness.Application.Assessments;

internal static class AssessmentMapper
{
    /// <summary>
    /// Convierte las evaluaciones de un alumno (todas, para poder arrastrar la estatura) en DTO con lo derivado.
    /// La estatura faltante se toma de la evaluación vigente más reciente anterior que la tenga y se marca como arrastrada.
    /// </summary>
    public static IReadOnlyList<AssessmentDto> ToDtos(IReadOnlyList<BodyAssessment> all, DateOnly? birthDate)
    {
        var corrected = all.Where(a => a.CorrectsId is not null).Select(a => a.CorrectsId!.Value).ToHashSet();
        var ordered = all.OrderBy(a => a.AssessedOn).ThenBy(a => a.CreatedAt).ToList();
        var result = new List<AssessmentDto>();
        decimal? lastHeight = null;
        foreach (var a in ordered)
        {
            var isCorrected = corrected.Contains(a.Id);
            var height = a.HeightCm ?? lastHeight;
            var carried = a.HeightCm is null && height is not null;
            if (!isCorrected && a.HeightCm is not null)
            {
                lastHeight = a.HeightCm;
            }

            var bmi = AssessmentRules.Bmi(a.WeightKg, height);
            result.Add(new AssessmentDto(
                a.Id, a.MemberId, a.AssessedOn, a.WeightKg, a.HeightCm, a.WaistCm, a.HipCm, a.ChestCm, a.ArmCm, a.ThighCm,
                a.BodyFatPercent, a.BodyFatMethod?.ToString(), a.Source.ToString(), a.Notes, a.CorrectsId, isCorrected,
                bmi, carried && bmi is not null, AssessmentRules.BmiReferenceRange(bmi, AssessmentRules.AgeOn(birthDate, a.AssessedOn)),
                AssessmentRules.WaistHipRatio(a.WaistCm, a.HipCm), a.CreatedAt));
        }

        return result;
    }

    public static async Task<Result<MemberAccessScope>> ScopeAsync(WellnessDbContext db, UserAccess actor, Guid memberId, CancellationToken cancellationToken)
    {
        var scope = await MemberScope.ResolveAsync(db, actor, memberId, cancellationToken);
        return scope.IsFailure ? AssessmentErrors.MemberNotFound : scope.Value;
    }

    public static async Task<IReadOnlyList<AssessmentDto>> LoadAsync(WellnessDbContext db, MemberRef member, CancellationToken cancellationToken)
    {
        var all = await db.BodyAssessments.AsNoTracking().Where(a => a.MemberId == member.Id).ToListAsync(cancellationToken);
        return ToDtos(all, member.BirthDate);
    }
}

internal sealed class CreateAssessmentHandler(WellnessDbContext db, IAccessContext access, IClock clock, SecurityAudit audit)
    : ICommandHandler<CreateAssessmentCommand, AssessmentDto>
{
    private const int MaxYearsBack = 10;

    public async Task<Result<AssessmentDto>> HandleAsync(CreateAssessmentCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var scope = await AssessmentMapper.ScopeAsync(db, actor.Value, command.MemberId, cancellationToken);
        if (scope.IsFailure || scope.Value.IsSelf)
        {
            return AssessmentErrors.MemberNotFound;
        }

        var member = scope.Value.Member;
        if (!member.IsActive)
        {
            return AssessmentErrors.MemberInactive;
        }

        var r = command.Request;
        var measurements = new Measurements(r.WeightKg, r.HeightCm, r.WaistCm, r.HipCm, r.ChestCm, r.ArmCm, r.ThighCm, r.BodyFatPercent, r.BodyFatMethod);
        if (AssessmentRules.Validate(measurements) is { } problem)
        {
            return Error.Validation(problem, "Las mediciones no son válidas: revise los rangos (peso 2–500 kg, estatura 50–260 cm, perímetros 5–250 cm, grasa 2–70 % con su método).");
        }

        var today = await LocalDates.TodayAtBranchAsync(db, clock, member.BranchId, cancellationToken);
        var day = r.AssessedOn ?? today;
        if (day > today || day < today.AddYears(-MaxYearsBack))
        {
            return AssessmentErrors.DateInvalid;
        }

        if (r.CorrectsId is { } correctsId)
        {
            var target = await db.BodyAssessments.AsNoTracking().FirstOrDefaultAsync(a => a.Id == correctsId && a.MemberId == member.Id, cancellationToken);
            if (target is null)
            {
                return AssessmentErrors.NotFound;
            }

            if (await db.BodyAssessments.AsNoTracking().AnyAsync(a => a.CorrectsId == correctsId, cancellationToken))
            {
                return AssessmentErrors.AlreadyCorrected;
            }
        }

        var assessment = BodyAssessment.Create(actor.Value.TenantId, member.Id, day, measurements, r.Source, r.Notes, r.CorrectsId, actor.Value.UserId, clock.UtcNow);
        try
        {
            db.BodyAssessments.Add(assessment);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return AssessmentErrors.AlreadyCorrected;
        }

        // Se audita el hecho (quién y sobre quién), nunca las mediciones.
        await audit.RecordAccessAsync("Wellness.AssessmentRecorded", "Member", member.Id.ToString(), cancellationToken);
        return (await AssessmentMapper.LoadAsync(db, member, cancellationToken)).First(a => a.Id == assessment.Id);
    }
}

internal sealed class ListAssessmentsHandler(WellnessDbContext db, IAccessContext access, SecurityAudit audit)
    : IQueryHandler<ListAssessmentsQuery, PagedResult<AssessmentDto>>
{
    public async Task<Result<PagedResult<AssessmentDto>>> HandleAsync(ListAssessmentsQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var scope = await AssessmentMapper.ScopeAsync(db, actor.Value, query.MemberId, cancellationToken);
        if (scope.IsFailure)
        {
            return scope.Error;
        }

        if (query.From is { } from && query.To is { } to && from > to)
        {
            return AssessmentErrors.RangeInvalid;
        }

        var items = (await AssessmentMapper.LoadAsync(db, scope.Value.Member, cancellationToken))
            .Where(a => (query.IncludeCorrected || !a.IsCorrected) && (query.From is null || a.AssessedOn >= query.From) && (query.To is null || a.AssessedOn <= query.To))
            .OrderByDescending(a => a.AssessedOn).ThenByDescending(a => a.CreatedAt)
            .ToList();
        if (!scope.Value.IsSelf)
        {
            await audit.RecordAccessAsync("Wellness.AssessmentsRead", "Member", query.MemberId.ToString(), cancellationToken);
        }

        var page = query.Page;
        return new PagedResult<AssessmentDto>([.. items.Skip((page.Page - 1) * page.PageSize).Take(page.PageSize)], page.Page, page.PageSize, items.Count);
    }
}

internal sealed class CompareAssessmentsHandler(WellnessDbContext db, IAccessContext access, SecurityAudit audit)
    : IQueryHandler<CompareAssessmentsQuery, AssessmentComparisonDto>
{
    public async Task<Result<AssessmentComparisonDto>> HandleAsync(CompareAssessmentsQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var scope = await AssessmentMapper.ScopeAsync(db, actor.Value, query.MemberId, cancellationToken);
        if (scope.IsFailure)
        {
            return scope.Error;
        }

        if (query.A == query.B)
        {
            return AssessmentErrors.CompareInvalid;
        }

        var all = await AssessmentMapper.LoadAsync(db, scope.Value.Member, cancellationToken);
        var a = all.FirstOrDefault(x => x.Id == query.A);
        var b = all.FirstOrDefault(x => x.Id == query.B);
        if (a is null || b is null)
        {
            return AssessmentErrors.NotFound;
        }

        // Siempre de la más antigua a la más reciente, para que el cambio se lea como evolución.
        if (a.AssessedOn > b.AssessedOn || (a.AssessedOn == b.AssessedOn && a.CreatedAt > b.CreatedAt))
        {
            (a, b) = (b, a);
        }

        if (!scope.Value.IsSelf)
        {
            await audit.RecordAccessAsync("Wellness.AssessmentsRead", "Member", query.MemberId.ToString(), cancellationToken);
        }

        static MetricChangeDto Change(string metric, string unit, decimal? from, decimal? to) =>
            new(metric, unit, from, to, from is not null && to is not null ? to - from : null);

        IReadOnlyList<MetricChangeDto> changes =
        [
            Change("WeightKg", "kg", a.WeightKg, b.WeightKg),
            Change("Bmi", "kg/m²", a.Bmi, b.Bmi),
            Change("WaistCm", "cm", a.WaistCm, b.WaistCm),
            Change("HipCm", "cm", a.HipCm, b.HipCm),
            Change("ChestCm", "cm", a.ChestCm, b.ChestCm),
            Change("ArmCm", "cm", a.ArmCm, b.ArmCm),
            Change("ThighCm", "cm", a.ThighCm, b.ThighCm),
            Change("BodyFatPercent", "%", a.BodyFatPercent, b.BodyFatPercent)
        ];
        return new AssessmentComparisonDto(a, b, b.AssessedOn.DayNumber - a.AssessedOn.DayNumber, changes);
    }
}

internal sealed class AssessmentProgressHandler(WellnessDbContext db, IAccessContext access, SecurityAudit audit)
    : IQueryHandler<AssessmentProgressQuery, IReadOnlyList<AssessmentPointDto>>
{
    public async Task<Result<IReadOnlyList<AssessmentPointDto>>> HandleAsync(AssessmentProgressQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var scope = await AssessmentMapper.ScopeAsync(db, actor.Value, query.MemberId, cancellationToken);
        if (scope.IsFailure)
        {
            return scope.Error;
        }

        if (query.From is { } from && query.To is { } to && from > to)
        {
            return AssessmentErrors.RangeInvalid;
        }

        var points = (await AssessmentMapper.LoadAsync(db, scope.Value.Member, cancellationToken))
            .Where(a => !a.IsCorrected && (query.From is null || a.AssessedOn >= query.From) && (query.To is null || a.AssessedOn <= query.To))
            .OrderBy(a => a.AssessedOn).ThenBy(a => a.CreatedAt)
            .Select(a => new AssessmentPointDto(a.Id, a.AssessedOn, a.WeightKg, a.Bmi, a.WaistCm, a.HipCm, a.ChestCm, a.ArmCm, a.ThighCm, a.BodyFatPercent))
            .ToList();
        if (!scope.Value.IsSelf)
        {
            await audit.RecordAccessAsync("Wellness.AssessmentsRead", "Member", query.MemberId.ToString(), cancellationToken);
        }

        return points;
    }
}
