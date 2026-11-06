using RA3SkirmishLauncher.Core.Settings;

namespace RA3SkirmishLauncher.Core.Presentation
{
    public sealed class SettingsViewModel : ObservableModel
    {
        private string gameRoot = "";
        private string profile = "";
        private string presetDirectory = "";
        private string backupDirectory = "";
        private string theme = "Dark";
        private string extraModLocations = "";
        private bool isolateMaps = true;
        private bool highFrameRateEnabled;

        public string GameRoot { get => gameRoot; set => Set(ref gameRoot, value); }
        public string Profile { get => profile; set => Set(ref profile, value); }
        public string PresetDirectory { get => presetDirectory; set => Set(ref presetDirectory, value); }
        public string BackupDirectory { get => backupDirectory; set => Set(ref backupDirectory, value); }
        public string Theme { get => theme; set => Set(ref theme, value); }
        public string ExtraModLocations { get => extraModLocations; set => Set(ref extraModLocations, value); }
        public bool IsolateMaps { get => isolateMaps; set => Set(ref isolateMaps, value); }
        public bool HighFrameRateEnabled { get => highFrameRateEnabled; set => Set(ref highFrameRateEnabled, value); }

        public void Apply(LauncherSettings settings)
        {
            GameRoot = settings.GameRoot;
            Profile = settings.Profile;
            PresetDirectory = settings.PresetDirectory;
            BackupDirectory = settings.BackupDirectory;
            Theme = settings.Theme;
            ExtraModLocations = settings.ExtraModLocations;
            IsolateMaps = settings.IsolateMaps;
            HighFrameRateEnabled = settings.HighFrameRateEnabled;
        }

        public LauncherSettings Snapshot(string selectedGameId) => new()
        {
            GameRoot = GameRoot, Profile = Profile, PresetDirectory = PresetDirectory,
            BackupDirectory = BackupDirectory, Theme = Theme,
            ExtraModLocations = ExtraModLocations,
            IsolateMaps = IsolateMaps, HighFrameRateEnabled = HighFrameRateEnabled, SelectedGameId = selectedGameId
        };
    }
}
