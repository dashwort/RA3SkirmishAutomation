using System.Diagnostics;
using System.Text.Json;

namespace RA3SkirmishLauncher.Core.Backend
{
    public sealed class BackendClient : IBackendClient
    {
        private readonly string executable;
        private readonly object activeLock = new();
        private Process? activeGameSupervisor;

        public BackendClient(string executable)
        {
            this.executable = Path.GetFullPath(executable);
        }

        public async Task<BackendResult> RunAsync(BackendRequest request, IProgress<string>? progress = null)
        {
            if (!File.Exists(executable)) { throw new FileNotFoundException("The backend is missing. Use the complete portable release.", executable); }
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
                WorkingDirectory = Path.GetDirectoryName(executable)!
            };
            foreach (var argument in request.Arguments()) { start.ArgumentList.Add(argument); }
            using var process = Process.Start(start) ?? throw new IOException("The launcher backend could not start.");
            var isGameLaunch = request.Command is "launch-skirmish" or "launch-mod-menu" or "launch-preset";
            if (isGameLaunch) { lock (activeLock) { activeGameSupervisor = process; } }
            var stdout = new List<string>();
            var stderr = new List<string>();
            try
            {
                await Task.WhenAll(ReadLines(process.StandardOutput, stdout, progress), ReadLines(process.StandardError, stderr, progress),
                    process.WaitForExitAsync()).ConfigureAwait(false);
            }
            finally
            {
                if (isGameLaunch) { lock (activeLock) { if (ReferenceEquals(activeGameSupervisor, process)) activeGameSupervisor = null; } }
            }
            var lines = process.ExitCode == 0 ? stdout : stderr.Concat(stdout).ToList();
            foreach (var line in lines.AsEnumerable().Reverse())
            {
                try
                {
                    var result = new BackendResult(process.ExitCode, line);
                    if (result.Status.Length > 0) { return result; }
                }
                catch (JsonException) { }
            }
            throw new IOException($"The backend exited with code {process.ExitCode}. " + string.Join(Environment.NewLine, stderr.TakeLast(5)));
        }

        public async Task<bool> StopActiveGameAsync()
        {
            Process? process;
            lock (activeLock) { process = activeGameSupervisor; }
            if (process is null) { return false; }
            try
            {
                if (process.HasExited) return false;
                await process.StandardInput.WriteLineAsync("stop").ConfigureAwait(false);
                await process.StandardInput.FlushAsync().ConfigureAwait(false);
                return true;
            }
            catch (IOException) { return false; }
            catch (InvalidOperationException) { return false; }
        }

        public static string? ProgressStage(string line)
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                return document.RootElement.TryGetProperty("stage", out var stage) ? stage.GetString() : null;
            }
            catch (JsonException) { return null; }
        }

        private static async Task ReadLines(StreamReader reader, List<string> lines, IProgress<string>? progress)
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                lines.Add(line);
                var stage = ProgressStage(line);
                if (stage != null) { progress?.Report(stage); }
            }
        }
    }
}
