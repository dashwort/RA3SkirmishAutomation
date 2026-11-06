using EpicWar.Automation;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace RA3SkirmishLauncher.Tests
{
    [TestClass]
    public sealed class SupervisorTextWriterTests
    {
        private Mock<TextWriter> destination = null!;
        private SupervisorTextWriter sut = null!;

        [TestInitialize]
        public void Initialize()
        {
            destination = new Mock<TextWriter>();
            sut = new SupervisorTextWriter(destination.Object);
        }

        [TestMethod]
        public void Should_ContinueWhenTheGuiPipeCloses()
        {
            destination.Setup(writer => writer.WriteLine(It.IsAny<string>())).Throws(new IOException("Pipe closed"));
            sut.WriteLine("session-restored");
            destination.Verify(writer => writer.WriteLine("session-restored"), Times.Once);
        }

        [TestMethod]
        public void Should_KeepNormalCliOutput()
        {
            sut.WriteLine("session-restored");
            destination.Verify(writer => writer.WriteLine("session-restored"), Times.Once);
        }
    }
}
