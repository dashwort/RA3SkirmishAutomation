namespace RA3SkirmishLauncher.Core.Backend
{
    public interface IBackendClient
    {
        Task<BackendResult> RunAsync(BackendRequest request, IProgress<string>? progress = null);
        Task<bool> StopActiveGameAsync();
    }
}
