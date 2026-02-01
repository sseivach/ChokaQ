using ChokaQ.Abstractions.Jobs;

namespace ChokaQ.Sample.Pipe.Jobs;

// Inherit from ChokaQBaseJob to satisfy the IChokaQQueue generic constraint.
public record FastLog(string Message, string Level) : ChokaQBaseJob;

public record MetricEvent(string SensorId, double Value, DateTime Timestamp) : ChokaQBaseJob;

public record SlowOperation(int DurationMs) : ChokaQBaseJob;