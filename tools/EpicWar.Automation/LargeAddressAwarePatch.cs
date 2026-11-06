using System.Buffers.Binary;

namespace EpicWar.Automation
{
    internal static class LargeAddressAwarePatch
    {
        public static byte[] Apply(byte[] image, bool enabled)
        {
            ArgumentNullException.ThrowIfNull(image);
            if (image.Length < 64 || image[0] != 'M' || image[1] != 'Z')
            {
                throw new InvalidDataException("The engine is not a Windows executable.");
            }

            var pe = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(60, 4));
            if (pe < 64 || pe > image.Length - 26 ||
                !image.AsSpan(pe, 4).SequenceEqual("PE\0\0"u8) ||
                BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(pe + 4, 2)) != 0x14c ||
                BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(pe + 24, 2)) != 0x10b)
            {
                throw new InvalidDataException("4 GB support requires a recognized 32-bit PE engine.");
            }

            var output = (byte[])image.Clone();
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(output.AsSpan(pe + 22, 2));
            flags = enabled ? (ushort)(flags | 0x20) : (ushort)(flags & ~0x20);
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(pe + 22, 2), flags);
            return output;
        }
    }
}
