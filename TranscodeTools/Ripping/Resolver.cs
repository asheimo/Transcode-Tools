// ============================================================
// Resolver.cs
// ------------------------------------------------------------
// Follows a menu button's command through the PGC command chains to
// where it really ends up: a title (and chapter), a menu screen, a
// resume, an exit, a loop, or "unresolved" with the reason.
//
// Started as a straight port of the oracle's Resolver class
// (tools\oracle\dvdmenumap.py). It executes the real pre / post /
// cell command blocks on a 16-register machine, because a disc
// decides the target by comparing registers the button set. On
// THE_CENTENNIAL, g[14] is the title selector and VTS 1 PGC 8 is
// the dispatcher every path funnels through.
//
// Two things go past the oracle, so its output no longer matches
// for buttons they touch:
//   - Registers start where the disc's own start-up leaves them. The
//     first-play chain is run once, the way a player runs it when
//     the disc goes in, until it reaches a menu screen; every button
//     then starts from a copy of those registers. A start-up that
//     reaches no menu leaves the player defaults, and StartupTrace
//     says why. The highlighted-button register (s[8]) is set to the
//     button being pressed.
//   - A menu PGC is only a stopping point when one of its cells has
//     buttons (the scan found them). One without buttons -- a warning,
//     a logo, a transition -- is played: its cells, then its post
//     commands, on to wherever they lead.
//   - A title is played rather than taken as the end. Its pre
//     commands, cell commands and post commands run, including a
//     CallSS out to a menu and the RSM back, until the program
//     settles on a menu screen or playback ends. Every title played
//     is listed in Played, in order, and TitleCoverage names each of
//     them by the button: a Play button that runs warnings and logos,
//     then the film, then an end card, names all of them. No rule
//     picks one; Title is the first played, where the button's own
//     chain went before playback carried it on.
//
// System registers start at the oracle's player defaults; a chain
// that reads one gets a note saying so. rnd is not executed (it is
// not deterministic) and SetTmpPML is ignored; both leave notes.
//
// Pure: parsed IFOs in, a result out. No I/O.
// ============================================================

using System.IO;

namespace TranscodeTools;

public sealed class Resolver
{
    private const int MaxSteps = 400;

    // Following titles crosses many program chains: a Play button can
    // run several short titles, each through a menu and back.
    private const int MaxDepth = 200;

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

    // HL_BTNN holds the highlighted button times 1024.
    private const int HighlightUnit = 0x400;

    // A cell's still time that holds until the viewer presses something.
    private const int InfiniteStill = 0xFF;

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

    // One title set's title program chains and the chapters of its titles.
    private sealed record TitleSet(
        Dictionary<int, IfoPgc> Pgcs,
        IReadOnlyList<IReadOnlyList<IfoChapter>> Chapters);

    private readonly Dictionary<string, Domain> _domains   = new();
    private readonly Dictionary<int, TitleSet>  _titleSets = new();
    private readonly IReadOnlyList<IfoTitle>    _titles;
    private readonly IfoPgc?                    _firstPlay;

    // Menu PGCs with at least one cell that has buttons, by domain,
    // language and PGC number. Only these are menu screens to stop at.
    private readonly HashSet<(string Dom, string Lang, int Pgcn)> _interactive = new();

    // The registers the start-up left, or null to start from the player
    // defaults.
    private readonly State? _startup;

    // How the start-up went, for the disc record.
    public IReadOnlyList<string> StartupTrace { get; }

    // `screens` are the disc record's menu screens after the button scan:
    // they say which menu PGCs have buttons.
    public Resolver(IEnumerable<Ifo> ifos, IReadOnlyList<IfoTitle> titles, IEnumerable<ScreenRecord> screens)
    {
        _titles = titles;
        foreach (var screen in screens)
            if (screen.ButtonSets.Any(set => set.Buttons.Count > 0))
                _interactive.Add((DomainKey(screen.Ifo), screen.Lang, screen.Pgc));

        foreach (var ifo in ifos)
        {
            var key = DomainKey(ifo.FileName);
            if (!_domains.TryGetValue(key, out var domain))
                _domains[key] = domain = new Domain { Vts = VtsNumber(ifo.FileName) };

            foreach (var pgc in ifo.MenuPgcs())
            {
                var entry = domain.Languages.FindIndex(x => x.Lang == pgc.Lang);
                if (entry < 0)
                {
                    domain.Languages.Add((pgc.Lang, new Dictionary<int, IfoMenuPgc>()));
                    entry = domain.Languages.Count - 1;
                }
                domain.Languages[entry].Pgcs.TryAdd(pgc.Pgcn, pgc);
            }

            if (ifo.Kind == IfoKind.Vts && domain.Vts is int vts)
                _titleSets[vts] = new TitleSet(ifo.TitlePgcs().ToDictionary(p => p.Pgcn), ifo.Chapters());
            else if (ifo.Kind == IfoKind.Vmg)
                _firstPlay = ifo.FirstPlayPgc();
        }

        (_startup, StartupTrace) = RunStartup();
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

    // Domains walked: "vmg" and "vts:N" are menus, "tt:N" is title set N's
    // titles, "fp" is the first-play chain. Titles and first play are
    // played (pre, cells, post); menus settle on their screens.
    private static bool IsPlayed(string dom) => dom == "fp" || dom.StartsWith("tt:", StringComparison.Ordinal);

    private static int? VtsOf(string dom)
    {
        var colon = dom.IndexOf(':');
        return colon > 0 && int.TryParse(dom.AsSpan(colon + 1), out var n) ? n : null;
    }

    // ── Machine state ────────────────────────────────────────────────

    private sealed record ResumePoint(string Dom, int Pgcn, int Cell);

    private sealed class State
    {
        public readonly int[]                 G;
        public readonly Dictionary<int, int>  Sprm;
        public readonly List<int>             SprmSeen = new();
        public readonly List<string>          Notes    = new();
        public readonly List<string>          Trace    = new();

        // Saved by CallSS out of a title, used by RSM.
        public ResumePoint? Resume;

        // The cell being played, for a CallSS with no resume cell.
        public int Cell;

        // Every title played, in order, and the chapter the first one was
        // started at when a chapter jump started it.
        public readonly List<int> Played = new();
        public int? FirstChapter;
        public int? NextChapter;

        public State()
        {
            G    = new int[16];
            Sprm = new Dictionary<int, int>(SprmDefaults);
        }

        // A fresh walk from the registers another one left.
        public State(State from)
        {
            G    = (int[])from.G.Clone();
            Sprm = new Dictionary<int, int>(from.Sprm);
        }
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

    private static string Label(Transfer t) =>
        t.Command == null ? "steplimit" : (t.Command.Text.Length > 0 ? t.Command.Text : t.Command.Op);

    // A link that does not leave: LinkNoLink, which only carries a button
    // highlight. In a title it lets playback carry on.
    private static bool IsNoLink(VmCommand c) => c is { Op: "linksub", Sub: "LinkNoLink" };

    // Part of a walk's loop key: the same place reached again with the same
    // registers is a loop; with different registers it is a new visit, as a
    // dispatcher every title returns through is.
    private static string Regs(State st) => string.Join(",", st.G);

    // ── Start-up ─────────────────────────────────────────────────────

    // Runs the first-play chain until the disc settles on a menu screen.
    // Its registers are kept only when it gets there.
    private (State?, IReadOnlyList<string>) RunStartup()
    {
        if (_firstPlay == null)
            return (null, new[] { "no first-play pgc - buttons start from player defaults" });

        var st  = new State();
        var end = Play(st, "fp", "", 1, 1, runPre: true, new HashSet<string>(), 0);

        var trace = st.Trace.ToList();
        if (st.Played.Count > 0)
            trace.Add($"titles played: {string.Join(", ", st.Played)}");

        if (end.Kind != "menu")
        {
            trace.Add($"start-up did not reach a menu ({end.Kind}{(end.Why is { } w ? $": {w}" : "")})" +
                      " - buttons start from player defaults");
            return (null, trace);
        }

        trace.Add($"start-up reached {end.Domain} pgc{end.MenuPgc}; " +
                  $"buttons start from g = {string.Join(" ", st.G.Select(v => $"0x{v:x}"))}");
        return (st, trace);
    }

    // ── Chain walk ───────────────────────────────────────────────────

    // `buttonNumber` is the button being pressed, which a disc can read
    // back through s[8].
    public ResolvedTarget Resolve(string domain, string lang, int pgcn, int buttonNumber, VmCommand button)
    {
        var st = _startup != null ? new State(_startup) : new State();
        st.Sprm[8] = buttonNumber * HighlightUnit;

        var end = Follow(st, domain, lang, pgcn, new[] { button }, "button", 1, new HashSet<string>(), 0);

        ResolvedTarget res;
        if (st.Played.Count == 0)
        {
            res = end;
        }
        else
        {
            // Title is the first title played and Played lists them all.
            // How the walk ended is in the trace, and in Why when it was
            // not a return to a menu or the end of playback.
            res = TitleResult(st.Played[0], st.FirstChapter);
            res.Played = st.Played.ToList();
            if (end.Kind is not ("menu" or "exit"))
                res.Why = $"after title {st.Played[^1]}: {end.Kind}{(end.Why is { } w ? $" - {w}" : "")}";
        }

        res.Trace = st.Trace;
        res.Notes = st.Notes;
        if (_startup != null)
            res.Notes.Add("registers start where the disc's start-up left them");
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

        var key = $"{dom}|{pgcn}|{kind}|{start}|{Regs(st)}";
        if (seen.Contains(key))
            return new ResolvedTarget { Kind = "loop", Why = $"revisited {Where(dom, pgcn)} {kind}" };
        seen = new HashSet<string>(seen) { key };

        var t = RunBlock(st, block, start);
        if (t == null)
        {
            st.Trace.Add($"{Where(dom, pgcn)}.{kind} falls through");
            return Menu(dom, pgcn, why: "block ended without a link");
        }

        st.Trace.Add($"{Where(dom, pgcn)}.{kind}:{t.Line} {Label(t)}");

        if (t.StepLimit) return Unresolved("command step limit");
        return Act(st, dom, lang, pgcn, t.Command!, seen, depth);
    }

    // Enter a menu PGC: run its pre-commands. If they don't link away, a
    // PGC with buttons is the menu screen the walk stops at; one without
    // is played through, cells then post-commands.
    private ResolvedTarget Enter(State st, string dom, string lang, int pgcn, HashSet<string> seen, int depth)
    {
        if (IsPlayed(dom)) return Play(st, dom, lang, pgcn, 1, runPre: true, seen, depth + 1);
        if (depth > MaxDepth) return Unresolved("depth limit");

        if (Pgc(dom, lang, pgcn) is not IfoMenuPgc p) return Unresolved($"no pgc {pgcn} in {DomName(dom)}");

        var key = $"{dom}|{pgcn}|pre|1|{Regs(st)}";
        if (seen.Contains(key))
            return new ResolvedTarget { Kind = "loop", Why = $"revisited {Where(dom, pgcn)} pre" };
        seen = new HashSet<string>(seen) { key };

        var pre = p.Commands.Pre;
        if (pre.Count > 0)
        {
            var t = RunBlock(st, pre, 1);
            if (t != null)
            {
                st.Trace.Add($"{Where(dom, pgcn)}.pre:{t.Line} {Label(t)}");
                if (t.StepLimit) return Unresolved("command step limit");
                return Act(st, dom, lang, pgcn, t.Command!, seen, depth + 1);
            }
            st.Trace.Add($"{Where(dom, pgcn)}.pre falls through");
        }

        if (_interactive.Contains((dom, p.Lang, pgcn)))
            return Menu(dom, pgcn, why: pre.Count > 0 ? "block ended without a link" : null);
        if (p.Cells.Count == 0 && p.Commands.Post.Count == 0)
            return pre.Count > 0
                ? Menu(dom, pgcn, why: "block ended without a link")
                : Unresolved($"pgc {pgcn} has no cells and no commands");

        st.Trace.Add($"{Where(dom, pgcn)} has no buttons, plays through");
        return PlayCells(st, dom, lang, pgcn, p, 1, seen, depth + 1);
    }

    // Plays a title or first-play PGC from `startCell`: the pre-commands
    // (when the PGC is entered rather than resumed), then each cell with
    // its cell command, then the post-commands. Post-commands that do not
    // link carry on to the next PGC, or end playback when there is none.
    private ResolvedTarget Play(
        State st, string dom, string lang, int pgcn, int startCell, bool runPre,
        HashSet<string> seen, int depth)
    {
        if (depth > MaxDepth) return Unresolved("depth limit");

        var p = Pgc(dom, lang, pgcn);
        if (p == null) return Unresolved($"no pgc {pgcn} in {DomName(dom)}");

        var key = $"{dom}|{pgcn}|play|{startCell}|{runPre}|{Regs(st)}";
        if (seen.Contains(key))
            return new ResolvedTarget { Kind = "loop", Why = $"revisited {Where(dom, pgcn)} cell {startCell}" };
        seen = new HashSet<string>(seen) { key };

        if (runPre) NotePlayed(st, dom, pgcn);

        if (runPre && p.Commands.Pre.Count > 0)
        {
            var t = RunBlock(st, p.Commands.Pre, 1);
            if (t != null)
            {
                st.Trace.Add($"{Where(dom, pgcn)}.pre:{t.Line} {Label(t)}");
                if (t.StepLimit) return Unresolved("command step limit");
                if (!IsNoLink(t.Command!)) return Act(st, dom, lang, pgcn, t.Command!, seen, depth);
            }
        }

        return PlayCells(st, dom, lang, pgcn, p, startCell, seen, depth);
    }

    // Plays a PGC's cells from `startCell`, each with its cell command,
    // then its post-commands. In a menu, a cell held as a still until the
    // viewer presses something is a screen to stop at even with no
    // buttons: the player waits there.
    private ResolvedTarget PlayCells(
        State st, string dom, string lang, int pgcn, IfoPgc p, int startCell, HashSet<string> seen, int depth)
    {
        for (int cell = Math.Max(1, startCell); cell <= p.Cells.Count; cell++)
        {
            st.Cell = cell;
            if (!IsPlayed(dom) && p.Cells[cell - 1].StillTime == InfiniteStill)
                return Menu(dom, pgcn, why: $"cell {cell} is a still with no buttons");

            int index = p.Cells[cell - 1].CellCommand;
            if (index <= 0 || index > p.Commands.Cell.Count) continue;

            var t = RunBlock(st, new[] { p.Commands.Cell[index - 1] }, 1);
            if (t == null) continue;

            st.Trace.Add($"{Where(dom, pgcn)}.cell{cell} {Label(t)}");
            var c = t.Command!;
            switch (c is { Op: "linksub" } ? c.Sub : null)
            {
                case "LinkNoLink":
                case "LinkNextC":
                    continue;
                case "LinkTopC":
                case "LinkTopPG":
                case "LinkTopPGC":
                case "LinkPrevC":
                case "LinkPrevPG":
                    return new ResolvedTarget { Kind = "loop", Why = $"{c.Sub} repeats {Where(dom, pgcn)}" };
                default:
                    return Act(st, dom, lang, pgcn, c, seen, depth);
            }
        }

        return PlayPost(st, dom, lang, pgcn, p, seen, depth);
    }

    private ResolvedTarget PlayPost(State st, string dom, string lang, int pgcn, IfoPgc p, HashSet<string> seen, int depth)
    {
        if (p.Commands.Post.Count > 0)
        {
            var t = RunBlock(st, p.Commands.Post, 1);
            if (t != null)
            {
                st.Trace.Add($"{Where(dom, pgcn)}.post:{t.Line} {Label(t)}");
                if (t.StepLimit) return Unresolved("command step limit");
                if (!IsNoLink(t.Command!)) return Act(st, dom, lang, pgcn, t.Command!, seen, depth);
            }
        }

        if (dom != "fp" && p.NextPgc > 0)
        {
            st.Trace.Add($"{Where(dom, pgcn)} ends, next pgc{p.NextPgc}");
            return Enter(st, dom, lang, p.NextPgc, seen, depth + 1);
        }

        st.Trace.Add($"{Where(dom, pgcn)} ends, playback stops");
        return new ResolvedTarget { Kind = "exit", Why = "playback ends" };
    }

    // Records the title a title PGC belongs to: the title with a chapter
    // that starts in it. A PGC no chapter starts in adds nothing.
    private void NotePlayed(State st, string dom, int pgcn)
    {
        if (!dom.StartsWith("tt:", StringComparison.Ordinal) || VtsOf(dom) is not int vts) return;
        if (!_titleSets.TryGetValue(vts, out var set)) return;

        int vtsTitle = 0;
        for (int i = 0; i < set.Chapters.Count && vtsTitle == 0; i++)
            if (set.Chapters[i].Any(ch => ch.Pgcn == pgcn)) vtsTitle = i + 1;
        if (vtsTitle == 0 || GlobalTitle(vts, vtsTitle) is not { } title) return;

        st.Sprm[4] = title.Number;
        st.Sprm[5] = vtsTitle;
        st.Sprm[6] = pgcn;

        if (st.Played.Count > 0 && st.Played[^1] == title.Number) return;
        if (st.Played.Count == 0) st.FirstChapter = st.NextChapter;
        st.Played.Add(title.Number);
        st.NextChapter = null;
        st.Trace.Add($"{Where(dom, pgcn)} plays title {title.Number}");
    }

    // Starts a title at a chapter: finds the chapter's PGC and program, and
    // plays from the program's first cell.
    private ResolvedTarget PlayTitle(
        State st, string lang, int number, int chapter, bool chapterJump, HashSet<string> seen, int depth)
    {
        var t = _titles.FirstOrDefault(x => x.Number == number);
        if (t == null) return Unresolved($"title {number} is not in TT_SRPT");
        if (!_titleSets.TryGetValue(t.Vts, out var set)) return Unresolved($"no title pgcs read for VTS {t.Vts}");
        if (t.VtsTitle < 1 || t.VtsTitle > set.Chapters.Count)
            return Unresolved($"VTS {t.Vts} has no chapter list for its title {t.VtsTitle}");

        var chapters = set.Chapters[t.VtsTitle - 1];
        if (chapter < 1 || chapter > chapters.Count)
            return Unresolved($"title {number} has no chapter {chapter}");

        var ch = chapters[chapter - 1];
        if (!set.Pgcs.TryGetValue(ch.Pgcn, out var pgc))
            return Unresolved($"title {number} chapter {chapter} names missing pgc {ch.Pgcn}");

        int startCell = ch.Program >= 1 && ch.Program <= pgc.ProgramMap.Count ? pgc.ProgramMap[ch.Program - 1] : 1;

        // A title already being played and jumped to again is a new start.
        if (st.Played.Count > 0 && st.Played[^1] == number) st.Played.RemoveAt(st.Played.Count - 1);
        st.NextChapter = chapterJump ? chapter : null;
        return Play(st, $"tt:{t.Vts}", lang, ch.Pgcn, startCell, runPre: true, seen, depth + 1);
    }

    private ResolvedTarget Act(State st, string dom, string lang, int pgcn, VmCommand c, HashSet<string> seen, int depth)
    {
        int? vts = VtsOf(dom);

        switch (c.Op)
        {
            case "JumpTT":
                return PlayTitle(st, lang, c.Title ?? 0, 1, chapterJump: false, seen, depth);

            case "JumpVTS_TT":
            case "JumpVTS_PTT":
            {
                var t = GlobalTitle(vts, c.VtsTitle ?? 0);
                if (t == null) return Unresolved($"{c.Op} {c.VtsTitle} has no entry in TT_SRPT");
                return c.Op == "JumpVTS_PTT"
                    ? PlayTitle(st, lang, t.Number, c.Chapter ?? 1, chapterJump: true, seen, depth)
                    : PlayTitle(st, lang, t.Number, 1, chapterJump: false, seen, depth);
            }

            case "LinkPGCN":
                return Enter(st, dom, lang, c.Pgcn ?? 0, seen, depth);

            case "LinkPTT" when IsPlayed(dom):
                return LinkChapter(st, dom, lang, c.Ptt ?? 0, seen, depth);

            case "LinkPGN" when IsPlayed(dom):
            {
                var p = Pgc(dom, lang, pgcn);
                int n = c.Pgn ?? 0;
                if (p == null || n < 1 || n > p.ProgramMap.Count) return Unresolved($"LinkPGN {n} outside {Where(dom, pgcn)}");
                return Play(st, dom, lang, pgcn, p.ProgramMap[n - 1], runPre: false, seen, depth + 1);
            }

            case "LinkCN" when IsPlayed(dom):
                return Play(st, dom, lang, pgcn, c.Cn ?? 1, runPre: false, seen, depth + 1);

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
                if (c.Op == "CallSS" && IsPlayed(dom))
                    st.Resume = new ResumePoint(dom, pgcn, c.RsmCell is > 0 ? c.RsmCell.Value : st.Cell);
                return JumpSs(st, dom, lang, c, seen, depth);

            case "Exit":
                return new ResolvedTarget { Kind = "exit" };

            default:
                return Unresolved($"unhandled {c.Op}");
        }
    }

    // LinkPTT inside a title: the chapter of the title being played.
    private ResolvedTarget LinkChapter(State st, string dom, string lang, int chapter, HashSet<string> seen, int depth)
    {
        if (st.Played.Count == 0) return Unresolved($"LinkPTT {chapter} with no title playing");
        return PlayTitle(st, lang, st.Played[^1], chapter, chapterJump: true, seen, depth);
    }

    private ResolvedTarget LinkSub(State st, string dom, string lang, int pgcn, VmCommand c, HashSet<string> seen, int depth)
    {
        var sub = c.Sub;
        var p   = Pgc(dom, lang, pgcn);

        switch (sub)
        {
            case "LinkTailPGC" when p != null && (IsPlayed(dom) || p is IfoMenuPgc m && !_interactive.Contains((dom, m.Lang, pgcn))):
                return PlayPost(st, dom, lang, pgcn, p, seen, depth + 1);
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
            case "RSM" when st.Resume is { } r:
                st.Trace.Add($"RSM back to {Where(r.Dom, r.Pgcn)} cell {r.Cell}");
                return Play(st, r.Dom, lang, r.Pgcn, r.Cell, runPre: false, seen, depth + 1);
            case "RSM":
                return new ResolvedTarget { Kind = "resume", Why = "RSM - target depends on saved playback state" };
            case "LinkTopPGC" or "LinkTopC" or "LinkTopPG" or "LinkPrevC" or "LinkPrevPG" when IsPlayed(dom):
                return new ResolvedTarget { Kind = "loop", Why = $"{sub} repeats {Where(dom, pgcn)}" };
            case "LinkNoLink" or "LinkNextC" or "LinkNextPG" when IsPlayed(dom):
                return Unresolved($"{sub} from a pre or post command of {Where(dom, pgcn)}");
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
                int? vts = c.Vts is > 0 ? c.Vts : VtsOf(dom);
                var ndom = $"vts:{NoneOr(vts)}";
                if (c.VtsTitle is > 0) st.Sprm[5] = c.VtsTitle.Value;
                var pgcn = EntryPgc(ndom, lang, c.Menu);
                if (pgcn == null) return Unresolved($"no VTS {NoneOr(vts)} entry pgc for menu {NoneOr(c.Menu)}");
                return Enter(st, ndom, lang, pgcn.Value, seen, depth);
            }

            case "FP" when _firstPlay != null:
                return Play(st, "fp", lang, 1, 1, runPre: true, seen, depth + 1);

            case "FP":
                return Unresolved("jumps to the first-play pgc, which the disc does not have");

            default:
                return Unresolved($"unhandled {c.Op}");
        }
    }

    // ── Lookups and results ──────────────────────────────────────────

    private IfoPgc? Pgc(string dom, string lang, int pgcn)
    {
        if (dom == "fp") return _firstPlay;
        if (dom.StartsWith("tt:", StringComparison.Ordinal))
            return VtsOf(dom) is int vts && _titleSets.TryGetValue(vts, out var set) && set.Pgcs.TryGetValue(pgcn, out var tp)
                ? tp : null;
        return _domains.TryGetValue(dom, out var d) && d.Table(lang).TryGetValue(pgcn, out var p) ? p : null;
    }

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

    private static string DomName(string dom) => dom switch
    {
        "vmg" => "VMGM",
        "fp"  => "FP",
        _ when dom.StartsWith("tt:", StringComparison.Ordinal) => $"VTS {dom["tt:".Length..]} title",
        _ => $"VTS {dom["vts:".Length..]}",
    };

    private static string Where(string dom, int pgcn) => $"{DomName(dom)} pgc{pgcn}";

    // Python formats a missing value as "None"; the oracle's messages do.
    private static string NoneOr(int? v) => v?.ToString() ?? "None";
}
