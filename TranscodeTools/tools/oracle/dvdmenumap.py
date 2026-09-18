#!/usr/bin/env python3
"""
dvdmenumap.py - extract the button -> title map from a DVD's menus,
plus a screenshot of each menu and a cropped image of each button.

Reads the IFO menu program chains, locates the menu VOB cells, parses the
PCI/HLI navigation packets for button rectangles and VM commands, and decodes
those commands (JumpTT / JumpVTS_TT / JumpVTS_PTT / LinkPGCN / ...).

Buttons rarely name a title directly: they set a GPRM and link to another
PGC, whose pre/post commands compare that GPRM and jump. So the PGC command
tables are dumped in full, and a small VM walks each chain from the button to
the title it eventually plays.

Offsets and the VM opcode decode are taken from libdvdread's ifo_types.h /
nav_types.h and libdvdnav's vm/vmcmd.c.

Usage:
    python dvdmenumap.py <VIDEO_TS dir> [-o OUTDIR] [--no-images]
    python dvdmenumap.py --disc THE_CENTENNIAL
    python dvdmenumap.py --selftest

Requires: Python 3.8+, and ffmpeg on PATH for the image steps.
Works identically on Windows and WSL. For an ISO, mount it first
(Windows: double-click the ISO, then point at D:\\VIDEO_TS).
"""

import argparse
import csv
import itertools
import json
import os
import re
import shutil
import string
import struct
import subprocess
import sys
import time
from pathlib import Path

SECTOR = 2048
PROBE = False
_PROBE_FH = None
PCI_OFFSET = 0x2D          # PCI_GI start within a NAV pack sector
HLI_OFFSET = PCI_OFFSET + 0x3C + 0x24   # pci_gi(60) + nsml_agli(36)
BTNIT_OFFSET = HLI_OFFSET + 0x16 + 0x18  # hl_gi(22) + btn_colit(24)

MENU_NAMES = {
    2: "title", 3: "root", 4: "subpicture", 5: "audio",
    6: "angle", 7: "chapter",   # 7 = PTT menu = scene selection
}

LINK_TABLE = [
    "LinkNoLink", "LinkTopC", "LinkNextC", "LinkPrevC",
    "", "LinkTopPG", "LinkNextPG", "LinkPrevPG",
    "", "LinkTopPGC", "LinkNextPGC", "LinkPrevPGC",
    "LinkGoUpPGC", "LinkTailPGC", "", "",
    "RSM",
]

CMP_OP_TABLE = ["", "&", "==", "!=", ">=", ">", "<=", "<"]

SET_OP_TABLE = ["", "=", "<->", "+=", "-=", "*=", "/=", "%=",
                "rnd", "&=", "|=", "^="]

SPRM_NAMES = [
    "MENU_LANG", "ASTN", "SPSTN", "AGLN", "TTN", "VTS_TTN", "TT_PGCN",
    "PTTN", "HL_BTNN", "NVTMR", "NV_PGCN", "KARAOKE_MIX", "CC_PLT", "PLT",
    "VIDEO_CFG", "AUDIO_CFG", "INIT_AUDIO_LANG", "INIT_AUDIO_LANG_EXT",
    "INIT_SPU_LANG", "INIT_SPU_LANG_EXT", "REGION", "RSV21", "RSV22", "RSV23",
]


# ---------------------------------------------------------------- primitives

def plog(msg):
    print(msg, file=sys.stderr)
    if _PROBE_FH:
        _PROBE_FH.write(msg + "\n")
        _PROBE_FH.flush()


def u8(b, o):
    return b[o]


def u16(b, o):
    return struct.unpack_from(">H", b, o)[0]


def u32(b, o):
    return struct.unpack_from(">I", b, o)[0]


def getbits(instr, start, count):
    """libdvdnav vm_getbits: `start` is the index of the field's MSB,
    bit 63 = MSB of byte 0 of the 8-byte command."""
    return (instr >> (start - count + 1)) & ((1 << count) - 1)


# ------------------------------------------------------------ VM instruction

def decode_command(cmd8):
    """Decode one 8-byte VM command.

    Straight port of libdvdnav vm/vmcmd.c :: vm_print_mnemonic(), including
    the if/set prefixes that the earlier version collapsed into "(set)".

    Returns (text, info).  info["op"] mirrors the terminating link/jump so
    existing consumers keep working; info["set"] / info["if"] carry the
    prefix parts, which are what the PGC dispatchers branch on.
    """
    instr = int.from_bytes(cmd8, "big")
    if instr == 0:
        return "Nop", {"op": "nop", "raw": cmd8.hex()}

    def gb(start, count):
        return getbits(instr, start, count)

    def reg_ref(r):
        if r & 0x80:
            return {"kind": "sprm", "n": r & 0x7F}
        return {"kind": "gprm", "n": r & 0x7F}

    def reg_name(r):
        if r & 0x80:
            n = r & 0x7F
            nm = SPRM_NAMES[n] if n < len(SPRM_NAMES) else f"SPRM{n}"
            return f"{nm}(s[{n}])"
        return f"g[{r & 0x7F}]"

    def ref_name(ref):
        if ref["kind"] == "imm":
            return f"0x{ref['v']:x}"
        if ref["kind"] == "sprm":
            n = ref["n"]
            nm = SPRM_NAMES[n] if n < len(SPRM_NAMES) else f"SPRM{n}"
            return f"{nm}(s[{n}])"
        return f"g[{ref['n']}]"

    def reg_or_data(imm, start):
        if imm:
            return {"kind": "imm", "v": gb(start, 16)}
        return reg_ref(gb(start - 8, 8))

    def reg_or_data_2(imm, start):
        if imm:
            return {"kind": "imm", "v": gb(start - 1, 7)}
        return {"kind": "gprm", "n": gb(start - 4, 4)}

    def reg_or_data_3(imm, start):
        if imm:
            return {"kind": "imm", "v": gb(start, 16)}
        return reg_ref(gb(start, 8))

    info = {"raw": cmd8.hex()}
    out = []

    # ---- if prefixes (vmcmd.c print_if_version_N) -------------------------
    def cmp_name(op):
        return CMP_OP_TABLE[op] if op < len(CMP_OP_TABLE) else f"cmp?{op}"

    def emit_if(lhs, op, rhs):
        out.append(f"if ({ref_name(lhs)} {cmp_name(op)} {ref_name(rhs)}) ")
        info["if"] = {"lhs": lhs, "op": cmp_name(op), "op_n": op, "rhs": rhs}

    def g(n):
        return {"kind": "gprm", "n": n}

    def if_v1():
        op = gb(54, 3)
        if op:
            emit_if(g(gb(39, 8)), op, reg_or_data(gb(55, 1), 31))

    def if_v2():
        op = gb(54, 3)
        if op:
            emit_if(reg_ref(gb(15, 8)), op, reg_ref(gb(7, 8)))

    def if_v3():
        op = gb(54, 3)
        if op:
            emit_if(g(gb(43, 4)), op, reg_or_data(gb(55, 1), 15))

    def if_v4():
        op = gb(54, 3)
        if op:
            emit_if(g(gb(51, 4)), op, reg_or_data(gb(55, 1), 31))

    def if_v5():
        op = gb(54, 3)
        if not op:
            return
        if gb(60, 1):
            emit_if(g(gb(31, 8)), op, reg_ref(gb(23, 8)))
        else:
            emit_if(g(gb(39, 8)), op, reg_or_data(gb(55, 1), 31))

    # ---- set parts (print_set_version_N / print_system_set) ---------------
    def set_name(op):
        return SET_OP_TABLE[op] if op < len(SET_OP_TABLE) else f"set?{op}"

    def emit_set(dst, op, src_ref):
        out.append(f"{ref_name(dst)} {set_name(op)} {ref_name(src_ref)}")
        info["set"] = {"dst": dst, "op": set_name(op), "op_n": op,
                       "src": src_ref}

    def set_v1():
        op = gb(59, 4)
        if not op:
            out.append("NOP")
            return
        emit_set(g(gb(35, 4)), op, reg_or_data(gb(60, 1), 31))

    def set_v2():
        op = gb(59, 4)
        if not op:
            out.append("NOP")
            return
        emit_set(g(gb(51, 4)), op, reg_or_data(gb(60, 1), 47))

    def set_v3():
        op = gb(59, 4)
        if not op:
            out.append("NOP")
            return
        emit_set(g(gb(51, 4)), op, reg_or_data_3(gb(60, 1), 47))

    def system_set():
        kind = gb(59, 4)
        sets = []
        if kind == 1:                      # SPRM 1/2/3: audio, subp, angle
            for i in (1, 2, 3):
                if gb(47 - i * 8, 1):
                    ref = reg_or_data_2(gb(60, 1), 47 - i * 8)
                    out.append(f"{SPRM_NAMES[i]}(s[{i}]) = {ref_name(ref)} ")
                    sets.append({"sprm": i, "src": ref})
        elif kind == 2:                    # SPRM 9/10: nav timer
            ref = reg_or_data(gb(60, 1), 47)
            out.append(f"NVTMR(s[9]) = {ref_name(ref)} "
                       f"NV_PGCN(s[10]) = {gb(30, 15)}")
            sets.append({"sprm": 9, "src": ref})
            sets.append({"sprm": 10, "src": {"kind": "imm", "v": gb(30, 15)}})
        elif kind == 3:                    # SetMode counter/register
            ref = reg_or_data(gb(60, 1), 47)
            mode = "Counter" if gb(23, 1) else "Register"
            out.append(f"SetMode {mode} g[{gb(19, 4)}] = {ref_name(ref)}")
            sets.append({"gprm": gb(19, 4), "mode": mode, "src": ref})
        elif kind == 6:                    # SPRM 8: highlighted button
            if gb(60, 1):
                ref = {"kind": "imm", "v": gb(31, 16)}
                out.append(f"HL_BTNN(s[8]) = 0x{gb(31, 16):x} "
                           f"(button {gb(31, 6)})")
            else:
                ref = {"kind": "gprm", "n": gb(19, 4)}
                out.append(f"HL_BTNN(s[8]) = {ref_name(ref)}")
            sets.append({"sprm": 8, "src": ref})
        else:
            out.append(f"WARNING: unknown system set {kind}")
        if sets:
            info["system_set"] = sets

    # ---- link / jump (print_link_instruction / print_jump_instruction) ----
    def linksub():
        linkop, btn = gb(7, 8), gb(15, 6)
        name = (LINK_TABLE[linkop] if linkop < len(LINK_TABLE)
                and LINK_TABLE[linkop] else f"Link?{linkop}")
        out.append(f"{name} (button {btn})")
        info.update({"op": "linksub", "sub": name, "button": btn})

    def link_instruction(optional):
        op = gb(51, 4)
        if op == 0:
            if not optional:
                out.append("WARNING: NOP (link)")
            return
        if optional:
            out.append(", ")
        if op == 1:
            linksub()
        elif op == 4:
            n = gb(14, 15)
            out.append(f"LinkPGCN {n}")
            info.update({"op": "LinkPGCN", "pgcn": n})
        elif op == 5:
            n, btn = gb(9, 10), gb(15, 6)
            out.append(f"LinkPTT {n} (button {btn})")
            info.update({"op": "LinkPTT", "ptt": n, "button": btn})
        elif op == 6:
            n, btn = gb(6, 7), gb(15, 6)
            out.append(f"LinkPGN {n} (button {btn})")
            info.update({"op": "LinkPGN", "pgn": n, "button": btn})
        elif op == 7:
            n, btn = gb(7, 8), gb(15, 6)
            out.append(f"LinkCN {n} (button {btn})")
            info.update({"op": "LinkCN", "cn": n, "button": btn})
        else:
            out.append(f"WARNING: unknown link op {op}")
            info.update({"op": "unknown_link", "sub_op": op})

    def jump_instruction():
        op = gb(51, 4)
        if op == 1:
            out.append("Exit")
            info.update({"op": "Exit"})
        elif op == 2:
            tt = gb(22, 7)
            out.append(f"JumpTT {tt}")
            info.update({"op": "JumpTT", "title": tt})
        elif op == 3:
            tt = gb(22, 7)
            out.append(f"JumpVTS_TT {tt}")
            info.update({"op": "JumpVTS_TT", "vts_title": tt})
        elif op == 5:
            tt, ptt = gb(22, 7), gb(41, 10)
            out.append(f"JumpVTS_PTT {tt}:{ptt}")
            info.update({"op": "JumpVTS_PTT", "vts_title": tt, "chapter": ptt})
        elif op in (6, 8):
            kind = "JumpSS" if op == 6 else "CallSS"
            which = gb(23, 2)
            rsm = f", rsm_cell {gb(31, 8)}" if op == 8 else ""
            base = {"op": kind}
            if op == 8:
                base["rsm_cell"] = gb(31, 8)
            if which == 0:
                out.append(f"{kind} FP{rsm}")
                base["target"] = "FP"
            elif which == 1:
                m = gb(19, 4)
                out.append(f"{kind} VMGM (menu {m}{rsm})")
                base.update({"target": "VMGM", "menu": m,
                             "menu_name": MENU_NAMES.get(m)})
            elif which == 2:
                m = gb(19, 4)
                if op == 6:
                    out.append(f"JumpSS VTSM (vts {gb(30, 7)}, "
                               f"title {gb(38, 7)}, menu {m})")
                    base.update({"target": "VTSM", "vts": gb(30, 7),
                                 "vts_title": gb(38, 7), "menu": m,
                                 "menu_name": MENU_NAMES.get(m)})
                else:
                    out.append(f"CallSS VTSM (menu {m}{rsm})")
                    base.update({"target": "VTSM", "menu": m,
                                 "menu_name": MENU_NAMES.get(m)})
            else:
                pgc = gb(46, 15)
                out.append(f"{kind} VMGM (pgc {pgc}{rsm})")
                base.update({"target": "VMGM", "pgcn": pgc})
            info.update(base)
        else:
            out.append(f"WARNING: unknown jump op {op}")
            info.update({"op": "unknown_jump", "sub_op": op})

    def special():
        op = gb(51, 4)
        if op == 0:
            out.append("Nop")
            info.update({"op": "nop"})
        elif op == 1:
            out.append(f"Goto {gb(7, 8)}")
            info.update({"op": "Goto", "line": gb(7, 8)})
        elif op == 2:
            out.append("Break")
            info.update({"op": "Break"})
        elif op == 3:
            out.append(f"SetTmpPML {gb(11, 4)}, Goto {gb(7, 8)}")
            info.update({"op": "SetTmpPML", "level": gb(11, 4),
                         "line": gb(7, 8)})
        else:
            out.append(f"WARNING: unknown special {op}")
            info.update({"op": "unknown_special", "sub_op": op})

    top = gb(63, 3)
    if top == 0:
        if_v1()
        special()
    elif top == 1:
        if gb(60, 1):
            if_v2()
            jump_instruction()
        else:
            if_v1()
            link_instruction(False)
    elif top == 2:
        if_v2()
        system_set()
        link_instruction(True)
    elif top == 3:
        if_v3()
        set_v1()
        link_instruction(True)
    elif top == 4:
        set_v2()
        out.append(", ")
        if_v4()
        linksub()
    elif top == 5:
        if_v5()
        out.append("{ ")
        set_v3()
        out.append(", ")
        linksub()
        out.append(" }")
    elif top == 6:
        if_v5()
        out.append("{ ")
        set_v3()
        out.append(" } ")
        linksub()
    else:
        out.append(f"WARNING: unknown instruction type {top}")
        info.setdefault("op", "unknown")

    info.setdefault("op", "other")
    info["text"] = "".join(out).strip()
    return info["text"], info


# ------------------------------------------------------------------ IFO bits

class Ifo:
    def __init__(self, path):
        self.path = Path(path)
        self.data = self.path.read_bytes()
        magic = self.data[:12]
        if magic == b"DVDVIDEO-VMG":
            self.kind = "vmg"
            self.menu_vobs = u32(self.data, 0xC0)
            self.tt_srpt = u32(self.data, 0xC4)
            self.pgci_ut = u32(self.data, 0xC8)
        elif magic == b"DVDVIDEO-VTS":
            self.kind = "vts"
            self.menu_vobs = u32(self.data, 0xC0)
            self.tt_srpt = 0
            self.pgci_ut = u32(self.data, 0xD0)
        else:
            raise ValueError(f"{path}: not a DVD IFO (magic {magic!r})")

    # --- global title table (VMG only) -------------------------------------
    def titles(self):
        if self.kind != "vmg" or not self.tt_srpt:
            return []
        base = self.tt_srpt * SECTOR
        n = u16(self.data, base)
        out = []
        for i in range(n):
            o = base + 8 + i * 12
            out.append({
                "title": i + 1,
                "angles": u8(self.data, o + 1),
                "chapters": u16(self.data, o + 2),
                "vts": u8(self.data, o + 6),
                "vts_title": u8(self.data, o + 7),
            })
        return out

    # --- menu program chains ----------------------------------------------
    def menu_pgcs(self):
        if not self.pgci_ut:
            return []
        base = self.pgci_ut * SECTOR
        nlu = u16(self.data, base)
        out = []
        for i in range(nlu):
            lo = base + 8 + i * 8
            lang = self.data[lo:lo + 2].decode("latin-1", "replace")
            lu = base + u32(self.data, lo + 4)
            npgc = u16(self.data, lu)
            for j in range(npgc):
                so = lu + 8 + j * 8
                entry_id = u8(self.data, so)
                pgc_off = lu + u32(self.data, so + 4)
                out.append({
                    "lang": lang,
                    "entry": bool(entry_id & 0x80),
                    "entry_id": entry_id,
                    "menu_id": entry_id & 0x0F,
                    "menu": MENU_NAMES.get(entry_id & 0x0F, f"menu{entry_id & 0x0F}"),
                    "pgcn": j + 1,
                    "programs": u8(self.data, pgc_off + 2),
                    "n_cells": u8(self.data, pgc_off + 3),
                    "still_time": u8(self.data, pgc_off + 0xA3),
                    "next_pgc": u16(self.data, pgc_off + 0x9C),
                    "prev_pgc": u16(self.data, pgc_off + 0x9E),
                    "goup_pgc": u16(self.data, pgc_off + 0xA0),
                    "program_map": self._program_map(pgc_off),
                    "commands": self._commands(pgc_off),
                    "cells": self._cells(pgc_off),
                })
        return out

    def _program_map(self, pgc):
        n = u8(self.data, pgc + 2)
        off = u16(self.data, pgc + 0xE6)
        if not off or not n:
            return []
        return [u8(self.data, pgc + off + i) for i in range(n)]

    def _commands(self, pgc):
        """pgc_command_tbl_t: nr_pre, nr_post, nr_cell, last_byte, then
        8-byte commands in that order (libdvdread ifo_types.h)."""
        off = u16(self.data, pgc + 0xE4)
        empty = {"pre": [], "post": [], "cell": []}
        if not off:
            return empty
        base = pgc + off
        n_pre = u16(self.data, base)
        n_post = u16(self.data, base + 2)
        n_cell = u16(self.data, base + 4)
        last = u16(self.data, base + 6)
        total = n_pre + n_post + n_cell
        # sanity: the table must fit inside last_byte, and 128 is the spec cap
        if total == 0 or total > 128 or 8 + total * 8 > last + 1:
            if total and PROBE:
                plog(f"    ! command table at 0x{base:x} looks wrong: "
                     f"pre={n_pre} post={n_post} cell={n_cell} last={last}")
            return empty
        out = {"pre": [], "post": [], "cell": []}
        k = 0
        for name, count in (("pre", n_pre), ("post", n_post), ("cell", n_cell)):
            for i in range(count):
                raw = self.data[base + 8 + k * 8: base + 16 + k * 8]
                k += 1
                text, info = decode_command(raw)
                out[name].append({"n": i + 1, "raw": raw.hex(),
                                  "text": text, "info": info})
        return out

    def _cells(self, pgc):
        ncells = u8(self.data, pgc + 3)
        cpb_off = u16(self.data, pgc + 232)
        if not cpb_off or not ncells:
            return []
        cells = []
        for i in range(ncells):
            o = pgc + cpb_off + i * 24
            cells.append({
                "cell": i + 1,
                "still_time": u8(self.data, o + 2),
                "cell_cmd_nr": u8(self.data, o + 3),
                "first_sector": u32(self.data, o + 8),
                "last_sector": u32(self.data, o + 20),
            })
        return cells


# --------------------------------------------------------------- PCI parsing

PCI_PES = 0x26          # PCI PES header, after pack header (14) + system header (24)
DSI_PES = 0x400         # DSI PES header


def is_nav_pack(b):
    """A NAV pack is a pack header + system header + a PCI private-stream-2
    PES + a DSI one. Checking only the pack start code matches every video
    pack on the disc, which is what produced the nonsense button data."""
    if b[0:4] != b"\x00\x00\x01\xba":
        return False
    if b[PCI_PES:PCI_PES + 4] != b"\x00\x00\x01\xbf":
        return False
    if u16(b, PCI_PES + 4) != 0x03D4 or b[PCI_PES + 6] != 0x00:
        return False
    if b[DSI_PES:DSI_PES + 4] != b"\x00\x00\x01\xbf":
        return False
    if u16(b, DSI_PES + 4) != 0x03FA or b[DSI_PES + 6] != 0x01:
        return False
    return True


def rect_ok(b, w=720, h=576):
    x0, y0, x1, y1 = b["rect"]
    return 0 <= x0 < x1 <= w and 0 <= y0 < y1 <= h


def rects_sane(buttons):
    """Keep the good buttons; only reject the set if most of it is junk."""
    good = [b for b in buttons if rect_ok(b)]
    return good if len(good) >= max(1, len(buttons) // 2) else []


def parse_buttons(sector_bytes):
    """Return the button list from a NAV pack sector, or None if no HLI."""
    b = sector_bytes
    if len(b) < SECTOR or not is_nav_pack(b):
        return None
    hli_ss = u16(b, HLI_OFFSET) & 0x03
    if hli_ss == 0:
        return None
    btn_ns = u8(b, HLI_OFFSET + 0x11) & 0x3F
    if not 1 <= btn_ns <= 36:
        return None

    buttons = []
    for i in range(btn_ns):
        o = BTNIT_OFFSET + i * 18
        w0 = int.from_bytes(b[o:o + 3], "big")
        w1 = int.from_bytes(b[o + 3:o + 6], "big")
        x_start = (w0 >> 12) & 0x3FF
        x_end = w0 & 0x3FF
        y_start = (w1 >> 12) & 0x3FF
        y_end = w1 & 0x3FF
        cmd = b[o + 10:o + 18]
        mnem, info = decode_command(cmd)
        if x_end <= x_start or y_end <= y_start:
            continue
        buttons.append({
            "button": i + 1,
            "rect": [x_start, y_start, x_end, y_end],
            "up": b[o + 6] & 0x3F, "down": b[o + 7] & 0x3F,
            "left": b[o + 8] & 0x3F, "right": b[o + 9] & 0x3F,
            "command": mnem,
            "target": info,
            "raw": cmd.hex(),
        })
    buttons = rects_sane(buttons)
    if not buttons:
        return None
    buttons.sort(key=lambda x: x["button"])
    return buttons


def scan_cell(vob_paths, first_sector, last_sector, max_sectors=8000):
    """Walk the VOB sectors of a menu cell, collecting distinct button sets."""
    sets, seen = [], set()
    navs = 0
    for sec in range(first_sector, min(last_sector + 1,
                                       first_sector + max_sectors)):
        raw = read_sector(vob_paths, sec)
        if raw is None:
            break
        if is_nav_pack(raw):
            navs += 1
            if PROBE and navs <= 12:
                hli = raw[HLI_OFFSET:HLI_OFFSET + 0x16]
                plog(f"      nav @{sec}: hli_ss={u16(raw, HLI_OFFSET):#06x} "
                           f"btn_ns@0x11={raw[HLI_OFFSET+0x11]:#04x} "
                           f"hl_gi={hli.hex()}")
        elif PROBE and sec - first_sector < 4:
            plog(f"      @{sec}: not a nav pack; "
                       f"head={raw[:4].hex()} pes26={raw[PCI_PES:PCI_PES+4].hex()} "
                       f"len={u16(raw, PCI_PES+4):#06x} "
                       f"pes400={raw[DSI_PES:DSI_PES+4].hex()}")
        btns = parse_buttons(raw)
        if not btns:
            continue
        key = tuple((x["button"], x["raw"], tuple(x["rect"])) for x in btns)
        if key not in seen:
            seen.add(key)
            sets.append({"at_sector": sec, "buttons": btns})
    return sets, navs


_FH_CACHE = {}
_BAD_SECTORS = 0


def _handle(path):
    """Keep one unbuffered handle per VOB - reopening per sector is what
    makes optical reads fail with EINVAL on Windows."""
    fh = _FH_CACHE.get(path)
    if fh is None:
        fh = open(path, "rb", buffering=0)
        _FH_CACHE[path] = fh
    return fh


def close_handles():
    for fh in _FH_CACHE.values():
        try:
            fh.close()
        except OSError:
            pass
    _FH_CACHE.clear()


def read_sector(vob_paths, sector):
    """VOB parts are concatenated; index into them by sector.
    A device read error yields None (skip) rather than killing the run."""
    global _BAD_SECTORS
    off = sector * SECTOR
    for p, size in vob_paths:
        if off < size:
            try:
                fh = _handle(p)
                fh.seek(off)
                d = fh.read(SECTOR)
            except OSError as e:
                _BAD_SECTORS += 1
                if _BAD_SECTORS <= 3:
                    print(f"! read error at sector {sector} of {p.name}: {e}",
                          file=sys.stderr)
                return None
            return d if len(d) == SECTOR else None
        off -= size
    return None


def aligned_copy(src, dest, size, chunk_sectors=64):
    """Copy a VOB with sector-aligned reads. Optical drives reject reads that
    aren't a multiple of 2048 or that overrun the last sector, which is what
    shutil.copyfile does on its final partial chunk. Unreadable sectors are
    zero-filled so later sector indexing stays correct."""
    nsec = (size + SECTOR - 1) // SECTOR
    bad = 0
    consec = 0
    good = 0
    last_pct = -10
    try:
        with open(src, "rb", buffering=0) as r, open(dest, "wb") as w:
            sec = 0
            while sec < nsec:
                want = min(chunk_sectors, nsec - sec)
                try:
                    r.seek(sec * SECTOR)
                    d = r.read(want * SECTOR)
                except OSError:
                    d = b""
                if len(d) != want * SECTOR:
                    # fall back to one sector at a time across this chunk
                    d = b""
                    for i in range(want):
                        try:
                            r.seek((sec + i) * SECTOR)
                            one = r.read(SECTOR)
                        except OSError:
                            one = b""
                        if len(one) != SECTOR:
                            one = bytes(SECTOR)
                            bad += 1
                            consec += 1
                        else:
                            consec = 0
                            good += 1
                        d += one
                        if consec >= 512 and good == 0:
                            break
                w.write(d)
                sec += want
                pct = 100 * sec // nsec
                if pct >= last_pct + 10:
                    last_pct = pct
                    plog(f"    {src.name}: {pct}% ({bad} unreadable so far)")
                if consec >= 512 and good == 0:
                    plog(f"! {src.name}: nothing readable in the first {sec} "
                         f"sectors - the drive is refusing this VOB entirely")
                    return False
    except OSError as e:
        plog(f"! could not stage {src.name}: {e}")
        return False
    if bad:
        plog(f"! {src.name}: {bad} of {nsec} sectors unreadable "
             f"({100*bad/nsec:.1f}%) - likely CSS-scrambled or a bad disc")
    return True


def stage_vobs(parts, tmpdir):
    """Copy the menu VOBs to local disk before parsing. Menu VOBs are small,
    and reading them off the drive directly is both slow and error-prone."""
    staged = []
    for p, size in parts:
        dest = tmpdir / p.name
        if not dest.exists() or dest.stat().st_size != size:
            plog(f"  staging {p.name} ({size/1048576:.1f} MB)...")
            if not aligned_copy(p, dest, size):
                staged.append((p, size))
                continue
        staged.append((dest, dest.stat().st_size))
    return staged


def vob_parts(video_ts, ifo_path):
    """Menu VOB(s) for an IFO: VIDEO_TS.VOB, or VTS_nn_0.VOB."""
    stem = ifo_path.stem
    if stem.upper() == "VIDEO_TS":
        cands = [video_ts / "VIDEO_TS.VOB"]
    else:
        cands = [video_ts / f"{stem}.VOB"]
    out = []
    for c in cands:
        real = find_ci(video_ts, c.name)
        if real and real.stat().st_size:
            out.append((real, real.stat().st_size))
    return out


def find_ci(folder, name):
    """Case-insensitive lookup - discs mount as VIDEO_TS or video_ts."""
    for p in folder.iterdir():
        if p.name.lower() == name.lower():
            return p
    return None


# ------------------------------------------------------------------- imaging

def have_ffmpeg():
    return shutil.which("ffmpeg") is not None


def grab_menu_image(vob_paths, first_sector, last_sector, out_png, tmpdir):
    """Cut the menu cell out of the VOB and pull a still frame."""
    tmp = tmpdir / "cell.vob"
    nsec = min(last_sector - first_sector + 1, 3000)
    with open(tmp, "wb") as w:
        for sec in range(first_sector, first_sector + nsec):
            d = read_sector(vob_paths, sec)
            if d is None:
                break
            w.write(d)
    for seek in ("0.7", "0"):
        cmd = ["ffmpeg", "-v", "error", "-y", "-ss", seek, "-i", str(tmp),
               "-frames:v", "1", "-q:v", "2", str(out_png)]
        subprocess.run(cmd, capture_output=True)
        if out_png.exists() and out_png.stat().st_size > 1000:
            tmp.unlink(missing_ok=True)
            return True
    tmp.unlink(missing_ok=True)
    return False


# ------------------------------------------------------------------- mounting

def _drive_letters():
    """Drives that exist right now. Diffing this before and after a mount
    finds the new letter without needing any privileges."""
    return {c for c in string.ascii_uppercase if os.path.exists(f"{c}:\\")}


def _ps(script):
    """Run a PowerShell snippet. Returns (returncode, stdout)."""
    try:
        p = subprocess.run(["powershell", "-NoProfile", "-NonInteractive",
                            "-Command", script],
                           capture_output=True, text=True, timeout=120)
        return p.returncode, (p.stdout or "").strip()
    except (OSError, subprocess.SubprocessError) as e:
        return 1, str(e)


def _with_video_ts(letters):
    return [c for c in sorted(letters) if Path(f"{c}:\\VIDEO_TS").is_dir()]


def mount_iso(iso, wait=30.0):
    """Return the VIDEO_TS folder of `iso`, mounting the image if needed.

    Mount-DiskImage wants elevation; Explorer's own mount verb does not, so
    that is the fallback. The drive letter comes from diffing the set of
    drives before and after - Get-DiskImage would also tell us, but it needs
    elevation too, and the diff needs nothing.

    Returns (video_ts_path, letter, mounted_by_us).
    """
    iso = Path(iso).expanduser()
    if not iso.is_file():
        raise SystemExit(f"--iso: no such file: {iso}")
    if os.name != "nt":
        raise SystemExit("--iso mounts through Windows; on Linux/WSL mount it "
                         "yourself (mount -o loop) and pass the VIDEO_TS path")
    full = str(iso.resolve()).replace("'", "''")

    before = _drive_letters()
    rc, out = _ps(f"(Get-DiskImage -ImagePath '{full}' "
                  f"-ErrorAction SilentlyContinue | Get-Volume).DriveLetter")
    letter = out.splitlines()[0].strip() if (rc == 0 and out) else ""
    if len(letter) == 1 and Path(f"{letter}:\\VIDEO_TS").is_dir():
        print(f"  {iso.name} already mounted as {letter}:")
        return Path(f"{letter}:\\VIDEO_TS"), letter, False

    already = set(_with_video_ts(before))
    rc, err = _ps(f"Mount-DiskImage -ImagePath '{full}' -ErrorAction Stop "
                  f"| Out-Null")
    if rc != 0:
        # almost always "Access is denied" - no elevation. Explorer's verb
        # does the same job as a normal user.
        rc, err = _ps("$s = New-Object -ComObject Shell.Application; "
                      f"$s.Namespace(0).ParseName('{full}').InvokeVerb('Mount')")

    deadline = time.time() + wait
    while time.time() < deadline:
        fresh = _with_video_ts(_drive_letters() - before)
        if fresh:
            c = fresh[0]
            print(f"  mounted {iso.name} as {c}:")
            return Path(f"{c}:\\VIDEO_TS"), c, True
        time.sleep(0.5)

    # nothing new appeared: it may have been mounted before we looked
    if len(already) == 1:
        c = sorted(already)[0]
        print(f"  no new drive appeared; using {c}: which already holds a "
              f"VIDEO_TS (verify this is the right disc)")
        return Path(f"{c}:\\VIDEO_TS"), c, False
    raise SystemExit(
        f"could not mount {iso} ({err or 'no new drive appeared'}). "
        f"Mount it by hand (double-click the .iso) and pass its VIDEO_TS path."
        + (f" Candidates with a VIDEO_TS: {', '.join(sorted(already))}"
           if already else ""))


def dismount_iso(iso):
    full = str(Path(iso).resolve()).replace("'", "''")
    rc, err = _ps(f"Dismount-DiskImage -ImagePath '{full}' "
                  f"-ErrorAction Stop | Out-Null")
    if rc != 0:
        rc, err = _ps("$s = New-Object -ComObject Shell.Application; "
                      f"$s.Namespace(0).ParseName('{full}').InvokeVerb('Eject')")
    print("  dismounted" if rc == 0 else f"! could not dismount: {err}")


# ------------------------------------------------------------- disc layout

DEFAULT_RIPS_ROOT = r"F:\rips"


def resolve_disc(name, work=None, rips_root=None):
    """Fill in the per-disc paths from the folder layout, so one --disc
    stands in for --iso / --mkv-info / -o / --rips.

    Anchored on the script's own folder, not the shell's working directory,
    so the command works from anywhere. Rips are not under `work` - they are
    the output you keep, and they live off C: for space.
    """
    root = Path(work) if work else Path(__file__).resolve().parent / "work"
    folder = root / name
    if not folder.is_dir():
        have = (sorted(p.name for p in root.iterdir() if p.is_dir())
                if root.is_dir() else [])
        raise SystemExit(f"--disc: no folder {folder}\n" + (
            f"  discs under {root}: {', '.join(have)}" if have else
            f"  {root} holds no disc folders yet"))

    iso = folder / "disc.iso"
    if not iso.is_file():
        found = sorted(folder.glob("*.iso"))
        if len(found) == 1:
            iso = found[0]
        elif not found:
            iso = None
        else:
            raise SystemExit(f"--disc: {folder} holds several .iso files "
                             f"({', '.join(f.name for f in found)}) - name one "
                             f"disc.iso or pass --iso")

    info = folder / "titles.txt"
    rips = Path(rips_root or DEFAULT_RIPS_ROOT) / name
    return {"folder": folder, "iso": iso,
            "info": info if info.is_file() else None,
            "outdir": folder / "menumap_out",
            "rips": rips if rips.is_dir() else None,
            "rips_wanted": rips}


def parse_makemkv_info(path):
    """Parse `makemkvcon -r info` output.

    The join key is TINFO field 24 (source title id), NOT the row index and
    NOT the chapter count: MakeMKV strips the trailing menu-jump cell, so its
    chapter counts run one short of TT_SRPT on every title, and `minlength`
    can drop rows entirely and shift the index. Field 24 survives both.
    """
    fields = {}
    disc = None
    text = Path(path).read_text(encoding="utf-8", errors="replace")
    for line in text.splitlines():
        m = re.match(r'TINFO:(\d+),(\d+),\d+,"(.*)"$', line)
        if m:
            fields.setdefault(int(m.group(1)), {})[int(m.group(2))] = m.group(3)
            continue
        m = re.match(r'CINFO:2,\d+,"(.*)"$', line)
        if m:
            disc = m.group(1)
    titles = {}
    for row in fields.values():
        src = row.get(24, "").strip()
        if not src.isdigit():
            continue
        titles[int(src)] = {
            "file": row.get(27, ""),
            "duration": row.get(9, ""),
            "seconds": _hms(row.get(9, "")),
            "chapters": int(row.get(8, "0") or 0),
            "size": row.get(10, ""),
            "bytes": _bytes(row.get(11, "")),
            "segments": row.get(26, ""),
        }
    return {"disc": disc, "titles": titles}


def _bytes(s):
    """TINFO field 11, the exact byte count. Field 10 is the human-readable
    size ('4.4 GB') and is no use as a key."""
    s = (s or "").strip().replace(",", "").replace(" ", "")
    return int(s) if s.isdigit() else 0


UNMATCHED = "_unmatched_"

RIP_EXTS = {".mkv", ".mp4", ".m2ts", ".ts", ".mpg", ".avi"}


def scan_rip_folder(folder):
    """{filename: size on disk} for the rip files in `folder`."""
    p = Path(folder)
    if not p.is_dir():
        raise SystemExit(f"--rips: not a folder: {folder}")
    return {f.name: f.stat().st_size for f in sorted(p.iterdir())
            if f.is_file() and f.suffix.lower() in RIP_EXTS}


SIZE_BAND = (0.95, 1.00)


def match_rips(want, have):
    """Bind titles to real files by name, with size as the guard.

    `want` {title: (filename from TINFO field 27, TINFO field 11)},
    `have` {filename: size on disk}.

    The name is the key - it carries MakeMKV's title index, which is what
    field 24 translates into a TT_SRPT number. Size cannot be the key:
    field 11 is the source title on the disc, and the remux comes out at
    0.978-0.981 of it (measured across 9.5 MB to 7.8 GB), never equal.

    What size is good for is catching the failure this whole thing exists
    to prevent. If the rip ran at a different --minlength than the info run,
    the names still line up but they line up with the WRONG titles, and the
    sizes fall out of band immediately. Out of band means refuse.

    Returns (bound, reasons, orphans).
    """
    lo, hi = SIZE_BAND
    bound, reasons = {}, {}
    for n in sorted(want):
        name, src = want[n]
        name = Path(name or "").name
        if not name:
            reasons[n] = "no filename in the info output (TINFO field 27)"
            continue
        if name not in have:
            reasons[n] = f"no file named {name} in the rip folder"
            continue
        if not src:
            reasons[n] = "no byte count in the info output (TINFO field 11)"
            continue
        ratio = have[name] / src
        if lo <= ratio <= hi:
            bound[n] = name
        else:
            reasons[n] = (f"{name} is {ratio:.3f} of the disc title's "
                          f"{src:,} bytes, outside {lo:.2f}-{hi:.2f} - the "
                          f"rip and the info run disagree, so this name "
                          f"points at the wrong title")
    return bound, reasons, sorted(set(have) - set(bound.values()))


def _hms(s):
    try:
        parts = [int(x) for x in s.split(":")]
    except ValueError:
        return 0
    out = 0
    for p in parts:
        out = out * 60 + p
    return out


def join_titles(result, mkv, rip_files=None):
    """Bind each DVD title to its ripped file, and flag composites.

    Refuses a binding whose chapter count doesn't sit at TT_SRPT-1 or
    TT_SRPT - renaming the wrong file is far worse than not renaming it.

    With `rip_files` (from --rips) the filename in the info output is only a
    hint: the real key is TINFO field 11, the exact byte count, matched
    against files actually on disk. The info run's `_t00` suffixes come from
    row index at rip time, so they shift if the rip used a different
    --minlength. Anything not matched exactly keeps its hint, prefixed
    UNMATCHED, and is never treated as bound.
    """
    rows = {}
    for t in result["titles"]:
        n = t["title"]
        info = mkv["titles"].get(n)
        if not info:
            rows[n] = {"file": None, "note": "no ripped file for this title"}
            continue
        ok = info["chapters"] in (t["chapters"], t["chapters"] - 1)
        rows[n] = dict(info)
        rows[n]["verified"] = ok
        if not ok:
            rows[n]["note"] = (f"chapter mismatch: disc says {t['chapters']}, "
                               f"rip says {info['chapters']} - not safe to rename")
    # composite ("play all") detection: several segment ranges, and a runtime
    # that equals the sum of the titles it is built from
    for n, r in rows.items():
        if not r.get("file") or "," not in (r.get("segments") or ""):
            continue
        others = [(m, o) for m, o in rows.items()
                  if m != n and o.get("seconds")]
        for size in (2, 3, 4):
            for combo in itertools.combinations(others, size):
                if abs(sum(o["seconds"] for _, o in combo) - r["seconds"]) <= 2:
                    r["composite_of"] = sorted(m for m, _ in combo)
                    r["note"] = ("play-all: same runtime as titles " +
                                 "+".join(str(m) for m, _ in combo) +
                                 " - likely skip")
                    break
            if r.get("composite_of"):
                break

    if rip_files is None:
        return rows

    want = {n: (r.get("file") or "", r.get("bytes") or 0)
            for n, r in rows.items() if "bytes" in r}
    bound, reasons, orphans = match_rips(want, rip_files)
    for n, r in rows.items():
        if "bytes" not in r:
            continue
        r["file_hint"] = r.get("file") or ""
        if n in bound and r.get("verified", True):
            r["file"] = bound[n]
            r["bound"] = True
            continue
        r["bound"] = False
        if n in bound:
            # byte-exact, but the chapter gate already refused it; the note
            # for that is on the row, so just keep the name it matched
            name = bound[n]
        else:
            # the info run's filename is only a hint - and if a file of that
            # name really is on disk it is some other title's, so don't echo it
            name = Path(r["file_hint"] or "").name or f"title_{n:02d}.mkv"
            if name in rip_files:
                name = f"title_{n:02d}.mkv"
            r["note"] = "; ".join(x for x in (r.get("note"), reasons.get(n))
                                  if x)
        r["file"] = UNMATCHED + name

    claimed = {r["file"] for r in rows.values() if r.get("bound")}
    result["rip_orphans"] = sorted(set(rip_files) - claimed)
    return rows


def naming_sheet(menu_png, buttons, out_png, header, rows_for_button):
    """Menu frame with numbered hotspots, plus a legend band underneath.

    The legend sits below the frame rather than floating over it: callouts
    drawn on the image cover the very captions you need to read (button 1's
    box lands right on "Live Forever" on THE_CENTENNIAL's episode index).
    """
    try:
        from PIL import Image, ImageDraw, ImageFont
    except ImportError:
        print("! Pillow not installed - run: pip install pillow",
              file=sys.stderr)
        return None

    def font(name, size):
        for cand in (fr"C:\Windows\Fonts\{name}",
                     f"/usr/share/fonts/truetype/dejavu/{name}"):
            try:
                return ImageFont.truetype(cand, size)
            except OSError:
                continue
        return ImageFont.load_default()

    scale = 2
    base = Image.open(menu_png).convert("RGB")
    base = base.resize((base.width * scale, base.height * scale),
                       Image.LANCZOS)
    f_bold = font("DejaVuSans-Bold.ttf", 21)
    f_small = font("DejaVuSans-Bold.ttf", 19)
    f_mono = font("DejaVuSansMono.ttf", 20)

    rowh, pad = 34, 14
    band = pad * 2 + rowh * len(buttons) + 34
    img = Image.new("RGB", (base.width, base.height + band), (18, 18, 18))
    img.paste(base, (0, 0))
    dr = ImageDraw.Draw(img)

    for b in buttons:
        x0, y0, x1, y1 = [v * scale for v in b["rect"]]
        dr.rectangle([x0, y0, x1, y1], outline=(0, 255, 255), width=3)
        lab = str(b["button"])
        try:
            w = dr.textlength(lab, font=f_bold)
        except AttributeError:
            w = 12 * len(lab)
        dr.rectangle([x0 + 3, y0 + 3, x0 + w + 21, y0 + 35], fill=(0, 255, 255))
        dr.text((x0 + 11, y0 + 6), lab, fill=(0, 0, 0), font=f_bold)

    y = base.height + pad
    dr.text((16, y), header, fill=(150, 150, 150), font=f_small)
    y += 30
    for b in buttons:
        dr.rectangle([16, y + 4, 44, y + 30], fill=(0, 255, 255))
        dr.text((24, y + 6), str(b["button"]), fill=(0, 0, 0), font=f_bold)
        cells = rows_for_button(b)
        dr.text((58, y + 6), cells["title"], fill=(255, 255, 255), font=f_bold)
        if cells.get("duration"):
            dr.text((190, y + 6), cells["duration"], fill=(190, 190, 190),
                    font=f_mono)
        if cells.get("file"):
            dr.text((330, y + 6), cells["file"], fill=(0, 255, 255), font=f_mono)
        if cells.get("note"):
            dr.text((520, y + 6), cells["note"], fill=(255, 190, 90),
                    font=f_small)
        y += rowh
    img.save(out_png)
    return out_png.name


def annotate_menu(menu_png, buttons, out_png):
    """Draw each button's hotspot and number onto the menu frame.

    The hotspot is where the remote's cursor lands, not where the caption
    is - on THE_CENTENNIAL's episode index the label runs ~20px below the
    rectangle - so cropping to it truncates labels. Boxing the full frame
    keeps every caption intact and readable in context.
    """
    try:
        from PIL import Image, ImageDraw, ImageFont
    except ImportError:
        print("! Pillow not installed - run: pip install pillow "
              "(falling back to the plain frame)", file=sys.stderr)
        return None

    img = Image.open(menu_png).convert("RGB")
    dr = ImageDraw.Draw(img)
    font = None
    for cand in (r"C:\Windows\Fonts\arialbd.ttf",
                 r"C:\Windows\Fonts\arial.ttf",
                 "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf"):
        try:
            font = ImageFont.truetype(cand, 18)
            break
        except OSError:
            continue
    if font is None:
        font = ImageFont.load_default()

    for b in buttons:
        x0, y0, x1, y1 = b["rect"]
        dr.rectangle([x0, y0, x1, y1], outline=(0, 255, 255), width=3)
        lab = str(b["button"])
        try:
            tw = dr.textlength(lab, font=font)
        except AttributeError:
            tw = 10 * len(lab)
        bx, by = x0 + 2, y0 + 2
        dr.rectangle([bx, by, bx + tw + 10, by + 26], fill=(0, 255, 255))
        dr.text((bx + 5, by + 3), lab, fill=(0, 0, 0), font=font)
    img.save(out_png)
    return out_png.name


def crop_buttons(menu_png, buttons, outdir, prefix):
    """One small PNG per button, so the label sits next to its title number."""
    made = []
    for b in buttons:
        x0, y0, x1, y1 = b["rect"]
        w, h = x1 - x0, y1 - y0
        if w < 8 or h < 6:
            continue
        out = outdir / f"{prefix}_btn{b['button']:02d}.png"
        subprocess.run(
            ["ffmpeg", "-v", "error", "-y", "-i", str(menu_png),
             "-vf", f"crop={w}:{h}:{x0}:{y0}", "-frames:v", "1", str(out)],
            capture_output=True)
        if out.exists():
            b["crop"] = out.name
            made.append(out)
    return made


# ------------------------------------------------------------- PGC resolver

SPRM_DEFAULTS = {
    0: 0x656E,   # menu language 'en'
    1: 15,       # audio stream: none selected
    2: 62,       # sub-picture stream: none selected
    3: 1, 4: 1, 5: 1, 6: 1, 7: 1,
    8: 0, 9: 0, 10: 0, 11: 0, 12: 0,
    13: 15,      # parental level: unrestricted
    20: 1,       # region
}
MAX_STEPS = 400
MAX_DEPTH = 40


class Resolver:
    """Walk PGC command chains from a menu button to the title it plays.

    Executes the real pre / post / cell command blocks with a 16-register
    machine, because the disc decides the target by comparing GPRMs the
    button set (see THE_CENTENNIAL: g[14] is the title selector, and
    VTS_01 PGC 8 is the dispatcher every path funnels through).
    """

    def __init__(self, domains, titles):
        self.domains = domains      # key -> {"lang": {pgcn: pgc}, "vts": n}
        self.titles = titles

    # --- helpers ----------------------------------------------------------
    def _pgc(self, dom, lang, pgcn):
        d = self.domains.get(dom)
        if not d:
            return None
        byl = d["pgcs"]
        return (byl.get(lang) or byl.get(next(iter(byl), None)) or {}).get(pgcn)

    def _entry_pgc(self, dom, lang, menu_id):
        d = self.domains.get(dom)
        if not d:
            return None
        byl = d["pgcs"]
        table = byl.get(lang) or byl.get(next(iter(byl), None)) or {}
        for pgcn in sorted(table):
            p = table[pgcn]
            if p["entry"] and p["menu_id"] == menu_id:
                return pgcn
        return None

    def _global_title(self, vts, vts_title):
        for t in self.titles:
            if t["vts"] == vts and t["vts_title"] == vts_title:
                return t
        return None

    # --- machine ----------------------------------------------------------
    def _read(self, st, ref):
        if ref["kind"] == "imm":
            return ref["v"]
        if ref["kind"] == "gprm":
            return st["g"][ref["n"] & 0x0F]
        n = ref["n"]
        if n not in st["sprm_seen"]:
            st["sprm_seen"].append(n)
        return st["sprm"].get(n, 0)

    @staticmethod
    def _cmp(op_n, a, b):
        return {1: lambda: bool(a & b), 2: lambda: a == b, 3: lambda: a != b,
                4: lambda: a >= b, 5: lambda: a > b, 6: lambda: a <= b,
                7: lambda: a < b}.get(op_n, lambda: False)()

    def _apply_set(self, st, s):
        dst, val, op = s["dst"], self._read(st, s["src"]), s["op_n"]
        if dst["kind"] != "gprm":
            st["sprm"][dst["n"]] = val
            return
        i = dst["n"] & 0x0F
        cur = st["g"][i]
        if op == 1:
            st["g"][i] = val
        elif op == 2:
            if s["src"]["kind"] == "gprm":
                j = s["src"]["n"] & 0x0F
                st["g"][i], st["g"][j] = st["g"][j], cur
        elif op == 3:
            st["g"][i] = (cur + val) & 0xFFFF
        elif op == 4:
            st["g"][i] = max(0, cur - val)
        elif op == 5:
            st["g"][i] = (cur * val) & 0xFFFF
        elif op == 6:
            st["g"][i] = cur // val if val else 0xFFFF
        elif op == 7:
            st["g"][i] = cur % val if val else 0xFFFF
        elif op == 9:
            st["g"][i] = cur & val
        elif op == 10:
            st["g"][i] = cur | val
        elif op == 11:
            st["g"][i] = cur ^ val
        # op 8 (rnd) left alone - non-deterministic, flagged by the caller

    def _run_block(self, st, block, start=1):
        """Execute a command list. Returns (info, line) of the command that
        transferred control, or (None, None) if the block ran off the end."""
        i = max(0, start - 1)
        steps = 0
        while i < len(block):
            steps += 1
            if steps > MAX_STEPS:
                return {"op": "steplimit"}, i + 1
            info = block[i]["info"]
            cond = info.get("if")
            if cond and not self._cmp(cond["op_n"],
                                      self._read(st, cond["lhs"]),
                                      self._read(st, cond["rhs"])):
                i += 1
                continue
            if info.get("set"):
                if info["set"]["op_n"] == 8:
                    st["notes"].append("rnd used - result is not deterministic")
                self._apply_set(st, info["set"])
            for s in info.get("system_set", []):
                if "sprm" in s:
                    st["sprm"][s["sprm"]] = self._read(st, s["src"])
                elif "gprm" in s:
                    st["g"][s["gprm"] & 0x0F] = self._read(st, s["src"])
            op = info.get("op")
            if op == "nop":
                i += 1
            elif op == "Goto":
                i = max(0, info["line"] - 1)
            elif op == "SetTmpPML":
                st["notes"].append(f"SetTmpPML {info['level']} ignored")
                i = max(0, info["line"] - 1)
            elif op == "Break":
                return None, i + 1
            elif op == "other":
                # set / system-set with no link part: fall through to the
                # next line rather than treating it as a control transfer
                i += 1
            else:
                return info, i + 1
            if op in ("Goto", "SetTmpPML") and steps > MAX_STEPS:
                return {"op": "steplimit"}, i + 1
        return None, None

    # --- chain walk -------------------------------------------------------
    def resolve(self, dom, lang, pgcn, button_info):
        st = {"g": [0] * 16, "sprm": dict(SPRM_DEFAULTS),
              "sprm_seen": [], "notes": []}
        trace = []
        res = self._follow(st, dom, lang, pgcn, [{"info": button_info}],
                           "button", 1, trace, set(), 0)
        res["trace"] = trace
        res["notes"] = st["notes"]
        if st["sprm_seen"]:
            res["notes"].append(
                "read system registers " +
                ", ".join(f"s[{n}]" for n in st["sprm_seen"]) +
                " - assumed player defaults")
        return res

    def _follow(self, st, dom, lang, pgcn, block, kind, start,
                trace, seen, depth):
        if depth > MAX_DEPTH:
            return {"kind": "unresolved", "why": "depth limit"}
        key = (str(dom), pgcn, kind, start)
        if key in seen:
            return {"kind": "loop", "why": f"revisited {self._where(dom, pgcn)} {kind}"}
        seen = seen | {key}

        info, line = self._run_block(st, block, start)
        if info is None:
            trace.append(f"{self._where(dom, pgcn)}.{kind} falls through")
            return {"kind": "menu", "menu_pgc": pgcn, "domain": self._dom_name(dom),
                    "why": "block ended without a link"}
        trace.append(f"{self._where(dom, pgcn)}.{kind}:{line} "
                     f"{info.get('text') or info.get('op')}")
        return self._act(st, dom, lang, pgcn, info, trace, seen, depth)

    def _enter(self, st, dom, lang, pgcn, trace, seen, depth):
        """Enter a PGC: run its pre-commands."""
        p = self._pgc(dom, lang, pgcn)
        if p is None:
            return {"kind": "unresolved",
                    "why": f"no pgc {pgcn} in {self._dom_name(dom)}"}
        pre = (p["commands"] or {}).get("pre") or []
        if not pre:
            return self._settle(dom, pgcn, p)
        r = self._follow(st, dom, lang, pgcn, pre, "pre", 1,
                         trace, seen, depth + 1)
        return r

    def _settle(self, dom, pgcn, p):
        """A PGC with no further command: it is a menu screen (or dead end)."""
        if p["cells"]:
            return {"kind": "menu", "menu_pgc": pgcn,
                    "domain": self._dom_name(dom)}
        return {"kind": "unresolved", "why": f"pgc {pgcn} has no cells and no commands"}

    def _act(self, st, dom, lang, pgcn, info, trace, seen, depth):
        op = info.get("op")
        vts = self.domains.get(dom, {}).get("vts")

        if op == "JumpTT":
            return self._title_result(info["title"], None)
        if op in ("JumpVTS_TT", "JumpVTS_PTT"):
            t = self._global_title(vts, info["vts_title"])
            if not t:
                return {"kind": "unresolved",
                        "why": f"{op} {info['vts_title']} has no entry in TT_SRPT"}
            return self._title_result(t["title"], info.get("chapter"))
        if op == "LinkPGCN":
            return self._enter(st, dom, lang, info["pgcn"], trace, seen, depth)
        if op in ("LinkPTT", "LinkPGN", "LinkCN"):
            p = self._pgc(dom, lang, pgcn)
            return {"kind": "menu", "menu_pgc": pgcn,
                    "domain": self._dom_name(dom),
                    "button": info.get("button") or None} if p else \
                   {"kind": "unresolved", "why": "link inside a missing pgc"}
        if op == "linksub":
            return self._linksub(st, dom, lang, pgcn, info, trace, seen, depth)
        if op in ("JumpSS", "CallSS"):
            return self._jumpss(st, dom, lang, info, trace, seen, depth)
        if op == "Exit":
            return {"kind": "exit"}
        if op == "steplimit":
            return {"kind": "unresolved", "why": "command step limit"}
        return {"kind": "unresolved", "why": f"unhandled {op}"}

    def _linksub(self, st, dom, lang, pgcn, info, trace, seen, depth):
        sub = info.get("sub")
        p = self._pgc(dom, lang, pgcn)
        if sub == "LinkTailPGC":
            post = ((p or {}).get("commands") or {}).get("post") or []
            if not post:
                return {"kind": "menu", "menu_pgc": pgcn,
                        "domain": self._dom_name(dom),
                        "why": "LinkTailPGC with no post-commands"}
            return self._follow(st, dom, lang, pgcn, post, "post", 1,
                                trace, seen, depth + 1)
        if sub == "LinkGoUpPGC" and p and p.get("goup_pgc"):
            return self._enter(st, dom, lang, p["goup_pgc"], trace, seen, depth)
        if sub == "LinkNextPGC" and p and p.get("next_pgc"):
            return self._enter(st, dom, lang, p["next_pgc"], trace, seen, depth)
        if sub == "LinkPrevPGC" and p and p.get("prev_pgc"):
            return self._enter(st, dom, lang, p["prev_pgc"], trace, seen, depth)
        if sub == "RSM":
            return {"kind": "resume",
                    "why": "RSM - target depends on saved playback state"}
        if sub in ("LinkTopPGC", "LinkTopC", "LinkTopPG", "LinkNextC",
                   "LinkPrevC", "LinkNextPG", "LinkPrevPG", "LinkNoLink"):
            return {"kind": "menu", "menu_pgc": pgcn,
                    "domain": self._dom_name(dom), "why": sub}
        return {"kind": "unresolved", "why": f"unhandled linksub {sub}"}

    def _jumpss(self, st, dom, lang, info, trace, seen, depth):
        tgt = info.get("target")
        if tgt == "VMGM" and info.get("pgcn"):
            return self._enter(st, "vmg", lang, info["pgcn"],
                               trace, seen, depth)
        if tgt == "VMGM":
            pgcn = self._entry_pgc("vmg", lang, info.get("menu"))
            if pgcn is None:
                return {"kind": "unresolved",
                        "why": f"no VMGM entry pgc for menu {info.get('menu')}"}
            return self._enter(st, "vmg", lang, pgcn, trace, seen, depth)
        if tgt == "VTSM":
            vts = info.get("vts") or self.domains.get(dom, {}).get("vts")
            ndom = ("vts", vts)
            if info.get("vts_title"):
                st["sprm"][5] = info["vts_title"]
            pgcn = self._entry_pgc(ndom, lang, info.get("menu"))
            if pgcn is None:
                return {"kind": "unresolved",
                        "why": f"no VTS {vts} entry pgc for menu {info.get('menu')}"}
            return self._enter(st, ndom, lang, pgcn, trace, seen, depth)
        if tgt == "FP":
            return {"kind": "unresolved", "why": "jumps to the first-play pgc"}
        return {"kind": "unresolved", "why": f"unhandled {info.get('op')}"}

    def _title_result(self, title, chapter):
        t = next((x for x in self.titles if x["title"] == title), None)
        r = {"kind": "title", "title": title, "chapter": chapter}
        if t:
            r.update({"vts": t["vts"], "vts_title": t["vts_title"],
                      "chapters": t["chapters"]})
        return r

    def _dom_name(self, dom):
        return "VMGM" if dom == "vmg" else f"VTS {dom[1]}"

    def _where(self, dom, pgcn):
        return f"{self._dom_name(dom)} pgc{pgcn}"


def describe_result(r):
    k = r.get("kind")
    if k == "title":
        bits = f"Title {r['title']}"
        if r.get("vts"):
            bits += f" (VTS {r['vts']} #{r['vts_title']}, {r['chapters']} ch)"
        if r.get("chapter"):
            bits += f", chapter {r['chapter']}"
        return bits
    if k == "menu":
        return f"menu ({r.get('domain')} pgc {r.get('menu_pgc')})"
    if k == "resume":
        return "resume playback"
    if k == "exit":
        return "stop / exit"
    if k == "loop":
        return f"loop - {r.get('why')}"
    return f"unresolved - {r.get('why')}"


# ---------------------------------------------------------------------- main

def legend_row(button, joined):
    r = (button.get("resolved") or {})
    if r.get("kind") != "title":
        return {"title": f"-> {describe_result(r)}"}
    n = r["title"]
    rip = joined.get(n) or {}
    return {"title": f"Title {n}",
            "duration": rip.get("duration") or f"{r.get('chapters','?')} ch",
            "file": rip.get("file") or "",
            "note": rip.get("note") or ""}


def keep_screen(screen, all_images):
    """Render a screen unless every button on it provably leads somewhere
    that is not a title. Anything unresolved / resume / loop keeps the
    screen - the failure mode of an odd disc must be extra images, never a
    missing one."""
    if all_images:
        return True
    if not screen["button_sets"]:
        return False
    for bs in screen["button_sets"]:
        for b in bs["buttons"]:
            kind = (b.get("resolved") or {}).get("kind")
            if kind not in ("menu", "exit"):
                return True
    return False


def title_coverage(result, domains):
    """Which titles a menu button can name, and where the rest are reached
    from - so a title never goes missing without saying so."""
    named = {}
    for m in result["menus"]:
        for s in m["screens"]:
            for bs in s["button_sets"]:
                for b in bs["buttons"]:
                    r = b.get("resolved") or {}
                    if r.get("kind") == "title":
                        named.setdefault(r["title"], []).append(
                            {"ifo": m["ifo"], "pgc": m["pgc"],
                             "button": b["button"],
                             "image": bs.get("image") or s.get("image")})
    # where else does a title get jumped to, if no button reaches it?
    jumps = {}
    for dom, d in domains.items():
        vts = d.get("vts")
        for lang, table in d["pgcs"].items():
            for pgcn, p in table.items():
                for which, block in (p["commands"] or {}).items():
                    for c in block:
                        info = c["info"]
                        t = None
                        if info.get("op") == "JumpTT":
                            t = info["title"]
                        elif info.get("op") in ("JumpVTS_TT", "JumpVTS_PTT"):
                            t = next((x["title"] for x in result["titles"]
                                      if x["vts"] == vts
                                      and x["vts_title"] == info["vts_title"]),
                                     None)
                        if t:
                            where = ("VMGM" if dom == "vmg"
                                     else f"VTS {dom[1]}")
                            jumps.setdefault(t, set()).add(
                                f"{where} pgc{pgcn} {which}")
    out = []
    for t in result["titles"]:
        n = t["title"]
        out.append({"title": n, "chapters": t["chapters"],
                    "vts": t["vts"], "vts_title": t["vts_title"],
                    "named_by": named.get(n, []),
                    "reached_from": sorted(jumps.get(n, []))})
    return out


def analyse(video_ts, outdir, do_images=True, stage=True,
            all_images=False, crops=False, mkv_info=None, rips=None):
    video_ts = Path(video_ts)
    if video_ts.name.lower() != "video_ts" and (video_ts / "VIDEO_TS").exists():
        video_ts = video_ts / "VIDEO_TS"
    if not video_ts.is_dir():
        raise SystemExit(f"no such folder: {video_ts}\n"
                         f"  if that is a drive letter for an ISO, the image "
                         f"is not mounted - pass --iso <file> instead and it "
                         f"will be mounted for you")
    outdir = Path(outdir)
    outdir.mkdir(parents=True, exist_ok=True)
    tmpdir = outdir / ".tmp"
    tmpdir.mkdir(exist_ok=True)

    ifos = sorted([p for p in video_ts.iterdir()
                   if re.fullmatch(r"(VIDEO_TS|VTS_\d\d_0)\.IFO", p.name, re.I)],
                  key=lambda p: (p.name.upper() != "VIDEO_TS.IFO", p.name.upper()))
    if not ifos:
        raise SystemExit(f"no IFO files under {video_ts}")

    images_ok = do_images and have_ffmpeg()
    if do_images and not images_ok:
        print("! ffmpeg not on PATH - skipping screenshots", file=sys.stderr)

    if PROBE:
        globals()["_PROBE_FH"] = open(outdir / "probe.log", "w")
        plog(f"probe of {video_ts}")

    result = {"source": str(video_ts), "titles": [], "menus": []}
    mkv = parse_makemkv_info(mkv_info) if mkv_info else None
    rip_files = scan_rip_folder(rips) if rips else None
    if rip_files is not None and not rip_files:
        print(f"! --rips: no rip files found in {rips}", file=sys.stderr)
    domains = {}
    pending = []

    for ifo_path in ifos:
        try:
            ifo = Ifo(ifo_path)
        except ValueError as e:
            print(f"! {e}", file=sys.stderr)
            continue
        if ifo.kind == "vmg":
            result["titles"] = ifo.titles()

        m = re.fullmatch(r"VTS_(\d\d)_0", ifo_path.stem, re.I)
        dom_key = ("vts", int(m.group(1))) if m else "vmg"
        pgcs = ifo.menu_pgcs()
        table = domains.setdefault(dom_key, {"pgcs": {},
                                             "vts": dom_key[1] if m else None})
        for p in pgcs:
            table["pgcs"].setdefault(p["lang"], {})[p["pgcn"]] = p

        parts = vob_parts(video_ts, ifo_path)
        if PROBE:
            plog(f"  {ifo_path.name}: menu_vobs sector={ifo.menu_vobs} "
                       f"pgci_ut={ifo.pgci_ut} parts="
                       f"{[(str(p), sz) for p, sz in parts] or 'NONE'}")
        if parts and stage:
            parts = stage_vobs(parts, tmpdir)
        for pgc in pgcs:
            tag = f"{ifo_path.stem}_{pgc['menu']}_pgc{pgc['pgcn']}"
            entry = {
                "ifo": ifo_path.name,
                "domain": ifo.kind,
                "lang": pgc["lang"],
                "menu": pgc["menu"],
                "pgc": pgc["pgcn"],
                "is_entry_pgc": pgc["entry"],
                "entry_id": pgc["entry_id"],
                "programs": pgc["programs"],
                "n_cells": pgc["n_cells"],
                "still_time": pgc["still_time"],
                "next_pgc": pgc["next_pgc"],
                "prev_pgc": pgc["prev_pgc"],
                "goup_pgc": pgc["goup_pgc"],
                "program_map": pgc["program_map"],
                "commands": pgc["commands"],
                "cells": pgc["cells"],
                "screens": [],
            }
            pending.append((dom_key, pgc["lang"], pgc["pgcn"], entry))
            if not parts or not pgc["cells"]:
                result["menus"].append(entry)
                continue

            total = sum(sz for _, sz in parts) // SECTOR
            for cell in pgc["cells"]:
                first, last = cell["first_sector"], cell["last_sector"]
                if first >= total:
                    print(f"! {tag} cell {cell['cell']}: sector {first} past "
                          f"end of menu VOB ({total} sectors) - skipping",
                          file=sys.stderr)
                    continue
                last = min(last, total - 1)
                if PROBE:
                    plog(f"    {tag} cell {cell['cell']}: sectors {first}-{last}")
                sets, navs = scan_cell(parts, first, last)
                entry["screens"].append({
                    "cell": cell["cell"],
                    "image": None,
                    "nav_packs": navs,
                    "sectors": [first, last],
                    "button_sets": sets,
                    "_parts": parts,
                    "_tag": tag,
                })
            result["menus"].append(entry)

    resolver = Resolver(domains, result["titles"])
    for dom_key, lang, pgcn, entry in pending:
        for s in entry["screens"]:
            for bs in s["button_sets"]:
                for b in bs["buttons"]:
                    b["resolved"] = resolver.resolve(dom_key, lang, pgcn,
                                                     b["target"])

    joined = join_titles(result, mkv, rip_files) if mkv else {}
    result["rips"] = joined

    # --- images, once every button is resolved --------------------------
    # Deferred to here on purpose: which screens are worth rendering depends
    # on what the buttons resolve to, which needs every IFO parsed first.
    if images_ok:
        cache = {}
        for _, _, _, entry in pending:
            for s in entry["screens"]:
                if not keep_screen(s, all_images):
                    continue
                key = (entry["ifo"], s["sectors"][0], s["sectors"][1])
                if key not in cache:
                    png = outdir / f"{s['_tag']}_cell{s['cell']}.png"
                    cache[key] = (png.name
                                  if grab_menu_image(s["_parts"],
                                                     s["sectors"][0],
                                                     s["sectors"][1],
                                                     png, tmpdir) else None)
                base = cache[key]
                if not base:
                    continue
                s["image"] = base
                for i, bs in enumerate(s["button_sets"], 1):
                    sfx = "" if len(s["button_sets"]) == 1 else f"_set{i}"
                    out = outdir / (f"{s['_tag']}_cell{s['cell']}{sfx}"
                                    f"_buttons.png")
                    hdr = (f"{(mkv or {}).get('disc') or Path(video_ts).parent.name}"
                           f"   {entry['ifo']} pgc {entry['pgc']}"
                           f"   - type the name shown on screen next to each file")
                    bs["image"] = naming_sheet(
                        outdir / base, bs["buttons"], out, hdr,
                        lambda b: legend_row(b, joined))
                    if crops:
                        crop_buttons(outdir / base, bs["buttons"], outdir,
                                     f"{s['_tag']}_cell{s['cell']}{sfx}")

    for _, _, _, entry in pending:
        for s in entry["screens"]:
            s.pop("_parts", None)
            s.pop("_tag", None)

    result["coverage"] = title_coverage(result, domains)

    close_handles()
    if _PROBE_FH:
        _PROBE_FH.close()
        globals()["_PROBE_FH"] = None
        print(f"wrote {outdir}/probe.log", file=sys.stderr)
    shutil.rmtree(tmpdir, ignore_errors=True)
    if _BAD_SECTORS:
        print(f"! {_BAD_SECTORS} sectors unreadable", file=sys.stderr)
    write_names_csv(result, outdir / "names.csv")
    (outdir / "menumap.json").write_text(json.dumps(result, indent=2))
    write_report(result, outdir / "menumap.txt")
    return result


def write_names_csv(result, path):
    """One row per title: the rip, where to read its name, and a blank to
    fill in. Titles no button reaches still get a row, so nothing is lost."""
    where = {}
    for m in result["menus"]:
        for s in m["screens"]:
            for bs in s["button_sets"]:
                for b in bs["buttons"]:
                    r = b.get("resolved") or {}
                    if r.get("kind") == "title":
                        where.setdefault(r["title"], []).append(
                            (bs.get("image") or s.get("image") or "",
                             m["pgc"], b["button"]))
    rips = result.get("rips") or {}
    with open(path, "w", newline="", encoding="utf-8") as fh:
        w = csv.writer(fh)
        w.writerow(["title", "vts", "chapters", "duration", "bytes",
                    "current_file", "bound", "new_name", "confidence",
                    "source", "read_from_image", "pgc", "button", "note"])
        for t in result["titles"]:
            n = t["title"]
            rip = rips.get(n) or {}
            spots = where.get(n) or []
            img, pgc, btn = spots[0] if spots else ("", "", "")
            note = rip.get("note") or ("" if spots
                                       else "no menu button reaches this title")
            bound = ("" if "bound" not in rip
                     else ("yes" if rip["bound"] else "no"))
            w.writerow([n, t["vts"], t["chapters"], rip.get("duration", ""),
                        rip.get("bytes", ""), rip.get("file", ""), bound,
                        "", "", "", img, pgc, btn, note])


def bs_image_for(menu, button):
    return button.get("_image")


def write_report(result, path):
    lines = []
    if result["titles"]:
        lines.append("TITLES (global numbering, as reported by the disc)")
        for t in result["titles"]:
            lines.append(f"  Title {t['title']:2d}  VTS {t['vts']} "
                         f"#{t['vts_title']}  chapters={t['chapters']} "
                         f"angles={t['angles']}")
        lines.append("")

    cov = result.get("coverage") or []
    if cov:
        lines.append("TITLE COVERAGE")
        for c in cov:
            head = (f"  Title {c['title']:2d}  (VTS {c['vts']} #"
                    f"{c['vts_title']}, {c['chapters']} ch)  ")
            if c["named_by"]:
                for i, n in enumerate(c["named_by"]):
                    where = (f"{n['ifo']} pgc {n['pgc']} button {n['button']}")
                    img = f"  [{n['image']}]" if n.get("image") else ""
                    lines.append((head if i == 0 else " " * len(head)) +
                                 f"named by {where}{img}")
            else:
                lines.append(head + "NO MENU BUTTON REACHES THIS TITLE")
                if c["reached_from"]:
                    lines.append(" " * len(head) +
                                 "reached from " + ", ".join(c["reached_from"]))
        lines.append("")
        lines.append("=" * 70)
        lines.append("")

    rips = result.get("rips") or {}
    if any("bound" in r for r in rips.values()):
        lines.append("RIP BINDING  (name from TINFO field 27, size checked "
                     "against field 11)")
        for n in sorted(rips):
            r = rips[n]
            if "bound" not in r:
                continue
            lines.append(f"  Title {n:2d}  {r.get('bytes', 0):>13,d} bytes  "
                         f"{'->' if r['bound'] else 'XX'}  {r.get('file', '')}")
            if not r["bound"] and r.get("note"):
                lines.append(f"            {r['note']}")
        for f in result.get("rip_orphans") or []:
            lines.append(f"  unclaimed file in the rip folder: {f}")
        lines.append("")
        lines.append("=" * 70)
        lines.append("")
    elif rips:
        lines.append("! filenames below come from the info run and are "
                     "UNVERIFIED - rerun with --rips <folder> to bind on "
                     "exact byte count")
        lines.append("")

    rows = []
    for m in result["menus"]:
        for s in m["screens"]:
            for bs in s["button_sets"]:
                for b in bs["buttons"]:
                    if b.get("resolved"):
                        b["_image"] = bs.get("image") or s.get("image")
                        rows.append((m, b))
    if rows:
        lines.append("BUTTON -> TITLE MAP")
        lines.append("  (button numbers are drawn on the annotated menu images)")
        last = None
        for m, b in rows:
            head = f"{m['ifo']}  {m['menu']} menu, pgc {m['pgc']}"
            if head != last:
                lines.append(f"\n  {head}")
                img = bs_image_for(m, b)
                if img:
                    lines.append(f"    read the labels off: {img}")
                last = head
            x0, y0, x1, y1 = b["rect"]
            lines.append(f"    btn {b['button']:2d}  ({x0:4d},{y0:4d})  "
                         f"{describe_result(b['resolved'])}")
            if b.get("crop"):
                lines.append(f"            crop:  {b['crop']}")
            lines.append(f"            path:  " +
                         " -> ".join(b["resolved"].get("trace") or ["-"]))
            for n in b["resolved"].get("notes") or []:
                lines.append(f"            note:  {n}")
        lines.append("")
        lines.append("=" * 70)
        lines.append("")

    for m in result["menus"]:
        cmds = m.get("commands") or {"pre": [], "post": [], "cell": []}
        flag = " ENTRY" if m.get("is_entry_pgc") else ""
        lines.append(f"{m['ifo']}  [{m['menu']} menu, pgc {m['pgc']}, "
                     f"lang {m['lang']}]{flag}")
        lines.append(f"  programs={m.get('programs')} cells={m.get('n_cells')} "
                     f"still={m.get('still_time')} "
                     f"next={m.get('next_pgc')} prev={m.get('prev_pgc')} "
                     f"goup={m.get('goup_pgc')}")
        if m.get("program_map"):
            lines.append(f"  program map (first cell of each program): "
                         f"{m['program_map']}")

        for which in ("pre", "post", "cell"):
            block = cmds.get(which) or []
            if not block:
                continue
            lines.append(f"  {which}-commands ({len(block)}):")
            for c in block:
                lines.append(f"    [{which} {c['n']:2d}] {c['raw']}  {c['text']}")

        for cell in m.get("cells") or []:
            lines.append(f"  cell {cell['cell']} table: cmd_nr="
                         f"{cell.get('cell_cmd_nr')} still={cell['still_time']} "
                         f"sectors {cell['first_sector']}-{cell['last_sector']}")

        if not m["screens"]:
            lines.append("  (no menu video parsed for this pgc)")
        for s in m["screens"]:
            img = f"  image: {s['image']}" if s["image"] else ""
            lines.append(f"  cell {s['cell']}{img}")
            if not s["button_sets"]:
                lines.append(f"    (no buttons; {s.get('nav_packs',0)} nav packs "
                             f"in sectors {s['sectors'][0]}-{s['sectors'][1]})")
            for bs in s["button_sets"]:
                for b in bs["buttons"]:
                    crop = f"  [{b['crop']}]" if b.get("crop") else ""
                    x0, y0, x1, y1 = b["rect"]
                    lines.append(
                        f"    btn {b['button']:2d}  "
                        f"({x0:4d},{y0:4d})-({x1:4d},{y1:4d})  "
                        f"{b['raw']}  -> {b['command']}{crop}")
                    if b.get("resolved"):
                        lines.append(f"           resolves to: "
                                     f"{describe_result(b['resolved'])}")
        lines.append("")
    Path(path).write_text("\n".join(lines))
    print("\n".join(lines))


def selftest():
    """Round-trip a few hand-built commands through the decoder."""
    def mk(*bs):
        return bytes(bs) + bytes(8 - len(bs))

    cases = [
        (mk(0x30, 0x02, 0x00, 0x00, 0x00, 0x85), "JumpTT 5"),
        (mk(0x30, 0x05, 0x00, 0x04, 0x00, 0x83), "JumpVTS_PTT 3:4"),
        (mk(0x20, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x07), "LinkPGCN 7"),
        (mk(0x00, 0x00), "Nop"),
        # real bytes off THE_CENTENNIAL, VTS_01_0.IFO
        (bytes.fromhex("7104000e006e000b"), "g[14] = 0x6e, LinkPGCN 11"),
        (bytes.fromhex("7101000e0002000d"), "g[14] = 0x2, LinkTailPGC (button 0)"),
        (bytes.fromhex("51060000c0000c01"), "SPSTN(s[2]) = 0x40 , LinkPGN 1 (button 3)"),
        (bytes.fromhex("200100000000040d"), "LinkTailPGC (button 1)"),
    ]
    ok = True
    for raw, want in cases:
        got, _ = decode_command(raw)
        flag = "ok " if got == want else "FAIL"
        if got != want:
            ok = False
        print(f"{flag} {raw.hex()}  -> {got!r:28} want {want!r}")
    return 0 if (ok and selftest_resolver() and selftest_rips()) else 1


def selftest_rips():
    """Name-keyed binding with the size guard, driven with THE_CENTENNIAL's
    real numbers: field 11 against the sizes the remux actually produced."""
    # title: (field 27 name, field 11), and what the rip really wrote
    info = {1: ("C1_t00.mkv", 4774254592), 2: ("C2_t01.mkv", 3084769280),
            3: ("B1_t02.mkv", 7859023872), 4: ("A1_t03.mkv", 115417088),
            5: ("A1_t04.mkv", 14385152),   6: ("B1_t05.mkv", 9750528)}
    disk = {"C1_t00.mkv": 4670233631, "C2_t01.mkv": 3017161275,
            "B1_t02.mkv": 7687412633, "A1_t03.mkv": 113067060,
            "A1_t04.mkv": 14076338,   "B1_t05.mkv": 9559952}

    cases = [
        ("real rip binds 6/6", info, disk,
         {n: info[n][0] for n in info}, []),
        ("title never ripped", info,
         {k: v for k, v in disk.items() if k != "A1_t04.mkv"},
         {n: info[n][0] for n in info if n != 5}, []),
        ("extra file nothing claims", info, dict(disk, bonus=999),
         {n: info[n][0] for n in info}, ["bonus"]),
        # the whole point: a shifted rip keeps the names and moves the content
        ("minlength shift - names line up with wrong titles", info,
         {"C1_t00.mkv": 4670233631, "C2_t01.mkv": 113067060,
          "B1_t02.mkv": 14076338},
         {1: "C1_t00.mkv"}, ["B1_t02.mkv", "C2_t01.mkv"]),
        # an interrupted rip leaves a short file under the right name
        ("truncated rip refused", info, {"C1_t00.mkv": 2000000000},
         {}, ["C1_t00.mkv"]),
    ]
    ok = True
    for label, want, have, want_bound, want_orphans in cases:
        bound, reasons, orphans = match_rips(want, have)
        good = bound == want_bound and orphans == want_orphans
        ok = ok and good
        print(f"{'ok ' if good else 'FAIL'} rips: {label}")
        if not good:
            print(f"     got bound={bound} orphans={orphans}")

    bound, reasons, _ = match_rips(info, {"C1_t00.mkv": 4670233631})
    missing = [n for n in info if n not in bound and n not in reasons]
    ok = ok and not missing
    print(f"{'ok ' if not missing else 'FAIL'} rips: every refusal carries a "
          f"reason")
    return ok


def selftest_resolver():
    """Walk a miniature disc built from THE_CENTENNIAL's real dispatcher:
    a button sets g[14], links to a pass-through PGC, which links to the
    PGC whose pre-commands pick the title by comparing g[14]."""
    def block(*hexes):
        out = []
        for i, h in enumerate(hexes):
            raw = bytes.fromhex(h)
            text, info = decode_command(raw)
            out.append({"n": i + 1, "raw": h, "text": text, "info": info})
        return out

    def pgc(n, pre=(), post=(), cells=0, entry=False, menu_id=0):
        return {"lang": "en", "entry": entry, "entry_id": 0,
                "menu_id": menu_id, "menu": "root", "pgcn": n,
                "programs": 0, "n_cells": cells, "still_time": 0,
                "next_pgc": 0, "prev_pgc": 0, "goup_pgc": 0,
                "program_map": [], "cells": [{"cell": 1}] * cells,
                "commands": {"pre": list(pre), "post": list(post),
                             "cell": []}}

    pgcs = {
        1: pgc(1, pre=block("2006000000000401"), cells=1, entry=True, menu_id=3),
        8: pgc(8, pre=block("7100000000650000",   # g[0] = 0x65
                            "3043000000030e00",   # if g[14] >= g[0] JumpVTS_TT 3
                            "7100000000020000",   # g[0] = 2
                            "3023000000020e00",   # if g[14] == g[0] JumpVTS_TT 2
                            "3003000000010000")), # JumpVTS_TT 1
        11: pgc(11, pre=block("2004000000000008")),
    }
    domains = {("vts", 1): {"pgcs": {"en": pgcs}, "vts": 1}}
    titles = [{"title": i, "vts": 1, "vts_title": i, "chapters": 5, "angles": 1}
              for i in (1, 2, 3)]
    r = Resolver(domains, titles)

    cases = [("7104000e006e000b", 3),   # g[14]=110 -> LinkPGCN 11 -> title 3
             ("7104000e0002000b", 2),   # g[14]=2               -> title 2
             ("7104000e0001000b", 1)]   # g[14]=1               -> title 1
    ok = True
    for h, want in cases:
        _, info = decode_command(bytes.fromhex(h))
        res = r.resolve(("vts", 1), "en", 1, info)
        got = res.get("title")
        if got != want:
            ok = False
        print(f"{'ok ' if got == want else 'FAIL'} {h}  -> "
              f"{describe_result(res):32} want title {want}")
    return ok


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("video_ts", nargs="?", help="path to a VIDEO_TS folder")
    ap.add_argument("-o", "--outdir", default=None,
                    help="output folder (default: menumap_out, or "
                         "work\\<DISC>\\menumap_out under --disc)")
    ap.add_argument("--disc", metavar="NAME",
                    help="a disc folder under work\\ - fills in --iso, "
                         "--mkv-info, -o and --rips from the layout; any of "
                         "those given explicitly wins")
    ap.add_argument("--work", metavar="DIR",
                    help="root holding the per-disc folders "
                         "(default: work\\ beside this script)")
    ap.add_argument("--rips-root", metavar="DIR", default=None,
                    help="where rip folders live (default: "
                         + DEFAULT_RIPS_ROOT + ")")
    ap.add_argument("--no-images", action="store_true",
                    help="parse only, skip ffmpeg screenshots")
    ap.add_argument("--all-images", action="store_true",
                    help="render every menu cell, not just the ones whose "
                         "buttons can reach a title")
    ap.add_argument("--mkv-info", metavar="FILE",
                    help="output of `makemkvcon -r --minlength=0 info ...`, "
                         "to bind each title to its ripped file")
    ap.add_argument("--iso", metavar="FILE",
                    help="disc image to mount and read instead of giving a "
                         "VIDEO_TS path; already-mounted images are reused")
    ap.add_argument("--dismount", action="store_true",
                    help="with --iso, eject the image again when done "
                         "(default: leave it mounted for the next run)")
    ap.add_argument("--rips", metavar="FOLDER",
                    help="folder holding the ripped files; each title is "
                         "bound to the file named in TINFO field 27, and only "
                         "if its size sits in the expected band of field 11 "
                         "(needs --mkv-info)")
    ap.add_argument("--crops", action="store_true",
                    help="also write one PNG per button hotspot (note: the "
                         "hotspot often clips the caption)")
    ap.add_argument("--no-stage", action="store_true",
                    help="parse the VOBs in place instead of copying them first")
    ap.add_argument("--probe", action="store_true",
                    help="dump per-sector nav/HLI diagnostics to stderr")
    ap.add_argument("--selftest", action="store_true")
    a = ap.parse_args()

    if a.selftest:
        return selftest()
    globals()["PROBE"] = a.probe

    if a.disc:
        if a.video_ts:
            ap.error("give either a VIDEO_TS folder or --disc, not both")
        d = resolve_disc(a.disc, a.work, a.rips_root)
        a.iso = a.iso or (str(d["iso"]) if d["iso"] else None)
        a.mkv_info = a.mkv_info or (str(d["info"]) if d["info"] else None)
        a.outdir = a.outdir or str(d["outdir"])
        a.rips = a.rips or (str(d["rips"]) if d["rips"] else None)
        print(f"disc {a.disc}   {d['folder']}")
        print(f"  iso   {a.iso or '(none in the disc folder)'}")
        print(f"  info  {a.mkv_info or '(no titles.txt in the disc folder)'}")
        print(f"  rips  {a.rips or '(not there yet: %s)' % d['rips_wanted']}")
        print(f"  out   {a.outdir}")
        if not a.iso:
            ap.error(f"--disc: no .iso in {d['folder']} - pass --iso, or give "
                     f"a VIDEO_TS folder instead")
    a.outdir = a.outdir or "menumap_out"

    if a.rips and not a.mkv_info:
        ap.error("--rips needs --mkv-info: the byte counts come from the "
                 "makemkvcon info output")
    if not a.video_ts and not a.iso:
        ap.error("give a VIDEO_TS folder, --iso <file> or --disc <name> "
                 "(or --selftest)")

    video_ts, mounted_by_us = a.video_ts, False
    if a.iso:
        if a.video_ts:
            ap.error("give either a VIDEO_TS folder or --iso, not both")
        video_ts, _letter, mounted_by_us = mount_iso(a.iso)
    try:
        analyse(video_ts, a.outdir, do_images=not a.no_images,
                stage=not a.no_stage, all_images=a.all_images, crops=a.crops,
                mkv_info=a.mkv_info, rips=a.rips)
    finally:
        if a.dismount and mounted_by_us:
            dismount_iso(a.iso)
        elif a.dismount and a.iso:
            print("  left mounted: this run did not mount it")
    print(f"\nwrote {a.outdir}/menumap.json and menumap.txt")
    return 0


if __name__ == "__main__":
    sys.exit(main())
