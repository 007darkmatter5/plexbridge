using Microsoft.EntityFrameworkCore;
using PlexBridge.Data;
using PlexBridge.Lyrics;
using PlexBridge.Plex;
using PlexBridge.Security;

namespace PlexBridge.Services;

public enum LyricsJobState
{
    Running,
    Completed,
    Failed,
    Canceled
}

public sealed record LyricsJobSnapshot(
    string Id,
    LyricsJobState State,
    string Phase,
    string Status,
    string? Error,
    int Done,
    int Total,
    int ServerId,
    string ServerName,
    string LibraryName,
    IReadOnlyList<LyricsTrackRow> Tracks);

/// <summary>One lyrics scan or write at a time per admin. The work keeps going if the browser disconnects.</summary>
public sealed class LyricsJobs(IServiceScopeFactory scopes, ILogger<LyricsJobs> logger)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, RunningLyricsJob> _jobs = new(StringComparer.Ordinal);

    public LyricsJobSnapshot StartScan(string userId, int serverId, string sectionKey, string? artistKey, string? albumKey, string libraryName)
    {
        RunningLyricsJob job;
        lock (_gate)
        {
            if (_jobs.TryGetValue(userId, out var current) && current.State == LyricsJobState.Running)
                return current.Snapshot();
            job = new RunningLyricsJob(Guid.NewGuid().ToString("n"), userId, serverId, libraryName);
            _jobs[userId] = job;
        }

        logger.LogInformation("Lyrics scan {JobId} started for server {ServerId}.", job.Id, serverId);
        _ = Task.Run(() => ScanAsync(job, serverId, sectionKey, artistKey, albumKey));
        return job.Snapshot();
    }

    public LyricsJobSnapshot? Latest(string userId)
    {
        lock (_gate)
            return _jobs.TryGetValue(userId, out var job) ? job.Snapshot() : null;
    }

    public bool IsServerBusy(int serverId)
    {
        lock (_gate)
            return _jobs.Values.Any(job => job.State == LyricsJobState.Running && job.ServerId == serverId);
    }

    public void Cancel(string userId, string jobId)
    {
        lock (_gate)
        {
            if (_jobs.TryGetValue(userId, out var job) && job.Id == jobId && job.State == LyricsJobState.Running)
                job.Cancel.Cancel();
        }
    }

    public int Select(string userId, string jobId, IReadOnlyCollection<string> ratingKeys, bool selected)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(userId, out var job) || job.Id != jobId || job.State == LyricsJobState.Running)
                return 0;
            return job.Select(ratingKeys, selected);
        }
    }

    public LyricsJobSnapshot? StartApply(string userId, string jobId)
    {
        RunningLyricsJob? job;
        lock (_gate)
        {
            if (!_jobs.TryGetValue(userId, out job) || job.Id != jobId || job.State == LyricsJobState.Running)
                return job?.Snapshot();
            if (job.Tracks.All(row => !row.Selected || !row.CanSelect))
                return job.Snapshot();
            job.BeginApply();
        }

        logger.LogInformation("Lyrics write {JobId} started.", job.Id);
        _ = Task.Run(() => ApplyAsync(job));
        return job.Snapshot();
    }

    public async Task<string> RefreshAlbumAsync(string userId, string jobId, string albumKey, CancellationToken cancellationToken)
    {
        RunningLyricsJob? job;
        lock (_gate)
        {
            if (!_jobs.TryGetValue(userId, out job) || job.Id != jobId)
                throw new PlexBridgeException("That lyrics scan was not found.");
            if (job.State == LyricsJobState.Running)
                throw new PlexBridgeException("Wait until the current lyrics run finishes.");
        }

        var trackKeys = job.Tracks.Where(row => row.AlbumKey == albumKey).Select(row => row.RatingKey).ToList();
        await using var scope = scopes.CreateAsyncScope();
        var workflow = scope.ServiceProvider.GetRequiredService<LyricsWorkflow>();
        var (uri, token) = await OpenAsync(scope.ServiceProvider, job.ServerId, cancellationToken);
        return await workflow.RefreshAlbumAsync(uri, token, albumKey, trackKeys, cancellationToken);
    }

    private async Task ScanAsync(RunningLyricsJob job, int serverId, string sectionKey, string? artistKey, string? albumKey)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var databases = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
            var workflow = scope.ServiceProvider.GetRequiredService<LyricsWorkflow>();
            await using var db = await databases.CreateDbContextAsync(job.Cancel.Token);
            var server = await db.PlexServers.AsNoTracking().FirstOrDefaultAsync(item => item.Id == serverId, job.Cancel.Token)
                ?? throw new PlexBridgeException("That server was not found.");
            var setting = await db.LyricsSettings.AsNoTracking().FirstOrDefaultAsync(item => item.PlexServerConnectionId == server.Id, job.Cancel.Token);
            var maps = LyricsPaths.FromJson(setting?.PathMapsJson);
            job.ServerName = server.Name;
            var (uri, token) = Open(scope.ServiceProvider, server);
            var progress = new Progress<LyricsProgress>(update => job.Report(update.Status, update.Done, update.Total));
            var rows = await workflow.ScanAsync(uri, token, sectionKey, artistKey, albumKey, maps, null, false, progress, null, job.Cancel.Token);
            job.Complete(rows, "Scan finished");
        }
        catch (OperationCanceledException)
        {
            job.CancelJob();
        }
        catch (Exception exception) when (exception is PlexBridgeException or PlexRequestException)
        {
            job.Fail(exception.Message);
        }
        catch (Exception)
        {
            job.Fail("The lyrics scan could not finish.");
        }
    }

    private async Task ApplyAsync(RunningLyricsJob job)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var workflow = scope.ServiceProvider.GetRequiredService<LyricsWorkflow>();
            var databases = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
            await using var db = await databases.CreateDbContextAsync(job.Cancel.Token);
            var server = await db.PlexServers.AsNoTracking().FirstOrDefaultAsync(item => item.Id == job.ServerId, job.Cancel.Token)
                ?? throw new PlexBridgeException("That server was not found.");
            var (uri, token) = Open(scope.ServiceProvider, server);
            var selected = job.Tracks.Where(row => row.Selected && row.CanSelect).ToList();
            var progress = new Progress<LyricsProgress>(update => job.Report(update.Status, update.Done, update.Total));
            await workflow.PublishAsync(uri, token, selected, progress, job.Cancel.Token);
            var wrote = selected.Count(row => row.ApplyResult?.StartsWith("Wrote ", StringComparison.Ordinal) == true);
            job.Complete(job.Tracks, "Wrote " + wrote + " lyrics files.");
        }
        catch (OperationCanceledException)
        {
            job.CancelJob();
        }
        catch (Exception exception) when (exception is PlexBridgeException or PlexRequestException)
        {
            job.Fail(exception.Message);
        }
        catch (Exception)
        {
            job.Fail("The lyrics write could not finish.");
        }
    }

    private static async Task<(Uri Uri, string Token)> OpenAsync(IServiceProvider services, int serverId, CancellationToken cancellationToken)
    {
        var databases = services.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var db = await databases.CreateDbContextAsync(cancellationToken);
        var server = await db.PlexServers.AsNoTracking().FirstOrDefaultAsync(item => item.Id == serverId, cancellationToken)
            ?? throw new PlexBridgeException("That server was not found.");
        return Open(services, server);
    }

    private static (Uri Uri, string Token) Open(IServiceProvider services, PlexServerConnection server)
    {
        if (!PlexServerUrl.TryNormalize(server.BaseUrl, out var normalized, out _))
            throw new PlexBridgeException("The saved server URL is not valid.");
        var protector = services.GetRequiredService<ISecretProtector>();
        if (string.IsNullOrEmpty(server.ProtectedToken))
            throw new PlexBridgeException("A Plex token is required.");
        try
        {
            return (new Uri(normalized), protector.Unprotect(server.ProtectedToken));
        }
        catch (Exception exception) when (Crypto.IsProtectionFailure(exception))
        {
            throw new PlexBridgeException("The saved token can't be read. Enter it again.");
        }
    }

    private sealed class RunningLyricsJob
    {
        public RunningLyricsJob(string id, string userId, int serverId, string libraryName)
        {
            Id = id;
            UserId = userId;
            ServerId = serverId;
            LibraryName = libraryName;
        }

        public string Id { get; }
        public string UserId { get; }
        public int ServerId { get; }
        public string ServerName { get; set; } = "";
        public string LibraryName { get; }
        public LyricsJobState State { get; private set; } = LyricsJobState.Running;
        public string Phase { get; private set; } = "Scan";
        public string Status { get; private set; } = "Starting";
        public string? Error { get; private set; }
        public int Done { get; private set; }
        public int Total { get; private set; }
        public List<LyricsTrackRow> Tracks { get; private set; } = [];
        public CancellationTokenSource Cancel { get; private set; } = new();
        private readonly object _gate = new();

        public void Report(string status, int done, int total)
        {
            lock (_gate)
            {
                Status = status;
                Done = done;
                Total = total;
            }
        }

        public void Complete(List<LyricsTrackRow> tracks, string status)
        {
            lock (_gate)
            {
                Tracks = tracks;
                State = LyricsJobState.Completed;
                Status = status;
                Error = null;
                Done = tracks.Count;
                Total = tracks.Count;
            }
        }

        public void BeginApply()
        {
            lock (_gate)
            {
                var previous = Cancel;
                Cancel = new CancellationTokenSource();
                previous.Dispose();
                State = LyricsJobState.Running;
                Phase = "Write";
                Status = "Writing lyrics";
                Error = null;
            }
        }

        public void Fail(string error)
        {
            lock (_gate)
            {
                State = LyricsJobState.Failed;
                Error = error;
                Status = error;
            }
        }

        public void CancelJob()
        {
            lock (_gate)
            {
                State = LyricsJobState.Canceled;
                Status = "Canceled";
            }
        }

        public int Select(IReadOnlyCollection<string> ratingKeys, bool selected)
        {
            var keys = ratingKeys.ToHashSet(StringComparer.Ordinal);
            var changed = 0;
            lock (_gate)
            {
                foreach (var row in Tracks)
                {
                    if (!row.CanSelect || !keys.Contains(row.RatingKey) || row.Selected == selected)
                        continue;
                    row.Selected = selected;
                    changed++;
                }
            }

            return changed;
        }

        public LyricsJobSnapshot Snapshot()
        {
            lock (_gate)
            {
                return new LyricsJobSnapshot(
                    Id,
                    State,
                    Phase,
                    Status,
                    Error,
                    Done,
                    Total,
                    ServerId,
                    ServerName,
                    LibraryName,
                    Tracks.Select(row => row.View()).ToList());
            }
        }
    }
}
