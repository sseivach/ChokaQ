using ChokaQ.Core.Concurrency;

namespace ChokaQ.Tests.Unit.Concurrency;

public class ElasticSemaphoreTests
{
    [Fact]
    public async Task WaitAsync_ShouldAcquirePermit_WhenAvailable()
    {
        // Arrange
        var semaphore = new ElasticSemaphore(initialCapacity: 3);

        // Act
        await semaphore.WaitAsync();

        // Assert
        semaphore.RunningCount.Should().Be(1);
    }

    [Fact]
    public async Task WaitAsync_ShouldBlock_WhenNoPermitsAvailable()
    {
        // Arrange
        var semaphore = new ElasticSemaphore(initialCapacity: 1);
        await semaphore.WaitAsync(); // Consume the only permit

        // Act
        var waitTask = semaphore.WaitAsync();

        // Assert
        await Task.Delay(100); // Give it time to block
        waitTask.IsCompleted.Should().BeFalse();
        
        // Cleanup
        semaphore.Release();
        await waitTask; // Should complete now
    }

    [Fact]
    public async Task Release_ShouldFreePermit_ForWaiters()
    {
        // Arrange
        var semaphore = new ElasticSemaphore(initialCapacity: 1);
        await semaphore.WaitAsync(); // Consume permit
        var waitTask = semaphore.WaitAsync(); // This will block

        // Act
        semaphore.Release();

        // Assert
        await waitTask.WaitAsync(TimeSpan.FromSeconds(1)); // Should complete quickly
        waitTask.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task SetCapacity_ScaleUp_ShouldReleaseAdditionalPermits()
    {
        // Arrange
        var semaphore = new ElasticSemaphore(initialCapacity: 2);
        await semaphore.WaitAsync();
        await semaphore.WaitAsync(); // Both permits consumed

        var waitTask = semaphore.WaitAsync(); // This will block

        // Act
        semaphore.SetCapacity(5); // Scale up to 5
        await Task.Delay(50); // Give time for permits to be released

        // Assert
        await waitTask.WaitAsync(TimeSpan.FromSeconds(1));
        waitTask.IsCompleted.Should().BeTrue();
        semaphore.Capacity.Should().Be(5);
    }

    [Fact]
    public async Task SetCapacity_ScaleDown_ShouldBurnPermits()
    {
        // Arrange
        var semaphore = new ElasticSemaphore(initialCapacity: 5);

        // Act
        semaphore.SetCapacity(2); // Scale down from 5 to 2
        await Task.Delay(100); // Give time for burn loop to consume permits

        // Assert
        semaphore.Capacity.Should().Be(2);
        
        // Should only be able to acquire 2 permits
        await semaphore.WaitAsync();
        await semaphore.WaitAsync();
        
        var waitTask = semaphore.WaitAsync();
        await Task.Delay(100);
        waitTask.IsCompleted.Should().BeFalse(); // Third acquire should block
        
        // Cleanup
        semaphore.Release();
        await waitTask;
    }

    [Fact]
    public async Task RunningCount_ShouldTrackActivePermits()
    {
        // Arrange
        var semaphore = new ElasticSemaphore(initialCapacity: 5);

        // Act & Assert
        semaphore.RunningCount.Should().Be(0);

        await semaphore.WaitAsync();
        semaphore.RunningCount.Should().Be(1);

        await semaphore.WaitAsync();
        semaphore.RunningCount.Should().Be(2);

        semaphore.Release();
        semaphore.RunningCount.Should().Be(1);

        semaphore.Release();
        semaphore.RunningCount.Should().Be(0);
    }

    [Fact]
    public void Capacity_ShouldReflectCurrentMaximum()
    {
        // Arrange & Act
        var semaphore = new ElasticSemaphore(initialCapacity: 10);

        // Assert
        semaphore.Capacity.Should().Be(10);
    }

    [Fact]
    public async Task BurnLoop_CancellationShouldAbort()
    {
        // Arrange
        var semaphore = new ElasticSemaphore(initialCapacity: 10);

        // Act
        semaphore.SetCapacity(2); // This starts a burn loop in background
        await Task.Delay(50); // Let it start burning
        semaphore.Dispose(); // Dispose cancels the burn loop

        // Assert
        // Disposal should not throw, even with burn loop running
    }


    [Fact]
    public async Task HighConcurrency_ShouldNotExceedCapacity()
    {
        // Arrange
        var capacity = 5;
        var semaphore = new ElasticSemaphore(initialCapacity: capacity);
        var maxConcurrent = 0;
        var currentConcurrent = 0;
        var lockObj = new object();

        // Act
        var tasks = Enumerable.Range(0, 20).Select(async _ =>
        {
            await semaphore.WaitAsync();
            
            lock (lockObj)
            {
                currentConcurrent++;
                if (currentConcurrent > maxConcurrent)
                    maxConcurrent = currentConcurrent;
            }

            await Task.Delay(10); // Simulate work

            lock (lockObj)
            {
                currentConcurrent--;
            }

            semaphore.Release();
        });

        await Task.WhenAll(tasks);

        // Assert
        maxConcurrent.Should().BeLessOrEqualTo(capacity);
        semaphore.RunningCount.Should().Be(0); // All released
    }

    [Fact]
    public void Dispose_ShouldCleanup()
    {
        // Arrange
        var semaphore = new ElasticSemaphore(initialCapacity: 5);

        // Act
        semaphore.Dispose();

        // Assert
        // Should not throw
        semaphore.Dispose(); // Double dispose should be safe
    }
}
