namespace EpicWar.Automation
{
    internal sealed record NativePatchIdentity
    {
        public NativePatchIdentity(string patchId, string originalSha256, string patchedSha256)
        {
            PatchId = patchId;
            OriginalSha256 = originalSha256;
            PatchedSha256 = patchedSha256;
        }

        public string PatchId { get; init; }
        public string OriginalSha256 { get; init; }
        public string PatchedSha256 { get; init; }
        public string? OriginalLargeAddressAwareSha256 { get; init; }
        public string? PatchedLargeAddressAwareSha256 { get; init; }

        public bool IsKnown(string? hash) => hash != null &&
            (hash == OriginalSha256 || hash == PatchedSha256 ||
             hash == OriginalLargeAddressAwareSha256 || hash == PatchedLargeAddressAwareSha256);

        public bool HasNativePatch(string? hash) => hash != null &&
            (hash == PatchedSha256 || hash == PatchedLargeAddressAwareSha256);

        public bool HasLargeAddressAware(string? hash) => hash != null &&
            (hash == OriginalLargeAddressAwareSha256 || hash == PatchedLargeAddressAwareSha256);
    }
}
