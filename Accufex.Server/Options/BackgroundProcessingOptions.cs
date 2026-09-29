namespace Accufex.Server.Options;

public class BackgroundProcessingOptions
{
    public const string SectionName = "BackgroundProcessing";

    public int MaxConcurrentJobs { get; set; } = 2; // Conservative default concurrency

    public int QueueCapacity { get; set; } = 100;

    public int JobTimeoutMinutes { get; set; } = 15;
}
