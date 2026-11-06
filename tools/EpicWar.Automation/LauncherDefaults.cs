using System.Text.RegularExpressions;

namespace EpicWar.Automation;

internal enum LauncherMode { BaseGame, ModMenu }

internal static class LauncherDefaults
{
    private const string GameDirectory = "Command and Conquer Red Alert 3";

    internal static LauncherMode ParseMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "mod-menu" => LauncherMode.ModMenu,
        _ => LauncherMode.BaseGame
    };

    internal static string InitialPreset(LauncherMode mode, IReadOnlyDictionary<string, string> saved, string bundledPreset)
    {
        if (mode == LauncherMode.BaseGame)
            return saved.GetValueOrDefault("base-preset", bundledPreset);
        return saved.GetValueOrDefault("mod-preset", saved.GetValueOrDefault("preset", ""));
    }

    internal static Dictionary<string, string?> NativeOptions(string gameRoot, string profile, string preset,
        string backupRoot, bool isolateMaps)
    {
        var options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["game-root"] = gameRoot,
            ["profile"] = profile,
            ["preset"] = preset,
            ["backup-root"] = backupRoot
        };
        if (isolateMaps) options["isolate-incompatible-maps"] = "true";
        return options;
    }

    internal static string ResolveGameRoot(string? saved, IEnumerable<string> candidates)
    {
        if (IsGameRoot(saved)) return Path.GetFullPath(saved!);
        return candidates.FirstOrDefault(IsGameRoot) is { } found ? Path.GetFullPath(found) :
            Path.GetFullPath(saved ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Steam", "steamapps", "common", GameDirectory));
    }

    internal static bool IsGameRoot(string? path) => !string.IsNullOrWhiteSpace(path) &&
        File.Exists(Path.Combine(path, "Data", "ra3_1.12.game"));

    internal static IEnumerable<string> DiscoverGameRoots()
    {
        var steamRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddSteamRoot(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
        AddSteamRoot(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"));
        AddSteamRoot(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Steam"));
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
        {
            AddSteamRoot(Path.Combine(drive.RootDirectory.FullName, "Steam"));
            AddSteamRoot(Path.Combine(drive.RootDirectory.FullName, "SteamLibrary"));
        }

        foreach (var steamRoot in steamRoots)
        {
            yield return Path.Combine(steamRoot, "steamapps", "common", GameDirectory);
            var libraries = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(libraries)) continue;
            string contents;
            try { contents = File.ReadAllText(libraries); }
            catch (IOException) { continue; }
            foreach (Match match in Regex.Matches(contents, "\\\"path\\\"\\s+\\\"([^\\\"]+)\\\"", RegexOptions.IgnoreCase))
                yield return Path.Combine(match.Groups[1].Value.Replace("\\\\", "\\"), "steamapps", "common", GameDirectory);
        }

        void AddSteamRoot(string root)
        {
            if (Directory.Exists(Path.Combine(root, "steamapps"))) steamRoots.Add(Path.GetFullPath(root));
        }
    }

    internal static string ResolveBackupRoot(string applicationBaseDirectory)
    {
        var current = new DirectoryInfo(Path.GetFullPath(applicationBaseDirectory));
        while (current != null)
        {
            var projectBackups = Path.Combine(current.FullName, "local-backups");
            if (Directory.Exists(projectBackups)) return Path.Combine(projectBackups, "NativeBackups");
            current = current.Parent;
        }
        return Path.Combine(Path.GetFullPath(applicationBaseDirectory), "local-backups", "NativeBackups");
    }
}
