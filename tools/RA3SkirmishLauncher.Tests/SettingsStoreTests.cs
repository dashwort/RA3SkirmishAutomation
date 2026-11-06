using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using RA3SkirmishLauncher.Core.Settings;
using RA3SkirmishLauncher.Tests.Infrastructure;

namespace RA3SkirmishLauncher.Tests
{
    [TestClass]
    public sealed class SettingsStoreTests
    {
        private TestWorkspace workspace = null!;
        private Mock<ILauncherPaths> paths = null!;
        private SettingsStore sut = null!;

        [TestInitialize]
        public void Initialize()
        {
            workspace = new TestWorkspace();
            paths = new Mock<ILauncherPaths>();
            paths.SetupGet(value => value.ApplicationDirectory).Returns(workspace.Root);
            paths.SetupGet(value => value.SettingsFile).Returns(Path.Combine(workspace.Root, "Settings", "settings.json"));
            paths.SetupGet(value => value.LegacySettingsFile).Returns(Path.Combine(workspace.Root, "legacy.json"));
            paths.SetupGet(value => value.BackupDirectory).Returns(Path.Combine(workspace.Root, "Backups"));
            paths.Setup(value => value.DiscoverGameRoot()).Returns(Path.Combine(workspace.Root, "Game"));
            paths.Setup(value => value.CurrentProfile()).Returns("active");
            sut = new SettingsStore(paths.Object);
        }

        [TestCleanup]
        public void Cleanup() => workspace.Dispose();

        [TestMethod]
        public void Should_DefaultToDarkAndSharedPortablePresetFolder()
        {
            var result = sut.Load();
            Assert.AreEqual("Dark", result.Theme);
            Assert.AreEqual(Path.Combine(workspace.Root, "Presets"), result.PresetDirectory);
            Assert.AreEqual("active", result.Profile);
            Assert.IsFalse(result.HighFrameRateEnabled);
        }

        [TestMethod]
        public void Should_MigratePreviousSelectedModeAndPreset()
        {
            workspace.Write("legacy.json", Encoding.UTF8.GetBytes("""
                {"mode":"mod-menu","mod-preset":"previous.ini","base-skudef":"C:\\Mods\\EpicWar.skudef","profile":"dave"}
                """));
            var result = sut.Load();
            Assert.AreEqual("previous.ini", result.PreviousPreset);
            Assert.AreEqual("dave", result.Profile);
            Assert.AreEqual("C:\\Mods\\EpicWar.skudef", result.SelectedGameId);
        }

        [TestMethod]
        public void Should_RoundTripSettingsWithoutChangingLegacyFile()
        {
            var settings = new LauncherSettings
            {
                Theme = "Light", Profile = "user", ExtraModLocations = "Mods", SelectedGameId = "other",
                HighFrameRateEnabled = true
            };
            sut.Save(settings);
            var loaded = sut.Load();
            Assert.AreEqual("Light", loaded.Theme);
            Assert.AreEqual("user", loaded.Profile);
            Assert.AreEqual("other", loaded.SelectedGameId);
            Assert.IsTrue(loaded.HighFrameRateEnabled);
            Assert.IsFalse(File.Exists(paths.Object.LegacySettingsFile));
        }

        [TestMethod]
        public void Should_DiscardLegacyEpicWarLaunchModeWhenSavingSettings()
        {
            workspace.Write("Settings/settings.json", Encoding.UTF8.GetBytes("""
                {"EpicWarLaunchMode":"menu","Theme":"Dark","Profile":"dave"}
                """));
            var loaded = sut.Load();
            sut.Save(loaded);
            var saved = File.ReadAllText(paths.Object.SettingsFile);
            Assert.IsFalse(saved.Contains("EpicWarLaunchMode", StringComparison.Ordinal));
        }
    }
}
