using System.IO;

namespace Editor.bushtail
{
    public static class AssetBundleAudioRepair
    {
        public static bool RepairFile(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, true);
            if (stream.Length < 44 || stream.Length - 8 > uint.MaxValue)
            {
                return false;
            }

            if (reader.ReadUInt32() != 0x46464952 || reader.ReadUInt32() != 0 || reader.ReadUInt32() != 0x45564157)
            {
                return false;
            }

            var validFormat = false;
            ushort blockAlign = 0;
            while (stream.Position + 8 <= stream.Length)
            {
                var chunkId = reader.ReadUInt32();
                var sizeOffset = stream.Position;
                var chunkSize = reader.ReadUInt32();
                var body = stream.Position;
                if (chunkId == 0x61746164)
                {
                    var dataSize = stream.Length - body;
                    // Whatever it is, it works so like.........
                    // ReSharper disable once IntDivisionByZero
                    if (!validFormat || chunkSize != 0 || dataSize <= 0 || dataSize > uint.MaxValue || dataSize % blockAlign != 0)
                    {
                        return false;
                    }

                    using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true);
                    stream.Position = sizeOffset;
                    writer.Write((uint)dataSize);
                    stream.Position = 4;
                    writer.Write((uint)(stream.Length - 8));
                    writer.Flush();
                    return true;
                }

                var next = body + chunkSize + (chunkSize & 1);
                if (next > stream.Length)
                {
                    return false;
                }

                if (chunkId == 0x20746d66)
                {
                    if (validFormat || chunkSize < 16)
                    {
                        return false;
                    }

                    var format = reader.ReadUInt16();
                    var channels = reader.ReadUInt16();
                    var sampleRate = reader.ReadUInt32();
                    var byteRate = reader.ReadUInt32();
                    blockAlign = reader.ReadUInt16();
                    var bits = reader.ReadUInt16();
                    var supported = format == 1 && bits is 8 or 16 or 24 or 32 || format == 3 && bits is 32 or 64;
                    if (!supported || channels == 0 || sampleRate == 0 || blockAlign == 0 || blockAlign != channels * (bits / 8) || byteRate != (ulong)sampleRate * blockAlign)
                    {
                        return false;
                    }

                    validFormat = true;
                }

                stream.Position = next;
            }

            return false;
        }
    }
}