// ============================================================
// TitleNaming.cs
// ------------------------------------------------------------
// The naming step, done row by row in the Rip view's title list.
//
// The list is built from the disc's record: one NamingRow per title
// MakeMKV's info run joined (it has a rip name). A copy MakeMKV
// skipped gets no row of its own; a pick of it lands on the kept
// title's row. Nothing is on disk yet: only the titles named here are
// ripped, and a title left unnamed simply isn't.
//
// Every row has a Name and a Type: Main title, or one of Plex's extras
// types (TitleType). An extra's name is stored with its Plex suffix,
// "Making Of" as a Featurette being "Making Of-featurette", which is
// also the file name its rip will get; a main title's is its name
// alone. The Name cell shows the name without the suffix. The type is
// not stored separately: it is read back from the stored name, so a
// named title with no suffix is a Main title and a title never named
// has no type until one is chosen.
//
// One Apply (RipView.Apply_Click) saves every changed row to the
// record, the way Remux and Transcode save their settings file:
//   - Problem() is checked on every changed row and Duplicates() across
//     all the names first; any problem stops the whole Apply: no type
//     chosen, an empty name, illegal characters, a trailing space or
//     dot, a name ending in a Plex suffix, or the same name on two
//     titles.
//   - A main title with no (YYYY) and no S01E01 in its name is listed in
//     the confirm, since Plex matches on those. Extras are not listed.
//   - Each name saved is added to the disc's naming.log, beside the
//     info.log of its last run.
// ============================================================

using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace TranscodeTools;

// A row's Type: Main title, whose name has no suffix, or one of Plex's
// extras types, whose name ends in "-" and its suffix. The Label is
// what the Type dropdown shows.
public sealed record TitleType(string Label, string Suffix)
{
    public static readonly TitleType MainTitle = new("Main title", "");

    // Plex's extras types, in the order the dropdown and Preferences list
    // them. The suffixes are Plex's own and must match exactly.
    public static readonly IReadOnlyList<TitleType> Extras = new TitleType[]
    {
        new("Behind the Scenes", "behindthescenes"),
        new("Deleted Scene",     "deleted"),
        new("Featurette",        "featurette"),
        new("Interview",         "interview"),
        new("Scene",             "scene"),
        new("Short",             "short"),
        new("Trailer",           "trailer"),
        new("Other",             "other"),
    };

    public bool IsExtra => Suffix.Length > 0;

    // The extras types not hidden in Preferences, in list order.
    public static List<TitleType> ShownExtras(IEnumerable<string> hiddenSuffixes)
    {
        var hidden = hiddenSuffixes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Extras.Where(t => !hidden.Contains(t.Suffix)).ToList();
    }

    // The extras type a name ends in ("Making Of-featurette"), or null.
    public static TitleType? SuffixOf(string name) =>
        Extras.FirstOrDefault(t =>
            name.Length > t.Suffix.Length + 1 &&
            name.EndsWith("-" + t.Suffix, StringComparison.OrdinalIgnoreCase));

    public override string ToString() => Label;
}

// One title in the list.
public sealed class NamingRow : INotifyPropertyChanged
{
    public TitleRecord Title { get; }

    // The name and type last saved, read from the title's stored name:
    // empty and null for a title never named. Moved on by Applied().
    private string     _appliedName;
    private TitleType? _appliedType;

    // `shownExtras` are the extras types offered in the Type dropdown.
    public NamingRow(TitleRecord title, IReadOnlyList<TitleType> shownExtras)
    {
        Title = title;

        if (title.Name.Length == 0)
        {
            _appliedName = "";
            _appliedType = null;
        }
        else if (TitleType.SuffixOf(title.Name) is { } extra)
        {
            _appliedName = title.Name[..^(extra.Suffix.Length + 1)];
            _appliedType = extra;
        }
        else
        {
            _appliedName = title.Name;
            _appliedType = TitleType.MainTitle;
        }
        _name = _appliedName;
        _type = _appliedType;

        // A type already saved stays offered even when Preferences hides it.
        TypeChoices = new[] { TitleType.MainTitle }
            .Concat(TitleType.Extras.Where(t => shownExtras.Contains(t) || t == _appliedType))
            .ToList();
    }

    public string Number  => Title.Number.ToString();
    public string Runtime => Title.Duration;

    // Shown as the row's tooltip; null shows none.
    public string? Note => Title.Note.Length > 0 ? Title.Note : null;

    private string _name;
    public string Name
    {
        get => _name;
        set { _name = value; Changed(); }
    }

    // The Type dropdown's choices: Main title, then the shown extras types.
    public IReadOnlyList<TitleType> TypeChoices { get; }

    // Null until a type is chosen.
    private TitleType? _type;
    public TitleType? Type
    {
        get => _type;
        set { _type = value; Changed(); }
    }

    // Called once the name is saved: it becomes what the row compares against.
    public void Applied()
    {
        _appliedName = _name;
        _appliedType = _type;
        Changed(nameof(Name));
    }

    // A name has been typed that differs from the one saved.
    public bool NameChanged => _name.Trim().Length > 0 && _name != _appliedName;

    // A type has been chosen that differs from the one saved.
    public bool TypeChanged => _type != null && _type != _appliedType;

    public bool HasChanges => NameChanged || TypeChanged;

    private void Changed([CallerMemberName] string? name = null)
    {
        OnPropertyChanged(name);
        OnPropertyChanged(nameof(HasChanges));
        OnPropertyChanged(nameof(NameChanged));
        OnPropertyChanged(nameof(TypeChanged));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public static class TitleNaming
{
    public const string LogFileName = "naming.log";

    // One row per title MakeMKV's info run joined, in title order. Copies
    // MakeMKV skipped get none. `shownExtras` are the extras types the
    // Type dropdown offers.
    public static List<NamingRow> BuildRows(DiscRecord disc, IReadOnlyList<TitleType> shownExtras) =>
        disc.Titles
            .Where(t => t.RipName != null && t.SameAs == null)
            .OrderBy(t => t.Number)
            .Select(t => new NamingRow(t, shownExtras))
            .ToList();

    // Why the row can't be saved, or null when it can.
    public static string? Problem(NamingRow row)
    {
        var name = StripExtension(row.Name);
        if (name.Trim().Length == 0)
            return "The name is empty.";
        if (row.Type is null)
            return "No type is chosen.";
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return "The name contains a character Windows does not allow in a file name: \\ / : * ? \" < > |";
        if (name.EndsWith(' ') || name.EndsWith('.'))
            return "The name ends with a space or a dot, which Windows drops from file names.";
        if (TitleType.SuffixOf(name) is { } suffix)
            return $"The name ends in \"-{suffix.Suffix}\", which Plex reads as a type. Choose the type in the Type column instead.";
        return null;
    }

    // Titles that would end up with the same name, as one line per name.
    // A changed row counts with its typed name, any other with the name
    // already saved; titles never named are left out.
    public static List<string> Duplicates(IEnumerable<NamingRow> rows) =>
        rows.Select(r => (Row: r, Name: r.HasChanges ? TargetName(r) : r.Title.Name))
            .Where(x => x.Name.Length > 0)
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => $"Titles {string.Join(", ", g.Select(x => x.Row.Number))}: the same name, \"{g.Key}\".")
            .ToList();

    // Plex matches movies on "Title (YYYY)" and episodes on "S01E01".
    public static bool LooksPlexReady(string name) =>
        MainWindow.YearPattern.IsMatch(name) || MainWindow.EpisodePattern.IsMatch(name);

    public static string StripExtension(string name) =>
        name.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    // The name the title is saved under: the name, then "-" and the suffix
    // for an extra. Its rip will be this plus ".mkv".
    public static string TargetName(NamingRow row) =>
        StripExtension(row.Name) + (row.Type is { IsExtra: true } t ? "-" + t.Suffix : "");

    // Saves the row's name on its title. The caller saves the record.
    public static void Apply(NamingRow row, DiscRecord disc, string destination)
    {
        var old = row.Title.Name;
        row.Title.Name = TargetName(row);
        row.Applied();
        Log(disc, destination, old.Length == 0
            ? $"title {row.Number} named \"{row.Title.Name}\""
            : $"title {row.Number} renamed \"{old}\" -> \"{row.Title.Name}\"");
    }

    // The named titles with no ripped file yet.
    public static List<TitleRecord> NamedNotRipped(DiscRecord disc) =>
        disc.Titles.Where(t => t.Name.Length > 0 && t.FileName == null).ToList();

    // Adds a line to the disc's naming.log, in the log folder of its last
    // run. A record from before that folder was recorded gets a new run
    // folder, which is then kept on the record.
    public static void Log(DiscRecord disc, string destination, string line)
    {
        if (string.IsNullOrEmpty(disc.RunLogFolder))
        {
            disc.RunLogFolder = Path.GetDirectoryName(
                DiscStore.StageLogPath(destination, DateTime.Now, disc.Name, LogFileName))!;
            DiscStore.Save(destination, disc);
        }

        Directory.CreateDirectory(disc.RunLogFolder);
        File.AppendAllText(Path.Combine(disc.RunLogFolder, LogFileName),
                           $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {line}{Environment.NewLine}",
                           Encoding.UTF8);
    }
}
