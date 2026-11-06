using System.Text;
using System.Text.RegularExpressions;

namespace RA3SkirmishLauncher.Core.Settings
{
    public sealed class LauncherPaths : ILauncherPaths
    {
        public LauncherPaths(string applicationDirectory)
        {
            ApplicationDirectory = Path.GetFullPath(applicationDirectory);
        }

        public string ApplicationDirectory { get; }
        public string SettingsFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RA3SkirmishLauncher", "settings.json");
        public string LegacySettingsFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EpicWar.Automation", "launcher-settings.json");
        public string ProfileDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Red Alert 3", "Profiles");
        public string ModDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Red Alert 3", "Mods");
        public string BackupDirectory
        {
            get
            {
                for (var directory = new DirectoryInfo(ApplicationDirectory); directory != null; directory = directory.Parent)
                {
                    var backups = Path.Combine(directory.FullName, "local-backups");
                    if (Directory.Exists(backups)) { return Path.Combine(backups, "NativeBackups"); }
                }
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".epicwar-automation", "NativeBackups");
            }
        }

        public string DiscoverGameRoot()
        {
            const string gameName = "Command and Conquer Red Alert 3";
            var steamRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam")
            };
            foreach (var drive in DriveInfo.GetDrives().Where(drive => drive.IsReady && drive.DriveType == DriveType.Fixed))
            {
                steamRoots.Add(Path.Combine(drive.RootDirectory.FullName, "Steam"));
                steamRoots.Add(Path.Combine(drive.RootDirectory.FullName, "SteamLibrary"));
            }
            foreach (var root in steamRoots.ToArray())
            {
                var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
                if (!File.Exists(vdf)) { continue; }
                foreach (Match match in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                {
                    steamRoots.Add(match.Groups[1].Value.Replace("\\\\", "\\"));
                }
            }
            return steamRoots.Select(root => Path.Combine(root, "steamapps", "common", gameName))
                .FirstOrDefault(root => File.Exists(Path.Combine(root, "Data", "ra3_1.12.game"))) ?? "";
        }

        public IReadOnlyList<string> Profiles() => Directory.Exists(ProfileDirectory)
            ? Directory.GetDirectories(ProfileDirectory).Select(path => Path.GetFileName(path)!).Order(StringComparer.OrdinalIgnoreCase).ToArray()
            : [];

        public string CurrentProfile()
        {
            var path = Path.Combine(ProfileDirectory, "directory.ini");
            if (!File.Exists(path)) { return ""; }
            foreach (var rawLine in File.ReadLines(path))
            {
                var line = rawLine;
                if (line.Contains("_00", StringComparison.OrdinalIgnoreCase))
                {
                    var decoded = Regex.Replace(line, "_([0-9A-Fa-f]{2})", m => ((char)Convert.ToByte(m.Groups[1].Value, 16)).ToString());
                    if (decoded.Length % 2 == 0 && decoded.All(c => c <= byte.MaxValue))
                    {
                        line = Encoding.Unicode.GetString(decoded.Select(c => (byte)c).ToArray());
                    }
                }
                var match = Regex.Match(line, @"^\s*CurrentProfile\s*=\s*(.*?)\s*$", RegexOptions.IgnoreCase);
                if (match.Success) { return match.Groups[1].Value; }
            }
            return "";
        }
    }
}
