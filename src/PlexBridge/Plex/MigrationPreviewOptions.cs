namespace PlexBridge.Plex;

/// <summary>Bounds a migration preview. Each Plex page uses the request timeout. The whole preview uses the deadline.</summary>
public sealed class MigrationPreviewOptions
{
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(20);
    public TimeSpan Deadline { get; set; } = TimeSpan.FromMinutes(15);
    public int PageSize { get; set; } = 100;
}
