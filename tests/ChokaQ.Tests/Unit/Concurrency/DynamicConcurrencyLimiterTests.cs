using ChokaQ.Core.Concurrency;

namespace ChokaQ.Tests.Unit.Concurrency;

[Trait(TestCategories.Category, TestCategories.Unit)]
public class DynamicConcurrencyLimiterTests
{
    [Fact]
    public async Task WaitAsync_ShouldAcquirePermit_WhenAvailable()
    {
        // Arrange
        var semaphore = new DynamicConcurrencyLimiter(initialCapacity: 3);

        // Act
        await semaphore.WaitAsync();

        // Assert
        semaphore.RunningCount.Should().Be(1);
    }

    [Fact]
    public async Task WaitAsync_ShouldBlock_WhenNoPermitsAvailable()
    {
        // Arrange
        var semaphore = new DynamicConcurrencyLimiter(initialCapacity: 1);
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
        var semaphore = new DynamicConcurrencyLimiter(initialCapacity: 1);
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
        var semaphore = new DynamicConcurrencyLimiter(initialCapacity: 2);
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
    public async Task SetCapacity_ScaleDown_ShouldLetActiveWorkersDrainNaturally()
    {
        // Arrange
        var semaphore = new DynamicConcurrencyLimiter(initialCapacity: 5);

        // Act
        semaphore.SetCapacity(2); // Scale down from 5 to 2

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
    public async Task SetCapacity_ScaleDownWhileActive_ShouldBlockNewAcquiresUntilBelowNewCapacity()
    {
        var semaphore = new DynamicConcurrencyLimiter(initialCapacity: 5);
        for (var i = 0; i < 5; i++)
        {
            await semaphore.WaitAsync();
        }

        semaphore.SetCapacity(2);
        var waitTask = semaphore.WaitAsync();

        for (var i = 0; i < 3; i++)
        {
            semaphore.Release();
            await Task.Delay(25);
            waitTask.IsCompleted.Should().BeFalse();
        }

        semaphore.Release();
        await waitTask.WaitAsync(TimeSpan.FromSeconds(1));

        semaphore.RunningCount.Should().Be(2);
    }

    [Fact]
    public void Release_WhenNoSlotWasAcquired_ShouldThrow()
    {
        var semaphore = new DynamicConcurrencyLimiter(initialCapacity: 1);

        Action act = () => semaphore.Release();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*without a matching successful WaitAsync*");
        semaphore.RunningCount.Should().Be(0);
    }

    [Fact]
    public async Task RunningCount_ShouldTrackActivePermits()
    {
        // Arrange
        var semaphore = new DynamicConcurrencyLimiter(initialCapacity: 5);

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
        var semaphore = new DynamicConcurrencyLimiter(initialCapacity: 10);

        // Assert
        semaphore.Capacity.Should().Be(10);
    }

    [Fact]
    public async Task SetCapacity_ManyWaitersScaleUp_ShouldWakeThroughRelay()
    {
        var semaphore = new DynamicConcurrencyLimiter(initialCapacity: 1);
        await semaphore.WaitAsync();

        var waiters = Enumerable.Range(0, 20)
            .Select(_ => semaphore.WaitAsync())
            .ToArray();

        await Task.Delay(50);
        waiters.Should().OnlyContain(task => !task.IsCompleted);

        semaphore.SetCapacity(21);

        await Task.WhenAll(waiters).WaitAsync(TimeSpan.FromSeconds(2));
        semaphore.RunningCount.Should().Be(21);

        for (var i = 0; i < 21; i++)
        {
            semaphore.Release();
        }
    }


    [Fact]
    public async Task HighConcurrency_ShouldNotExceedCapacity()
    {
        // Arrange
        var capacity = 5;
        var semaphore = new DynamicConcurrencyLimiter(initialCapacity: capacity);
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
        var semaphore = new DynamicConcurrencyLimiter(initialCapacity: 5);

        // Act
        semaphore.Dispose();

        // Assert
        // Should not throw
        semaphore.Dispose(); // Double dispose should be safe
    }

    [Fact]
    public async Task Dispose_WithWaitingAcquire_ShouldFailWaiterWithObjectDisposedException()
    {
        var semaphore = new DynamicConcurrencyLimiter(initialCapacity: 1);
        await semaphore.WaitAsync();
        var waiter = semaphore.WaitAsync();

        semaphore.Dispose();

        Func<Task> act = () => waiter.WaitAsync(TimeSpan.FromSeconds(1));
        await act.Should().ThrowAsync<ObjectDisposedException>();

        // Releasing an already-acquired slot during shutdown is allowed.
        semaphore.Release();
        semaphore.RunningCount.Should().Be(0);
    }

    [Fact]
    public async Task WaitAsync_AfterDispose_ShouldThrowObjectDisposedException()
    {
        var semaphore = new DynamicConcurrencyLimiter(initialCapacity: 1);
        semaphore.Dispose();

        Func<Task> act = () => semaphore.WaitAsync();
        await act.Should().ThrowAsync<ObjectDisposedException>();
    }
}
