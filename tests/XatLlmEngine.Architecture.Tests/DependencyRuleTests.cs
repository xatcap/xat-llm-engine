using System.Reflection;
using NetArchTest.Rules;
using XatLlmEngine.Application.Abstractions;
using XatLlmEngine.Domain;
using XatLlmEngine.Infrastructure;

namespace XatLlmEngine.Architecture.Tests;

/// <summary>Enforces the Clean Architecture rule that dependencies point inward only.</summary>
public class DependencyRuleTests
{
    /// <summary>The Domain layer's assembly.</summary>
    private static readonly Assembly Domain = typeof(ModelReference).Assembly;

    /// <summary>The Application layer's assembly.</summary>
    private static readonly Assembly ApplicationLayer = typeof(IChatProvider).Assembly;

    /// <summary>The Infrastructure layer's assembly.</summary>
    private static readonly Assembly Infrastructure = typeof(DependencyInjection).Assembly;

    /// <summary>Domain depends on nothing but the base class library.</summary>
    [Fact]
    public void Domain_DependsOnNoOuterLayerOrFramework()
    {
        var result = Types.InAssembly(Domain).ShouldNot().HaveDependencyOnAny(
            "XatLlmEngine.Application", "XatLlmEngine.Infrastructure", "XatLlmEngine.Host", "Microsoft.Extensions", "Microsoft.AspNetCore", "System.Net.Http").GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    /// <summary>Application depends on no outer layer or vendor technology.</summary>
    [Fact]
    public void Application_DependsOnNoOuterLayerOrVendorTechnology()
    {
        var result = Types.InAssembly(ApplicationLayer).ShouldNot().HaveDependencyOnAny(
            "XatLlmEngine.Infrastructure", "XatLlmEngine.Host", "Microsoft.AspNetCore", "System.Net.Http", "System.Diagnostics.Process").GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    /// <summary>Infrastructure depends on neither the host nor the web framework. The adapters need only the Microsoft.Extensions abstractions.</summary>
    [Fact]
    public void Infrastructure_DoesNotDependOnHostOrWebFramework()
    {
        var result = Types.InAssembly(Infrastructure).ShouldNot().HaveDependencyOnAny("XatLlmEngine.Host", "Microsoft.AspNetCore").GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    /// <summary>The Host uses Infrastructure only to register it. The public API, such as the health states, must not depend on adapter types.</summary>
    [Fact]
    public void Host_DoesNotUseAdapterTypes()
    {
        var result = Types.InAssembly(typeof(Program).Assembly).ShouldNot().HaveDependencyOnAny(
            "XatLlmEngine.Infrastructure.LlamaCpp", "XatLlmEngine.Infrastructure.Providers").GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    /// <summary>Adapters are reachable only through the ports they implement.</summary>
    [Fact]
    public void Adapters_AreInternalAndSealed()
    {
        var result = Types.InAssembly(Infrastructure).That().ImplementInterface(typeof(IChatProvider)).Should().NotBePublic().And().BeSealed().GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    /// <summary>Lists the types that broke a rule.</summary>
    /// <param name="result">The rule's result.</param>
    /// <returns>The type names.</returns>
    private static string Describe(NetArchTest.Rules.TestResult result) =>
        string.Join(", ", result.FailingTypeNames ?? []);
}
