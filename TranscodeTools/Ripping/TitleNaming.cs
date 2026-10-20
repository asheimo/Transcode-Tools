// ============================================================
// TitleNaming.cs
// ------------------------------------------------------------
// The naming step, done row by row in the Rip view's title list.
//
// The list is built from the files on disk, not from the IFO: one
// NamingRow per .mkv in the disc's rip folder. The record supplies
// the rest. A file a disc title is bound to gets that title's number,
// runtime and menu screens; a file no title claims shows none of them.
// A disc title with no file gets no row, and stays on the record.
//
// Every row's Name is editable unless its Delete box is ticked. A
// name already applied shows in the Name cell and can be changed
// again. A file that has never been named starts empty: one with a
// MakeMKV name, or one the binder marked "_unmatched_".
//
// Every row also has a Type: Main title, or one of Plex's extras types
// (TitleType). An extra's file name is its name plus the Plex suffix,
// "Making Of" as a Featurette being "Making Of-featurette.mkv"; a main
// title's file name is its name alone. The Name cell shows the name
// without the suffix. The type is not stored separately: it is read back
// from the applied name, so a named file with no suffix is a Main title
// and a file never named has no type until one is chosen.
//
// One Apply (RipView.Apply_Click) applies every changed row:
//   - Problem() is checked on every row first, and any problem stops
//     the whole Apply: no type chosen, illegal characters, a trailing
//     space or dot, a name ending in a Plex suffix, a name another file
//     in the folder already has (unless that file is ticked for Delete,
//     since deletes run first), or a row both changed and ticked for
//     Delete.
//   - A main title with no (YYYY) and no S01E01 in its name is listed in
//     the confirm, since Plex matches on those. Extras are not listed.
//   - DeleteFile() then Rename() do the work. Each change is saved on
//     the disc record and added to the disc's naming.log, beside the
//     info.log and rip.log of its last run.
//
// A deleted file's row leaves the list. Its title stays on the record,
// marked Deleted. Marking the disc complete (RipView.Complete_Click) is
// refused while UnnamedFiles lists any file.
// ============================================================

using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace TranscodeTools;

// A row's Type: Main title, whose file name has no suffix, or one of Plex's
// extras types, whose file name ends in "-" and its suffix. The Label is
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

public sealed class NamingRow : INotifyPropertyChanged
{
    // The disc title bound to this file, or null when no title claims it.
    public TitleRecord? Title { get; }

    // The name last applied to the file: the title's name, or for a file
    // no title claims, its own name once it has lost the unmatched prefix.
    // Empty for a file that has never been named. Moved on by Applied()
    // after a rename, so a renamed row no longer counts as changed.
    private string _appliedName;

    // The type last applied, read from the applied name: null for a file
    // never named. Moved on by Applied() with the name.
    private TitleType? _appliedType;

    // `shownExtras` are the extras types offered in the Type dropdown.
    public NamingRow(string fileName, TitleRecord? title, IReadOnlyList<TitleType> shownExtras)
    {
        Title     = title;
        _fileName = fileName;

        var applied = title?.Name ?? TitleNaming.NameFromFile(fileName);
        if (applied.Length == 0)
        {
            _appliedName = "";
            _appliedType = null;
        }
        else if (TitleType.SuffixOf(applied) is { } extra)
        {
            _appliedName = applied[..^(extra.Suffix.Length + 1)];
            _appliedType = extra;
        }
        else
        {
            _appliedName = applied;
            _appliedType = TitleType.MainTitle;
        }
        _name = _appliedName;
        _type = _appliedType;

        // A type already on the file stays offered even when Preferences
        // hides it.
        TypeChoices = new[] { TitleType.MainTitle }
            .Concat(TitleType.Extras.Where(t => shownExtras.Contains(t) || t == _appliedType))
            .ToList();
        // A play-all repeats other titles, so it starts marked for Delete
        // until it has been named.
        _delete      = title is { Flags.PlayAll: true, Name.Length: 0 };
    }

    public string Number  => Title?.Number.ToString() ?? "";
    public string Runtime => Title?.Duration ?? "";

    // Shown as the row's tooltip; null shows none.
    public string? Note =>
        Title is null           ? "No title claims this file."
        : Title.Note.Length > 0 ? Title.Note
        :                         null;

    private string _fileName;
    public string FileName
    {
        get => _fileName;
        set { _fileName = value; Changed(); }
    }

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

    private bool _delete;
    public bool Delete
    {
        get => _delete;
        set { _delete = value; Changed(); }
    }

    public bool CanEdit => !_delete;

    // Called once the file on disk has been renamed: the new file name,
    // name and type become what the row compares against.
    public void Applied(string fileName, string name, TitleType type)
    {
        _appliedName = name;
        _appliedType = type;
        FileName     = fileName;
        Name         = name;
        Type         = type;
    }

    // A name has been typed that differs from the one already applied.
    public bool NameChanged => _name.Trim().Length > 0 && _name != _appliedName;

    // A type has been chosen that differs from the one already applied.
    public bool TypeChanged => _type != null && _type != _appliedType;

    // Something to apply: a ticked Delete, a changed name or a changed type.
    public bool HasChanges => _delete || NameChanged || TypeChanged;

    private void Changed([CallerMemberName] string? name = null)
    {
        OnPropertyChanged(name);
        OnPropertyChanged(nameof(CanEdit));
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

    // Shown on the status line and as the Name cell's tooltip when a row
    // ticked for Delete is clicked to edit.
    public const string UntickDeleteToEdit = "Untick Delete to edit the name";

    // One row per .mkv in the rip folder: the files disc titles are bound
    // to, in title order, then the files no title claims, in name order.
    // `shownExtras` are the extras types the Type dropdown offers.
    public static List<NamingRow> BuildRows(DiscRecord disc, IReadOnlyList<TitleType> shownExtras)
    {
        if (!Directory.Exists(disc.RipFolder)) return new List<NamingRow>();

        var byFile = disc.Titles
            .Where(t => t.FileName != null)
            .ToDictionary(t => t.FileName!, StringComparer.OrdinalIgnoreCase);

        return Directory.GetFiles(disc.RipFolder, "*.mkv")
            .Select(Path.GetFileName)
            .Select(f => new NamingRow(f!, byFile.GetValueOrDefault(f!), shownExtras))
            .OrderBy(r => r.Title is null)
            .ThenBy(r => r.Title?.Number ?? 0)
            .ThenBy(r => r.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // The name a file no title claims has been given: its own name without
    // ".mkv", or empty while it still carries the unmatched prefix.
    public static string NameFromFile(string fileName) =>
        fileName.StartsWith(RipBinder.UnmatchedPrefix, StringComparison.OrdinalIgnoreCase)
            ? ""
            : StripExtension(fileName);

    // Why the row can't be applied, or null when it can. `freed` holds the
    // files ticked for Delete in the same Apply: they are deleted before any
    // rename, so their names are free to take.
    public static string? Problem(NamingRow row, DiscRecord disc, IReadOnlySet<string> freed)
    {
        if (row.Delete && (row.NameChanged || row.TypeChanged))
            return "The row has a new name or type and is marked for Delete. Clear one of them.";

        if (row.Delete) return null;

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

        var target = TargetFile(row);
        if (!target.Equals(row.FileName, StringComparison.OrdinalIgnoreCase) &&
            !freed.Contains(target) &&
            File.Exists(Path.Combine(disc.RipFolder, target)))
            return $"A file named \"{target}\" is already in the rip folder.";

        return null;
    }

    // Plex matches movies on "Title (YYYY)" and episodes on "S01E01".
    public static bool LooksPlexReady(string name) =>
        MainWindow.YearPattern.IsMatch(name) || MainWindow.EpisodePattern.IsMatch(name);

    public static string StripExtension(string name) =>
        name.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    // The file name the row is renamed to: the name, then "-" and the
    // suffix for an extra, then ".mkv".
    public static string TargetFile(NamingRow row) =>
        StripExtension(row.Name) + (row.Type is { IsExtra: true } t ? "-" + t.Suffix : "") + ".mkv";

    // Renames the row's file to the typed name and type, and records it on
    // the record's file list, and on the title for a file one is bound to;
    // the title's Name is the file name without ".mkv", suffix included. A
    // rename that only changes letter case goes through a temporary name,
    // since Windows treats the two names as the same file.
    public static void Rename(NamingRow row, DiscRecord disc, string destination)
    {
        var target = TargetFile(row);
        var old    = row.FileName;
        var from   = Path.Combine(disc.RipFolder, old);
        var to     = Path.Combine(disc.RipFolder, target);

        if (target.Equals(old, StringComparison.OrdinalIgnoreCase) && target != old)
        {
            var temp = to + "_tmp_rename_";
            File.Move(from, temp);
            File.Move(temp, to);
        }
        else if (target != old)
        {
            File.Move(from, to);
        }

        row.Applied(target, StripExtension(row.Name), row.Type!);

        if (row.Title is { } title)
        {
            title.FileName = target;
            title.Name     = StripExtension(target);
        }
        if (RipFile(disc, old) is { } ripFile)
            ripFile.Name = target;
        DiscStore.Save(destination, disc);
        Log(disc, destination, $"renamed {old} -> {target}");
    }

    // Deletes the row's file. It stays on the record's file list, flagged
    // Deleted, and a title it was bound to stays on the record, marked
    // Deleted, with no file.
    public static void DeleteFile(NamingRow row, DiscRecord disc, string destination)
    {
        var file = row.FileName;
        File.Delete(Path.Combine(disc.RipFolder, file));

        if (row.Title is { } title)
        {
            title.FileName      = null;
            title.Flags.Deleted = true;
        }
        if (RipFile(disc, file) is { } ripFile)
            ripFile.Deleted = true;
        DiscStore.Save(destination, disc);
        Log(disc, destination, $"deleted {file}");
    }

    // The record's entry for a file the rip wrote, or null for a file it did
    // not write (one put in the folder by hand).
    private static RipFileRecord? RipFile(DiscRecord disc, string fileName) =>
        disc.RipFiles?.FirstOrDefault(f => !f.Deleted && f.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase));

    // The .mkv files left in the rip folder that have not been named: any
    // still carrying the unmatched prefix, and any a title is bound to whose
    // name has not been applied. Deleted files are gone, so never listed.
    public static List<string> UnnamedFiles(DiscRecord disc)
    {
        var unnamedTitleFiles = disc.Titles
            .Where(t => t.FileName != null && t.Name.Length == 0)
            .Select(t => t.FileName!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return Directory.GetFiles(disc.RipFolder, "*.mkv")
            .Select(f => Path.GetFileName(f))
            .Where(f => NameFromFile(f).Length == 0 || unnamedTitleFiles.Contains(f))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

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
