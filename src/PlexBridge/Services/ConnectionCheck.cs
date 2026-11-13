using PlexBridge.Plex;

namespace PlexBridge.Services;

public enum ConnectionCheckStatus
{
    Queued,
    Testing,
    Verified,
    Failed,
    Canceled,
    NotTested
}

public sealed class ConnectionCheckRow
{
    public required string Uri { get; init; }
    public bool Local { get; init; }
    public bool Relay { get; init; }
    public bool Https { get; init; }
    public ConnectionCheckStatus Status { get; set; } = ConnectionCheckStatus.Queued;
    public string? Detail { get; set; }
}

/// <summary>Per-server check list. Each row changes as soon as that address finishes.</summary>
public sealed class ConnectionCheckSession
{
    public required string MachineId { get; init; }
    public List<ConnectionCheckRow> Rows { get; } = [];
    public bool Running { get; set; }
    public int Generation { get; set; }
    public TimeSpan ProbeLimit { get; set; } = TimeSpan.FromSeconds(8);
    public int Finished => Rows.Count(row => row.Status is ConnectionCheckStatus.Verified or ConnectionCheckStatus.Failed or ConnectionCheckStatus.Canceled);
    public bool AnyVerified => Rows.Any(row => row.Status == ConnectionCheckStatus.Verified);
    public bool Settled => !Running && Rows.All(row => row.Status is not (ConnectionCheckStatus.Queued or ConnectionCheckStatus.Testing));
    public bool ShowNoneAnswered => Settled && !AnyVerified && Rows.Any(row => row.Status == ConnectionCheckStatus.Failed);

    public static ConnectionCheckSession Create(string machineId, IReadOnlyList<PlexConnectionCandidate> candidates, bool httpsRequired)
    {
        var session = new ConnectionCheckSession { MachineId = machineId, Running = true };
        var automatic = 0;
        foreach (var candidate in candidates)
        {
            var row = new ConnectionCheckRow
            {
                Uri = candidate.Uri,
                Local = candidate.Local,
                Relay = candidate.Relay,
                Https = candidate.Https
            };
            if (httpsRequired && !candidate.Https)
            {
                row.Status = ConnectionCheckStatus.NotTested;
                row.Detail = "Not in the automatic check because this server requires HTTPS. Check this one still runs it.";
            }
            else if (automatic >= PlexConnectionOrder.AutomaticBatchSize)
            {
                row.Status = ConnectionCheckStatus.NotTested;
                row.Detail = "Not checked automatically. Use Check this one, or check the rest.";
            }
            else
            {
                automatic++;
            }
            session.Rows.Add(row);
        }
        return session;
    }

    public void MarkTesting(int index) => Rows[index].Status = ConnectionCheckStatus.Testing;

    public void MarkFinished(int index, ProbeAttemptView attempt)
    {
        Rows[index].Status = attempt.Outcome == "Reachable" ? ConnectionCheckStatus.Verified : ConnectionCheckStatus.Failed;
        Rows[index].Detail = string.IsNullOrWhiteSpace(attempt.Detail) ? attempt.Outcome : attempt.Outcome + ". " + attempt.Detail;
    }

    public void CancelFrom(int index)
    {
        for (var i = index; i < Rows.Count; i++)
        {
            if (Rows[i].Status is ConnectionCheckStatus.Queued or ConnectionCheckStatus.Testing)
            {
                Rows[i].Status = ConnectionCheckStatus.Canceled;
                Rows[i].Detail = "Not checked.";
            }
        }
        Running = false;
    }

    public void Settle()
    {
        foreach (var row in Rows)
        {
            if (row.Status != ConnectionCheckStatus.Testing)
                continue;
            row.Status = ConnectionCheckStatus.Failed;
            row.Detail = "The check stopped before this address finished.";
        }
    }
}

public static class ConnectionCheckRunner
{
    public static async Task RunAsync(
        ConnectionCheckSession session,
        Func<string, CancellationToken, Task<ProbeAttemptView>> probe,
        Func<Task> updated,
        CancellationToken cancellationToken)
    {
        var generation = ++session.Generation;
        try
        {
            for (var i = 0; i < session.Rows.Count; i++)
            {
                if (session.Generation != generation)
                    return;
                if (session.Rows[i].Status != ConnectionCheckStatus.Queued)
                    continue;
                if (cancellationToken.IsCancellationRequested)
                {
                    session.CancelFrom(i);
                    await updated();
                    return;
                }

                session.MarkTesting(i);
                await updated();
                ProbeAttemptView attempt;
                try
                {
                    attempt = await PlexDeadline.Run(
                        session.ProbeLimit,
                        cancellationToken,
                        token => probe(session.Rows[i].Uri, token));
                }
                catch (OperationCanceledException)
                {
                    if (session.Generation != generation)
                        return;
                    session.CancelFrom(i);
                    await updated();
                    return;
                }
                catch (Exception exception)
                {
                    if (session.Generation != generation)
                        return;
                    if (cancellationToken.IsCancellationRequested)
                    {
                        session.CancelFrom(i);
                        await updated();
                        return;
                    }

                    var timedOut = exception is PlexRequestException
                        && (exception.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase)
                            || exception.Message.Contains("canceled", StringComparison.OrdinalIgnoreCase));
                    attempt = new ProbeAttemptView(
                        session.Rows[i].Uri,
                        timedOut ? "Timed out" : "Unreachable",
                        timedOut ? exception.Message : "The check stopped before this address finished.");
                }

                if (session.Generation != generation)
                    return;
                session.MarkFinished(i, attempt);
                if (cancellationToken.IsCancellationRequested)
                {
                    session.CancelFrom(i + 1);
                    await updated();
                    return;
                }

                await updated();
            }
        }
        finally
        {
            if (session.Generation == generation)
            {
                session.Settle();
                session.Running = false;
            }
        }
    }
}
