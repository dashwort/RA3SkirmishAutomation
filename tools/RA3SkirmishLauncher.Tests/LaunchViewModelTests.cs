using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using RA3SkirmishLauncher.Core.Backend;
using RA3SkirmishLauncher.Core.Mods;
using RA3SkirmishLauncher.Core.Presentation;
using RA3SkirmishLauncher.Core.Presets;
using RA3SkirmishLauncher.Core.Settings;

namespace RA3SkirmishLauncher.Tests
{
    [TestClass]
    public sealed class LaunchViewModelTests
    {
        private Mock<IBackendClient> backend = null!;
        private Mock<ISettingsStore> store = null!;
        private Mock<ISageSupportManager> sage = null!;
        private Mock<ILauncherPaths> paths = null!;
        private Mock<IModDiscovery> discovery = null!;
        private Mock<IPresetLibrary> library = null!;
        private LaunchViewModel sut = null!;
        private GameChoice[] games = [];
        private LauncherSettings settings = null!;

        [TestInitialize]
        public async Task Initialize()
        {
            var root = Path.Combine(Path.GetTempPath(), "RA3 view model fixture");
            settings = new LauncherSettings
            {
                GameRoot = Path.Combine(root, "Game"), Profile = "profile", PresetDirectory = Path.Combine(root, "Presets"),
                BackupDirectory = Path.Combine(root, "Backups")
            };
            games =
            [
                new("Base", null, LaunchRoute.NativeSkirmish),
                new("Other", Path.Combine(root, "mod.skudef"), LaunchRoute.ModMenu),
                new("Epic War", Path.Combine(root, "RA3 Epic War_5.4.skudef"), LaunchRoute.EpicWarSkirmish)
            ];
            backend = new Mock<IBackendClient>();
            store = new Mock<ISettingsStore>();
            sage = new Mock<ISageSupportManager>();
            sage.Setup(value => value.Apply(It.IsAny<string>(), It.IsAny<bool>())).Returns("SAGE setting applied.");
            paths = new Mock<ILauncherPaths>();
            discovery = new Mock<IModDiscovery>();
            library = new Mock<IPresetLibrary>();
            store.Setup(value => value.Load()).Returns(settings);
            paths.SetupGet(value => value.ApplicationDirectory).Returns(root);
            paths.SetupGet(value => value.ModDirectory).Returns(Path.Combine(root, "Mods"));
            paths.Setup(value => value.Profiles()).Returns(["profile"]);
            discovery.Setup(value => value.Discover(It.IsAny<IEnumerable<string>>())).Returns(games);
            library.Setup(value => value.List(It.IsAny<string>())).Returns([new PresetChoice(Path.Combine(settings.PresetDirectory, "default.ini"))]);
            backend.Setup(value => value.RunAsync(It.Is<BackendRequest>(request => request.Command == "memory-patch"), It.IsAny<IProgress<string>?>()))
                .ReturnsAsync(new BackendResult(0, """
                    {"status":"memory-patch-status","transaction":{"State":"installed","LargeAddressAware":true,
                     "NativeInstalled":true,"BackupVerified":true,"TransactionState":"installed"}}
                    """));
            backend.Setup(value => value.RunAsync(It.Is<BackendRequest>(request => request.Command == "launch-skirmish"), It.IsAny<IProgress<string>?>()))
                .ReturnsAsync(new BackendResult(0, "{\"status\":\"session-ended\"}"));
            sut = new LaunchViewModel(backend.Object, store.Object, sage.Object, paths.Object, discovery.Object, library.Object);
            await sut.InitializeAsync();
        }

        [TestMethod]
        public void Should_InitializeMemorySwitchFromEngine()
        {
            Assert.IsTrue(sut.MemoryEnabled);
            Assert.IsTrue(sut.CanLaunch);
            Assert.AreEqual("default.ini", sut.Presets.Selected?.Name);
        }

        [TestMethod]
        public void Should_DisablePresetsForMenuOnlyMod()
        {
            sut.SelectedGame = games[1];
            Assert.IsFalse(sut.CanUsePresets);
            Assert.IsTrue(sut.CanLaunch);
        }

        [TestMethod]
        public async Task Should_RequireNativeSupportBeforeDirectEpicWarLaunch()
        {
            settings.SelectedGameId = games[2].Id;
            backend.Setup(value => value.RunAsync(It.Is<BackendRequest>(request => request.Command == "memory-patch"), It.IsAny<IProgress<string>?>()))
                .ReturnsAsync(new BackendResult(0, """
                    {"status":"memory-patch-status","transaction":{"State":"installed","LargeAddressAware":false,
                     "NativeInstalled":false,"BackupVerified":true,"TransactionState":"installed"}}
                    """));
            await sut.InitializeAsync();
            Assert.AreSame(games[2], sut.SelectedGame);
            Assert.IsFalse(sut.CanLaunch);
        }

        [TestMethod]
        public void Should_RequireSettingsRefreshAfterChangingGameFolder()
        {
            sut.Settings.GameRoot += "-changed";
            Assert.IsFalse(sut.CanLaunch);
            Assert.IsFalse(sut.CanChangeMemory);
        }

        [TestMethod]
        public async Task Should_DisableConfigurationUntilSupervisorFinishes()
        {
            var completion = new TaskCompletionSource<BackendResult>();
            backend.Setup(value => value.RunAsync(It.Is<BackendRequest>(request => request.Command == "launch-skirmish"), It.IsAny<IProgress<string>?>()))
                .Returns(completion.Task);
            var launch = sut.LaunchCommand.ExecuteAsync();
            Assert.IsTrue(sut.IsBusy);
            Assert.IsFalse(sut.CanConfigure);
            Assert.IsFalse(sut.CanUsePresets);
            Assert.IsFalse(sut.LaunchCommand.CanExecute(null));
            completion.SetResult(new BackendResult(0, "{\"status\":\"session-ended\"}"));
            await launch;
            Assert.IsTrue(sut.CanConfigure);
        }

        [TestMethod]
        public async Task Should_StopOwnedGameAndWaitForRestoration()
        {
            var completion = new TaskCompletionSource<BackendResult>();
            backend.Setup(value => value.RunAsync(It.Is<BackendRequest>(request => request.Command == "launch-skirmish"), It.IsAny<IProgress<string>?>()))
                .Returns(completion.Task);
            backend.Setup(value => value.StopActiveGameAsync()).ReturnsAsync(true);
            var launch = sut.LaunchCommand.ExecuteAsync();
            Assert.IsTrue(sut.CanStopGame);
            await sut.StopGameCommand.ExecuteAsync();
            backend.Verify(value => value.StopActiveGameAsync(), Times.Once);
            Assert.IsTrue(sut.IsBusy);
            completion.SetResult(new BackendResult(130, "{\"status\":\"error\",\"message\":\"The operation was canceled.\"}"));
            await launch;
            Assert.AreEqual("Game stopped. Restoration completed.", sut.Status);
            Assert.IsFalse(sut.CanStopGame);
        }

        [TestMethod]
        public async Task Should_HandleStopRacingWithSupervisorExit()
        {
            var completion = new TaskCompletionSource<BackendResult>();
            backend.Setup(value => value.RunAsync(It.Is<BackendRequest>(request => request.Command == "launch-skirmish"), It.IsAny<IProgress<string>?>()))
                .Returns(completion.Task);
            backend.Setup(value => value.StopActiveGameAsync()).ThrowsAsync(new InvalidOperationException("Session already closed"));
            var launch = sut.LaunchCommand.ExecuteAsync();
            await sut.StopGameCommand.ExecuteAsync();
            Assert.IsTrue(sut.CanStopGame);
            Assert.AreEqual("The game session is already finishing.", sut.Status);
            completion.SetResult(new BackendResult(0, "{\"status\":\"session-ended\"}"));
            await launch;
        }

        [TestMethod]
        public async Task Should_PreserveBackendErrorWhenControlsAreEnabledAgain()
        {
            backend.Setup(value => value.RunAsync(It.Is<BackendRequest>(request => request.Command == "launch-skirmish"), It.IsAny<IProgress<string>?>()))
                .ReturnsAsync(new BackendResult(1, "{\"status\":\"error\",\"message\":\"Preset is invalid.\"}"));
            await sut.LaunchCommand.ExecuteAsync();
            Assert.AreEqual("Preset is invalid.", sut.Status);
            Assert.IsTrue(sut.CanConfigure);
        }

        [TestMethod]
        public async Task Should_PreserveCompletionWhenControlsAreEnabledAgain()
        {
            await sut.LaunchCommand.ExecuteAsync();
            StringAssert.Contains(sut.Status, "Restoration completed");
            Assert.IsTrue(sut.CanConfigure);
        }

        [TestMethod]
        public async Task Should_SaveSettingsAndPassRememberDestinationToBackend()
        {
            await sut.LaunchCommand.ExecuteAsync();
            store.Verify(value => value.Save(It.Is<LauncherSettings>(saved => saved.SelectedGameId == "base-game")), Times.Once);
            backend.Verify(value => value.RunAsync(It.Is<BackendRequest>(request => request.Command == "launch-skirmish" &&
                request.Options["remember-preset"] == Path.Combine(settings.PresetDirectory, "default.ini")), It.IsAny<IProgress<string>?>()), Times.Once);
        }

        [TestMethod]
        public async Task Should_ApplySageSettingAndPersistItWhenApplyingSettings()
        {
            sut.Settings.HighFrameRateEnabled = true;

            await sut.ApplySettingsAsync();

            sage.Verify(value => value.Apply(settings.GameRoot, true), Times.Once);
            store.Verify(value => value.Save(It.Is<LauncherSettings>(saved => saved.HighFrameRateEnabled)), Times.Once);
            StringAssert.Contains(sut.Status, "SAGE setting applied.");
        }

        [TestMethod]
        public async Task Should_NotPersistHighFrameRateSettingWhenSageInstallFails()
        {
            sage.Setup(value => value.Apply(It.IsAny<string>(), It.IsAny<bool>()))
                .Throws(new InvalidOperationException("SAGE file conflict."));
            sut.Settings.HighFrameRateEnabled = true;

            await sut.ApplySettingsAsync();

            store.Verify(value => value.Save(It.IsAny<LauncherSettings>()), Times.Never);
            Assert.AreEqual("SAGE file conflict.", sut.Status);
        }

        [TestMethod]
        public void Should_RefreshLibraryWhenReturningToWindow()
        {
            sut.RefreshPresets();
            library.Verify(value => value.List(settings.PresetDirectory), Times.Exactly(2));
        }
    }
}
