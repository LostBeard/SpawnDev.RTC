using System.Reflection;

namespace SpawnDev.RTC.Demo;

/// <summary>
/// Build timestamp and library version info for the test page header.
/// Reads the BuildTimestamp metadata injected by MSBuild at compile time
/// and collects referenced SpawnDev library versions.
/// </summary>
internal static class BuildTimestamp
{
    public static readonly string Value = GetBuildTimestamp();
    public static readonly string LibraryVersions = GetLibraryVersions();

    /// <summary>
    /// Full build info string for copy-to-clipboard: build time + all SpawnDev library versions.
    /// </summary>
    public static readonly string FullBuildInfo = $"Build: {Value}\n{LibraryVersions}";

    private static string GetBuildTimestamp()
    {
        try
        {
            var assembly = typeof(BuildTimestamp).Assembly;
            var metadata = assembly
                .GetCustomAttributes(typeof(AssemblyMetadataAttribute), false)
                .OfType<AssemblyMetadataAttribute>();
            foreach (var meta in metadata)
            {
                if (meta.Key == "BuildTimestamp")
                    return meta.Value ?? "unknown";
            }
            return assembly.GetName().Version?.ToString() ?? "unknown";
        }
        catch { return "unknown"; }
    }

    private static string GetLibraryVersions()
    {
        try
        {
            var lines = new List<string>();
            var assemblies = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => a.GetName().Name?.StartsWith("SpawnDev.") == true)
                .OrderBy(a => a.GetName().Name);
            foreach (var asm in assemblies)
            {
                var name = asm.GetName();
                var infoVer = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                var ver = infoVer ?? name.Version?.ToString() ?? "?";
                // Trim git hash suffix if present (e.g., "4.9.2-rc.4+abc123")
                var plusIdx = ver.IndexOf('+');
                if (plusIdx > 0) ver = ver.Substring(0, plusIdx);
                lines.Add($"{name.Name} {ver}");
            }
            return string.Join("\n", lines);
        }
        catch { return "unknown"; }
    }
}
