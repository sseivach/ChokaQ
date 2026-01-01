using ChokaQ.Abstractions;

namespace ChokaQ.Core.Jobs;

// Simple record is perfect for DTOs
public record PrintMessageJob(string Text) : IChokaQJob;