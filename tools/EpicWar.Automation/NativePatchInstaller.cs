using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EpicWar.Automation
{
    internal sealed class NativePatchInstaller
    {
        private readonly string enginePath;
        private readonly string backupPath;
        private readonly string journalPath;
        private readonly string transitionPath;
        private readonly NativePatchIdentity identity;
        private readonly Action ensureGameClosed;
        private readonly Func<IDisposable> acquireExclusion;
        private readonly Action<string>? checkpoint;

        internal NativePatchInstaller(string enginePath, string backupRoot, NativePatchIdentity identity,
            Action? ensureGameClosed = null, Func<IDisposable>? acquireExclusion = null, Action<string>? checkpoint = null)
        {
            this.enginePath = Path.GetFullPath(enginePath);
            backupRoot = Path.GetFullPath(backupRoot);
            var engineDirectory = Path.GetDirectoryName(this.enginePath)!;
            var gameDirectory = Path.GetFileName(engineDirectory).Equals("Data", StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(engineDirectory)! : engineDirectory;
            if (Within(backupRoot, gameDirectory) || Within(this.enginePath, backupRoot))
            {
                throw new ArgumentException("Engine backups must be outside the game directory.");
            }
            if (!Regex.IsMatch(identity.PatchId, @"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$") ||
                !IsHash(identity.OriginalSha256) || !IsHash(identity.PatchedSha256) ||
                identity.OriginalSha256.Equals(identity.PatchedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("A native package needs a patch ID and distinct SHA-256 identities.");
            }

            this.identity = identity with
            {
                OriginalSha256 = identity.OriginalSha256.ToUpperInvariant(),
                PatchedSha256 = identity.PatchedSha256.ToUpperInvariant()
            };
            backupPath = Path.Combine(backupRoot, this.identity.OriginalSha256, Path.GetFileName(this.enginePath));
            var targetId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(this.enginePath.ToUpperInvariant())));
            journalPath = Path.Combine(backupRoot, "transactions", targetId + ".json");
            transitionPath = Path.Combine(backupRoot, "transactions", targetId + ".before");
            this.ensureGameClosed = ensureGameClosed ?? LaunchPreset.EnsureGameClosed;
            this.acquireExclusion = acquireExclusion ?? (() => LaunchExclusion.Acquire());
            this.checkpoint = checkpoint;
            ValidatePaths();
        }

        internal NativePatchStatus Inspect()
        {
            ValidatePaths();
            var hash = Hash(enginePath);
            var state = "unknown";
            if (hash == null) { state = "missing"; }
            else if (hash == identity.OriginalSha256) { state = "original"; }
            else if (identity.HasNativePatch(hash)) { state = "installed"; }
            else if (identity.HasLargeAddressAware(hash)) { state = "original-large-address-aware"; }
            var backupHash = Hash(backupPath);
            return new NativePatchStatus(state, hash, backupHash, backupPath, journalPath, ReadJournal()?.State)
            {
                BackupVerified = backupHash == identity.OriginalSha256,
                LargeAddressAware = identity.HasLargeAddressAware(hash),
                NativeInstalled = identity.HasNativePatch(hash)
            };
        }

        internal NativePatchStatus Install(byte[] candidate)
        {
            if (!identity.HasNativePatch(Digest(candidate)))
            {
                throw new InvalidDataException("Native candidate does not match its pinned output SHA-256.");
            }
            using var exclusion = acquireExclusion();
            ensureGameClosed();
            RecoverCore();
            return Transition(candidate, "install");
        }

        internal NativePatchStatus SetMemory(bool enabled)
        {
            using var exclusion = acquireExclusion();
            return SetMemoryUnderLease(enabled);
        }

        // Launch coordinators already own the exclusion lease for their whole session.
        internal NativePatchStatus SetMemoryUnderLease(bool enabled)
        {
            ensureGameClosed();
            RecoverCore();
            var status = Inspect();
            if (!identity.IsKnown(status.EngineSha256))
            {
                throw new InvalidDataException("4 GB support cannot modify an unknown or missing engine.");
            }
            var candidate = LargeAddressAwarePatch.Apply(File.ReadAllBytes(enginePath), enabled);
            if (!identity.IsKnown(Digest(candidate)))
            {
                throw new InvalidDataException("The 4 GB variant does not match its pinned SHA-256.");
            }
            return Transition(candidate, "install");
        }

        internal NativePatchStatus Restore()
        {
            using var exclusion = acquireExclusion();
            ensureGameClosed();
            RecoverCore();
            var status = Inspect();
            if (status.State == "original") { return status; }
            if (!identity.IsKnown(status.EngineSha256))
            {
                throw new InvalidDataException("Refusing to overwrite an unknown or missing engine during restoration.");
            }
            RequireBackup();
            return Transition(File.ReadAllBytes(backupPath), "restore");
        }

        internal NativePatchStatus Recover()
        {
            using var exclusion = acquireExclusion();
            ensureGameClosed();
            RecoverCore();
            return Inspect();
        }

        private NativePatchStatus Transition(byte[] candidate, string operation)
        {
            ValidatePaths();
            var before = Hash(enginePath);
            var after = Digest(candidate);
            if (!identity.IsKnown(before) || !identity.IsKnown(after))
            {
                throw new InvalidDataException("Engine transitions require recognized source and destination hashes.");
            }
            PreserveOriginal();
            if (before == after)
            {
                RequireBackup();
                return Inspect();
            }

            Directory.CreateDirectory(Path.GetDirectoryName(transitionPath)!);
            File.Copy(enginePath, transitionPath, true);
            if (Hash(transitionPath) != before)
            {
                throw new IOException("The engine changed while its transition backup was captured.");
            }
            WriteJournal("prepared-" + operation, before, after);
            checkpoint?.Invoke("prepared-" + operation);
            Replace(candidate, before!, after);
            checkpoint?.Invoke("replaced-" + operation);
            WriteJournal(after == identity.OriginalSha256 ? "restored" : "installed", before, after);
            return Inspect();
        }

        private void RecoverCore()
        {
            ValidatePaths();
            var journal = ReadJournal();
            if (journal == null || journal.State is "installed" or "restored") { return; }
            var hash = Hash(enginePath);
            if (journal.State == "prepared-restore")
            {
                var restoreSource = journal.Version == 1 ? identity.PatchedSha256 : journal.BeforeSha256;
                if (hash != identity.OriginalSha256)
                {
                    if (hash != restoreSource) { throw new InvalidDataException("Recovery found a changed engine; its bytes were preserved."); }
                    RequireBackup();
                    Replace(File.ReadAllBytes(backupPath), hash!, identity.OriginalSha256);
                }
                WriteJournal("restored", identity.OriginalSha256, identity.OriginalSha256);
                return;
            }
            var before = journal.Version == 1 ? identity.OriginalSha256 : journal.BeforeSha256!;
            var after = journal.Version == 1 ? identity.PatchedSha256 : journal.AfterSha256!;
            if (hash != before && hash != after)
            {
                throw new InvalidDataException("Recovery found an unknown or changed engine; its bytes were preserved.");
            }
            if (hash != before)
            {
                RequireBackup();
                var source = journal.Version == 1 ? backupPath : transitionPath;
                if (Hash(source) != before)
                {
                    throw new InvalidDataException("The transition backup is missing or corrupt; recovery was refused.");
                }
                Replace(File.ReadAllBytes(source), after, before);
            }
            WriteJournal(before == identity.OriginalSha256 ? "restored" : "installed", before, before);
        }

        private NativePatchJournal? ReadJournal()
        {
            if (!File.Exists(journalPath)) { return null; }
            var journal = JsonSerializer.Deserialize<NativePatchJournal>(File.ReadAllText(journalPath))
                ?? throw new InvalidDataException("Invalid native patch transaction journal.");
            var sameIdentity = journal.Identity.PatchId == identity.PatchId &&
                journal.Identity.OriginalSha256 == identity.OriginalSha256 && journal.Identity.PatchedSha256 == identity.PatchedSha256;
            if (journal.Version is not (1 or 2) || !sameIdentity ||
                !string.Equals(journal.EnginePath, enginePath, StringComparison.OrdinalIgnoreCase) ||
                journal.State is not ("prepared-install" or "installed" or "prepared-restore" or "restored"))
            {
                throw new InvalidDataException("Native patch journal does not match this engine/package identity.");
            }
            if (journal.Version == 2 && (!identity.IsKnown(journal.BeforeSha256) || !identity.IsKnown(journal.AfterSha256)))
            {
                throw new InvalidDataException("The journal contains an unrecognized engine transition.");
            }
            return journal;
        }

        private void WriteJournal(string state, string? before, string? after)
        {
            ValidatePaths();
            var journal = new NativePatchJournal
            {
                Version = 2, EnginePath = enginePath, Identity = identity, State = state,
                BeforeSha256 = before, AfterSha256 = after
            };
            WriteAtomic(journalPath, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(journal)));
        }

        private void PreserveOriginal()
        {
            ValidatePaths();
            if (File.Exists(backupPath)) { RequireBackup(); return; }
            var bytes = File.ReadAllBytes(enginePath);
            if (Digest(bytes) == identity.OriginalLargeAddressAwareSha256)
            {
                bytes = LargeAddressAwarePatch.Apply(bytes, false);
            }
            if (Digest(bytes) != identity.OriginalSha256)
            {
                throw new InvalidDataException("The original engine backup is missing; the current engine was preserved.");
            }
            WriteAtomic(backupPath, bytes, false);
            RequireBackup();
        }

        private void RequireBackup()
        {
            ValidatePaths();
            if (Hash(backupPath) != identity.OriginalSha256)
            {
                throw new InvalidDataException("The original engine backup is missing or corrupt; it was not overwritten.");
            }
        }

        private void Replace(byte[] bytes, string expectedBefore, string expectedAfter)
        {
            ValidatePaths();
            if (Digest(bytes) != expectedAfter) { throw new InvalidDataException("Replacement bytes do not match their pinned identity."); }
            var temporary = enginePath + ".ew-new-" + Guid.NewGuid().ToString("N");
            var previous = enginePath + ".ew-previous-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllBytes(temporary, bytes);
                if (Hash(temporary) != expectedAfter || Hash(enginePath) != expectedBefore)
                {
                    throw new IOException("The engine changed before atomic replacement; replacement was refused.");
                }
                File.Replace(temporary, enginePath, previous);
                if (Hash(previous) != expectedBefore)
                {
                    if (Hash(enginePath) == expectedAfter) { File.Replace(previous, enginePath, null); }
                    throw new IOException("An external engine update raced installation; its original bytes were preserved.");
                }
                if (Hash(enginePath) != expectedAfter) { throw new IOException("Installed engine failed its output SHA-256 check."); }
            }
            finally
            {
                if (File.Exists(temporary)) { File.Delete(temporary); }
                if (File.Exists(previous) && Hash(previous) == expectedBefore) { File.Delete(previous); }
            }
        }

        private static void WriteAtomic(string path, byte[] bytes, bool overwrite = true)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".new";
            try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, path, overwrite); }
            finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
        }

        private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

        private static string? Hash(string path)
        {
            if (!File.Exists(path)) { return null; }
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }

        private static bool IsHash(string value) => Regex.IsMatch(value, @"\A[0-9A-Fa-f]{64}\z");

        private void ValidatePaths()
        {
            foreach (var path in new[] { enginePath, backupPath, journalPath, transitionPath })
            {
                for (string? current = path; current != null; current = Path.GetDirectoryName(current))
                {
                    if (new DirectoryInfo(current).LinkTarget != null || new FileInfo(current).LinkTarget != null)
                    {
                        throw new InvalidDataException($"Native patch paths cannot pass through a symbolic link or junction: {current}");
                    }
                }
            }
        }

        private static bool Within(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
