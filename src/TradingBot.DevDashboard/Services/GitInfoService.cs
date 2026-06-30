namespace TradingBot.DevDashboard.Services;

/// <summary>
/// Liest Branch + Commit READ-ONLY direkt aus den .git-Dateien (kein git-Prozess, keine Writes).
/// Fällt bei Problemen sauber auf "unbekannt" zurück.
/// </summary>
public sealed class GitInfoService
{
    private readonly string? _root;

    public GitInfoService(string? root) => _root = root;

    public GitInfo Read()
    {
        try
        {
            if (_root is null) return GitInfo.Unknown;
            var gitDir = Path.Combine(_root, ".git");
            var headPath = Path.Combine(gitDir, "HEAD");
            if (!File.Exists(headPath)) return GitInfo.Unknown;

            var head = File.ReadAllText(headPath).Trim();
            if (head.StartsWith("ref:", StringComparison.Ordinal))
            {
                var refPath = head[4..].Trim();                       // refs/heads/main
                var branch = refPath.Replace("refs/heads/", "");
                var looseRef = Path.Combine(gitDir, refPath.Replace('/', Path.DirectorySeparatorChar));
                var commit = File.Exists(looseRef)
                    ? File.ReadAllText(looseRef).Trim()
                    : ReadPackedRef(gitDir, refPath);
                return new GitInfo(branch, Short(commit));
            }
            return new GitInfo("(detached)", Short(head));
        }
        catch
        {
            return GitInfo.Unknown;
        }
    }

    private static string? ReadPackedRef(string gitDir, string refPath)
    {
        var packed = Path.Combine(gitDir, "packed-refs");
        if (!File.Exists(packed)) return null;
        foreach (var line in File.ReadLines(packed))
        {
            if (line.StartsWith('#') || line.StartsWith('^')) continue;
            var parts = line.Split(' ', 2);
            if (parts.Length == 2 && parts[1].Trim() == refPath)
                return parts[0].Trim();
        }
        return null;
    }

    private static string Short(string? commit)
        => string.IsNullOrWhiteSpace(commit) ? "unbekannt" : commit.Length >= 7 ? commit[..7] : commit;
}

public sealed record GitInfo(string Branch, string Commit)
{
    public static readonly GitInfo Unknown = new("unbekannt", "unbekannt");
}
