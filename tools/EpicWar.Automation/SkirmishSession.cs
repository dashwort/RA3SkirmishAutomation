using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EpicWar.Automation;

// Durable profile lease. No restoration happens implicitly while a game can
// still be alive; the supervisor must first wait for or stop its owned process.
internal sealed class SkirmishSession
{
    internal sealed record Journal(int Version, string SessionId, string ProfileRoot, string ProfileDirectory,
        string EnginePath, bool OriginalExisted, string? OriginalSha256, string PresetSha256, string State,
        int? ProcessId = null, long? ProcessStartUtcTicks = null);
    private Journal journal;
    private readonly string sessionDirectory;
    internal string JournalPath => Path.Combine(sessionDirectory, "profile-restoration.json");
    private string OriginalPath => Path.Combine(sessionDirectory, "Skirmish.original.ini");
    private string ActivePath => Path.Combine(journal.ProfileDirectory, "Skirmish.ini");
    private string OwnerPath => Path.Combine(journal.ProfileDirectory, ".epicwar-skirmish-owner.json");
    private sealed record Owner(string SessionId, string JournalPath);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private SkirmishSession(string sessionDirectory, Journal journal) { this.sessionDirectory = sessionDirectory; this.journal = journal; }

    internal static SkirmishSession Prepare(string profileRoot, string profileDirectory, string sessionDirectory,
        string sessionId, string enginePath, byte[] presetBytes, Action? beforeOwnershipClaim = null)
    {
        profileRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profileRoot)); profileDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profileDirectory));
        sessionDirectory = Path.GetFullPath(sessionDirectory); enginePath = Path.GetFullPath(enginePath);
        ValidatePaths(profileDirectory, sessionDirectory, enginePath);
        if (!Regex.IsMatch(sessionId, "^[a-f0-9]{32}$") || Path.GetFileName(sessionDirectory) != sessionId)
            throw new ArgumentException("Invalid native skirmish session identity.");
        if (!string.Equals(Path.GetDirectoryName(profileDirectory), profileRoot, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The selected profile must be directly inside its profile root.");
        if (Directory.Exists(sessionDirectory)) throw new IOException("The skirmish session directory already exists.");
        var active = Path.Combine(profileDirectory, "Skirmish.ini");
        var original = File.Exists(active) ? ReadChecked(active) : null;
        var journal = new Journal(1, sessionId, profileRoot, profileDirectory, enginePath, original != null,
            original == null ? null : Hash(original), Hash(presetBytes), "unclaimed");
        var session = new SkirmishSession(sessionDirectory, journal);
        Directory.CreateDirectory(sessionDirectory);
        if (original != null) WriteAtomic(session.OriginalPath, original, overwrite: false);
        if (HashOrNull(session.OriginalPath) != journal.OriginalSha256)
            throw new IOException("Original profile backup failed verification.");
        // An unclaimed journal never permits profile mutation and cannot block
        // another session. Publish its durable backup/plan before claiming it.
        session.Save();
        beforeOwnershipClaim?.Invoke(); // Disposable-fixture crash injection.
        // Non-overwriting atomic publication closes the check/create race and
        // never exposes a partially written ownership marker after a crash.
        WriteAtomic(session.OwnerPath, JsonSerializer.SerializeToUtf8Bytes(new Owner(sessionId, session.JournalPath)), overwrite: false);
        session.RequireOwner();
        session.journal = journal with { State = "prepared" }; session.Save();
        return session;
    }

    internal void Apply(byte[] presetBytes)
    {
        if (journal.State != "prepared" || Hash(presetBytes) != journal.PresetSha256)
            throw new InvalidDataException("Preset bytes or profile transaction state disagree.");
        ValidatePaths(journal.ProfileDirectory, sessionDirectory, journal.EnginePath);
        RequireOwner();
        if (HashOrNull(ActivePath) != journal.OriginalSha256)
            throw new IOException("The profile changed after its backup was captured; replacement was refused.");
        journal = journal with { State = "applying" }; Save();
        WriteAtomic(ActivePath, presetBytes, overwrite: true);
        journal = journal with { State = "applied" }; Save();
    }

    internal void MarkStarting() { journal = journal with { State = "starting" }; Save(); }
    internal void MarkProcess(Process process)
    {
        journal = journal with { State = "running", ProcessId = process.Id, ProcessStartUtcTicks = process.StartTime.ToUniversalTime().Ticks }; Save();
    }
    internal void MarkGameplay() { journal = journal with { State = "gameplay" }; Save(); }

    internal void Restore()
    {
        ValidatePaths(journal.ProfileDirectory, sessionDirectory, journal.EnginePath);
        if (journal.State is "unclaimed" or "abandoned")
        {
            // A crash before claim, a lost claim race, or a crash immediately
            // after claim cannot have authorized any active-profile mutation.
            journal = journal with { State = "abandoned" }; Save();
            ReleaseOwner(requireMatch: false); return;
        }
        // A profile is not a completed session until every durable lease is
        // restored. In particular, failed map recovery retains profile ownership.
        var mapJournal = Path.Combine(sessionDirectory, "map-restoration.json");
        ValidateFilePath(mapJournal);
        if (File.Exists(mapJournal)) MapCompatibilityLease.Recover(mapJournal);
        ReplayPreservationLease.RecoverIfPresent(sessionDirectory);
        if (journal.State == "restored") { ReleaseOwner(); return; }
        if (journal.State == "prepared")
        {
            // Replacement was never authorized. Leave concurrent profile edits
            // intact if preflight failed before entering the applying state.
            journal = journal with { State = "restored" }; Save(); ReleaseOwner(); return;
        }
        if (journal.OriginalExisted && HashOrNull(OriginalPath) != journal.OriginalSha256)
            throw new InvalidDataException($"Original profile backup is missing or corrupt. Recovery journal: {JournalPath}");
        if (journal.OriginalExisted && journal.OriginalSha256 == null)
            throw new InvalidDataException("Original profile hash is missing.");
        journal = journal with { State = "restoring" }; Save();
        if (File.Exists(ActivePath))
        {
            // Preserve what RA3 or an external editor wrote before exact rollback.
            var after = ReadChecked(ActivePath);
            var afterPath = Path.Combine(sessionDirectory, "Skirmish.after-" + Hash(after) + ".ini");
            if (!File.Exists(afterPath)) WriteAtomic(afterPath, after, overwrite: false);
        }
        if (journal.OriginalExisted) WriteAtomic(ActivePath, ReadChecked(OriginalPath), overwrite: true);
        else if (File.Exists(ActivePath)) { ValidateFilePath(ActivePath); File.Delete(ActivePath); }
        if (HashOrNull(ActivePath) != journal.OriginalSha256)
            throw new IOException($"Original profile restoration failed verification. Recovery journal: {JournalPath}");
        journal = journal with { State = "restored" }; Save(); ReleaseOwner();
    }

    internal static object Recover(string journalPath)
    {
        using var exclusion = LaunchExclusion.Acquire();
        LaunchPreset.EnsureGameClosed();
        return RecoverClosedSession(journalPath);
    }

    internal static void EnsureNoPendingSession(string profileDirectory, string sessionRoot)
    {
        var ownerPath = Path.Combine(profileDirectory, ".epicwar-skirmish-owner.json");
        ValidateFilePath(ownerPath);
        if (File.Exists(ownerPath))
        {
            var owner = JsonSerializer.Deserialize<Owner>(ReadChecked(ownerPath)) ?? throw new InvalidDataException("Invalid profile session ownership marker.");
            throw new InvalidOperationException($"An earlier skirmish session owns this profile. Recover: {owner.JournalPath}");
        }
        if (!Directory.Exists(sessionRoot)) return;
        foreach (var folder in Directory.EnumerateDirectories(sessionRoot))
        {
            if (!Regex.IsMatch(Path.GetFileName(folder), "^[a-f0-9]{32}$")) continue;
            var path = Path.Combine(folder, "profile-restoration.json");
            if (!File.Exists(path)) continue;
            ValidatePaths(profileDirectory, folder, path);
            var prior = JsonSerializer.Deserialize<Journal>(ReadChecked(path)) ?? throw new InvalidDataException($"Invalid prior session journal: {path}");
            if (string.Equals(Path.GetFullPath(prior.ProfileDirectory), profileDirectory, StringComparison.OrdinalIgnoreCase) && prior.State is not ("restored" or "unclaimed" or "abandoned"))
                throw new InvalidOperationException($"An earlier skirmish session needs recovery before this profile can be replaced. Recover: {path}");
        }
    }

    // The caller of this narrow core holds game exclusion and has proved that no
    // game is running. Tests can exercise crash recovery without starting a game.
    internal static object RecoverClosedSession(string journalPath)
    {
        journalPath = Path.GetFullPath(journalPath);
        var directory = Path.GetDirectoryName(journalPath)!;
        if (!Path.GetFileName(journalPath).Equals("profile-restoration.json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Expected a profile-restoration.json journal.");
        var journal = JsonSerializer.Deserialize<Journal>(ReadChecked(journalPath)) ?? throw new InvalidDataException("Invalid profile recovery journal.");
        if (journal.Version != 1 || !Regex.IsMatch(journal.SessionId, "^[a-f0-9]{32}$") ||
            Path.GetFileName(directory) != journal.SessionId || !ValidHash(journal.PresetSha256) ||
            (journal.OriginalExisted ? !ValidHash(journal.OriginalSha256) : journal.OriginalSha256 != null) ||
            journal.State is not ("unclaimed" or "abandoned" or "prepared" or "applying" or "applied" or "starting" or "running" or "gameplay" or "restoring" or "restored") ||
            !string.Equals(Path.GetDirectoryName(Path.GetFullPath(journal.ProfileDirectory)), Path.GetFullPath(journal.ProfileRoot), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Profile recovery journal does not match its session/profile identity.");
        ValidatePaths(journal.ProfileDirectory, directory, journal.EnginePath);
        var session = new SkirmishSession(directory, journal);
        session.Restore();
        return new { schemaVersion = 1, status = "session-restored", sessionId = journal.SessionId,
            profileRestored = true, mapsRestored = true, replayRestored = true, recoveryJournal = journalPath };
    }

    internal static void ValidatePaths(string profileDirectory, string sessionDirectory, string enginePath)
    {
        foreach (var path in new[] { Path.Combine(profileDirectory, "Skirmish.ini"), sessionDirectory, enginePath }) ValidateFilePath(path);
    }

    internal static void ValidateFilePath(string path)
    {
        for (string? part = Path.GetFullPath(path); part != null; part = Path.GetDirectoryName(part))
        {
            var info = new FileInfo(part);
            if (info.LinkTarget != null || new DirectoryInfo(part).LinkTarget != null ||
                ((File.Exists(part) || Directory.Exists(part)) && (File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0))
                throw new InvalidDataException($"Skirmish paths cannot pass through symbolic links or junctions: {part}");
        }
    }

    internal static byte[] ReadChecked(string path) { ValidateFilePath(path); return File.ReadAllBytes(path); }

    private void RequireOwner()
    {
        ValidateFilePath(OwnerPath);
        if (!File.Exists(OwnerPath)) throw new IOException("Profile ownership is missing; replacement was refused.");
        var owner = JsonSerializer.Deserialize<Owner>(ReadChecked(OwnerPath));
        if (owner?.SessionId != journal.SessionId || !string.Equals(owner.JournalPath, JournalPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Profile is owned by another session; replacement was refused.");
    }

    private void ReleaseOwner(bool requireMatch = true)
    {
        ValidateFilePath(OwnerPath);
        if (!File.Exists(OwnerPath)) return; // Legacy journals predate ownership markers.
        var owner = JsonSerializer.Deserialize<Owner>(ReadChecked(OwnerPath));
        if (owner?.SessionId != journal.SessionId || !string.Equals(owner.JournalPath, JournalPath, StringComparison.OrdinalIgnoreCase))
        {
            if (requireMatch) throw new IOException("Profile ownership changed; refusing to remove another session's marker.");
            return;
        }
        ValidateFilePath(OwnerPath);
        File.Delete(OwnerPath);
    }

    private void Save() => WriteAtomic(JournalPath, JsonSerializer.SerializeToUtf8Bytes(journal, JsonOptions), overwrite: true);
    internal static void WriteAtomic(string destination, byte[] bytes, bool overwrite)
    {
        ValidateFilePath(destination);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            ValidateFilePath(temporary);
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(bytes); output.Flush(flushToDisk: true); }
            ValidateFilePath(destination);
            File.Move(temporary, destination, overwrite);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string? HashOrNull(string path) { ValidateFilePath(path); return File.Exists(path) ? Hash(ReadChecked(path)) : null; }
    private static bool ValidHash(string? hash) => hash != null && Regex.IsMatch(hash, "^[0-9A-F]{64}$");
}
