// ============================================================
// VmCommand.cs
// ------------------------------------------------------------
// Decodes one 8-byte DVD virtual-machine command: a menu button's
// command, or one of a PGC's pre, post or cell commands.
//
// A straight port of the oracle's decode_command
// (tools\oracle\dvdmenumap.py), which is itself a port of libdvdnav
// vm/vmcmd.c :: vm_print_mnemonic(), including the if / set prefixes.
// The text it produces must match the oracle's character for
// character -- the comparison against the oracle depends on it -- so
// spacing oddities such as "SPSTN(s[2]) = 0x40 , LinkPGN 1" are kept.
//
// Returns the text and the structured parts: the terminating link or
// jump (Op and its fields) plus any If, Set and system-set parts.
// The structured parts are what the PGC resolver (seam 3) branches on;
// only Text and Raw are saved on the disc record.
//
// Pure: bytes in, a command out. No I/O.
// ============================================================

using System.Text;

namespace TranscodeTools;

// A register or immediate value an if / set part refers to.
public sealed record VmRef(string Kind, int Value)   // Kind: "imm", "gprm" or "sprm"
{
    public static VmRef Imm(int v)  => new("imm", v);
    public static VmRef Gprm(int n) => new("gprm", n);
    public static VmRef Sprm(int n) => new("sprm", n);
}

public sealed record VmIf(VmRef Lhs, string Op, int OpN, VmRef Rhs);

public sealed record VmSet(VmRef Dst, string Op, int OpN, VmRef Src);

// One register written by a system set. Sprm is set for SPRM writes,
// Gprm and Mode for SetMode.
public sealed record VmSystemSet(int? Sprm, int? Gprm, string? Mode, VmRef Src);

public sealed class VmCommand
{
    public string Raw  { get; init; } = "";   // the 8 bytes as lower-case hex
    public string Text { get; init; } = "";

    // The terminating operation, as the oracle names it: "LinkPGCN",
    // "JumpTT", "linksub", "nop", "Goto", "other", "unknown_link"...
    public string Op { get; internal set; } = "other";

    // Operation fields; only the ones the operation carries are set.
    public string? Sub      { get; internal set; }   // link subinstruction name, for "linksub"
    public int?    Button   { get; internal set; }
    public int?    Pgcn     { get; internal set; }
    public int?    Ptt      { get; internal set; }
    public int?    Pgn      { get; internal set; }
    public int?    Cn       { get; internal set; }
    public int?    Title    { get; internal set; }
    public int?    VtsTitle { get; internal set; }
    public int?    Chapter  { get; internal set; }
    public string? Target   { get; internal set; }   // "FP", "VMGM" or "VTSM"
    public int?    Vts      { get; internal set; }
    public int?    Menu     { get; internal set; }
    public string? MenuName { get; internal set; }
    public int?    RsmCell  { get; internal set; }
    public int?    Line     { get; internal set; }
    public int?    Level    { get; internal set; }
    public int?    SubOp    { get; internal set; }   // for the unknown_* ops

    public VmIf?  If  { get; internal set; }
    public VmSet? Set { get; internal set; }
    public IReadOnlyList<VmSystemSet> SystemSets { get; internal set; } = Array.Empty<VmSystemSet>();

    public override string ToString() => Text;

    // ── Name tables (from the oracle, which has them from libdvdnav) ─

    private static readonly Dictionary<int, string> MenuNames = new()
    {
        [2] = "title", [3] = "root", [4] = "subpicture",
        [5] = "audio", [6] = "angle", [7] = "chapter",
    };

    private static readonly string[] LinkTable =
    {
        "LinkNoLink", "LinkTopC", "LinkNextC", "LinkPrevC",
        "", "LinkTopPG", "LinkNextPG", "LinkPrevPG",
        "", "LinkTopPGC", "LinkNextPGC", "LinkPrevPGC",
        "LinkGoUpPGC", "LinkTailPGC", "", "",
        "RSM",
    };

    private static readonly string[] CmpOpTable = { "", "&", "==", "!=", ">=", ">", "<=", "<" };

    private static readonly string[] SetOpTable =
        { "", "=", "<->", "+=", "-=", "*=", "/=", "%=", "rnd", "&=", "|=", "^=" };

    private static readonly string[] SprmNames =
    {
        "MENU_LANG", "ASTN", "SPSTN", "AGLN", "TTN", "VTS_TTN", "TT_PGCN",
        "PTTN", "HL_BTNN", "NVTMR", "NV_PGCN", "KARAOKE_MIX", "CC_PLT", "PLT",
        "VIDEO_CFG", "AUDIO_CFG", "INIT_AUDIO_LANG", "INIT_AUDIO_LANG_EXT",
        "INIT_SPU_LANG", "INIT_SPU_LANG_EXT", "REGION", "RSV21", "RSV22", "RSV23",
    };

    // ── Decode ───────────────────────────────────────────────────────

    public static VmCommand Decode(ReadOnlySpan<byte> cmd8)
    {
        if (cmd8.Length != 8)
            throw new ArgumentException($"A VM command is 8 bytes, not {cmd8.Length}.", nameof(cmd8));
        return new Decoder(cmd8).Run();
    }

    private sealed class Decoder
    {
        private readonly ulong         _instr;
        private readonly string        _raw;
        private readonly StringBuilder _out  = new();
        private readonly VmCommand     _cmd  = new();
        private readonly List<VmSystemSet> _sets = new();
        private bool _opSet;

        public Decoder(ReadOnlySpan<byte> cmd8)
        {
            ulong v = 0;
            foreach (var b in cmd8) v = (v << 8) | b;
            _instr = v;
            _raw   = Convert.ToHexString(cmd8).ToLowerInvariant();
        }

        // libdvdnav vm_getbits: `start` is the index of the field's MSB,
        // bit 63 = MSB of byte 0 of the 8-byte command.
        private int Gb(int start, int count) =>
            (int)((_instr >> (start - count + 1)) & ((1UL << count) - 1));

        private static VmRef RegRef(int r) =>
            (r & 0x80) != 0 ? VmRef.Sprm(r & 0x7F) : VmRef.Gprm(r & 0x7F);

        private static string SprmName(int n) => n < SprmNames.Length ? SprmNames[n] : $"SPRM{n}";

        private static string RefName(VmRef r) => r.Kind switch
        {
            "imm"  => $"0x{r.Value:x}",
            "sprm" => $"{SprmName(r.Value)}(s[{r.Value}])",
            _      => $"g[{r.Value}]",
        };

        private VmRef RegOrData(int imm, int start) =>
            imm != 0 ? VmRef.Imm(Gb(start, 16)) : RegRef(Gb(start - 8, 8));

        private VmRef RegOrData2(int imm, int start) =>
            imm != 0 ? VmRef.Imm(Gb(start - 1, 7)) : VmRef.Gprm(Gb(start - 4, 4));

        private VmRef RegOrData3(int imm, int start) =>
            imm != 0 ? VmRef.Imm(Gb(start, 16)) : RegRef(Gb(start, 8));

        private void SetOp(string op)
        {
            _cmd.Op = op;
            _opSet  = true;
        }

        // ── if prefixes (vmcmd.c print_if_version_N) ─────────────────

        private static string CmpName(int op) => op < CmpOpTable.Length ? CmpOpTable[op] : $"cmp?{op}";

        private void EmitIf(VmRef lhs, int op, VmRef rhs)
        {
            _out.Append($"if ({RefName(lhs)} {CmpName(op)} {RefName(rhs)}) ");
            _cmd.If = new VmIf(lhs, CmpName(op), op, rhs);
        }

        private void IfV1()
        {
            int op = Gb(54, 3);
            if (op != 0) EmitIf(VmRef.Gprm(Gb(39, 8)), op, RegOrData(Gb(55, 1), 31));
        }

        private void IfV2()
        {
            int op = Gb(54, 3);
            if (op != 0) EmitIf(RegRef(Gb(15, 8)), op, RegRef(Gb(7, 8)));
        }

        private void IfV3()
        {
            int op = Gb(54, 3);
            if (op != 0) EmitIf(VmRef.Gprm(Gb(43, 4)), op, RegOrData(Gb(55, 1), 15));
        }

        private void IfV4()
        {
            int op = Gb(54, 3);
            if (op != 0) EmitIf(VmRef.Gprm(Gb(51, 4)), op, RegOrData(Gb(55, 1), 31));
        }

        private void IfV5()
        {
            int op = Gb(54, 3);
            if (op == 0) return;
            if (Gb(60, 1) != 0)
                EmitIf(VmRef.Gprm(Gb(31, 8)), op, RegRef(Gb(23, 8)));
            else
                EmitIf(VmRef.Gprm(Gb(39, 8)), op, RegOrData(Gb(55, 1), 31));
        }

        // ── set parts (print_set_version_N / print_system_set) ───────

        private static string SetName(int op) => op < SetOpTable.Length ? SetOpTable[op] : $"set?{op}";

        private void EmitSet(VmRef dst, int op, VmRef src)
        {
            _out.Append($"{RefName(dst)} {SetName(op)} {RefName(src)}");
            _cmd.Set = new VmSet(dst, SetName(op), op, src);
        }

        private void SetV1()
        {
            int op = Gb(59, 4);
            if (op == 0) { _out.Append("NOP"); return; }
            EmitSet(VmRef.Gprm(Gb(35, 4)), op, RegOrData(Gb(60, 1), 31));
        }

        private void SetV2()
        {
            int op = Gb(59, 4);
            if (op == 0) { _out.Append("NOP"); return; }
            EmitSet(VmRef.Gprm(Gb(51, 4)), op, RegOrData(Gb(60, 1), 47));
        }

        private void SetV3()
        {
            int op = Gb(59, 4);
            if (op == 0) { _out.Append("NOP"); return; }
            EmitSet(VmRef.Gprm(Gb(51, 4)), op, RegOrData3(Gb(60, 1), 47));
        }

        private void SystemSet()
        {
            int kind = Gb(59, 4);
            if (kind == 1)                         // SPRM 1/2/3: audio, subpicture, angle
            {
                for (int i = 1; i <= 3; i++)
                {
                    if (Gb(47 - i * 8, 1) != 0)
                    {
                        var r = RegOrData2(Gb(60, 1), 47 - i * 8);
                        _out.Append($"{SprmNames[i]}(s[{i}]) = {RefName(r)} ");
                        _sets.Add(new VmSystemSet(i, null, null, r));
                    }
                }
            }
            else if (kind == 2)                    // SPRM 9/10: navigation timer
            {
                var r = RegOrData(Gb(60, 1), 47);
                _out.Append($"NVTMR(s[9]) = {RefName(r)} NV_PGCN(s[10]) = {Gb(30, 15)}");
                _sets.Add(new VmSystemSet(9, null, null, r));
                _sets.Add(new VmSystemSet(10, null, null, VmRef.Imm(Gb(30, 15))));
            }
            else if (kind == 3)                    // SetMode counter / register
            {
                var r    = RegOrData(Gb(60, 1), 47);
                var mode = Gb(23, 1) != 0 ? "Counter" : "Register";
                _out.Append($"SetMode {mode} g[{Gb(19, 4)}] = {RefName(r)}");
                _sets.Add(new VmSystemSet(null, Gb(19, 4), mode, r));
            }
            else if (kind == 6)                    // SPRM 8: highlighted button
            {
                VmRef r;
                if (Gb(60, 1) != 0)
                {
                    r = VmRef.Imm(Gb(31, 16));
                    _out.Append($"HL_BTNN(s[8]) = 0x{Gb(31, 16):x} (button {Gb(31, 6)})");
                }
                else
                {
                    r = VmRef.Gprm(Gb(19, 4));
                    _out.Append($"HL_BTNN(s[8]) = {RefName(r)}");
                }
                _sets.Add(new VmSystemSet(8, null, null, r));
            }
            else
            {
                _out.Append($"WARNING: unknown system set {kind}");
            }
        }

        // ── link / jump (print_link_instruction / print_jump_instruction)

        private void LinkSub()
        {
            int linkOp = Gb(7, 8), btn = Gb(15, 6);
            var name = linkOp < LinkTable.Length && LinkTable[linkOp].Length > 0
                ? LinkTable[linkOp]
                : $"Link?{linkOp}";
            _out.Append($"{name} (button {btn})");
            SetOp("linksub");
            _cmd.Sub    = name;
            _cmd.Button = btn;
        }

        private void LinkInstruction(bool optional)
        {
            int op = Gb(51, 4);
            if (op == 0)
            {
                if (!optional) _out.Append("WARNING: NOP (link)");
                return;
            }
            if (optional) _out.Append(", ");

            switch (op)
            {
                case 1:
                    LinkSub();
                    break;
                case 4:
                {
                    int n = Gb(14, 15);
                    _out.Append($"LinkPGCN {n}");
                    SetOp("LinkPGCN"); _cmd.Pgcn = n;
                    break;
                }
                case 5:
                {
                    int n = Gb(9, 10), btn = Gb(15, 6);
                    _out.Append($"LinkPTT {n} (button {btn})");
                    SetOp("LinkPTT"); _cmd.Ptt = n; _cmd.Button = btn;
                    break;
                }
                case 6:
                {
                    int n = Gb(6, 7), btn = Gb(15, 6);
                    _out.Append($"LinkPGN {n} (button {btn})");
                    SetOp("LinkPGN"); _cmd.Pgn = n; _cmd.Button = btn;
                    break;
                }
                case 7:
                {
                    int n = Gb(7, 8), btn = Gb(15, 6);
                    _out.Append($"LinkCN {n} (button {btn})");
                    SetOp("LinkCN"); _cmd.Cn = n; _cmd.Button = btn;
                    break;
                }
                default:
                    _out.Append($"WARNING: unknown link op {op}");
                    SetOp("unknown_link"); _cmd.SubOp = op;
                    break;
            }
        }

        private void JumpInstruction()
        {
            int op = Gb(51, 4);
            switch (op)
            {
                case 1:
                    _out.Append("Exit");
                    SetOp("Exit");
                    break;
                case 2:
                {
                    int tt = Gb(22, 7);
                    _out.Append($"JumpTT {tt}");
                    SetOp("JumpTT"); _cmd.Title = tt;
                    break;
                }
                case 3:
                {
                    int tt = Gb(22, 7);
                    _out.Append($"JumpVTS_TT {tt}");
                    SetOp("JumpVTS_TT"); _cmd.VtsTitle = tt;
                    break;
                }
                case 5:
                {
                    int tt = Gb(22, 7), ptt = Gb(41, 10);
                    _out.Append($"JumpVTS_PTT {tt}:{ptt}");
                    SetOp("JumpVTS_PTT"); _cmd.VtsTitle = tt; _cmd.Chapter = ptt;
                    break;
                }
                case 6:
                case 8:
                {
                    var kind  = op == 6 ? "JumpSS" : "CallSS";
                    int which = Gb(23, 2);
                    var rsm   = op == 8 ? $", rsm_cell {Gb(31, 8)}" : "";
                    SetOp(kind);
                    if (op == 8) _cmd.RsmCell = Gb(31, 8);

                    if (which == 0)
                    {
                        _out.Append($"{kind} FP{rsm}");
                        _cmd.Target = "FP";
                    }
                    else if (which == 1)
                    {
                        int m = Gb(19, 4);
                        _out.Append($"{kind} VMGM (menu {m}{rsm})");
                        _cmd.Target = "VMGM"; _cmd.Menu = m; _cmd.MenuName = MenuName(m);
                    }
                    else if (which == 2)
                    {
                        int m = Gb(19, 4);
                        if (op == 6)
                        {
                            _out.Append($"JumpSS VTSM (vts {Gb(30, 7)}, title {Gb(38, 7)}, menu {m})");
                            _cmd.Target = "VTSM"; _cmd.Vts = Gb(30, 7); _cmd.VtsTitle = Gb(38, 7);
                            _cmd.Menu = m; _cmd.MenuName = MenuName(m);
                        }
                        else
                        {
                            _out.Append($"CallSS VTSM (menu {m}{rsm})");
                            _cmd.Target = "VTSM"; _cmd.Menu = m; _cmd.MenuName = MenuName(m);
                        }
                    }
                    else
                    {
                        int pgc = Gb(46, 15);
                        _out.Append($"{kind} VMGM (pgc {pgc}{rsm})");
                        _cmd.Target = "VMGM"; _cmd.Pgcn = pgc;
                    }
                    break;
                }
                default:
                    _out.Append($"WARNING: unknown jump op {op}");
                    SetOp("unknown_jump"); _cmd.SubOp = op;
                    break;
            }
        }

        private static string? MenuName(int m) => MenuNames.TryGetValue(m, out var n) ? n : null;

        private void Special()
        {
            int op = Gb(51, 4);
            switch (op)
            {
                case 0: _out.Append("Nop");                 SetOp("nop");   break;
                case 1: _out.Append($"Goto {Gb(7, 8)}");    SetOp("Goto");  _cmd.Line = Gb(7, 8); break;
                case 2: _out.Append("Break");               SetOp("Break"); break;
                case 3:
                    _out.Append($"SetTmpPML {Gb(11, 4)}, Goto {Gb(7, 8)}");
                    SetOp("SetTmpPML"); _cmd.Level = Gb(11, 4); _cmd.Line = Gb(7, 8);
                    break;
                default:
                    _out.Append($"WARNING: unknown special {op}");
                    SetOp("unknown_special"); _cmd.SubOp = op;
                    break;
            }
        }

        public VmCommand Run()
        {
            if (_instr == 0)
                return new VmCommand { Raw = _raw, Text = "Nop", Op = "nop" };

            int top = Gb(63, 3);
            switch (top)
            {
                case 0:
                    IfV1(); Special();
                    break;
                case 1:
                    if (Gb(60, 1) != 0) { IfV2(); JumpInstruction(); }
                    else                { IfV1(); LinkInstruction(false); }
                    break;
                case 2:
                    IfV2(); SystemSet(); LinkInstruction(true);
                    break;
                case 3:
                    IfV3(); SetV1(); LinkInstruction(true);
                    break;
                case 4:
                    SetV2(); _out.Append(", "); IfV4(); LinkSub();
                    break;
                case 5:
                    IfV5(); _out.Append("{ "); SetV3(); _out.Append(", "); LinkSub(); _out.Append(" }");
                    break;
                case 6:
                    IfV5(); _out.Append("{ "); SetV3(); _out.Append(" } "); LinkSub();
                    break;
                default:
                    _out.Append($"WARNING: unknown instruction type {top}");
                    if (!_opSet) SetOp("unknown");
                    break;
            }

            // Python's str.strip(): whitespace off both ends only.
            var text = _out.ToString().Trim();
            return new VmCommand
            {
                Raw        = _raw,
                Text       = text,
                Op         = _cmd.Op,
                Sub        = _cmd.Sub,
                Button     = _cmd.Button,
                Pgcn       = _cmd.Pgcn,
                Ptt        = _cmd.Ptt,
                Pgn        = _cmd.Pgn,
                Cn         = _cmd.Cn,
                Title      = _cmd.Title,
                VtsTitle   = _cmd.VtsTitle,
                Chapter    = _cmd.Chapter,
                Target     = _cmd.Target,
                Vts        = _cmd.Vts,
                Menu       = _cmd.Menu,
                MenuName   = _cmd.MenuName,
                RsmCell    = _cmd.RsmCell,
                Line       = _cmd.Line,
                Level      = _cmd.Level,
                SubOp      = _cmd.SubOp,
                If         = _cmd.If,
                Set        = _cmd.Set,
                SystemSets = _sets.ToArray(),
            };
        }
    }
}
