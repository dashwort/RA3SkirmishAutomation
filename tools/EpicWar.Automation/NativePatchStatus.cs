namespace EpicWar.Automation
{
    internal sealed record NativePatchStatus
    {
        public NativePatchStatus(string state, string? engineSha256, string? backupSha256,
            string backupPath, string journalPath, string? transactionState)
        {
            State = state;
            EngineSha256 = engineSha256;
            BackupSha256 = backupSha256;
            BackupPath = backupPath;
            JournalPath = journalPath;
            TransactionState = transactionState;
        }

        public string State { get; init; }
        public string? EngineSha256 { get; init; }
        public string? BackupSha256 { get; init; }
        public string BackupPath { get; init; }
        public string JournalPath { get; init; }
        public string? TransactionState { get; init; }
        public bool BackupVerified { get; init; }
        public bool LargeAddressAware { get; init; }
        public bool NativeInstalled { get; init; }
    }
}
