using System.Reflection;

namespace Termiot;

public static class BuildInfo
{
    public static readonly DateTime? BuildTimeUtc = LoadTime();
    public static readonly string RepoRoot = LoadMetadata("RepoRoot") ?? "";

    public static string Display => BuildTimeUtc is { } utc ? "build " + utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "";

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
