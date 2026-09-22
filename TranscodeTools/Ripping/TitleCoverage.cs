// ============================================================
// TitleCoverage.cs
// ------------------------------------------------------------
// Fills each title's NamedBy (every menu button whose resolved target
// is that title) and ReachedFrom (every PGC command that jumps
// straight to it: JumpTT, JumpVTS_TT, JumpVTS_PTT). A title no button
// reaches still says where it is played from.
//
// A port of the oracle's title_coverage (tools\oracle\dvdmenumap.py).
// The record's NamedBy also carries the cell and button set (09-18
// record shape); the oracle keeps only IFO, PGC and button.
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
                    if (button.Resolved is { Kind: "title", Title: int n } && byNumber.TryGetValue(n, out var title))
                        title.NamedBy.Add(new NamedBySource
                        {
                            Ifo = screen.Ifo, Pgc = screen.Pgc, Cell = screen.Cell,
                            ButtonSet = set.Number, Button = button.Number,
                        });

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

}
