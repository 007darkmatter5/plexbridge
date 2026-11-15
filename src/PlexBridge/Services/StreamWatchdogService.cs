using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using PlexBridge.Data;
using PlexBridge.Plex;
using PlexBridge.Security;
using PlexBridge.Watchdog;

namespace PlexBridge.Services;

/// <summary>
/// Polls the slots the admin turned on. A failed poll does not close incidents.
/// Tokens are read from the data-protection column and are not logged.
/// </summary>
public sealed class StreamWatchdogService(
    IServiceScopeFactory scopes,
    PlexSessionClient sessions,
    WatchdogActions actions,
    WatchdogSessionEnds ends,
    WatchdogLiveBoard board,
    IPlexAddressGuard addresses,
    ISecretProtector protector,
    TimeProvider clock,
    ILogger<StreamWatchdogService> logger) : BackgroundService
{
    private readonly Dictionary<int, ServerRuntime> _runtimes = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollAllAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning("Watchdog poll failed ({ExceptionType}).", exception.GetType().Name);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        foreach (var runtime in _runtimes.Values)
            StopListener(runtime);
    }

    private async Task PollAllAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var databases = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var db = await databases.CreateDbContextAsync(cancellationToken);
        var servers = await db.PlexServers.AsNoTracking().OrderBy(server => server.Slot).ToListAsync(cancellationToken);
        var settings = await db.WatchdogSettings.AsNoTracking().ToListAsync(cancellationToken);
        var known = servers.Select(server => server.Id).ToHashSet();
        foreach (var id in _runtimes.Keys.Where(id => !known.Contains(id)).ToArray())
        {
            StopListener(_runtimes[id]);
            _runtimes.Remove(id);
        }

        var live = new List<LiveServerView>();
        foreach (var server in servers)
        {
            var setting = settings.FirstOrDefault(item => item.PlexServerConnectionId == server.Id);
            if (!_runtimes.TryGetValue(server.Id, out var runtime))
            {
                runtime = new ServerRuntime();
                _runtimes[server.Id] = runtime;
            }

            if (setting is not { Enabled: true })
            {
                StopListener(runtime);
                runtime.Hydrated = false;
                runtime.Watch = new ServerWatch();
                live.Add(new LiveServerView(server.Id, server.Slot, server.Name, false, null, null, "off", []));
                continue;
            }

            live.Add(await PollServerAsync(db, server, setting, runtime, cancellationToken));
        }

        board.Publish(live);
    }

    private async Task<LiveServerView> PollServerAsync(ApplicationDbContext db, PlexServerConnection server, WatchdogSetting setting, ServerRuntime runtime, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var pollSeconds = setting.PollIntervalSeconds is >= WatchdogDefaults.PollMinSeconds and <= WatchdogDefaults.PollMaxSeconds
            ? setting.PollIntervalSeconds
            : WatchdogDefaults.PollSeconds;
        string token;
        try
        {
            token = Unprotect(server);
        }
        catch (PlexBridgeException exception)
        {
            StopListener(runtime);
            return Remember(runtime, server, setting, null, exception.Message, []);
        }

        EnsureListener(runtime, server, setting.UseNotifications, token, cancellationToken);
        var wake = runtime.Listener?.ConsumeWake() == true;
        var due = runtime.LastPoll == default || now >= runtime.NextPoll || (wake && now - runtime.LastPoll >= TimeSpan.FromSeconds(1));
        if (!due && runtime.LastLive is not null)
        {
            if (wake)
                runtime.Listener?.RequestWake();
            return runtime.LastLive with { Notifications = Notes(setting, runtime) };
        }

        runtime.LastPoll = now;
        runtime.NextPoll = now.AddSeconds(pollSeconds);
        IReadOnlyList<WatchedSession> sessionsNow;
        try
        {
            sessionsNow = await sessions.GetSessionsAsync(PlexServerUrl.ToUri(server.BaseUrl), token, now, cancellationToken);
        }
        catch (PlexRequestException exception)
        {
            return Remember(runtime, server, setting, now, SecretRedactor.ForDisplay(exception.Message, token), runtime.LastLive?.Sessions ?? []);
        }
        catch (Exception exception)
        {
            logger.LogWarning("Watchdog poll for server {ServerId} failed ({ExceptionType}).", server.Id, exception.GetType().Name);
            return Remember(runtime, server, setting, now, "The session list could not be read.", runtime.LastLive?.Sessions ?? []);
        }

        var thresholds = WatchdogThresholds.FromSeconds(setting.OffsetStallSeconds, setting.SlowTranscodeSeconds, setting.TranscodeMissingSeconds, setting.LongBufferingSeconds);
        var mode = WatchdogRecovery.IsKnown(setting.RecoveryMode) ? setting.RecoveryMode : WatchdogRecovery.ReportOnly;
        if (!runtime.Hydrated)
        {
            var open = await db.WatchdogIncidents
                .Where(item => item.PlexServerConnectionId == server.Id && item.Resolution == IncidentResolutions.StillStuck)
                .ToListAsync(cancellationToken);
            foreach (var incident in open)
                runtime.Watch.Rehydrate(incident.PlaySessionId, incident.StallRule);
            runtime.Hydrated = true;
        }

        var updates = runtime.Watch.Apply(sessionsNow, thresholds);
        var auto = new List<int>();
        foreach (var update in updates)
            await ApplyUpdateAsync(db, server, setting, mode, update, auto, cancellationToken);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            logger.LogWarning("Watchdog could not save an incident for server {ServerId} ({ExceptionType}).", server.Id, exception.GetType().Name);
        }

        foreach (var incidentId in auto)
        {
            try
            {
                await actions.ExecuteAsync(incidentId, cancellationToken);
            }
            catch (Exception exception)
            {
                logger.LogWarning("Watchdog recovery for incident {IncidentId} failed ({ExceptionType}).", incidentId, exception.GetType().Name);
            }
        }

        var rows = sessionsNow.Select(session => new LiveSessionView(
            session.PlaySessionId,
            session.DisplayTitle,
            string.IsNullOrWhiteSpace(session.AccountTitle) ? "Unknown user" : session.AccountTitle,
            WatchdogText.Client(session.PlayerProduct, session.PlayerPlatform, session.PlayerDevice),
            WatchdogText.Decision(session.PlayDecision, session.HardwareTranscode, session.HardwareFullPipeline),
            WatchdogText.Speed(session.Sample.Speed, session.Sample.Throttled),
            runtime.Watch.HealthFor(session.PlaySessionId, session.Sample.State),
            runtime.Watch.OpenRuleFor(session.PlaySessionId))).ToList();
        return Remember(runtime, server, setting, now, null, rows);
    }

    private async Task ApplyUpdateAsync(
        ApplicationDbContext db,
        PlexServerConnection server,
        WatchdogSetting setting,
        string mode,
        WatchUpdate update,
        List<int> auto,
        CancellationToken cancellationToken)
    {
        if (update.Signal is WatchSignal.None)
            return;

        var incident = await db.WatchdogIncidents
            .Where(item => item.PlexServerConnectionId == server.Id && item.PlaySessionId == update.SessionId && item.Resolution == IncidentResolutions.StillStuck)
            .OrderByDescending(item => item.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (update.Signal == WatchSignal.Opened && incident is null && update.Session is not null && update.Rule is not null)
        {
            var session = update.Session;
            var transcodeRunning = session.Sample.TranscodePresent && update.Rule != StallRules.TranscodeDisappeared;
            var plan = RecoveryPlanner.Propose(mode, transcodeRunning, session.TranscodeSessionId);
            incident = new WatchdogIncident
            {
                PlexServerConnectionId = server.Id,
                ServerName = WatchdogText.Clip(server.Name, 80),
                ServerSlot = server.Slot,
                PlaySessionId = session.PlaySessionId,
                SessionKey = session.SessionKey,
                TranscodeSessionId = session.TranscodeSessionId,
                DisplayTitle = string.IsNullOrWhiteSpace(session.DisplayTitle) ? "Untitled" : session.DisplayTitle,
                AccountTitle = session.AccountTitle,
                PlayerProduct = session.PlayerProduct,
                PlayerPlatform = session.PlayerPlatform,
                PlayerDevice = session.PlayerDevice,
                Guids = session.Guids,
                RatingKey = session.RatingKey,
                FilePath = session.FilePath,
                PartId = session.PartId,
                Container = session.Container,
                VideoCodec = session.VideoCodec,
                AudioCodec = session.AudioCodec,
                VideoResolution = session.VideoResolution,
                BitrateKbps = session.BitrateKbps,
                PlayDecision = session.PlayDecision,
                HardwareTranscode = session.HardwareTranscode,
                HardwareFullPipeline = session.HardwareFullPipeline,
                SubtitleBurn = session.SubtitleBurn,
                SubtitleCodec = session.SubtitleCodec,
                StallRule = update.Rule,
                Resolution = IncidentResolutions.StillStuck,
                TimelineJson = TimelineCodec.Serialize(update.Timeline),
                LogExcerpt = ClipLog(ReadLogs(setting.LogFolder, session.Sample.At)),
                ProposedAction = plan?.Text,
                ActionKind = plan?.Kind,
                ActionStatus = plan is null ? WatchdogActionStatus.None : WatchdogActionStatus.Proposed,
                StartedUtc = session.Sample.At,
                UpdatedUtc = session.Sample.At
            };
            db.WatchdogIncidents.Add(incident);
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Watchdog incident {IncidentId} on server {ServerId}: {Rule}.", incident.Id, server.Id, update.Rule);
            if (mode == WatchdogRecovery.Auto)
                auto.Add(incident.Id);
            return;
        }

        if (incident is null)
            return;

        if (update.Signal == WatchSignal.StillOpen && mode == WatchdogRecovery.Auto && incident.ActionStatus == WatchdogActionStatus.Proposed)
            auto.Add(incident.Id);

        incident.UpdatedUtc = clock.GetUtcNow();
        incident.TimelineJson = TimelineCodec.Merge(incident.TimelineJson, update.Timeline);
        if (update.Signal is WatchSignal.Recovered or WatchSignal.Ended)
        {
            var endedByUs = update.Signal == WatchSignal.Ended && ends.Consume(server.Id, update.SessionId);
            incident.Resolution = update.Signal == WatchSignal.Recovered
                ? IncidentResolutions.Recovered
                : endedByUs ? IncidentResolutions.EndedByWatchdog : IncidentResolutions.UserStopped;
            incident.ResolvedUtc = incident.UpdatedUtc;
            incident.LogExcerpt = ClipLog(PlexLogTail.Merge(incident.LogExcerpt, PlexLogTail.Excerpt(setting.LogFolder, incident.UpdatedUtc)));
            logger.LogInformation("Watchdog incident {IncidentId} on server {ServerId} is {Resolution}.", incident.Id, server.Id, incident.Resolution);
        }
    }

    private void EnsureListener(ServerRuntime runtime, PlexServerConnection server, bool useNotifications, string token, CancellationToken stoppingToken)
    {
        if (!useNotifications)
        {
            StopListener(runtime);
            return;
        }

        var key = server.BaseUrl + "\n" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        if (runtime.ListenerTask is { IsCompleted: false } && runtime.ListenerKey == key)
            return;

        StopListener(runtime);
        var listener = new PlexNotificationListener(addresses, logger);
        listener.MarkConnecting();
        var stop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        runtime.Listener = listener;
        runtime.ListenerStop = stop;
        runtime.ListenerKey = key;
        runtime.ListenerTask = Task.Run(() => listener.RunAsync(server.Id, PlexServerUrl.ToUri(server.BaseUrl), token, stop.Token), CancellationToken.None);
    }

    private static void StopListener(ServerRuntime runtime)
    {
        try
        {
            runtime.ListenerStop?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        runtime.Listener?.MarkOff();
        runtime.Listener = null;
        runtime.ListenerStop = null;
        runtime.ListenerTask = null;
        runtime.ListenerKey = "";
    }

    private static LiveServerView Remember(ServerRuntime runtime, PlexServerConnection server, WatchdogSetting setting, DateTimeOffset? polled, string? error, IReadOnlyList<LiveSessionView> sessions)
    {
        var view = new LiveServerView(server.Id, server.Slot, server.Name, true, polled ?? runtime.LastLive?.PolledUtc, error, Notes(setting, runtime), sessions);
        runtime.LastLive = view;
        return view;
    }

    private static string Notes(WatchdogSetting setting, ServerRuntime runtime)
        => !setting.UseNotifications ? "off" : runtime.Listener?.Status ?? "off";

    private static string? ReadLogs(string? folder, DateTimeOffset at)
    {
        var lines = PlexLogTail.Excerpt(folder, at);
        return lines.Count == 0 ? null : string.Join('\n', lines);
    }

    private static string? ClipLog(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        return text.Length <= 8000 ? text : text[^8000..];
    }

    private string Unprotect(PlexServerConnection entity)
    {
        if (string.IsNullOrEmpty(entity.ProtectedToken))
            throw new PlexBridgeException("A Plex token is required.");
        try
        {
            return protector.Unprotect(entity.ProtectedToken);
        }
        catch (Exception exception) when (exception is CryptographicException)
        {
            logger.LogWarning("A stored Plex token could not be decrypted for server {ServerId}.", entity.Id);
            throw new PlexBridgeException("The saved token can't be read. Enter it again. This usually means the keys folder changed.");
        }
    }

    private sealed class ServerRuntime
    {
        public ServerWatch Watch { get; set; } = new();
        public bool Hydrated { get; set; }
        public DateTimeOffset NextPoll { get; set; }
        public DateTimeOffset LastPoll { get; set; }
        public LiveServerView? LastLive { get; set; }
        public PlexNotificationListener? Listener { get; set; }
        public CancellationTokenSource? ListenerStop { get; set; }
        public Task? ListenerTask { get; set; }
        public string ListenerKey { get; set; } = "";
    }
}
