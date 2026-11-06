namespace RA3SkirmishLauncher.Core.Presets
{
    public sealed class PresetLibrary : IPresetLibrary
    {
        public IReadOnlyList<PresetChoice> List(string directory)
        {
            if (!Directory.Exists(directory)) { return []; }
            return Directory.EnumerateFiles(directory, "*.ini").Select(path => new PresetChoice(path))
                .OrderBy(preset => !preset.Name.Equals("default.ini", StringComparison.OrdinalIgnoreCase))
                .ThenBy(preset => preset.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        public void Initialize(string directory, string? previousPreset, string bundledPreset)
        {
            var target = System.IO.Path.Combine(directory, "default.ini");
            if (File.Exists(target)) { return; }
            byte[]? bytes = null;
            if (!string.IsNullOrWhiteSpace(previousPreset))
            {
                try { bytes = PresetFiles.Read(previousPreset); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { }
            }
            bytes ??= PresetFiles.Read(bundledPreset);
            PresetFiles.WriteAtomic(target, bytes, false);
        }

        public string Import(string directory, string source, string name, bool overwrite)
        {
            name = name.Trim();
            if (string.IsNullOrWhiteSpace(name) || name is "." or ".." ||
                name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\'))
            {
                throw new ArgumentException("Choose a simple preset filename.");
            }
            if (!name.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)) { name += ".ini"; }
            if (name.Equals("default.ini", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("default.ini is reserved for the last successful skirmish. Choose another name.");
            }
            var target = System.IO.Path.Combine(System.IO.Path.GetFullPath(directory), name);
            PresetFiles.WriteAtomic(target, PresetFiles.Read(source), overwrite);
            return target;
        }
    }
}
