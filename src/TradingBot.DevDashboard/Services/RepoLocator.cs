namespace TradingBot.DevDashboard.Services;

/// <summary>Findet das Repo-Wurzelverzeichnis (Ordner mit TradingBot.sln) read-only.</summary>
public static class RepoLocator
{
    public static string? FindRoot(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "TradingBot.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
