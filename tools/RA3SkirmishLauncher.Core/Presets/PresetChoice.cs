namespace RA3SkirmishLauncher.Core.Presets
{
    public sealed class PresetChoice
    {
        public PresetChoice(string path)
        {
            Path = System.IO.Path.GetFullPath(path);
            Name = System.IO.Path.GetFileName(path);
        }

        public string Path { get; }
        public string Name { get; }
        public override string ToString() => Name;
    }
}
