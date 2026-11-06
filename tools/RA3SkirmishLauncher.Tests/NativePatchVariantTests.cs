using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using EpicWar.Automation;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using RA3SkirmishLauncher.Tests.Infrastructure;

namespace RA3SkirmishLauncher.Tests
{
    [TestClass]
    public sealed class NativePatchVariantTests
    {
        private TestWorkspace workspace = null!;
        private byte[] original = [];
        private byte[] native = [];
        private NativePatchIdentity identity = null!;
        private string engine = "";
        private string backups = "";
        private Mock<IDisposable> lease = null!;
        private Mock<Action> ensureClosed = null!;
        private NativePatchInstaller sut = null!;

        [TestInitialize]
        public void Initialize()
        {
            workspace = new TestWorkspace();
            original = new byte[4096];
            original[0] = (byte)'M'; original[1] = (byte)'Z';
            BinaryPrimitives.WriteInt32LittleEndian(original.AsSpan(60, 4), 128);
            "PE\0\0"u8.CopyTo(original.AsSpan(128));
            BinaryPrimitives.WriteUInt16LittleEndian(original.AsSpan(132, 2), 0x14c);
            BinaryPrimitives.WriteUInt16LittleEndian(original.AsSpan(150, 2), 0x103);
            BinaryPrimitives.WriteUInt16LittleEndian(original.AsSpan(152, 2), 0x10b);
            native = (byte[])original.Clone();
            native[1024] = 0x90;
            identity = new NativePatchIdentity("test-engine", Hash(original), Hash(native))
            {
                OriginalLargeAddressAwareSha256 = Hash(LargeAddressAwarePatch.Apply(original, true)),
                PatchedLargeAddressAwareSha256 = Hash(LargeAddressAwarePatch.Apply(native, true))
            };
            engine = workspace.Write("game/Data/ra3_1.12.game", original);
            backups = Path.Combine(workspace.Root, "backups");
            lease = new Mock<IDisposable>();
            ensureClosed = new Mock<Action>();
            sut = CreateSut();
        }

        [TestCleanup]
        public void Cleanup() => workspace.Dispose();

        [DataTestMethod]
        [DataRow(false, false, true)]
        [DataRow(false, true, false)]
        [DataRow(true, false, true)]
        [DataRow(true, true, false)]
        [DataRow(true, true, true)]
        public void Should_ChangeOnlyMemoryFlagAndPreserveNativeBytes(bool hasNative, bool wasEnabled, bool enabled)
        {
            var baseline = hasNative ? native : original;
            File.WriteAllBytes(engine, LargeAddressAwarePatch.Apply(baseline, wasEnabled));
            workspace.Write(Path.GetRelativePath(workspace.Root, Path.Combine(backups, identity.OriginalSha256, "ra3_1.12.game")), original);
            var result = sut.SetMemory(enabled);
            CollectionAssert.AreEqual(LargeAddressAwarePatch.Apply(baseline, enabled), File.ReadAllBytes(engine));
            Assert.AreEqual(enabled, result.LargeAddressAware);
            Assert.AreEqual(hasNative, result.NativeInstalled);
            Assert.IsTrue(result.BackupVerified);
        }

        [TestMethod]
        public void Should_RestoreExactOriginalFromCombinedVariant()
        {
            sut.Install(LargeAddressAwarePatch.Apply(native, true));
            var result = sut.Restore();
            CollectionAssert.AreEqual(original, File.ReadAllBytes(engine));
            Assert.AreEqual("original", result.State);
        }

        [DataTestMethod]
        [DataRow("prepared-install")]
        [DataRow("replaced-install")]
        public void Should_RecoverInterruptedMemoryChangeToPreviousNativeVariant(string stage)
        {
            sut.Install(native);
            var interrupted = CreateSut(point => { if (point == stage) { throw new IOException("Simulated interruption"); } });
            try { interrupted.SetMemory(true); } catch (IOException) { }
            var result = sut.Recover();
            CollectionAssert.AreEqual(native, File.ReadAllBytes(engine));
            Assert.IsTrue(result.NativeInstalled);
            Assert.IsFalse(result.LargeAddressAware);
        }

        [TestMethod]
        public void Should_ReadAndRecoverVersionOneJournals()
        {
            sut.Install(native);
            var status = sut.Inspect();
            File.WriteAllText(status.JournalPath, JsonSerializer.Serialize(new
            {
                Version = 1, EnginePath = Path.GetFullPath(engine),
                Identity = new { identity.PatchId, identity.OriginalSha256, identity.PatchedSha256 }, State = "prepared-install"
            }));
            var result = sut.Recover();
            Assert.AreEqual("original", result.State);
            CollectionAssert.AreEqual(original, File.ReadAllBytes(engine));
        }

        [TestMethod]
        public void Should_RejectUnknownEngineWithoutChangingIt()
        {
            var unknown = (byte[])native.Clone(); unknown[2000] = 1;
            File.WriteAllBytes(engine, unknown);
            Assert.ThrowsException<InvalidDataException>(() => sut.SetMemory(true));
            CollectionAssert.AreEqual(unknown, File.ReadAllBytes(engine));
        }

        [TestMethod]
        public void Should_RejectCorruptOriginalBackup()
        {
            sut.Install(native);
            File.WriteAllBytes(sut.Inspect().BackupPath, [1, 2, 3]);
            Assert.ThrowsException<InvalidDataException>(() => sut.SetMemory(true));
            CollectionAssert.AreEqual(native, File.ReadAllBytes(engine));
        }

        [TestMethod]
        public void Should_NotAcquireAnotherLeaseInsideLaunch()
        {
            var acquire = new Mock<Func<IDisposable>>(MockBehavior.Strict);
            var installer = new NativePatchInstaller(engine, backups, identity, ensureClosed.Object, acquire.Object);
            installer.SetMemoryUnderLease(true);
            acquire.Verify(call => call(), Times.Never);
        }

        [TestMethod]
        public void Should_KeepEveryByteExceptTheLaaBit()
        {
            var result = LargeAddressAwarePatch.Apply(native, true);
            var changes = result.Select((value, index) => (value, index)).Where(pair => pair.value != native[pair.index]).ToArray();
            Assert.AreEqual(1, changes.Length);
            Assert.AreEqual(150, changes[0].index);
            Assert.AreEqual(0x20, result[150] ^ native[150]);
        }

        [TestMethod]
        public void Should_PreserveEnabledMemoryWhenInstallingNativeSupport()
        {
            sut.SetMemory(true);
            var definition = new NativePatchDefinition(identity, [new NativePatchEdit(1024, [0], [0x90])]);
            var gameRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(engine)))!;
            var options = new Dictionary<string, string?> { ["game-root"] = gameRoot, ["backup-root"] = backups };
            NativePatchCommands.Run("install", options, definition);
            CollectionAssert.AreEqual(LargeAddressAwarePatch.Apply(native, true), File.ReadAllBytes(engine));
        }

        [TestMethod]
        public void Should_RefuseRecoveryWithCorruptTransitionBackup()
        {
            sut.Install(native);
            var interrupted = CreateSut(point => { if (point == "replaced-install") { throw new IOException("Simulated interruption"); } });
            try { interrupted.SetMemory(true); } catch (IOException) { }
            File.WriteAllBytes(Path.ChangeExtension(sut.Inspect().JournalPath, ".before"), [1, 2, 3]);
            Assert.ThrowsException<InvalidDataException>(() => sut.Recover());
            CollectionAssert.AreEqual(LargeAddressAwarePatch.Apply(native, true), File.ReadAllBytes(engine));
        }

        [DataTestMethod]
        [DataRow("on", true)]
        [DataRow("off", false)]
        public void Should_ParseExplicitMemorySelection(string option, bool expected)
        {
            var result = MemoryPatchCommands.RequestedState(new Dictionary<string, string?> { ["large-address-aware"] = option });
            Assert.AreEqual(expected, result);
        }

        [TestMethod]
        public void Should_PreserveInstalledMemoryWhenOptionIsOmitted()
        {
            Assert.IsNull(MemoryPatchCommands.RequestedState(new Dictionary<string, string?>()));
        }

        [TestMethod]
        public void Should_RejectAmbiguousMemorySelection()
        {
            Assert.ThrowsException<ArgumentException>(() => MemoryPatchCommands.RequestedState(
                new Dictionary<string, string?> { ["large-address-aware"] = "yes" }));
        }

        private NativePatchInstaller CreateSut(Action<string>? checkpoint = null) =>
            new(engine, backups, identity, ensureClosed.Object, () => lease.Object, checkpoint);

        private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    }
}
