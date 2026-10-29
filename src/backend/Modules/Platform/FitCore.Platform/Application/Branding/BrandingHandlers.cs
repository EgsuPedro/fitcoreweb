using FitCore.Application.Core;
using FitCore.Platform.Domain;
using FitCore.Platform.Persistence;
using FitCore.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace FitCore.Platform.Application.Branding;

public sealed record BrandingDto(string TenantName, bool HasLogo, string? ContentType, int? Width, int? Height, string? Version, DateTimeOffset? UpdatedAt);

public sealed record LogoFile(Stream Content, string ContentType, string Version);

/// <summary>Cambia el logo del propio gimnasio (el del token).</summary>
public sealed record SetOwnLogoCommand(byte[] Content) : ICommand<BrandingDto>;

/// <summary>Cambia el logo de un gimnasio desde la plataforma (alta o soporte).</summary>
public sealed record SetTenantLogoCommand(Guid TenantId, byte[] Content) : ICommand<BrandingDto>;

public sealed record RemoveOwnLogoCommand : ICommand<BrandingDto>;

public sealed record GetBrandingQuery : IQuery<BrandingDto>;

public sealed record GetLogoQuery : IQuery<LogoFile>;

/// <summary>
/// Guarda, reemplaza y quita el logo de un gimnasio. El archivo vive en el almacén privado (clave aleatoria, nunca reutilizada) y la base guarda solo su
/// constancia, aislada por gimnasio (RLS). Cambiarlo guarda primero el archivo nuevo y solo después borra el anterior: un fallo nunca deja al gimnasio sin
/// logo ni un archivo huérfano. Desde la plataforma se escribe en el gimnasio indicado y al terminar se restituye el contexto del solicitante.
/// </summary>
public sealed class TenantBrandingService(PlatformDbContext db, IFileStore store, ITenantContext tenantContext, IClock clock, ICurrentUser currentUser, SecurityAudit audit)
{
    public async Task<Result<BrandingDto>> SetAsync(Guid tenantId, byte[] data, CancellationToken cancellationToken)
    {
        var prepared = LogoImage.Prepare(data);
        if (prepared.IsFailure)
        {
            return prepared.Error;
        }

        if (!await db.Tenants.AnyAsync(t => t.Id == tenantId, cancellationToken))
        {
            return Error.NotFound("tenants.not_found", "El gimnasio no existe.");
        }

        var logo = prepared.Value;
        var key = LogoImage.NewStorageKey(tenantId);
        await using (var content = new MemoryStream(logo.Content))
        {
            await store.SaveAsync(key, content, cancellationToken);
        }

        string? previousKey = null;
        var original = tenantContext.TenantId;
        try
        {
            await db.UseTenantAsync(tenantId, cancellationToken);
            var row = await db.TenantLogos.FirstOrDefaultAsync(cancellationToken);
            previousKey = row?.StorageKey;
            row ??= db.TenantLogos.Add(new TenantLogo { TenantId = tenantId }).Entity;
            row.StorageKey = key;
            row.ContentType = logo.ContentType;
            row.SizeBytes = logo.Content.Length;
            row.Sha256 = logo.Sha256;
            row.Width = logo.Width;
            row.Height = logo.Height;
            row.UpdatedAt = clock.UtcNow;
            row.UpdatedBy = currentUser.UserId;
            audit.Record("Tenant.LogoChanged", nameof(TenantLogo), tenantId.ToString(), details: new { logo.ContentType, Bytes = logo.Content.Length, logo.Width, logo.Height });
            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            await store.DeleteAsync(key, CancellationToken.None);
            throw;
        }
        finally
        {
            await RestoreAsync(original);
        }

        if (previousKey is not null)
        {
            await store.DeleteAsync(previousKey, cancellationToken);
        }

        return await DescribeAsync(tenantId, cancellationToken);
    }

    public async Task<Result<BrandingDto>> RemoveAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        string? previousKey;
        var original = tenantContext.TenantId;
        try
        {
            await db.UseTenantAsync(tenantId, cancellationToken);
            var row = await db.TenantLogos.FirstOrDefaultAsync(cancellationToken);
            if (row is null)
            {
                return LogoImage.NotFound;
            }

            previousKey = row.StorageKey;
            db.TenantLogos.Remove(row);
            audit.Record("Tenant.LogoRemoved", nameof(TenantLogo), tenantId.ToString());
            await db.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            await RestoreAsync(original);
        }

        await store.DeleteAsync(previousKey, cancellationToken);
        return await DescribeAsync(tenantId, cancellationToken);
    }

    /// <summary>Datos de marca de un gimnasio (RLS y el contexto limitan la lectura al gimnasio activo).</summary>
    public async Task<Result<BrandingDto>> DescribeAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var original = tenantContext.TenantId;
        try
        {
            await db.UseTenantAsync(tenantId, cancellationToken);
            var name = await db.Tenants.AsNoTracking().Where(t => t.Id == tenantId).Select(t => t.Name).FirstOrDefaultAsync(cancellationToken);
            if (name is null)
            {
                return Error.NotFound("tenants.not_found", "El gimnasio no existe.");
            }

            var row = await db.TenantLogos.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
            return row is null
                ? new BrandingDto(name, false, null, null, null, null, null)
                : new BrandingDto(name, true, row.ContentType, row.Width, row.Height, row.Sha256[..16], row.UpdatedAt);
        }
        finally
        {
            await RestoreAsync(original);
        }
    }

    private async Task RestoreAsync(Guid? original)
    {
        if (original is { } id)
        {
            await db.UseTenantAsync(id, CancellationToken.None);
        }
        else
        {
            await db.ClearTenantAsync(CancellationToken.None);
        }
    }
}

internal sealed class SetOwnLogoHandler(TenantBrandingService service, ITenantContext tenantContext) : ICommandHandler<SetOwnLogoCommand, BrandingDto>
{
    public Task<Result<BrandingDto>> HandleAsync(SetOwnLogoCommand command, CancellationToken cancellationToken) =>
        tenantContext.TenantId is { } tenantId
            ? service.SetAsync(tenantId, command.Content, cancellationToken)
            : Task.FromResult<Result<BrandingDto>>(PlatformErrors.Forbidden);
}

internal sealed class SetTenantLogoHandler(TenantBrandingService service) : ICommandHandler<SetTenantLogoCommand, BrandingDto>
{
    public Task<Result<BrandingDto>> HandleAsync(SetTenantLogoCommand command, CancellationToken cancellationToken) =>
        service.SetAsync(command.TenantId, command.Content, cancellationToken);
}

internal sealed class RemoveOwnLogoHandler(TenantBrandingService service, ITenantContext tenantContext) : ICommandHandler<RemoveOwnLogoCommand, BrandingDto>
{
    public Task<Result<BrandingDto>> HandleAsync(RemoveOwnLogoCommand command, CancellationToken cancellationToken) =>
        tenantContext.TenantId is { } tenantId
            ? service.RemoveAsync(tenantId, cancellationToken)
            : Task.FromResult<Result<BrandingDto>>(PlatformErrors.Forbidden);
}

internal sealed class GetBrandingHandler(TenantBrandingService service, ITenantContext tenantContext) : IQueryHandler<GetBrandingQuery, BrandingDto>
{
    public Task<Result<BrandingDto>> HandleAsync(GetBrandingQuery query, CancellationToken cancellationToken) =>
        tenantContext.TenantId is { } tenantId
            ? service.DescribeAsync(tenantId, cancellationToken)
            : Task.FromResult<Result<BrandingDto>>(PlatformErrors.Forbidden);
}

internal sealed class GetLogoHandler(PlatformDbContext db, IFileStore store) : IQueryHandler<GetLogoQuery, LogoFile>
{
    public async Task<Result<LogoFile>> HandleAsync(GetLogoQuery query, CancellationToken cancellationToken)
    {
        var row = await db.TenantLogos.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return LogoImage.NotFound;
        }

        var stream = await store.OpenReadAsync(row.StorageKey, cancellationToken);
        return stream is null ? LogoImage.NotFound : new LogoFile(stream, row.ContentType, row.Sha256);
    }
}

/// <summary>Marca del gimnasio de la solicitud en curso para los módulos que generan documentos.</summary>
public sealed class TenantBrandingProvider(PlatformDbContext db, IFileStore store, ITenantContext tenantContext) : ITenantBranding
{
    public async Task<TenantBrand> GetAsync(CancellationToken cancellationToken)
    {
        var tenantId = tenantContext.TenantId ?? Guid.Empty;
        var tenant = await db.Tenants.AsNoTracking().Where(t => t.Id == tenantId).Select(t => new { t.Name, t.TimeZone }).FirstOrDefaultAsync(cancellationToken);
        var name = tenant?.Name ?? string.Empty;
        var zone = tenant?.TimeZone ?? Tenant.DefaultTimeZone;
        var row = await db.TenantLogos.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return new TenantBrand(name, zone, null);
        }

        await using var stream = await store.OpenReadAsync(row.StorageKey, cancellationToken);
        if (stream is null)
        {
            return new TenantBrand(name, zone, null);
        }

        using var memory = new MemoryStream(row.SizeBytes);
        await stream.CopyToAsync(memory, cancellationToken);
        return new TenantBrand(name, zone, new TenantLogoFile(memory.ToArray(), row.ContentType, row.Width, row.Height));
    }
}
