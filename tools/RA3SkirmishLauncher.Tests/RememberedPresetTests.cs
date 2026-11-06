using AutoFixture;
using EpicWar.Automation;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RA3SkirmishLauncher.Tests.Infrastructure;

namespace RA3SkirmishLauncher.Tests
{
    [TestClass]
    public sealed class RememberedPresetTests
    {
        private Fixture fixture = null!;
        private PresetTestData data = null!;
        private TestWorkspace workspace = null!;
        private Dictionary<string, string?> options = null!;
        private string destination = "";

        [TestInitialize]
        public void Initialize()
        {
            fixture = new Fixture();
            fixture.Customizations.Add(new LauncherSpecimenBuilder());
            data = fixture.Create<PresetTestData>();
            workspace = new TestWorkspace();
            destination = workspace.Write("Presets/default.ini", data.OtherBytes);
            options = new Dictionary<string, string?> { ["remember-preset"] = destination };
        }

        [TestCleanup]
        public void Cleanup() => workspace.Dispose();

        [TestMethod]
        public void Should_SaveTheLaunchSnapshotAfterSuccessfulCleanup()
        {
            var sut = CreateSut();
            var expected = (byte[])data.Bytes.Clone();
            data.Bytes[0] = 0;
            sut.Complete(true);
            CollectionAssert.AreEqual(expected, File.ReadAllBytes(destination));
        }

        [TestMethod]
        public void Should_PreserveDefaultAfterFailureOrCancellation()
        {
            var sut = CreateSut();
            sut.Complete(false);
            CollectionAssert.AreEqual(data.OtherBytes, File.ReadAllBytes(destination));
        }

        [TestMethod]
        public void Should_LeaveLegacyCallsWithoutRememberOptionUnchanged()
        {
            options.Clear();
            var sut = CreateSut();
            sut.Complete(true);
            CollectionAssert.AreEqual(data.OtherBytes, File.ReadAllBytes(destination));
        }

        [TestMethod]
        public void Should_RejectRememberingIntoProtectedProfile()
        {
            options["remember-preset"] = Path.Combine(workspace.Root, "profiles", "default.ini");
            Assert.ThrowsException<ArgumentException>(() => CreateSut());
        }

        private RememberedPreset CreateSut() => RememberedPreset.Prepare(options, data.Bytes, Path.Combine(workspace.Root, "profiles"));
    }
}
