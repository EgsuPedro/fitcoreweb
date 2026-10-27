using FitCore.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace FitCore.Application.Core;

internal sealed class Dispatcher(IServiceProvider services) : IDispatcher
{
    public Task<Result<TResponse>> SendAsync<TResponse>(ICommand<TResponse> command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return Invoke<TResponse>(typeof(ICommandHandler<,>), command, cancellationToken);
    }

    public Task<Result<TResponse>> QueryAsync<TResponse>(IQuery<TResponse> query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Invoke<TResponse>(typeof(IQueryHandler<,>), query, cancellationToken);
    }

    private Task<Result<TResponse>> Invoke<TResponse>(Type handlerDefinition, object message, CancellationToken cancellationToken)
    {
        var handlerType = handlerDefinition.MakeGenericType(message.GetType(), typeof(TResponse));
        var handler = services.GetRequiredService(handlerType);

        // Un solo punto de reflexión; los handlers se registran explícitamente en cada módulo.
        var method = handlerType.GetMethod("HandleAsync")!;
        return (Task<Result<TResponse>>)method.Invoke(handler, [message, cancellationToken])!;
    }
}

public static class ApplicationCoreExtensions
{
    public static IServiceCollection AddDispatcher(this IServiceCollection services)
    {
        services.AddScoped<IDispatcher, Dispatcher>();
        return services;
    }
}
