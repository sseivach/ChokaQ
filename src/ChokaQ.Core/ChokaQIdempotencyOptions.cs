namespace ChokaQ.Core;

public sealed class ChokaQIdempotencyOptions
{
    public TimeSpan InProgressTtl { get; set; } = TimeSpan.FromMinutes(30);

    public TimeSpan? DefaultResultTtl { get; set; }

    public TimeSpan? MinResultTtl { get; set; }

    public TimeSpan? MaxResultTtl { get; set; }
}

