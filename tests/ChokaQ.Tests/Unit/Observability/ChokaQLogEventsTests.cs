using ChokaQ.Core.Observability;

namespace ChokaQ.Tests.Unit.Observability;

public class ChokaQLogEventsTests
{
    [Fact]
    public void ChokaQLogEvents_ShouldHaveUniqueStableIdsAndNames()
    {
        var events = ChokaQLogEvents.All;

        // Log EventId values are part of the operational contract. SIEM queries, alert rules,
        // and runbooks can safely key on the number only if every number has one meaning.
        events.Select(item => item.Id)
            .Should().OnlyHaveUniqueItems();
        events.Select(item => item.Name)
            .Should().OnlyHaveUniqueItems()
            .And.NotContainNulls()
            .And.NotContain(string.Empty);
    }

    [Theory]
    [InlineData(1000, "WorkerStarted")]
    [InlineData(2003, "JobSucceededArchived")]
    [InlineData(2023, "JobRetriesExhaustedDlq")]
    [InlineData(3000, "EnqueueDuplicateSkipped")]
    [InlineData(4002, "ZombieJobsArchived")]
    [InlineData(5002, "SqlInitializationFailed")]
    [InlineData(6000, "AdminCommandRejected")]
    public void ChokaQLogEvents_ShouldKeepPublishedIdsStable(int id, string name)
    {
        ChokaQLogEvents.All.Should().Contain(item => item.Id == id && item.Name == name);
    }
}
