using System.Diagnostics;
using RA3SkirmishLauncher.Core.Mods;

namespace EpicWar.Automation
{
    internal static class ModMenuLaunch
    {
        public static object Launch(IReadOnlyDictionary<string, string?> options, CancellationToken cancellationToken = default)
        {
            var gameRoot = NativePatchCommands.ResolveGameRoot(options.GetValueOrDefault("game-root"));
            var skuDef = Path.GetFullPath(options.GetValueOrDefault("base-skudef") ??
                throw new ArgumentException("Choose a mod SKUDEF."));
            ModDiscovery.Validate(skuDef);
            MemoryPatchCommands.RequestedState(options);
            var engine = NativePatchCommands.EnginePath(gameRoot);
            if (!File.Exists(engine)) { throw new FileNotFoundException("The RA3 1.12 engine is missing.", engine); }
            var config = VanillaSkirmish.ResolveConfiguration(gameRoot, null);
            var start = new ProcessStartInfo(engine) { WorkingDirectory = gameRoot, UseShellExecute = false };
            foreach (var argument in new[] { "-config", config, "-modConfig", skuDef }) { start.ArgumentList.Add(argument); }
            NativeRuntimeDependencies.PrependSearchDirectory(start, Path.Combine(AppContext.BaseDirectory, "dependencies", "x86"));
            // A normal mod launch must never inherit a native automation request.
            start.Environment.Remove("EPICWAR_NATIVE_SKIRMISH");
            start.Environment.Remove("EPICWAR_NATIVE_SESSION");
            if (options.GetValueOrDefault("dry-run") == "true")
            {
                return new { status = "dry-run", mode = "mod-menu", gameImage = engine, arguments = start.ArgumentList.ToArray() };
            }
            InteractiveDesktop.EnsureCurrentSessionReady();
            using var exclusion = LaunchExclusion.Acquire();
            LaunchPreset.EnsureGameClosed();
            MemoryPatchCommands.ApplyUnderLease(options, gameRoot);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Windows did not start RA3.");
            ForegroundGame.Log("mod-menu-running", process.Id);
            try
            {
                while (!process.WaitForExit(250)) cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(); }
                throw;
            }
            if (process.ExitCode != 0) { throw new InvalidOperationException($"The mod exited with code {process.ExitCode}."); }
            return new { status = "session-ended", mode = "mod-menu", matchStartVerified = false, processId = process.Id };
        }
    }
}
