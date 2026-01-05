using ChokaQ.Abstractions;

namespace ChokaQ.Core.Jobs;

public record PrintMessageJob(string Text) : IChokaQJob
{
    // Auto-generate ID on creation
    public string Id { get; init; } = Guid.NewGuid().ToString();
}