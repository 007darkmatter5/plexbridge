namespace PlexBridge.Watchdog;

/// <summary>
/// Decides when one play session is stalled. Paused time does not count.
/// A throttled transcode that is ahead of the viewer is not a slow transcode.
/// Buffering has its own rule; the offset rule opens only while the player says it is playing,
/// and the time spent buffering still counts toward that offset window.
/// </summary>
public sealed class StallTracker
{
    private readonly List<StreamSample> _samples = [];
    private string? _openRule;
    private long _openOffset;
    private bool _openOffsetSet;
    private bool _armRecovery = true;
    private bool _anchorSet;
    private DateTimeOffset _anchorAt;
    private long _anchorOffset;
    private TimeSpan _pausedSinceAnchor;
    private DateTimeOffset? _pauseBegan;
    private DateTimeOffset? _slowSince;
    private DateTimeOffset? _bufferingSince;
    private DateTimeOffset? _missingSince;
    private long _missingOffset;
    private bool _sawTranscode;

    public string? OpenRule => _openRule;
    public string? Building { get; private set; }

    public void Rehydrate(string rule)
    {
        _openRule = rule;
        _openOffsetSet = false;
        _armRecovery = false;
    }

    public string? TakeOpenRule()
    {
        var rule = _openRule;
        _openRule = null;
        return rule;
    }

    public IReadOnlyList<StreamSample> CopyTimeline() => _samples.ToArray();

    public string Health(string state)
    {
        var normalized = Normalize(state);
        if (_openRule is not null)
            return "Stuck";
        if (normalized == "paused")
            return "Paused";
        if (normalized == "buffering")
            return "Buffering";
        if (Building == StallRules.SlowTranscode)
            return "Slow";
        if (Building is not null)
            return "Stalling";
        return "Healthy";
    }

    public WatchStep Observe(StreamSample sample, WatchdogThresholds thresholds)
    {
        _samples.Add(sample);
        if (_samples.Count > TimelineCodec.Cap)
            _samples.RemoveAt(0);

        var state = Normalize(sample.State);
        if (sample.TranscodePresent)
            _sawTranscode = true;
        UpdateAnchor(sample, state);
        Building = null;

        if (_openRule is not null && !_openOffsetSet)
        {
            _openOffset = sample.ViewOffsetMs;
            _openOffsetSet = true;
        }

        var canRecover = _armRecovery;
        _armRecovery = true;
        if (canRecover && _openRule is not null && IsRecovered(sample, state))
        {
            var rule = _openRule;
            _openRule = null;
            ResetPending();
            return new WatchStep(WatchSignal.Recovered, rule, CopyTimeline());
        }

        if (state == "paused")
        {
            ResetPending();
            return _openRule is null
                ? new WatchStep(WatchSignal.None, null, CopyTimeline())
                : new WatchStep(WatchSignal.StillOpen, _openRule, CopyTimeline());
        }

        string? winner = null;
        Consider(ref winner, StallRules.TranscodeDisappeared, Missing(sample, state, thresholds.TranscodeMissing));
        Consider(ref winner, StallRules.SlowTranscode, Slow(sample, thresholds.SlowTranscode));
        Consider(ref winner, StallRules.LongBuffering, Buffering(sample, state, thresholds.LongBuffering));
        Consider(ref winner, StallRules.OffsetFrozen, Frozen(sample, state, thresholds.OffsetStall));

        if (_openRule is not null)
            return new WatchStep(WatchSignal.StillOpen, _openRule, CopyTimeline());
        if (winner is null)
            return new WatchStep(WatchSignal.None, null, CopyTimeline());

        _openRule = winner;
        _openOffset = sample.ViewOffsetMs;
        _openOffsetSet = true;
        return new WatchStep(WatchSignal.Opened, winner, CopyTimeline());
    }

    private void Consider(ref string? winner, string rule, RuleClock clock)
    {
        if (!clock.Active)
            return;
        Building ??= rule;
        if (clock.Sustained && winner is null && _openRule is null)
            winner = rule;
    }

    private RuleClock Frozen(StreamSample sample, string state, TimeSpan threshold)
    {
        var duration = OffsetDuration(sample);
        var active = state == "playing" && duration > TimeSpan.Zero;
        return new RuleClock(active, active && duration >= threshold);
    }

    private RuleClock Buffering(StreamSample sample, string state, TimeSpan threshold)
    {
        if (state != "buffering")
        {
            _bufferingSince = null;
            return default;
        }

        _bufferingSince ??= sample.At;
        if (sample.At < _bufferingSince)
            _bufferingSince = sample.At;
        var duration = sample.At - _bufferingSince.Value;
        return new RuleClock(true, duration >= threshold);
    }

    private RuleClock Slow(StreamSample sample, TimeSpan threshold)
    {
        if (!IsSlow(sample))
        {
            _slowSince = null;
            return default;
        }

        _slowSince ??= sample.At;
        if (sample.At < _slowSince)
            _slowSince = sample.At;
        return new RuleClock(true, sample.At - _slowSince.Value >= threshold);
    }

    private RuleClock Missing(StreamSample sample, string state, TimeSpan threshold)
    {
        if (sample.TranscodePresent || !_sawTranscode || state is not ("playing" or "buffering"))
        {
            if (sample.TranscodePresent || state == "paused")
                _missingSince = null;
            return default;
        }

        if (_missingSince is null || sample.ViewOffsetMs + 500 < _missingOffset)
        {
            _missingSince = sample.At;
            _missingOffset = sample.ViewOffsetMs;
            return new RuleClock(true, false);
        }

        if (sample.ViewOffsetMs >= _missingOffset + 1000)
        {
            _missingSince = null;
            _sawTranscode = false;
            return default;
        }

        if (sample.At < _missingSince)
            _missingSince = sample.At;
        return new RuleClock(true, sample.At - _missingSince.Value >= threshold);
    }

    private bool IsRecovered(StreamSample sample, string state)
    {
        switch (_openRule)
        {
            case StallRules.OffsetFrozen:
                return state == "playing" && sample.ViewOffsetMs >= _openOffset + 1000;
            case StallRules.LongBuffering:
                return state == "playing";
            case StallRules.SlowTranscode:
                if (sample.TranscodePresent && !IsSlow(sample))
                    return true;
                return !sample.TranscodePresent && state == "playing" && sample.ViewOffsetMs >= _openOffset + 1000;
            case StallRules.TranscodeDisappeared:
                return sample.TranscodePresent || (state == "playing" && sample.ViewOffsetMs >= _openOffset + 1000);
            default:
                return false;
        }
    }

    private static bool IsSlow(StreamSample sample)
    {
        if (!sample.TranscodePresent || sample.Speed is null || sample.Speed >= WatchdogDefaults.SlowSpeed)
            return false;
        if (sample.Throttled != false)
            return false;
        if (sample.Progress is null || sample.DurationMs is null or <= 0 || sample.ViewOffsetMs < 0)
            return false;
        if (sample.Progress >= 97)
            return false;
        var playback = sample.ViewOffsetMs / (double)sample.DurationMs.Value * 100.0;
        return sample.Progress < playback + WatchdogDefaults.AheadMarginPercent;
    }

    private void UpdateAnchor(StreamSample sample, string state)
    {
        if (!_anchorSet)
        {
            _anchorAt = sample.At;
            _anchorOffset = sample.ViewOffsetMs;
            _anchorSet = true;
            _pausedSinceAnchor = TimeSpan.Zero;
            if (state == "paused")
                _pauseBegan = sample.At;
            return;
        }

        if (state == "paused")
        {
            _pauseBegan ??= sample.At;
            return;
        }

        if (_pauseBegan is not null)
        {
            var paused = sample.At - _pauseBegan.Value;
            if (paused > TimeSpan.Zero)
                _pausedSinceAnchor += paused;
            _pauseBegan = null;
        }

        if (sample.ViewOffsetMs > _anchorOffset || sample.ViewOffsetMs + 500 < _anchorOffset)
        {
            _anchorAt = sample.At;
            _anchorOffset = sample.ViewOffsetMs;
            _pausedSinceAnchor = TimeSpan.Zero;
        }
    }

    private TimeSpan OffsetDuration(StreamSample sample)
    {
        if (!_anchorSet)
            return TimeSpan.Zero;
        var elapsed = sample.At - _anchorAt - _pausedSinceAnchor;
        return elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
    }

    private void ResetPending()
    {
        _slowSince = null;
        _bufferingSince = null;
        _missingSince = null;
    }

    private static string Normalize(string? state)
    {
        var value = (state ?? "").Trim().ToLowerInvariant();
        return value is "playing" or "buffering" or "paused" ? value : "paused";
    }

    private readonly record struct RuleClock(bool Active, bool Sustained);
}

public sealed record WatchUpdate(
    string SessionId,
    WatchedSession? Session,
    WatchSignal Signal,
    string? Rule,
    IReadOnlyList<StreamSample> Timeline);

/// <summary>Applies one poll to every session on a server, including sessions that disappeared.</summary>
public sealed class ServerWatch
{
    private readonly Dictionary<string, StallTracker> _trackers = new(StringComparer.Ordinal);

    public void Rehydrate(string sessionId, string rule)
    {
        if (string.IsNullOrEmpty(sessionId))
            return;
        var tracker = new StallTracker();
        tracker.Rehydrate(rule);
        _trackers[sessionId] = tracker;
    }

    public IReadOnlyList<WatchUpdate> Apply(IReadOnlyList<WatchedSession> sessions, WatchdogThresholds thresholds)
    {
        var byId = new Dictionary<string, WatchedSession>(StringComparer.Ordinal);
        foreach (var session in sessions)
        {
            if (!string.IsNullOrEmpty(session.PlaySessionId))
                byId[session.PlaySessionId] = session;
        }

        var updates = new List<WatchUpdate>();
        foreach (var session in byId.Values)
        {
            if (!_trackers.TryGetValue(session.PlaySessionId, out var tracker))
            {
                tracker = new StallTracker();
                _trackers[session.PlaySessionId] = tracker;
            }

            var step = tracker.Observe(session.Sample, thresholds);
            updates.Add(new WatchUpdate(session.PlaySessionId, session, step.Signal, step.Rule, step.Timeline));
        }

        foreach (var existing in _trackers.Keys.ToArray())
        {
            if (byId.ContainsKey(existing))
                continue;
            var tracker = _trackers[existing];
            var rule = tracker.TakeOpenRule();
            var timeline = tracker.CopyTimeline();
            _trackers.Remove(existing);
            if (rule is not null)
                updates.Add(new WatchUpdate(existing, null, WatchSignal.Ended, rule, timeline));
        }

        return updates;
    }

    public string HealthFor(string sessionId, string state)
        => _trackers.TryGetValue(sessionId, out var tracker) ? tracker.Health(state) : "Healthy";

    public string? OpenRuleFor(string sessionId)
        => _trackers.TryGetValue(sessionId, out var tracker) ? tracker.OpenRule : null;
}
