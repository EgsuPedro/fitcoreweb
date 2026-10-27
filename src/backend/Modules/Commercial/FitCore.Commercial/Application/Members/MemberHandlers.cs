using System.Text.RegularExpressions;
using FitCore.Application.Core;
using FitCore.Commercial.Domain;
using FitCore.Commercial.Persistence;
using FitCore.Platform.Application;
using FitCore.Platform.Security;
using FitCore.SharedKernel;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace FitCore.Commercial.Application.Members;

public sealed record MemberDto(
    Guid Id,
    string Code,
    Guid BranchId,
    string FirstName,
    string LastName,
    string IdType,
    string IdNumber,
    DateOnly? BirthDate,
    string? Email,
    string? Phone,
    string? Address,
    string? PhotoUrl,
    string? EmergencyContactName,
    string? EmergencyContactPhone,
    string? EmergencyContactRelationship,
    DateOnly EnrolledOn,
    string Status,
    string? Notes,
    bool HasQrToken,
    DateTimeOffset? QrTokenIssuedAt,
    DateTimeOffset CreatedAt,
    Guid? CreatedBy,
    DateTimeOffset? UpdatedAt,
    Guid? UpdatedBy,
    bool WhatsAppConsent,
    DateTimeOffset? WhatsAppConsentAt)
{
    public static MemberDto From(Member m) =>
        new(
            m.Id,
            m.Code,
            m.BranchId,
            m.FirstName,
            m.LastName,
            m.IdType.ToString(),
            m.IdNumber,
            m.BirthDate,
            m.Email,
            m.Phone,
            m.Address,
            m.PhotoUrl,
            m.EmergencyContactName,
            m.EmergencyContactPhone,
            m.EmergencyContactRelationship,
            m.EnrolledOn,
            m.Status.ToString(),
            m.Notes,
            m.QrTokenHash != null,
            m.QrTokenIssuedAt,
            m.CreatedAt,
            m.CreatedBy,
            m.UpdatedAt,
            m.UpdatedBy,
            m.WhatsAppConsent,
            m.WhatsAppConsentAt);
}

/// <summary>Campos de perfil comunes a crear y editar un alumno.</summary>
public interface IMemberProfileRequest
{
    string FirstName { get; }
    string LastName { get; }
    IdentificationType IdType { get; }
    string IdNumber { get; }
    DateOnly? BirthDate { get; }
    string? Email { get; }
    string? Phone { get; }
    string? Address { get; }
    string? PhotoUrl { get; }
    string? EmergencyContactName { get; }
    string? EmergencyContactPhone { get; }
    string? EmergencyContactRelationship { get; }
    string? Notes { get; }
}

/// <summary>Datos que envía el cliente para crear un alumno. El tenant sale siempre del token.</summary>
public sealed record CreateMemberRequest(
    Guid BranchId,
    string FirstName,
    string LastName,
    IdentificationType IdType,
    string IdNumber,
    DateOnly? BirthDate,
    string? Email,
    string? Phone,
    string? Address,
    string? PhotoUrl,
    string? EmergencyContactName,
    string? EmergencyContactPhone,
    string? EmergencyContactRelationship,
    string? Notes) : IMemberProfileRequest;

public sealed record UpdateMemberRequest(
    string FirstName,
    string LastName,
    IdentificationType IdType,
    string IdNumber,
    DateOnly? BirthDate,
    string? Email,
    string? Phone,
    string? Address,
    string? PhotoUrl,
    string? EmergencyContactName,
    string? EmergencyContactPhone,
    string? EmergencyContactRelationship,
    string? Notes) : IMemberProfileRequest;

public sealed record SetMemberStatusRequest(bool IsActive);

public sealed record ListMembersQuery(PageRequest Page, string? Search, MemberStatus? Status, Guid? BranchId) : IQuery<PagedResult<MemberDto>>;

public sealed record GetMemberQuery(Guid Id) : IQuery<MemberDto>;

public sealed record CreateMemberCommand(CreateMemberRequest Request) : ICommand<MemberDto>;

public sealed record UpdateMemberCommand(Guid Id, UpdateMemberRequest Request) : ICommand<MemberDto>;

public sealed record SetMemberStatusCommand(Guid Id, SetMemberStatusRequest Request) : ICommand<MemberDto>;

public static partial class MemberValidation
{
    [GeneratedRegex(@"^[+0-9][0-9 ()\-]{6,29}$")]
    private static partial Regex PhonePattern();

    public static void AddProfileRules<T>(this AbstractValidator<T> validator)
        where T : IMemberProfileRequest
    {
        validator.RuleFor(x => x.FirstName).NotEmpty().MaximumLength(100);
        validator.RuleFor(x => x.LastName).NotEmpty().MaximumLength(100);
        validator.RuleFor(x => x.IdType).IsInEnum();
        validator.RuleFor(x => x.IdNumber).NotEmpty().MaximumLength(30);
        validator.RuleFor(x => x.IdNumber)
            .Must((request, number) => IdentificationValidator.IsValid(request.IdType, number))
            .When(x => !string.IsNullOrWhiteSpace(x.IdNumber) && Enum.IsDefined(x.IdType))
            .WithMessage("El número de identificación no es válido para el tipo de documento.");
        validator.RuleFor(x => x.BirthDate)
            .Must(d => d <= DateOnly.FromDateTime(DateTime.UtcNow) && d!.Value.Year >= 1900)
            .When(x => x.BirthDate is not null)
            .WithMessage("La fecha de nacimiento no es válida.");
        validator.RuleFor(x => x.Email).EmailAddress().MaximumLength(256).When(x => !string.IsNullOrWhiteSpace(x.Email));
        validator.RuleFor(x => x.Phone).Must(p => PhonePattern().IsMatch(p!)).When(x => !string.IsNullOrWhiteSpace(x.Phone))
            .WithMessage("El teléfono no es válido.");
        validator.RuleFor(x => x.EmergencyContactPhone).Must(p => PhonePattern().IsMatch(p!)).When(x => !string.IsNullOrWhiteSpace(x.EmergencyContactPhone))
            .WithMessage("El teléfono de emergencia no es válido.");
        validator.RuleFor(x => x.PhotoUrl)
            .Must(u => Uri.TryCreate(u, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            .MaximumLength(500)
            .When(x => !string.IsNullOrWhiteSpace(x.PhotoUrl))
            .WithMessage("La fotografía debe ser una dirección http o https.");
        validator.RuleFor(x => x.Address).MaximumLength(300);
        validator.RuleFor(x => x.EmergencyContactName).MaximumLength(150);
        validator.RuleFor(x => x.EmergencyContactRelationship).MaximumLength(50);
        validator.RuleFor(x => x.Notes).MaximumLength(1000);
    }
}

public sealed class CreateMemberRequestValidator : AbstractValidator<CreateMemberRequest>
{
    public CreateMemberRequestValidator()
    {
        RuleFor(x => x.BranchId).NotEmpty();
        this.AddProfileRules();
    }
}

public sealed class UpdateMemberRequestValidator : AbstractValidator<UpdateMemberRequest>
{
    public UpdateMemberRequestValidator() => this.AddProfileRules();
}
internal static class MemberErrors
{
    public static readonly Error NotFound = Error.NotFound("members.not_found", "Alumno no encontrado.");

    public static readonly Error DuplicateIdentification =
        Error.Conflict("members.duplicate_identification", "Ya existe un alumno con ese documento de identidad.");
}

internal static class MemberQueries
{
    /// <summary>Alumnos visibles para el usuario: los de sus sucursales (o todos si accede a todas).</summary>
    public static IQueryable<Member> VisibleTo(this IQueryable<Member> members, UserAccess actor) => members.InScope(actor, m => m.BranchId);
}

internal sealed class ListMembersHandler(CommercialDbContext db, IAccessContext access)
    : IQueryHandler<ListMembersQuery, PagedResult<MemberDto>>
{
    public async Task<Result<PagedResult<MemberDto>>> HandleAsync(ListMembersQuery query, CancellationToken cancellationToken)
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

        var members = db.Members.AsNoTracking().VisibleTo(actor.Value);
        if (query.Status is { } status)
        {
            members = members.Where(m => m.Status == status);
        }

        if (query.BranchId is { } branchId)
        {
            members = members.Where(m => m.BranchId == branchId);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var pattern = LikePattern.Contains(query.Search);
            members = members.Where(m =>
                EF.Functions.ILike(m.FirstName + " " + m.LastName, pattern)
                || EF.Functions.ILike(m.IdNumber, pattern)
                || EF.Functions.ILike(m.Code, pattern)
                || (m.Phone != null && EF.Functions.ILike(m.Phone, pattern)));
        }

        var paged = await members.OrderBy(m => m.LastName).ThenBy(m => m.FirstName).ThenBy(m => m.Code).ToPagedAsync(query.Page, cancellationToken);
        return new PagedResult<MemberDto>([.. paged.Items.Select(MemberDto.From)], paged.Page, paged.PageSize, paged.TotalCount);
    }
}

internal sealed class GetMemberHandler(CommercialDbContext db, IAccessContext access) : IQueryHandler<GetMemberQuery, MemberDto>
{
    public async Task<Result<MemberDto>> HandleAsync(GetMemberQuery query, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        // Un alumno ajeno (otro gimnasio o fuera de las sucursales del usuario) responde 404.
        var member = await db.Members.AsNoTracking().VisibleTo(actor.Value).FirstOrDefaultAsync(m => m.Id == query.Id, cancellationToken);
        return member is null ? MemberErrors.NotFound : MemberDto.From(member);
    }
}

internal sealed class CreateMemberHandler(CommercialDbContext db, IAccessContext access, IClock clock, ICurrentUser currentUser)
    : ICommandHandler<CreateMemberCommand, MemberDto>
{
    public async Task<Result<MemberDto>> HandleAsync(CreateMemberCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var request = command.Request;
        var branch = await db.ResolveBranchAsync(actor.Value, request.BranchId, cancellationToken);
        if (branch.IsFailure)
        {
            return branch.Error;
        }

        var idNumber = IdentificationValidator.Normalize(request.IdNumber);
        if (await db.Members.AnyAsync(m => m.IdType == request.IdType && m.IdNumber == idNumber, cancellationToken))
        {
            return MemberErrors.DuplicateIdentification;
        }

        // El código sale de un contador: si el alta falla, la transacción revierte el número y no quedan huecos.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var number = await db.NextCounterAsync(actor.Value.TenantId, "member-code", cancellationToken);
        var now = clock.UtcNow;
        var member = Member.Create(
            actor.Value.TenantId,
            branch.Value.Id,
            $"A-{number:D6}",
            new MemberProfile(
                request.FirstName,
                request.LastName,
                request.IdType,
                idNumber,
                request.BirthDate,
                request.Email,
                request.Phone,
                request.Address,
                request.PhotoUrl,
                request.EmergencyContactName,
                request.EmergencyContactPhone,
                request.EmergencyContactRelationship,
                request.Notes),
            DateOnly.FromDateTime(now.UtcDateTime),
            currentUser.UserId,
            now);

        db.Members.Add(member);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return MemberDto.From(member);
    }
}

internal sealed class UpdateMemberHandler(CommercialDbContext db, IAccessContext access, IClock clock, ICurrentUser currentUser)
    : ICommandHandler<UpdateMemberCommand, MemberDto>
{
    public async Task<Result<MemberDto>> HandleAsync(UpdateMemberCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var member = await db.Members.VisibleTo(actor.Value).FirstOrDefaultAsync(m => m.Id == command.Id, cancellationToken);
        if (member is null)
        {
            return MemberErrors.NotFound;
        }

        var request = command.Request;
        var idNumber = IdentificationValidator.Normalize(request.IdNumber);
        if (await db.Members.AnyAsync(m => m.Id != member.Id && m.IdType == request.IdType && m.IdNumber == idNumber, cancellationToken))
        {
            return MemberErrors.DuplicateIdentification;
        }

        // El tenant, el código y la sucursal de inscripción no forman parte de lo editable.
        member.Update(
            new MemberProfile(
                request.FirstName,
                request.LastName,
                request.IdType,
                idNumber,
                request.BirthDate,
                request.Email,
                request.Phone,
                request.Address,
                request.PhotoUrl,
                request.EmergencyContactName,
                request.EmergencyContactPhone,
                request.EmergencyContactRelationship,
                request.Notes),
            currentUser.UserId,
            clock.UtcNow);

        await db.SaveChangesAsync(cancellationToken);
        return MemberDto.From(member);
    }
}

internal sealed class SetMemberStatusHandler(CommercialDbContext db, IAccessContext access, IClock clock, ICurrentUser currentUser)
    : ICommandHandler<SetMemberStatusCommand, MemberDto>
{
    public async Task<Result<MemberDto>> HandleAsync(SetMemberStatusCommand command, CancellationToken cancellationToken)
    {
        var actor = await access.RequireAccessAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var member = await db.Members.VisibleTo(actor.Value).FirstOrDefaultAsync(m => m.Id == command.Id, cancellationToken);
        if (member is null)
        {
            return MemberErrors.NotFound;
        }

        member.SetStatus(command.Request.IsActive ? MemberStatus.Active : MemberStatus.Inactive, currentUser.UserId, clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        return MemberDto.From(member);
    }
}
