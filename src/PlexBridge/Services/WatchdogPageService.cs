using Microsoft.EntityFrameworkCore;
using PlexBridge.Data;
using PlexBridge.Security;
using PlexBridge.Watchdog;

namespace PlexBridge.Services;

public sealed record LiveSessionView(
    string SessionId,
    string Title,
    string User,
    string Client,
    string Decision,
    string Speed,
    string Health,
    string? Rule);

public sealed record LiveServerView(
    int ServerId,
    int Slot,
    string Name,
    bool Watching,
    DateTimeOffset? PolledUtc,
    string? Error,
    string Notifications,
    IReadOnlyList<LiveSessionView> Sessions);

public sealed class WatchdogLiveBoard
{
    private readonly object _gate = new();
    private IReadOnlyList<LiveServerView> _servers = [];

    public IReadOnlyList<LiveServerView> Snapshot()
    {
        lock (_gate)
            return _servers;
    }

    public void Publish(IReadOnlyList<LiveServerView> servers)
    {
        lock (_gate)
            _servers = servers;
    }
}

public sealed class WatchdogSettingsInput
{
    public int ServerId { get; set; }
    public bool Enabled { get; set; }
    public int PollIntervalSeconds { get; set; } = WatchdogDefaults.PollSeconds;
    public int OffsetStallSeconds { get; set; } = WatchdogDefaults.OffsetStallSeconds;
    public int SlowTranscodeSeconds { get; set; } = WatchdogDefaults.SlowTranscodeSeconds;
    public int TranscodeMissingSeconds { get; set; } = WatchdogDefaults.TranscodeMissingSeconds;
    public int LongBufferingSeconds { get; set; } = WatchdogDefaults.LongBufferingSeconds;
    public string RecoveryMode { get; set; } = WatchdogRecovery.ReportOnly;
    public string? LogFolder { get; set; }
    public bool UseNotifications { get; set; } = true;
}

public sealed record WatchdogSettingsForm(
    int ServerId,
    int Slot,
    string Name,
    string BaseUrl,
    bool HasToken,
    bool Enabled,
    int PollIntervalSeconds,
    int OffsetStallSeconds,
    int SlowTranscodeSeconds,
    int TranscodeMissingSeconds,
    int LongBufferingSeconds,
    string RecoveryMode,
    string LogFolder,
    bool UseNotifications);

public sealed record WatchdogIncidentView(
    int Id,
    string ServerName,
    int ServerSlot,
    string Title,
    string When,
    string User,
    string Client,
    string Rule,
    string Outcome,
    string Decision,
    string? FilePath,
    string? Guids,
    string? Container,
    string? VideoCodec,
    string? AudioCodec,
    string? VideoResolution,
    string Bitrate,
    bool SubtitleBurn,
    string? SubtitleCodec,
    bool? Hardware,
    bool? FullPipeline,
    string? ActionText,
    string? ActionDetail,
    string ActionStatus,
    bool ShowButton,
    string ButtonLabel,
    IReadOnlyList<string> Timeline,
    string? LogExcerpt);

public sealed record WatchdogScreen(
    IReadOnlyList<WatchdogSettingsForm> Settings,
    IReadOnlyList<LiveServerView> Live,
    IReadOnlyList<WatchdogIncidentView> Incidents,
    PatternReport Patterns);

public sealed class WatchdogPageService(IDbContextFactory<ApplicationDbContext> databases, WatchdogLiveBoard board, WatchdogActions actions)
{
    public async Task<WatchdogScreen> LoadAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await databases.CreateDbContextAsync(cancellationToken);
        var servers = await db.PlexServers.AsNoTracking().OrderBy(server => server.Slot).ThenBy(server => server.Id).ToListAsync(cancellationToken);
        var settings = await db.WatchdogSettings.AsNoTracking().ToListAsync(cancellationToken);
        var incidents = (await db.WatchdogIncidents.AsNoTracking()
            .OrderByDescending(item => item.Id)
            .Take(200)
            .ToListAsync(cancellationToken))
            .OrderByDescending(item => item.StartedUtc)
            .ToList();

        var forms = servers.Select(server => Form(server, settings.FirstOrDefault(item => item.PlexServerConnectionId == server.Id))).ToList();
        var modes = forms.ToDictionary(form => form.ServerId, form => form.RecoveryMode);
        var views = incidents.Take(50).Select(incident => ToView(incident, modes)).ToList();
        var patterns = WatchdogPatterns.Build(incidents.Select(ToPattern).ToList());
        return new WatchdogScreen(forms, board.Snapshot(), views, patterns);
    }

    public async Task SaveAsync(WatchdogSettingsInput input, CancellationToken cancellationToken = default)
    {
        var error = WatchdogSettingsRules.Validate(
            input.PollIntervalSeconds,
            input.OffsetStallSeconds,
            input.SlowTranscodeSeconds,
            input.TranscodeMissingSeconds,
            input.LongBufferingSeconds,
            input.RecoveryMode,
            input.LogFolder);
        if (error is not null)
            throw new PlexBridgeException(error);
        if (!WatchdogSettingsRules.NormalizeFolder(input.LogFolder, out var folder, out _))
            throw new PlexBridgeException("The logs folder path could not be read.");

        await using var db = await databases.CreateDbContextAsync(cancellationToken);
        var server = await db.PlexServers.FirstOrDefaultAsync(item => item.Id == input.ServerId, cancellationToken)
            ?? throw new PlexBridgeException("That server was not found.");
        var row = await db.WatchdogSettings.FirstOrDefaultAsync(item => item.PlexServerConnectionId == server.Id, cancellationToken);
        if (row is null)
        {
            row = new WatchdogSetting { PlexServerConnectionId = server.Id };
            db.WatchdogSettings.Add(row);
        }

        row.Enabled = input.Enabled;
        row.PollIntervalSeconds = input.PollIntervalSeconds;
        row.OffsetStallSeconds = input.OffsetStallSeconds;
        row.SlowTranscodeSeconds = input.SlowTranscodeSeconds;
        row.TranscodeMissingSeconds = input.TranscodeMissingSeconds;
        row.LongBufferingSeconds = input.LongBufferingSeconds;
        row.RecoveryMode = input.RecoveryMode;
        row.LogFolder = folder;
        row.UseNotifications = input.UseNotifications;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<WatchdogActionResult> ActAsync(int incidentId, CancellationToken cancellationToken = default)
    {
        await using var db = await databases.CreateDbContextAsync(cancellationToken);
        var incident = await db.WatchdogIncidents.AsNoTracking().FirstOrDefaultAsync(item => item.Id == incidentId, cancellationToken)
            ?? throw new PlexBridgeException("That incident was not found.");
        var mode = WatchdogRecovery.Off;
        if (incident.PlexServerConnectionId is int serverId)
        {
            mode = await db.WatchdogSettings.AsNoTracking()
                .Where(item => item.PlexServerConnectionId == serverId)
                .Select(item => item.RecoveryMode)
                .FirstOrDefaultAsync(cancellationToken) ?? WatchdogRecovery.Off;
        }

        if (mode == WatchdogRecovery.Off)
            throw new PlexBridgeException("Recovery is off for that server.");
        return await actions.ExecuteAsync(incidentId, cancellationToken);
    }

    private static WatchdogSettingsForm Form(PlexServerConnection server, WatchdogSetting? setting) => new(
        server.Id,
        server.Slot,
        server.Name,
        server.BaseUrl,
        !string.IsNullOrEmpty(server.ProtectedToken),
        setting?.Enabled ?? false,
        setting?.PollIntervalSeconds ?? WatchdogDefaults.PollSeconds,
        setting?.OffsetStallSeconds ?? WatchdogDefaults.OffsetStallSeconds,
        setting?.SlowTranscodeSeconds ?? WatchdogDefaults.SlowTranscodeSeconds,
        setting?.TranscodeMissingSeconds ?? WatchdogDefaults.TranscodeMissingSeconds,
        setting?.LongBufferingSeconds ?? WatchdogDefaults.LongBufferingSeconds,
        setting?.RecoveryMode ?? WatchdogRecovery.ReportOnly,
        setting?.LogFolder ?? "",
        setting?.UseNotifications ?? true);

    private static WatchdogIncidentView ToView(WatchdogIncident incident, IReadOnlyDictionary<int, string> modes)
    {
        var mode = incident.PlexServerConnectionId is int serverId && modes.TryGetValue(serverId, out var stored)
            ? stored
            : WatchdogRecovery.Off;
        var computed = incident.ProposedAction;
        if (string.IsNullOrWhiteSpace(computed) && mode != WatchdogRecovery.Off)
        {
            computed = RecoveryPlanner.Propose(
                WatchdogRecovery.ReportOnly,
                incident.StallRule != StallRules.TranscodeDisappeared && PlexWatchdogPolicy.IsSafeId(incident.TranscodeSessionId),
                incident.TranscodeSessionId)?.Text;
        }

        var show = mode != WatchdogRecovery.Off
            && incident.Resolution == IncidentResolutions.StillStuck
            && incident.ActionStatus != WatchdogActionStatus.Done
            && (mode == WatchdogRecovery.ReportOnly || incident.ActionStatus == WatchdogActionStatus.Failed);
        return new WatchdogIncidentView(
            incident.Id,
            incident.ServerName,
            incident.ServerSlot,
            incident.DisplayTitle,
            incident.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
            string.IsNullOrWhiteSpace(incident.AccountTitle) ? "Unknown user" : incident.AccountTitle,
            WatchdogText.Client(incident.PlayerProduct, incident.PlayerPlatform, incident.PlayerDevice),
            WatchdogText.Rule(incident.StallRule),
            WatchdogText.Outcome(incident.Resolution),
            WatchdogText.Decision(incident.PlayDecision, incident.HardwareTranscode, incident.HardwareFullPipeline),
            incident.FilePath,
            incident.Guids,
            incident.Container,
            incident.VideoCodec,
            incident.AudioCodec,
            incident.VideoResolution,
            WatchdogText.Bitrate(incident.BitrateKbps),
            incident.SubtitleBurn,
            incident.SubtitleCodec,
            incident.HardwareTranscode,
            incident.HardwareFullPipeline,
            computed,
            incident.ActionDetail,
            incident.ActionStatus,
            show,
            incident.ActionStatus == WatchdogActionStatus.Failed ? "Try again" : "Do it now",
            TimelineCodec.Read(incident.TimelineJson).Select(row => row.Describe()).ToList(),
            incident.LogExcerpt);
    }

    private static PatternIncident ToPattern(WatchdogIncident incident) => new(
        incident.Id,
        incident.FilePath,
        incident.PlayerProduct,
        incident.PlayerPlatform,
        incident.PlayerDevice,
        incident.VideoCodec,
        incident.VideoResolution,
        incident.SubtitleBurn,
        incident.SubtitleCodec,
        incident.PlayDecision,
        incident.HardwareTranscode,
        incident.HardwareFullPipeline);
}
