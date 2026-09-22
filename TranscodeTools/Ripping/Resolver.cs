// ============================================================
// Resolver.cs
// ------------------------------------------------------------
// Follows a menu button's command through the PGC command chains to
// where it really ends up: a title (and chapter), a menu screen, a
// resume, an exit, a loop, or "unresolved" with the reason.
//
// A straight port of the oracle's Resolver class
// (tools\oracle\dvdmenumap.py). It executes the real pre / post /
// cell command blocks on a 16-register machine, because a disc
// decides the target by comparing registers the button set. On
// THE_CENTENNIAL, g[14] is the title selector and VTS 1 PGC 8 is
// the dispatcher every path funnels through.
//
// System registers start at the oracle's player defaults; a chain
// that reads one gets a note saying so. rnd is not executed (it is
// not deterministic) and SetTmpPML is ignored; both leave notes.
// Trace and note text match the oracle's so the two can be compared.
//
// Pure: parsed IFOs in, a result out. No I/O.
// ============================================================

using System.IO;

namespace TranscodeTools;

public sealed class Resolver
{
    private const int MaxSteps = 400;
    private const int MaxDepth = 40;

    private static readonly Dictionary<int, int> SprmDefaults = new()
    {
        [0] = 0x656E,   // menu language "en"
        [1] = 15,       // audio stream: none selected
        [2] = 62,       // sub-picture stream: none selected
        [3] = 1, [4] = 1, [5] = 1, [6] = 1, [7] = 1,
        [8] = 0, [9] = 0, [10] = 0, [11] = 0, [12] = 0,
        [13] = 15,      // parental level: unrestricted
        [20] = 1,       // region
    };

    // One menu domain: the VMG's menus, or one title set's. Languages are
    // kept in the order the IFO lists them; a lookup in a language the
    // domain lacks falls back to the first, as the oracle does.
    private sealed class Domain
    {
        public int? Vts;
        public readonly List<(string Lang, Dictionary<int, IfoMenuPgc> Pgcs)> Languages = new();

        public Dictionary<int, IfoMenuPgc> Table(string lang)
        {
            foreach (var (l, pgcs) in Languages)
                if (l == lang) return pgcs;
            return Languages.Count > 0 ? Languages[0].Pgcs : new Dictionary<int, IfoMenuPgc>();
        }
    }

    private readonly Dictionary<string, Domain> _domains = new();
    private readonly IReadOnlyList<IfoTitle>   _titles;

    public Resolver(IEnumerable<Ifo> ifos, IReadOnlyList<IfoTitle> titles)
        : this(ifos.Select(i => (i.FileName, i.MenuPgcs())), titles) { }

    // The same, from each IFO's file name and menu PGCs. Lets the resolver
    // be checked against the oracle's output without the IFO files.
    public Resolver(IEnumerable<(string IfoFileName, IReadOnlyList<IfoMenuPgc> Pgcs)> menus, IReadOnlyList<IfoTitle> titles)
    {
        _titles = titles;
        foreach (var (fileName, pgcs) in menus)
        {
            var key = DomainKey(fileName);
            if (!_domains.TryGetValue(key, out var domain))
                _domains[key] = domain = new Domain { Vts = VtsNumber(fileName) };

            foreach (var pgc in pgcs)
            {
                var entry = domain.Languages.FindIndex(x => x.Lang == pgc.Lang);
                if (entry < 0)
                {
                    domain.Languages.Add((pgc.Lang, new Dictionary<int, IfoMenuPgc>()));
                    entry = domain.Languages.Count - 1;
                }
                domain.Languages[entry].Pgcs.TryAdd(pgc.Pgcn, pgc);
            }
        }
    }

    // "vmg" for VIDEO_TS.IFO, "vts:N" for VTS_NN_0.IFO.
    public static string DomainKey(string ifoFileName) =>
        VtsNumber(ifoFileName) is int n ? $"vts:{n}" : "vmg";

    private static int? VtsNumber(string ifoFileName)
    {
        var stem = Path.GetFileNameWithoutExtension(ifoFileName).ToUpperInvariant();
        if (stem.StartsWith("VTS_") && stem.Length >= 6 && int.TryParse(stem.AsSpan(4, 2), out var n))
            return n;
        return null;
    }

    // ── Machine state ────────────────────────────────────────────────

    private sealed class State
    {
        public readonly int[]                 G    = new int[16];
        public readonly Dictionary<int, int>  Sprm = new(SprmDefaults);
        public readonly List<int>             SprmSeen = new();
        public readonly List<string>          Notes    = new();
        public readonly List<string>          Trace    = new();
    }

    private static int Read(State st, VmRef r)
    {
        if (r.Kind == "imm")  return r.Value;
        if (r.Kind == "gprm") return st.G[r.Value & 0x0F];
        if (!st.SprmSeen.Contains(r.Value)) st.SprmSeen.Add(r.Value);
        return st.Sprm.TryGetValue(r.Value, out var v) ? v : 0;
    }

    private static bool Compare(int op, int a, int b) => op switch
    {
        1 => (a & b) != 0,
        2 => a == b,
        3 => a != b,
        4 => a >= b,
        5 => a > b,
        6 => a <= b,
        7 => a < b,
        _ => false,
    };

    private static void ApplySet(State st, VmSet s)
    {
        int val = Read(st, s.Src);
        if (s.Dst.Kind != "gprm")
        {
            st.Sprm[s.Dst.Value] = val;
            return;
        }
        int i = s.Dst.Value & 0x0F, cur = st.G[i];
        switch (s.OpN)
        {
            case 1:  st.G[i] = val; break;
            case 2:
                if (s.Src.Kind == "gprm")
                {
                    int j = s.Src.Value & 0x0F;
                    st.G[i] = st.G[j];
                    st.G[j] = cur;
                }
                break;
            case 3:  st.G[i] = (cur + val) & 0xFFFF; break;
            case 4:  st.G[i] = Math.Max(0, cur - val); break;
            case 5:  st.G[i] = (cur * val) & 0xFFFF; break;
            case 6:  st.G[i] = val != 0 ? cur / val : 0xFFFF; break;
            case 7:  st.G[i] = val != 0 ? cur % val : 0xFFFF; break;
            case 9:  st.G[i] = cur & val; break;
            case 10: st.G[i] = cur | val; break;
            case 11: st.G[i] = cur ^ val; break;
            // 8 (rnd) left alone: not deterministic, noted by the caller.
        }
    }

    // A control transfer out of a block: the command that made it (null for
    // the step limit) and its 1-based line.
    private sealed record Transfer(VmCommand? Command, int Line)
    {
        public bool StepLimit => Command == null;
    }

    // Runs a command list from `start`. Returns the command that transferred
    // control, or null when the block ran off the end or hit Break.
    private static Transfer? RunBlock(State st, IReadOnlyList<VmCommand> block, int start)
    {
        int i = Math.Max(0, start - 1), steps = 0;
        while (i < block.Count)
        {
            steps++;
            if (steps > MaxSteps) return new Transfer(null, i + 1);

            var c = block[i];
            if (c.If is { } cond && !Compare(cond.OpN, Read(st, cond.Lhs), Read(st, cond.Rhs)))
            {
                i++;
                continue;
            }
            if (c.Set is { } set)
            {
                if (set.OpN == 8) st.Notes.Add("rnd used - result is not deterministic");
                ApplySet(st, set);
            }
            foreach (var s in c.SystemSets)
            {
                if (s.Sprm is int sp)      st.Sprm[sp] = Read(st, s.Src);
                else if (s.Gprm is int gp) st.G[gp & 0x0F] = Read(st, s.Src);
            }

            switch (c.Op)
            {
                case "nop":
                case "other":   // a set with no link part falls through to the next line
                    i++;
                    break;
                case "Goto":
                    i = Math.Max(0, (c.Line ?? 0) - 1);
                    break;
                case "SetTmpPML":
                    st.Notes.Add($"SetTmpPML {c.Level} ignored");
                    i = Math.Max(0, (c.Line ?? 0) - 1);
                    break;
                case "Break":
                    return null;
                default:
                    return new Transfer(c, i + 1);
            }
            if (c.Op is "Goto" or "SetTmpPML" && steps > MaxSteps)
                return new Transfer(null, i + 1);
        }
        return null;
    }

    // ── Chain walk ───────────────────────────────────────────────────

    public ResolvedTarget Resolve(string domain, string lang, int pgcn, VmCommand button)
    {
        var st  = new State();
        var res = Follow(st, domain, lang, pgcn, new[] { button }, "button", 1, new HashSet<string>(), 0);

        res.Trace = st.Trace;
        res.Notes = st.Notes;
        if (st.SprmSeen.Count > 0)
            res.Notes.Add("read system registers " +
                          string.Join(", ", st.SprmSeen.Select(n => $"s[{n}]")) +
                          " - assumed player defaults");
        return res;
    }

    private ResolvedTarget Follow(
        State st, string dom, string lang, int pgcn, IReadOnlyList<VmCommand> block,
        string kind, int start, HashSet<string> seen, int depth)
    {
        if (depth > MaxDepth) return Unresolved("depth limit");

        var key = $"{dom}|{pgcn}|{kind}|{start}";
        if (seen.Contains(key))
            return new ResolvedTarget { Kind = "loop", Why = $"revisited {Where(dom, pgcn)} {kind}" };
        seen = new HashSet<string>(seen) { key };

        var t = RunBlock(st, block, start);
        if (t == null)
        {
            st.Trace.Add($"{Where(dom, pgcn)}.{kind} falls through");
            return Menu(dom, pgcn, why: "block ended without a link");
        }

        var label = t.Command == null
            ? "steplimit"
            : (t.Command.Text.Length > 0 ? t.Command.Text : t.Command.Op);
        st.Trace.Add($"{Where(dom, pgcn)}.{kind}:{t.Line} {label}");

        if (t.StepLimit) return Unresolved("command step limit");
        return Act(st, dom, lang, pgcn, t.Command!, seen, depth);
    }

    // Enter a PGC: run its pre-commands.
    private ResolvedTarget Enter(State st, string dom, string lang, int pgcn, HashSet<string> seen, int depth)
    {
        var p = Pgc(dom, lang, pgcn);
        if (p == null) return Unresolved($"no pgc {pgcn} in {DomName(dom)}");

        var pre = p.Commands.Pre;
        if (pre.Count == 0) return Settle(dom, pgcn, p);
        return Follow(st, dom, lang, pgcn, pre, "pre", 1, seen, depth + 1);
    }

    // A PGC with no further command: a menu screen, or a dead end.
    private ResolvedTarget Settle(string dom, int pgcn, IfoMenuPgc p) =>
        p.Cells.Count > 0
            ? Menu(dom, pgcn)
            : Unresolved($"pgc {pgcn} has no cells and no commands");

    private ResolvedTarget Act(State st, string dom, string lang, int pgcn, VmCommand c, HashSet<string> seen, int depth)
    {
        int? vts = _domains.TryGetValue(dom, out var d) ? d.Vts : null;

        switch (c.Op)
        {
            case "JumpTT":
                return TitleResult(c.Title ?? 0, null);

            case "JumpVTS_TT":
            case "JumpVTS_PTT":
            {
                var t = GlobalTitle(vts, c.VtsTitle ?? 0);
                if (t == null) return Unresolved($"{c.Op} {c.VtsTitle} has no entry in TT_SRPT");
                return TitleResult(t.Number, c.Chapter);
            }

            case "LinkPGCN":
                return Enter(st, dom, lang, c.Pgcn ?? 0, seen, depth);

            case "LinkPTT":
            case "LinkPGN":
            case "LinkCN":
                return Pgc(dom, lang, pgcn) != null
                    ? Menu(dom, pgcn, button: c.Button is > 0 ? c.Button : null)
                    : Unresolved("link inside a missing pgc");

            case "linksub":
                return LinkSub(st, dom, lang, pgcn, c, seen, depth);

            case "JumpSS":
            case "CallSS":
                return JumpSs(st, dom, lang, c, seen, depth);

            case "Exit":
                return new ResolvedTarget { Kind = "exit" };

            default:
                return Unresolved($"unhandled {c.Op}");
        }
    }

    private ResolvedTarget LinkSub(State st, string dom, string lang, int pgcn, VmCommand c, HashSet<string> seen, int depth)
    {
        var sub = c.Sub;
        var p   = Pgc(dom, lang, pgcn);

        switch (sub)
        {
            case "LinkTailPGC":
            {
                var post = p?.Commands.Post ?? Array.Empty<VmCommand>();
                if (post.Count == 0) return Menu(dom, pgcn, why: "LinkTailPGC with no post-commands");
                return Follow(st, dom, lang, pgcn, post, "post", 1, seen, depth + 1);
            }
            case "LinkGoUpPGC" when p is { GoUpPgc: > 0 }:
                return Enter(st, dom, lang, p.GoUpPgc, seen, depth);
            case "LinkNextPGC" when p is { NextPgc: > 0 }:
                return Enter(st, dom, lang, p.NextPgc, seen, depth);
            case "LinkPrevPGC" when p is { PrevPgc: > 0 }:
                return Enter(st, dom, lang, p.PrevPgc, seen, depth);
            case "RSM":
                return new ResolvedTarget { Kind = "resume", Why = "RSM - target depends on saved playback state" };
            case "LinkTopPGC": case "LinkTopC":  case "LinkTopPG": case "LinkNextC":
            case "LinkPrevC":  case "LinkNextPG": case "LinkPrevPG": case "LinkNoLink":
                return Menu(dom, pgcn, why: sub);
            default:
                return Unresolved($"unhandled linksub {sub}");
        }
    }

    private ResolvedTarget JumpSs(State st, string dom, string lang, VmCommand c, HashSet<string> seen, int depth)
    {
        switch (c.Target)
        {
            case "VMGM" when c.Pgcn is > 0:
                return Enter(st, "vmg", lang, c.Pgcn.Value, seen, depth);

            case "VMGM":
            {
                var pgcn = EntryPgc("vmg", lang, c.Menu);
                if (pgcn == null) return Unresolved($"no VMGM entry pgc for menu {NoneOr(c.Menu)}");
                return Enter(st, "vmg", lang, pgcn.Value, seen, depth);
            }

            case "VTSM":
            {
                int? vts = c.Vts is > 0 ? c.Vts : (_domains.TryGetValue(dom, out var d) ? d.Vts : null);
                var ndom = $"vts:{NoneOr(vts)}";
                if (c.VtsTitle is > 0) st.Sprm[5] = c.VtsTitle.Value;
                var pgcn = EntryPgc(ndom, lang, c.Menu);
                if (pgcn == null) return Unresolved($"no VTS {NoneOr(vts)} entry pgc for menu {NoneOr(c.Menu)}");
                return Enter(st, ndom, lang, pgcn.Value, seen, depth);
            }

            case "FP":
                return Unresolved("jumps to the first-play pgc");

            default:
                return Unresolved($"unhandled {c.Op}");
        }
    }

    // ── Lookups and results ──────────────────────────────────────────

    private IfoMenuPgc? Pgc(string dom, string lang, int pgcn) =>
        _domains.TryGetValue(dom, out var d) && d.Table(lang).TryGetValue(pgcn, out var p) ? p : null;

    private int? EntryPgc(string dom, string lang, int? menuId)
    {
        if (!_domains.TryGetValue(dom, out var d)) return null;
        foreach (var (pgcn, p) in d.Table(lang).OrderBy(kv => kv.Key).Select(kv => (kv.Key, kv.Value)))
            if (p.IsEntry && p.MenuId == menuId) return pgcn;
        return null;
    }

    private IfoTitle? GlobalTitle(int? vts, int vtsTitle) =>
        _titles.FirstOrDefault(t => t.Vts == vts && t.VtsTitle == vtsTitle);

    private ResolvedTarget TitleResult(int title, int? chapter)
    {
        var r = new ResolvedTarget { Kind = "title", Title = title, Chapter = chapter };
        var t = _titles.FirstOrDefault(x => x.Number == title);
        if (t != null)
        {
            r.Vts      = t.Vts;
            r.VtsTitle = t.VtsTitle;
            r.Chapters = t.Chapters;
        }
        return r;
    }

    private static ResolvedTarget Menu(string dom, int pgcn, string? why = null, int? button = null) =>
        new() { Kind = "menu", MenuPgc = pgcn, Domain = DomName(dom), Why = why, Button = button };

    private static ResolvedTarget Unresolved(string why) => new() { Kind = "unresolved", Why = why };

    private static string DomName(string dom) => dom == "vmg" ? "VMGM" : $"VTS {dom["vts:".Length..]}";

    private static string Where(string dom, int pgcn) => $"{DomName(dom)} pgc{pgcn}";

    // Python formats a missing value as "None"; the oracle's messages do.
    private static string NoneOr(int? v) => v?.ToString() ?? "None";
}
