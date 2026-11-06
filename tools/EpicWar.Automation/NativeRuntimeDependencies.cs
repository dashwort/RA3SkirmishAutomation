using System.Diagnostics;

namespace EpicWar.Automation;

internal static class NativeRuntimeDependencies
{
    internal static void PrependSearchDirectory(ProcessStartInfo start, string directory)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var fullDirectory = Path.GetFullPath(directory);
        if (!Directory.Exists(fullDirectory)) return;

        var inheritedPath = start.Environment.TryGetValue("PATH", out var configuredPath)
            ? configuredPath ?? string.Empty
            : Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        if (inheritedPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(entry => entry.Equals(fullDirectory, StringComparison.OrdinalIgnoreCase))) return;

        start.Environment["PATH"] = inheritedPath.Length == 0
            ? fullDirectory
            : fullDirectory + Path.PathSeparator + inheritedPath;
    }
}
