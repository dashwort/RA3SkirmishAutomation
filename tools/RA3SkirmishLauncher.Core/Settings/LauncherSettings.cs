namespace RA3SkirmishLauncher.Core.Settings
{
    public sealed class LauncherSettings
    {
        public string GameRoot { get; set; } = "";
        public string Profile { get; set; } = "";
        public string PresetDirectory { get; set; } = "";
        public string BackupDirectory { get; set; } = "";
        public string Theme { get; set; } = "Dark";
        public string SelectedGameId { get; set; } = "base-game";
        public bool IsolateMaps { get; set; } = true;
        public bool HighFrameRateEnabled { get; set; }
        public string ExtraModLocations { get; set; } = "";
        public string? PreviousPreset { get; set; }
    }
}
