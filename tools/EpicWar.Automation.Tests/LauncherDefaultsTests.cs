using EpicWar.Automation;

internal static class LauncherDefaultsTests
{
    internal static void Run(string repo, Action<bool, string> check)
    {
        InteractiveDesktopTests.Run(check);
        check(LauncherDefaults.ParseMode(null) == LauncherMode.BaseGame,
            "Launcher opens in native base-game mode when no mode is saved");
        check(LauncherDefaults.ParseMode("mod-menu") == LauncherMode.ModMenu,
            "Launcher preserves an explicitly saved mod-menu mode");

        var root = Path.Combine(repo, ".build", "launcher-default-tests", Guid.NewGuid().ToString("N"));
        var game = Path.Combine(root, "G", "SteamLibrary", "steamapps", "common", "Command and Conquer Red Alert 3");
        Directory.CreateDirectory(Path.Combine(game, "Data"));
        File.WriteAllBytes(Path.Combine(game, "Data", "ra3_1.12.game"), [1]);
        check(LauncherDefaults.ResolveGameRoot(Path.Combine(root, "missing"), [game]) == Path.GetFullPath(game),
            "Launcher falls back from an invalid saved game path to an installed Steam library");

        var oldSettings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["preset"] = Path.Combine(root, "mod", "EpicWar.ini"),
            ["base-skudef"] = Path.Combine(root, "mod", "EpicWar.skudef")
        };
        var bundled = Path.Combine(root, "Presets", "Allies.ini");
        check(LauncherDefaults.InitialPreset(LauncherMode.BaseGame, oldSettings, bundled) == bundled,
            "Legacy mod preset is not reused for native base-game mode");
        check(LauncherDefaults.InitialPreset(LauncherMode.ModMenu, oldSettings, bundled) == oldSettings["preset"],
            "Legacy mod preset remains available in the optional mod-menu mode");

        var options = LauncherDefaults.NativeOptions(game, "test-profile", bundled, Path.Combine(root, "NativeBackups"), true);
        check(options["backup-root"] == Path.Combine(root, "NativeBackups") &&
              !options.ContainsKey("base-skudef") && !options.ContainsKey("mod-directory") &&
              options["isolate-incompatible-maps"] == "true",
            "Native GUI launch builds direct-game options without mod configuration");

        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(Path.Combine(project, "local-backups"));
        var release = Path.Combine(project, "artifacts", "releases", "portable");
        Directory.CreateDirectory(release);
        check(LauncherDefaults.ResolveBackupRoot(release) == Path.GetFullPath(Path.Combine(project, "local-backups", "NativeBackups")),
            "Portable release resolves native backups to its source project's local-backups directory");
    }
}
