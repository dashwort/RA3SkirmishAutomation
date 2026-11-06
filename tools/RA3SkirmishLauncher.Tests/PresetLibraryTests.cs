using AutoFixture;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RA3SkirmishLauncher.Core.Presets;
using RA3SkirmishLauncher.Tests.Infrastructure;

namespace RA3SkirmishLauncher.Tests
{
    [TestClass]
    public sealed class PresetLibraryTests
    {
        private Fixture fixture = null!;
        private PresetTestData data = null!;
        private TestWorkspace workspace = null!;
        private PresetLibrary sut = null!;
        private string source = "";
        private string directory = "";

        [TestInitialize]
        public void Initialize()
        {
            fixture = new Fixture();
            fixture.Customizations.Add(new LauncherSpecimenBuilder());
            data = fixture.Create<PresetTestData>();
            workspace = new TestWorkspace();
            source = workspace.Write("Allies.ini", data.Bytes);
            directory = Path.Combine(workspace.Root, "Presets");
            sut = new PresetLibrary();
        }

        [TestCleanup]
        public void Cleanup() => workspace.Dispose();

        [TestMethod]
        public void Should_InitializeDefaultFromPreviousPreset()
        {
            var previous = workspace.Write("previous.ini", data.OtherBytes);
            sut.Initialize(directory, previous, source);
            CollectionAssert.AreEqual(data.OtherBytes, File.ReadAllBytes(Path.Combine(directory, "default.ini")));
        }

        [TestMethod]
        public void Should_PreserveExistingDefault()
        {
            workspace.Write("Presets/default.ini", data.OtherBytes);
            sut.Initialize(directory, null, source);
            CollectionAssert.AreEqual(data.OtherBytes, File.ReadAllBytes(Path.Combine(directory, "default.ini")));
        }

        [TestMethod]
        public void Should_FallBackWhenPreviousPresetIsInvalid()
        {
            var previous = workspace.Write("previous.ini", [1, 2, 3]);
            sut.Initialize(directory, previous, source);
            CollectionAssert.AreEqual(data.Bytes, File.ReadAllBytes(Path.Combine(directory, "default.ini")));
        }

        [TestMethod]
        public void Should_ListDefaultFirstThenSortNames()
        {
            workspace.Write("Presets/Zulu.ini", data.Bytes);
            workspace.Write("Presets/default.ini", data.Bytes);
            workspace.Write("Presets/Alpha.ini", data.Bytes);
            var result = sut.List(directory);
            CollectionAssert.AreEqual(new[] { "default.ini", "Alpha.ini", "Zulu.ini" }, result.Select(item => item.Name).ToArray());
        }

        [TestMethod]
        public void Should_ImportAnExactCopy()
        {
            var target = sut.Import(directory, source, "My test", false);
            CollectionAssert.AreEqual(data.Bytes, File.ReadAllBytes(target));
        }

        [TestMethod]
        public void Should_RefuseOverwriteWithoutConfirmation()
        {
            var target = workspace.Write("Presets/test.ini", data.OtherBytes);
            Assert.ThrowsException<IOException>(() => sut.Import(directory, source, "test.ini", false));
            CollectionAssert.AreEqual(data.OtherBytes, File.ReadAllBytes(target));
        }

        [TestMethod]
        public void Should_OverwriteAfterConfirmation()
        {
            workspace.Write("Presets/test.ini", data.OtherBytes);
            var target = sut.Import(directory, source, "test.ini", true);
            CollectionAssert.AreEqual(data.Bytes, File.ReadAllBytes(target));
        }

        [DataTestMethod]
        [DataRow("default.ini")]
        [DataRow("DEFAULT")]
        [DataRow("../escape")]
        [DataRow("")]
        public void Should_RejectReservedOrInvalidNames(string name)
        {
            Assert.ThrowsException<ArgumentException>(() => sut.Import(directory, source, name, false));
        }

        [TestMethod]
        public void Should_RejectIncompletePresetWithoutCreatingOutput()
        {
            File.WriteAllText(source, "Rts:GameInfo = incomplete");
            Assert.ThrowsException<InvalidDataException>(() => sut.Import(directory, source, "test.ini", false));
            Assert.IsFalse(Directory.Exists(directory));
        }
    }
}
