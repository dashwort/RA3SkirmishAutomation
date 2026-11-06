using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace EpicWar.Automation;

// These edits keep the EA source-pack files exactly the same size and preserve
// existing pointers. Only main-menu navigation has passed a runtime test.
internal static class AptInPlacePatch
{
    private const string MainSha256 = "0652914AF2C60FE8B1950C2C74F38C8548F1A19988EF746964328DF25A32EBA6";
    private const string SkirmishConstSha256 = "45CFB330BEA5E573EC603D05E0A8F19D62E57995EAE203153EC7019E63E42B73";
    private const string FlatMenuConstSha256 = "B2D4B19C20CAF1D73A12077DE7F2D97252E7861DD1525A9FC71B39B36DB0531E";

    private static readonly byte[] MainStockFunction =
    [
        0x59, 0xB9, 0x01, 0xB2, 0x1A, // initGUI()
        0x59, 0xB9, 0x01, 0xB2, 0x1B, // startIntro()
        0x83, 0x00, 0xE0, 0x4E, 0x00, 0x00, 0xFC, 0x4E, 0x00, 0x00 // resolution timer
    ];

    private static readonly byte[] MainAutomaticFunction =
    [
        0x59, 0xB9, 0x01, 0xB2, 0x1A, // initGUI()
        0x59, 0xB9, 0x01, 0xB2, 0x1B, // startIntro()
        // Operands index this function's pool (starts at CONST item 89),
        // so processVersusMenuClick at item 190 is local index 101.
        0x5A, 0x5A, 0xB9, 0x01, 0xB2, 0x65, // processVersusMenuClick(1)
        0x59, 0x17, 0x59, 0x17 // two stack-neutral pairs
    ];

    public static object PatchMain(string input, string output)
    {
        EnsureDifferentPaths(input, output);
        var bytes = File.ReadAllBytes(input);
        VerifyHash(bytes, MainSha256, "main-menu APT");
        var start = FindUnique(bytes, MainStockFunction);
        if (start != 11616) throw new InvalidDataException("The main-menu function moved in the source APT.");
        MainAutomaticFunction.CopyTo(bytes, start);
        File.WriteAllBytes(output, bytes);
        return new { status = "patched-in-place", screen = "main", output, bytes = bytes.Length, changedOffset = start + 10 };
    }

    public static object Skirmish(string input, string output)
    {
        EnsureDifferentPaths(input, output);
        var bytes = File.ReadAllBytes(input);
        VerifyHash(bytes, SkirmishConstSha256, "skirmish CONST");
        var count = Read32(bytes, 0x18);
        if (count <= 339 || Read32(bytes, 0x1C) != 0x20)
            throw new InvalidDataException("Unexpected skirmish CONST table layout.");
        var playGame = ReadStringItem(bytes, 97, "playGame");
        _ = ReadStringItem(bytes, 339, "startIntro");
        var pointerAt = checked(0x20 + 339 * 8 + 4);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(pointerAt, 4), playGame);
        File.WriteAllBytes(output, bytes);
        return new { status = "patched-in-place", screen = "skirmish", output, bytes = bytes.Length, changedOffset = pointerAt };
    }

    public static object ProbeFlatMenuExit(string input, string output)
    {
        EnsureDifferentPaths(input, output);
        var bytes = File.ReadAllBytes(input);
        VerifyHash(bytes, FlatMenuConstSha256, "flat main-menu CONST");
        var quitSelected = ReadStringItem(bytes, 73, "quitSelected");
        _ = ReadStringItem(bytes, 83, "startIntro");
        var pointerAt = checked(0x20 + 83 * 8 + 4);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(pointerAt, 4), quitSelected);
        File.WriteAllBytes(output, bytes);
        return new { status = "probe-patched-in-place", screen = "flat-main", output, bytes = bytes.Length, changedOffset = pointerAt };
    }

    public static object ProbeRootClass(string input, string output)
    {
        EnsureDifferentPaths(input, output);
        var bytes = File.ReadAllBytes(input);
        VerifyHash(bytes, MainSha256, "main-menu APT");
        var rootAction = new byte[]
        {
            0xA1, 0x00, 0x00, 0x00, 0x80, 0x48, 0x00, 0x00,
            0x70, 0x5A, 0xA1, 0x00, 0x88, 0x48, 0x00, 0x00,
            0x40, 0x3C, 0x07, 0x00
        };
        var start = FindUnique(bytes, rootAction);
        if (start != 9552) throw new InvalidDataException("The root menu action moved in the source APT.");
        // Diagnostic only: instantiate the nonexistent 'screen' class instead
        // of feg_m_mainMenu3D. A missing menu proves the overlay is selected.
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(start + 12, 4), 0x4880);
        File.WriteAllBytes(output, bytes);
        return new { status = "probe-patched-in-place", screen = "main-root", output, changedOffset = start + 12 };
    }

    private static uint ReadStringItem(byte[] bytes, int index, string expected)
    {
        var item = checked(0x20 + index * 8);
        if (Read32(bytes, item) != 1) throw new InvalidDataException($"CONST item {index} is not a string.");
        var pointer = Read32(bytes, item + 4);
        if (pointer >= bytes.Length) throw new InvalidDataException($"CONST item {index} is out of bounds.");
        var end = Array.IndexOf(bytes, (byte)0, checked((int)pointer));
        if (end < 0) throw new InvalidDataException($"CONST item {index} is not terminated.");
        var value = Encoding.ASCII.GetString(bytes, checked((int)pointer), end - checked((int)pointer));
        if (value != expected) throw new InvalidDataException($"CONST item {index} is '{value}', expected '{expected}'.");
        return pointer;
    }

    private static uint Read32(byte[] bytes, int offset)
    {
        if (offset < 0 || offset > bytes.Length - 4) throw new InvalidDataException("CONST field is out of bounds.");
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
    }

    private static int FindUnique(byte[] bytes, byte[] pattern)
    {
        var found = -1;
        for (var i = 0; i <= bytes.Length - pattern.Length; i++)
        {
            if (!bytes.AsSpan(i, pattern.Length).SequenceEqual(pattern)) continue;
            if (found >= 0) throw new InvalidDataException("Main-menu function pattern occurs more than once.");
            found = i;
        }
        if (found < 0) throw new InvalidDataException("Main-menu function pattern was not found.");
        return found;
    }

    private static void VerifyHash(byte[] bytes, string expected, string label)
    {
        var actual = Convert.ToHexString(SHA256.HashData(bytes));
        if (actual != expected) throw new InvalidDataException($"The {label} does not match the tested EA source pack (SHA256 {actual}).");
    }

    private static void EnsureDifferentPaths(string input, string output)
    {
        if (Path.GetFullPath(input).Equals(Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Input and output paths must differ.");
    }
}
