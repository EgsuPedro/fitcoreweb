using System.Reflection;
using FitCore.Analytics;
using FitCore.Application.Core;
using FitCore.Classes;
using FitCore.Commercial;
using FitCore.Infrastructure.Core;
using FitCore.Platform;
using FitCore.Wellness;
using FitCore.SharedKernel;
using NetArchTest.Rules;

namespace FitCore.Architecture.Tests;

/// <summary>Protege los límites entre capas: si alguien introduce una dependencia prohibida, el build falla.</summary>
public class LayerDependencyTests
{
    private static readonly Assembly SharedKernel = typeof(Money).Assembly;
    private static readonly Assembly ApplicationCore = typeof(IDispatcher).Assembly;
    private static readonly Assembly InfrastructureCore = typeof(SystemClock).Assembly;
    private static readonly Assembly Platform = typeof(PlatformModule).Assembly;
    private static readonly Assembly Commercial = typeof(CommercialModule).Assembly;
    private static readonly Assembly Wellness = typeof(WellnessModule).Assembly;
    private static readonly Assembly Classes = typeof(ClassesModule).Assembly;
    private static readonly Assembly Analytics = typeof(AnalyticsModule).Assembly;
    private static readonly Assembly Api = typeof(Program).Assembly;

    [Fact]
    public void SharedKernel_no_depende_de_otras_capas()
    {
        AssertNoDependency(
            AllTypes(SharedKernel),
            "FitCore.Application", "FitCore.Infrastructure", "FitCore.Platform", "FitCore.Commercial", "FitCore.Wellness", "FitCore.Classes", "FitCore.Analytics", "FitCore.Api",
            "Microsoft.EntityFrameworkCore", "Npgsql", "Microsoft.AspNetCore");
    }

    [Fact]
    public void Application_no_depende_de_infraestructura_ni_api()
    {
        AssertNoDependency(
            AllTypes(ApplicationCore),
            "FitCore.Infrastructure", "FitCore.Platform", "FitCore.Api",
            "Microsoft.EntityFrameworkCore", "Npgsql", "Microsoft.AspNetCore");
    }

    [Fact]
    public void Infrastructure_core_no_depende_de_la_api_ni_de_los_modulos()
    {
        AssertNoDependency(AllTypes(InfrastructureCore), "FitCore.Api", "FitCore.Platform", "FitCore.Commercial");
    }

    [Fact]
    public void Platform_no_depende_de_la_api_ni_de_ASP_NET_Core_web()
    {
        // Los módulos son lógica de negocio y acceso a datos: HTTP vive solo en la API.
        AssertNoDependency(
            AllTypes(Platform),
            "FitCore.Api", "Microsoft.AspNetCore.Http", "Microsoft.AspNetCore.Mvc", "Microsoft.AspNetCore.Routing");
    }

    [Fact]
    public void Dominio_de_Platform_es_puro()
    {
        AssertNoDependency(
            Types.InAssembly(Platform).That().ResideInNamespace("FitCore.Platform.Domain"),
            "Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore", "FitCore.Platform.Persistence",
            "FitCore.Platform.Application", "FitCore.Platform.Identity");
    }

    [Fact]
    public void Casos_de_uso_no_dependen_de_HTTP()
    {
        AssertNoDependency(
            Types.InAssembly(Platform).That().ResideInNamespaceStartingWith("FitCore.Platform.Application"),
            "Microsoft.AspNetCore.Http", "Microsoft.AspNetCore.Mvc", "FitCore.Api");
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

    [Fact]
    public void Los_handlers_son_internos_y_la_api_solo_usa_el_despachador()
    {
        var handlers = Types.InAssembly(Platform)
            .That().HaveNameEndingWith("Handler").And().AreClasses().And().ResideInNamespaceStartingWith("FitCore.Platform.Application")
            .GetTypes()
            .ToList();

        Assert.NotEmpty(handlers);
        Assert.All(handlers, t => Assert.False(t.IsPublic, $"{t.Name} debe ser internal: la API solo usa el despachador."));
    }

    [Fact]
    public void Platform_no_depende_de_los_modulos_de_negocio()
    {
        // La dependencia es de un solo sentido: Commercial usa Platform (acceso, auditoría), nunca al revés.
        AssertNoDependency(AllTypes(Platform), "FitCore.Commercial", "FitCore.Wellness", "FitCore.Classes", "FitCore.Analytics");
    }

    [Fact]
    public void Commercial_no_depende_de_la_api_ni_de_ASP_NET_Core_web()
    {
        AssertNoDependency(
            AllTypes(Commercial),
            "FitCore.Api", "Microsoft.AspNetCore.Http", "Microsoft.AspNetCore.Mvc", "Microsoft.AspNetCore.Routing");
    }

    [Fact]
    public void Dominio_de_Commercial_es_puro()
    {
        AssertNoDependency(
            Types.InAssembly(Commercial).That().ResideInNamespace("FitCore.Commercial.Domain"),
            "Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore", "FitCore.Commercial.Persistence",
            "FitCore.Commercial.Application", "FitCore.Platform");
    }

    [Fact]
    public void Los_handlers_de_Commercial_son_internos()
    {
        var handlers = Types.InAssembly(Commercial)
            .That().HaveNameEndingWith("Handler").And().AreClasses().And().ResideInNamespaceStartingWith("FitCore.Commercial.Application")
            .GetTypes()
            .ToList();

        Assert.NotEmpty(handlers);
        Assert.All(handlers, t => Assert.False(t.IsPublic, $"{t.Name} debe ser internal: la API solo usa el despachador."));
    }

    [Fact]
    public void Wellness_no_depende_de_la_api_ni_de_ASP_NET_Core_web()
    {
        AssertNoDependency(
            AllTypes(Wellness),
            "FitCore.Api", "Microsoft.AspNetCore.Http", "Microsoft.AspNetCore.Mvc", "Microsoft.AspNetCore.Routing");
    }

    [Fact]
    public void Los_modulos_de_negocio_no_dependen_entre_si()
    {
        // Los datos de otro módulo se leen con vistas de solo lectura propias, nunca referenciando sus tipos.
        AssertNoDependency(AllTypes(Wellness), "FitCore.Commercial", "FitCore.Classes", "FitCore.Analytics");
        AssertNoDependency(AllTypes(Commercial), "FitCore.Wellness", "FitCore.Classes", "FitCore.Analytics");
        AssertNoDependency(AllTypes(Classes), "FitCore.Commercial", "FitCore.Wellness", "FitCore.Analytics");
        // Analytics lee los datos de los demás con vistas propias de solo lectura: no referencia sus tipos.
        AssertNoDependency(AllTypes(Analytics), "FitCore.Commercial", "FitCore.Wellness", "FitCore.Classes");
    }

    [Fact]
    public void Dominio_de_Wellness_es_puro()
    {
        AssertNoDependency(
            Types.InAssembly(Wellness).That().ResideInNamespace("FitCore.Wellness.Domain"),
            "Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore", "FitCore.Wellness.Persistence",
            "FitCore.Wellness.Application", "FitCore.Platform");
    }

    [Fact]
    public void Los_handlers_de_Wellness_son_internos()
    {
        var handlers = Types.InAssembly(Wellness)
            .That().HaveNameEndingWith("Handler").And().AreClasses().And().ResideInNamespaceStartingWith("FitCore.Wellness.Application")
            .GetTypes()
            .ToList();

        Assert.NotEmpty(handlers);
        Assert.All(handlers, t => Assert.False(t.IsPublic, $"{t.Name} debe ser internal: la API solo usa el despachador."));
    }

    [Fact]
    public void Classes_no_depende_de_la_api_ni_de_ASP_NET_Core_web()
    {
        AssertNoDependency(
            AllTypes(Classes),
            "FitCore.Api", "Microsoft.AspNetCore.Http", "Microsoft.AspNetCore.Mvc", "Microsoft.AspNetCore.Routing");
    }

    [Fact]
    public void Dominio_de_Classes_es_puro()
    {
        AssertNoDependency(
            Types.InAssembly(Classes).That().ResideInNamespace("FitCore.Classes.Domain"),
            "Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore", "FitCore.Classes.Persistence",
            "FitCore.Classes.Application", "FitCore.Platform");
    }

    [Fact]
    public void Los_handlers_de_Classes_son_internos()
    {
        var handlers = Types.InAssembly(Classes)
            .That().HaveNameEndingWith("Handler").And().AreClasses().And().ResideInNamespaceStartingWith("FitCore.Classes.Application")
            .GetTypes()
            .ToList();

        Assert.NotEmpty(handlers);
        Assert.All(handlers, t => Assert.False(t.IsPublic, $"{t.Name} debe ser internal: la API solo usa el despachador."));
    }

    [Fact]
    public void Analytics_no_depende_de_la_api_ni_de_ASP_NET_Core_web()
    {
        AssertNoDependency(
            AllTypes(Analytics),
            "FitCore.Api", "Microsoft.AspNetCore.Http", "Microsoft.AspNetCore.Mvc", "Microsoft.AspNetCore.Routing");
    }

    [Fact]
    public void Dominio_de_Analytics_es_puro()
    {
        AssertNoDependency(
            Types.InAssembly(Analytics).That().ResideInNamespace("FitCore.Analytics.Domain"),
            "Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore", "FitCore.Analytics.Persistence",
            "FitCore.Analytics.Application", "FitCore.Platform");
    }

    [Fact]
    public void Los_handlers_de_Analytics_son_internos()
    {
        var handlers = Types.InAssembly(Analytics)
            .That().HaveNameEndingWith("Handler").And().AreClasses().And().ResideInNamespaceStartingWith("FitCore.Analytics.Application")
            .GetTypes()
            .ToList();

        Assert.NotEmpty(handlers);
        Assert.All(handlers, t => Assert.False(t.IsPublic, $"{t.Name} debe ser internal: la API solo usa el despachador."));
    }

    private static PredicateList AllTypes(Assembly assembly) =>
        Types.InAssembly(assembly).That().ResideInNamespaceStartingWith("FitCore");

    private static void AssertNoDependency(PredicateList types, params string[] forbidden)
    {
        var result = types.ShouldNot().HaveDependencyOnAny(forbidden).GetResult();

        Assert.True(result.IsSuccessful, $"Dependencias prohibidas en: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }
}
