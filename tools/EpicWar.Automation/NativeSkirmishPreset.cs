using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace EpicWar.Automation;

// Schema from original 1.12 serializer 6A1A10 and parser 6A1D40.
internal sealed record NativeSkirmishSlot(int Type, string? Name, int[] Attributes);
internal sealed record NativeSkirmishPreset(string GameInfo, string Map, uint MapPrefix, uint MapCrc,
    int MapSize, int Seed, uint GameId, int GameType, int Pc, int[] Rules, NativeSkirmishSlot[] Slots)
{
    internal static NativeSkirmishPreset Parse(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, true);
        var ini = reader.ReadToEnd().Split('\n').Where(l => l.Contains('=') && !l.TrimStart().StartsWith(';'))
            .Select(l => l.Split('=', 2)).ToDictionary(p => p[0].Trim().TrimStart('\uFEFF'), p => p[1].Trim(), StringComparer.OrdinalIgnoreCase);
        if (!ini.TryGetValue("Rts:GameInfo", out var info) || !ini.TryGetValue("Rts:Map", out var map))
            throw new InvalidDataException("Complete Rts:GameInfo and Rts:Map are required.");
        var fields = info.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split('=', 2))
            .ToDictionary(p => p[0], p => p.Length == 2 ? p[1] : throw new InvalidDataException("Malformed GameInfo field."), StringComparer.Ordinal);
        string Get(string key) => fields.TryGetValue(key, out var value) ? value : throw new InvalidDataException($"Missing GameInfo {key}.");
        static int Number(string s) => int.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture);
        static uint Hex(string s) => uint.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var reference = Get("M");
        if (reference.Length <= 3 || !Regex.IsMatch(reference[..3], "^[0-9a-fA-F]{3}$"))
            throw new InvalidDataException("Map reference needs its three hexadecimal metadata digits.");
        var rules = Get("RU").Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Number).ToArray();
        if (rules.Length != 14) throw new InvalidDataException("Exactly fourteen native rules are required.");
        var serializedSlots = Get("S").Split(':').ToList();
        if (serializedSlots[^1] == "") serializedSlots.RemoveAt(serializedSlots.Count - 1);
        if (serializedSlots.Count != 6) throw new InvalidDataException("Exactly six native slot descriptors are required.");
        var slots = serializedSlots.Select(s =>
        {
            if (s == "X") return new NativeSkirmishSlot(1, null, []);
            if (s == "O") return new NativeSkirmishSlot(0, null, []);
            var parts = s.Split(',');
            if (parts[0].StartsWith('H') && parts.Length == 12)
                return new NativeSkirmishSlot(6, parts[0][1..], parts.Skip(4).Take(7).Select(Number).ToArray());
            if (parts.Length == 7 && parts[0].Length == 2 && parts[0][0] == 'C')
            {
                var type = parts[0][1] switch { 'E' => 2, 'M' => 3, 'H' => 4, 'B' => 5, _ => throw new InvalidDataException("Unknown AI difficulty.") };
                return new NativeSkirmishSlot(type, null, parts.Skip(1).Select(Number).ToArray());
            }
            throw new InvalidDataException("Unsupported native slot descriptor.");
        }).ToArray();
        if (slots.Count(s => s.Type == 6) != 1) throw new InvalidDataException("A vanilla skirmish requires exactly one human.");
        var decoded = Regex.Replace(map, "_([0-9A-Fa-f]{2})", m => ((char)Convert.ToByte(m.Groups[1].Value, 16)).ToString());
        var result = new NativeSkirmishPreset(info, decoded, Hex(reference[..3]), Hex(Get("MC")), Number(Get("MS")),
            Number(Get("SD")), Hex(Get("GSID")), Number(Get("GT")), Number(Get("PC")), rules, slots);
        var mapDirectory = decoded.Replace('/', '\\');
        mapDirectory = mapDirectory[..mapDirectory.LastIndexOf('\\')];
        if (!mapDirectory.Equals(reference[3..].Replace('/', '\\'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("GameInfo map and Rts:Map disagree.");
        return result;
    }

    internal void VerifySession(uint session, Func<uint, int, byte[]> read)
    {
        var bytes = read(session, 0x100);
        uint U(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
        int I(int offset) => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4));
        void Require(bool matches, string name) { if (!matches) throw new InvalidDataException($"Effective native {name} differs from the complete INI; Start was refused."); }
        Require(U(0x48) == MapPrefix, "map metadata prefix"); Require(U(0x40) == MapCrc, "map CRC");
        Require(I(0x44) == MapSize, "map size"); Require(I(0x4C) == Seed, "seed");
        Require(U(0x94) == GameId, "game ID"); Require(I(0x58) == GameType, "game type");
        Require(unchecked((sbyte)bytes[0xFA]) == Pc, "PC setting");
        Require(ReadText(U(0x34), false, read).Equals(Map, StringComparison.OrdinalIgnoreCase), "map path");
        for (int i = 0; i < Rules.Length; i++) Require(I(0x5C + 4 * i) == Rules[i], $"rule {i}");
        for (int i = 0; i < Slots.Length; i++)
        {
            var slot = Slots[i]; uint pointer = U(4 + i * 4);
            Require(pointer != 0, $"slot {i} allocation");
            var native = read(pointer, 0x60);
            int Value(int offset) => BinaryPrimitives.ReadInt32LittleEndian(native.AsSpan(offset, 4));
            Require(Value(4) == slot.Type, $"slot {i} type/difficulty");
            if (slot.Type is 0 or 1) continue; // Native normalizes unused attributes on empty slots.
            var offsets = slot.Type == 6 ? new[] { 0xC, 0x18, 0x10, 0x1C, 0x20, 0x40, 0x4C } : new[] { 0xC, 0x18, 0x10, 0x1C, 0x20, 0x4C };
            for (int j = 0; j < offsets.Length; j++) Require(Value(offsets[j]) == slot.Attributes[j], $"slot {i} attribute +{offsets[j]:X}");
            if (slot.Type == 6)
                Require(ReadText(unchecked((uint)Value(0x30)), true, read) == slot.Name, $"slot {i} human name");
        }
        // Connection IDs, ports and readiness bits follow the stock lobby lifecycle.
        // They are not faction, position, team, handicap or AI configuration.
    }

    internal void VerifyTemplates(Func<uint, int, byte[]> read)
    {
        uint U(uint address) => BinaryPrimitives.ReadUInt32LittleEndian(read(address, 4));
        uint manager = U(0xCEDE30);
        if (manager == 0) throw new InvalidDataException("Native player template manager is missing.");
        uint first = U(manager + 0x24), last = U(manager + 0x28);
        if (first == 0 || last < first || (last - first) % 4 != 0 || (last - first) / 4 is < 1 or > 64)
            throw new InvalidDataException("Native player template catalog is invalid.");
        for (int i = 0; i < Slots.Length; i++)
        {
            var slot = Slots[i]; if (slot.Type is 0 or 1) continue;
            int index = slot.Attributes[1];
            if (index < 0 || (uint)index >= (last - first) / 4)
                throw new InvalidDataException($"Slot {i} has an unknown faction index; Start was refused.");
            uint wrapper = U(first + 4 * (uint)index);
            if (wrapper == 0) throw new InvalidDataException($"Slot {i} player template is missing.");
            uint definition = U(wrapper);
            if (definition == 0) throw new InvalidDataException($"Slot {i} player template definition is missing.");
            uint type = U(definition + 4);
            // Original 877720 identifies playable/random (1/3). Observer (2)
            // can occupy the human slot, but is not an AI faction.
            if (type is not (1 or 3) && !(slot.Type == 6 && type == 2))
                throw new InvalidDataException($"Slot {i} faction {index} is not a supported {(slot.Type == 6 ? "human" : "AI")} template; Start was refused.");
        }
    }

    private static string ReadText(uint pointer, bool wide, Func<uint, int, byte[]> read)
    {
        if (pointer < 4) throw new InvalidDataException("Native string pointer is invalid.");
        int length = BinaryPrimitives.ReadUInt16LittleEndian(read(pointer - 4, 2));
        if (length is < 1 or > 1023) throw new InvalidDataException("Native string length is invalid.");
        return (wide ? Encoding.Unicode : Encoding.ASCII).GetString(read(pointer, length * (wide ? 2 : 1)));
    }
}
