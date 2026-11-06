using RA3SkirmishLauncher.Core.Presets;

namespace EpicWar.Automation
{
    internal sealed class RememberedPreset
    {
        private readonly string? destination;
        private readonly byte[] snapshot;

        private RememberedPreset(string? destination, byte[] snapshot)
        {
            this.destination = destination;
            this.snapshot = (byte[])snapshot.Clone();
        }

        public static RememberedPreset Prepare(IReadOnlyDictionary<string, string?> options, byte[] snapshot,
            params string[] protectedRoots)
        {
            var destination = options.GetValueOrDefault("remember-preset");
            if (destination != null)
            {
                destination = Path.GetFullPath(destination);
                SkirmishSession.ValidateFilePath(destination);
                foreach (var root in protectedRoots)
                {
                    var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
                    if (destination.Equals(fullRoot, StringComparison.OrdinalIgnoreCase) ||
                        destination.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new ArgumentException("The remembered preset must be outside game, profile and session directories.");
                    }
                }
                if (!Path.GetExtension(destination).Equals(".ini", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException("--remember-preset must name an INI file.");
                }
            }
            return new RememberedPreset(destination, snapshot);
        }

        public void Complete(bool successful)
        {
            if (successful && destination != null) { PresetFiles.WriteAtomic(destination, snapshot, true); }
        }
    }
}
