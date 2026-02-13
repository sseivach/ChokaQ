using ChokaQ.Core.Execution;
using FluentAssertions;

namespace ChokaQ.Tests.Unit.Execution;

/// <summary>
/// Unit tests for JobTypeRegistry - bidirectional job type mapping.
/// </summary>
public class JobTypeRegistryTests
{
    [Fact]
    public void Register_ShouldStoreKeyToTypeMapping()
    {
        // Arrange
        var registry = new JobTypeRegistry();

        // Act
        registry.Register("email_v1", typeof(string));

        // Assert
        registry.GetTypeByKey("email_v1").Should().Be(typeof(string));
    }

    [Fact]
    public void Register_ShouldStoreTypeToKeyMapping()
    {
        // Arrange
        var registry = new JobTypeRegistry();

        // Act
        registry.Register("email_v1", typeof(string));

        // Assert
        registry.GetKeyByType(typeof(string)).Should().Be("email_v1");
    }

    [Fact]
    public void Register_DuplicateKey_ShouldThrow()
    {
        // Arrange
        var registry = new JobTypeRegistry();
        registry.Register("email_v1", typeof(string));

        // Act & Assert
        registry.Invoking(r => r.Register("email_v1", typeof(int)))
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*Duplicate Job Key*");
    }

    [Fact]
    public void GetTypeByKey_UnknownKey_ShouldReturnNull()
    {
        // Arrange
        var registry = new JobTypeRegistry();

        // Act
        var result = registry.GetTypeByKey("unknown");

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void GetKeyByType_UnknownType_ShouldReturnNull()
    {
        // Arrange
        var registry = new JobTypeRegistry();

        // Act
        var result = registry.GetKeyByType(typeof(string));

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void Register_CaseInsensitive_ShouldMatch()
    {
        // Arrange
        var registry = new JobTypeRegistry();
        registry.Register("Email_V1", typeof(string));

        // Act
        var result = registry.GetTypeByKey("email_v1");

        // Assert
        result.Should().Be(typeof(string));
    }

    [Fact]
    public void GetAllKeys_ShouldReturnRegisteredKeys()
    {
        // Arrange
        var registry = new JobTypeRegistry();
        registry.Register("email_v1", typeof(string));
        registry.Register("sms_v1", typeof(int));

        // Act
        var keys = registry.GetAllKeys().ToList();

        // Assert
        keys.Should().HaveCount(2);
        keys.Should().Contain("email_v1");
        keys.Should().Contain("sms_v1");
    }
}
