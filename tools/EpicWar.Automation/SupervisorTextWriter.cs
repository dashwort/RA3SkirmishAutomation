using System.Text;

namespace EpicWar.Automation
{
    // Losing the GUI's output pipe must not abort the supervisor's restoration work.
    internal sealed class SupervisorTextWriter : TextWriter
    {
        private readonly TextWriter destination;

        public SupervisorTextWriter(TextWriter destination)
        {
            this.destination = destination;
        }

        public override Encoding Encoding => destination.Encoding;

        public override void Write(char value)
        {
            try { destination.Write(value); }
            catch (IOException) { }
        }

        public override void WriteLine(string? value)
        {
            try { destination.WriteLine(value); }
            catch (IOException) { }
        }

        public override void Flush()
        {
            try { destination.Flush(); }
            catch (IOException) { }
        }
    }
}
