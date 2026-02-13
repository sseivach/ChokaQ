using ChokaQ.Core.Defaults;

namespace ChokaQ.Tests.Unit.Resilience;

public class InMemoryDeduplicatorTests
{
    [Fact]
    public async Task TryAcquireAsync_ShouldReturnTrue_FirstTime()
    {
        // Arrange
        var deduplicator = new InMemoryDeduplicator();

        // Act
        var acquired = await deduplicator.TryAcquireAsync("key1", TimeSpan.FromSeconds(5));

        // Assert
        acquired.Should().BeTrue();
    }

    [Fact]
    public async Task TryAcquireAsync_ShouldReturnFalse_ForDuplicateKey()
    {
        // Arrange
        var deduplicator = new InMemoryDeduplicator();
        await deduplicator.TryAcquireAsync("key1", TimeSpan.FromSeconds(5));

        // Act
        var acquired = await deduplicator.TryAcquireAsync("key1", TimeSpan.FromSeconds(5));

        // Assert
        acquired.Should().BeFalse();
    }

    [Fact]
    public async Task TryAcquireAsync_ShouldReturnTrue_AfterTTLExpires()
    {
        // Arrange
        var deduplicator = new InMemoryDeduplicator();
        await deduplicator.TryAcquireAsync("key1", TimeSpan.FromMilliseconds(500));

        // Act
        await Task.Delay(600); // Wait for TTL to expire
        var acquired = await deduplicator.TryAcquireAsync("key1", TimeSpan.FromSeconds(5));

        // Assert
        acquired.Should().BeTrue();
    }

    [Fact]
    public async Task BackgroundCleanup_ShouldRemoveExpiredEntries()
    {
        // Arrange
        var deduplicator = new InMemoryDeduplicator();
        await deduplicator.TryAcquireAsync("key1", TimeSpan.FromMilliseconds(500));
        await deduplicator.TryAcquireAsync("key2", TimeSpan.FromSeconds(10));

        // Act
        await Task.Delay(1500); // Wait for cleanup timer + key1 expiry

        // Assert
        // key1 should be cleaned up, key2 should still block
        (await deduplicator.TryAcquireAsync("key1", TimeSpan.FromSeconds(5))).Should().BeTrue();
        (await deduplicator.TryAcquireAsync("key2", TimeSpan.FromSeconds(5))).Should().BeFalse();
    }

    [Fact]
    public async Task DifferentKeys_ShouldNotConflict()
    {
        // Arrange
        var deduplicator = new InMemoryDeduplicator();

        // Act
        var acquired1 = await deduplicator.TryAcquireAsync("key1", TimeSpan.FromSeconds(5));
        var acquired2 = await deduplicator.TryAcquireAsync("key2", TimeSpan.FromSeconds(5));
        var acquired3 = await deduplicator.TryAcquireAsync("key3", TimeSpan.FromSeconds(5));

        // Assert
        acquired1.Should().BeTrue();
        acquired2.Should().BeTrue();
        acquired3.Should().BeTrue();
    }

    [Fact]
    public void Dispose_ShouldStopCleanupTimer()
    {
        // Arrange
        var deduplicator = new InMemoryDeduplicator();

        // Act
        deduplicator.Dispose();

        // Assert
        // Should not throw
        deduplicator.Dispose(); // Double dispose should be safe
    }
}
