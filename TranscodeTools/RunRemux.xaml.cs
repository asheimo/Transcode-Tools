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
    }

    // ── Window loaded ─────────────────────────────────────────────────
    private void RunRemux_Loaded(object sender, RoutedEventArgs e)
    {
        if (!AppSettings.Instance.WriteLogFiles)
            OutputLog.ContextMenu = null;
        PopulateFolderTree();
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
                               !name.Equals("Logs",      StringComparison.OrdinalIgnoreCase))
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
        => PopulateFolderTree();

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

        // Create a single timestamp folder for this entire run
        _runTimestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        _lastRunLogPaths.Clear();
        ViewLogMenuItem.IsEnabled  = false;

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
    // Remux — no settings file: robocopy the file as-is.
    // Transcode — no settings file: probe with ffprobe, build command from
    // defaults, save to disk, then run ffmpeg.
    private async Task ProcessFileAsync(RunFileNode file)
    {
        var modeFolder   = _isTranscodeMode ? "Transcode" : "Remux";
        var nameNoExt    = Path.GetFileNameWithoutExtension(file.FileName);
        var settingsFile = Path.Combine(_inputDirectory, modeFolder,
                               file.FolderName, nameNoExt + ".txt");

        var settings        = AppSettings.Instance;
        var writeLog        = settings.WriteLogFiles;
        var verboseLogging  = writeLog && settings.VerboseLogging;

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
                if (verboseLogging && _isTranscodeMode)
                    command = SwapLogFlags(command);

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
                    await RunProcessAsync(exe, args, logWriter: logWriter,
                                          verboseLogging: verboseLogging);
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

                    await RunProcessAsync(exe, args, logWriter: logWriter,
                                          verboseLogging: false);
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
                        verboseLogging);

                    // No settings file — run from defaults but do NOT save to disk.
                    // The user should explicitly save settings from the main window.
                    AppendLog($"  (No settings file found — running from defaults)");
                    AppendLog($"Command: {command}");
                    AppendLog("");
                    logWriter?.WriteLine($"Command: {command}");
                    logWriter?.WriteLine();

                    Directory.CreateDirectory(Path.Combine(_outputDirectory, file.FolderName));
                    await LogTranscodeSummaryAsync(command, logWriter);

                    var exe  = command.StartsWith("\"")
                        ? command.Substring(1, command.IndexOf('"', 1) - 1)
                        : command.Substring(0, command.IndexOf(' '));
                    var args = command.StartsWith("\"")
                        ? command.Substring(command.IndexOf('"', 1) + 1).Trim()
                        : command.Substring(command.IndexOf(' ') + 1).Trim();

                    await RunProcessAsync(exe, args, logWriter: logWriter,
                                          verboseLogging: verboseLogging);
                }
                catch (Exception ex)
                {
                    AppendLog($"  Error building command: {ex.Message}");
                    logWriter?.WriteLine($"Error building command: {ex.Message}");
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
                await RunProcessAsync("robocopy", robocopyArgs, logWriter: logWriter,
                                      verboseLogging: false);
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
    private async Task RunProcessAsync(string exe, string args,
                                       string? workingDirectory = null,
                                       StreamWriter? logWriter = null,
                                       bool verboseLogging = false)
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
}

// Represents an individual .mkv file
public class RunFileNode
{
    public string FolderName  { get; set; } = "";
    public string FileName    { get; set; } = "";
    public bool   HasSettings { get; set; }
}
