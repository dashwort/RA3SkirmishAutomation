using System.Text.RegularExpressions;

namespace RA3SkirmishLauncher.Core.Mods
{
    public sealed class ModDiscovery : IModDiscovery
    {
        public IReadOnlyList<GameChoice> Discover(IEnumerable<string> roots)
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in roots.Where(Directory.Exists))
            {
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true, MaxRecursionDepth = 2,
                    IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint
                };
                foreach (var path in Directory.EnumerateFiles(root, "*.skudef", options))
                {
                    paths.Add(Path.GetFullPath(path));
                }
            }
            var result = new List<GameChoice> { new("Base game", null, LaunchRoute.NativeSkirmish) };
            foreach (var path in paths.Order(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    Validate(path);
                    var name = Path.GetFileNameWithoutExtension(path);
                    var route = Path.GetFileName(path).Equals("RA3 Epic War_5.4.skudef", StringComparison.OrdinalIgnoreCase)
                        ? LaunchRoute.EpicWarSkirmish : LaunchRoute.ModMenu;
                    result.Add(new GameChoice($"{name} — {Path.GetFileName(Path.GetDirectoryName(path))}", path, route));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { }
            }
            return result;
        }

        public static void Validate(string skuDef)
        {
            var info = new FileInfo(skuDef);
            if (!info.Exists || info.Length is 0 or > 1048576)
            {
                throw new InvalidDataException("The mod SKUDEF is missing or invalid.");
            }
            var text = File.ReadAllText(skuDef);
            if (!Regex.IsMatch(text, @"(?im)^\s*mod-game\s+1\.12\s*$"))
            {
                throw new InvalidDataException("Choose a mod configuration for RA3 1.12.");
            }
            var archives = Regex.Matches(text, @"(?im)^\s*add-big\s+(.+?)\s*$");
            if (archives.Count == 0) { throw new InvalidDataException("The mod configuration has no BIG archives."); }
            foreach (Match archive in archives)
            {
                var relative = archive.Groups[1].Value.Trim().Trim('"');
                var path = Path.GetFullPath(Path.Combine(info.DirectoryName!, relative));
                if (!File.Exists(path)) { throw new FileNotFoundException("A mod archive is missing: " + relative, path); }
            }
        }
    }
}
