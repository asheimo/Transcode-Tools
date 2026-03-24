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

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
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
    }

    // ── Window loaded ─────────────────────────────────────────────────
    private void RunRemux_Loaded(object sender, RoutedEventArgs e)
    {
        PopulateFolderTree();
    }

    // ── Populate the TreeView ─────────────────────────────────────────
    // In folders-only mode (default): one checkbox node per movie folder.
    // In show-files mode: each folder node expands to show its .mkv files,
    // with green background on files that have a settings file saved.
    private void PopulateFolderTree()
    {
        FolderTree.Items.Clear();

        if (string.IsNullOrWhiteSpace(_inputDirectory) ||
            !Directory.Exists(_inputDirectory)) return;

        var modeFolder  = _isTranscodeMode ? "Transcode" : "Remux";
        var showFiles   = ShowFilesCheckBox.IsChecked == true;

        try
        {
            var folders = Directory.GetDirectories(_inputDirectory)
                .Select(Path.GetFileName)
                .Where(name => name != null &&
                               !name.Equals("Remux",     StringComparison.OrdinalIgnoreCase) &&
                               !name.Equals("Transcode", StringComparison.OrdinalIgnoreCase))
                .OrderBy(name => name);

            foreach (var folderName in folders)
            {
                if (folderName == null) continue;

                var folderNode = new RunFolderNode { FolderName = folderName };
                var treeItem   = BuildFolderTreeItem(folderNode, folderName, modeFolder, showFiles);
                FolderTree.Items.Add(treeItem);
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Error reading input directory: {ex.Message}");
        }
    }

    // Builds a TreeViewItem for a folder, optionally with file children.
    private TreeViewItem BuildFolderTreeItem(RunFolderNode node, string folderName,
                                              string modeFolder, bool showFiles)
    {
        // ── Folder node ───────────────────────────────────────────────
        var checkBox = new CheckBox
        {
            Content    = folderName,
            FontSize   = 13,
            Foreground = (Brush)Application.Current.FindResource("ForegroundColor"),
            Tag        = node
        };
        checkBox.Checked   += FolderCheckBox_Checked;
        checkBox.Unchecked += FolderCheckBox_Unchecked;

        var treeItem = new TreeViewItem
        {
            Header  = checkBox,
            Tag     = node
        };

        if (!showFiles) return treeItem;

        // ── File children (show files mode only) ─────────────────────
        var folderPath = Path.Combine(_inputDirectory, folderName);
        try
        {
            var files = Directory.GetFiles(folderPath, "*.mkv")
                .Select(Path.GetFileName)
                .Where(f => f != null)
                .OrderBy(f => f);

            foreach (var fileName in files)
            {
                if (fileName == null) continue;

                var nameNoExt    = Path.GetFileNameWithoutExtension(fileName);
                var settingsFile = Path.Combine(_inputDirectory, modeFolder,
                                       folderName, nameNoExt + ".txt");
                var hasSettings  = File.Exists(settingsFile);

                var fileNode    = new RunFileNode
                {
                    FolderName  = folderName,
                    FileName    = fileName,
                    HasSettings = hasSettings
                };

                var fileCheckBox = new CheckBox
                {
                    Content    = nameNoExt,
                    FontSize   = 12,
                    Foreground = (Brush)Application.Current.FindResource("ForegroundColor"),
                    // Green background if settings exist, matching main window style
                    Background = hasSettings ? Brushes.Green : Brushes.Transparent,
                    Tag        = fileNode
                };

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
        => SetChildCheckBoxes(sender, true);

    private void FolderCheckBox_Unchecked(object sender, RoutedEventArgs e)
        => SetChildCheckBoxes(sender, false);

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
        => PopulateFolderTree();

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
        _isRunning       = true;
        _cancelRequested = false;
        StartBtn.IsEnabled      = false;
        CancelCloseBtn.Content  = "Cancel";
        ShowFilesCheckBox.IsEnabled = false;
        OutputLog.Clear();

        foreach (var file in filesToProcess)
        {
            if (_cancelRequested) break;

            AppendLog($"--- Processing: {file.FolderName}\\{file.FileName} ---");
            await ProcessFileAsync(file);

            if (_cancelRequested)
            {
                AppendLog("Job cancelled.");
                break;
            }
        }

        if (!_cancelRequested)
            AppendLog("--- Complete ---");

        // Restore UI state
        _isRunning                  = false;
        StartBtn.IsEnabled          = true;
        CancelCloseBtn.Content      = "Close";
        ShowFilesCheckBox.IsEnabled = true;
    }

    // ── Cancel / Close button ─────────────────────────────────────────
    private void CancelCloseBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning)
        {
            // Cancel — kill the running process
            _cancelRequested = true;
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
    // If not found, runs Robocopy to copy the file to the output folder.
    private async Task ProcessFileAsync(RunFileNode file)
    {
        var modeFolder   = _isTranscodeMode ? "Transcode" : "Remux";
        var nameNoExt    = Path.GetFileNameWithoutExtension(file.FileName);
        var settingsFile = Path.Combine(_inputDirectory, modeFolder,
                               file.FolderName, nameNoExt + ".txt");

        if (File.Exists(settingsFile))
        {
            // ── Run the saved command ─────────────────────────────────
            var command = File.ReadAllText(settingsFile).Trim();
            if (string.IsNullOrWhiteSpace(command)) return;

            // Split the command into executable and arguments.
            // The executable is the first quoted string in the command.
            // e.g. "C:\bin\mkvmerge.exe" --output ...
            string exe, args;
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

            // Ensure output folder exists
            var outputFolder = Path.Combine(_outputDirectory, file.FolderName);
            Directory.CreateDirectory(outputFolder);

            await RunProcessAsync(exe, args);
        }
        else
        {
            // ── Robocopy — no settings file, just copy the file ───────
            var sourceFolder = Path.Combine(_inputDirectory, file.FolderName);
            var destFolder   = Path.Combine(_outputDirectory, file.FolderName);
            var robocopyArgs = $"\"{sourceFolder}\" \"{destFolder}\" \"{file.FileName}\" " +
                               AppSettings.Instance.RoboCopy_Defaults;

            Directory.CreateDirectory(destFolder);
            await RunProcessAsync("robocopy", robocopyArgs);
        }
    }

    // ── Run an external process and stream output to the log ──────────
    // Uses async/await so the UI stays responsive throughout.
    // Output lines are appended to the log on the UI thread.
    // Progress lines (frame=, Progress:, ending with %) update the
    // last line in place rather than appending, matching the original
    // app's intent but implemented correctly with async.
    private async Task RunProcessAsync(string exe, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = exe,
            Arguments              = args,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true
        };

        _currentProcess = new Process { StartInfo = psi };

        // Wire up async output handlers.
        // These fire on a thread pool thread — we marshal to the UI
        // thread using Dispatcher.InvokeAsync, which is the correct
        // WPF approach (replaces the VB.NET InvokeRequired/Invoke pattern).
        _currentProcess.OutputDataReceived += (s, e) =>
        {
            if (e.Data != null)
                Dispatcher.InvokeAsync(() => AppendLogLine(e.Data));
        };

        _currentProcess.ErrorDataReceived += (s, e) =>
        {
            if (e.Data != null)
                Dispatcher.InvokeAsync(() => AppendLogLine(e.Data));
        };

        try
        {
            _currentProcess.Start();
            _currentProcess.BeginOutputReadLine();
            _currentProcess.BeginErrorReadLine();

            // WaitForExitAsync lets the UI stay responsive while the
            // process runs — no freezing, no DoEvents() hack needed.
            await _currentProcess.WaitForExitAsync();

            // Flush any queued output dispatcher operations before returning,
            // so all output lines (including the final progress %) land in the
            // log before the next "--- Processing ---" header is appended.
            await Dispatcher.InvokeAsync(() =>
            {
                // Ensure the log ends with a newline so the next header
                // always starts on a fresh line — fixes "99.9%100%" and
                // "Progress: 100%--- Processing ---" run-together glitches.
                if (OutputLog.Text.Length > 0 && !OutputLog.Text.EndsWith('\n'))
                    OutputLog.Text += "\n";
            }, System.Windows.Threading.DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            AppendLog($"Error running process: {ex.Message}");
        }
        finally
        {
            _currentProcess.Dispose();
            _currentProcess = null;
        }
    }

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
        var result    = new List<RunFileNode>();
        var showFiles = ShowFilesCheckBox.IsChecked == true;
        var modeFolder = _isTranscodeMode ? "Transcode" : "Remux";

        foreach (TreeViewItem folderItem in FolderTree.Items)
        {
            if (folderItem.Header is not CheckBox folderCb) continue;
            if (folderItem.Tag is not RunFolderNode folderNode) continue;

            if (showFiles)
            {
                // Only process individually checked files
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
                // Process all .mkv files in the checked folder
                var folderPath = Path.Combine(_inputDirectory, folderNode.FolderName);
                try
                {
                    var files = Directory.GetFiles(folderPath, "*.mkv")
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

        return result;
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
}

// Represents an individual .mkv file
public class RunFileNode
{
    public string FolderName  { get; set; } = "";
    public string FileName    { get; set; } = "";
    public bool   HasSettings { get; set; }
}
