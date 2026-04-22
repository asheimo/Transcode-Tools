// ============================================================
// RunRemux.xaml.cs
// ------------------------------------------------------------
// Code-behind for the Run processing window.
//
// Responsibilities:
//   - Populate the TreeView with folders (and optionally files)
//     from the input directory
//   - Process checked items when Start is clicked:
//       * Files with a .txt settings file → run the saved command
//       * Files without a settings file   → run Robocopy to copy
//   - Stream live process output to the log TextBox
//   - Cancel the running process when Cancel is clicked
// ============================================================

using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TranscodeTools;

public partial class RunRemux : Window
{
    // ── State passed in from MainWindow ──────────────────────────────
    private readonly string _inputDirectory;
    private readonly string _outputDirectory;
    private readonly bool   _isTranscodeMode;

    // ── Processing state ─────────────────────────────────────────────
    private Process?         _currentProcess;
    private bool             _cancelRequested;
    private bool             _isRunning;

    // Tracks which folder names had at least one file fail during a run.
    // Populated by ProcessFileAsync; read by the move worker.
    // Cleared at the start of each run so stale errors don't carry over.
    private readonly HashSet<string> _foldersWithErrors = new(StringComparer.OrdinalIgnoreCase);

    // ── Parallel move infrastructure ──────────────────────────────────
    // As each top-level folder's files all finish successfully, its name
    // is enqueued here and the move worker picks it up immediately —
    // moves run in parallel with ongoing processing of other folders.
    //
    // ConcurrentQueue is thread-safe for simultaneous enqueue (processing
    // loop) and dequeue (move worker) without needing a lock.
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _moveQueue = new();

    // The long-lived move worker task, started at the same time as the
    // processing loop and awaited after it finishes.
    private Task? _moveWorkerTask;

    // Signals the move worker that no more folders will be enqueued
    // (either the run completed normally or was cancelled).
    // The worker drains any remaining queue entries before exiting.
    private volatile bool _processingComplete;

    // Per-folder file counts — built before the loop starts.
    // _folderFileTotal: how many files were queued for each top-level folder.
    // _folderFileDone:  incremented after each ProcessFileAsync completes.
    // When Done == Total the folder is either queued for move or marked failed.
    private readonly Dictionary<string, int> _folderFileTotal = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _folderFileDone  = new(StringComparer.OrdinalIgnoreCase);

    // Maps top-level folder name → RunFolderNode so the move worker can
    // retrieve the TreeItem reference for colour updates. Built at run start.
    private readonly Dictionary<string, RunFolderNode> _folderNodes = new(StringComparer.OrdinalIgnoreCase);

    // ── Log file state ────────────────────────────────────────────────
    // Timestamp folder created once per run — shared across all files.
    // _currentLogPath tracks the log file for the file currently processing,
    // so View Log can open it after the run completes.
    // _lastRunLogPaths maps FolderName\FileName → log path for right-click.
    private string           _runTimestamp       = "";
    private string?          _currentLogPath;
    private readonly Dictionary<string, string> _lastRunLogPaths = new();

    // Suppresses SelectAll sync while PopulateFolderTree rebuilds the tree,
    // preventing spurious check/uncheck events from resetting SelectAllCheckBox.
    private bool             _suppressSelectAllSync;

    // ── Constructor ──────────────────────────────────────────────────
    // Receives the current state from MainWindow so the Run window
    // knows where to look for files and settings.
    public RunRemux(string inputDirectory, string outputDirectory, bool isTranscodeMode)
    {
        InitializeComponent();

        _inputDirectory  = inputDirectory;
        _outputDirectory = outputDirectory;
        _isTranscodeMode = isTranscodeMode;

        // Update window title to match mode
        Title = isTranscodeMode ? "Run Transcode" : "Run Remux";

        Closed += (_, _) => RunCompleted?.Invoke(this, EventArgs.Empty);
    }

    // Raised when the Run window closes. MainWindow subscribes to this
    // to trigger a folder tree refresh after a run.
    public event EventHandler? RunCompleted;

    // ── Window loaded ─────────────────────────────────────────────────
    private void RunRemux_Loaded(object sender, RoutedEventArgs e)
    {
        if (!AppSettings.Instance.WriteLogFiles)
            OutputLog.ContextMenu = null;
        PopulateFolderTree();
    }

    // ── Output folder completion check ────────────────────────────────
    // Called after PopulateFolderTree() to async-check which folders/files
    // already exist in the output directory. Appends a green ✓ to any
    // folder where every input .mkv has a matching output file, and to
    // each individual file that already exists (when Show Files is on).
    //
    // Runs entirely on a background thread — only the final UI updates
    // marshal back via Dispatcher. The window opens instantly; checkmarks
    // appear within a second or two even on network paths.
    //
    // Called with fire-and-forget (_ = CheckOutputFolderAsync()) so it
    // doesn't block the UI thread. A CancellationToken lets a subsequent
    // PopulateFolderTree() call (e.g. Show Files toggle) abort any
    // in-progress scan before starting a new one.
    private CancellationTokenSource? _outputCheckCts;

    private void StartOutputCheck()
    {
        // Cancel any previous scan that's still running
        _outputCheckCts?.Cancel();
        _outputCheckCts = new CancellationTokenSource();
        _ = CheckOutputFolderAsync(_outputCheckCts.Token);
    }

    private async Task CheckOutputFolderAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_outputDirectory) ||
            !Directory.Exists(_outputDirectory)) return;

        var showFiles = await Dispatcher.InvokeAsync(() => ShowFilesCheckBox.IsChecked == true);

        // Snapshot the tree nodes we need to check — on the UI thread,
        // before we go async. We collect (node, inputRelativePath) pairs.
        var folderChecks = new List<(TreeViewItem treeItem, string relPath, bool hasFileChildren)>();

        await Dispatcher.InvokeAsync(() =>
        {
            foreach (TreeViewItem topItem in FolderTree.Items)
            {
                if (topItem.Header is CheckBox)
                {
                    // Movie — single selectable node
                    if (topItem.Tag is RunFolderNode node)
                        folderChecks.Add((topItem, node.FolderName, topItem.Items.Count > 0 && showFiles));
                }
                else
                {
                    // TV show — iterate season children
                    foreach (TreeViewItem seasonItem in topItem.Items)
                    {
                        if (seasonItem.Tag is RunFolderNode sNode)
                            folderChecks.Add((seasonItem, sNode.FolderName, seasonItem.Items.Count > 0 && showFiles));
                    }
                }
            }
        });

        if (ct.IsCancellationRequested) return;

        // For each folder: check on a background thread, update UI after
        foreach (var (treeItem, relPath, hasFileChildren) in folderChecks)
        {
            if (ct.IsCancellationRequested) return;

            var inputFolder  = Path.Combine(_inputDirectory,  relPath);
            var outputFolder = Path.Combine(_outputDirectory, relPath);

            // ── Background work: collect file name sets ───────────────
            HashSet<string>? outputFiles = null;
            string[]?        inputMkvs   = null;

            await Task.Run(() =>
            {
                try
                {
                    inputMkvs = Directory.GetFiles(inputFolder, "*.mkv")
                        .Select(Path.GetFileName)
                        .Where(f => f != null)
                        .Select(f => f!)
                        .ToArray();
                }
                catch { inputMkvs = Array.Empty<string>(); }

                if (Directory.Exists(outputFolder))
                {
                    try
                    {
                        outputFiles = new HashSet<string>(
                            Directory.GetFiles(outputFolder, "*.mkv")
                                .Select(Path.GetFileName)
                                .Where(f => f != null)
                                .Select(f => f!),
                            StringComparer.OrdinalIgnoreCase);
                    }
                    catch { outputFiles = new HashSet<string>(); }
                }
                else
                {
                    outputFiles = new HashSet<string>();
                }
            }, ct);

            if (ct.IsCancellationRequested) return;
            if (inputMkvs == null || outputFiles == null) continue;

            var allPresent = inputMkvs.Length > 0 &&
                             inputMkvs.All(f => outputFiles.Contains(f));

            // ── Update folder checkbox label ──────────────────────────
            await Dispatcher.InvokeAsync(() =>
            {
                if (ct.IsCancellationRequested) return;
                SetFolderCheckmark(treeItem, allPresent);

                // ── Update individual file children (Show Files mode) ──
                if (!hasFileChildren) return;
                foreach (TreeViewItem fileItem in treeItem.Items)
                {
                    if (fileItem.Tag is RunFileNode fileNode)
                    {
                        var exists = outputFiles!.Contains(fileNode.FileName);
                        SetFileCheckmark(fileItem, exists);
                    }
                }
            });
        }
    }

    // Appends or removes the ✓ suffix on a folder's CheckBox content.
    // The CheckBox Content is a plain string — we swap it in place.
    private static void SetFolderCheckmark(TreeViewItem treeItem, bool complete)
    {
        if (treeItem.Header is not CheckBox cb) return;

        // Extract raw text regardless of whether Content is already a StackPanel
        var text = cb.Content is string s ? s
                 : cb.Content is StackPanel sp && sp.Children.Count > 0 &&
                   sp.Children[0] is TextBlock tb ? tb.Text
                 : (cb.Content as string) ?? "";

        if (text.EndsWith(" \u2713")) text = text[..^2];

        // Build a StackPanel so only the \u2713 is amber — folder name stays default colour
        if (complete)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(new TextBlock
            {
                Text       = text,
                Foreground = (Brush)Application.Current.FindResource("ForegroundColor")
            });
            panel.Children.Add(new TextBlock
            {
                Text       = " \u2713",
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFE08A"))
            });
            cb.Content = panel;
        }
        else
        {
            cb.Content    = text;
            cb.Foreground = (Brush)Application.Current.FindResource("ForegroundColor");
        }
    }

    // Appends or removes the amber \u2713 suffix on a file's CheckBox content.
    private static void SetFileCheckmark(TreeViewItem fileItem, bool exists)
    {
        if (fileItem.Header is not CheckBox cb) return;

        // Extract raw text regardless of whether Content is already a StackPanel
        var text = cb.Content is string s2 ? s2
                 : cb.Content is StackPanel sp2 && sp2.Children.Count > 0 &&
                   sp2.Children[0] is TextBlock tb2 ? tb2.Text
                 : (cb.Content as string) ?? "";

        if (text.EndsWith(" \u2713")) text = text[..^2];

        if (exists)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(new TextBlock
            {
                Text       = text,
                Foreground = (Brush)Application.Current.FindResource("ForegroundColor")
            });
            panel.Children.Add(new TextBlock
            {
                Text       = " \u2713",
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFE08A"))
            });
            cb.Content = panel;
        }
        else
        {
            cb.Content    = text;
            cb.Foreground = (Brush)Application.Current.FindResource("ForegroundColor");
        }
    }

    // ── Populate the TreeView ─────────────────────────────────────────
    // Movies appear as single checkbox nodes.
    // TV shows appear as expandable nodes with season children.
    // In show-files mode, each selectable node further expands to show
    // its .mkv files with green background if settings exist.
    private void PopulateFolderTree()
    {
        FolderTree.Items.Clear();

        if (string.IsNullOrWhiteSpace(_inputDirectory) ||
            !Directory.Exists(_inputDirectory)) return;

        var modeFolder = _isTranscodeMode ? "Transcode" : "Remux";
        var showFiles  = ShowFilesCheckBox.IsChecked == true;

        try
        {
            var folders = Directory.GetDirectories(_inputDirectory)
                .Select(Path.GetFileName)
                .Where(name => name != null &&
                               !name.Equals("Remux",     StringComparison.OrdinalIgnoreCase) &&
                               !name.Equals("Transcode", StringComparison.OrdinalIgnoreCase) &&
                               !name.Equals("Logs",      StringComparison.OrdinalIgnoreCase) &&
                               !name.Equals("Completed", StringComparison.OrdinalIgnoreCase))
                .OrderBy(name => name);

            foreach (var folderName in folders)
            {
                if (folderName == null) continue;

                var folderPath  = Path.Combine(_inputDirectory, folderName);
                var subFolders  = Directory.GetDirectories(folderPath);

                if (subFolders.Length > 0)
                {
                    // ── TV show: non-selectable header with season children ──
                    var showItem = new TreeViewItem
                    {
                        Header = new TextBlock
                        {
                            Text       = folderName,
                            FontSize   = 13,
                            Foreground = (System.Windows.Media.Brush)Application.Current
                                            .FindResource("ForegroundColor")
                        }
                    };

                    foreach (var seasonPath in subFolders.OrderBy(s => s))
                    {
                        var seasonName = Path.GetFileName(seasonPath);
                        if (seasonName == null) continue;

                        // FolderPath for TV is "Show\Season" relative to input
                        var relativePath = Path.Combine(folderName, seasonName);
                        var seasonNode   = new RunFolderNode { FolderName = relativePath };
                        var seasonItem   = BuildFolderTreeItem(
                            seasonNode, relativePath, seasonName, modeFolder, showFiles);

                        showItem.Items.Add(seasonItem);
                    }

                    FolderTree.Items.Add(showItem);
                }
                else
                {
                    // ── Movie: single selectable checkbox node ────────────
                    var folderNode = new RunFolderNode { FolderName = folderName };
                    var treeItem   = BuildFolderTreeItem(
                        folderNode, folderName, folderName, modeFolder, showFiles);
                    FolderTree.Items.Add(treeItem);
                }
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Error reading input directory: {ex.Message}");
        }

        // Async background check — appends ✓ to folders/files already in output.
        // Fire-and-forget; any previous scan is cancelled before starting a new one.
        StartOutputCheck();
    }

    // Builds a TreeViewItem for a selectable folder (movie or TV season).
    // folderRelativePath — path relative to input dir (e.g. "Show\Season 01")
    // displayName        — label shown on the checkbox (e.g. "Season 01")
    private TreeViewItem BuildFolderTreeItem(RunFolderNode node, string folderRelativePath,
                                              string displayName, string modeFolder, bool showFiles)
    {
        var checkBox = new CheckBox
        {
            Content    = displayName,
            FontSize   = 13,
            Foreground = (Brush)Application.Current.FindResource("ForegroundColor"),
            Tag        = node
        };
        checkBox.Checked   += FolderCheckBox_Checked;
        checkBox.Unchecked += FolderCheckBox_Unchecked;

        var treeItem = new TreeViewItem
        {
            Header = checkBox,
            Tag    = node
        };

        // Store the TreeViewItem on the node so the move worker can colour it
        // directly via Dispatcher without having to search the visual tree.
        node.TreeItem = treeItem;

        if (!showFiles) return treeItem;

        // ── File children (show files mode only) ─────────────────────
        var fullPath = Path.Combine(_inputDirectory, folderRelativePath);
        try
        {
            var files = Directory.GetFiles(fullPath, "*.mkv")
                .Select(Path.GetFileName)
                .Where(f => f != null)
                .OrderBy(f => f);

            foreach (var fileName in files)
            {
                if (fileName == null) continue;

                var nameNoExt    = Path.GetFileNameWithoutExtension(fileName);
                var settingsFile = Path.Combine(_inputDirectory, modeFolder,
                                       folderRelativePath, nameNoExt + ".txt");
                var hasSettings  = File.Exists(settingsFile);

                var fileNode = new RunFileNode
                {
                    FolderName  = folderRelativePath,
                    FileName    = fileName,
                    HasSettings = hasSettings
                };

                var fileCheckBox = new CheckBox
                {
                    Content    = nameNoExt,
                    FontSize   = 12,
                    Foreground = (Brush)Application.Current.FindResource("ForegroundColor"),
                    Background = hasSettings ? Brushes.Green : Brushes.White,
                    Tag        = fileNode
                };
                fileCheckBox.Checked   += FileCheckBox_Changed;
                fileCheckBox.Unchecked += FileCheckBox_Changed;

                var fileItem = new TreeViewItem
                {
                    Header = fileCheckBox,
                    Tag    = fileNode
                };

                treeItem.Items.Add(fileItem);
            }
        }
        catch { /* Skip folders we can't read */ }

        return treeItem;
    }

    // ── Folder checkbox cascade ───────────────────────────────────────
    // Checking a folder checks all its file children automatically.
    private void FolderCheckBox_Checked(object sender, RoutedEventArgs e)
    {
        SetChildCheckBoxes(sender, true);
        SyncSelectAllCheckBox();
    }

    private void FolderCheckBox_Unchecked(object sender, RoutedEventArgs e)
    {
        SetChildCheckBoxes(sender, false);
        SyncSelectAllCheckBox();
    }

    private void SetChildCheckBoxes(object sender, bool isChecked)
    {
        if (sender is not CheckBox cb) return;
        var treeItem = FindAncestor<TreeViewItem>(cb as DependencyObject);
        if (treeItem == null) return;

        foreach (TreeViewItem child in treeItem.Items)
        {
            if (child.Header is CheckBox childCb)
                childCb.IsChecked = isChecked;
        }
    }

    // ── Show files toggle ─────────────────────────────────────────────
    private void ShowFilesCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        PopulateFolderTree();
        SyncTestModeCheckBox();
    }

    // ── Select All checkbox ───────────────────────────────────────────
    // Checks or unchecks every selectable folder/season checkbox in the tree.
    // File children cascade automatically via FolderCheckBox_Checked/Unchecked.
    // _suppressSelectAllSync prevents the individual checkbox change events
    // from immediately re-evaluating and fighting back against this bulk set.
    private void SelectAll_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressSelectAllSync) return;
        var isChecked = SelectAllCheckBox.IsChecked == true;

        _suppressSelectAllSync = true;
        try
        {
            SetAllFolderCheckBoxes(FolderTree.Items, isChecked);
        }
        finally
        {
            _suppressSelectAllSync = false;
        }
    }

    // Prevents the user from landing on the indeterminate state via clicking.
    // IsThreeState="True" is needed so SyncSelectAllCheckBox can set null
    // programmatically to show a mixed state, but when the user clicks through
    // checked → indeterminate we immediately push it to unchecked instead.
    private void SelectAllIndeterminate_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressSelectAllSync) return;
        _suppressSelectAllSync = true;
        try
        {
            SelectAllCheckBox.IsChecked = false;
            SetAllFolderCheckBoxes(FolderTree.Items, false);
        }
        finally
        {
            _suppressSelectAllSync = false;
        }
    }

    // Walks the TreeView item collection recursively and sets every CheckBox
    // header to the given state. TV show headers are plain TextBlocks (not
    // checkboxes) so they are skipped — only selectable season/movie nodes fire.
    private void SetAllFolderCheckBoxes(ItemCollection items, bool isChecked)
    {
        foreach (TreeViewItem item in items)
        {
            if (item.Header is CheckBox cb)
                cb.IsChecked = isChecked;

            if (item.Items.Count > 0)
                SetAllFolderCheckBoxes(item.Items, isChecked);
        }
    }

    // Syncs the Select All checkbox state after any individual folder checkbox
    // changes — all checked → checked; any unchecked → unchecked.
    // Called from FolderCheckBox_Checked and FolderCheckBox_Unchecked.
    private void SyncSelectAllCheckBox()
    {
        if (_suppressSelectAllSync) return;

        var allCheckBoxes = CollectAllFolderCheckBoxes(FolderTree.Items);
        if (allCheckBoxes.Count == 0) return;

        var allChecked  = allCheckBoxes.All(cb => cb.IsChecked == true);
        var noneChecked = allCheckBoxes.All(cb => cb.IsChecked != true);

        _suppressSelectAllSync = true;
        try
        {
            // Three-state: all checked → checked, none checked → unchecked,
            // mixed → indeterminate (IsThreeState must be true on the CheckBox).
            SelectAllCheckBox.IsChecked = allChecked ? true
                                        : noneChecked ? false
                                        : null;
        }
        finally
        {
            _suppressSelectAllSync = false;
        }

        SyncTestModeCheckBox();
    }

    // Fires when an individual file checkbox is checked or unchecked.
    private void FileCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        // Snapshot test mode state BEFORE SyncSelectAllCheckBox runs —
        // SyncTestModeCheckBox (called from within) will disable and clear
        // test mode if count != 1, so we'd never see IsChecked == true after.
        var testModeWasOn = TestModeCheckBox.IsChecked == true;

        SyncSelectAllCheckBox();

        // If test mode was on and a second file was just checked, warn and clear it.
        if (testModeWasOn && CountCheckedFileNodes(FolderTree.Items) > 1)
        {
            MessageBox.Show(
                "Test Mode can only be used with a single file.\n\nTest Mode has been unchecked.",
                "Test Mode",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            TestModeCheckBox.IsChecked = false;
        }
    }

    // Test Mode is only enabled when: transcode mode, individual files visible,
    // and exactly one file checkbox is checked.
    private void SyncTestModeCheckBox()
    {
        if (!_isTranscodeMode || ShowFilesCheckBox.IsChecked != true)
        {
            TestModeCheckBox.IsEnabled = false;
            TestModeCheckBox.IsChecked = false;
            return;
        }

        var count = CountCheckedFileNodes(FolderTree.Items);
        TestModeCheckBox.IsEnabled = count == 1;

        if (!TestModeCheckBox.IsEnabled)
            TestModeCheckBox.IsChecked = false;
    }

    // Counts file checkboxes (Tag = RunFileNode, Header = CheckBox) that are checked.
    private int CountCheckedFileNodes(ItemCollection items)
    {
        var count = 0;
        foreach (TreeViewItem item in items)
        {
            if (item.Tag is RunFileNode &&
                item.Header is CheckBox cb &&
                cb.IsChecked == true)
                count++;
            if (item.Items.Count > 0)
                count += CountCheckedFileNodes(item.Items);
        }
        return count;
    }

    private List<CheckBox> CollectAllFolderCheckBoxes(ItemCollection items)
    {
        var result = new List<CheckBox>();
        foreach (TreeViewItem item in items)
        {
            if (item.Header is CheckBox cb)
                result.Add(cb);
            if (item.Items.Count > 0)
                result.AddRange(CollectAllFolderCheckBoxes(item.Items));
        }
        return result;
    }

    // ── Start button ──────────────────────────────────────────────────
    // ── Start button ──────────────────────────────────────────────────
    private async void StartBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning) return;

        var filesToProcess = GetCheckedFiles();
        if (filesToProcess.Count == 0)
        {
            MessageBox.Show("No folders or files selected.",
                "Nothing Selected", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Switch to running state
        _isRunning          = true;
        _cancelRequested    = false;
        _processingComplete = false;
        StartBtn.IsEnabled      = false;
        CancelCloseBtn.Content  = "Cancel";
        ShowFilesCheckBox.IsEnabled = false;
        OutputLog.Clear();

        // Create a single timestamp folder for this entire run
        _runTimestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        _lastRunLogPaths.Clear();
        ViewLogMenuItem.IsEnabled = false;
        _foldersWithErrors.Clear();
        _moveWorkerTask = null;

        // ── Build per-folder file counts and node map ─────────────────
        // We need to know when every file in a folder is done so we can
        // immediately queue that folder for moving — without waiting for
        // the entire run to finish.
        _folderFileTotal.Clear();
        _folderFileDone.Clear();
        _folderNodes.Clear();

        foreach (var file in filesToProcess)
        {
            var top = file.FolderName.Split('\\')[0];
            _folderFileTotal[top] = _folderFileTotal.TryGetValue(top, out var n) ? n + 1 : 1;
            _folderFileDone[top]  = 0;
        }

        // Walk the live tree to populate _folderNodes with TreeItem references.
        BuildFolderNodeMap();

        // ── Start the move worker alongside the processing loop ───────
        // The worker runs independently, draining _moveQueue as folders
        // complete. It exits only when _processingComplete is set AND
        // the queue is empty.
        if (!AppSettings.Instance.DisableMoveCompleted)
            _moveWorkerTask = RunMoveWorkerAsync();

        foreach (var file in filesToProcess)
        {
            if (_cancelRequested) break;

            // Blank line before each file block (except the first) for readability
            if (OutputLog.Text.Length > 0)
                AppendLog("");

            AppendLog($"--- Processing: {file.FolderName}\\{file.FileName} ---");
            await ProcessFileAsync(file);

            if (_cancelRequested)
            {
                // Cancel message depends on whether auto-move is active.
                // When auto-move is disabled the "waiting for folder moves"
                // wording is a lie — no worker is running, nothing to wait
                // for. Keep the message truthful so the user isn't misled.
                AppendLog(AppSettings.Instance.DisableMoveCompleted
                    ? "Job cancelled."
                    : "Job cancelled. Waiting for folder moves to complete\u2026");
                break;
            }

            // ── Check if this folder is now fully processed ───────────
            // If all files for this top-level folder are done and none had
            // errors, enqueue it for moving immediately.
            if (!AppSettings.Instance.DisableMoveCompleted)
            {
                var top = file.FolderName.Split('\\')[0];
                _folderFileDone[top] = (_folderFileDone.TryGetValue(top, out var done) ? done : 0) + 1;

                var allDone   = _folderFileDone[top] >= _folderFileTotal[top];
                var hasErrors = _foldersWithErrors.Any(err =>
                    err.Equals(top, StringComparison.OrdinalIgnoreCase) ||
                    err.StartsWith(top + "\\", StringComparison.OrdinalIgnoreCase));

                if (allDone && !hasErrors)
                    _moveQueue.Enqueue(top);
            }
        }

        if (!_cancelRequested)
            AppendLog("--- Complete ---");

        // Signal the worker that no more folders will arrive, then wait for
        // it to drain whatever is still in the queue before restoring UI.
        _processingComplete = true;

        if (_moveWorkerTask != null)
        {
            CancelCloseBtn.Content   = "Please wait\u2026";
            CancelCloseBtn.IsEnabled = false;
            await _moveWorkerTask;
        }

        // Restore UI state
        _isRunning                  = false;
        StartBtn.IsEnabled          = true;
        CancelCloseBtn.Content      = "Close";
        CancelCloseBtn.IsEnabled    = true;
        ShowFilesCheckBox.IsEnabled = true;

        // Re-run the output check so folders completed in this run
        // get their ✓ checkmarks without needing to reopen the window.
        StartOutputCheck();
    }

    // ── Build folder node map ─────────────────────────────────────────
    // Walks the live TreeView and populates _folderNodes with the
    // RunFolderNode (which holds a TreeItem reference) for every
    // selectable node, keyed on its top-level folder name.
    private void BuildFolderNodeMap()
    {
        foreach (TreeViewItem topItem in FolderTree.Items)
        {
            if (topItem.Tag is RunFolderNode movieNode)
            {
                // Movie — top item is directly selectable
                var top = movieNode.FolderName.Split('\\')[0];
                _folderNodes[top] = movieNode;
            }
            else
            {
                // TV show — season children are the selectable nodes.
                // We want the show's top-level folder, which is the
                // show item itself (plain TextBlock header, no Tag).
                // Use the first season's FolderName to extract it.
                foreach (TreeViewItem seasonItem in topItem.Items)
                {
                    if (seasonItem.Tag is RunFolderNode seasonNode)
                    {
                        var top = seasonNode.FolderName.Split('\\')[0];
                        if (!_folderNodes.ContainsKey(top))
                        {
                            // For TV shows we colour the show-level TreeViewItem
                            // (the non-selectable header row) rather than the
                            // individual season rows, so the whole show lights up.
                            _folderNodes[top] = new RunFolderNode
                            {
                                FolderName = top,
                                TreeItem   = topItem
                            };
                        }
                    }
                }
            }
        }
    }

    // ── Move worker ───────────────────────────────────────────────────
    // Runs alongside the processing loop. Drains _moveQueue as folders
    // complete successfully. Exits when _processingComplete is true and
    // the queue is empty. Colours the TreeViewItem amber → green/red.
    private async Task RunMoveWorkerAsync()
    {
        var completedRoot = Path.Combine(_inputDirectory, "Completed");
        // Minimum amber visibility — even an instant Directory.Move gets
        // a brief flash so the user sees the state change.
        const int AmberMinMs = 300;

        while (!_processingComplete || !_moveQueue.IsEmpty)
        {
            if (_moveQueue.TryDequeue(out var folder))
            {
                var source = Path.Combine(_inputDirectory, folder);
                var dest   = Path.Combine(completedRoot, folder);

                // ── Amber: move in progress ───────────────────────────
                await SetFolderColourAsync(folder, "#FFE08A");

                var sw = System.Diagnostics.Stopwatch.StartNew();

                bool success = false;
                try
                {
                    if (!Directory.Exists(source))
                    {
                        AppendLog($"  Move skipped (not found): {folder}");
                    }
                    else if (Directory.Exists(dest))
                    {
                        AppendLog($"  Move skipped (destination exists): {folder}");
                    }
                    else
                    {
                        Directory.CreateDirectory(completedRoot);
                        Directory.Move(source, dest);
                        AppendLog($"  Moved: {folder}");
                        success = true;
                    }
                }
                catch (Exception ex)
                {
                    AppendLog($"  Error moving {folder}: {ex.Message}");
                }

                // Hold amber for at least AmberMinMs so it's always visible
                var elapsed = (int)sw.ElapsedMilliseconds;
                if (elapsed < AmberMinMs)
                    await Task.Delay(AmberMinMs - elapsed);

                // ── Green on success, red on failure ──────────────────
                await SetFolderColourAsync(folder, success ? "#4CAF50" : "#E53935");
            }
            else
            {
                // Queue empty but processing still running — yield briefly
                // rather than spinning at 100% CPU.
                await Task.Delay(100);
            }
        }
    }

    // Sets the Background of the TreeViewItem for the given top-level folder.
    // Must be called from any thread — marshals to the UI dispatcher internally.
    private Task SetFolderColourAsync(string topFolder, string hex)
    {
        return Dispatcher.InvokeAsync(() =>
        {
            if (_folderNodes.TryGetValue(topFolder, out var node) &&
                node.TreeItem != null)
            {
                node.TreeItem.Background = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString(hex));
            }
        }).Task;
    }

    // ── Cancel / Close button ─────────────────────────────────────────
    private void CancelCloseBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning)
        {
            // Cancel — kill the running process and disable the button.
            // StartBtn_Click will re-enable it as "Close" once the move
            // worker has drained the queue.
            _cancelRequested         = true;
            CancelCloseBtn.Content   = "Please wait\u2026";
            CancelCloseBtn.IsEnabled = false;
            try
            {
                // Kill mkvmerge, ffmpeg, or robocopy if running
                foreach (var name in new[] { "mkvmerge", "ffmpeg", "robocopy" })
                {
                    foreach (var p in Process.GetProcessesByName(name))
                    {
                        p.Kill();
                    }
                }
            }
            catch { /* Process may have already exited */ }
        }
        else
        {
            Close();
        }
    }

    // ── Process a single file ─────────────────────────────────────────
    // Checks for a settings .txt file. If found, runs the saved command.
    // Remux — no settings file: robocopy the file as-is.
    // Transcode — no settings file: probe with ffprobe, build command from
    // defaults, save to disk, then run ffmpeg.
    private async Task ProcessFileAsync(RunFileNode file)
    {
        var modeFolder   = _isTranscodeMode ? "Transcode" : "Remux";
        var nameNoExt    = Path.GetFileNameWithoutExtension(file.FileName);
        var settingsFile = Path.Combine(_inputDirectory, modeFolder,
                               file.FolderName, nameNoExt + ".txt");

        var settings             = AppSettings.Instance;
        var writeLog             = settings.WriteLogFiles;
        var ffmpegVerboseLogging = writeLog && settings.FfmpegVerboseLogging;
        var mkvMergeVerbose      = writeLog && settings.MkvMergeVerbose;

        // Test mode: encode 5 minutes from 10:00 into the source.
        // Only active in transcode mode — read once here and applied
        // consistently throughout this file's processing.
        var testMode = _isTranscodeMode && (TestModeCheckBox.IsChecked == true);

        // ── Prepare log file if enabled ───────────────────────────────
        StreamWriter? logWriter = null;
        _currentLogPath = null;

        if (writeLog)
        {
            try
            {
                var logDir = Path.Combine(_outputDirectory, "Logs", _runTimestamp,
                                          file.FolderName);
                Directory.CreateDirectory(logDir);
                var logFileName = nameNoExt + ".log";
                var logPath     = Path.Combine(logDir, logFileName);
                _currentLogPath = logPath;
                _lastRunLogPaths[$"{file.FolderName}\\{file.FileName}"] = logPath;

                logWriter = new StreamWriter(logPath, append: false, System.Text.Encoding.UTF8);
                logWriter.WriteLine($"TranscodeTools Log");
                logWriter.WriteLine($"Run:  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                logWriter.WriteLine($"File: {file.FolderName}\\{file.FileName}");
                logWriter.WriteLine(new string('-', 60));
                logWriter.WriteLine();
            }
            catch (Exception ex)
            {
                AppendLog($"  Warning: could not create log file: {ex.Message}");
                logWriter = null;
            }
        }

        try
        {
            if (File.Exists(settingsFile))
            {
                var command = File.ReadAllText(settingsFile).Trim();
                if (string.IsNullOrWhiteSpace(command)) return;

                // In verbose mode, swap the loglevel flags in the saved command.
                // Saved commands always contain -loglevel error -stats (non-verbose).
                // We replace that token pair with the verbose flags at runtime so
                // the file on disk always reflects the canonical non-verbose form.
                if (ffmpegVerboseLogging && _isTranscodeMode)
                    command = SwapLogFlags(command);

                // Remux verbose: mkvmerge's "-v" raises verbosity by one level
                // (default level 1 → level 2). Level 2 prints Matroska element
                // details — useful for remux troubleshooting. Inject at runtime
                // so the on-disk settings file stays canonical.
                if (mkvMergeVerbose && !_isTranscodeMode)
                    command = InjectMkvMergeVerbose(command);

                // Test mode: inject -ss/-t and redirect output to Test\ subfolder.
                // Applied after verbose swap so the on-disk command is never touched.
                if (testMode)
                    command = ApplyTestMode(command);

                // Show command in window and write to log
                AppendLog($"Command: {command}");
                AppendLog("");
                logWriter?.WriteLine($"Command: {command}");
                logWriter?.WriteLine();

                var outputFolder = Path.Combine(_outputDirectory, file.FolderName);
                Directory.CreateDirectory(outputFolder);

                string exe, args;

                if (_isTranscodeMode)
                {
                    if (command.StartsWith("\""))
                    {
                        var endQuote = command.IndexOf('"', 1);
                        exe  = command.Substring(1, endQuote - 1);
                        args = command.Substring(endQuote + 1).Trim();
                    }
                    else
                    {
                        var firstSpace = command.IndexOf(' ');
                        exe  = firstSpace > 0 ? command.Substring(0, firstSpace) : command;
                        args = firstSpace > 0 ? command.Substring(firstSpace + 1).Trim() : "";
                    }

                    await LogTranscodeSummaryAsync(command, logWriter);
                    int exitCode1 = await RunProcessAsync(exe, args, logWriter: logWriter,
                                          verboseLogging: ffmpegVerboseLogging);
                    if (exitCode1 != 0)
                        _foldersWithErrors.Add(file.FolderName);
                }
                else
                {
                    if (command.StartsWith("\""))
                    {
                        var endQuote = command.IndexOf('"', 1);
                        exe  = command.Substring(1, endQuote - 1);
                        args = command.Substring(endQuote + 1).Trim();
                    }
                    else
                    {
                        var firstSpace = command.IndexOf(' ');
                        exe  = firstSpace > 0 ? command.Substring(0, firstSpace) : command;
                        args = firstSpace > 0 ? command.Substring(firstSpace + 1).Trim() : "";
                    }

                    int exitCode2 = await RunProcessAsync(exe, args, logWriter: logWriter,
                                          verboseLogging: false);
                    if (exitCode2 != 0)
                        _foldersWithErrors.Add(file.FolderName);
                }
            }
            else if (_isTranscodeMode)
            {
                try
                {
                    var inputFile = Path.Combine(_inputDirectory, file.FolderName, file.FileName);
                    var probe     = await FfprobeService.ProbeFileAsync(inputFile);

                    var videoTracks    = new ObservableCollection<TranscodeVideoTrack>(probe.TranscodeVideo);
                    var audioTracks    = new ObservableCollection<TranscodeAudioTrack>(probe.TranscodeAudio);
                    var subtitleTracks = new ObservableCollection<TranscodeSubtitleTrack>(probe.TranscodeSubtitle);

                    var command = CommandBuilder.BuildTranscodeCommand(
                        _outputDirectory, file.FolderName, file.FileName,
                        _inputDirectory, videoTracks, audioTracks, subtitleTracks,
                        ffmpegVerboseLogging);

                    // No settings file — run from defaults but do NOT save to disk.
                    // The user should explicitly save settings from the main window.
                    AppendLog($"  (No settings file found — running from defaults)");

                    // Test mode: inject -ss/-t and redirect output to Test\ subfolder.
                    if (testMode)
                        command = ApplyTestMode(command);

                    AppendLog($"Command: {command}");
                    AppendLog("");
                    logWriter?.WriteLine($"Command: {command}");
                    logWriter?.WriteLine();

                    var effectiveOutputFolder = Path.Combine(_outputDirectory, file.FolderName);
                    Directory.CreateDirectory(effectiveOutputFolder);
                    await LogTranscodeSummaryAsync(command, logWriter);

                    var exe  = command.StartsWith("\"")
                        ? command.Substring(1, command.IndexOf('"', 1) - 1)
                        : command.Substring(0, command.IndexOf(' '));
                    var args = command.StartsWith("\"")
                        ? command.Substring(command.IndexOf('"', 1) + 1).Trim()
                        : command.Substring(command.IndexOf(' ') + 1).Trim();

                    int exitCode3 = await RunProcessAsync(exe, args, logWriter: logWriter,
                                          verboseLogging: ffmpegVerboseLogging);
                    if (exitCode3 != 0)
                        _foldersWithErrors.Add(file.FolderName);
                }
                catch (Exception ex)
                {
                    AppendLog($"  Error building command: {ex.Message}");
                    logWriter?.WriteLine($"Error building command: {ex.Message}");
                    _foldersWithErrors.Add(file.FolderName);
                }
            }
            else
            {
                // Remux: no settings file — robocopy
                var sourceFolder = Path.Combine(_inputDirectory, file.FolderName);
                var destFolder   = Path.Combine(_outputDirectory, file.FolderName);
                var robocopyArgs = $"\"{sourceFolder}\" \"{destFolder}\" \"{file.FileName}\" " +
                                   AppSettings.Instance.RoboCopy_Defaults;

                AppendLog($"Command: robocopy {robocopyArgs}");
                AppendLog("");
                logWriter?.WriteLine($"Command: robocopy {robocopyArgs}");
                logWriter?.WriteLine();

                Directory.CreateDirectory(destFolder);
                int exitCode4 = await RunProcessAsync("robocopy", robocopyArgs, logWriter: logWriter,
                                      verboseLogging: false);
                // Robocopy uses a bitmask exit code — values 0–7 are all success
                // (bits indicate files copied/skipped/extras, not errors).
                // Exit code 8 or higher signals at least one failure.
                if (exitCode4 > 7)
                    _foldersWithErrors.Add(file.FolderName);
            }
        }
        finally
        {
            if (logWriter != null)
            {
                logWriter.WriteLine();
                logWriter.WriteLine(new string('-', 60));
                logWriter.WriteLine($"Completed: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                logWriter.Dispose();
            }

            // Enable View Log if we wrote any log files this run
            if (writeLog && !string.IsNullOrEmpty(_runTimestamp))
                ViewLogMenuItem.IsEnabled = true;
        }
    }

    // Swaps -loglevel error -stats in a saved command for the verbose flags.
    // Called at runtime only — the file on disk always stores the non-verbose form.
    private static string SwapLogFlags(string command)
    {
        var settings   = AppSettings.Instance;
        var verboseStr = $"-loglevel {settings.FfmpegLogLevel}";
        // Replace the canonical non-verbose tokens with the verbose form.
        // Handle both orderings that CommandBuilder may produce.
        command = command.Replace("-loglevel error -stats", verboseStr);
        command = command.Replace("-loglevel error", verboseStr);
        return command;
    }

    // Injects "-v " right after the quoted mkvmerge executable path so
    // mkvmerge runs at verbosity level 2 (default is 1). Called at
    // runtime only; the file on disk stores the non-verbose form.
    //
    // Expected command shape: "X:\...\mkvmerge.exe" --output "..." ...
    // We find the closing quote of the exe path and insert " -v"
    // immediately after it. If the command doesn't start with a quote
    // (which shouldn't happen for mkvmerge), we no-op to stay safe.
    private static string InjectMkvMergeVerbose(string command)
    {
        if (!command.StartsWith("\"")) return command;
        var endQuote = command.IndexOf('"', 1);
        if (endQuote < 0) return command;
        return command.Substring(0, endQuote + 1) + " -v" + command.Substring(endQuote + 1);
    }

    // Modifies a transcode command for test mode:
    //   - Injects -ss 00:10:00 -t 00:05:00 immediately before -i so ffmpeg
    //     decodes only 5 minutes of source starting at the 10-minute mark.
    //     Seek goes before -i (input-side) so hardware decoders (cuvid/qsv)
    //     can seek efficiently without decoding from the start.
    //   - Injects -ss 00:10:00 -t 00:05:00 as output options (after -i).
    //     Output-side seek works correctly with hardware decoders (QSV/CUDA)
    //     which cannot perform random-access input-side seeks.
    //   - Renames the output file with a [test-<method>] moniker so test clips
    //     are clearly identifiable alongside real encodes in the output folder.
    //     e.g.  Beetlejuice (1998)-480p.mkv
    //       →   Beetlejuice (1998)-480p [test-qsv].mkv
    private string ApplyTestMode(string command)
    {
        // ── Inject -ss and -t after the input file path ───────────────
        // Output-side: ffmpeg decodes from the start, discards until 10:00,
        // then encodes 5 minutes. Works correctly with all hardware decoders.
        var iIdx = command.IndexOf(" -i \"", StringComparison.Ordinal);
        if (iIdx >= 0)
        {
            var inputQuoteStart = command.IndexOf('"', iIdx + 4);
            var inputQuoteEnd   = command.IndexOf('"', inputQuoteStart + 1);
            if (inputQuoteEnd >= 0)
                command = command.Insert(inputQuoteEnd + 1, " -ss 00:10:00 -t 00:05:00");
        }

        // ── Rename output file with [test-<method>] moniker ───────────
        var vendor    = AppSettings.Instance.GpuVendor;
        var methodTag = vendor.Equals("Intel", StringComparison.OrdinalIgnoreCase)
            ? "test-qsv" : "test-nvenc";

        var lastQuoteEnd   = command.LastIndexOf('"');
        var lastQuoteStart = command.LastIndexOf('"', lastQuoteEnd - 1);

        if (lastQuoteStart >= 0 && lastQuoteEnd > lastQuoteStart)
        {
            var outputPath = command.Substring(lastQuoteStart + 1,
                                               lastQuoteEnd - lastQuoteStart - 1);
            var dir      = Path.GetDirectoryName(outputPath) ?? "";
            var nameNoEx = Path.GetFileNameWithoutExtension(outputPath);
            var ext      = Path.GetExtension(outputPath);
            var testPath = Path.Combine(dir, $"{nameNoEx} [{methodTag}]{ext}");

            command = command.Substring(0, lastQuoteStart + 1) +
                      testPath +
                      command.Substring(lastQuoteEnd);
        }

        return command;
    }

    // ── Transcode summary logger ──────────────────────────────────────
    // Runs ffprobe on the input file and writes a per-track summary to
    // the log before ffmpeg starts. Cross-references the saved command
    // to show what decision was made for each track (copy vs encode).
    //
    // Example output:
    //   Input:    Casino Royale (2006).mkv
    //   Video:    0, h264, 1920x1080, 23.98fps → hevc_nvenc, preset p5
    //   Audio:    1, DTS 5.1 768k → copy
    //   Audio:    2, DTS-HD MA 5.1 → copy
    //   Audio:    3, AC3 stereo 224k → copy
    //   Subtitle: 4, PGS → copy
    //   Subtitle: 5, PGS → copy
    //   Output:   F:\Transcoded\Casino Royale (2006)\Casino Royale (2006).mkv
    private async Task LogTranscodeSummaryAsync(string command, StreamWriter? logWriter = null)
    {
        try
        {
            // Local helper — writes to both the Run window and the log file.
            void LogLine(string msg) { AppendLog(msg); logWriter?.WriteLine(msg); }
            // ── Extract input and output paths from command ────────────
            // Input path follows -i, output path is the last quoted token.
            var inputPath  = ExtractQuotedArg(command, "-i");
            var outputPath = ExtractLastQuotedArg(command);

            if (string.IsNullOrWhiteSpace(inputPath)) return;

            // ── Probe the input file ──────────────────────────────────
            var probe = await FfprobeService.ProbeFileAsync(inputPath);

            LogLine($"Input:    {Path.GetFileName(inputPath)}");
            if (!string.IsNullOrWhiteSpace(probe.Duration))
                LogLine($"Runtime:  {probe.Duration}");

            // ── Parse command decisions ───────────────────────────────
            // Read encode codec (after -i), preset, and per-track decisions.
            var tokens = command.Split(' ');

            // Video encode codec: find -c:v after -i
            var encodeCodec = "hevc_nvenc";
            var preset      = AppSettings.Instance.DefaultPreset;
            var inputIndex  = Array.IndexOf(tokens, "-i");
            if (inputIndex >= 0)
            {
                for (int i = inputIndex + 1; i < tokens.Length - 1; i++)
                {
                    if (tokens[i] == "-c:v") { encodeCodec = tokens[i + 1]; break; }
                }
            }
            var presetIndex = Array.IndexOf(tokens, "-preset");
            if (presetIndex >= 0 && presetIndex + 1 < tokens.Length)
                preset = tokens[presetIndex + 1];

            // ── Video summary ─────────────────────────────────────────
            var video = probe.RemuxVideo.FirstOrDefault();
            if (video != null)
            {
                LogLine($"Video:    {video.OriginalTrackIndex}, {video.VideoFormat}, " +
                        $"{video.Resolution}, {video.Fps}fps → {encodeCodec}, preset {preset}");
            }

            // ── Audio summary — per track ─────────────────────────────
            var audioDecisions = new Dictionary<int, string>();
            var audioBitrates  = new Dictionary<int, string>();
            for (int i = 0; i < tokens.Length - 1; i++)
            {
                if (tokens[i].StartsWith("-c:a:") &&
                    int.TryParse(tokens[i].Substring(5), out var caIdx))
                    audioDecisions[caIdx] = tokens[i + 1];

                if (tokens[i].StartsWith("-b:a:") &&
                    int.TryParse(tokens[i].Substring(5), out var baIdx))
                    audioBitrates[baIdx] = tokens[i + 1];
            }

            var allAudio = probe.RemuxAudio
                .OrderBy(t => t.OriginalTrackIndex)
                .ToList();

            for (int i = 0; i < allAudio.Count; i++)
            {
                var t        = allAudio[i];
                var decision = audioDecisions.TryGetValue(i, out var d) ? d : "copy";
                var br       = audioBitrates.TryGetValue(i, out var b)  ? $" {b}"  : "";
                var codec    = string.IsNullOrWhiteSpace(t.AudioFormat) ? "?" : t.AudioFormat;
                var width    = string.IsNullOrWhiteSpace(t.Width)       ? "" : $" {t.Width}";
                var srcBr    = string.IsNullOrWhiteSpace(t.BitRate)     ? "" : $" {t.BitRate}k";

                LogLine($"Audio:    {t.OriginalTrackIndex}, {codec}{width}{srcBr} → {decision}{br}");
            }

            // ── Subtitle summary — per track ──────────────────────────
            var allSubs = probe.RemuxSubtitle
                .OrderBy(t => t.OriginalTrackIndex)
                .ToList();

            for (int i = 0; i < allSubs.Count; i++)
            {
                var t     = allSubs[i];
                var codec = string.IsNullOrWhiteSpace(t.SubtitleFormat) ? "?" : t.SubtitleFormat;
                LogLine($"Subtitle: {t.OriginalTrackIndex}, {codec} → copy");
            }

            // ── Output path ───────────────────────────────────────────
            if (!string.IsNullOrWhiteSpace(outputPath))
                LogLine($"Output:   {outputPath}");

            LogLine("");  // blank line before progress output
        }
        catch
        {
            // If probing fails for any reason, skip the summary and
            // proceed with the encode — don't block processing.
        }
    }

    // Extracts the quoted argument that follows a named flag in a command string.
    // e.g. ExtractQuotedArg(command, "-i") returns the path after -i.
    private static string ExtractQuotedArg(string command, string flag)
    {
        var flagIndex = command.IndexOf(flag + " \"", StringComparison.OrdinalIgnoreCase);
        if (flagIndex < 0) return "";

        var start = command.IndexOf('"', flagIndex + flag.Length);
        if (start < 0) return "";

        var end = command.IndexOf('"', start + 1);
        if (end < 0) return "";

        return command.Substring(start + 1, end - start - 1);
    }

    // Extracts the last quoted argument in a command string (the output path).
    private static string ExtractLastQuotedArg(string command)
    {
        var lastEnd   = command.LastIndexOf('"');
        if (lastEnd < 0) return "";

        var lastStart = command.LastIndexOf('"', lastEnd - 1);
        if (lastStart < 0) return "";

        return command.Substring(lastStart + 1, lastEnd - lastStart - 1);
    }

    // ── Run an external process and stream output to the log ──────────
    // Uses async/await so the UI stays responsive throughout.
    // Output lines are appended to the log on the UI thread.
    // Progress lines (frame=, Progress:, ending with %) update the
    // last line in place rather than appending, matching the original
    // app's intent but implemented correctly with async.
    //
    // workingDirectory is optional — only needed for Transcode mode,
    // where other-transcode writes its output .mkv to the current
    // working directory rather than using an explicit --output flag.
    private async Task<int> RunProcessAsync(string exe, string args,
                                            string? workingDirectory = null,
                                            StreamWriter? logWriter = null,
                                            bool verboseLogging = false)
    {
        int exitCode = -1;
        var psi = new ProcessStartInfo
        {
            FileName               = exe,
            Arguments              = args,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true
        };

        if (!string.IsNullOrWhiteSpace(workingDirectory))
            psi.WorkingDirectory = workingDirectory;

        _currentProcess = new Process { StartInfo = psi };

        // ── Verbose mode: animated text ticker ───────────────────────
        // Since all ffmpeg output goes to the log file, the window shows
        // an animated [----      ] ticker so the user knows work is ongoing.
        // A CancellationTokenSource lets us stop the ticker when the
        // process finishes.
        CancellationTokenSource? tickerCts = null;
        Task? tickerTask = null;

        if (verboseLogging)
        {
            tickerCts  = new CancellationTokenSource();
            tickerTask = RunTickerAsync(tickerCts.Token);
        }

        _currentProcess.OutputDataReceived += (s, e) =>
        {
            if (e.Data == null) return;
            if (verboseLogging)
                logWriter?.WriteLine(e.Data);
            else
                Dispatcher.InvokeAsync(() =>
                {
                    AppendLogLine(e.Data);
                    // Strip progress lines from the log file — they flood it
                    // with hundreds of near-identical frame= lines.
                    if (logWriter != null && !IsProgressLine(e.Data.Trim()))
                        logWriter.WriteLine(e.Data);
                });
        };

        _currentProcess.ErrorDataReceived += (s, e) =>
        {
            if (e.Data == null) return;
            if (verboseLogging)
                logWriter?.WriteLine(e.Data);
            else
                Dispatcher.InvokeAsync(() =>
                {
                    AppendLogLine(e.Data);
                    if (logWriter != null && !IsProgressLine(e.Data.Trim()))
                        logWriter.WriteLine(e.Data);
                });
        };

        try
        {
            _currentProcess.Start();
            _currentProcess.BeginOutputReadLine();
            _currentProcess.BeginErrorReadLine();

            await _currentProcess.WaitForExitAsync();
            exitCode = _currentProcess.ExitCode;

            await Dispatcher.InvokeAsync(() =>
            {
                if (!verboseLogging &&
                    OutputLog.Text.Length > 0 && !OutputLog.Text.EndsWith('\n'))
                    OutputLog.Text += "\n";
            }, System.Windows.Threading.DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            AppendLog($"Error running process: {ex.Message}");
            logWriter?.WriteLine($"Error running process: {ex.Message}");
        }
        finally
        {
            _currentProcess.Dispose();
            _currentProcess = null;

            if (tickerCts != null)
            {
                tickerCts.Cancel();
                try { await tickerTask!; } catch { }
                tickerCts.Dispose();
                // Clear the ticker line from the window
                await Dispatcher.InvokeAsync(() => ClearTickerLine());
            }
        }

        return exitCode;
    }

    // ── Animated text ticker ──────────────────────────────────────────
    // Writes a bouncing [----      ] indicator to the output log while
    // verbose-mode ffmpeg runs. Updates every 120ms on the UI thread.
    private const int TickerWidth = 12;

    private async Task RunTickerAsync(CancellationToken ct)
    {
        int  pos       = 0;
        int  direction = 1;
        bool firstTick = true;
        const int blockLen = 4;

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(120, ct).ContinueWith(_ => { });  // swallow cancellation

            if (ct.IsCancellationRequested) break;

            var bar = new char[TickerWidth];
            for (int i = 0; i < TickerWidth; i++)
                bar[i] = (i >= pos && i < pos + blockLen) ? '-' : ' ';
            var ticker = $"Processing... [{new string(bar)}]  Details in log file";

            await Dispatcher.InvokeAsync(() =>
            {
                if (firstTick)
                {
                    AppendLog(ticker);
                    firstTick = false;
                }
                else
                {
                    // Replace the last line in place
                    var text   = OutputLog.Text;
                    var lastNl = text.LastIndexOf('\n', text.Length - 2);
                    OutputLog.Text = lastNl >= 0
                        ? text.Substring(0, lastNl + 1) + ticker + "\n"
                        : ticker + "\n";
                    OutputLog.ScrollToEnd();
                }
            });

            pos += direction;
            if (pos + blockLen >= TickerWidth) direction = -1;
            if (pos <= 0)                      direction =  1;
        }
    }

    // Removes the ticker line when the process finishes, leaving a clean log.
    private void ClearTickerLine()
    {
        var text = OutputLog.Text;
        if (!text.Contains("Processing... [")) return;
        var lastNl = text.LastIndexOf('\n', text.Length - 2);
        OutputLog.Text = lastNl >= 0 ? text.Substring(0, lastNl + 1) : "";
        OutputLog.ScrollToEnd();
    }

    // Returns true for lines that should be suppressed in the plain log file.
    private static bool IsProgressLine(string display) =>
        display.StartsWith("frame=",    StringComparison.OrdinalIgnoreCase) ||
        display.StartsWith("Progress:", StringComparison.OrdinalIgnoreCase) ||
        display.EndsWith("%");

    // ── Log helpers ───────────────────────────────────────────────────

    // Appends a line to the log, handling progress lines specially.
    // Progress lines (frame=, Progress:, ending with %) update the
    // last line in place rather than adding a new line — this avoids
    // flooding the log with hundreds of progress updates.
    //
    // Robocopy uses \r to overwrite progress in a real console, which
    // means redirected output arrives with leading/trailing whitespace.
    // We trim for display, but use the original to detect robocopy's
    // final progress line (ends with "%  ") and add a blank separator.
    private void AppendLogLine(string line)
    {
        if (string.IsNullOrEmpty(line)) return;

        // Detect robocopy completion BEFORE trimming — its final progress
        // line ends with "% " (percent followed by trailing spaces).
        var isRobocopyComplete = line.TrimEnd().EndsWith("%") &&
                                 line.EndsWith("  ");

        // Trim \r and surrounding whitespace for clean display
        var display = line.Trim();
        if (string.IsNullOrEmpty(display)) return;

        var isProgress = display.StartsWith("frame=",    StringComparison.OrdinalIgnoreCase) ||
                         display.StartsWith("Progress:", StringComparison.OrdinalIgnoreCase) ||
                         display.EndsWith("%");

        if (isProgress && OutputLog.Text.Length > 0)
        {
            // Replace the last line with the updated progress
            var text   = OutputLog.Text;
            var lastNl = text.LastIndexOf('\n');
            OutputLog.Text = lastNl >= 0
                ? text.Substring(0, lastNl + 1) + display
                : display;

            // Robocopy's final progress line — add a newline so the next
            // "--- Processing ---" header starts on a fresh line with
            // visible separation between robocopy operations.
            if (isRobocopyComplete)
                OutputLog.Text += "\n";
        }
        else
        {
            // Ensure a newline before appending — progress lines don't end
            // with \n, so without this check non-progress lines run straight
            // into them (e.g. "Progress: 100%The cue entries...").
            if (OutputLog.Text.Length > 0 && !OutputLog.Text.EndsWith('\n'))
                OutputLog.Text += "\n";
            OutputLog.Text += display + "\n";
        }

        // Scroll to bottom
        OutputLog.ScrollToEnd();
    }

    // Simple log append — used for status messages like "--- Processing ---"
    private void AppendLog(string message)
    {
        if (OutputLog.Text.Length > 0 && !OutputLog.Text.EndsWith('\n'))
            OutputLog.Text += "\n";
        OutputLog.Text += message + "\n";
        OutputLog.ScrollToEnd();
    }

    // ── Build the list of files to process ───────────────────────────
    // In folders-only mode: all .mkv files in checked folders.
    // In show-files mode: only individually checked files.
    private List<RunFileNode> GetCheckedFiles()
    {
        var result     = new List<RunFileNode>();
        var showFiles  = ShowFilesCheckBox.IsChecked == true;
        var modeFolder = _isTranscodeMode ? "Transcode" : "Remux";

        foreach (TreeViewItem topItem in FolderTree.Items)
        {
            if (topItem.Header is CheckBox)
            {
                // ── Movie folder ──────────────────────────────────────
                CollectFromFolderItem(topItem, showFiles, modeFolder, result);
            }
            else
            {
                // ── TV show: iterate season children ──────────────────
                foreach (TreeViewItem seasonItem in topItem.Items)
                    CollectFromFolderItem(seasonItem, showFiles, modeFolder, result);
            }
        }

        return result;
    }

    // Collects files from a single selectable folder item (movie or season).
    // In show-files mode: only individually checked file children are collected.
    // In folders mode: all .mkv files in the folder are collected if checked.
    private void CollectFromFolderItem(TreeViewItem folderItem, bool showFiles,
                                       string modeFolder, List<RunFileNode> result)
    {
        if (folderItem.Header is not CheckBox folderCb) return;
        if (folderItem.Tag is not RunFolderNode folderNode) return;

        if (showFiles)
        {
            foreach (TreeViewItem fileItem in folderItem.Items)
            {
                if (fileItem.Header is CheckBox fileCb &&
                    fileCb.IsChecked == true &&
                    fileItem.Tag is RunFileNode fileNode)
                {
                    result.Add(fileNode);
                }
            }
        }
        else if (folderCb.IsChecked == true)
        {
            var fullPath = Path.Combine(_inputDirectory, folderNode.FolderName);
            try
            {
                var files = Directory.GetFiles(fullPath, "*.mkv")
                    .Select(Path.GetFileName)
                    .Where(f => f != null)
                    .OrderBy(f => f);

                foreach (var fileName in files)
                {
                    if (fileName == null) continue;
                    var nameNoExt    = Path.GetFileNameWithoutExtension(fileName);
                    var settingsFile = Path.Combine(_inputDirectory, modeFolder,
                                           folderNode.FolderName, nameNoExt + ".txt");
                    result.Add(new RunFileNode
                    {
                        FolderName  = folderNode.FolderName,
                        FileName    = fileName,
                        HasSettings = File.Exists(settingsFile)
                    });
                }
            }
            catch { /* Skip unreadable folders */ }
        }
    }

    // ── View Log right-click handler ──────────────────────────────────
    // Opens the session's Logs timestamp folder in Explorer so the user
    // can browse all log files from this run.
    private void ViewLogMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var logFolder = Path.Combine(_outputDirectory, "Logs", _runTimestamp);
        if (!Directory.Exists(logFolder)) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{logFolder}\"")
                { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open log folder:\n{ex.Message}",
                "View Log", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ── Visual tree helper ────────────────────────────────────────────
    private static T? FindAncestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element != null)
        {
            if (element is T target) return target;
            element = VisualTreeHelper.GetParent(element);
        }
        return null;
    }
}

// ── Data models for the TreeView nodes ───────────────────────────────

// Represents a movie/show folder in the TreeView
public class RunFolderNode
{
    public string FolderName { get; set; } = "";

    // Stored at tree-build time so the move worker can update the node's
    // background colour from a Dispatcher call without searching the tree.
    public TreeViewItem? TreeItem { get; set; }
}

// Represents an individual .mkv file
public class RunFileNode
{
    public string FolderName  { get; set; } = "";
    public string FileName    { get; set; } = "";
    public bool   HasSettings { get; set; }
}
