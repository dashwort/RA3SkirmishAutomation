using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace EpicWar.Automation;

// Keep the compiled bytes intact: only the BIG4 index and entry offsets change.
internal static class Big4Merger
{
    private static readonly byte[] Magic = "BIG4"u8.ToArray();

    public static object Merge(string basePath, string overlayPath, string outputPath, bool replaceConflicts = false)
    {
        basePath = Path.GetFullPath(basePath);
        overlayPath = Path.GetFullPath(overlayPath);
        outputPath = Path.GetFullPath(outputPath);
        if (new[] { basePath, overlayPath }.Any(path =>
                path.Equals(outputPath, StringComparison.OrdinalIgnoreCase)) ||
            basePath.Equals(overlayPath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Base, overlay and output must be different files.");
        if (File.Exists(outputPath)) throw new IOException($"Output already exists: {outputPath}");

        using var baseFile = File.OpenRead(basePath);
        using var overlayFile = File.OpenRead(overlayPath);
        var original = ReadIndex(baseFile, "base");
        var overlay = ReadIndex(overlayFile, "overlay");
        var names = new HashSet<string>(original.Entries.Select(entry => entry.Name), StringComparer.OrdinalIgnoreCase);
        var replacementNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in overlay.Entries)
        {
            if (!names.Add(entry.Name))
            {
                if (!replaceConflicts)
                    throw new InvalidDataException($"The overlay conflicts with an existing BIG entry: {entry.Name}");
                replacementNames.Add(entry.Name);
            }
        }
        if (replaceConflicts && replacementNames.Count == 0)
            throw new InvalidDataException("The overlay does not replace any base BIG entries.");

        // Shipped RA3 Apt.big uses L280; the SDK MakeBig tool emits L219.
        // Preserve the base archive's marker while copying its payload bytes.
        if (!SupportedTail(original.Tail) || !SupportedTail(overlay.Tail))
            throw new InvalidDataException("The BIG archives use unsupported index trailers.");
        var retained = original.Entries.Where(entry => !replacementNames.Contains(entry.Name)).ToArray();
        var entries = retained.Concat(overlay.Entries).ToArray();
        var indexEnd = checked(16L + entries.Sum(entry => 8L + entry.RawName.Length + 1) + original.Tail.Length);
        var baseDataLength = baseFile.Length - original.IndexEnd;
        var overlayDataLength = overlayFile.Length - overlay.IndexEnd;
        var outputLength = checked(indexEnd + baseDataLength + overlayDataLength);
        if (outputLength > uint.MaxValue) throw new InvalidDataException("Merged BIG exceeds the BIG4 size limit.");

        var directory = Path.GetDirectoryName(outputPath)!;
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       1024 * 1024, FileOptions.SequentialScan))
            {
                output.Write(Magic);
                WriteLittle32(output, checked((uint)outputLength));
                WriteBig32(output, checked((uint)entries.Length));
                WriteBig32(output, checked((uint)indexEnd));
                foreach (var entry in retained)
                    WriteEntry(output, entry, checked(indexEnd - original.IndexEnd));
                foreach (var entry in overlay.Entries)
                    WriteEntry(output, entry, checked(indexEnd + baseDataLength - overlay.IndexEnd));
                output.Write(original.Tail);
                if (output.Position != indexEnd) throw new InvalidDataException("Merged BIG index length mismatch.");
                baseFile.Position = original.IndexEnd;
                baseFile.CopyTo(output, 1024 * 1024);
                overlayFile.Position = overlay.IndexEnd;
                overlayFile.CopyTo(output, 1024 * 1024);
                if (output.Length != outputLength) throw new InvalidDataException("Merged BIG length mismatch.");
                output.Flush(flushToDisk: true);
            }
            using (var verify = File.OpenRead(tempPath))
            {
                var parsed = ReadIndex(verify, "merged");
                if (parsed.Entries.Length != entries.Length || parsed.IndexEnd != indexEnd)
                    throw new InvalidDataException("Merged BIG index verification failed.");
            }
            File.Move(tempPath, outputPath);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }

        using var hashStream = File.OpenRead(outputPath);
        return new
        {
            status = "merged",
            output = outputPath,
            bytes = outputLength,
            baseEntries = original.Entries.Length,
            overlayEntries = overlay.Entries.Length,
            replacedEntries = replacementNames.Count,
            entries = entries.Length,
            sha256 = Convert.ToHexString(SHA256.HashData(hashStream))
        };
    }

    private static BigIndex ReadIndex(FileStream stream, string label)
    {
        if (stream.Length < 24 || stream.Length > uint.MaxValue)
            throw new InvalidDataException($"Invalid {label} BIG size.");
        Span<byte> header = stackalloc byte[16];
        stream.ReadExactly(header);
        if (!header[..4].SequenceEqual(Magic) ||
            BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]) != stream.Length)
            throw new InvalidDataException($"Invalid {label} BIG4 header or length.");
        var count = BinaryPrimitives.ReadUInt32BigEndian(header[8..12]);
        var indexEnd = BinaryPrimitives.ReadUInt32BigEndian(header[12..16]);
        if (count > 1_000_000 || indexEnd > stream.Length || indexEnd < 24)
            throw new InvalidDataException($"Invalid {label} BIG4 index bounds.");
        var entries = new BigEntry[checked((int)count)];
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fields = new byte[8];
        for (var i = 0; i < entries.Length; i++)
        {
            stream.ReadExactly(fields);
            var offset = BinaryPrimitives.ReadUInt32BigEndian(fields.AsSpan(0, 4));
            var length = BinaryPrimitives.ReadUInt32BigEndian(fields.AsSpan(4, 4));
            using var rawName = new MemoryStream();
            while (true)
            {
                if (stream.Position >= indexEnd) throw new InvalidDataException($"Unterminated {label} BIG entry.");
                var value = stream.ReadByte();
                if (value == 0) break;
                rawName.WriteByte(checked((byte)value));
            }
            var bytes = rawName.ToArray();
            var name = Encoding.UTF8.GetString(bytes).Replace('/', '\\');
            if (bytes.Length == 0 || !names.Add(name) || offset < indexEnd ||
                checked((long)offset + length) > stream.Length)
                throw new InvalidDataException($"Invalid or duplicate {label} BIG entry: {name}");
            entries[i] = new BigEntry(name, bytes, offset, length);
        }
        if (stream.Position > indexEnd || indexEnd - stream.Position > 64)
            throw new InvalidDataException($"Invalid {label} BIG index trailer.");
        var tail = new byte[checked((int)(indexEnd - stream.Position))];
        stream.ReadExactly(tail);
        return new BigIndex(indexEnd, tail, entries);
    }

    private static void WriteEntry(Stream output, BigEntry entry, long offsetDelta)
    {
        WriteBig32(output, checked((uint)(entry.Offset + offsetDelta)));
        WriteBig32(output, entry.Length);
        output.Write(entry.RawName);
        output.WriteByte(0);
    }

    private static bool SupportedTail(byte[] tail) =>
        tail.AsSpan().SequenceEqual("L219\0\0\0\0"u8) ||
        tail.AsSpan().SequenceEqual(new byte[] { 0x4C, 0x32, 0x38, 0x30, 0x15, 0x05, 0x00, 0x01 });

    private static void WriteLittle32(Stream output, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        output.Write(bytes);
    }

    private static void WriteBig32(Stream output, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        output.Write(bytes);
    }

    private sealed record BigIndex(long IndexEnd, byte[] Tail, BigEntry[] Entries);
    private sealed record BigEntry(string Name, byte[] RawName, uint Offset, uint Length);
}
