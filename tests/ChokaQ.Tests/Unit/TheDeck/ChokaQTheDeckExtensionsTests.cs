using ChokaQ.TheDeck;
using ChokaQ.TheDeck.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace ChokaQ.Tests.Unit.TheDeck;

[Trait(TestCategories.Category, TestCategories.Unit)]
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
        options.DestructiveAuthorizationPolicy.Should().BeNull();
        options.AllowAnonymousDeck.Should().BeFalse();
        options.QueueLagWarningThresholdSeconds.Should().Be(5);
        options.QueueLagCriticalThresholdSeconds.Should().Be(10);
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
    public void AddChokaQTheDeck_WithDestructiveAuthorizationPolicy_ShouldRegisterCorrectly()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddChokaQTheDeck(o => o.DestructiveAuthorizationPolicy = "ChokaQWrite");

        var sp = services.BuildServiceProvider();
        var options = sp.GetRequiredService<ChokaQTheDeckOptions>();

        options.DestructiveAuthorizationPolicy.Should().Be("ChokaQWrite");
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
        options.AllowAnonymousDeck.Should().BeFalse();
    }

    [Fact]
    public void AddChokaQTheDeck_WithAllowAnonymous_ShouldRegisterExplicitAnonymousOptIn()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddChokaQTheDeck(o => o.AllowAnonymousDeck = true);

        var sp = services.BuildServiceProvider();
        var options = sp.GetRequiredService<ChokaQTheDeckOptions>();

        options.AllowAnonymousDeck.Should().BeTrue();
    }

    [Fact]
    public void AddChokaQTheDeck_WithPolicyAndAllowAnonymous_ShouldThrow()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var act = () => services.AddChokaQTheDeck(o =>
        {
            o.AuthorizationPolicy = "ChokaQAdmin";
            o.AllowAnonymousDeck = true;
        });

        // Anonymous access and policy protection communicate opposite security intent.
        // Failing during startup prevents a production app from quietly exposing The Deck.
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*cannot be both anonymous and policy-protected*");
    }

    [Fact]
    public void AddChokaQTheDeck_WithInvalidLagThresholds_ShouldThrow()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var act = () => services.AddChokaQTheDeck(o =>
        {
            o.QueueLagWarningThresholdSeconds = 10;
            o.QueueLagCriticalThresholdSeconds = 5;
        });

        // The dashboard color bands are operational policy. Invalid bands would make queue
        // health unreadable for operators, so configuration should fail during startup.
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*queue lag thresholds*");
    }
}
