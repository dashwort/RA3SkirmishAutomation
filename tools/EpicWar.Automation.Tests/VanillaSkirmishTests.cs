using System.Diagnostics;
using System.Text;
using System.Text.Json;
using EpicWar.Automation;

internal static class VanillaSkirmishTests
{
    internal static void Run(string repo, Action<bool, string> check)
    {
        var root = Path.Combine(repo, ".build", "vanilla-skirmish-tests", Guid.NewGuid().ToString("N"));
        var profileRoot = Path.Combine(root, "profiles");
        var profile = Path.Combine(profileRoot, "test");
        var game = Path.Combine(root, "game");
        var sessions = Path.Combine(root, "sessions");
        var backupRoot = Path.Combine(root, "native-backups");
        var parsedLaunch = CliOptions.Parse("launch-skirmish", ["--profile", "test", "--preset", "fixture.ini", "--backup-root", backupRoot]);
        check(parsedLaunch["backup-root"] == backupRoot, "Direct native launch accepts an explicit backup root");
        var timedLaunch = CliOptions.Parse("launch-skirmish", ["--test-seconds", "15"]);
        check(timedLaunch["test-seconds"] == "15", "Native CLI accepts a bounded gameplay observation before owned-process cleanup");
        var runtimeDependencies = Path.Combine(root, "runtime-dependencies"); Directory.CreateDirectory(runtimeDependencies);
        var childStart = new ProcessStartInfo();
        NativeRuntimeDependencies.PrependSearchDirectory(childStart, runtimeDependencies);
        check(childStart.Environment["PATH"]!.StartsWith(Path.GetFullPath(runtimeDependencies) + Path.PathSeparator, StringComparison.OrdinalIgnoreCase),
            "Direct native child search path includes the D-side legacy DirectX runtime");
        Directory.CreateDirectory(profile); Directory.CreateDirectory(Path.Combine(game, "Data"));
        var engine = Path.Combine(game, "Data", "ra3_1.12.game");
        File.WriteAllBytes(engine, [0, 1, 2, 3]); // Inert bytes: no executable is launched by these tests.
        var config = Path.Combine(game, "RA3_english_1.12.SkuDef"); File.WriteAllText(config, "stock fixture");
        File.WriteAllText(Path.Combine(profileRoot, "directory.ini"), "CurrentProfile=test\r\n");
        var original = Encoding.UTF8.GetBytes("original profile settings\r\n");
        var preset = Encoding.UTF8.GetBytes("Rts:Map = official/map\r\nRts:GameInfo = M=official/map;RU=rules;S=Hhuman;\r\nExtraSetting = preserve exactly\r\n");
        var presetPath = Path.Combine(root, "preset.ini"); File.WriteAllBytes(presetPath, preset);
        var active = Path.Combine(profile, "Skirmish.ini"); File.WriteAllBytes(active, original);
        var options = new Dictionary<string, string?> { ["profile"] = "test", ["profile-root"] = profileRoot,
            ["preset"] = presetPath, ["game-root"] = game, ["runtime-root"] = sessions, ["dry-run"] = "true" };
        var rejectedBridge = new RejectingBridge();
        var dry = JsonSerializer.SerializeToElement(VanillaSkirmish.Launch(options, rejectedBridge));
        var timedOptions = new Dictionary<string, string?>(options) { ["test-seconds"] = "15" };
        check(JsonSerializer.SerializeToElement(VanillaSkirmish.Launch(timedOptions)).GetProperty("testSeconds").GetInt32() == 15,
            "Native launch preserves the requested gameplay observation duration");
        foreach (var seconds in new[] { "0", "301", "invalid" })
        {
            timedOptions["test-seconds"] = seconds;
            check(Rejected(() => VanillaSkirmish.Launch(timedOptions)), "Native launch rejects invalid observation duration " + seconds);
        }
        check(dry.GetProperty("mode").GetString() == "vanilla-native-skirmish", "Vanilla dry-run has an explicit native mode");
        check(dry.GetProperty("gameImage").GetString() == engine, "Vanilla resolves the released Data engine directly");
        var launchArguments = dry.GetProperty("arguments").EnumerateArray().Select(value => value.GetString()!).ToArray();
        var xresIndex = Array.IndexOf(launchArguments, "-xres"); var yresIndex = Array.IndexOf(launchArguments, "-yres");
        check(xresIndex >= 0 && yresIndex >= 0 && launchArguments[xresIndex + 1] == "1280" && launchArguments[yresIndex + 1] == "720",
            "Direct native default resolution fits the current 1024x768 display");
        check(!dry.GetProperty("arguments").EnumerateArray().Any(v => v.GetString()!.Contains("modConfig", StringComparison.OrdinalIgnoreCase)), "Vanilla arguments omit all mod configuration");
        check(!dry.GetProperty("nativePatchVerified").GetBoolean() && !dry.GetProperty("matchStartVerified").GetBoolean(), "Dry-run does not fabricate native patch or gameplay verification");
        check(!rejectedBridge.Called && !Directory.Exists(sessions) && File.ReadAllBytes(active).SequenceEqual(original), "Dry-run creates no session or backup and never calls the bridge");
        var previousFactory = VanillaSkirmish.BridgeFactory;
        try
        {
            VanillaSkirmish.BridgeFactory = () => throw new Exception("Dry-run must not construct a live native bridge");
            VanillaSkirmish.Launch(options);
            check(true, "Dry-run remains available when production bridge construction is unavailable");
        }
        finally { VanillaSkirmish.BridgeFactory = previousFactory; }
        var anotherConfig = Path.Combine(game, "RA3_french_1.12.SkuDef"); File.WriteAllText(anotherConfig, "second stock fixture");
        check(Rejected(() => VanillaSkirmish.Launch(options)), "Ambiguous stock language configuration requires an explicit selection");
        options["config-skudef"] = config;
        check(JsonSerializer.SerializeToElement(VanillaSkirmish.Launch(options)).GetProperty("configSkuDef").GetString() == config, "Explicit stock language selection resolves ambiguity");
        var wrongProfile = new Dictionary<string, string?>(options) { ["profile"] = "other" };
        Directory.CreateDirectory(Path.Combine(profileRoot, "other"));
        check(Rejected(() => VanillaSkirmish.Launch(wrongProfile)), "A profile flag cannot silently select a different active profile");
        var encoded = string.Concat("CurrentProfile=test".Select(c => c + "_00"));
        File.WriteAllText(Path.Combine(profileRoot, "directory.ini"), encoded + "\r\n");
        VanillaSkirmish.ValidateSelectedProfile(profileRoot, "test");
        check(true, "Engine directory.ini escaped UTF-16 profile names are decoded");
        var unicodeEncoded = string.Concat(Encoding.Unicode.GetBytes("CurrentProfile=玩家").Select(b => b is >= 32 and <= 126 && b != '_' ? ((char)b).ToString() : $"_{b:X2}"));
        File.WriteAllText(Path.Combine(profileRoot, "directory.ini"), unicodeEncoded + "\r\n");
        VanillaSkirmish.ValidateSelectedProfile(profileRoot, "玩家");
        check(true, "Escaped UTF-16 profile decoding preserves non-ASCII names");
        File.WriteAllText(Path.Combine(profileRoot, "directory.ini"), encoded + "\r\n");
        var liveOptions = new Dictionary<string, string?>(options); liveOptions.Remove("dry-run");
        liveOptions["backup-root"] = backupRoot;
        var nativeRejected = Rejected(() => VanillaSkirmish.Launch(liveOptions, rejectedBridge));
        check(nativeRejected &&
            rejectedBridge.VerifiedBackupRoot == backupRoot && rejectedBridge.VerifiedGameRoot == game,
            $"Native launch verifies its patch against selected roots before process creation (rejected={nativeRejected}; game={rejectedBridge.VerifiedGameRoot}; backup={rejectedBridge.VerifiedBackupRoot})");
        check(!Directory.Exists(sessions) && File.ReadAllBytes(active).SequenceEqual(original), "Native package rejection preserves the active profile and creates no recovery state");

        (SkirmishSession Session, string Directory) Prepare()
        {
            var id = Guid.NewGuid().ToString("N"); var directory = Path.Combine(sessions, id);
            return (SkirmishSession.Prepare(profileRoot, profile, directory, id, engine, preset), directory);
        }
        var interruptedClaimId = Guid.NewGuid().ToString("N");
        var interruptedClaimDirectory = Path.Combine(sessions, interruptedClaimId);
        check(Rejected(() => SkirmishSession.Prepare(profileRoot, profile, interruptedClaimDirectory, interruptedClaimId, engine, preset,
            () => throw new IOException("Fixture interruption before atomic owner publication"))) &&
            File.ReadAllBytes(active).SequenceEqual(original) && !File.Exists(Path.Combine(profile, ".epicwar-skirmish-owner.json")),
            "A crash between durable journal preparation and owner claim leaves the profile untouched and unowned");
        SkirmishSession.EnsureNoPendingSession(profile, sessions);
        check(File.ReadAllBytes(Path.Combine(interruptedClaimDirectory, "Skirmish.original.ini")).SequenceEqual(original),
            "An interrupted unclaimed session has a complete durable backup and does not block a new launch");
        var first = Prepare();
        var ownerBeforeOrphanRecovery = File.ReadAllBytes(Path.Combine(profile, ".epicwar-skirmish-owner.json"));
        SkirmishSession.RecoverClosedSession(Path.Combine(interruptedClaimDirectory, "profile-restoration.json"));
        check(File.ReadAllBytes(active).SequenceEqual(original) && File.ReadAllBytes(Path.Combine(profile, ".epicwar-skirmish-owner.json")).SequenceEqual(ownerBeforeOrphanRecovery),
            "Recovering an unclaimed journal preserves a newer session's ownership and active settings");
        first.Session.Apply(preset);
        check(Rejected(() => SkirmishSession.EnsureNoPendingSession(profile, Path.Combine(root, "different-runtime"))), "Changing runtime-root cannot bypass stable profile ownership");
        check(Rejected(() => Prepare()), "Atomic ownership refuses a second transaction even without a pending-session scan");
        check(File.ReadAllBytes(active).SequenceEqual(preset), "Full INI bytes, including unknown settings, are applied exactly");
        check(File.ReadAllBytes(Path.Combine(first.Directory, "Skirmish.original.ini")).SequenceEqual(original), "Original settings are durably captured before replacement");
        check(Rejected(() => SkirmishSession.EnsureNoPendingSession(profile, sessions)), "An interrupted profile transaction blocks a new launch");
        File.WriteAllText(active, "game wrote settings on exit");
        first.Session.Restore();
        check(File.ReadAllBytes(active).SequenceEqual(original), "Normal cleanup restores exact original bytes after the game writes settings");
        check(Directory.EnumerateFiles(first.Directory, "Skirmish.after-*.ini").Count() == 1, "Cleanup preserves game or external edits in the session before rollback");
        first.Session.Restore(); SkirmishSession.EnsureNoPendingSession(profile, sessions);
        check(true, "Restoration is idempotent and restored sessions do not block later launches");

        var claimedBeforePrepared = Prepare();
        var claimedJournal = JsonSerializer.Deserialize<SkirmishSession.Journal>(File.ReadAllBytes(claimedBeforePrepared.Session.JournalPath))!;
        File.WriteAllBytes(claimedBeforePrepared.Session.JournalPath, JsonSerializer.SerializeToUtf8Bytes(claimedJournal with { State = "unclaimed" }));
        SkirmishSession.RecoverClosedSession(claimedBeforePrepared.Session.JournalPath);
        check(File.ReadAllBytes(active).SequenceEqual(original) && !File.Exists(Path.Combine(profile, ".epicwar-skirmish-owner.json")),
            "Recovery after claim but before prepared state removes only its exact marker without changing the profile");

        var interrupted = Prepare(); interrupted.Session.Apply(preset); interrupted.Session.MarkStarting();
        var recovered = JsonSerializer.SerializeToElement(SkirmishSession.RecoverClosedSession(interrupted.Session.JournalPath));
        check(recovered.GetProperty("profileRestored").GetBoolean() && File.ReadAllBytes(active).SequenceEqual(original), "Persistent recovery restores a session interrupted before process acknowledgement");
        var noApply = Prepare(); File.WriteAllText(active, "external edit before applying");
        check(Rejected(() => noApply.Session.Apply(preset)), "Apply refuses profile changes racing the captured backup");
        noApply.Session.Restore();
        check(File.ReadAllText(active) == "external edit before applying", "An unapplied transaction preserves external edits during cleanup");
        File.WriteAllBytes(active, original);
        var corrupt = Prepare(); corrupt.Session.Apply(preset);
        File.WriteAllText(Path.Combine(corrupt.Directory, "Skirmish.original.ini"), "corrupt");
        check(Rejected(() => corrupt.Session.Restore()) && File.ReadAllBytes(active).SequenceEqual(preset), "Corrupt original backup cannot overwrite the active profile");
        File.WriteAllBytes(Path.Combine(corrupt.Directory, "Skirmish.original.ini"), original); corrupt.Session.Restore();
        File.Delete(active);
        var absent = Prepare(); absent.Session.Apply(preset); absent.Session.Restore();
        check(!File.Exists(active), "A profile with no original Skirmish.ini returns to the original absent-file state");
        File.WriteAllBytes(active, original);
        var replayPath = Path.Combine(root, "replays", "Last Replay.RA3Replay");
        Directory.CreateDirectory(Path.GetDirectoryName(replayPath)!);
        var originalReplay = Encoding.UTF8.GetBytes("original replay exact bytes\0\r\n");
        File.WriteAllBytes(replayPath, originalReplay);
        var withReplay = Prepare(); withReplay.Session.Apply(preset);
        ReplayPreservationLease.Acquire(withReplay.Directory, replayPath);
        File.WriteAllText(replayPath, "generated replay");
        SkirmishSession.RecoverClosedSession(withReplay.Session.JournalPath);
        check(File.ReadAllBytes(replayPath).SequenceEqual(originalReplay), "Crash recovery restores the exact previous Last Replay bytes");
        check(Directory.EnumerateFiles(withReplay.Directory, "LastReplay.after-*.RA3Replay").Single() is string replayArchive && File.ReadAllText(replayArchive) == "generated replay", "Generated Last Replay is archived before original replay restoration");
        SkirmishSession.RecoverClosedSession(withReplay.Session.JournalPath);
        check(!File.Exists(replayPath + ".epicwar-owner.json"), "Replay recovery is idempotent and releases its exact ownership marker");
        File.Delete(replayPath);
        var absentReplay = Prepare(); absentReplay.Session.Apply(preset);
        ReplayPreservationLease.Acquire(absentReplay.Directory, replayPath);
        File.WriteAllText(replayPath, "new replay when none existed");
        absentReplay.Session.Restore();
        check(!File.Exists(replayPath) && Directory.EnumerateFiles(absentReplay.Directory, "LastReplay.after-*.RA3Replay").Count() == 1, "An originally absent replay is archived and returns to absent after cleanup");

        var failedMaps = Prepare(); failedMaps.Session.Apply(preset);
        var mapRoot = Path.Combine(root, "map-fixtures");
        var badMap = Path.Combine(mapRoot, "long-map"); Directory.CreateDirectory(badMap);
        File.WriteAllText(Path.Combine(badMap, new string('x', 48) + ".map"), "fixture map");
        MapCompatibilityLease.Acquire(mapRoot, failedMaps.Directory, true);
        Directory.CreateDirectory(badMap); // A conflicting replacement must never be overwritten.
        check(Rejected(() => failedMaps.Session.Restore()) &&
            JsonSerializer.Deserialize<SkirmishSession.Journal>(File.ReadAllBytes(failedMaps.Session.JournalPath))!.State != "restored" &&
            Rejected(() => SkirmishSession.EnsureNoPendingSession(profile, Path.Combine(root, "elsewhere"))), "Failed map cleanup retains the pending journal and stable profile owner");
        Directory.Delete(badMap); failedMaps.Session.Restore();
        check(File.ReadAllBytes(active).SequenceEqual(original) && Directory.Exists(badMap), "Recovery completes maps and profile after the map conflict is removed");

        var failedReplay = Prepare(); failedReplay.Session.Apply(preset);
        File.WriteAllBytes(replayPath, originalReplay);
        ReplayPreservationLease.Acquire(failedReplay.Directory, replayPath);
        var replayBackup = Path.Combine(failedReplay.Directory, "LastReplay.original.RA3Replay");
        File.WriteAllText(replayBackup, "corrupt"); File.WriteAllText(replayPath, "new generated replay");
        check(Rejected(() => failedReplay.Session.Restore()) && File.ReadAllText(replayPath) == "new generated replay" &&
            Rejected(() => SkirmishSession.EnsureNoPendingSession(profile, sessions)), "Corrupt replay backup preserves generated replay and pending profile ownership");
        File.WriteAllBytes(replayBackup, originalReplay); failedReplay.Session.Restore();

        void RejectLinkedFile(string file, Action action, string description)
        {
            var held = file + ".fixture-original"; File.Move(file, held);
            try
            {
                try { File.CreateSymbolicLink(file, held); }
                catch (IOException e) when ((e.HResult & 0xFFFF) == 1314)
                {
                    Console.Error.WriteLine("SKIPPED (Windows symbolic-link privilege unavailable): " + description);
                    return;
                }
                check(Rejected(action), description);
            }
            finally
            {
                if (new FileInfo(file).LinkTarget != null) File.Delete(file);
                File.Move(held, file);
            }
        }
        var linked = Prepare(); linked.Session.Apply(preset);
        RejectLinkedFile(linked.Session.JournalPath, () => SkirmishSession.RecoverClosedSession(linked.Session.JournalPath), "Recovery rejects a file-level symbolic link journal before reading it");
        RejectLinkedFile(Path.Combine(linked.Directory, "Skirmish.original.ini"), () => linked.Session.Restore(), "Recovery rejects a file-level symbolic link original backup even when its hash matches");
        linked.Session.Restore();

        var ownership = Prepare(); ownership.Session.Apply(preset);
        var ownerPath = Path.Combine(profile, ".epicwar-skirmish-owner.json"); var ownerBytes = File.ReadAllBytes(ownerPath);
        File.WriteAllText(ownerPath, JsonSerializer.Serialize(new { SessionId = Guid.NewGuid().ToString("N"), JournalPath = ownership.Session.JournalPath }));
        check(Rejected(() => ownership.Session.Restore()) && File.Exists(ownerPath), "Cleanup cannot remove another session's ownership marker");
        File.WriteAllBytes(ownerPath, ownerBytes); ownership.Session.Restore();
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        check(Rejected(() => VanillaSkirmish.Launch(liveOptions, rejectedBridge, cancelled.Token)), "Cancellation before launch leaves all files untouched");
        check(File.ReadAllBytes(active).SequenceEqual(original), "Cancellation retains exact baseline settings");
    }

    private static bool Rejected(Action action)
    {
        try { action(); return false; }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or InvalidDataException or IOException or OperationCanceledException) { return true; }
    }

    private sealed class RejectingBridge : INativeSkirmishBridge
    {
        internal bool Called;
        internal string? VerifiedGameRoot;
        internal string? VerifiedBackupRoot;
        public void VerifyInstalled(string gameRoot, string? backupRoot)
        {
            Called = true; VerifiedGameRoot = gameRoot; VerifiedBackupRoot = backupRoot;
            throw new InvalidOperationException("Inert test has no reviewed native patch");
        }
        public void ConfigureChild(ProcessStartInfo start, string sessionId, string profile, string presetSha256) => throw new Exception("No child is allowed");
        public void WaitForGameplay(Process process, string sessionId, string profile, string presetSha256, TimeSpan timeout, CancellationToken token) => throw new Exception("No game is allowed");
    }
}
