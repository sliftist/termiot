using System.Reflection;

namespace Termiot;

public static class BuildInfo
{
    public static readonly DateTime? BuildTimeUtc = LoadTime();
    public static readonly string RepoRoot = LoadMetadata("RepoRoot") ?? "";

    public static string Display => BuildTimeUtc is { } utc ? utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "";

    // Only meaningful on the dev machine: released exes carry a RepoRoot that doesn't exist on the user's disk, and without source there's nothing to rebuild.
    public static bool HasSource
    {
        get
        {
            try
            {
                return RepoRoot.Length > 0 && System.IO.Directory.Exists(RepoRoot);
            }
            catch
            {
                return false;
            }
        }
    }

    private static DateTime? LoadTime()
    {
        try
        {
            var value = LoadMetadata("BuildTimeUtc");
            return value != null ? DateTime.Parse(value, null, System.Globalization.DateTimeStyles.RoundtripKind) : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? LoadMetadata(string key)
    {
        try
        {
            return Assembly.GetExecutingAssembly()
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == key)?.Value;
        }
        catch
        {
            return null;
        }
    }
}
