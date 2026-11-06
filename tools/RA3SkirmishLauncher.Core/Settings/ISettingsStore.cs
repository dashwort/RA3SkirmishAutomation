namespace RA3SkirmishLauncher.Core.Settings
{
    public interface ISettingsStore
    {
        LauncherSettings Load();
        void Save(LauncherSettings settings);
    }
}
