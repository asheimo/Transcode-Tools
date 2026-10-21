// ============================================================
// TitleCoverage.cs
// ------------------------------------------------------------
// Fills each title's NamedBy (every menu button whose chain plays that
// title: each title in the button's Played list, or its Title when
// nothing was played) and ReachedFrom (every PGC command that jumps
// straight to it: JumpTT, JumpVTS_TT, JumpVTS_PTT). A title no button
// reaches still says where it is played from.
//
// A port of the oracle's title_coverage (tools\oracle\dvdmenumap.py).
// The record's NamedBy also carries the cell and button set (09-18
// record shape); the oracle keeps only IFO, PGC and button.
//
// NamedBy is the raw "this button's chain plays it". The menu screens
// shown for a title come from NamingButtons instead, which keeps only the
// buttons that play that title and nothing else worth naming:
//   - An item is a title some button's chain plays first, whose
//     PlaySeconds is not 0. Title 7 on SB30NNW1, the 0-second entry Play
//     and Fast Play jump to, is a switch in the disc's program, not an
//     item; the warnings, logos and end cards played on the way are never
//     a button's first stop, so they are not items either. A record from
//     before PlaySeconds was read has none, and its titles all count.
//   - A title MakeMKV skipped as a copy (SameAs) counts as the title it
//     copies.
//   - A button names a title only when its chain plays exactly one item:
//     Play, which runs clips, the film and an end card, names the film;
//     Fast Play, which runs the film and then the extras, names nothing.
//
// Pure: runs after the resolver, over the record and the menu PGCs.
// ============================================================

namespace TranscodeTools;

public static class TitleCoverage
{
    // Which buttons name each title, and which PGC commands jump straight to
    // it, so a title no button reaches still says where it is played from.
    // A port of the oracle's title_coverage.
    public static void Apply(DiscRecord record, IEnumerable<(string IfoFileName, IReadOnlyList<IfoMenuPgc> Pgcs)> menus)
    {
        var byNumber = record.Titles.ToDictionary(t => t.Number);

        foreach (var screen in record.Screens)
            foreach (var set in screen.ButtonSets)
                foreach (var button in set.Buttons)
                {
                    if (button.Resolved is not { Kind: "title", Title: int first } resolved) continue;

                    var played = resolved.Played.Count > 0 ? resolved.Played.Distinct() : new[] { first };
                    foreach (var n in played)
                        if (byNumber.TryGetValue(n, out var title))
                            title.NamedBy.Add(new NamedBySource
                            {
                                Ifo = screen.Ifo, Pgc = screen.Pgc, Cell = screen.Cell,
                                ButtonSet = set.Number, Button = button.Number,
                            });
                }

        var jumps = new Dictionary<int, SortedSet<string>>();
        foreach (var (fileName, pgcs) in menus)
        {
            var domain = Resolver.DomainKey(fileName);
            int? vts   = domain == "vmg" ? null : int.Parse(domain["vts:".Length..]);
            var where  = vts == null ? "VMGM" : $"VTS {vts}";

            foreach (var pgc in pgcs)
                foreach (var (which, block) in new[] { ("pre", pgc.Commands.Pre), ("post", pgc.Commands.Post), ("cell", pgc.Commands.Cell) })
                    foreach (var c in block)
                    {
                        int? t = c.Op switch
                        {
                            "JumpTT" => c.Title,
                            "JumpVTS_TT" or "JumpVTS_PTT" => record.Titles
                                .FirstOrDefault(x => x.Vts == vts && x.VtsTitle == c.VtsTitle)?.Number,
                            _ => null,
                        };
                        if (t is int n and > 0)
                        {
                            if (!jumps.TryGetValue(n, out var list)) jumps[n] = list = new SortedSet<string>(StringComparer.Ordinal);
                            list.Add($"{where} pgc{pgc.Pgcn} {which}");
                        }
                    }
        }

        foreach (var title in record.Titles)
            title.ReachedFrom = jumps.TryGetValue(title.Number, out var list) ? list.ToList() : new List<string>();
    }

    // The buttons whose chain plays `title` as its one item, in the order
    // the screens were found. Run when a title's screens are shown rather
    // than at Analyse, because SameAs only exists after MakeMKV's info run.
    public static List<NamedBySource> NamingButtons(DiscRecord disc, TitleRecord title)
    {
        var byNumber = disc.Titles.ToDictionary(t => t.Number);
        int Canonical(int n) => byNumber.TryGetValue(n, out var t) && t.SameAs is int kept ? kept : n;

        var buttons = disc.Screens
            .SelectMany(s => s.ButtonSets.SelectMany(set => set.Buttons.Select(b => (Screen: s, Set: set, Button: b))))
            .Where(x => x.Button.Resolved is { Kind: "title", Title: int })
            .Select(x => (x.Screen, x.Set, x.Button, Played: Played(x.Button.Resolved!)))
            .ToList();

        var items = buttons
            .Select(x => x.Played[0])
            .Where(n => byNumber.TryGetValue(n, out var t) && t.PlaySeconds != 0)
            .Select(Canonical)
            .ToHashSet();

        var target = Canonical(title.Number);
        return buttons
            .Where(x =>
            {
                var played = x.Played.Select(Canonical).Where(items.Contains).Distinct().ToList();
                return played.Count == 1 && played[0] == target;
            })
            .Select(x => new NamedBySource
            {
                Ifo = x.Screen.Ifo, Pgc = x.Screen.Pgc, Cell = x.Screen.Cell,
                ButtonSet = x.Set.Number, Button = x.Button.Number,
            })
            .ToList();
    }

    // A record saved before Played existed has only Title.
    private static List<int> Played(ResolvedTarget resolved) =>
        resolved.Played.Count > 0 ? resolved.Played : new List<int> { resolved.Title!.Value };
}
