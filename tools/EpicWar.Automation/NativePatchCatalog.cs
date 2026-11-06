using System.Text.Json;

namespace EpicWar.Automation;

internal sealed record NativePatchDefinition(NativePatchIdentity Identity, IReadOnlyList<NativePatchEdit> Edits);

// This is a production allowlist, not a discovery mechanism. Add a recipe only
// after its hash-bound native evidence and three unattended runs are reviewed.
// Diagnostic packages, command-line filenames and local journals cannot add it.
internal static class NativePatchCatalog
{
    internal const string OriginalSha256 = "DC54C54AA40D48BF2605CD5616F01E19FB1B3859B9DBE03DFA6DC28E6921E4B0";

    internal const string PatchedSha256 = "2B782B5FEBB57711364D8982262994887BFB0F50D7732ED4D1A314E64463DB89";
    private static readonly Lazy<NativePatchDefinition> Reviewed = new(Load);
    internal static NativePatchDefinition? Current => Reviewed.Value;

    private static NativePatchDefinition Load()
    {
        using var stream = typeof(NativePatchCatalog).Assembly.GetManifestResourceStream("EpicWar.Automation.Native.ra3-1.12-abi4.json")
            ?? throw new InvalidDataException("Embedded native patch recipe is missing.");
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        var identity = root.GetProperty("identity").Deserialize<NativePatchIdentity>()
            ?? throw new InvalidDataException("Embedded native patch identity is missing.");
        if (!root.GetProperty("approvedForInstallation").GetBoolean() || root.GetProperty("abiVersion").GetInt32() != 4 ||
            identity.PatchId != "ra3-1.12-native-skirmish-abi4-20261021" ||
            identity.OriginalSha256 != OriginalSha256 || identity.PatchedSha256 != PatchedSha256)
            throw new InvalidDataException("Embedded native patch identity differs from the reviewed package.");
        var edits = root.GetProperty("edits").Deserialize<NativePatchEdit[]>()
            ?? throw new InvalidDataException("Embedded native patch bytes are missing.");
        identity = identity with
        {
            OriginalLargeAddressAwareSha256 = "1CC9A1D6D5455FD2566B187EAA18E97D66C8E96A83DFEBBB90E122EC05B6F78D",
            PatchedLargeAddressAwareSha256 = "7677B539616DBDB4434824E5BDAF19743E4A690BA8CE16D3D06C0D52186E3390"
        };
        return new(identity, edits);
    }
}
