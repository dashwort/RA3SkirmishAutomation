namespace EpicWar.Automation
{
    internal sealed class NativePatchJournal
    {
        public int Version { get; set; }
        public string EnginePath { get; set; } = "";
        public NativePatchIdentity Identity { get; set; } = new("", "", "");
        public string State { get; set; } = "";
        public string? BeforeSha256 { get; set; }
        public string? AfterSha256 { get; set; }
    }
}
