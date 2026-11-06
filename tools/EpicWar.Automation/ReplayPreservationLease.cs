using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EpicWar.Automation;

// RA3 replaces Last Replay on match completion. Keep exact original bytes and
// retain the generated replay in the durable session before restoring them.
internal static class ReplayPreservationLease
{
    private sealed record Journal(int Version, string ReplayPath, bool OriginalExisted, string? OriginalSha256, bool Restored);
    private sealed record Owner(string JournalPath);
    private const string JournalName = "replay-restoration.json";

    internal static void Acquire(string sessionDirectory, string replayPath)
    {
        replayPath = Path.GetFullPath(replayPath);
        SkirmishSession.ValidateFilePath(replayPath);
        var path = Path.Combine(sessionDirectory, JournalName);
        var ownerPath = replayPath + ".epicwar-owner.json";
        SkirmishSession.ValidateFilePath(ownerPath);
        if (File.Exists(ownerPath)) throw new IOException($"An unfinished session owns the replay. Inspect: {ownerPath}");
        var original = File.Exists(replayPath) ? SkirmishSession.ReadChecked(replayPath) : null;
        if (original != null) SkirmishSession.WriteAtomic(Path.Combine(sessionDirectory, "LastReplay.original.RA3Replay"), original, false);
        // Durable plan precedes acquisition. A failure acquiring the marker is
        // recoverable; recovery refuses a marker belonging to another journal.
        SkirmishSession.WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(new Journal(1, replayPath, original != null,
            original == null ? null : Hash(original), false)), false);
        SkirmishSession.ValidateFilePath(Path.GetDirectoryName(ownerPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(ownerPath)!);
        SkirmishSession.ValidateFilePath(ownerPath);
        using var owner = new FileStream(ownerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        owner.Write(JsonSerializer.SerializeToUtf8Bytes(new Owner(path))); owner.Flush(true);
    }

    internal static void RecoverIfPresent(string sessionDirectory)
    {
        var path = Path.Combine(sessionDirectory, JournalName);
        SkirmishSession.ValidateFilePath(path);
        if (!File.Exists(path)) return;
        var journal = JsonSerializer.Deserialize<Journal>(SkirmishSession.ReadChecked(path)) ?? throw new InvalidDataException("Invalid replay journal.");
        if (journal.Version != 1 || !Path.IsPathFullyQualified(journal.ReplayPath) ||
            (journal.OriginalExisted ? journal.OriginalSha256 == null || !Regex.IsMatch(journal.OriginalSha256, "^[0-9A-F]{64}$") : journal.OriginalSha256 != null))
            throw new InvalidDataException("Invalid replay recovery identity.");
        var replay = journal.ReplayPath;
        var ownerPath = replay + ".epicwar-owner.json";
        SkirmishSession.ValidateFilePath(replay); SkirmishSession.ValidateFilePath(ownerPath);
        if (File.Exists(ownerPath))
        {
            var owner = JsonSerializer.Deserialize<Owner>(SkirmishSession.ReadChecked(ownerPath));
            if (!string.Equals(owner?.JournalPath, path, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Replay belongs to another session; recovery was refused.");
        }
        if (!journal.Restored)
        {
            var originalPath = Path.Combine(sessionDirectory, "LastReplay.original.RA3Replay");
            var original = journal.OriginalExisted ? SkirmishSession.ReadChecked(originalPath) : null;
            if (original != null && Hash(original) != journal.OriginalSha256) throw new InvalidDataException("Original replay backup is corrupt.");
            if (File.Exists(replay))
            {
                var after = SkirmishSession.ReadChecked(replay);
                var archive = Path.Combine(sessionDirectory, "LastReplay.after-" + Hash(after) + ".RA3Replay");
                SkirmishSession.ValidateFilePath(archive);
                if (!File.Exists(archive)) SkirmishSession.WriteAtomic(archive, after, false);
                else if (!SkirmishSession.ReadChecked(archive).SequenceEqual(after)) throw new InvalidDataException("Replay archive changed.");
            }
            if (original != null) SkirmishSession.WriteAtomic(replay, original, true);
            else if (File.Exists(replay)) { SkirmishSession.ValidateFilePath(replay); File.Delete(replay); }
            if ((File.Exists(replay) ? Hash(SkirmishSession.ReadChecked(replay)) : null) != journal.OriginalSha256)
                throw new IOException("Replay restoration failed verification.");
            SkirmishSession.WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(journal with { Restored = true }), true);
        }
        SkirmishSession.ValidateFilePath(ownerPath);
        if (File.Exists(ownerPath)) File.Delete(ownerPath);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
