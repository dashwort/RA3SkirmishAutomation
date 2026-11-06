using System.Security.Cryptography;
using EpicWar.Automation;

internal static class NativePatchTests
{
    public static void Run(string repo, Action<bool, string> check)
    {
        var root = Path.Combine(repo, ".build", "native-launch-20261016", "validation", "native-installer-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var original = Enumerable.Range(0, 4096).Select(i => (byte)((i * 37 + 11) & 0xff)).ToArray();
        var patched = (byte[])original.Clone();
        // Stand-in for a verified native payload; these are inert fixture bytes, never an engine.
        patched[128] = 0x90;
        patched[129] = 0x90;
        patched[2048] ^= 0x5a;
        var identity = Identity(original, patched);

        string NewCase(string name, byte[] engineBytes)
        {
            var dir = Path.Combine(root, name);
            Directory.CreateDirectory(dir);
            var engineDir = Path.Combine(dir, "Data");
            var backupRoot = Path.Combine(root, "backups-" + name);
            Directory.CreateDirectory(engineDir);
            Directory.CreateDirectory(backupRoot);
            File.WriteAllBytes(Path.Combine(engineDir, "RA3_1.12.game"), engineBytes);
            return dir;
        }

        NativePatchInstaller Installer(string dir, NativePatchIdentity? use = null, Action<string>? checkpoint = null) =>
            new(Path.Combine(dir, "Data", "RA3_1.12.game"), Path.Combine(root, "backups-" + Path.GetFileName(dir)), use ?? identity,
                ensureGameClosed: () => { }, acquireExclusion: () => new NoopLease(), checkpoint: checkpoint);

        var normalDir = NewCase("install-restore-idempotent", original);
        var normal = Installer(normalDir);
        var before = normal.Inspect();
        check(before.State == "original" && before.EngineSha256 == Hash(original), "Fake baseline is identified as original without mutation");
        check(!before.BackupVerified, "Original state without a backup is not reported rollback-verified");
        check(!File.Exists(before.BackupPath), "Inspect does not create a backup or modify the fake engine");
        var installed = normal.Install(patched);
        check(installed.State == "installed" && installed.EngineSha256 == Hash(patched) && installed.BackupSha256 == Hash(original) && installed.BackupVerified, "Install writes the exact pinned candidate and verifies the original backup");
        check(File.ReadAllBytes(Path.Combine(normalDir, "Data", "RA3_1.12.game")).SequenceEqual(patched), "Installed fake engine bytes match the candidate exactly");
        var again = normal.Install(patched);
        check(again.State == "installed" && again.EngineSha256 == Hash(patched) && again.BackupSha256 == Hash(original), "Repeated install is idempotent with a verified backup");
        var restored = normal.Restore();
        check(restored.State == "original" && restored.EngineSha256 == Hash(original), "Restore returns the exact original fake engine");
        var restoredAgain = normal.Restore();
        check(restoredAgain.State == "original" && restoredAgain.EngineSha256 == Hash(original), "Repeated restore is idempotent");

        var editSet = new NativePatchEdit[]
        {
            new(128, original.AsSpan(128, 2).ToArray(), patched.AsSpan(128, 2).ToArray()),
            new(2048, original.AsSpan(2048, 1).ToArray(), patched.AsSpan(2048, 1).ToArray())
        };
        var rebuilt = NativePatchBuilder.Build(original, identity, editSet);
        check(rebuilt.SequenceEqual(patched), "Pinned byte recipe exactly reconstructs its candidate");
        var wrongSource = (byte[])original.Clone();
        wrongSource[0] ^= 1;
        check(RejectedBuild(() => NativePatchBuilder.Build(wrongSource, identity, editSet)), "Byte recipe rejects an unsupported source hash");
        var wrongExpected = new NativePatchEdit(128, [0, 0], patched.AsSpan(128, 2).ToArray());
        check(RejectedBuild(() => NativePatchBuilder.Build(original, identity, [wrongExpected, editSet[1]])), "Byte recipe rejects wrong expected source bytes");
        var overlap = new NativePatchEdit[]
        {
            editSet[0],
            new(129, original.AsSpan(129, 1).ToArray(), patched.AsSpan(129, 1).ToArray())
        };
        check(RejectedBuild(() => NativePatchBuilder.Build(original, identity, overlap)), "Byte recipe rejects overlapping edit ranges");
        var insertion = new NativePatchEdit(64, [], [0x90]);
        check(RejectedBuild(() => NativePatchBuilder.Build(original, identity, [insertion, editSet[0]])), "Byte recipe rejects an internal insertion");
        var outOfRange = new NativePatchEdit(original.Length, [0x00], [0x90]);
        check(RejectedBuild(() => NativePatchBuilder.Build(original, identity, [outOfRange])), "Byte recipe rejects an edit extending past EOF");
        var lengthChange = new NativePatchEdit(256, original.AsSpan(256, 2).ToArray(), [0x90]);
        check(RejectedBuild(() => NativePatchBuilder.Build(original, identity, [lengthChange])), "Byte recipe rejects length-changing in-place edits");
        var wrongOutputIdentity = identity with { PatchedSha256 = Hash(Enumerable.Repeat((byte)0x55, original.Length).ToArray()) };
        check(RejectedBuild(() => NativePatchBuilder.Build(original, wrongOutputIdentity, editSet)), "Byte recipe rejects a wrong pinned output hash");
        var appended = original.Concat(new byte[] { 0xaa, 0xbb }).ToArray();
        var appendIdentity = identity with { PatchedSha256 = Hash(appended) };
        var appendRecipe = new NativePatchEdit(original.Length, [], [0xaa, 0xbb]);
        check(NativePatchBuilder.Build(original, appendIdentity, [appendRecipe]).SequenceEqual(appended), "Byte recipe permits one payload appended exactly at EOF");
        var duplicateAppend = new NativePatchEdit[]
        {
            new(original.Length, [], [0xaa]),
            new(original.Length, [], [0xbb])
        };
        check(RejectedBuild(() => NativePatchBuilder.Build(original, appendIdentity, duplicateAppend)), "Byte recipe rejects duplicate append records");
        var undeclaredAlteration = (byte[])original.Clone();
        undeclaredAlteration[3000] ^= 1;
        var undeclaredIdentity = identity with { PatchedSha256 = Hash(undeclaredAlteration) };
        check(RejectedBuild(() => NativePatchBuilder.Build(original, undeclaredIdentity, [editSet[0]])), "Unlisted byte changes cannot satisfy the pinned output hash");

        static bool RejectedBuild(Func<byte[]> action)
        {
            try { _ = action(); return false; }
            catch (InvalidDataException) { return true; }
            catch (OverflowException) { return true; }
        }
        var unsupportedDir = NewCase("unsupported-baseline", Enumerable.Repeat((byte)0x42, original.Length).ToArray());
        var unsupportedEngine = Path.Combine(unsupportedDir, "Data", "RA3_1.12.game");
        var unsupportedBytes = File.ReadAllBytes(unsupportedEngine);
        var unsupported = Installer(unsupportedDir);
        var rejectedUnsupported = Rejected(() => unsupported.Install(patched), "Unsupported baseline hash is rejected");
        check(rejectedUnsupported && File.ReadAllBytes(unsupportedEngine).SequenceEqual(unsupportedBytes), "Unsupported baseline refusal preserves all fake engine bytes");
        check(unsupported.Inspect().State == "unknown", "Unsupported engine is reported as unknown");

        var badCandidateDir = NewCase("unsupported-candidate", original);
        var badCandidateEngine = Path.Combine(badCandidateDir, "Data", "RA3_1.12.game");
        var badCandidate = (byte[])patched.Clone();
        badCandidate[300] ^= 1;
        var badCandidateBefore = File.ReadAllBytes(badCandidateEngine);
        check(Rejected(() => Installer(badCandidateDir).Install(badCandidate), "Candidate with wrong pinned hash is rejected") &&
            File.ReadAllBytes(badCandidateEngine).SequenceEqual(badCandidateBefore), "Mismatched/corrupt candidate is rejected before target mutation");
        check(!Directory.EnumerateFiles(Path.Combine(root, "backups-unsupported-candidate"), "*", SearchOption.AllDirectories).Any(), "Rejected candidate does not leave an apparent verified backup");

        var modifiedDir = NewCase("external-engine-modification", original);
        var modified = Installer(modifiedDir);
        _ = modified.Inspect();
        var modifiedEngine = Path.Combine(modifiedDir, "Data", "RA3_1.12.game");
        var external = Enumerable.Repeat((byte)0x6d, original.Length).ToArray();
        File.WriteAllBytes(modifiedEngine, external);
        check(Rejected(() => modified.Install(patched), "Engine modified after preflight is refused") &&
            File.ReadAllBytes(modifiedEngine).SequenceEqual(external), "Concurrent external engine bytes are never overwritten");
        check(Rejected(() => modified.Restore(), "Restore refuses unknown engine hash") &&
            File.ReadAllBytes(modifiedEngine).SequenceEqual(external), "Restore leaves an unknown current engine untouched");

        var badRestoreCandidateDir = NewCase("restore-refuses-unknown-current", original);
        var badRestoreCandidate = Installer(badRestoreCandidateDir);
        _ = badRestoreCandidate.Install(patched);
        var unknownCurrent = Enumerable.Repeat((byte)0x7e, original.Length).ToArray();
        var badRestoreEngine = Path.Combine(badRestoreCandidateDir, "Data", "RA3_1.12.game");
        File.WriteAllBytes(badRestoreEngine, unknownCurrent);
        check(Rejected(() => badRestoreCandidate.Restore(), "Restore refuses user-modified installed engine") &&
            File.ReadAllBytes(badRestoreEngine).SequenceEqual(unknownCurrent), "Restore never overwrites post-install external edits");

        var missingBackupDir = NewCase("missing-backup", original);
        var missingBackupInstaller = Installer(missingBackupDir);
        var missingBackupInstalled = missingBackupInstaller.Install(patched);
        File.Delete(missingBackupInstalled.BackupPath);
        check(!missingBackupInstaller.Inspect().BackupVerified, "Installed state with missing backup is not rollback-verified");
        var patchedStillThere = File.ReadAllBytes(Path.Combine(missingBackupDir, "Data", "RA3_1.12.game"));
        check(Rejected(() => missingBackupInstaller.Restore(), "Missing verified backup blocks restore") &&
            File.ReadAllBytes(Path.Combine(missingBackupDir, "Data", "RA3_1.12.game")).SequenceEqual(patchedStillThere), "Missing backup failure preserves current candidate");

        var corruptBackupDir = NewCase("corrupt-backup", original);
        var corruptBackupInstaller = Installer(corruptBackupDir);
        var corruptBackupInstalled = corruptBackupInstaller.Install(patched);
        File.WriteAllBytes(corruptBackupInstalled.BackupPath, Enumerable.Repeat((byte)0x99, original.Length).ToArray());
        check(!corruptBackupInstaller.Inspect().BackupVerified, "Installed state with corrupt backup is not rollback-verified");
        var corruptBackupEngine = File.ReadAllBytes(Path.Combine(corruptBackupDir, "Data", "RA3_1.12.game"));
        check(Rejected(() => corruptBackupInstaller.Restore(), "Corrupt verified backup blocks restore") &&
            File.ReadAllBytes(Path.Combine(corruptBackupDir, "Data", "RA3_1.12.game")).SequenceEqual(corruptBackupEngine), "Corrupt backup cannot replace the candidate");

        foreach (var boundary in new[] { "prepared-install", "replaced-install" })
        {
            var dir = NewCase("recover-" + boundary, original);
            var installer = Installer(dir, checkpoint: reached => { if (reached == boundary) throw new IOException("synthetic interruption"); });
            _ = Rejected(() => installer.Install(patched), "Synthetic interruption at " + boundary + " is surfaced");
            var recovered = Installer(dir).Recover();
            check(recovered.State == "original" && recovered.EngineSha256 == Hash(original), "Recovery after " + boundary + " converges to verified original");
            var repeated = Installer(dir).Recover();
            check(repeated.State == "original" && repeated.EngineSha256 == Hash(original), "Recovery after " + boundary + " is repeatable");
        }

        // Exercise restore-side interruption after a valid install. Each failure is then recovered from journal evidence.
        foreach (var boundary in new[] { "prepared-restore", "replaced-restore" })
        {
            var dir = NewCase("recover-" + boundary, original);
            _ = Installer(dir).Install(patched);
            var interrupted = Installer(dir, checkpoint: reached => { if (reached == boundary) throw new IOException("synthetic interruption"); });
            _ = Rejected(() => interrupted.Restore(), "Synthetic interruption at " + boundary + " is surfaced");
            var recovered = Installer(dir).Recover();
            check(recovered.State == "original" && recovered.EngineSha256 == Hash(original), "Recovery after " + boundary + " converges to verified original");
            var repeated = Installer(dir).Recover();
            check(repeated.State == "original" && repeated.EngineSha256 == Hash(original), "Recovery after " + boundary + " is repeatable");
        }

        var unknownRecoveryDir = NewCase("recover-unknown-current", original);
        var unknownRecovery = Installer(unknownRecoveryDir, checkpoint: reached => { if (reached == "replaced-install") throw new IOException("synthetic interruption"); });
        _ = Rejected(() => unknownRecovery.Install(patched), "Install interruption leaves a recoverable journal");
        var unexpectedBytes = Enumerable.Repeat((byte)0x31, original.Length).ToArray();
        var unknownRecoveryEngine = Path.Combine(unknownRecoveryDir, "Data", "RA3_1.12.game");
        File.WriteAllBytes(unknownRecoveryEngine, unexpectedBytes);
        check(Rejected(() => Installer(unknownRecoveryDir).Recover(), "Recovery refuses unknown current hash") &&
            File.ReadAllBytes(unknownRecoveryEngine).SequenceEqual(unexpectedBytes), "Recovery never overwrites bytes outside the known transaction hashes");

        var forgedJournalDir = NewCase("forged-journal", original);
        var forgedInstaller = Installer(forgedJournalDir, checkpoint: reached => { if (reached == "replaced-install") throw new IOException("synthetic interruption"); });
        _ = Rejected(() => forgedInstaller.Install(patched), "Interrupted install leaves journal for validation");
        var forgedStatus = Installer(forgedJournalDir).Inspect();
        var decoy = Path.Combine(root, "journal-decoy.game");
        var decoyBytes = Enumerable.Repeat((byte)0x23, original.Length).ToArray();
        File.WriteAllBytes(decoy, decoyBytes);
        File.WriteAllText(forgedStatus.JournalPath, System.Text.Json.JsonSerializer.Serialize(new
        {
            Version = 1,
            EnginePath = decoy,
            Identity = identity,
            State = "prepared-install"
        }));
        var interruptedEngine = File.ReadAllBytes(Path.Combine(forgedJournalDir, "Data", "RA3_1.12.game"));
        check(Rejected(() => Installer(forgedJournalDir).Recover(), "Journal for a different engine path is rejected") &&
            File.ReadAllBytes(Path.Combine(forgedJournalDir, "Data", "RA3_1.12.game")).SequenceEqual(interruptedEngine) &&
            File.ReadAllBytes(decoy).SequenceEqual(decoyBytes), "Forged recovery journal cannot redirect writes to another file");
        var path259 = @"C:\" + new string('p', 256);
        var path260 = @"C:\" + new string('p', 257);
        check(Path.GetFullPath(path259).Length == 259, "Runtime path boundary fixture is exactly 259 absolute characters");
        LaunchPreset.ValidateRuntimePaths([path259]);
        check(true, "A 259-character absolute staged path is accepted");
        check(RejectedRuntimePath(path260), "A 260-character absolute staged path is rejected");
        check(RejectedRuntimePath(@"\\?\C:\short"), "An extended-prefix staged path is rejected");

        var launchFixture = Path.Combine(root, "launch-runtime-path-validation");
        var launchProfileRoot = Path.Combine(launchFixture, "profiles");
        var launchProfile = Path.Combine(launchProfileRoot, "validation");
        Directory.CreateDirectory(launchProfile);
        var liveIni = Path.Combine(launchProfile, "Skirmish.ini");
        File.WriteAllText(liveIni, "original live settings");
        var launchPreset = Path.Combine(launchFixture, "selected-preset.ini");
        File.WriteAllText(launchPreset, "Rts:Map = internal/test\nRts:GameInfo = M=internal/test;RU=1;S=Htest;\n");
        var fakeGameRoot = Path.Combine(launchFixture, "fake-game");
        var fakeModRoot = Path.Combine(launchFixture, "fake-mod");
        Directory.CreateDirectory(fakeGameRoot);
        Directory.CreateDirectory(fakeModRoot);
        var fakeEngine = Path.Combine(fakeGameRoot, "RA3_1.12.game");
        File.WriteAllText(fakeEngine, "non-executable safety fixture");
        var fakeConfig = Path.Combine(fakeGameRoot, "RA3_english_1.12.SkuDef");
        File.WriteAllText(fakeConfig, "fake config");
        var fakeBig = Path.Combine(fakeModRoot, "fixture.big");
        File.WriteAllBytes(fakeBig, [1, 2, 3, 4]);
        var fakeSku = Path.Combine(fakeModRoot, "RA3 Epic War_5.4.skudef");
        File.WriteAllText(fakeSku, "mod-game 1.12\nadd-big fixture.big\n");
        var embeddedOverlay = BundledNavigation.Extract(Path.Combine(launchFixture, "embedded-navigation"));
        check(BundledNavigation.Matches(embeddedOverlay), "Launch fixture uses the embedded overlay with its pinned SHA-256");
        var longRuntimeRoot = Path.Combine(launchFixture, new string('r', 110));
        var invalidOptions = new Dictionary<string, string?>
        {
            ["profile"] = "validation",
            ["profile-root"] = launchProfileRoot,
            ["preset"] = launchPreset,
            ["game-root"] = fakeGameRoot,
            ["engine-image"] = fakeEngine,
            ["config-skudef"] = fakeConfig,
            ["mod-directory"] = fakeModRoot,
            ["base-skudef"] = fakeSku,
            ["runtime-root"] = longRuntimeRoot,
            ["overlay"] = embeddedOverlay,
            ["dry-run"] = "true"
        };
        var wouldStagePath = Path.Combine(longRuntimeRoot, "run-" + new string('x', 52), "fixture.big");
        check(Path.GetFullPath(wouldStagePath).Length >= 260, "Full-launch fixture produces a staged BIG path at or above the unsafe boundary");
        check(RejectedRuntimeLaunch(invalidOptions), "Launch dry-run rejects the invalid runtime root");
        check(!Directory.Exists(Path.Combine(launchProfile, "EpicWarBackups")) && !Directory.Exists(longRuntimeRoot) &&
            File.ReadAllText(liveIni) == "original live settings" && File.Exists(fakeEngine),
            "Dry-run path refusal creates no profile backup/runtime directory and leaves the fake engine untouched");
        var liveOptions = new Dictionary<string, string?>(invalidOptions);
        liveOptions.Remove("dry-run");
        check(RejectedRuntimeLaunch(liveOptions), "Live launch rejects the invalid runtime root before profile mutation");
        check(!Directory.Exists(Path.Combine(launchProfile, "EpicWarBackups")) && !Directory.Exists(longRuntimeRoot) &&
            File.ReadAllText(liveIni) == "original live settings" && File.Exists(fakeEngine),
            "Live path refusal creates no profile backup/runtime directory and does not start the non-executable fake engine");

        static bool RejectedRuntimePath(string path)
        {
            try { LaunchPreset.ValidateRuntimePaths([path]); return false; }
            catch (ArgumentException error) when (error.Message.Contains("RA3 cannot safely read this staged path", StringComparison.Ordinal)) { return true; }
        }

        static bool RejectedRuntimeLaunch(IReadOnlyDictionary<string, string?> options)
        {
            try { _ = LaunchPreset.Launch(options, useMenuOverlay: true); return false; }
            catch (ArgumentException error) when (error.Message.Contains("RA3 cannot safely read this staged path", StringComparison.Ordinal)) { return true; }
        }
        var aliasFixture = Path.Combine(root, "reparse-alias-validation");
        var aliasGameRoot = Path.Combine(aliasFixture, "game");
        var aliasData = Path.Combine(aliasGameRoot, "Data");
        var aliasBackupTarget = Path.Combine(aliasGameRoot, "backup-target");
        Directory.CreateDirectory(aliasData);
        Directory.CreateDirectory(aliasBackupTarget);
        var aliasEngine = Path.Combine(aliasData, "RA3_1.12.game");
        File.WriteAllBytes(aliasEngine, original);
        var backupAlias = Path.Combine(aliasFixture, "backup-link");
        CreateJunction(backupAlias, aliasBackupTarget);
        check(RejectedAlias(() => new NativePatchInstaller(aliasEngine, backupAlias, identity,
            ensureGameClosed: () => { }, acquireExclusion: () => new NoopLease())),
            "Backup directory junction into the game tree is refused");

        var brokenTarget = Path.Combine(aliasGameRoot, "broken-backup-target");
        Directory.CreateDirectory(brokenTarget);
        var brokenBackupAlias = Path.Combine(aliasFixture, "broken-backup-link");
        CreateJunction(brokenBackupAlias, brokenTarget);
        Directory.Delete(brokenTarget);
        check(RejectedAlias(() => new NativePatchInstaller(aliasEngine, brokenBackupAlias, identity,
            ensureGameClosed: () => { }, acquireExclusion: () => new NoopLease())),
            "Dangling backup junction is refused even when its target is absent");
        var engineAliasDirectory = Path.Combine(aliasGameRoot, "LinkedData");
        CreateJunction(engineAliasDirectory, aliasData);
        var aliasedEnginePath = Path.Combine(engineAliasDirectory, "RA3_1.12.game");
        var safeBackupRoot = Path.Combine(aliasFixture, "safe-backups");
        Directory.CreateDirectory(safeBackupRoot);
        check(RejectedAlias(() => new NativePatchInstaller(aliasedEnginePath, safeBackupRoot, identity,
            ensureGameClosed: () => { }, acquireExclusion: () => new NoopLease())),
            "Engine path through a directory junction is refused at construction");

        var lateAliasDir = Path.Combine(aliasFixture, "late-engine-alias");
        var lateAliasData = Path.Combine(lateAliasDir, "Data");
        var lateAliasBackups = Path.Combine(aliasFixture, "late-safe-backups");
        var lateRealData = Path.Combine(aliasFixture, "late-real-data");
        Directory.CreateDirectory(lateAliasData);
        Directory.CreateDirectory(lateAliasBackups);
        var lateAliasEngine = Path.Combine(lateAliasData, "RA3_1.12.game");
        File.WriteAllBytes(lateAliasEngine, original);
        var lateAliasInstaller = new NativePatchInstaller(lateAliasEngine, lateAliasBackups, identity,
            ensureGameClosed: () => { }, acquireExclusion: () => new NoopLease());
        Directory.Move(lateAliasData, lateRealData);
        CreateJunction(lateAliasData, lateRealData);
        check(RejectedAlias(() => { _ = lateAliasInstaller.Inspect(); }),
            "Inspect revalidates engine path junctions created after construction");
        check(File.ReadAllBytes(Path.Combine(lateRealData, "RA3_1.12.game")).SequenceEqual(original), "Alias rejection leaves the linked engine target unchanged");

        static bool RejectedAlias(Action action)
        {
            try { action(); return false; }
            catch (ArgumentException) { return true; }
            catch (IOException) { return true; }
            catch (InvalidDataException) { return true; }
        }
        var lockedDir = NewCase("locked-engine", original);
        var lockedEngine = Path.Combine(lockedDir, "Data", "RA3_1.12.game");
        var lockedBytes = File.ReadAllBytes(lockedEngine);
        var lockRefused = false;
        using (var handle = new FileStream(lockedEngine, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            lockRefused = Rejected(() => Installer(lockedDir).Install(patched), "Locked engine blocks install");

        }
        check(lockRefused && File.ReadAllBytes(lockedEngine).SequenceEqual(lockedBytes), "Exclusive file lock was honored and original bytes preserved");

        static bool Rejected(Func<NativePatchStatus> action, string _)
        {
            try
            {
                var result = action();
                return result.State != "installed";
            }
            catch (Exception)
            {
                return true;
            }
        }
    }

    private static NativePatchIdentity Identity(byte[] original, byte[] patched) =>
        new("native-validation-fixture-v1", Hash(original), Hash(patched));

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static void CreateJunction(string link, string target)
    {
        static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
        var start = new System.Diagnostics.ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add($"New-Item -ItemType Junction -Path {Quote(link)} -Target {Quote(target)} | Out-Null");
        using var process = System.Diagnostics.Process.Start(start) ?? throw new IOException("Could not create fake junction fixture.");
        if (!process.WaitForExit(15000))
        {
            process.Kill(entireProcessTree: true);
            throw new IOException("Fake junction creation timed out.");
        }
        var stderr = process.StandardError.ReadToEnd();
        _ = process.StandardOutput.ReadToEnd();
        if (process.ExitCode != 0) throw new IOException("Fake junction creation failed: " + stderr);
    }

    private sealed class NoopLease : IDisposable
    {
        public void Dispose() { }
    }
}
