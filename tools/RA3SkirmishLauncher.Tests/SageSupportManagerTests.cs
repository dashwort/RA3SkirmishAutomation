using Microsoft.VisualStudio.TestTools.UnitTesting;
using RA3SkirmishLauncher.Core.Settings;
using RA3SkirmishLauncher.Tests.Infrastructure;

namespace RA3SkirmishLauncher.Tests
{
    [TestClass]
    public sealed class SageSupportManagerTests
    {
        private TestWorkspace workspace = null!;
        private SageSupportManager sut = null!;
        private string gameRoot = null!;

        [TestInitialize]
        public void Initialize()
        {
            workspace = new TestWorkspace();
            gameRoot = Path.Combine(workspace.Root, "Game");
            CreateGameRoot(gameRoot);
            var assets = Path.Combine(workspace.Root, "assets");
            Directory.CreateDirectory(assets);
            File.WriteAllBytes(Path.Combine(assets, "d3d9.dll"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(assets, "CnCFpsUnlocker.dll"), [4, 5, 6]);
            File.WriteAllText(Path.Combine(assets, "SAGEUnlocked.ini"), "fps=60\n");
            sut = new SageSupportManager(assets, Path.Combine(workspace.Root, "sage-managed.json"));
        }

        [TestCleanup]
        public void Cleanup() => workspace.Dispose();

        [TestMethod]
        public void Should_InstallIdempotentlyAndRemoveManagedFiles()
        {
            sut.Apply(gameRoot, true);
            sut.Apply(gameRoot, true);

            var data = Path.Combine(gameRoot, "Data");
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(data, "d3d9.dll")));
            CollectionAssert.AreEqual(new byte[] { 4, 5, 6 }, File.ReadAllBytes(Path.Combine(data, "CnCFpsUnlocker.dll")));
            Assert.AreEqual("fps=60\n", File.ReadAllText(Path.Combine(data, "SAGEUnlocked.ini")));

            sut.Apply(gameRoot, false);
            Assert.IsFalse(File.Exists(Path.Combine(data, "d3d9.dll")));
            Assert.IsFalse(File.Exists(Path.Combine(data, "CnCFpsUnlocker.dll")));
            Assert.IsFalse(File.Exists(Path.Combine(data, "SAGEUnlocked.ini")));
        }

        [TestMethod]
        public void Should_RefuseUnmanagedCollisionWithoutPartialWrites()
        {
            var data = Path.Combine(gameRoot, "Data");
            Directory.CreateDirectory(data);
            File.WriteAllBytes(Path.Combine(data, "d3d9.dll"), [9, 9, 9]);

            Assert.ThrowsException<InvalidOperationException>(() => sut.Apply(gameRoot, true));

            CollectionAssert.AreEqual(new byte[] { 9, 9, 9 }, File.ReadAllBytes(Path.Combine(data, "d3d9.dll")));
            Assert.IsFalse(File.Exists(Path.Combine(data, "CnCFpsUnlocker.dll")));
            Assert.IsFalse(File.Exists(Path.Combine(data, "SAGEUnlocked.ini")));
        }

        [TestMethod]
        public void Should_PreserveFilesModifiedAfterInstallationWhenDisabled()
        {
            sut.Apply(gameRoot, true);
            var config = Path.Combine(gameRoot, "Data", "SAGEUnlocked.ini");
            File.WriteAllText(config, "fps=75\n");

            sut.Apply(gameRoot, false);

            Assert.AreEqual("fps=75\n", File.ReadAllText(config));
            Assert.IsFalse(File.Exists(Path.Combine(gameRoot, "Data", "d3d9.dll")));
            Assert.IsFalse(File.Exists(Path.Combine(gameRoot, "Data", "CnCFpsUnlocker.dll")));
            Assert.IsFalse(File.Exists(Path.Combine(workspace.Root, "sage-managed.json")));
        }

        [TestMethod]
        public void Should_MoveManagedInstallationWhenGameRootChanges()
        {
            sut.Apply(gameRoot, true);
            var nextRoot = Path.Combine(workspace.Root, "NextGame");
            CreateGameRoot(nextRoot);

            sut.Apply(nextRoot, true);

            Assert.IsFalse(File.Exists(Path.Combine(gameRoot, "Data", "d3d9.dll")));
            Assert.IsTrue(File.Exists(Path.Combine(nextRoot, "Data", "d3d9.dll")));
        }

        [TestMethod]
        public void Should_RollBackNewFilesWhenOwnershipRecordCannotBeWritten()
        {
            var markerParent = Path.Combine(workspace.Root, "not-a-directory");
            File.WriteAllText(markerParent, "block marker directory creation");
            sut = new SageSupportManager(Path.Combine(workspace.Root, "assets"), Path.Combine(markerParent, "sage-managed.json"));

            Assert.ThrowsException<IOException>(() => sut.Apply(gameRoot, true));

            var data = Path.Combine(gameRoot, "Data");
            Assert.IsFalse(File.Exists(Path.Combine(data, "d3d9.dll")));
            Assert.IsFalse(File.Exists(Path.Combine(data, "CnCFpsUnlocker.dll")));
            Assert.IsFalse(File.Exists(Path.Combine(data, "SAGEUnlocked.ini")));
        }

        private static void CreateGameRoot(string root)
        {
            var data = Path.Combine(root, "Data");
            Directory.CreateDirectory(data);
            File.WriteAllText(Path.Combine(data, "ra3_1.12.game"), "test engine");
        }
    }
}
