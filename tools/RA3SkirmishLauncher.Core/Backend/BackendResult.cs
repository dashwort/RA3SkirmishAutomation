using System.Text.Json;

namespace RA3SkirmishLauncher.Core.Backend
{
    public sealed class BackendResult
    {
        public BackendResult(int exitCode, string json)
        {
            ExitCode = exitCode;
            using var document = JsonDocument.Parse(json);
            Data = document.RootElement.Clone();
        }

        public int ExitCode { get; }
        public JsonElement Data { get; }
        public bool Success => ExitCode == 0;
        public string Status => Data.TryGetProperty("status", out var value) ? value.GetString() ?? "" : "";
        public string Message => Data.TryGetProperty("message", out var value) ? value.GetString() ?? "" : Status;
    }
}
