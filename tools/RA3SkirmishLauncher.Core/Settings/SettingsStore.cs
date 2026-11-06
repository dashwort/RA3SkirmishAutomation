using System.Text.Json;

namespace RA3SkirmishLauncher.Core.Settings
{
    public sealed class SettingsStore : ISettingsStore
    {
        private readonly ILauncherPaths paths;

        public SettingsStore(ILauncherPaths paths)
        {
            this.paths = paths;
        }

        public LauncherSettings Load()
        {
            if (File.Exists(paths.SettingsFile))
            {
                return JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(paths.SettingsFile))
                    ?? throw new InvalidDataException("Saved launcher settings are invalid.");
            }
            var settings = new LauncherSettings
            {
                GameRoot = paths.DiscoverGameRoot(), Profile = paths.CurrentProfile(),
                PresetDirectory = Path.Combine(paths.ApplicationDirectory, "Presets"), BackupDirectory = paths.BackupDirectory
            };
            if (!File.Exists(paths.LegacySettingsFile)) { return settings; }
            var old = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(paths.LegacySettingsFile))
                ?? throw new InvalidDataException("Previous launcher settings are invalid.");
            var modMode = old.GetValueOrDefault("mode") == "mod-menu";
            var game = old.GetValueOrDefault("game-root");
            if (game != null && File.Exists(Path.Combine(game, "Data", "ra3_1.12.game"))) { settings.GameRoot = game; }
            settings.Profile = old.GetValueOrDefault("profile", settings.Profile);
            settings.BackupDirectory = old.GetValueOrDefault("native-backup-root", settings.BackupDirectory);
            settings.PreviousPreset = modMode ? old.GetValueOrDefault("mod-preset") ?? old.GetValueOrDefault("preset")
                : old.GetValueOrDefault("base-preset");
            if (modMode)
            {
                settings.SelectedGameId = old.GetValueOrDefault("base-skudef", "base-game");
                if (settings.SelectedGameId != "base-game")
                {
                    settings.ExtraModLocations = Path.GetDirectoryName(settings.SelectedGameId) ?? "";
                }
            }
            if (bool.TryParse(old.GetValueOrDefault(modMode ? "mod-isolate" : "native-isolate"), out var isolate))
            {
                settings.IsolateMaps = isolate;
            }
            return settings;
        }

        public void Save(LauncherSettings settings)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(paths.SettingsFile)!);
            var temporary = paths.SettingsFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temporary, paths.SettingsFile, true);
            }
            finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
        }
    }
}
