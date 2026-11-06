namespace RA3SkirmishLauncher.Core.Settings
{
    public interface ILauncherPaths
    {
        string ApplicationDirectory { get; }
        string SettingsFile { get; }
        string LegacySettingsFile { get; }
        string ProfileDirectory { get; }
        string ModDirectory { get; }
        string BackupDirectory { get; }
        string DiscoverGameRoot();
        IReadOnlyList<string> Profiles();
        string CurrentProfile();
    }
}
