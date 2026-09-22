// ============================================================
// NavPack.cs
// ------------------------------------------------------------
// Reads a menu's buttons out of one 2,048-byte VOB sector: the
// NAV pack's PCI highlight information (HLI), with each button's
// rectangle, arrow-key neighbours and command.
//
// A straight port of the oracle's is_nav_pack / parse_buttons /
// rects_sane (tools\oracle\dvdmenumap.py). Offsets follow
// libdvdread's nav_types.h by way of the oracle; do not adjust one
// from memory, fetch the source.
//
// Pure: a sector in, buttons out. No I/O.
// ============================================================

namespace TranscodeTools;

public sealed record NavButton(
    int Number, int X0, int Y0, int X1, int Y1,
    int Up, int Down, int Left, int Right,
    VmCommand Command);

public static class NavPack
{
    public const int SectorSize = 2048;

    private const int PciOffset   = 0x2D;                       // PCI_GI start within a NAV pack sector
    private const int HliOffset   = PciOffset + 0x3C + 0x24;    // pci_gi (60) + nsml_agli (36)
    private const int BtnitOffset = HliOffset + 0x16 + 0x18;    // hl_gi (22) + btn_colit (24)
    private const int PciPes      = 0x26;                       // after pack header (14) + system header (24)
    private const int DsiPes      = 0x400;

    // Largest picture a DVD carries (PAL). A rectangle outside it is junk.
    private const int MaxWidth  = 720;
    private const int MaxHeight = 576;

    // A NAV pack is a pack header + system header + a PCI private-stream-2
    // PES + a DSI one. Checking only the pack start code matches every
    // video pack on the disc, which is what once produced nonsense buttons.
    public static bool IsNavPack(ReadOnlySpan<byte> b)
    {
        if (b.Length < SectorSize) return false;
        if (!(b[0] == 0x00 && b[1] == 0x00 && b[2] == 0x01 && b[3] == 0xBA)) return false;
        if (!(b[PciPes] == 0x00 && b[PciPes + 1] == 0x00 && b[PciPes + 2] == 0x01 && b[PciPes + 3] == 0xBF)) return false;
        if (U16(b, PciPes + 4) != 0x03D4 || b[PciPes + 6] != 0x00) return false;
        if (!(b[DsiPes] == 0x00 && b[DsiPes + 1] == 0x00 && b[DsiPes + 2] == 0x01 && b[DsiPes + 3] == 0xBF)) return false;
        if (U16(b, DsiPes + 4) != 0x03FA || b[DsiPes + 6] != 0x01) return false;
        return true;
    }

    // The button list from a NAV pack sector, or null when the sector is
    // not a NAV pack, carries no highlight information, or its buttons are
    // mostly junk.
    public static IReadOnlyList<NavButton>? ParseButtons(ReadOnlySpan<byte> b)
    {
        if (!IsNavPack(b)) return null;

        int hliSs = U16(b, HliOffset) & 0x03;
        if (hliSs == 0) return null;

        int btnNs = b[HliOffset + 0x11] & 0x3F;
        if (btnNs < 1 || btnNs > 36) return null;

        var buttons = new List<NavButton>(btnNs);
        for (int i = 0; i < btnNs; i++)
        {
            int o  = BtnitOffset + i * 18;
            int w0 = (b[o] << 16) | (b[o + 1] << 8) | b[o + 2];
            int w1 = (b[o + 3] << 16) | (b[o + 4] << 8) | b[o + 5];
            int x0 = (w0 >> 12) & 0x3FF, x1 = w0 & 0x3FF;
            int y0 = (w1 >> 12) & 0x3FF, y1 = w1 & 0x3FF;

            var command = VmCommand.Decode(b.Slice(o + 10, 8));
            if (x1 <= x0 || y1 <= y0) continue;

            buttons.Add(new NavButton(
                i + 1, x0, y0, x1, y1,
                b[o + 6] & 0x3F, b[o + 7] & 0x3F, b[o + 8] & 0x3F, b[o + 9] & 0x3F,
                command));
        }

        // Keep the good buttons; reject the set only if most of it is junk.
        var good = buttons.Where(RectOk).ToList();
        if (good.Count < Math.Max(1, buttons.Count / 2)) return null;

        return good.OrderBy(x => x.Number).ToList();
    }

    private static bool RectOk(NavButton x) =>
        0 <= x.X0 && x.X0 < x.X1 && x.X1 <= MaxWidth &&
        0 <= x.Y0 && x.Y0 < x.Y1 && x.Y1 <= MaxHeight;

    private static int U16(ReadOnlySpan<byte> b, int o) => (b[o] << 8) | b[o + 1];
}
