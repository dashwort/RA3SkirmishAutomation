using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RA3SkirmishLauncher.Core.Mods;
using RA3SkirmishLauncher.Tests.Infrastructure;

namespace RA3SkirmishLauncher.Tests
{
    [TestClass]
    public sealed class ModDiscoveryTests
    {
        private TestWorkspace workspace = null!;
        private ModDiscovery sut = null!;
        private byte[] skuDef = [];

        [TestInitialize]
        public void Initialize()
        {
            workspace = new TestWorkspace();
            sut = new ModDiscovery();
            skuDef = Encoding.UTF8.GetBytes("mod-game 1.12\nadd-big mod.big\n");
            workspace.Write("First/Mod_1.0.skudef", skuDef);
            workspace.Write("First/mod.big", [1]);
        }

        [TestCleanup]
        public void Cleanup() => workspace.Dispose();

        [TestMethod]
        public void Should_ListBaseGameFirstAndDiscoveredModAsMenuOnly()
        {
            var result = sut.Discover([workspace.Root]);
            Assert.AreEqual(LaunchRoute.NativeSkirmish, result[0].Route);
            Assert.AreEqual(LaunchRoute.ModMenu, result[1].Route);
        }

        [TestMethod]
        public void Should_KeepDuplicateNamesFromDifferentPaths()
        {
            workspace.Write("Second/Mod_1.0.skudef", skuDef);
            workspace.Write("Second/mod.big", [1]);
            var result = sut.Discover([workspace.Root, workspace.Root]);
            Assert.AreEqual(3, result.Count);
            Assert.AreNotEqual(result[1].Id, result[2].Id);
        }

        [TestMethod]
        public void Should_IgnoreModsWithMissingArchives()
        {
            File.Delete(Path.Combine(workspace.Root, "First", "mod.big"));
            var result = sut.Discover([workspace.Root]);
            Assert.AreEqual(1, result.Count);
        }

        [TestMethod]
        public void Should_RecognizeSupportedEpicWarConfiguration()
        {
            workspace.Write("EpicWar/RA3 Epic War_5.4.skudef", skuDef);
            workspace.Write("EpicWar/mod.big", [1]);
            var result = sut.Discover([workspace.Root]);
            Assert.AreEqual(1, result.Count(choice => choice.Route == LaunchRoute.EpicWarSkirmish));
        }

        [TestMethod]
        public void Should_SkipGameConfigurationsWithoutModGame()
        {
            workspace.Write("RA3_english_1.12.SkuDef", Encoding.UTF8.GetBytes("add-big stock.big\n"));
            var result = sut.Discover([workspace.Root]);
            Assert.AreEqual(2, result.Count);
        }
    }
}
