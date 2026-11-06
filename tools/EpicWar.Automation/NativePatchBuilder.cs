using System.Security.Cryptography;

namespace EpicWar.Automation;

internal sealed record NativePatchEdit(int Offset,byte[] Expected,byte[] Replacement);

// A reviewed package records exact original bytes, in-place replacements and
// optionally one appended payload. Runtime installation never needs a compiler.
internal static class NativePatchBuilder
{
    internal static byte[] Build(byte[] original,NativePatchIdentity identity,IReadOnlyList<NativePatchEdit> edits)
    {
        if(!Convert.ToHexString(SHA256.HashData(original)).Equals(identity.OriginalSha256,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Native patch input does not match its pinned source SHA-256.");
        if(edits.Count==0)throw new InvalidDataException("A native patch must declare its byte changes.");
        var ordered=edits.OrderBy(e=>e.Offset).ToArray();
        int previousEnd=0,appendBytes=0;
        foreach(var edit in ordered)
        {
            if(edit.Offset<0 || edit.Offset<previousEnd || edit.Expected==null || edit.Replacement==null)
                throw new InvalidDataException("Native patch ranges are invalid or overlap.");
            if(edit.Expected.Length==0)
            {
                if(edit.Offset!=original.Length || edit.Replacement.Length==0 || appendBytes!=0)
                    throw new InvalidDataException("A native payload may only be appended once at the original file boundary.");
                appendBytes=edit.Replacement.Length;
            }
            else
            {
                if(edit.Replacement.Length!=edit.Expected.Length || (long)edit.Offset+edit.Expected.Length>original.Length)
                    throw new InvalidDataException("Native in-place edits must preserve length and remain within the original file.");
                if(!original.AsSpan(edit.Offset,edit.Expected.Length).SequenceEqual(edit.Expected))
                    throw new InvalidDataException($"Native patch original bytes disagree at file offset 0x{edit.Offset:X}.");
            }
            previousEnd=checked(edit.Offset+edit.Replacement.Length);
        }
        var output=new byte[checked(original.Length+appendBytes)];
        original.CopyTo(output,0);
        foreach(var edit in ordered)edit.Replacement.CopyTo(output,edit.Offset);
        if(!Convert.ToHexString(SHA256.HashData(output)).Equals(identity.PatchedSha256,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Native byte recipe does not reproduce its pinned output SHA-256.");
        return output;
    }
}
