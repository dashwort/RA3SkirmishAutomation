using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EpicWar.Automation;

// The native bridge must prove the effective profile directory and preset hash
// before requesting a match, and prove gameplay before returning successfully.
internal interface INativeSkirmishBridge
{
    void VerifyInstalled(string gameRoot, string? backupRoot);
    void ConfigureChild(ProcessStartInfo start, string sessionId, string profile, string presetSha256);
    void WaitForGameplay(Process process, string sessionId, string profile, string presetSha256,
        TimeSpan timeout, CancellationToken token);
}

internal static class VanillaSkirmish
{
    internal static string ModeName(bool nativeMod) => nativeMod ? "native-mod-compatibility" : "vanilla-native-skirmish";

    internal static Func<INativeSkirmishBridge>? BridgeFactory { get; set; }
    internal static event Action<string>? ProgressChanged;

    internal static object Launch(IReadOnlyDictionary<string, string?> options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Value(options, "dry-run") != "true") InteractiveDesktop.EnsureCurrentSessionReady();
        return Launch(options, Value(options, "dry-run") == "true" ? new MissingBridge() : BridgeFactory?.Invoke() ??
            (NativePatchCatalog.Current is null ? new MissingBridge() : new NativeSkirmishBridge()), cancellationToken);
    }

    internal static object Launch(IReadOnlyDictionary<string, string?> options, INativeSkirmishBridge bridge,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var key in new[] { "overlay", "engine-image" })
            if (Value(options, key) != null) throw new ArgumentException($"--{key} is not supported for native skirmishes.");
        var modDirectoryValue = Value(options, "mod-directory");
        var baseSkuDefValue = Value(options, "base-skudef");
        var nativeMod = modDirectoryValue is not null || baseSkuDefValue is not null;
        if (nativeMod && Value(options, "experimental") != "true")
            throw new ArgumentException("Native mod compatibility requires --experimental.");
        if (nativeMod && (modDirectoryValue is null || baseSkuDefValue is null))
            throw new ArgumentException("Native mod compatibility requires both --mod-directory and --base-skudef.");

        var profile = Required(options, "profile");
        if (profile is "." or ".." || profile.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || profile.Contains('/') || profile.Contains('\\'))
            throw new ArgumentException("--profile must name one existing profile folder.");
        var profileRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Value(options, "profile-root") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Red Alert 3", "Profiles")));
        var profileDirectory = Path.Combine(profileRoot, profile);
        if (!Directory.Exists(profileDirectory)) throw new DirectoryNotFoundException($"RA3 profile was not found: {profileDirectory}");
        ValidateSelectedProfile(profileRoot, profile);

        var preset = Path.GetFullPath(Required(options, "preset"));
        var presetBytes = ReadPreset(preset);
        MemoryPatchCommands.RequestedState(options);
        var presetSha256 = Convert.ToHexString(SHA256.HashData(presetBytes));
        var gameRoot = Path.GetFullPath(Value(options, "game-root") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steamapps", "common", "Command and Conquer Red Alert 3"));
        var engine = Path.Combine(gameRoot, "Data", "ra3_1.12.game");
        if (!File.Exists(engine)) throw new FileNotFoundException("Released RA3 1.12 engine was not found.", engine);
        var config = ResolveConfiguration(gameRoot, Value(options, "config-skudef"));
        var modDirectory = nativeMod ? Path.GetFullPath(modDirectoryValue!) : null;
        var modSkuDef = nativeMod ? Path.GetFullPath(baseSkuDefValue!) : null;
        if (nativeMod)
        {
            var modRoot = Path.TrimEndingDirectorySeparator(modDirectory!);
            var modPrefix = modRoot + Path.DirectorySeparatorChar;
            if (!modSkuDef!.StartsWith(modPrefix, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("--base-skudef must remain inside --mod-directory.");
            RA3SkirmishLauncher.Core.Mods.ModDiscovery.Validate(modSkuDef);
        }
        var width = Number(options, "width", 1280, 800, 7680);
        var height = Number(options, "height", 720, 600, 4320);
        var setupSeconds = Number(options, "setup-timeout", 120, 10, 300);
        var loadSeconds = Number(options, "load-timeout", 300, 10, 300);
        var testSeconds = Value(options, "test-seconds") == null ? 0 : Number(options, "test-seconds", 10, 1, 300);
        var sessionRoot = Path.GetFullPath(Value(options, "runtime-root") ?? Path.Combine(NativeAutomationStorage.Root, "VanillaSessions"));
        if (Within(sessionRoot, gameRoot) || Within(sessionRoot, profileRoot))
            throw new ArgumentException("--runtime-root must be outside the game and profile directories.");
        var sessionId = Guid.NewGuid().ToString("N");
        var sessionDirectory = Path.Combine(sessionRoot, sessionId);
        var remembered = RememberedPreset.Prepare(options, presetBytes, gameRoot, profileRoot, sessionRoot);
        var replayPath = Path.GetFullPath(Value(options, "replay-path") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Red Alert 3", "Replays", "Last Replay.RA3Replay"));
        if (Within(replayPath, gameRoot) || Within(replayPath, profileRoot) || Within(replayPath, sessionRoot))
            throw new ArgumentException("--replay-path must be outside game, profile and runtime directories.");
        SkirmishSession.ValidateFilePath(replayPath);
        SkirmishSession.ValidatePaths(profileDirectory, sessionDirectory, engine);
        var mapRoot = Path.GetFullPath(Value(options, "map-root") ?? Path.Combine(Path.GetDirectoryName(profileRoot)!, "Maps"));
        var incompatibleMaps = MapCompatibilityLease.FindIncompatible(mapRoot);
        var isolateMaps = Value(options, "isolate-incompatible-maps") == "true";
        var start = new ProcessStartInfo { FileName = engine, WorkingDirectory = gameRoot, UseShellExecute = false };
        NativeRuntimeDependencies.PrependSearchDirectory(start, Path.Combine(AppContext.BaseDirectory, "dependencies", "x86"));
        var launchArguments = new List<string> { "-config", config };
        if (nativeMod) launchArguments.AddRange(["-modConfig", modSkuDef!]);
        launchArguments.AddRange(["-win", "-xres", width.ToString(), "-yres", height.ToString()]);
        foreach (var argument in launchArguments)
            start.ArgumentList.Add(argument);
        // This is a request for the native bridge to verify the engine's actual
        // folder lookup. It is not a claim that an environment variable redirects RA3.
        start.Environment["RA3_AUTOSKIRMISH_PROFILE_DIRECTORY"] = profileDirectory;

        if (Value(options, "dry-run") == "true")
            return new { schemaVersion = 1, status = "dry-run", mode = ModeName(nativeMod), gameRoot,
                gameImage = engine, configSkuDef = config, profile, profileDirectory, preset,
                modDirectory, modSkuDef,
                presetSha256, sessionId, sessionDirectory, backupRoot = Value(options, "backup-root"),
                nativePatchRequired = true, nativePatchVerified = false,
                profileSelectionVerified = false, matchStartVerified = false, incompatibleMaps, mapIsolationRequested = isolateMaps,
                profileRestoredOnExit = true, replayPath, replayRestoredOnExit = true, setupSeconds, loadSeconds, testSeconds, arguments = start.ArgumentList.ToArray() };

        if (incompatibleMaps.Length > 0 && !isolateMaps)
            throw new InvalidDataException("Custom-map filenames exceed RA3's 47-byte usable limit. Use --isolate-incompatible-maps for reversible isolation.");
        if (incompatibleMaps.Length > 0 && isolateMaps &&
            !string.Equals(Path.GetPathRoot(mapRoot), Path.GetPathRoot(sessionRoot), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Map isolation requires --runtime-root on the same volume as --map-root so map moves and recovery remain atomic.");
        using var exclusion = LaunchExclusion.Acquire();
        LaunchPreset.EnsureGameClosed();
        SkirmishSession.EnsureNoPendingSession(profileDirectory, sessionRoot);
        bridge.VerifyInstalled(gameRoot, Value(options, "backup-root"));
        MemoryPatchCommands.ApplyUnderLease(options, gameRoot);
        if (options.ContainsKey("large-address-aware"))
        {
            bridge.VerifyInstalled(gameRoot, Value(options, "backup-root"));
        }
        cancellationToken.ThrowIfCancellationRequested();
        var stockArguments = start.ArgumentList.ToArray();
        start.Environment["RA3_AUTOSKIRMISH_SESSION_DIRECTORY"] = sessionDirectory;
        bridge.ConfigureChild(start, sessionId, profile, presetSha256);
        // The bridge may add protocol arguments/environment, but must preserve the
        // direct, vanilla image and argument list supplied above.
        if (!string.Equals(start.FileName, engine, StringComparison.OrdinalIgnoreCase) || start.UseShellExecute ||
            !string.Equals(start.WorkingDirectory, gameRoot, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(start.Arguments) ||
            !start.ArgumentList.Take(stockArguments.Length).SequenceEqual(stockArguments, StringComparer.Ordinal) ||
            (nativeMod != start.ArgumentList.Any(a => a.Equals("-modConfig", StringComparison.OrdinalIgnoreCase))) ||
            start.Environment["RA3_AUTOSKIRMISH_PROFILE_DIRECTORY"] != profileDirectory)
            throw new InvalidOperationException("The native bridge changed the native launch contract.");

        SkirmishSession? session = null;
        MapCompatibilityLease? maps = null;
        Process? process = null;
        try
        {
            session = SkirmishSession.Prepare(profileRoot, profileDirectory, sessionDirectory, sessionId, engine, presetBytes);
            session.Apply(presetBytes);
            maps = MapCompatibilityLease.Acquire(mapRoot, sessionDirectory, isolateMaps);
            ReplayPreservationLease.Acquire(sessionDirectory, replayPath);
            cancellationToken.ThrowIfCancellationRequested();
            session.MarkStarting();
            process = Process.Start(start) ?? throw new InvalidOperationException("Windows did not return an RA3 engine process.");
            session.MarkProcess(process);
            Progress("engine-started", sessionId, process.Id, session.JournalPath);
            bridge.WaitForGameplay(process, sessionId, profile, presetSha256,
                TimeSpan.FromSeconds(setupSeconds + loadSeconds), cancellationToken);
            if (process.HasExited) throw new InvalidOperationException("The owned RA3 engine exited before gameplay was confirmed.");
            session.MarkGameplay();
            Progress("match-loaded", sessionId, process.Id, session.JournalPath);
            if (testSeconds > 0)
            {
                var observation = Stopwatch.StartNew();
                while (observation.Elapsed < TimeSpan.FromSeconds(testSeconds))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (process.WaitForExit(100)) throw new InvalidOperationException("The owned RA3 engine exited during gameplay observation.");
                }
                StopOwned(process);
            }
            else while (!process.WaitForExit(200)) cancellationToken.ThrowIfCancellationRequested();
            var processId = process.Id;
            maps.Dispose(); maps = null;
            session.Restore();
            if (testSeconds == 0 && process.ExitCode != 0)
            {
                throw new InvalidOperationException($"RA3 exited with code {process.ExitCode}. Files were restored; default.ini was preserved.");
            }
            remembered.Complete(process.ExitCode == 0 || testSeconds > 0);
            Progress("session-restored", sessionId, processId, session.JournalPath);
            return new { schemaVersion = 1, status = "session-ended", mode = ModeName(nativeMod), sessionId,
                sessionDirectory, processId, gameImage = engine, configSkuDef = config, profile, profileDirectory,
                preset, presetSha256, nativePatchVerified = true, profileSelectionVerified = true,
                matchStartVerified = true, aiActionsVerified = false, profileRestored = true, mapsRestored = true, replayRestored = true, replayPath,
                recoveryJournal = session.JournalPath };
        }
        catch (Exception original)
        {
            var failures = new List<Exception> { original };
            try { StopOwned(process); } catch (Exception cleanup) { failures.Add(cleanup); }
            if (failures.Count == 1)
            {
                try { maps?.Dispose(); maps = null; } catch (Exception cleanup) { failures.Add(cleanup); }
                try { session?.Restore(); } catch (Exception cleanup) { failures.Add(cleanup); }
            }
            if (failures.Count > 1)
                throw new AggregateException($"Skirmish launch cleanup requires recovery. Session: {sessionDirectory}", failures);
            throw;
        }
        finally { process?.Dispose(); }
    }

    internal static string ResolveConfiguration(string gameRoot, string? selected)
    {
        var candidates = Directory.EnumerateFiles(gameRoot, "RA3_*_1.12.SkuDef", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFullPath).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (selected != null)
        {
            var path = Path.GetFullPath(selected);
            if (!candidates.Contains(path, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException("--config-skudef must select an installed stock RA3 language configuration for version 1.12.");
            return path;
        }
        return candidates.Length == 1 ? candidates[0] : throw new InvalidOperationException(
            candidates.Length == 0 ? "No stock RA3 1.12 language configuration was found." :
            "Multiple stock RA3 1.12 language configurations exist; choose one with --config-skudef.");
    }

    internal static void ValidateSelectedProfile(string profileRoot, string profile)
    {
        var directory = Path.Combine(profileRoot, "directory.ini");
        if (!File.Exists(directory)) throw new InvalidDataException("The profile directory.ini is missing; the active game profile cannot be established.");
        var content = File.ReadAllText(directory);
        if (content.Contains("C_00u_00r_00r_00e_00n_00t_00P_00r_00o_00f_00i_00l_00e_00", StringComparison.OrdinalIgnoreCase))
        {
            content = string.Join('\n', content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(line =>
            {
                if (!line.Contains("_00", StringComparison.OrdinalIgnoreCase)) return line;
                var unescaped = Regex.Replace(line, "_([0-9A-Fa-f]{2})", m => ((char)Convert.ToByte(m.Groups[1].Value, 16)).ToString());
                if (unescaped.Any(c => c > byte.MaxValue) || unescaped.Length % 2 != 0)
                    throw new InvalidDataException("RA3's directory.ini contains malformed encoded profile data.");
                return new UnicodeEncoding(false, false, true).GetString(unescaped.Select(c => (byte)c).ToArray());
            }));
        }
        var current = Regex.Matches(content, @"(?im)^\s*CurrentProfile\s*=\s*(.*?)\s*$");
        if (current.Count != 1 || !current[0].Groups[1].Value.Equals(profile, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("--profile must match CurrentProfile in RA3's directory.ini. Select that profile in RA3 before launching.");
    }

    private static byte[] ReadPreset(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("Skirmish preset was not found.", path);
        if (info.Length is 0 or > 16 * 1024 * 1024) throw new InvalidDataException("Skirmish preset size is invalid.");
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length is 0 or > 16 * 1024 * 1024) throw new InvalidDataException("Skirmish preset size changed while it was read.");
        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, true);
        var content = reader.ReadToEnd();
        var game = Regex.Match(content, @"(?im)^\s*Rts:GameInfo\s*=\s*(.+)$");
        if (!game.Success || !Regex.IsMatch(content, @"(?im)^\s*Rts:Map\s*=\s*\S+") ||
            !Regex.IsMatch(game.Groups[1].Value, @"(?:^|;)M=[^;]+;") ||
            !Regex.IsMatch(game.Groups[1].Value, @";RU=[^;]+;") || !Regex.IsMatch(game.Groups[1].Value, @";S=H[^;]+;"))
            throw new InvalidDataException("Supply a complete RA3 Skirmish.ini with map, rules and human/player slots.");
        return bytes;
    }

    private static void StopOwned(Process? process)
    {
        if (process == null) return;
        try
        {
            if (process.HasExited) return;
            process.Kill(entireProcessTree: true);
            if (!process.WaitForExit(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("The owned RA3 process did not stop; profile/map backups remain available for recovery.");
        }
        catch (InvalidOperationException) { }
    }

    private static void Progress(string stage, string sessionId, int processId, string recoveryJournal) =>
        ReportProgress(stage, sessionId, processId, recoveryJournal);

    private static void ReportProgress(string stage, string sessionId, int processId, string recoveryJournal)
    {
        ProgressChanged?.Invoke(stage);
        Console.Error.WriteLine(JsonSerializer.Serialize(new { schemaVersion = 1, stage, sessionId, processId, recoveryJournal }));
    }
    private static string? Value(IReadOnlyDictionary<string, string?> options, string key) => options.TryGetValue(key, out var value) ? value : null;
    private static string Required(IReadOnlyDictionary<string, string?> options, string key) =>
        !string.IsNullOrWhiteSpace(Value(options, key)) ? Value(options, key)! : throw new ArgumentException($"--{key} is required.");
    private static int Number(IReadOnlyDictionary<string, string?> options, string key, int fallback, int min, int max) =>
        Value(options, key) == null ? fallback : int.TryParse(Value(options, key), out var n) && n >= min && n <= max ? n :
        throw new ArgumentException($"--{key} must be an integer from {min} to {max}.");
    private static bool Within(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private sealed class MissingBridge : INativeSkirmishBridge
    {
        public void VerifyInstalled(string gameRoot, string? backupRoot) => throw new InvalidOperationException("A verified native skirmish package is not connected. Dry-run is available; native launch is unavailable.");
        public void ConfigureChild(ProcessStartInfo start, string sessionId, string profile, string presetSha256) => throw new InvalidOperationException("Native bridge unavailable.");
        public void WaitForGameplay(Process process, string sessionId, string profile, string presetSha256, TimeSpan timeout, CancellationToken token) => throw new InvalidOperationException("Native bridge unavailable.");
    }
}
