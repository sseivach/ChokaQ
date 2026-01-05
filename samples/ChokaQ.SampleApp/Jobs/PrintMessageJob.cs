using ChokaQ.Abstractions;

namespace ChokaQ.SampleApp.Jobs;

public record PrintMessageJob(string Text) : ChokaQBaseJob;