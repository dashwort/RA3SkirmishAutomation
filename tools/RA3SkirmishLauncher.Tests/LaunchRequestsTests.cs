using Microsoft.VisualStudio.TestTools.UnitTesting;
using RA3SkirmishLauncher.Core.Backend;
using RA3SkirmishLauncher.Core.Mods;
using RA3SkirmishLauncher.Core.Settings;

namespace RA3SkirmishLauncher.Tests
{
    [TestClass]
    public sealed class LaunchRequestsTests
    {
        private LauncherSettings settings = null!;
        private string preset = "";

        [TestInitialize]
        public void Initialize()
        {
            var root = Path.Combine(Path.GetTempPath(), "RA3 request fixture");
            settings = new LauncherSettings
            {
                GameRoot = Path.Combine(root, "Game"), Profile = "test-profile",
                PresetDirectory = Path.Combine(root, "Presets"), BackupDirectory = Path.Combine(root, "Backups")
            };
            preset = Path.Combine(settings.PresetDirectory, "Test setup.ini");
        }

        [TestMethod]
        public void Should_LaunchNativeWithoutModArguments()
        {
            var choice = new GameChoice("Base game", null, LaunchRoute.NativeSkirmish);
            var request = LaunchRequests.Create(choice, settings, preset, true);
            Assert.AreEqual("launch-skirmish", request.Command);
            Assert.AreEqual("on", request.Options["large-address-aware"]);
            Assert.AreEqual(Path.Combine(settings.PresetDirectory, "default.ini"), request.Options["remember-preset"]);
            Assert.IsFalse(request.Options.ContainsKey("base-skudef"));
        }


        [TestMethod]
        public void Should_DefaultEpicWarToNativeCompatibility()
        {
            var choice = new GameChoice("Epic War", Path.Combine(settings.GameRoot, "RA3 Epic War_5.4.skudef"), LaunchRoute.EpicWarSkirmish);
            var request = LaunchRequests.Create(choice, settings, preset, false);
            Assert.AreEqual("launch-skirmish", request.Command);
            Assert.AreEqual(Path.GetDirectoryName(choice.SkuDef), request.Options["mod-directory"]);
            Assert.AreEqual(choice.SkuDef, request.Options["base-skudef"]);
            Assert.IsTrue(request.Options.ContainsKey("experimental"));
            Assert.AreEqual("off", request.Options["large-address-aware"]);
        }

        [TestMethod]
        public void Should_IgnoreLegacyEpicWarModeAndUseDirectPatchedLaunch()
        {
            var legacySettings = System.Text.Json.JsonSerializer.Deserialize<LauncherSettings>("""
                {"GameRoot":"G:\\RA3","Profile":"test-profile","PresetDirectory":"D:\\Presets","BackupDirectory":"D:\\Backups","EpicWarLaunchMode":"menu"}
                """)!;
            var choice = new GameChoice("Epic War", Path.Combine(settings.GameRoot, "RA3 Epic War_5.4.skudef"), LaunchRoute.EpicWarSkirmish);
            var request = LaunchRequests.Create(choice, legacySettings, preset, false);
            Assert.AreEqual("launch-skirmish", request.Command);
            Assert.AreEqual(Path.GetDirectoryName(choice.SkuDef), request.Options["mod-directory"]);
            Assert.AreEqual(choice.SkuDef, request.Options["base-skudef"]);
            Assert.IsTrue(request.Options.ContainsKey("experimental"));
            Assert.IsFalse(request.Options.ContainsKey("start-match"));
            Assert.AreEqual("off", request.Options["large-address-aware"]);
        }

        [TestMethod]
        public void Should_OmitPresetAndAutomationForOtherMods()
        {
            var choice = new GameChoice("Other mod", Path.Combine(settings.GameRoot, "other.skudef"), LaunchRoute.ModMenu);
            var request = LaunchRequests.Create(choice, settings, preset, null);
            Assert.AreEqual("launch-mod-menu", request.Command);
            Assert.IsFalse(request.Options.ContainsKey("preset"));
            Assert.IsFalse(request.Options.ContainsKey("remember-preset"));
            Assert.IsFalse(request.Options.ContainsKey("start-match"));
            Assert.IsFalse(request.Options.ContainsKey("large-address-aware"));
        }

        [TestMethod]
        public void Should_KeepPathsWithSpacesAsSingleArguments()
        {
            var request = LaunchRequests.Create(new GameChoice("Base", null, LaunchRoute.NativeSkirmish), settings, preset, true);
            var arguments = request.Arguments();
            Assert.IsTrue(arguments.Contains(preset));
            Assert.IsFalse(arguments.Contains('"' + preset + '"'));
            Assert.IsTrue(arguments.Contains("--isolate-incompatible-maps"));
        }

        [TestMethod]
        public void Should_ParseNativeAndLegacyProgress()
        {
            var stage = BackendClient.ProgressStage("{\"stage\":\"match-loaded\",\"processId\":12}");
            Assert.AreEqual("match-loaded", stage);
        }

        [TestMethod]
        public void Should_IgnoreNonJsonDiagnostics()
        {
            Assert.IsNull(BackendClient.ProgressStage("An ordinary diagnostic line"));
        }
    }
}
