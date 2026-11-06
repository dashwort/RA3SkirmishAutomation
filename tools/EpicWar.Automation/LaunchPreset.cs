using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EpicWar.Automation;

internal static class LaunchPreset
{
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string newFileName, string existingFileName, IntPtr securityAttributes);

    private const string DefaultGameDirectory = "Command and Conquer Red Alert 3";
    private const string DefaultModDirectory = "Red Alert 3\\Mods\\RA3 Epic War";
    private const string DefaultBaseSkuDef = "RA3 Epic War_5.4.skudef";
    private const string DefaultOverlayPattern = "Epic War 5.2-AutoSkirmish-*.big";

    public static object Capture(IReadOnlyDictionary<string, string?> options)
    {
        var profile = Required(options, "profile");
        var profileDirectory = GetProfileDirectory(profile, GetProfileRoot(options));
        var liveIni = Path.Combine(profileDirectory, "Skirmish.ini");
        ValidateSkirmishIni(liveIni);

        var name = ValidatePresetName(Value(options, "name") ?? "default");
        var output = Value(options, "output");
        var preset = Path.GetFullPath(output ?? Path.Combine(profileDirectory, "EpicWarPresets", $"{name}.ini"));
        if (PathsEqual(preset, liveIni))
            throw new InvalidOperationException("The preset output must differ from the profile's active Skirmish.ini.");
        if (File.Exists(preset) && !(options.TryGetValue("overwrite", out var overwrite) && overwrite == "true"))
            throw new IOException($"Preset already exists: {preset}. Pass --overwrite to replace it.");

        var directory = Path.GetDirectoryName(preset)!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(preset)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.Copy(liveIni, temp, overwrite: false);
            File.Move(temp, preset, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }

        return new
        {
            status = "preset-captured",
            profile,
            preset,
            bytes = new FileInfo(preset).Length,
            note = "The profile file was copied; no game settings were included in the project."
        };
    }

    public static object Launch(IReadOnlyDictionary<string, string?> options, bool useMenuOverlay, CancellationToken cancellationToken=default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var profile = Required(options, "profile");
        var profileDirectory = GetProfileDirectory(profile, GetProfileRoot(options));
        var presetName = ValidatePresetName(Value(options, "name") ?? "default");
        var presetPath = Path.GetFullPath(Value(options, "preset") ??
            Path.Combine(profileDirectory, "EpicWarPresets", $"{presetName}.ini"));
        ValidateSkirmishIni(presetPath);
        var presetBytes = RA3SkirmishLauncher.Core.Presets.PresetFiles.Read(presetPath);
        MemoryPatchCommands.RequestedState(options);

        var gameDirectory = Path.GetFullPath(Value(options, "game-root") ?? GetDefaultGameDirectory());
        var engineImage = Value(options, "engine-image");
        var directEngine = !string.IsNullOrWhiteSpace(engineImage);
        var gameExe = directEngine ? Path.GetFullPath(engineImage!) : Path.Combine(gameDirectory, "RA3.exe");
        if (!File.Exists(gameExe))
            throw new FileNotFoundException(directEngine ? "The selected RA3 engine image was not found." : "The RA3 1.12 game executable was not found.", gameExe);
        if (!directEngine && Value(options, "config-skudef") is not null)
            throw new ArgumentException("--config-skudef requires --engine-image.");
        var configSkuDef = directEngine
            ? Path.GetFullPath(Value(options, "config-skudef") ?? Path.Combine(gameDirectory, "RA3_english_1.12.SkuDef"))
            : null;
        if (configSkuDef is not null && !File.Exists(configSkuDef))
            throw new FileNotFoundException("The RA3 engine configuration SKUDEF was not found.", configSkuDef);

        var modDirectory = Path.GetFullPath(Value(options, "mod-directory") ?? GetDefaultModDirectory());
        if (!Directory.Exists(modDirectory)) throw new DirectoryNotFoundException($"Epic War install folder was not found: {modDirectory}");
        var baseSkuDef = Path.GetFullPath(Value(options, "base-skudef") ?? Path.Combine(modDirectory, DefaultBaseSkuDef));
        var baseBigNames = ReadBaseBigs(baseSkuDef);
        var baseBigs = baseBigNames.Select(name => Path.Combine(Path.GetDirectoryName(baseSkuDef)!, name)).ToArray();
        foreach (var baseBig in baseBigs)
            if (!File.Exists(baseBig)) throw new FileNotFoundException("A BIG referenced by the Epic War SKUDEF is missing.", baseBig);

        string? overlayPath = null;
        if (useMenuOverlay)
        {
            overlayPath = Path.GetFullPath(Value(options, "overlay") ?? FindOverlay(modDirectory));
            if (!File.Exists(overlayPath)) throw new FileNotFoundException("The Epic War auto-skirmish overlay is missing.", overlayPath);
            if (baseBigs.Any(baseBig => PathsEqual(baseBig, overlayPath)))
                throw new InvalidOperationException("The automation overlay cannot be one of the active Epic War BIGs.");
            if (baseBigNames.Contains(Path.GetFileName(overlayPath), StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("The base BIG and overlay must have different filenames.");
        }
        else if (Value(options, "overlay") is not null)
            throw new ArgumentException("--overlay is only supported by launch-preset.");

        var width = ParseDimension(options, "width", 1280, 800, 7680);
        var height = ParseDimension(options, "height", 720, 600, 4320);
        var dryRun = options.TryGetValue("dry-run", out var dryRunValue) && dryRunValue == "true";
        var experimental = options.TryGetValue("experimental", out var experimentalValue) && experimentalValue == "true";
        var startMatch = Value(options,"start-match") == "true";
        // Validate deadlines before backing up settings or starting a process.
        var setupSeconds=ParseDimension(options,"setup-timeout",120,10,300);
        var loadSeconds=ParseDimension(options,"load-timeout",300,10,300);
        var testSeconds=Value(options,"test-seconds")==null ? 0 : ParseDimension(options,"test-seconds",10,1,300);
        if(testSeconds>0 && !startMatch)throw new ArgumentException("--test-seconds requires the test-game command.");
        var isolateMaps = Value(options,"isolate-incompatible-maps") == "true";
        if(startMatch && !useMenuOverlay) throw new ArgumentException("Automatic Begin requires launch-game or launch-preset with the navigation overlay.");
        if (!startMatch && options.ContainsKey("remember-preset"))
        {
            throw new ArgumentException("Remembering a preset requires a verified supervised skirmish.");
        }
        var mapRoot = Path.GetFullPath(Value(options,"map-root") ?? Path.Combine(Path.GetDirectoryName(GetProfileRoot(options))!,"Maps"));
        var incompatibleMaps = startMatch ? MapCompatibilityLease.FindIncompatible(mapRoot) : [];
        if(startMatch)
        {
            var selected=Regex.Match(File.ReadAllText(presetPath),@"\bM=([^;]+);",RegexOptions.Multiline).Groups[1].Value;
            if(incompatibleMaps.Any(p=>selected.EndsWith("/"+Path.GetFileNameWithoutExtension(p),StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("The preset selects a custom map whose filename exceeds RA3's 47-byte usable limit. Select a compatible map before launching; the launcher will not substitute a different battlefield.");
        }
        if(startMatch && incompatibleMaps.Length>0 && !isolateMaps && !dryRun)
            throw new InvalidDataException("Incompatible custom-map names would crash RA3: "+string.Join(", ",incompatibleMaps.Select(Path.GetFileName))+". Use --isolate-incompatible-maps for reversible per-session isolation.");
        var setupNavigationVerified = useMenuOverlay && IsVerifiedSetupNavigationOverlay(overlayPath!);
        if (useMenuOverlay && !dryRun && !experimental && !setupNavigationVerified)
            throw new InvalidOperationException("This menu overlay is not a verified Skirmish-setup navigation package. Use the published navigation package or pass --experimental for an isolated test.");
        if(startMatch && !setupNavigationVerified) throw new InvalidOperationException("Automatic Begin requires the published, hash-verified navigation package.");
        var runtimeRoot = Path.GetFullPath(Value(options, "runtime-root") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EpicWar.Automation", "Runtime"));
        if (useMenuOverlay && IsPathWithin(runtimeRoot, modDirectory))
            throw new ArgumentException("The runtime directory must be outside the active Epic War install.");
        var runtimeDirectory = useMenuOverlay ? Path.Combine(runtimeRoot,
            $"run-{DateTime.UtcNow:yyyyMMddTHHmmssfff}-{Guid.NewGuid():N}") : null;
        var remembered = RememberedPreset.Prepare(options, presetBytes, gameDirectory, GetProfileRoot(options), runtimeRoot);
        // Preserve the canonical base SKUDEF basename: RA3 parses its version
        // from this name, so diagnostic suffixes can abort startup.
        var launchSkuDef = useMenuOverlay
            ? Path.Combine(runtimeDirectory!, Path.GetFileName(baseSkuDef))
            : baseSkuDef;
        if(useMenuOverlay)
            ValidateRuntimePaths(baseBigs.Select(p=>Path.Combine(runtimeDirectory!,Path.GetFileName(p)))
                .Append(Path.Combine(runtimeDirectory!,Path.GetFileName(overlayPath!))).Append(launchSkuDef));
        var skuDefContents = useMenuOverlay ? string.Join(Environment.NewLine,
            new[] { "mod-game 1.12" }
                .Concat(baseBigNames.Select(name => $"add-big {name}"))
                .Append($"add-big {Path.GetFileName(overlayPath)}")
                .Append("")) : null;

        var arguments = directEngine
            ? new[]
            {
                "-config", configSkuDef!,
                "-modConfig", launchSkuDef,
                "-win",
                "-xres", width.ToString(),
                "-yres", height.ToString()
            }
            : new[]
            {
                "-runver", "1.12",
                "-modConfig", launchSkuDef,
                "-win",
                "-xres", width.ToString(),
                "-yres", height.ToString()
            };
        var commandLine = string.Join(" ", new[] { gameExe }.Concat(arguments).Select(QuoteArgument));

        if (dryRun)
        {
            return new
            {
                status = "dry-run",
                preset = presetPath,
                profileDirectory,
                mapRoot,
                gameDirectory,
                modDirectory,
                baseBigs,
                overlay = overlayPath,
                setupNavigationPackageVerified = setupNavigationVerified,
                matchStartVerified = false,
                automaticBegin = startMatch,
                testSeconds,
                incompatibleMaps,
                mapIsolationRequested = isolateMaps,
                profileRestoredOnExit = startMatch,
                gameImage = gameExe,
                configSkuDef,
                launchMechanism = directEngine ? "direct-engine-image" : "ra3-launcher",
                runtimeDirectory,
                launchSkuDef,
                mode = useMenuOverlay ? (setupNavigationVerified ? "skirmish-setup-navigation" : "experimental-menu-overlay") : "base-menu",
                command = commandLine,
                arguments
            };
        }

        using var exclusion = LaunchExclusion.Acquire();
        EnsureGameClosed();
        MemoryPatchCommands.ApplyUnderLease(options, gameDirectory);
        var backup = BackupCurrentProfile(profileDirectory);
        Process? launcher = null;
        Process? gameProcess = null;
        MapCompatibilityLease? maps = null;
        var evidenceDirectory=startMatch ? Path.GetFullPath(Value(options,"evidence-root") ?? Path.Combine(runtimeDirectory!,"evidence")) : null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            RA3SkirmishLauncher.Core.Presets.PresetFiles.WriteAtomic(Path.Combine(profileDirectory, "Skirmish.ini"), presetBytes, true);
            if (useMenuOverlay)
                StageRuntime(runtimeDirectory!, baseBigs, overlayPath!, launchSkuDef, skuDefContents!);
            if(startMatch) maps=MapCompatibilityLease.Acquire(mapRoot,runtimeDirectory!,isolateMaps);
            if(startMatch)
            {
                Directory.CreateDirectory(evidenceDirectory!);
                File.WriteAllText(Path.Combine(evidenceDirectory!,"launch.json"),JsonSerializer.Serialize(new {
                    status="starting",gameImage=gameExe,profile,preset=presetPath,profileBackup=backup,mapRoot,runtimeDirectory,
                    mapRecoveryJournal=Path.Combine(runtimeDirectory!,"map-restoration.json"),command=commandLine,arguments}));
            }
            var startInfo = new ProcessStartInfo
            {
                FileName = gameExe,
                WorkingDirectory = gameDirectory,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Normal
            };
            foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

            try
            {
                launcher = Process.Start(startInfo)
                    ?? throw new InvalidOperationException("Windows did not return an RA3 process.");
            }
            catch (Win32Exception error)
            {
                // Keep the native code so a host process-creation failure can
                // be distinguished from a crash after the game has opened.
                    throw new Win32Exception(error.NativeErrorCode,
                    $"Windows could not create the selected RA3 image (Win32 error {error.NativeErrorCode}); no game process was returned. " +
                    (error.NativeErrorCode==623 ? "Run the launcher directly from an ordinary Windows session outside a restricted agent sandbox. Do not replace system DLLs. " : "") + error.Message);
            }
            gameProcess = ProcessOwnership.WaitForEngine(launcher,directEngine ? gameExe : Path.Combine(gameDirectory,"Data","RA3_1.12.game"),TimeSpan.FromSeconds(60),cancellationToken);
            var gameProcessId = gameProcess.Id;
            var engineImagePath=gameProcess.MainModule?.FileName;
            if(startMatch)
            {
                ForegroundGame.Log("engine-started",gameProcessId);
                File.WriteAllText(Path.Combine(evidenceDirectory!,"session.json"),JsonSerializer.Serialize(new {
                    status="starting",processId=gameProcessId,launcherProcessId=launcher.Id,engineImage=engineImagePath,
                    profileBackup=backup,runtimeDirectory}));
                var verification=ForegroundGame.StartMatch(gameProcess,evidenceDirectory!,
                    setupSeconds,loadSeconds,cancellationToken);
                File.WriteAllText(Path.Combine(evidenceDirectory!,"session.json"),JsonSerializer.Serialize(new {processId=gameProcessId,
                    launcherProcessId=launcher.Id,gameImage=gameExe,engineImage=engineImagePath,
                    profile,preset=presetPath,profileBackup=backup,mapRoot,runtimeDirectory,command=commandLine,arguments,verification}));
                // Keep the supervisor alive so temporarily held maps cannot be
                // restored while the engine is still using its map catalogue.
                if(testSeconds>0)
                {
                    ForegroundGame.ObserveGameplay(gameProcess,evidenceDirectory!,testSeconds,cancellationToken);
                    StopStartedProcess(gameProcess);
                    StopStartedProcess(launcher);
                    ForegroundGame.Log("test-owned-game-stopped",gameProcessId);
                }
                else while(!gameProcess.WaitForExit(500)) cancellationToken.ThrowIfCancellationRequested();
                RestoreBackup(backup,Path.Combine(profileDirectory,"Skirmish.ini"));
                maps!.Dispose(); maps=null;
                if (testSeconds == 0 && gameProcess.ExitCode != 0)
                {
                    throw new InvalidOperationException($"RA3 exited with code {gameProcess.ExitCode}. Files were restored; default.ini was preserved.");
                }
                remembered.Complete(gameProcess.ExitCode == 0 || testSeconds > 0);
                ForegroundGame.Log("session-restored",gameProcessId);
                return new {status="session-ended",processId=gameProcessId,setupNavigationPackageVerified=setupNavigationVerified,
                    matchStartVerified=true,aiActionsVerified=false,profileRestored=true,mapsRestored=true,evidenceDirectory,
                    gameImage=gameExe,engineImage=engineImagePath,profile,preset=presetPath,runtimeDirectory,testSeconds,verification};
            }
            var stableSeconds = useMenuOverlay ? 60 : 10;
            var observedSeconds = WaitForGameWindow(gameProcess, TimeSpan.FromSeconds(120), stableSeconds);
            return new
            {
                status = "game-window-running",
                processId = gameProcessId,
                launcherProcessId = launcher.Id,
                observedSeconds,
                windowOpened = gameProcess.MainWindowHandle != IntPtr.Zero,
                windowTitle = gameProcess.MainWindowTitle,
                setupNavigationPackageVerified = setupNavigationVerified,
                matchStartVerified = false,
                gameImage = gameExe,
                configSkuDef,
                launchMechanism = directEngine ? "direct-engine-image" : "ra3-launcher",
                profile,
                preset = presetPath,
                profileBackup = backup,
                launchSkuDef,
                runtimeDirectory,
                mode = useMenuOverlay ? (setupNavigationVerified ? "skirmish-setup-navigation" : "experimental-menu-overlay") : "base-menu",
                command = commandLine,
                note = useMenuOverlay
                    ? setupNavigationVerified
                        ? "The published navigation package opens the configured Skirmish setup. This run confirms RA3 stayed open; it does not verify a match load."
                        : "RA3 remained running for 60 seconds after its window opened. This does not verify that the Skirmish setup or a match loaded."
                    : "Epic War is running with the selected skirmish settings. Start the skirmish through the game menu."
            };
        }
        catch(Exception original)
        {
            var failure = PreferStopAfterFocusLoss(original, cancellationToken);
            var failures=new List<Exception>{failure};
            foreach(var owned in new[]{gameProcess,launcher})
                try { StopStartedProcess(owned); } catch(Exception cleanupError) { failures.Add(cleanupError); }
            if(failures.Count==1)
            {
                try { RestoreBackup(backup, Path.Combine(profileDirectory, "Skirmish.ini")); }
                catch(Exception cleanupError) { failures.Add(cleanupError); }
                try { maps?.Dispose(); maps=null; }
                catch(Exception cleanupError) { failures.Add(cleanupError); }
            }
            if(failures.Count>1) throw new AggregateException("Launcher failed; inspect retained profile backups and map-restoration.json before recovery.",failures);
            if (!ReferenceEquals(failure, original)) throw failure;
            throw;
        }
        finally
        {
            gameProcess?.Dispose();
            launcher?.Dispose();
        }
    }

    internal static Exception PreferStopAfterFocusLoss(Exception failure, CancellationToken cancellationToken)
    {
        if (failure is GameFocusLostException && cancellationToken.CanBeCanceled &&
            cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(1)))
            return new OperationCanceledException("Game stopped by launcher.", failure, cancellationToken);
        return failure;
    }

    private static string GetProfileRoot(IReadOnlyDictionary<string, string?> options) => Path.GetFullPath(
        Value(options, "profile-root") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Red Alert 3", "Profiles"));

    private static string GetProfileDirectory(string profile, string profileRoot)
    {
        if (string.IsNullOrWhiteSpace(profile) || profile is "." or ".." ||
            profile.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || profile.Contains('/') || profile.Contains('\\'))
            throw new ArgumentException("Profile must be a single existing RA3 profile folder name.");
        var directory = Path.GetFullPath(Path.Combine(profileRoot, profile));
        var prefix = Path.GetFullPath(profileRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!directory.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Profile path must remain inside the selected profile root.");
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException($"RA3 profile was not found: {directory}");
        return directory;
    }

    private static string GetDefaultGameDirectory()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (string.IsNullOrWhiteSpace(root)) throw new InvalidOperationException("Could not resolve Program Files (x86); pass --game-root.");
        return Path.Combine(root, "Steam", "steamapps", "common", DefaultGameDirectory);
    }

    private static string GetDefaultModDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), DefaultModDirectory);

    private static string[] ReadBaseBigs(string skuDefPath)
    {
        if (!File.Exists(skuDefPath)) throw new FileNotFoundException("The Epic War base SKUDEF was not found.", skuDefPath);
        var lines = File.ReadAllLines(skuDefPath);
        if (!lines.Any(line => line.Trim().Equals("mod-game 1.12", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The Epic War base SKUDEF does not target RA3 1.12.");
        var entries = lines.Select(line => Regex.Match(line, @"^\s*add-big\s+(.+?\.big)\s*$", RegexOptions.IgnoreCase))
            .Where(match => match.Success)
            .Select(match => match.Groups[1].Value.Trim())
            .ToArray();
        if (entries.Length == 0) throw new InvalidDataException("The Epic War base SKUDEF must reference at least one base BIG.");
        foreach (var name in entries)
            if (Path.GetFileName(name) != name || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidDataException("Each base BIG reference must be a filename in the SKUDEF folder.");
        if (entries.Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Length)
            throw new InvalidDataException("The Epic War base SKUDEF contains duplicate BIG references.");
        return entries;
    }

    private static string FindOverlay(string modDirectory)
    {
        var artifactDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "AutoSkirmish"));
        var releasePath = Path.Combine(artifactDirectory, "release.json");
        if (File.Exists(releasePath))
        {
            using var release = JsonDocument.Parse(File.ReadAllText(releasePath));
            var listedPath = release.RootElement.GetProperty("bigFile").GetString()
                ?? throw new InvalidDataException("The auto-skirmish release does not name a BIG.");
            var fileName = Path.GetFileName(listedPath);
            if (!fileName.StartsWith("Epic War 5.2-AutoSkirmish-", StringComparison.OrdinalIgnoreCase) ||
                !fileName.EndsWith(".big", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The auto-skirmish release names an unexpected BIG.");
            var selected = Path.GetFullPath(Path.Combine(artifactDirectory, listedPath));
            if (!IsPathWithin(selected, artifactDirectory))
                throw new InvalidDataException("The auto-skirmish release BIG must be inside the published artifact folder.");
            if (!File.Exists(selected)) throw new FileNotFoundException("The published auto-skirmish BIG is missing.", selected);
            var expectedHash = release.RootElement.GetProperty("sha256").GetString();
            using var stream = File.OpenRead(selected);
            var actualHash = Convert.ToHexString(SHA256.HashData(stream));
            if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The published auto-skirmish BIG hash does not match release.json: {selected}");
            return selected;
        }
        var overlays = new[] { artifactDirectory, modDirectory }
            .Where(Directory.Exists)
            .SelectMany(directory => Directory.GetFiles(directory, DefaultOverlayPattern, SearchOption.TopDirectoryOnly))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return overlays.Length switch
        {
            1 => overlays[0],
            0 => BundledNavigation.Extract(),
            _ => throw new InvalidOperationException("More than one auto-skirmish overlay was found; pass --overlay explicitly.")
        };
    }

    private static bool IsVerifiedSetupNavigationOverlay(string overlayPath)
    {
        if(BundledNavigation.Matches(overlayPath))return true;
        var artifactDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "AutoSkirmish"));
        var releasePath = Path.Combine(artifactDirectory, "release.json");
        if (!File.Exists(releasePath)) return false;

        using var release = JsonDocument.Parse(File.ReadAllText(releasePath));
        var root = release.RootElement;
        if (!root.TryGetProperty("navigationOnly", out var navigationOnly) || !navigationOnly.GetBoolean() ||
            !root.TryGetProperty("setupNavigationVerified", out var setupVerified) || !setupVerified.GetBoolean())
            return false;

        var listedPath = root.GetProperty("bigFile").GetString();
        if (string.IsNullOrWhiteSpace(listedPath)) return false;
        var selected = Path.GetFullPath(Path.Combine(artifactDirectory, listedPath));
        if (!IsPathWithin(selected, artifactDirectory) || !PathsEqual(selected, overlayPath)) return false;

        var expectedHash = root.GetProperty("sha256").GetString();
        if (string.IsNullOrWhiteSpace(expectedHash)) return false;
        using var stream = File.OpenRead(selected);
        var actualHash = Convert.ToHexString(SHA256.HashData(stream));
        return string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase);
    }

    internal static void ValidateRuntimePaths(IEnumerable<string> paths)
    {
        foreach(var path in paths)
        {
            var full=Path.GetFullPath(path);
            if(full.Length>=260 || full.StartsWith(@"\\?\",StringComparison.Ordinal))
                throw new ArgumentException($"RA3 cannot safely read this staged path ({full.Length} characters): {full}. Choose a shorter --runtime-root; no profile or map changes were made.");
        }
    }

    private static void StageRuntime(string directory, IReadOnlyList<string> baseBigs, string overlay,
        string skuDef, string skuDefContents)
    {
        if (Directory.Exists(directory)) throw new IOException($"Runtime directory already exists: {directory}");
        Directory.CreateDirectory(directory);
        try
        {
            foreach (var baseBig in baseBigs)
                LinkOrCopy(baseBig, Path.Combine(directory, Path.GetFileName(baseBig)));
            LinkOrCopy(overlay, Path.Combine(directory, Path.GetFileName(overlay)));
            WriteTextAtomically(skuDef, skuDefContents);
        }
        catch
        {
            foreach (var name in baseBigs.Select(path => Path.GetFileName(path)!)
                .Append(Path.GetFileName(overlay)!).Append(Path.GetFileName(skuDef)!))
            {
                var staged = Path.Combine(directory, name);
                if (File.Exists(staged)) File.Delete(staged);
            }
            Directory.Delete(directory);
            throw;
        }
    }

    private static void LinkOrCopy(string source, string destination)
    {
        if (CreateHardLink(destination, source, IntPtr.Zero)) return;
        var code = Marshal.GetLastWin32Error();
        try
        {
            File.Copy(source, destination, overwrite: false);
        }
        catch (Exception copyError) when (copyError is IOException or UnauthorizedAccessException)
        {
            throw new IOException(
                $"Could not hard-link {source} into {destination} (Win32 error {code}) or copy it.", copyError);
        }
    }

    private static bool IsPathWithin(string candidate, string parent)
    {
        var fullParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullCandidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullCandidate.StartsWith(fullParent, StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateSkirmishIni(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Skirmish preset was not found.", path);
        var info = new FileInfo(path);
        if (info.Length is 0 or > 16 * 1024 * 1024) throw new InvalidDataException("Skirmish preset size is invalid.");
        var content = File.ReadAllText(path);
        var infoLine=Regex.Match(content,@"(?im)^\s*Rts:GameInfo\s*=\s*(.+)$");
        if (!infoLine.Success || !Regex.IsMatch(content,@"(?im)^\s*Rts:Map\s*=\s*\S+") ||
            !Regex.IsMatch(infoLine.Groups[1].Value,@"(?:^|;)M=[^;]+;") ||
            !Regex.IsMatch(infoLine.Groups[1].Value,@";RU=[^;]+;") ||
            !Regex.IsMatch(infoLine.Groups[1].Value,@";S=H[^;]+;"))
            throw new InvalidDataException("Choose a complete RA3 Skirmish.ini containing Rts:Map and Rts:GameInfo with map, rules and human/player slots.");
    }

    private static string BackupCurrentProfile(string profileDirectory)
    {
        var current = Path.Combine(profileDirectory, "Skirmish.ini");
        if (!File.Exists(current)) return "";
        var backupDirectory = Path.Combine(profileDirectory, "EpicWarBackups");
        Directory.CreateDirectory(backupDirectory);
        var backup = Path.Combine(backupDirectory,
            $"Skirmish-{DateTime.UtcNow:yyyyMMddTHHmmssfff}-{Guid.NewGuid():N}.ini");
        File.Copy(current, backup, overwrite: false);
        return backup;
    }

    private static void ReplaceAtomically(string source, string destination)
    {
        var temp = destination + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.Copy(source, temp, overwrite: false);
            File.Move(temp, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static void WriteTextAtomically(string destination, string content)
    {
        var temp = destination + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temp, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temp, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static void RestoreBackup(string backup, string destination)
    {
        if (backup.Length > 0 && File.Exists(backup)) ReplaceAtomically(backup, destination);
        else if (File.Exists(destination)) File.Delete(destination);
    }

    internal static void EnsureGameClosed()
    {
        var running = new List<Process>();
        foreach (var process in Process.GetProcesses())
        {
            if (IsGameOrLauncherProcess(process)) running.Add(process);
            else process.Dispose();
        }
        foreach (var process in running) process.Dispose();
        if (running.Count > 0) throw new InvalidOperationException("RA3 1.12 is already running; close it before applying a Skirmish.ini preset.");
    }

    private static Process WaitForGameProcess(Process launcher, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            foreach (var process in Process.GetProcesses())
            {
                if (IsGameProcess(process)) return process;
                process.Dispose();
            }
            Thread.Sleep(250);
        }
        var exit = launcher.HasExited ? $" Launcher exit code: {launcher.ExitCode}." : " The RA3 launcher is still running.";
        throw new TimeoutException($"RA3.exe did not start ra3_1.12.game within {timeout.TotalSeconds:0} seconds." + exit);
    }

    private static double WaitForGameWindow(Process gameProcess, TimeSpan timeout, int stableSeconds)
    {
        var timer = Stopwatch.StartNew();
        TimeSpan? readySince = null;
        while (timer.Elapsed < timeout)
        {
            if (gameProcess.HasExited)
                throw new InvalidOperationException($"RA3 1.12 exited during startup{ExitCodeDetail(gameProcess)}.");
            gameProcess.Refresh();
            if (gameProcess.MainWindowTitle.Contains("DirectX Error", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("RA3 displayed a DirectX error during startup; the skirmish did not load.");
            if (gameProcess.MainWindowTitle.Contains("Game crash", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("RA3 displayed a game-crash dialog while loading the preset; the skirmish did not load.");
            // The DirectX error also has a window; a populated game process
            // is needed before reporting startup as successful.
            if (gameProcess.MainWindowHandle != IntPtr.Zero && gameProcess.PrivateMemorySize64 > 500L * 1024 * 1024)
            {
                readySince ??= timer.Elapsed;
                if (timer.Elapsed - readySince >= TimeSpan.FromSeconds(stableSeconds))
                    return Math.Round(timer.Elapsed.TotalSeconds, 1);
            }
            else readySince = null;
            Thread.Sleep(500);
        }
        throw new TimeoutException($"RA3 did not reach its game window within {timeout.TotalSeconds:0} seconds.");
    }

    private static string ExitCodeDetail(Process process)
    {
        // A discovered process can exit before .NET acquires its handle.
        // Keep the original startup failure even when its exit code is unavailable.
        try { return $" (exit code {process.ExitCode})"; }
        catch (InvalidOperationException) { return " (exit code unavailable)"; }
        catch (Win32Exception) { return " (exit code unavailable)"; }
    }

    private static void StopStartedProcess(Process? process)
    {
        if (process is null) return;
        try
        {
            if (process.HasExited) return;
            process.Kill(entireProcessTree: true);
            if(!process.WaitForExit(TimeSpan.FromSeconds(10)))throw new TimeoutException("Owned RA3 process did not stop; user files were left backed up for recovery.");
        }
        catch (InvalidOperationException) { }
    }

    private static bool IsGameOrLauncherProcess(Process process) => IsGameProcess(process) ||
        GetProcessName(process).Equals("ra3_1.13.game", StringComparison.OrdinalIgnoreCase) ||
        GetProcessName(process).Equals("RA3", StringComparison.OrdinalIgnoreCase);

    private static bool IsGameProcess(Process process)
    {
        var name = GetProcessName(process);
        return name.Equals("ra3_1.12", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("ra3_1.12.game", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetProcessName(Process process)
    {
        try { return process.ProcessName; }
        catch { return ""; }
    }

    private static string ValidatePresetName(string name)
    {
        if (name.Length is 0 or > 64 || name != name.Trim() || !Regex.IsMatch(name, @"^[A-Za-z0-9][A-Za-z0-9 _-]*$"))
            throw new ArgumentException("Preset name may contain letters, digits, spaces, underscores, and hyphens.");
        return name;
    }

    private static int ParseDimension(IReadOnlyDictionary<string, string?> options, string key, int fallback, int minimum, int maximum)
    {
        var text = Value(options, key);
        if (text is null) return fallback;
        if (!int.TryParse(text, out var value) || value < minimum || value > maximum)
            throw new ArgumentException($"--{key} must be between {minimum} and {maximum}.");
        return value;
    }

    private static string Required(IReadOnlyDictionary<string, string?> options, string key) =>
        Value(options, key) ?? throw new ArgumentException($"Missing required option --{key}.");

    private static string? Value(IReadOnlyDictionary<string, string?> options, string key) =>
        options.TryGetValue(key, out var value) ? value : null;

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    private static string QuoteArgument(string argument)
    {
        if (argument.Length == 0) return "\"\"";
        if (!argument.Any(char.IsWhiteSpace) && !argument.Contains('"')) return argument;
        var builder = new StringBuilder().Append('"');
        var backslashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }
            if (character == '"')
            {
                builder.Append('\\', backslashes * 2 + 1).Append('"');
                backslashes = 0;
                continue;
            }
            builder.Append('\\', backslashes).Append(character);
            backslashes = 0;
        }
        builder.Append('\\', backslashes * 2).Append('"');
        return builder.ToString();
    }
}
