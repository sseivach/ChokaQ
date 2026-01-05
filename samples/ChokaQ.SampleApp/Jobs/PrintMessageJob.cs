using ChokaQ.Abstractions;

namespace ChokaQ.SampleApp.Jobs;

public record PrintMessageJob(string Text) : IChokaQJob
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
}