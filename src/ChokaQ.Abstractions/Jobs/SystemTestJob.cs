namespace ChokaQ.Abstractions.Jobs;

public record SystemTestJob(string Payload, int DurationMs = 1000, bool ShouldFail = false) : ChokaQBaseJob;