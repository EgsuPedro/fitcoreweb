using FitCore.SharedKernel;

namespace FitCore.Application.Core;

public interface ICommand<TResponse>;

public interface IQuery<TResponse>;

public interface ICommandHandler<in TCommand, TResponse>
    where TCommand : ICommand<TResponse>
{
    Task<Result<TResponse>> HandleAsync(TCommand command, CancellationToken cancellationToken);
}

public interface IQueryHandler<in TQuery, TResponse>
    where TQuery : IQuery<TResponse>
{
    Task<Result<TResponse>> HandleAsync(TQuery query, CancellationToken cancellationToken);
}

/// <summary>Despachador mínimo: resuelve el handler por DI, sin librerías externas.</summary>
public interface IDispatcher
{
    Task<Result<TResponse>> SendAsync<TResponse>(ICommand<TResponse> command, CancellationToken cancellationToken = default);

    Task<Result<TResponse>> QueryAsync<TResponse>(IQuery<TResponse> query, CancellationToken cancellationToken = default);
}

/// <summary>Usuario autenticado de la solicitud actual. El tenant sale SIEMPRE del token, nunca del request.</summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }
    Guid? UserId { get; }
    Guid? TenantId { get; }
}

/// <summary>Tenant activo para el acceso a datos (alimenta filtros de EF y RLS de PostgreSQL).</summary>
public interface ITenantContext
{
    Guid? TenantId { get; }

    void Set(Guid tenantId);

    void Clear();
}

/// <summary>Datos de la solicitud HTTP para auditoría y trazabilidad.</summary>
public interface IRequestInfo
{
    string? IpAddress { get; }
    string? UserAgent { get; }
    string? CorrelationId { get; }
}

/// <summary>Reacciona a un evento de dominio publicado por la outbox. Debe ser idempotente (puede reintentarse).</summary>
public interface IOutboxEventHandler
{
    /// <summary>Nombre completo del tipo de evento que maneja.</summary>
    string EventType { get; }

    Task HandleAsync(Guid? tenantId, string payload, CancellationToken cancellationToken);
}

/// <summary>Respuesta vacía de un comando que no devuelve datos.</summary>
public readonly record struct Unit;

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

public sealed record PageRequest(int Page = 1, int PageSize = 25)
{
    public const int MaxPageSize = 100;

    public int SafePage => Math.Max(1, Page);

    public int SafePageSize => Math.Clamp(PageSize, 1, MaxPageSize);

    public int Skip => (SafePage - 1) * SafePageSize;
}
