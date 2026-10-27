using System.Reflection;
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

    /// <summary>Registra todos los handlers de un ensamblado (una sola vez, al arrancar).</summary>
    public static IServiceCollection AddHandlersFromAssembly(this IServiceCollection services, Assembly assembly)
    {
        foreach (var type in assembly.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false }))
        {
            foreach (var contract in type.GetInterfaces().Where(IsHandlerContract))
            {
                services.AddScoped(contract, type);
            }
        }

        return services;
    }

    private static bool IsHandlerContract(Type type) =>
        type.IsGenericType &&
        (type.GetGenericTypeDefinition() == typeof(ICommandHandler<,>) ||
         type.GetGenericTypeDefinition() == typeof(IQueryHandler<,>));
}
