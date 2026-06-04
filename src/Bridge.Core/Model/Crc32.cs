using System.Text;

namespace Bridge.Core.Model;

/// <summary>
/// CRC32 helper for computing compact identifiers from strings.
/// </summary>
public static class Crc32
{
    private static readonly uint[] Table = GenerateTable();

    private static uint[] GenerateTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var crc = i;
            for (int j = 0; j < 8; j++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            table[i] = crc;
        }
        return table;
    }

    public static uint Compute(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }
}
