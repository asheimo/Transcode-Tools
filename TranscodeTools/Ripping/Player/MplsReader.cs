// ============================================================
// MplsReader.cs
// ------------------------------------------------------------
// Reads the clip list of a Blu-ray playlist (.mpls).
//
// Layout follows libbluray's bdnav/mpls_parse.c:
//   - "MPLS" + 4-char version, then at byte 8 a big-endian offset to
//     the playlist section (u32 length, 2 reserved bytes, u16 item
//     count, u16 sub-path count).
//   - Each play item: u16 length, 5-char clip name, 4-char codec
//     name, 2 bytes of flags, 1 byte STC id, u32 in time, u32 out
//     time (45 kHz ticks), ...; the next item starts 2 + length bytes
//     later. Only each item's main clip is read (no angles).
//   - User-operation masks (libbluray uo_mask.c): 64 bits, first bit
//     menu call, fourth bit time search. The playlist's mask is at
//     byte 48 (AppInfoPlayList); each play item's follows its out
//     time.
//
// A file that fails any check throws InvalidDataException with the
// reason; the caller rejects that one playlist and carries on.
//
// Content (features, extras) has allowed time search on every disc
// checked; bumpers, interstitials, menus and start-up items block it,
// in the playlist mask or on a play item.
// ============================================================

using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace TranscodeTools;

public sealed record MplsPlaylist(IReadOnlyList<string> Clips, TimeSpan Length, bool BlocksTimeSearch);

public static class MplsReader
{
    private const byte TimeSearchBit = 0x10;
    private const int ItemFieldsRead = 23; // length, clip name, codec, flags, STC id, in, out, first mask byte

    public static MplsPlaylist Read(string mplsPath)
    {
        var data = File.ReadAllBytes(mplsPath);
        Require(data, 0, 4, "signature");
        if (Encoding.ASCII.GetString(data, 0, 4) != "MPLS")
            throw new InvalidDataException("not an MPLS file");
        Require(data, 8, 4, "playlist offset");
        Require(data, 48, 1, "playlist mask");

        var listPos = (long)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8));
        Require(data, listPos, 10, "playlist header");
        var blocksTimeSearch = (data[48] & TimeSearchBit) != 0;
        var count = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan((int)listPos + 6));
        var pos = listPos + 10;
        var clips = new List<string>(count);
        long ticks = 0;
        for (var i = 0; i < count; i++)
        {
            Require(data, pos, 2, $"play item {i} length");
            var at = (int)pos;
            var length = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at));
            if (2 + length < ItemFieldsRead)
                throw new InvalidDataException($"play item {i} is {2 + length} bytes, shorter than the {ItemFieldsRead} bytes read from it");
            Require(data, pos, 2 + length, $"play item {i}");

            clips.Add(Encoding.ASCII.GetString(data, at + 2, 5));
            var inTime = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at + 14));
            var outTime = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at + 18));
            if (outTime < inTime)
                throw new InvalidDataException($"play item {i} ends before it starts (in {inTime}, out {outTime})");
            ticks += outTime - inTime;
            blocksTimeSearch |= (data[at + 22] & TimeSearchBit) != 0;
            pos += 2 + length;
        }
        return new MplsPlaylist(clips, TimeSpan.FromSeconds(ticks / 45000.0), blocksTimeSearch);
    }

    private static void Require(byte[] data, long offset, int count, string what)
    {
        if (offset + count > data.Length)
            throw new InvalidDataException($"{what} runs past the end of the file ({data.Length} bytes)");
    }
}
