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

/// <summary>Despachador mínimo: resuelve el handler por DI, sin reflexión de ensamblados ni librerías externas.</summary>
public interface IDispatcher
{
    Task<Result<TResponse>> SendAsync<TResponse>(ICommand<TResponse> command, CancellationToken cancellationToken = default);

    Task<Result<TResponse>> QueryAsync<TResponse>(IQuery<TResponse> query, CancellationToken cancellationToken = default);
}

/// <summary>Contexto del usuario autenticado. El tenant sale SIEMPRE del token, nunca del request.</summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }
    Guid? UserId { get; }
    Guid? TenantId { get; }
    IReadOnlyCollection<Guid> BranchIds { get; }
}
