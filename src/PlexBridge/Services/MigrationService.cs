using Microsoft.EntityFrameworkCore;
using PlexBridge.Data;
using PlexBridge.Plex;
using PlexBridge.Security;

namespace PlexBridge.Services;

/// <summary>
/// One-way copy from a saved server to the other. Preview only reads. Apply writes the confirmed rows and nothing else.
/// Saved URLs, encrypted tokens, and slots are not modified.
/// </summary>
public sealed class MigrationService(
    IServiceProvider services,
    IDbContextFactory<ApplicationDbContext> databases,
    ISecretProtector protector,
    PlexReadClient identity,
    PlexMigrationReader reader,
    PlexWriteClient writer,
    MigrationPreviewJobs jobs,
    MigrationUserTokens userTokens,
    TimeProvider clock,
    ILogger<MigrationService> logger)
{
    // The admin gate reads AuthenticationStateProvider, which only works on the Blazor circuit.
    // Background preview and apply call the methods that already have a user id, and never this gate.
    private IAdminGate Admin => services.GetRequiredService<IAdminGate>();
    private HeldPlan? _plan;
    public static readonly TimeSpan PlanLifetime = TimeSpan.FromMinutes(30);

    public async Task<MigrationPlan> PreviewAsync(int sourceId, int destinationId, IReadOnlyCollection<MigrationCategory> categories, CancellationToken cancellationToken = default)
    {
        var userId = await Admin.RequireUserIdAsync(cancellationToken);
        var built = await BuildPreviewAsync(userId, sourceId, destinationId, categories, progress: null, cancellationToken, planId: null);
        _plan = HeldFrom(userId, built);
        return built;
    }

    public async Task<MigrationJobSnapshot> StartPreviewAsync(int sourceId, int destinationId, IReadOnlyCollection<MigrationCategory> categories, CancellationToken cancellationToken = default, string? plexUserKey = null, string? plexUserName = null)
    {
        var userId = await Admin.RequireUserIdAsync(cancellationToken);
        var selected = Normalize(categories);
        if (sourceId == destinationId)
            throw new PlexBridgeException("Choose two different servers. The source is copied onto the destination.");
        logger.LogInformation("Queued migration preview from server {SourceId} to server {DestinationId} for {PlexUser}.", sourceId, destinationId, string.IsNullOrEmpty(plexUserName) ? "saved tokens" : plexUserName);
        return jobs.Start(userId, sourceId, destinationId, selected, plexUserKey, plexUserName);
    }

    public async Task<MigrationJobSnapshot?> LatestPreviewAsync(CancellationToken cancellationToken = default, string? plexUserKey = null)
    {
        var userId = await Admin.RequireUserIdAsync(cancellationToken);
        return jobs.Latest(userId, plexUserKey);
    }

    public async Task CancelPreviewAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var userId = await Admin.RequireUserIdAsync(cancellationToken);
        jobs.Cancel(userId, jobId);
        logger.LogInformation("Migration preview {PlanId} cancel requested.", jobId);
    }

    internal async Task<MigrationPlan> BuildPreviewAsync(
        string userId,
        int sourceId,
        int destinationId,
        IReadOnlyCollection<MigrationCategory> categories,
        IProgress<MigrationReadProgress>? progress,
        CancellationToken cancellationToken,
        string? planId,
        string? plexUserKey = null,
        string? plexUserName = null)
    {
        var selected = Normalize(categories);
        if (sourceId == destinationId)
            throw new PlexBridgeException("Choose two different servers. The source is copied onto the destination.");

        var (source, sourceToken, destination, destinationToken) = await LoadPairAsync(userId, sourceId, destinationId, plexUserKey, cancellationToken);
        logger.LogInformation("Migration preview reading servers {SourceId} and {DestinationId}.", sourceId, destinationId);
        var built = await ReadPlanAsync(source, sourceToken, destination, destinationToken, selected, progress, cancellationToken, userId, planId);
        if (!string.IsNullOrEmpty(planId))
            built = built with { Id = planId };
        jobs.Remember(new StoredMigrationPreview(
            built.Id,
            userId,
            sourceId,
            destinationId,
            selected,
            built.Fingerprint,
            clock.GetUtcNow(),
            plexUserKey ?? "",
            plexUserName ?? ""));
        jobs.RememberRows(userId, built.Id, built.Rows);
        logger.LogInformation("Migration preview {PlanId} built from server {SourceId} to server {DestinationId}.", built.Id, sourceId, destinationId);
        return built;
    }

    public async Task<IReadOnlyList<string>> SelectionAsync(string planId, CancellationToken cancellationToken = default)
    {
        var userId = await Admin.RequireUserIdAsync(cancellationToken);
        return jobs.Selection(userId, planId);
    }

    public async Task<int> SelectAsync(string planId, IReadOnlyCollection<string> keys, bool include, CancellationToken cancellationToken = default)
    {
        var userId = await Admin.RequireUserIdAsync(cancellationToken);
        return jobs.Select(userId, planId, keys, include);
    }

    public async Task<int> SelectMatchingAsync(string planId, string? filter, string? search, bool include, CancellationToken cancellationToken = default)
    {
        var userId = await Admin.RequireUserIdAsync(cancellationToken);
        return jobs.SelectMatching(userId, planId, filter, search, include);
    }

    public async Task<IReadOnlyList<LibrarySection>?> PreviewLibrariesAsync(
        string planId,
        string? filter,
        string? search,
        IReadOnlyDictionary<string, int>? skips,
        IReadOnlySet<string>? expanded,
        IReadOnlySet<string>? collapsed,
        CancellationToken cancellationToken = default)
    {
        var userId = await Admin.RequireUserIdAsync(cancellationToken);
        return jobs.Libraries(userId, planId, filter, search, skips, expanded, collapsed);
    }

    public Task<ScanPage?> PreviewPageAsync(string planId, string? filter, int skip, CancellationToken cancellationToken = default, string? type = null, string? search = null, string? playlistKey = null)
        => PreviewPageCore(planId, filter, skip, type, search, playlistKey, cancellationToken);

    private async Task<ScanPage?> PreviewPageCore(string planId, string? filter, int skip, string? type, string? search, string? playlistKey, CancellationToken cancellationToken)
    {
        var userId = await Admin.RequireUserIdAsync(cancellationToken);
        return jobs.Page(userId, planId, filter, skip, MigrationPlan.PageSize, type, search, playlistKey);
    }

    public async Task StartApplyAsync(string planId, IReadOnlyCollection<string> includedKeys, CancellationToken cancellationToken = default)
    {
        var userId = await Admin.RequireUserIdAsync(cancellationToken);
        if (includedKeys.Count == 0)
            throw new PlexBridgeException("Choose at least one change before applying.");
        if (!jobs.StartApply(userId, planId, includedKeys))
            throw new PlexBridgeException("That preview is not ready to apply. Preview again.");
    }

    public async Task<MigrationApplyResult> ApplyAsync(string planId, IReadOnlyCollection<string>? includedKeys, CancellationToken cancellationToken = default, IReadOnlyCollection<string>? excludedKeys = null, IProgress<MigrationApplyProgress>? progress = null)
    {
        var userId = await Admin.RequireUserIdAsync(cancellationToken);
        return await ApplyForUserAsync(userId, planId, includedKeys, cancellationToken, excludedKeys, progress);
    }

    internal async Task<MigrationApplyResult> ApplyForUserAsync(string userId, string planId, IReadOnlyCollection<string>? includedKeys, CancellationToken cancellationToken = default, IReadOnlyCollection<string>? excludedKeys = null, IProgress<MigrationApplyProgress>? progress = null)
    {
        string? sourceToken = null;
        string? destinationToken = null;
        HeldPlan? held = null;
        var results = new List<MigrationApplyItem>();
        try
        {
            return await ApplyStoredAsync(userId, planId, includedKeys, excludedKeys, progress, results, heldPlan => held = heldPlan, (source, destination) =>
            {
                sourceToken = source;
                destinationToken = destination;
            }, cancellationToken);
        }
        catch (Exception exception)
        {
            var reason = exception is PlexBridgeException bridge
                ? SecretRedactor.ForDisplay(bridge.Message, sourceToken, 500)
                : SecretRedactor.ForDisplay(exception.Message, sourceToken, 500);
            reason = SecretRedactor.ForDisplay(reason, destinationToken, 500);
            logger.LogError(
                "Migration apply {PlanId} failed.\n{Detail}",
                planId,
                SecretRedactor.ForLog(exception.ToString(), sourceToken, destinationToken));
            try
            {
                await RecordFatalApplyAsync(userId, held, reason, results, cancellationToken);
            }
            catch (Exception auditFailure)
            {
                logger.LogError(
                    "Migration apply {PlanId} could not save the failure audit.\n{Detail}",
                    planId,
                    SecretRedactor.ForLog(auditFailure.ToString(), sourceToken, destinationToken));
            }

            if (exception is PlexBridgeException)
                throw;
            throw new PlexBridgeException(reason, exception);
        }
    }

    private async Task<MigrationApplyResult> ApplyStoredAsync(
        string userId,
        string planId,
        IReadOnlyCollection<string>? includedKeys,
        IReadOnlyCollection<string>? excludedKeys,
        IProgress<MigrationApplyProgress>? progress,
        List<MigrationApplyItem> results,
        Action<HeldPlan> rememberHeld,
        Action<string, string> rememberTokens,
        CancellationToken cancellationToken)
    {
        HeldPlan? held = _plan;
        if (held is null || !string.Equals(held.Id, planId, StringComparison.Ordinal) || !string.Equals(held.UserId, userId, StringComparison.Ordinal))
        {
            var stored = jobs.Find(userId, planId)
                ?? throw new PlexBridgeException("That preview is no longer available. Preview again.");
            held = new HeldPlan(stored.PlanId, stored.UserId, stored.SourceId, stored.DestinationId, stored.Categories, stored.Fingerprint, stored.CreatedUtc, stored.PlexUserKey, stored.PlexUserName);
        }

        rememberHeld(held);
        if (clock.GetUtcNow() - held.CreatedUtc > PlanLifetime)
            throw new PlexBridgeException("That preview expired. Preview again. Nothing was written.");

        var (source, sourceToken, destination, destinationToken) = await LoadPairAsync(userId, held.SourceId, held.DestinationId, held.PlexUserKey, cancellationToken);
        rememberTokens(sourceToken, destinationToken);
        var storedRows = jobs.Rows(userId, planId) ?? throw new PlexBridgeException("That preview is no longer available. Preview again.");
        var include = includedKeys?.ToHashSet(StringComparer.Ordinal);
        var exclude = excludedKeys?.ToHashSet(StringComparer.Ordinal) ?? [];
        var selected = storedRows
            .Where(row => row.Kind is MigrationRowKind.WillApply or MigrationRowKind.Conflict)
            .Where(row => include is null || include.Contains(row.Key))
            .Where(row => !exclude.Contains(row.Key))
            .ToList();
        progress?.Report(new MigrationApplyProgress("Checking selected items.", 0, selected.Count, null));
        var check = 0;
        foreach (var row in selected)
        {
            check++;
            progress?.Report(new MigrationApplyProgress($"Checking selected items. {check} of {selected.Count}: {row.Title}", check, selected.Count, null));
            if (!await SelectedItemStillMatchesAsync(source, sourceToken, destination, destinationToken, row, cancellationToken))
                throw new PlexBridgeException("A selected item changed. Nothing was written.");
        }
        var createdPlaylists = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var settledKeys = new List<string>();
        string? machineId = null;
        var canceled = false;
        var index = 0;

        foreach (var original in selected)
        {
            var row = original;
            index++;
            var label = ChangeLabel(row);
            if (cancellationToken.IsCancellationRequested)
            {
                canceled = true;
                var skippedItem = new MigrationApplyItem(row.Title, row.Action, "Skipped", "Canceled before this item.");
                results.Add(skippedItem);
                progress?.Report(new MigrationApplyProgress("Canceled.", index, selected.Count, skippedItem));
                continue;
            }

            try
            {
                if (row.Action is "playlist-create" or "playlist-add")
                {
                    machineId ??= await MachineIdAsync(destination, destinationToken, cancellationToken);
                    var existingPlaylist = await FindDestinationPlaylistAsync(destination, destinationToken, row, createdPlaylists, cancellationToken);
                    if (existingPlaylist is not null)
                    {
                        createdPlaylists[PlaylistName(row)] = existingPlaylist;
                        if (await PlaylistContainsAsync(destination, destinationToken, existingPlaylist, row.TargetRatingKey, cancellationToken))
                        {
                            var already = new MigrationApplyItem(row.Title, row.Action, "Skipped", "Already on the destination.");
                            results.Add(already);
                            settledKeys.Add(row.Key);
                            var present = await reader.ReadPlaylistItemsAsync(PlexServerUrl.ToUri(destination.BaseUrl), destinationToken, existingPlaylist, cancellationToken);
                            jobs.NoteApplied(userId, planId, PlaylistObservation(row, true, present.Count));
                            progress?.Report(new MigrationApplyProgress("Already on the destination: " + label, index, selected.Count, already));
                            continue;
                        }

                        if (row.Action == "playlist-create")
                            row = row with { Action = "playlist-add", PlaylistRatingKey = existingPlaylist };
                    }
                }
            }
            catch (PlexRequestException exception)
            {
                var detail = SecretRedactor.ForDisplay(exception.Message, destinationToken);
                var failedItem = new MigrationApplyItem(row.Title, row.Action, "Failed", MigrationApplyText.ItemFailure(row, detail));
                results.Add(failedItem);
                progress?.Report(new MigrationApplyProgress(failedItem.Detail ?? failedItem.Title, index, selected.Count, failedItem));
                continue;
            }

            progress?.Report(new MigrationApplyProgress($"Writing {index} of {selected.Count}: {label}", index, selected.Count, null));
            logger.LogInformation("Migration apply writing {Action} for {Title}.", row.Action, row.Title);
            try
            {
                if (row.Action is "playlist-create" or "playlist-add")
                    machineId ??= await MachineIdAsync(destination, destinationToken, cancellationToken);
                await WriteRowAsync(destination, destinationToken, machineId, row, createdPlaylists, cancellationToken);
                progress?.Report(new MigrationApplyProgress($"Verifying {index} of {selected.Count}: {label}", index, selected.Count, null));
                var verdict = await VerifyWrittenAsync(destination, destinationToken, row, createdPlaylists, cancellationToken);
                if (verdict.Observed is not null)
                    jobs.NoteApplied(userId, planId, verdict.Observed);
                else if (verdict.Name is "Applied" or "Verified")
                    jobs.NoteSettled(userId, planId, row.Key);
                else if (verdict.Name == "Not reflected")
                    jobs.NoteVerified(userId, planId, row.Key, false);
                if (verdict.Name is "Applied" or "Verified" or "Skipped")
                    settledKeys.Add(row.Key);
                var item = new MigrationApplyItem(row.Title, row.Action, verdict.Name, verdict.Name == "Not reflected" ? "The destination still shows a different value." : null);
                results.Add(item);
                logger.LogInformation("Migration apply {Outcome} for {Title}.", verdict.Name, row.Title);
                progress?.Report(new MigrationApplyProgress($"{verdict.Name} {index} of {selected.Count}: {label}", index, selected.Count, item));
            }
            catch (OperationCanceledException)
            {
                canceled = true;
                results.Add(new MigrationApplyItem(row.Title, row.Action, "Skipped", "Canceled."));
            }
            catch (PlexRequestException) when (cancellationToken.IsCancellationRequested)
            {
                canceled = true;
                results.Add(new MigrationApplyItem(row.Title, row.Action, "Skipped", "Canceled."));
            }
            catch (PlexRequestException exception)
            {
                var detail = SecretRedactor.ForDisplay(exception.Message, destinationToken);
                detail = SecretRedactor.ForDisplay(detail, sourceToken);
                var failedItem = new MigrationApplyItem(row.Title, row.Action, "Failed", MigrationApplyText.ItemFailure(row, detail));
                results.Add(failedItem);
                progress?.Report(new MigrationApplyProgress(failedItem.Detail ?? failedItem.Title, index, selected.Count, failedItem));
            }
            catch (PlexBridgeException exception)
            {
                var detail = SecretRedactor.ForDisplay(exception.Message, destinationToken);
                var failedItem = new MigrationApplyItem(row.Title, row.Action, "Failed", MigrationApplyText.ItemFailure(row, detail));
                results.Add(failedItem);
                progress?.Report(new MigrationApplyProgress(failedItem.Detail ?? failedItem.Title, index, selected.Count, failedItem));
            }
        }

        if (settledKeys.Count > 0)
            jobs.Select(userId, planId, settledKeys, false);
        var applied = results.Count(item => item.Outcome is "Applied" or "Verified");
        var failed = results.Count(item => item.Outcome == "Failed");
        var skipped = results.Count(item => item.Outcome == "Skipped");
        var outcome = canceled ? "Canceled" : failed > 0 ? "Completed with failures" : "Completed";
        await SaveAuditAsync(userId, held, outcome, applied, failed, skipped, results, cancellationToken);
        _plan = null;
        var finished = MigrationApplyText.Summary(new MigrationApplyResult(applied, failed, skipped, canceled, results));
        progress?.Report(new MigrationApplyProgress(finished, selected.Count, selected.Count, null));
        logger.LogInformation("Migration apply finished with outcome {Outcome}. Applied {AppliedCount}, failed {FailedCount}, skipped {SkippedCount}.", outcome, applied, failed, skipped);
        return new MigrationApplyResult(applied, failed, skipped, canceled, results);
    }

    private async Task<bool> SelectedItemStillMatchesAsync(
        PlexServerConnection source,
        string sourceToken,
        PlexServerConnection destination,
        string destinationToken,
        MigrationPlanRow row,
        CancellationToken cancellationToken)
    {
        if (!TryItemKeys(row, out var sourceKey, out var destinationKey))
            return true;
        var sourceItem = await reader.ReadItemAsync(PlexServerUrl.ToUri(source.BaseUrl), sourceToken, sourceKey, cancellationToken);
        await reader.ReadItemAsync(PlexServerUrl.ToUri(destination.BaseUrl), destinationToken, destinationKey, cancellationToken);
        if (sourceItem is null)
            return true;
        return row.Action switch
        {
            "scrobble" => sourceItem.Watched,
            "unscrobble" => !sourceItem.Watched,
            "progress" => Math.Abs(sourceItem.ViewOffset - (row.ProgressMs ?? 0)) <= 2_000,
            "rate" => RatingsMatch(sourceItem.UserRating, row.Rating),
            _ => true
        };
    }

    private static bool TryItemKeys(MigrationPlanRow row, out string sourceKey, out string destinationKey)
    {
        sourceKey = "";
        destinationKey = "";
        var parts = row.Key.Split('|');
        if (parts.Length != 3 || parts[0] is not ("watched" or "progress" or "rating"))
            return false;
        sourceKey = parts[1];
        destinationKey = parts[2];
        return sourceKey.Length > 0 && destinationKey.Length > 0 && sourceKey.All(char.IsDigit) && destinationKey.All(char.IsDigit);
    }

    private static string ChangeLabel(MigrationPlanRow row) => row.Action switch
    {
        "scrobble" => row.Title + " — watched",
        "unscrobble" => row.Title + " — not watched",
        "progress" => row.Title + " — progress " + MigrationPlanBuilder.Clock(row.ProgressMs ?? 0),
        "rate" => row.Title + " — rating " + (row.Rating is null ? "none" : row.Rating.Value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)),
        _ => row.Title
    };

    private readonly record struct WriteCheck(string Name, AppliedObservation? Observed);

    private async Task<WriteCheck> VerifyWrittenAsync(
        PlexServerConnection destination,
        string token,
        MigrationPlanRow row,
        Dictionary<string, string> createdPlaylists,
        CancellationToken cancellationToken)
    {
        if (row.Action is "playlist-create" or "playlist-add")
            return await VerifyPlaylistAsync(destination, token, row, createdPlaylists, cancellationToken);
        if (string.IsNullOrEmpty(row.TargetRatingKey))
            return new WriteCheck("Applied", null);
        try
        {
            var item = await reader.ReadItemAsync(PlexServerUrl.ToUri(destination.BaseUrl), token, row.TargetRatingKey, cancellationToken);
            if (item is null)
                return new WriteCheck("Applied", null);
            var reflected = row.Action switch
            {
                "scrobble" => item.Watched,
                "unscrobble" => !item.Watched,
                "progress" => Math.Abs(item.ViewOffset - (row.ProgressMs ?? 0)) <= 2_000,
                "rate" => RatingsMatch(item.UserRating, row.Rating),
                _ => true
            };
            return new WriteCheck(
                reflected ? "Verified" : "Not reflected",
                new AppliedObservation(row.Key, reflected, null, null, item.Watched, item.ViewOffset, item.UserRating));
        }
        catch (PlexRequestException)
        {
            return new WriteCheck("Applied", null);
        }
    }

    private async Task<string?> FindDestinationPlaylistAsync(
        PlexServerConnection destination,
        string token,
        MigrationPlanRow row,
        Dictionary<string, string> createdPlaylists,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(row.PlaylistRatingKey))
            return row.PlaylistRatingKey;
        if (createdPlaylists.TryGetValue(PlaylistName(row), out var created))
            return created;
        var playlists = await reader.ListPlaylistsAsync(PlexServerUrl.ToUri(destination.BaseUrl), token, cancellationToken);
        var match = playlists.FirstOrDefault(playlist =>
            !playlist.Smart
            && string.Equals(playlist.Title, row.PlaylistTitle, StringComparison.OrdinalIgnoreCase)
            && string.Equals(playlist.PlaylistType, row.PlaylistType, StringComparison.OrdinalIgnoreCase));
        return match?.RatingKey;
    }

    private async Task<bool> PlaylistContainsAsync(
        PlexServerConnection destination,
        string token,
        string playlistKey,
        string? ratingKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(ratingKey))
            return false;
        var items = await reader.ReadPlaylistItemsAsync(PlexServerUrl.ToUri(destination.BaseUrl), token, playlistKey, cancellationToken);
        return items.Any(item => item.RatingKey == ratingKey);
    }

    private async Task<WriteCheck> VerifyPlaylistAsync(
        PlexServerConnection destination,
        string token,
        MigrationPlanRow row,
        Dictionary<string, string> createdPlaylists,
        CancellationToken cancellationToken)
    {
        var playlistKey = row.PlaylistRatingKey;
        if (string.IsNullOrEmpty(playlistKey))
            createdPlaylists.TryGetValue(PlaylistName(row), out playlistKey);
        if (string.IsNullOrEmpty(playlistKey) || string.IsNullOrEmpty(row.TargetRatingKey))
            return new WriteCheck("Applied", null);
        try
        {
            var items = await reader.ReadPlaylistItemsAsync(PlexServerUrl.ToUri(destination.BaseUrl), token, playlistKey, cancellationToken);
            var reflected = items.Any(item => item.RatingKey == row.TargetRatingKey);
            return new WriteCheck(reflected ? "Verified" : "Not reflected", PlaylistObservation(row, reflected, items.Count));
        }
        catch (PlexRequestException)
        {
            return new WriteCheck("Applied", null);
        }
    }

    private static AppliedObservation PlaylistObservation(MigrationPlanRow row, bool reflected, int count)
    {
        var type = string.IsNullOrEmpty(row.PlaylistType) ? "video" : row.PlaylistType;
        var title = string.IsNullOrEmpty(row.PlaylistTitle) ? "Playlist" : row.PlaylistTitle;
        var detail = type + " playlist · " + count.ToString(System.Globalization.CultureInfo.InvariantCulture) + (count == 1 ? " item" : " items");
        return new AppliedObservation(row.Key, reflected, title, detail, null, null, null);
    }

    private static bool RatingsMatch(decimal? actual, decimal? expected)
    {
        if (actual is null && expected is null)
            return true;
        if (actual is null || expected is null)
            return false;
        return Math.Abs(actual.Value - expected.Value) < 0.05m;
    }

    private async Task WriteRowAsync(
        PlexServerConnection destination,
        string token,
        string? machineId,
        MigrationPlanRow row,
        Dictionary<string, string> createdPlaylists,
        CancellationToken cancellationToken)
    {
        var uri = PlexServerUrl.ToUri(destination.BaseUrl);
        switch (row.Action)
        {
            case "scrobble":
                await writer.MarkWatchedAsync(uri, token, RequiredKey(row.TargetRatingKey), true, cancellationToken);
                return;
            case "unscrobble":
                await writer.MarkWatchedAsync(uri, token, RequiredKey(row.TargetRatingKey), false, cancellationToken);
                return;
            case "progress":
                await writer.SetProgressAsync(uri, token, RequiredKey(row.TargetRatingKey), row.ProgressMs ?? 0, cancellationToken);
                return;
            case "rate":
                await writer.SetRatingAsync(uri, token, RequiredKey(row.TargetRatingKey), row.Rating, cancellationToken);
                return;
            case "playlist-create":
                if (string.IsNullOrEmpty(machineId) || string.IsNullOrEmpty(row.PlaylistTitle) || string.IsNullOrEmpty(row.PlaylistType))
                    throw new PlexRequestException("The playlist could not be created.");
                var created = await writer.CreatePlaylistAsync(uri, token, machineId, row.PlaylistType, row.PlaylistTitle, RequiredKey(row.TargetRatingKey), cancellationToken);
                createdPlaylists[PlaylistName(row)] = created;
                return;
            case "playlist-add":
                var playlistKey = row.PlaylistRatingKey;
                if (string.IsNullOrEmpty(playlistKey))
                    createdPlaylists.TryGetValue(PlaylistName(row), out playlistKey);
                if (string.IsNullOrEmpty(playlistKey))
                    throw new PlexRequestException("The playlist was not created, so this item was not added.");
                if (string.IsNullOrEmpty(machineId))
                    throw new PlexRequestException("The destination did not report a machine identifier.");
                await writer.AddPlaylistItemAsync(uri, token, playlistKey, machineId, RequiredKey(row.TargetRatingKey), cancellationToken);
                return;
            default:
                throw new PlexRequestException("That migration action is not available.");
        }
    }

    private async Task<MigrationPlan> ReadPlanAsync(
        PlexServerConnection source,
        string sourceToken,
        PlexServerConnection destination,
        string destinationToken,
        IReadOnlySet<MigrationCategory> categories,
        IProgress<MigrationReadProgress>? progress,
        CancellationToken cancellationToken,
        string userId,
        string? planId)
    {
        var includePlaylists = categories.Contains(MigrationCategory.Playlists);
        if (!string.IsNullOrEmpty(planId))
            jobs.Edit(userId, planId, board => board.Use(categories));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var destinationTask = ReadSideAsync(destination, destinationToken, "Destination");
        var sourceTask = ReadSideAsync(source, sourceToken, "Source");
        MigrationCatalog sourceCatalog;
        MigrationCatalog destinationCatalog;
        try
        {
            await Task.WhenAll(destinationTask, sourceTask);
            destinationCatalog = await destinationTask;
            sourceCatalog = await sourceTask;
        }
        catch (Exception exception)
        {
            stop.Cancel();
            var root = exception is AggregateException aggregate
                ? aggregate.Flatten().InnerException ?? exception
                : exception;
            if (cancellationToken.IsCancellationRequested || root is OperationCanceledException)
                throw new PlexBridgeException("The read was canceled. Nothing was written.");
            if (root is PlexRequestException plex)
            {
                var message = SecretRedactor.ForDisplay(plex.Message, sourceToken);
                message = SecretRedactor.ForDisplay(message, destinationToken);
                throw new PlexBridgeException(message + " Nothing was written.");
            }

            throw;
        }

        progress?.Report(new MigrationReadProgress("Both", source.Name, "Matching", "Matching items", "Reading", 0, sourceCatalog.Items.Count + destinationCatalog.Items.Count));
        var built = MigrationPlanBuilder.Build(sourceCatalog, destinationCatalog, categories);
        var fingerprint = MigrationPlanBuilder.Fingerprint(sourceCatalog, destinationCatalog);
        if (!string.IsNullOrEmpty(planId))
            jobs.Edit(userId, planId, board => board.Stamp(built.Comparisons, built.Rows));
        progress?.Report(new MigrationReadProgress("Both", destination.Name, "Building plan", "Building plan", "Done", 0, built.Rows.Count));

        Task<MigrationCatalog> ReadSideAsync(PlexServerConnection server, string token, string role)
            => reader.ReadCatalogAsync(
                PlexServerUrl.ToUri(server.BaseUrl),
                token,
                includePlaylists,
                server.Name,
                role,
                progress,
                stop.Token,
                batch =>
                {
                    if (string.IsNullOrEmpty(planId) || batch.Items.Count == 0)
                        return;
                    var section = PlexMigrationReader.PhaseOf(batch.Section);
                    jobs.Edit(userId, planId, board =>
                    {
                        if (role == "Source")
                        {
                            board.IngestSource(section, batch.Items, confirmed: false);
                            board.IngestSource(section, batch.Items, confirmed: true);
                        }
                        else
                            board.IngestDestination(section, batch.Items);
                    });
                },
                () =>
                {
                    progress?.Report(new MigrationReadProgress(role, server.Name, "Catalog", "", "Closed", 0, 0));
                    if (!string.IsNullOrEmpty(planId))
                        jobs.Edit(userId, planId, board => board.CloseLibraries(role));
                },
                playlist =>
                {
                    if (string.IsNullOrEmpty(planId))
                        return;
                    jobs.Edit(userId, planId, board =>
                    {
                        if (role == "Source")
                        {
                            board.AddPlaylist(role, playlist, confirmed: false);
                            board.AddPlaylist(role, playlist, confirmed: true);
                        }
                        else
                            board.AddPlaylist(role, playlist, confirmed: true);
                    });
                });

        return new MigrationPlan(
            Guid.NewGuid().ToString("n"),
            fingerprint,
            source.Id,
            destination.Id,
            source.Name,
            destination.Name,
            built.Rows,
            built.Summary,
            built.Comparisons);
    }

    private async Task<string> MachineIdAsync(PlexServerConnection destination, string token, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(destination.MachineIdentifier) && destination.MachineIdentifier.All(char.IsAsciiHexDigit))
            return destination.MachineIdentifier;
        try
        {
            var read = await identity.GetIdentityAsync(PlexServerUrl.ToUri(destination.BaseUrl), token, cancellationToken);
            if (string.IsNullOrWhiteSpace(read.MachineIdentifier))
                throw new PlexRequestException("The destination did not report a machine identifier.");
            return read.MachineIdentifier;
        }
        catch (PlexRequestException exception)
        {
            throw new PlexBridgeException(SecretRedactor.ForDisplay(exception.Message, token));
        }
    }

    private async Task RecordFatalApplyAsync(string userId, HeldPlan? held, string error, IReadOnlyList<MigrationApplyItem> items, CancellationToken cancellationToken)
    {
        if (held is null)
        {
            logger.LogError("Migration apply failed and no plan was available for the audit.");
            return;
        }

        var applied = items.Count(item => item.Outcome is "Applied" or "Verified");
        var failed = items.Count(item => item.Outcome == "Failed");
        var skipped = items.Count(item => item.Outcome == "Skipped");
        var rows = items.ToList();
        if (rows.All(item => item.Detail != error))
            rows.Add(new MigrationApplyItem("Apply", "apply", "Failed", error));
        await SaveAuditAsync(userId, held, "Failed", applied, Math.Max(failed, 1), skipped, rows, cancellationToken);
    }

    private async Task SaveAuditAsync(
        string userId,
        HeldPlan held,
        string outcome,
        int applied,
        int failed,
        int skipped,
        IReadOnlyList<MigrationApplyItem> results,
        CancellationToken cancellationToken)
    {
        await using var db = await databases.CreateDbContextAsync(cancellationToken);
        var audit = new MigrationAudit
        {
            UserId = userId,
            SourceServerId = held.SourceId,
            DestinationServerId = held.DestinationId,
            Categories = string.Join(',', held.Categories.OrderBy(category => category.ToString())),
            Outcome = outcome,
            PlexUserUuid = string.IsNullOrEmpty(held.PlexUserKey) ? null : Trim(held.PlexUserKey, 80),
            PlexUserName = string.IsNullOrEmpty(held.PlexUserName) ? null : Trim(held.PlexUserName, 200),
            AppliedCount = applied,
            FailedCount = failed,
            SkippedCount = skipped,
            StartedUtc = held.CreatedUtc,
            FinishedUtc = clock.GetUtcNow(),
            Items = results.Take(500).Select(item => new MigrationAuditItem
            {
                Action = Trim(item.Action, 40),
                Outcome = Trim(item.Outcome, 40),
                Title = Trim(item.Title, 200),
                Detail = item.Detail is null ? null : Trim(item.Detail, 300)
            }).ToList()
        };
        db.MigrationAudits.Add(audit);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<(PlexServerConnection Source, string SourceToken, PlexServerConnection Destination, string DestinationToken)> LoadPairAsync(
        string adminUserId,
        int sourceId,
        int destinationId,
        string? plexUserKey,
        CancellationToken cancellationToken)
    {
        await using var db = await databases.CreateDbContextAsync(cancellationToken);
        var rows = await db.PlexServers.AsNoTracking().Where(server => server.Id == sourceId || server.Id == destinationId).ToListAsync(cancellationToken);
        var source = rows.FirstOrDefault(server => server.Id == sourceId) ?? throw new PlexBridgeException("The source server was not found.");
        var destination = rows.FirstOrDefault(server => server.Id == destinationId) ?? throw new PlexBridgeException("The destination server was not found.");
        var sourceToken = Unprotect(source);
        var destinationToken = Unprotect(destination);
        if (string.IsNullOrEmpty(plexUserKey))
            return (source, sourceToken, destination, destinationToken);
        if (!userTokens.TryGet(adminUserId, plexUserKey, out _, out var servers))
            throw new PlexBridgeException("Choose that Plex user again. The short-lived server tokens expired.");
        sourceToken = TokenFor(servers, source, source.Name);
        destinationToken = TokenFor(servers, destination, destination.Name);
        return (source, sourceToken, destination, destinationToken);
    }

    private static string TokenFor(IReadOnlyDictionary<string, string> servers, PlexServerConnection server, string label)
    {
        if (!string.IsNullOrWhiteSpace(server.MachineIdentifier)
            && servers.TryGetValue(server.MachineIdentifier, out var token)
            && !string.IsNullOrWhiteSpace(token))
            return token;
        throw new PlexBridgeException(label + " has no server token for this Plex user. Plex did not grant access, or the saved server has no machine id.");
    }

    private string Unprotect(PlexServerConnection server)
    {
        if (string.IsNullOrEmpty(server.ProtectedToken))
            throw new PlexBridgeException("A Plex token is required.");
        try
        {
            return protector.Unprotect(server.ProtectedToken);
        }
        catch (Exception exception) when (Crypto.IsProtectionFailure(exception))
        {
            logger.LogWarning("A stored Plex token could not be decrypted for server {ServerId}.", server.Id);
            throw new PlexBridgeException("The saved token can't be read. Enter it again. This usually means the keys folder changed.");
        }
    }

    private static IReadOnlySet<MigrationCategory> Normalize(IReadOnlyCollection<MigrationCategory> categories)
    {
        var selected = categories.Where(category => Enum.IsDefined(category)).ToHashSet();
        if (selected.Count == 0)
            throw new PlexBridgeException("Choose at least one category.");
        return selected;
    }

    private static string RequiredKey(string? ratingKey)
    {
        if (string.IsNullOrEmpty(ratingKey) || ratingKey.Length > 12 || !ratingKey.All(char.IsDigit))
            throw new PlexRequestException("The destination item id is not valid.");
        return ratingKey;
    }

    private static string PlaylistName(MigrationPlanRow row)
        => (row.PlaylistType ?? "") + "|" + (row.PlaylistTitle ?? "");

    private HeldPlan HeldFrom(string userId, MigrationPlan built)
    {
        var stored = jobs.Find(userId, built.Id)
            ?? throw new PlexBridgeException("That preview is no longer available. Preview again.");
        return new HeldPlan(stored.PlanId, stored.UserId, stored.SourceId, stored.DestinationId, stored.Categories, stored.Fingerprint, stored.CreatedUtc, stored.PlexUserKey, stored.PlexUserName);
    }

    private static string Trim(string value, int max)
        => value.Length <= max ? value : value[..max];

    private sealed record HeldPlan(
        string Id,
        string UserId,
        int SourceId,
        int DestinationId,
        IReadOnlySet<MigrationCategory> Categories,
        string Fingerprint,
        DateTimeOffset CreatedUtc,
        string PlexUserKey = "",
        string PlexUserName = "");
}

internal static class MigrationApplyText
{
    public static string ItemFailure(MigrationPlanRow row, string reason)
    {
        var title = string.IsNullOrWhiteSpace(row.PlaylistTitle) ? row.Title : row.PlaylistTitle;
        return row.Action switch
        {
            "playlist-create" => $"Couldn't create playlist \"{title}\": {reason}",
            "playlist-add" => $"Couldn't add to playlist \"{title}\": {reason}",
            "scrobble" => $"Couldn't mark \"{row.Title}\" watched: {reason}",
            "unscrobble" => $"Couldn't mark \"{row.Title}\" unwatched: {reason}",
            "progress" => $"Couldn't set progress on \"{row.Title}\": {reason}",
            "rate" => $"Couldn't set the rating on \"{row.Title}\": {reason}",
            _ => $"Couldn't apply \"{row.Title}\": {reason}"
        };
    }

    public static string Summary(MigrationApplyResult result)
    {
        var failed = result.Items.Where(item => item.Outcome == "Failed").Select(item => item.Detail).Where(detail => !string.IsNullOrWhiteSpace(detail)).ToList();
        var written = result.Items.Where(item => item.Outcome is "Applied" or "Verified").Select(item => item.Title).ToList();
        var text = $"Finished. Applied {result.Applied}. Failed {result.Failed}. Skipped {result.Skipped}.";
        foreach (var detail in failed)
            text += " " + detail;
        if (written.Count > 0)
            text += " Wrote: " + string.Join(", ", written) + ".";
        else
            text += " Nothing was written.";
        return text;
    }

    public static string Fatal(string reason, IReadOnlyList<MigrationApplyItem> items)
    {
        var applied = items.Count(item => item.Outcome is "Applied" or "Verified");
        var failed = Math.Max(1, items.Count(item => item.Outcome == "Failed"));
        var skipped = items.Count(item => item.Outcome == "Skipped");
        var written = items.Where(item => item.Outcome is "Applied" or "Verified").Select(item => item.Title).ToList();
        var text = $"Apply failed: {reason}. Applied {applied}. Failed {failed}. Skipped {skipped}.";
        foreach (var item in items.Where(item => item.Outcome == "Failed" && !string.IsNullOrWhiteSpace(item.Detail)))
            text += " " + item.Detail;
        text += written.Count > 0 ? " Wrote: " + string.Join(", ", written) + "." : " Nothing was written.";
        return text;
    }
}
