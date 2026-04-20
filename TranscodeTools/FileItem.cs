// ============================================================
// FileItem.cs
// ------------------------------------------------------------
// Data models for the file browser TreeView.
//
// In WPF, TreeView nodes are bound to data objects rather than
// being created directly like WinForms TreeNode objects.
// Each class here represents one type of node in the tree.
// ============================================================

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace TranscodeTools;

// ── Base class for all tree nodes ────────────────────────────────────
// Contains the properties shared by both group nodes and file nodes.
// "abstract" means you can't create a TreeNode directly — you must
// use one of the subclasses below.
// In VB.NET: MustInherit Class TreeNode
public abstract class FileTreeNode : INotifyPropertyChanged
{
    // INotifyPropertyChanged tells WPF to update the UI when a property
    // changes — same pattern as the ObservableBase in Models.cs
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    // The display text shown in the TreeView
    private string _displayName = "";
    public string DisplayName
    {
        get => _displayName;
        set { _displayName = value; OnPropertyChanged(); }
    }

    // Background colour — used to highlight files that have settings saved.
    // Brushes.Transparent = no highlight (default)
    // Brushes.Green = settings file exists
    private Brush _background = Brushes.Transparent;
    public Brush Background
    {
        get => _background;
        set { _background = value; OnPropertyChanged(); }
    }
}

// ── Group node — e.g. "Featurette", "Deleted" ────────────────────────
// Contains child FileNodes. Shown with [+] expand arrow in the TreeView.
public class FileGroupNode : FileTreeNode
{
    // ObservableCollection so the TreeView updates if children are added/removed
    public ObservableCollection<FileLeafNode> Children { get; } = new();

    // The category name used for filename matching, e.g. "featurette"
    // Stored lowercase so we can match against split filenames without
    // caring about case.
    public string CategoryKey { get; set; } = "";
}

// ── Leaf node — an individual file ───────────────────────────────────
// Represents one .mkv file. May be at the root level (plain movie)
// or nested under a FileGroupNode (extra/featurette/etc).
public class FileLeafNode : FileTreeNode
{
    // The short display name (no extension, no category suffix)
    // e.g. "Casino Royale (2006)" or "Gettler Raises Bond's Suspicions"

    // The full filename including extension, used to build the full path
    // e.g. "Casino Royale (2006).mkv" or "Gettler Raises Bond's Suspicions-deleted.mkv"
    public string FileName { get; set; } = "";

    // The parent group key if this is an extra, or null if it's a root-level movie.
    // e.g. "deleted" for a file named "Something-deleted.mkv"
    // null for "Casino Royale (2006).mkv"
    public string? GroupKey { get; set; }

    // Set to true when the filename has no (YYYY) year pattern.
    // Drives the warning indicator in the FileLeafNode DataTemplate.
    private bool _hasYearWarning;
    public bool HasYearWarning
    {
        get => _hasYearWarning;
        set { _hasYearWarning = value; OnPropertyChanged(); }
    }

    // Set to true when ResolutionVerifyAlways is on and the resolution label
    // in the filename does not match what ffprobe reports.
    // Drives the orange warning indicator in the FileLeafNode DataTemplate.
    private bool _hasResolutionMismatch;
    public bool HasResolutionMismatch
    {
        get => _hasResolutionMismatch;
        set { _hasResolutionMismatch = value; OnPropertyChanged(); }
    }

    // Tooltip text for the resolution mismatch warning.
    // e.g. "Resolution mismatch: filename says 1080p, ffprobe reports 2160p"
    private string _resolutionMismatchTooltip = "";
    public string ResolutionMismatchTooltip
    {
        get => _resolutionMismatchTooltip;
        set { _resolutionMismatchTooltip = value; OnPropertyChanged(); }
    }
}

// ── FOLDER LIST NODES (left panel) ───────────────────────────────────
//
// The left panel uses a TreeView with two node types:
//   FolderNode  — a movie folder, selectable directly (no children)
//   ShowNode    — a TV show folder, expandable (has SeasonNode children)
//   SeasonNode  — a season within a show, selectable

// ── Movie folder node ─────────────────────────────────────────────────
// Represents a single movie folder. Selecting it populates the file tree.
// FolderPath is the folder name relative to the input directory.
public class FolderNode : FileTreeNode
{
    public string FolderPath { get; set; } = "";

    // Set to true when the folder name has no (YYYY) year pattern.
    // Drives the warning indicator in the FolderNode DataTemplate.
    private bool _hasYearWarning;
    public bool HasYearWarning
    {
        get => _hasYearWarning;
        set { _hasYearWarning = value; OnPropertyChanged(); }
    }
}

// ── TV show node ──────────────────────────────────────────────────────
// Represents a TV show folder. Expanding it reveals SeasonNode children.
// Not directly selectable — user must select a season.
public class ShowNode : FileTreeNode
{
    public ObservableCollection<SeasonNode> Children { get; } = new();
}

// ── Season node ───────────────────────────────────────────────────────
// Represents one season within a TV show. Selecting it populates the
// file tree. FolderPath is relative to input directory, e.g.
// "Star Trek- Picard\Season 01" — used directly as _selectedMovieFolder.
public class SeasonNode : FileTreeNode
{
    public string FolderPath { get; set; } = "";
}
