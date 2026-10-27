using System.Reflection;
using FitCore.Application.Core;
using FitCore.Infrastructure.Core;
using FitCore.SharedKernel;
using NetArchTest.Rules;

namespace FitCore.Architecture.Tests;

/// <summary>Protege los límites entre capas: si alguien introduce una dependencia prohibida, el build falla.</summary>
public class LayerDependencyTests
{
    private static readonly Assembly SharedKernel = typeof(Money).Assembly;
    private static readonly Assembly ApplicationCore = typeof(IDispatcher).Assembly;
    private static readonly Assembly InfrastructureCore = typeof(SystemClock).Assembly;
    private static readonly Assembly Api = typeof(Program).Assembly;

    [Fact]
    public void SharedKernel_no_depende_de_otras_capas()
    {
        AssertNoDependency(SharedKernel,
            "FitCore.Application", "FitCore.Infrastructure", "FitCore.Api",
            "Microsoft.EntityFrameworkCore", "Npgsql", "Microsoft.AspNetCore");
    }

    [Fact]
    public void Application_no_depende_de_infraestructura_ni_api()
    {
        AssertNoDependency(ApplicationCore,
            "FitCore.Infrastructure", "FitCore.Api",
            "Microsoft.EntityFrameworkCore", "Npgsql", "Microsoft.AspNetCore");
    }

    [Fact]
    public void Infrastructure_no_depende_de_la_api()
    {
        AssertNoDependency(InfrastructureCore, "FitCore.Api");
    }

    [Fact]
    public void Api_no_expone_entidades_de_dominio_como_dto()
    {
        var result = Types.InAssembly(Api)
            .That().ResideInNamespaceEndingWith("Endpoints")
            .ShouldNot().Inherit(typeof(Entity<>))
            .GetResult();

        Assert.True(result.IsSuccessful);
    }

    private static void AssertNoDependency(Assembly assembly, params string[] forbidden)
    {
        var result = Types.InAssembly(assembly).ShouldNot().HaveDependencyOnAny(forbidden).GetResult();

        Assert.True(
            result.IsSuccessful,
            $"Dependencias prohibidas en {assembly.GetName().Name}: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }
}
