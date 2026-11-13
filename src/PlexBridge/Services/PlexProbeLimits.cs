namespace PlexBridge.Services;

/// <summary>How long one connection check may run before the row is marked finished.</summary>
public sealed class PlexProbeLimits
{
    public TimeSpan Limit { get; set; } = TimeSpan.FromSeconds(8);
}
