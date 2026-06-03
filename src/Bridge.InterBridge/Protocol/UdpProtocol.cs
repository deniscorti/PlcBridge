using System.Text;

namespace Bridge.InterBridge.Protocol;

/// <summary>
/// Constants and helpers for the inter-bridge UDP binary protocol.
/// </summary>
public static class UdpProtocol
{
    public const ushort Magic = 0xBD01;
    public const byte Version = 0x01;
    public const int HeaderSize = 24;
    public const int MaxPayloadSize = 1376; // 1400 - HeaderSize
    public const int MaxPacketSize = 1400;

    // Flags bits
    public const byte FlagTelemetry = 0x00;
    public const byte FlagEvent = 0x01;
    public const byte FlagAlarm = 0x02;
    public const byte FlagFragmented = 0x04;
    public const byte FlagCompressed = 0x08;
    public const byte FlagMetadata = 0x10;
    public const byte FlagMapping = 0x20;

    // Value types
    public const byte TypeFloat32 = 0x01;
    public const byte TypeFloat64 = 0x02;
    public const byte TypeInt32 = 0x03;
    public const byte TypeBool = 0x04;
    public const byte TypeFloat32Array = 0x05;
    public const byte TypeFloat64Array = 0x06;

    public static uint Crc32(string input)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in Encoding.UTF8.GetBytes(input))
        {
            crc ^= b;
            for (int j = 0; j < 8; j++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return crc ^ 0xFFFFFFFF;
    }

    /// <summary>Write the 24-byte packet header.</summary>
    public static void WriteHeader(Span<byte> buf, byte flags, uint sourceId,
        long timestampMs, ushort groupSeq, byte fragIdx, byte fragTotal, uint msgIdBase)
    {
        buf[0] = (byte)(Magic >> 8);
        buf[1] = (byte)(Magic & 0xFF);
        buf[2] = Version;
        buf[3] = flags;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buf[4..], sourceId);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(buf[8..], timestampMs);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(buf[16..], groupSeq);
        buf[18] = fragIdx;
        buf[19] = fragTotal;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buf[20..], msgIdBase);
    }

    /// <summary>Parse the 24-byte packet header. Returns false if invalid.</summary>
    public static bool TryReadHeader(ReadOnlySpan<byte> buf, out UdpPacketHeader header)
    {
        header = default;
        if (buf.Length < HeaderSize) return false;
        if (buf[0] != (byte)(Magic >> 8) || buf[1] != (byte)(Magic & 0xFF)) return false;

        header = new UdpPacketHeader
        {
            Version = buf[2],
            Flags = buf[3],
            SourceId = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(buf[4..]),
            TimestampMs = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(buf[8..]),
            GroupSeq = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(buf[16..]),
            FragIdx = buf[18],
            FragTotal = buf[19],
            MsgIdBase = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(buf[20..])
        };
        return true;
    }
}

public struct UdpPacketHeader
{
    public byte Version;
    public byte Flags;
    public uint SourceId;
    public long TimestampMs;
    public ushort GroupSeq;
    public byte FragIdx;
    public byte FragTotal;
    public uint MsgIdBase;

    public readonly byte KindBits => (byte)(Flags & 0x03);
    public readonly bool IsFragmented => (Flags & 0x04) != 0;
    public readonly bool IsCompressed => (Flags & 0x08) != 0;
    public readonly bool IsMetadata => (Flags & 0x10) != 0;
    public readonly bool IsMapping => (Flags & 0x20) != 0;
}
