using System.Text;
using System.Text.RegularExpressions;

namespace RA3SkirmishLauncher.Core.Presets
{
    public static class PresetFiles
    {
        public static byte[] Read(string path)
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is 0 or > 16777216)
            {
                throw new InvalidDataException("Choose a complete Skirmish.ini file smaller than 16 MB.");
            }
            var bytes = File.ReadAllBytes(path);
            Validate(bytes);
            return bytes;
        }

        public static void Validate(byte[] bytes)
        {
            if (bytes.Length is 0 or > 16777216)
            {
                throw new InvalidDataException("The skirmish preset is empty or too large.");
            }
            using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, true);
            var text = reader.ReadToEnd();
            var game = Regex.Match(text, @"(?im)^\s*Rts:GameInfo\s*=\s*(.+)$");
            if (!game.Success || !Regex.IsMatch(text, @"(?im)^\s*Rts:Map\s*=\s*\S+") ||
                !Regex.IsMatch(game.Groups[1].Value, @"(?:^|;)M=[^;]+;") ||
                !Regex.IsMatch(game.Groups[1].Value, @";RU=[^;]+;") ||
                !Regex.IsMatch(game.Groups[1].Value, @";S=H[^;]+;"))
            {
                throw new InvalidDataException("Supply a complete Skirmish.ini containing map, rules and player slots.");
            }
        }

        public static void WriteAtomic(string path, byte[] bytes, bool overwrite)
        {
            Validate(bytes);
            path = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, path, overwrite); }
            finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
        }
    }
}
