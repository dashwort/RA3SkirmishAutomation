using System.Buffers.Binary;
using System.Text;
using System.Xml.Linq;

namespace EpicWar.Automation;

// APT stores file-relative pointers. Copying an existing action stream and
// changing its owning frame item's pointer leaves the stock movie untouched.
internal static class AptBinaryPatch
{
    public static object CloneAction(string inputApt, string inputConst, string outputApt)
    {
        if (Path.GetFullPath(inputApt).Equals(Path.GetFullPath(outputApt), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Input and output APT paths must differ.");

        var apt = File.ReadAllBytes(inputApt);
        var constants = File.ReadAllBytes(inputConst);
        if (constants.Length < 0x18 || apt.Length < 64)
            throw new InvalidDataException("APT or CONST file is too short.");

        var root = ReadPointer(constants, 0x14, apt.Length);
        if (Read32(apt, root) != 9 || Read32(apt, root + 4) != 0x09876543)
            throw new InvalidDataException("Expected a root APT movie at the CONST entry point.");
        if (Read32(apt, root + 8) != 1)
            throw new InvalidDataException("Expected a one-frame menu movie.");

        var frames = ReadPointer(apt, root + 12, apt.Length);
        var itemCount = checked((int)Read32(apt, frames));
        var items = ReadPointer(apt, frames + 4, apt.Length);
        if (itemCount is < 1 or > 1000 || items > apt.Length - itemCount * 4)
            throw new InvalidDataException("Invalid initial-frame item list.");

        var actionItems = new List<int>();
        for (var i = 0; i < itemCount; i++)
        {
            var item = ReadPointer(apt, items + i * 4, apt.Length);
            if (Read32(apt, item) == 1) actionItems.Add(item);
        }
        if (actionItems.Count != 1)
            throw new InvalidDataException($"Expected one initial-frame action item, found {actionItems.Count}.");

        var actionItem = actionItems[0];
        var actionStart = ReadPointer(apt, actionItem + 4, apt.Length);
        var actionEnd = FindActionEnd(apt, actionStart);
        var actionSize = actionEnd - actionStart;
        var copiedStart = Align4(apt.Length);
        var copy = new byte[checked(copiedStart + actionSize)];
        apt.CopyTo(copy, 0);
        Buffer.BlockCopy(apt, actionStart, copy, copiedStart, actionSize);
        BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(actionItem + 4, 4), (uint)copiedStart);
        File.WriteAllBytes(outputApt, copy);
        return new { sourceBytes = apt.Length, outputBytes = copy.Length, actionStart, actionSize, copiedStart };
    }

    public static object PatchAction(string inputApt, string inputConst, string outputApt, bool mainMenu)
    {
        if (Path.GetFullPath(inputApt).Equals(Path.GetFullPath(outputApt), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Input and output APT paths must differ.");

        var apt = File.ReadAllBytes(inputApt);
        var constants = File.ReadAllBytes(inputConst);
        if (constants.Length < 0x18 || apt.Length < 64)
            throw new InvalidDataException("APT or CONST file is too short.");

        var root = ReadPointer(constants, 0x14, apt.Length);
        if (Read32(apt, root) != 9 || Read32(apt, root + 4) != 0x09876543 || Read32(apt, root + 8) != 1)
            throw new InvalidDataException("Expected a one-frame root APT movie.");
        var frames = ReadPointer(apt, root + 12, apt.Length);
        var itemCount = checked((int)Read32(apt, frames));
        var items = ReadPointer(apt, frames + 4, apt.Length);
        if (itemCount is < 1 or > 1000 || items > apt.Length - itemCount * 4)
            throw new InvalidDataException("Invalid initial-frame item list.");

        var actionItems = new List<int>();
        for (var i = 0; i < itemCount; i++)
        {
            var item = ReadPointer(apt, items + i * 4, apt.Length);
            if (Read32(apt, item) == 1) actionItems.Add(item);
        }
        if (actionItems.Count != 1)
            throw new InvalidDataException($"Expected one initial-frame action item, found {actionItems.Count}.");

        var actionItem = actionItems[0];
        var actionStart = ReadPointer(apt, actionItem + 4, apt.Length);
        var actionEnd = FindActionEnd(apt, actionStart);
        if (actionEnd - actionStart < 2 || apt[actionEnd - 2] != 0x07 || apt[actionEnd - 1] != 0x00)
            throw new InvalidDataException("The stock menu constructor does not end in STOP, END.");
        var copiedStart = Align4(apt.Length);
        var (replacement, actionByteCount) = BuildPatchedAction(apt.AsSpan(actionStart, actionEnd - actionStart), copiedStart, mainMenu);
        var copy = new byte[checked(copiedStart + replacement.Length)];
        apt.CopyTo(copy, 0);
        replacement.CopyTo(copy, copiedStart);
        BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(actionItem + 4, 4), (uint)copiedStart);
        if (FindActionEnd(copy, copiedStart) != copiedStart + actionByteCount)
            throw new InvalidDataException("The patched action stream did not parse to its intended end.");
        File.WriteAllBytes(outputApt, copy);
        return new
        {
            sourceBytes = apt.Length,
            outputBytes = copy.Length,
            actionStart,
            stockActionBytes = actionEnd - actionStart,
            patchedActionBytes = actionByteCount,
            copiedStart,
            screen = mainMenu ? "main" : "skirmish"
        };
    }

    private static (byte[] Data, int ActionByteCount) BuildPatchedAction(ReadOnlySpan<byte> stock, int absoluteStart, bool mainMenu)
    {
        var bytes = new List<byte>(stock.Length + 256);
        for (var i = 0; i < stock.Length - 2; i++) bytes.Add(stock[i]);
        var strings = new List<(int PatchAt, string Value)>();
        var sourcePosition = 0;
        var positions = new Dictionary<int, int>();
        var fixups = new List<(int Field, int ActualBase, int SourceTarget)>();
        var instructions = AptPatch.CreateAction(mainMenu).Elements().ToArray();

        void Emit(byte value) => bytes.Add(value);
        void Emit32(uint value)
        {
            var data = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(data, value);
            bytes.AddRange(data);
        }
        void Align()
        {
            while ((absoluteStart + bytes.Count) % 4 != 0) Emit(0);
        }
        void ReserveString(string value)
        {
            strings.Add((bytes.Count, value));
            Emit32(0);
        }
        void WriteAt(int offset, uint value)
        {
            var data = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(data, value);
            for (var j = 0; j < 4; j++) bytes[offset + j] = data[j];
        }

        for (var index = 0; index < instructions.Length; index++)
        {
            var instruction = instructions[index];
            positions.Add(sourcePosition, absoluteStart + bytes.Count);

            var opcode = checked((byte)(int?)instruction.Attribute("action")!);
            Emit(opcode);
            sourcePosition++;
            switch (instruction.Name.LocalName)
            {
                case "noarg": break;
                case "string":
                    sourcePosition = Align4(sourcePosition) + 4;
                    Align();
                    ReserveString((string?)instruction.Attribute("str") ?? "");
                    break;
                case "byte":
                    sourcePosition++;
                    Emit(checked((byte)(int?)instruction.Attribute("val")!));
                    break;
                case "goto":
                    sourcePosition = Align4(sourcePosition) + 4;
                    Align();
                    fixups.Add((bytes.Count, absoluteStart + bytes.Count + 4,
                        sourcePosition + (int)instruction.Attribute("pos")!));
                    Emit32(0);
                    break;
                case "definefunction2":
                    sourcePosition = Align4(sourcePosition) + 28;
                    Align();
                    ReserveString((string?)instruction.Attribute("name") ?? "");
                    Emit32(0); // No arguments.
                    Emit32(checked((uint)(int?)instruction.Attribute("flags")!));
                    Emit32(0); // No argument array.
                    fixups.Add((bytes.Count, absoluteStart + bytes.Count + 12,
                        sourcePosition + (int)instruction.Attribute("size")!));
                    Emit32(0);
                    Emit32(0x98765432);
                    Emit32(0x12345678);
                    break;
                case "geturl":
                    sourcePosition = Align4(sourcePosition) + 8;
                    Align();
                    ReserveString((string?)instruction.Attribute("str1") ?? "");
                    ReserveString((string?)instruction.Attribute("str2") ?? "");
                    break;
                default:
                    throw new InvalidDataException($"Unsupported injected action: {instruction.Name.LocalName}");
            }
        }

        positions.Add(sourcePosition, absoluteStart + bytes.Count);
        foreach (var fixup in fixups)
        {
            if (!positions.TryGetValue(fixup.SourceTarget, out var target))
                throw new InvalidDataException("Injected action target is not an instruction boundary.");
            WriteAt(fixup.Field, unchecked((uint)(target - fixup.ActualBase)));
        }

        Emit(0x07); // Stock STOP, END suffix.
        Emit(0x00);
        var actionByteCount = bytes.Count;
        var stringOffsets = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (patchAt, value) in strings)
        {
            if (!stringOffsets.TryGetValue(value, out var offset))
            {
                offset = absoluteStart + bytes.Count;
                stringOffsets.Add(value, offset);
                bytes.AddRange(Encoding.ASCII.GetBytes(value));
                Emit(0);
            }
            WriteAt(patchAt, checked((uint)offset));
        }
        return (bytes.ToArray(), actionByteCount);
    }

    private static int FindActionEnd(byte[] apt, int start)
    {
        var pos = start;
        for (var instruction = 0; instruction < 100_000 && pos < apt.Length; instruction++)
        {
            var opcode = apt[pos++];
            switch (opcode)
            {
                case 0x00: return pos;
                case 0x99 or 0x9d or 0xb8 or 0x81 or 0x87 or 0x94 or 0x9f:
                case 0xa1 or 0xa4 or 0xa5 or 0xa6 or 0xa7 or 0x8b or 0x8c:
                    pos = checked(Align4(pos) + 4); break;
                case 0x83 or 0x88 or 0x96:
                    pos = checked(Align4(pos) + 8); break;
                case 0x8e:
                    pos = checked(Align4(pos) + 28); break;
                case 0x9b:
                    pos = checked(Align4(pos) + 24); break;
                case 0xb0 or 0xb1 or 0xb2 or 0xb3 or 0xa2 or 0xae or 0xaf or 0xb5 or 0xb9:
                    pos++; break;
                case 0xa3 or 0xb6:
                    pos += 2; break;
                case 0xb4 or 0xb7:
                    pos += 4; break;
            }
            if (pos > apt.Length)
                throw new InvalidDataException("An action operand exceeds the APT file.");
        }
        throw new InvalidDataException("The menu action stream has no terminating END opcode.");
    }

    private static uint Read32(byte[] data, int offset)
    {
        if (offset < 0 || offset > data.Length - 4) throw new InvalidDataException("APT pointer is out of bounds.");
        return BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
    }

    private static int ReadPointer(byte[] data, int offset, int targetSize)
    {
        var value = Read32(data, offset);
        if (value > targetSize - 4) throw new InvalidDataException("APT pointer is out of bounds.");
        return checked((int)value);
    }

    private static int Align4(int value) => checked((value + 3) & ~3);
}
