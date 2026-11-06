using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EpicWar.Automation;

internal sealed class MapCompatibilityLease : IDisposable
{
    internal sealed record Entry(string Original, string Held, Dictionary<string,string> Hashes);
    internal sealed record Journal(string MapRoot, List<Entry> Entries, bool Restored);
    private readonly string journalPath;
    private readonly Journal journal;
    private bool disposed;

    internal static string[] FindIncompatible(string root)
    {
        if (!Directory.Exists(root)) return [];
        return Directory.EnumerateFiles(root, "*.map", SearchOption.AllDirectories)
            .Where(path => Encoding.UTF8.GetByteCount(Path.GetFileName(path)) >= 48)
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static MapCompatibilityLease Acquire(string root, string sessionDirectory, bool allowIsolation)
    {
        root=Path.GetFullPath(root);
        var bad=FindIncompatible(root);
        if (bad.Length>0 && !allowIsolation)
            throw new InvalidDataException("RA3 cannot safely enumerate these custom-map filenames (48-byte engine limit): " +
                string.Join(", ",bad.Select(Path.GetFileName)) + ". Use --isolate-incompatible-maps to preserve them outside the scan directory until the game exits.");
        var journalPath=Path.Combine(sessionDirectory,"map-restoration.json");
        Directory.CreateDirectory(sessionDirectory);
        var journal=new Journal(root,[],false);
        var lease=new MapCompatibilityLease(journalPath,journal);
        lease.Save();
        try
        {
            foreach(var folder in bad.Select(Path.GetDirectoryName).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var full=Path.GetFullPath(folder!);
                var relative=Path.GetRelativePath(root,full);
                if (relative is "." or ".." || relative.StartsWith(".."+Path.DirectorySeparatorChar) || Path.IsPathRooted(relative))
                    throw new InvalidDataException("An incompatible map is not inside its own map folder; refusing to move the map root.");
                if ((File.GetAttributes(full)&FileAttributes.ReparsePoint)!=0)
                    throw new InvalidDataException("Refusing to relocate a linked map folder.");
                var held=Path.GetFullPath(Path.Combine(sessionDirectory,"held-maps",relative));
                if (!held.StartsWith(Path.GetFullPath(sessionDirectory)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Map staging escaped the session directory.");
                var entry=new Entry(full,held,Hashes(full));
                journal.Entries.Add(entry);
                lease.Save(); // Persist the recovery plan before touching the source.
                Directory.CreateDirectory(Path.GetDirectoryName(held)!);
                Directory.Move(full,held);
                AssertHashes(held,entry.Hashes);
            }
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    private MapCompatibilityLease(string path,Journal journal) { journalPath=path;this.journal=journal; }
    private void Save()
    {
        var temporary=journalPath+".new";
        File.WriteAllText(temporary,JsonSerializer.Serialize(journal,new JsonSerializerOptions { WriteIndented=true }));
        File.Move(temporary,journalPath,true);
    }

    internal static void Recover(string path)
    {
        var journal=JsonSerializer.Deserialize<Journal>(File.ReadAllText(path)) ?? throw new InvalidDataException("Invalid map recovery journal.");
        var session=Path.GetDirectoryName(Path.GetFullPath(path))!;
        foreach(var entry in journal.Entries)
        {
            var original=Path.GetFullPath(entry.Original);
            var held=Path.GetFullPath(entry.Held);
            if(!original.StartsWith(Path.GetFullPath(journal.MapRoot)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase) ||
               !held.StartsWith(Path.Combine(session,"held-maps")+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase) ||
               entry.Hashes.Keys.Any(k=>Path.IsPathRooted(k)||k.Split(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar).Any(part=>part=="..")))
                throw new InvalidDataException("Recovery journal contains paths outside its declared map/session roots.");
        }
        using var lease=new MapCompatibilityLease(Path.GetFullPath(path),journal);
        // Recovery is exposed only after the launcher's global no-game check.
    }

    public void Dispose()
    {
        if(disposed || journal.Restored)return;
        foreach(var entry in journal.Entries.AsEnumerable().Reverse())
        {
            if(!Directory.Exists(entry.Held))
            {
                AssertHashes(entry.Original,entry.Hashes);
                continue;
            }
            if(Directory.Exists(entry.Original))
                throw new IOException($"Map restoration refused to overwrite a new folder: {entry.Original}. Recovery journal: {journalPath}");
            AssertHashes(entry.Held,entry.Hashes);
            Directory.Move(entry.Held,entry.Original);
            AssertHashes(entry.Original,entry.Hashes);
        }
        File.WriteAllText(journalPath,JsonSerializer.Serialize(journal with { Restored=true },new JsonSerializerOptions { WriteIndented=true }));
        disposed=true;
    }

    internal static Dictionary<string,string> Hashes(string folder) => Directory.EnumerateFiles(folder,"*",SearchOption.AllDirectories)
        .ToDictionary(p=>Path.GetRelativePath(folder,p),p=>
        {
            using var stream=File.OpenRead(p);
            return Convert.ToHexString(SHA256.HashData(stream));
        },StringComparer.OrdinalIgnoreCase);
    private static void AssertHashes(string folder,Dictionary<string,string> expected)
    {
        var actual=Hashes(folder);
        if(actual.Count!=expected.Count || expected.Any(pair=>!actual.TryGetValue(pair.Key,out var hash)||hash!=pair.Value))
            throw new IOException($"Map contents changed; restoration requires inspection: {folder}");
    }
}
