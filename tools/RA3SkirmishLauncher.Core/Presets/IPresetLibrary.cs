namespace RA3SkirmishLauncher.Core.Presets
{
    public interface IPresetLibrary
    {
        IReadOnlyList<PresetChoice> List(string directory);
        void Initialize(string directory, string? previousPreset, string bundledPreset);
        string Import(string directory, string source, string name, bool overwrite);
    }
}
