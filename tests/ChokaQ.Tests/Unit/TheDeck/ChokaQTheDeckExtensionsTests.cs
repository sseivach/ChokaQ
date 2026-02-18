using ChokaQ.TheDeck;
using ChokaQ.TheDeck.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace ChokaQ.Tests.Unit.TheDeck;

public class ChokaQTheDeckExtensionsTests
{
    [Fact]
    public void AddChokaQTheDeck_Defaults_ShouldRegisterOptionsWithDefaultValues()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddChokaQTheDeck();

        var sp = services.BuildServiceProvider();
        var options = sp.GetRequiredService<ChokaQTheDeckOptions>();

        options.RoutePrefix.Should().Be("/chokaq");
        options.AuthorizationPolicy.Should().BeNull();
    }

    [Fact]
    public void AddChokaQTheDeck_WithCustomRoutePrefix_ShouldRegisterCorrectly()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddChokaQTheDeck(o => o.RoutePrefix = "/admin/deck");

        var sp = services.BuildServiceProvider();
        var options = sp.GetRequiredService<ChokaQTheDeckOptions>();

        options.RoutePrefix.Should().Be("/admin/deck");
    }

    [Fact]
    public void AddChokaQTheDeck_WithAuthorizationPolicy_ShouldRegisterCorrectly()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddChokaQTheDeck(o => o.AuthorizationPolicy = "ChokaQAdmin");

        var sp = services.BuildServiceProvider();
        var options = sp.GetRequiredService<ChokaQTheDeckOptions>();

        options.AuthorizationPolicy.Should().Be("ChokaQAdmin");
    }

    [Fact]
    public void AddChokaQTheDeck_WithNullPolicy_ShouldLeaveAuthorizationPolicyNull()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddChokaQTheDeck(o => o.AuthorizationPolicy = null);

        var sp = services.BuildServiceProvider();
        var options = sp.GetRequiredService<ChokaQTheDeckOptions>();

        options.AuthorizationPolicy.Should().BeNull();
    }
}
