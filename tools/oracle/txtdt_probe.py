"""txtdt_probe.py - does a DVD carry its own title text (TXTDT_MGI)?

    python txtdt_probe.py <path to VIDEO_TS.IFO>

The VMG header holds, at 0xD4, the start sector of the text data manager
(TXTDT_MGI), relative to the start of VIDEO_TS.IFO; 0 means the disc has none.
Layout from libdvdread src/dvdread/ifo_types.h (txtdt_mgi_t, txtdt_lu_t), where
it is marked "Incomplete": a 20-byte header (disc name[12], unknown u16,
number of language units u16, last byte u32), then 8 bytes per language unit
(language code[2], zero, character set, start byte u32 relative to TXTDT_MGI).

Everything past the language-unit table is undocumented, so each unit's bytes
are printed as a hex dump with the readable text beside it, not decoded.
"""

import struct
import sys

SECTOR = 2048
TXTDT_MGI_POINTER = 0xD4

CHAR_SETS = {0x00: "Unicode", 0x01: "ISO 646", 0x10: "JIS Roman & JIS Kanji",
             0x11: "ISO 8859-1", 0x12: "Shift JIS Kanji"}


def dump(data, base):
    for i in range(0, len(data), 16):
        row = data[i:i + 16]
        hexes = " ".join(f"{b:02x}" for b in row)
        text = "".join(chr(b) if 32 <= b < 127 else "." for b in row)
        print(f"    {base + i:06x}  {hexes:<47}  {text}")


def main(argv):
    if len(argv) != 2:
        print(__doc__)
        return 2

    with open(argv[1], "rb") as f:
        ifo = f.read()

    if ifo[:12] != b"DVDVIDEO-VMG":
        print(f"{argv[1]} is not a VIDEO_TS.IFO (no DVDVIDEO-VMG identifier)")
        return 1

    sector = struct.unpack_from(">I", ifo, TXTDT_MGI_POINTER)[0]
    print(f"TXTDT_MGI pointer at 0x{TXTDT_MGI_POINTER:X}: sector {sector}")
    if sector == 0:
        print("This disc has no text data.")
        return 0

    start = sector * SECTOR
    if start + 20 > len(ifo):
        print(f"Sector {sector} is past the end of the file ({len(ifo):,} bytes).")
        return 1

    name, _unknown, units, last_byte = struct.unpack_from(">12sHHI", ifo, start)
    print(f"disc name field: {name!r}")
    print(f"language units: {units}; last byte: {last_byte}")

    end = min(start + last_byte + 1, len(ifo))
    unit_starts = []
    for i in range(units):
        lang, _zero, char_set, rel = struct.unpack_from(">2sBBI", ifo, start + 20 + i * 8)
        unit_starts.append(rel)
        print(f"  unit {i + 1}: language {lang.decode('ascii', 'replace')!r}, "
              f"character set 0x{char_set:02x} ({CHAR_SETS.get(char_set, 'unknown')}), "
              f"starts at byte {rel}")

    for i, rel in enumerate(unit_starts):
        unit_end = start + unit_starts[i + 1] if i + 1 < len(unit_starts) else end
        print(f"\nunit {i + 1} bytes:")
        dump(ifo[start + rel:unit_end], rel)

    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
