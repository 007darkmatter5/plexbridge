namespace PlexBridge.Plex;

/// <summary>The compact library progress table shown while a preview is reading.</summary>
public static class PreviewProgress
{
    public sealed record Cell(string Kind, int Items);
    public sealed record Row(string Label, Cell Source, Cell Destination);

    private static readonly (string Phase, string Label)[] Libraries =
    [
        ("Movies", "Movies"),
        ("TV episodes", "TV Shows"),
        ("Music tracks", "Music"),
        ("Other Videos", "Other Videos"),
        ("Playlists", "Playlists")
    ];

    public static IReadOnlyList<Row> Rows(IReadOnlyList<MigrationPhaseView> phases)
    {
        var sourceClosed = Closed(phases, "Source");
        var destinationClosed = Closed(phases, "Destination");
        var rows = new List<Row>();
        foreach (var (phase, label) in Libraries)
        {
            var source = phases.Where(item => item.Role == "Source" && item.Phase == phase).ToList();
            var destination = phases.Where(item => item.Role == "Destination" && item.Phase == phase).ToList();
            if (source.Count == 0 && destination.Count == 0)
                continue;
            rows.Add(new Row(
                label,
                CellFor(source, sourceClosed, phase == "Playlists"),
                CellFor(destination, destinationClosed, phase == "Playlists")));
        }

        return rows;
    }

    public static (int Done, int Total) Meter(IReadOnlyList<Row> rows)
    {
        var done = 0;
        var total = 0;
        foreach (var row in rows)
        {
            Count(row.Source, ref done, ref total);
            Count(row.Destination, ref done, ref total);
        }

        return (done, total);
    }

    public static string Count(int items)
        => items.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

    private static void Count(Cell cell, ref int done, ref int total)
    {
        if (cell.Kind is "absent")
            return;
        total++;
        if (cell.Kind is "done" or "failed")
            done++;
    }

    private static bool Closed(IReadOnlyList<MigrationPhaseView> phases, string role)
        => phases.Any(item => item.Role == role && item.Phase == "Catalog" && item.State == "Closed");

    private static Cell CellFor(List<MigrationPhaseView> phases, bool catalogClosed, bool playlists)
    {
        if (phases.Count == 0)
            return catalogClosed && !playlists ? new Cell("absent", 0) : new Cell("waiting", 0);
        if (phases.Any(item => item.State == "Failed"))
            return new Cell("failed", phases.Sum(item => item.ItemsRead));
        if (phases.Any(item => item.State == "Reading"))
            return new Cell("reading", phases.Sum(item => item.ItemsRead));
        if (phases.Any(item => item.State != "Done"))
            return new Cell("waiting", phases.Sum(item => item.ItemsRead));
        return new Cell("done", phases.Sum(item => item.ItemsRead));
    }
}
