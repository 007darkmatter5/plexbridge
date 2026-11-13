using PlexBridge.Plex;
using PlexBridge.Security;

namespace PlexBridge.Services;

public enum MigrationJobState
{
    Running,
    Completed,
    Failed,
    Canceled
}

public sealed record MigrationJobSnapshot(
    string Id,
    string UserId,
    MigrationJobState State,
    string Status,
    string? Error,
    MigrationPlanSummary? Summary,
    IReadOnlyList<MigrationPhaseView> Phases,
    string Step,
    DateTimeOffset StartedUtc,
    DateTimeOffset UpdatedUtc,
    string SourceName,
    string DestinationName,
    int RowCount,
    string ApplyState = "Idle",
    string ApplyStatus = "",
    int ApplyDone = 0,
    int ApplyTotal = 0,
    IReadOnlyList<MigrationApplyItem>? ApplyItems = null,
    string PlexUserKey = "",
    string PlexUserName = "");

/// <summary>
/// One preview at a time per admin. The work keeps running if the browser circuit disconnects.
/// Cancel comes from the Cancel button, or from the overall deadline.
/// </summary>
public sealed class MigrationPreviewJobs(IServiceScopeFactory scopes, MigrationPreviewOptions options, ILogger<MigrationPreviewJobs> logger)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, RunningJob> _latest = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StoredMigrationPreview> _plans = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<MigrationPlanRow>> _rows = new(StringComparer.Ordinal);

    public MigrationJobSnapshot Start(string userId, int sourceId, int destinationId, IReadOnlyCollection<MigrationCategory> categories, string? plexUserKey = null, string? plexUserName = null)
    {
        CancellationTokenSource userCancel;
        CancellationTokenSource linked;
        RunningJob job;
        var scope = Scope(userId, plexUserKey);
        lock (_gate)
        {
            if (_latest.TryGetValue(scope, out var previous) && previous.State == MigrationJobState.Running)
            {
                logger.LogInformation("Migration preview {PlanId} is already running.", previous.Id);
                return previous.Snapshot();
            }
            userCancel = new CancellationTokenSource();
            var deadline = new CancellationTokenSource(options.Deadline);
            linked = CancellationTokenSource.CreateLinkedTokenSource(userCancel.Token, deadline.Token);
            job = new RunningJob(Guid.NewGuid().ToString("n"), userId, userCancel, deadline, linked)
            {
                PlexUserKey = plexUserKey ?? "",
                PlexUserName = plexUserName ?? ""
            };
            _latest[scope] = job;
        }

        logger.LogInformation("Migration preview {PlanId} started for servers {SourceId} to {DestinationId} for Plex user {PlexUser}.", job.Id, sourceId, destinationId, string.IsNullOrEmpty(job.PlexUserName) ? "saved tokens" : job.PlexUserName);
        _ = Task.Run(() => RunAsync(job, sourceId, destinationId, categories));
        return job.Snapshot();
    }

    public MigrationJobSnapshot? Latest(string userId, string? plexUserKey = null)
    {
        lock (_gate)
            return _latest.TryGetValue(Scope(userId, plexUserKey), out var job) ? job.Snapshot() : null;
    }

    private static string Scope(string userId, string? plexUserKey)
        => string.IsNullOrEmpty(plexUserKey) ? userId : userId + "\n" + plexUserKey;

    private RunningJob? Match(string userId, string planId)
    {
        foreach (var job in _latest.Values)
        {
            if (job.UserId == userId && (job.Id == planId || job.Plan?.Id == planId))
                return job;
        }

        return null;
    }

    public void Cancel(string userId, string jobId)
    {
        lock (_gate)
        {
            if (Match(userId, jobId) is { } job && job.State == MigrationJobState.Running)
                job.UserCancel.Cancel();
        }
    }

    public void Remember(StoredMigrationPreview preview)
    {
        lock (_gate)
            _plans[preview.UserId + "|" + preview.PlanId] = preview;
    }

    public void RememberRows(string userId, string planId, IReadOnlyList<MigrationPlanRow> rows)
    {
        lock (_gate)
            _rows[userId + "|" + planId] = rows;
    }

    public IReadOnlyList<MigrationPlanRow>? Rows(string userId, string planId)
    {
        lock (_gate)
            return _rows.TryGetValue(userId + "|" + planId, out var rows) ? rows : null;
    }

    public StoredMigrationPreview? Find(string userId, string planId)
    {
        lock (_gate)
            return _plans.TryGetValue(userId + "|" + planId, out var preview) ? preview : null;
    }

    public ScanPage? Page(string userId, string planId, string? filter, int skip, int take, string? type = null, string? search = null, string? playlistKey = null)
    {
        lock (_gate)
        {
            if (Match(userId, planId) is not { } job)
                return null;
            return job.Read(filter, type, search, skip, take);
        }
    }

    public IReadOnlyList<LibrarySection>? Libraries(
        string userId,
        string planId,
        string? filter,
        string? search,
        IReadOnlyDictionary<string, int>? skips,
        IReadOnlySet<string>? expanded,
        IReadOnlySet<string>? collapsed)
    {
        lock (_gate)
        {
            if (Match(userId, planId) is not { } job)
                return null;
            return job.ReadLibraries(filter, search, skips, expanded, collapsed);
        }
    }

    public IReadOnlyList<string> Selection(string userId, string planId)
    {
        lock (_gate)
            return Match(userId, planId)?.Selected() ?? [];
    }

    public int Select(string userId, string planId, IReadOnlyCollection<string> keys, bool include)
    {
        lock (_gate)
        {
            if (Match(userId, planId) is not { } job)
                return 0;
            return job.Select(keys, include);
        }
    }

    public int SelectMatching(string userId, string planId, string? filter, string? search, bool include)
    {
        lock (_gate)
        {
            if (Match(userId, planId) is not { } job)
                return 0;
            return job.SelectMatching(filter, search, include);
        }
    }

    public bool StartApply(string userId, string planId, IReadOnlyCollection<string> keys)
    {
        RunningJob job;
        lock (_gate)
        {
            if (Match(userId, planId) is not { } found)
                return false;
            job = found;
            if (!job.BeginApply(keys))
                return false;
        }

        var selected = keys.ToArray();
        _ = Task.Run(() => RunApplyAsync(job, selected));
        return true;
    }

    public void NoteVerified(string userId, string planId, string actionKey, bool verified)
    {
        lock (_gate)
        {
            if (Match(userId, planId) is { } job)
                job.Update(board => board.NoteVerified(actionKey, verified));
        }
    }

    public void NoteSettled(string userId, string planId, string actionKey)
        => NoteApplied(userId, planId, new AppliedObservation(actionKey, true, null, null, null, null, null));

    public void NoteApplied(string userId, string planId, AppliedObservation update)
    {
        lock (_gate)
        {
            if (Match(userId, planId) is { } job)
                job.Update(board => board.NoteApplied(update));
        }
    }

    public void Edit(string userId, string planId, Action<LiveScanBoard> edit)
    {
        lock (_gate)
        {
            if (Match(userId, planId) is not { } job || job.Id != planId || job.State != MigrationJobState.Running)
                return;
            job.Update(edit);
        }
    }

    private async Task RunApplyAsync(RunningJob job, IReadOnlyCollection<string> keys)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            // This scope has no Blazor circuit. ApplyForUserAsync uses the user id captured on the page.
            // It must not resolve IAdminGate or AuthenticationStateProvider.
            var migration = scope.ServiceProvider.GetRequiredService<MigrationService>();
            var progress = new InlineApplyProgress(update => job.ReportApply(update));
            var result = await migration.ApplyForUserAsync(job.UserId, job.Id, keys, CancellationToken.None, progress: progress);
            job.FinishApply(result);
            logger.LogInformation("Migration apply {PlanId} finished. Applied {AppliedCount}, failed {FailedCount}.", job.Id, result.Applied, result.Failed);
        }
        catch (Exception exception)
        {
            var reason = exception is PlexBridgeException bridge ? bridge.Message : exception.Message;
            var items = job.Snapshot().ApplyItems ?? [];
            logger.LogError("Migration apply {PlanId} failed. {Message}\n{Stack}", job.Id, reason, exception.StackTrace);
            job.FailApply(MigrationApplyText.Fatal(reason, items));
        }
    }

    private async Task RunAsync(RunningJob job, int sourceId, int destinationId, IReadOnlyCollection<MigrationCategory> categories)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            // BuildPreviewAsync already has the user id. This scope must not touch the circuit.
            var migration = scope.ServiceProvider.GetRequiredService<MigrationService>();
            var progress = new InlineProgress(job.Report);
            var plan = await migration.BuildPreviewAsync(job.UserId, sourceId, destinationId, categories, progress, job.Linked.Token, job.Id, job.PlexUserKey, job.PlexUserName);
            job.Complete(plan);
            logger.LogInformation("Migration preview {PlanId} completed. {RowCount} detail rows, {AlreadySame} already the same.", job.Id, plan.Rows.Count, plan.Summary.AlreadySame);
        }
        catch (Exception) when (job.UserCancel.IsCancellationRequested)
        {
            job.Mark(MigrationJobState.Canceled, "Canceled. Nothing was written.", null);
            logger.LogInformation("Migration preview {PlanId} was canceled.", job.Id);
        }
        catch (Exception) when (job.Deadline.IsCancellationRequested)
        {
            var message = "The preview took longer than " + (int)options.Deadline.TotalMinutes + " minutes. Nothing was written.";
            job.Mark(MigrationJobState.Failed, message, null);
            logger.LogWarning("Migration preview {PlanId} hit the deadline.", job.Id);
        }
        catch (Exception exception)
        {
            var message = exception is PlexBridgeException bridge
                ? bridge.Message
                : "The preview failed. Nothing was written.";
            job.Mark(MigrationJobState.Failed, message, null);
            logger.LogError("Migration preview {PlanId} failed.\n{Detail}", job.Id, SecretRedactor.ForLog(exception.ToString()));
        }
    }

    private sealed class RunningJob(string id, string userId, CancellationTokenSource userCancel, CancellationTokenSource deadline, CancellationTokenSource linked)
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, PhaseSlot> _phases = new(StringComparer.Ordinal);
        private readonly HashSet<string> _selected = new(StringComparer.Ordinal);
        private readonly List<MigrationApplyItem> _applyItems = [];
        private string _applyState = "Idle";
        private string _applyStatus = "";
        private int _applyDone;
        private int _applyTotal;
        private string _sourceName = "";
        private string _destinationName = "";
        public LiveScanBoard Board { get; } = new();
        private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;
        private DateTimeOffset _updated = DateTimeOffset.UtcNow;
        private string _status = "Starting. Nothing is written.";
        private string _step = "Reading";
        public string Id { get; } = id;
        public string UserId { get; } = userId;
        public CancellationTokenSource UserCancel { get; } = userCancel;
        public CancellationTokenSource Deadline { get; } = deadline;
        public CancellationTokenSource Linked { get; } = linked;
        public MigrationJobState State { get; private set; } = MigrationJobState.Running;
        public MigrationPlan? Plan { get; private set; }
        public string? Error { get; private set; }
        public string PlexUserKey { get; set; } = "";
        public string PlexUserName { get; set; } = "";

        public string[] Selected()
        {
            lock (_gate)
                return _selected.ToArray();
        }

        public int Select(IReadOnlyCollection<string> keys, bool include)
        {
            lock (_gate)
            {
                foreach (var key in keys)
                {
                    if (include)
                        _selected.Add(key);
                    else
                        _selected.Remove(key);
                }

                return _selected.Count;
            }
        }

        public int SelectMatching(string? filter, string? search, bool include)
        {
            lock (_gate)
            {
                foreach (var key in Board.ChangeKeys(null, filter, search))
                {
                    if (include)
                        _selected.Add(key);
                    else
                        _selected.Remove(key);
                }

                return _selected.Count;
            }
        }

        public void Update(Action<LiveScanBoard> edit)
        {
            lock (_gate)
            {
                edit(Board);
                _updated = DateTimeOffset.UtcNow;
            }
        }

        public ScanPage Read(string? filter, string? type, string? search, int skip, int take)
        {
            lock (_gate)
                return Board.Page(filter, type, search, skip, take);
        }

        public IReadOnlyList<LibrarySection> ReadLibraries(string? filter, string? search, IReadOnlyDictionary<string, int>? skips, IReadOnlySet<string>? expanded, IReadOnlySet<string>? collapsed)
        {
            lock (_gate)
                return Board.Libraries(filter, search, skips, expanded, collapsed);
        }

        public void Report(MigrationReadProgress update)
        {
            lock (_gate)
            {
                if (State != MigrationJobState.Running)
                    return;
                _updated = DateTimeOffset.UtcNow;
                _step = update.Phase is "Matching" or "Building plan" ? update.Library : "Reading";
                var key = update.Role + "|" + update.Phase + "|" + update.Library;
                if (!_phases.TryGetValue(key, out var slot))
                {
                    slot = new PhaseSlot(update.Role, update.Phase, update.Library, _updated);
                    _phases[key] = slot;
                }

                slot.State = update.State;
                slot.Page = update.Page;
                slot.Items = update.ItemsRead;
                slot.Updated = _updated;
                if (update.Role == "Destination")
                    _destinationName = update.ServerName;
                else if (update.Role == "Source")
                    _sourceName = update.ServerName;
                Board.ApplyProgress(update);
                _status = $"{update.Role} · {update.Phase} · {update.Library} · page {update.Page} · {update.ItemsRead} items";
            }
        }

        public bool BeginApply(IReadOnlyCollection<string> keys)
        {
            lock (_gate)
            {
                if (State != MigrationJobState.Completed || _applyState == "Running" || keys.Count == 0)
                    return false;
                _applyState = "Running";
                _applyStatus = "Checking selected items.";
                _applyDone = 0;
                _applyTotal = keys.Count;
                _applyItems.Clear();
                _updated = DateTimeOffset.UtcNow;
                return true;
            }
        }

        public void ReportApply(MigrationApplyProgress update)
        {
            lock (_gate)
            {
                _applyStatus = update.Status;
                _applyDone = update.Index;
                _applyTotal = update.Total;
                if (update.Item is not null)
                    _applyItems.Add(update.Item);
                _updated = DateTimeOffset.UtcNow;
            }
        }

        public void FinishApply(MigrationApplyResult result)
        {
            lock (_gate)
            {
                _applyState = "Completed";
                _applyStatus = MigrationApplyText.Summary(result);
                _applyItems.Clear();
                _applyItems.AddRange(result.Items);
                _updated = DateTimeOffset.UtcNow;
            }
        }

        public void FailApply(string message)
        {
            lock (_gate)
            {
                _applyState = "Failed";
                _applyStatus = message;
                _updated = DateTimeOffset.UtcNow;
            }
        }

        public void Complete(MigrationPlan plan)
        {
            lock (_gate)
            {
                State = MigrationJobState.Completed;
                Plan = plan;
                Board.ReleaseItems();
                Error = null;
                _step = "Ready";
                _status = "Preview ready. Nothing has been written.";
                _updated = DateTimeOffset.UtcNow;
            }
        }

        public void Mark(MigrationJobState state, string message, MigrationPlan? plan)
        {
            lock (_gate)
            {
                State = state;
                Error = message;
                Plan = plan;
                _status = message;
                _step = state.ToString();
                _updated = DateTimeOffset.UtcNow;
            }
        }

        public MigrationJobSnapshot Snapshot()
        {
            lock (_gate)
            {
                var phases = _phases.Values
                    .OrderBy(phase => phase.Role, StringComparer.Ordinal)
                    .ThenBy(phase => phase.Phase, StringComparer.Ordinal)
                    .Select(phase => new MigrationPhaseView(
                        phase.Role,
                        phase.Phase,
                        phase.Library,
                        phase.State,
                        phase.Page,
                        phase.Items,
                        Math.Max(0, (phase.Updated - phase.Started).TotalSeconds)))
                    .ToList();
                var summary = Plan?.Summary ?? Board.ToSummary();
                var rowCount = Board.LineCount();
                var sourceName = string.IsNullOrEmpty(Plan?.SourceName) ? _sourceName : Plan.SourceName;
                var destinationName = string.IsNullOrEmpty(Plan?.DestinationName) ? _destinationName : Plan.DestinationName;
                return new MigrationJobSnapshot(Id, UserId, State, _status, Error, summary, phases, _step, _started, _updated, sourceName, destinationName, rowCount, _applyState, _applyStatus, _applyDone, _applyTotal, _applyItems.ToList(), PlexUserKey, PlexUserName);
            }
        }

        private sealed class PhaseSlot(string role, string phase, string library, DateTimeOffset started)
        {
            public string Role { get; } = role;
            public string Phase { get; } = phase;
            public string Library { get; } = library;
            public string State { get; set; } = "Waiting";
            public int Page { get; set; }
            public int Items { get; set; }
            public DateTimeOffset Started { get; } = started;
            public DateTimeOffset Updated { get; set; } = started;
        }
    }
}

file sealed class InlineProgress(Action<MigrationReadProgress> report) : IProgress<MigrationReadProgress>
{
    public void Report(MigrationReadProgress value) => report(value);
}

file sealed class InlineApplyProgress(Action<MigrationApplyProgress> report) : IProgress<MigrationApplyProgress>
{
    public void Report(MigrationApplyProgress value) => report(value);
}

public sealed record StoredMigrationPreview(
    string PlanId,
    string UserId,
    int SourceId,
    int DestinationId,
    IReadOnlySet<MigrationCategory> Categories,
    string Fingerprint,
    DateTimeOffset CreatedUtc,
    string PlexUserKey = "",
    string PlexUserName = "");
