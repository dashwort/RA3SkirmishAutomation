using System.Collections.ObjectModel;
using RA3SkirmishLauncher.Core.Backend;
using RA3SkirmishLauncher.Core.Mods;
using RA3SkirmishLauncher.Core.Presets;
using RA3SkirmishLauncher.Core.Settings;

namespace RA3SkirmishLauncher.Core.Presentation
{
    public sealed class LaunchViewModel : ObservableModel
    {
        private readonly IBackendClient backend;
        private readonly ISettingsStore store;
        private readonly ISageSupportManager sageSupport;
        private readonly ILauncherPaths paths;
        private readonly IModDiscovery discovery;
        private GameChoice? selectedGame;
        private bool busy;
        private bool memoryEnabled;
        private bool engineKnown;
        private bool nativeReady;
        private bool settingsDirty;
        private bool launchActive;
        private bool stopRequested;
        private string status = "Choose a game and preset, then launch.";
        private string engineStatus = "Checking the installation…";

        public LaunchViewModel(IBackendClient backend, ISettingsStore store, ISageSupportManager sageSupport, ILauncherPaths paths,
            IModDiscovery discovery, IPresetLibrary library)
        {
            this.backend = backend;
            this.store = store;
            this.sageSupport = sageSupport;
            this.paths = paths;
            this.discovery = discovery;
            Settings = new SettingsViewModel();
            Presets = new PresetsViewModel(library, Settings);
            LaunchCommand = new AsyncCommand(LaunchAsync, () => CanLaunch);
            StopGameCommand = new AsyncCommand(StopGameAsync, () => CanStopGame);
            Settings.PropertyChanged += (_, _) =>
            {
                settingsDirty = true;
                UpdateAvailability();
            };
            Presets.PropertyChanged += (_, _) => UpdateAvailability();
        }

        public SettingsViewModel Settings { get; }
        public PresetsViewModel Presets { get; }
        public ObservableCollection<GameChoice> Games { get; } = [];
        public ObservableCollection<string> Profiles { get; } = [];
        public AsyncCommand LaunchCommand { get; }
        public AsyncCommand StopGameCommand { get; }
        public GameChoice? SelectedGame
        {
            get => selectedGame;
            set { if (Set(ref selectedGame, value)) { Notify(nameof(RouteDescription)); UpdateAvailability(); } }
        }
        public bool IsBusy { get => busy; private set { Set(ref busy, value); UpdateAvailability(); } }
        public bool MemoryEnabled { get => memoryEnabled; set => Set(ref memoryEnabled, value); }
        public bool CanConfigure => !IsBusy;
        public bool CanUsePresets => !IsBusy && SelectedGame?.SupportsPresets == true;
        public bool CanChangeMemory => !IsBusy && engineKnown && !settingsDirty;
        private bool RequiresNativeSupport => SelectedGame?.Route is LaunchRoute.NativeSkirmish or LaunchRoute.EpicWarSkirmish;
        public bool CanLaunch => !IsBusy && !settingsDirty && SelectedGame != null && Settings.GameRoot.Length > 0 &&
            (!SelectedGame.SupportsPresets || (Presets.Selected != null && Settings.Profile.Length > 0)) &&
            (!RequiresNativeSupport || nativeReady);
        public bool CanStopGame => IsBusy && launchActive && !stopRequested;
        public string RouteDescription => SelectedGame?.Description ?? "Select a game or mod.";
        public string Status { get => status; private set => Set(ref status, value); }
        public string EngineStatus { get => engineStatus; private set => Set(ref engineStatus, value); }

        public async Task InitializeAsync()
        {
            await RunOperationAsync(async () =>
            {
                var settings = store.Load();
                Settings.Apply(settings);
                Profiles.Clear();
                foreach (var profile in paths.Profiles()) { Profiles.Add(profile); }
                Presets.Initialize(settings.PreviousPreset, Path.Combine(paths.ApplicationDirectory, "Presets", "Allies.ini"));
                RefreshMods(settings.SelectedGameId);
                await InspectEngineAsync();
                settingsDirty = false;
                Status = "Ready. Choose a game and preset.";
            });
        }

        public async Task ApplySettingsAsync()
        {
            await RunOperationAsync(async () =>
            {
                var settings = Settings.Snapshot(SelectedGame?.Id ?? "base-game");
                var previous = store.Load();
                var sageStatus = sageSupport.Apply(settings.GameRoot, settings.HighFrameRateEnabled);
                try { store.Save(settings); }
                catch (Exception saveError)
                {
                    try { sageSupport.Apply(previous.GameRoot, previous.HighFrameRateEnabled); }
                    catch (Exception rollbackError)
                    {
                        throw new AggregateException("Settings could not be saved and SAGE settings could not be restored.", saveError, rollbackError);
                    }
                    throw;
                }
                Presets.Initialize(null, Path.Combine(paths.ApplicationDirectory, "Presets", "Allies.ini"));
                RefreshMods(settings.SelectedGameId);
                await InspectEngineAsync();
                settingsDirty = false;
                Status = "Settings saved. " + sageStatus;
            });
        }

        public async Task PatchAsync(string action)
        {
            await RunOperationAsync(async () =>
            {
                var result = await backend.RunAsync(new BackendRequest("native-patch", PatchOptions(), action));
                if (!result.Success) { throw new InvalidOperationException(result.Message); }
                await InspectEngineAsync();
                Status = action == "status" ? EngineStatus : "Engine operation completed. " + EngineStatus;
            });
        }

        public void RefreshPresets()
        {
            if (IsBusy) { return; }
            try { Presets.Refresh(); }
            catch (Exception error) { Status = error.Message; }
        }

        public void ImportPreset(string source, string name, bool overwrite)
        {
            if (!CanUsePresets) { return; }
            Presets.Import(source, name, overwrite);
            Status = "Preset saved. default.ini will update after a successful skirmish.";
        }

        public void ReportError(string message) => Status = message;

        private async Task LaunchAsync()
        {
            await RunOperationAsync(async () =>
            {
                stopRequested = false;
                var choice = SelectedGame ?? throw new InvalidOperationException("Select a game.");
                var settings = Settings.Snapshot(choice.Id);
                store.Save(settings);
                var request = LaunchRequests.Create(choice, settings, Presets.Selected?.Path, engineKnown ? MemoryEnabled : null);
                Status = "Checking the installation and starting RA3…";
                var progress = new Progress<string>(ReportProgress);
                launchActive = true;
                UpdateAvailability();
                BackendResult result;
                try { result = await backend.RunAsync(request, progress); }
                finally { launchActive = false; UpdateAvailability(); }
                if (stopRequested && result.ExitCode == 130)
                {
                    Status = "Game stopped. Restoration completed.";
                    return;
                }
                if (!result.Success) { throw new InvalidOperationException(result.Message); }
                await InspectEngineAsync();
                Presets.Refresh();
                Status = choice.SupportsPresets
                    ? "Game closed. Restoration completed and default.ini was saved."
                    : "Mod closed. Your saved presets were unchanged.";
            });
        }

        private async Task StopGameAsync()
        {
            stopRequested = true;
            UpdateAvailability();
            try
            {
                if (await backend.StopActiveGameAsync())
                {
                    Status = "Stopping RA3 and restoring your files…";
                    return;
                }
            }
            catch (InvalidOperationException) { }
            catch (IOException) { }
            stopRequested = false;
            UpdateAvailability();
            Status = "The game session is already finishing.";
        }

        private async Task InspectEngineAsync()
        {
            engineKnown = false;
            nativeReady = false;
            var result = await backend.RunAsync(new BackendRequest("memory-patch", PatchOptions(), "status"));
            if (!result.Success) { EngineStatus = result.Message; return; }
            var transaction = result.Data.GetProperty("transaction");
            var state = transaction.GetProperty("State").GetString();
            engineKnown = state is "original" or "installed" or "original-large-address-aware";
            MemoryEnabled = transaction.GetProperty("LargeAddressAware").GetBoolean();
            nativeReady = transaction.GetProperty("NativeInstalled").GetBoolean() &&
                transaction.GetProperty("BackupVerified").GetBoolean() &&
                transaction.GetProperty("TransactionState").GetString() == "installed";
            EngineStatus = !engineKnown ? "Select a supported RA3 1.12 installation. 4 GB changes are unavailable."
                : nativeReady ? "Native skirmish support is installed."
                : "Native skirmish support needs installation or recovery. Open Settings.";
            UpdateAvailability();
        }

        private Dictionary<string, string?> PatchOptions() => new()
        {
            ["game-root"] = Settings.GameRoot, ["backup-root"] = Settings.BackupDirectory
        };

        private void RefreshMods(string selectedId)
        {
            var roots = Settings.ExtraModLocations.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Concat([paths.ModDirectory, paths.ApplicationDirectory]);
            Games.Clear();
            foreach (var choice in discovery.Discover(roots)) { Games.Add(choice); }
            SelectedGame = Games.FirstOrDefault(choice => choice.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase)) ?? Games.First();
        }

        private async Task RunOperationAsync(Func<Task> operation)
        {
            if (IsBusy) { return; }
            IsBusy = true;
            try { await operation(); }
            catch (Exception error) { Status = error.Message; }
            finally { IsBusy = false; }
        }

        private void ReportProgress(string stage)
        {
            if (!IsBusy) { return; }
            Status = stage switch
            {
                "engine-started" => "RA3 started. Waiting for the skirmish…",
                "native-preset-verified" => "Preset verified. Starting the match…",
                "begin-sent" => "Setup recognized. Waiting for the match…",
                "match-loaded" => "Match loaded. Keep the launcher open until RA3 closes.",
                "mod-menu-running" => "Mod running. Close RA3 to return to the launcher.",
                "session-restored" => "Restoration completed. Finishing the session…",
                _ => Status
            };
        }

        private void UpdateAvailability()
        {
            Notify(nameof(CanConfigure));
            Notify(nameof(CanUsePresets));
            Notify(nameof(CanChangeMemory));
            Notify(nameof(CanLaunch));
            Notify(nameof(CanStopGame));
            LaunchCommand.Refresh();
            StopGameCommand.Refresh();
        }
    }
}
