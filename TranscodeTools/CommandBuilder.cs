// ============================================================
// CommandBuilder.cs
// ------------------------------------------------------------
// Builds the mkvmerge or other-transcode command line string
// from the current track table state.
//
// In the original VB.NET app this logic lived inside the large
// CreateCommandSettingsString() function in Form1.vb alongside
// the save/load file logic. Here we separate concerns:
//   - CommandBuilder  → builds the command string
//   - MainWindow      → handles save/load, UI feedback
//
// Both methods return a plain string that gets written to the
// .txt settings file. The file contains exactly one line —
// the complete command ready to be executed by RunRemux (Step 6).
// ============================================================

using System.Collections.ObjectModel;
using System.Text;
using System.IO;

namespace TranscodeTools;

public static class CommandBuilder
{
    // ── Remux command ─────────────────────────────────────────────────
    // Builds a mkvmerge command from the Remux track tables.
    //
    // Parameters:
    //   outputDirectory  — the output root path from the UI
    //   movieFolder      — the selected movie folder name
    //   fileName         — the selected .mkv filename (with extension)
    //   inputDirectory   — the input root path
    //   audioTracks      — current state of the audio ListView (may be reordered)
    //   subtitleTracks   — current state of the subtitle ListView
    //
    // Returns the complete mkvmerge command string, or throws an
    // InvalidOperationException if no audio tracks are selected.
    public static string BuildRemuxCommand(
        string outputDirectory,
        string movieFolder,
        string fileName,
        string inputDirectory,
        ObservableCollection<RemuxAudioTrack> audioTracks,
        ObservableCollection<RemuxSubtitleTrack> subtitleTracks)
    {
        // ── Validate audio selection ──────────────────────────────────
        // mkvmerge requires at least one audio track. Match the original
        // app's behaviour of aborting if none are selected.
        var selectedAudio = audioTracks.Where(t => t.IsSelected).ToList();
        if (selectedAudio.Count == 0)
            throw new InvalidOperationException(
                "No audio tracks selected. Please select at least one audio track.");

        var selectedSubtitles = subtitleTracks.Where(t => t.IsSelected).ToList();

        // ── Build output file path ────────────────────────────────────
        // Output mirrors the input folder structure under the output root.
        // e.g. "H:\Output\Casino Royale (2006)\Casino Royale (2006).mkv"
        var nameNoExt    = Path.GetFileNameWithoutExtension(fileName);
        var outputFile   = Path.Combine(outputDirectory, movieFolder, fileName);
        var inputFile    = Path.Combine(inputDirectory, movieFolder, fileName);
        var mkvMergePath = AppSettings.Instance.MKVMerge_Path;
        var options      = AppSettings.Instance.MKVMerge_Options;

        // StringBuilder is more efficient than string concatenation in a loop.
        // In VB.NET you would use String.Concat or &= — same idea, but
        // StringBuilder avoids creating a new string object on every append.
        var sb = new StringBuilder();

        // ── Base: mkvmerge --output "outputFile" ─────────────────────
        sb.Append($"\"{mkvMergePath}\" --output \"{outputFile}\"");

        // ── Title ─────────────────────────────────────────────────────
        // Use the display name (everything before the first dash for extras,
        // or the full name for main titles). We derive it from the filename
        // the same way the original did — split on "-" and take first part,
        // then trim whitespace.
        var title = nameNoExt.Contains('-')
            ? nameNoExt.Substring(0, nameNoExt.IndexOf('-')).Trim()
            : nameNoExt.Trim();
        sb.Append($" --title \"{title}\"");

        // ── Default video track ───────────────────────────────────────
        // Video is always track 0 and always included. The original always
        // hardcoded this, and we do the same.
        sb.Append(" --default-track 0");
        sb.Append(" --video-tracks 0");

        // ── Default audio track ───────────────────────────────────────
        // The track marked IsDefault gets --default-track <index>.
        // Any track explicitly NOT default gets --default-track <index>:0
        // (the ":0" tells mkvmerge to clear the default flag).
        var defaultAudio = selectedAudio.FirstOrDefault(t => t.IsDefault);
        if (defaultAudio != null)
            sb.Append($" --default-track {defaultAudio.OriginalTrackIndex}");

        foreach (var t in selectedAudio.Where(t => !t.IsDefault))
            sb.Append($" --default-track {t.OriginalTrackIndex}:0");

        // ── Default subtitle track ────────────────────────────────────
        var defaultSubtitle = selectedSubtitles.FirstOrDefault(t => t.IsDefault);
        if (defaultSubtitle != null)
            sb.Append($" --default-track {defaultSubtitle.OriginalTrackIndex}");

        // ── Forced subtitle tracks ────────────────────────────────────
        foreach (var t in selectedSubtitles.Where(t => t.IsForced))
            sb.Append($" --forced-track {t.OriginalTrackIndex}");

        // ── Audio track list ──────────────────────────────────────────
        // "--audio-tracks 1,3,2" tells mkvmerge which audio streams to include.
        // The order here reflects the current ListView order (after any drag reorder).
        var audioIndexes = selectedAudio.Select(t => t.OriginalTrackIndex.ToString());
        sb.Append($" --audio-tracks {string.Join(",", audioIndexes)}");

        // ── Subtitle track list ───────────────────────────────────────
        if (selectedSubtitles.Count > 0)
        {
            var subIndexes = selectedSubtitles.Select(t => t.OriginalTrackIndex.ToString());
            sb.Append($" --subtitle-tracks {string.Join(",", subIndexes)}");
        }

        // ── Track order ───────────────────────────────────────────────
        // If the user has reordered audio tracks via drag and drop, we need
        // to tell mkvmerge the physical output order using --track-order.
        // Format: --track-order 0:0,0:2,0:1,0:3,...
        // (all from file 0, in the order the user arranged them)
        //
        // We only add this if the audio is NOT in ascending index order,
        // matching the original app's IsAlphabaticOrder check.
        // This avoids cluttering the command for the common case where
        // the user hasn't reordered anything.
        if (!IsAscendingOrder(selectedAudio.Select(t => t.OriginalTrackIndex)))
        {
            // Video is always first (0:0), then audio in user order, then subtitles
            var trackOrderParts = new List<string> { "0:0" };
            trackOrderParts.AddRange(selectedAudio.Select(t => $"0:{t.OriginalTrackIndex}"));
            trackOrderParts.AddRange(selectedSubtitles.Select(t => $"0:{t.OriginalTrackIndex}"));
            sb.Append($" --track-order {string.Join(",", trackOrderParts)}");
        }

        // ── MKVMerge options from preferences ────────────────────────
        if (!string.IsNullOrWhiteSpace(options))
            sb.Append($" {options.Trim()}");

        // ── Input file ────────────────────────────────────────────────
        sb.Append($" \"{inputFile}\"");

        return sb.ToString();
    }

    // ── Transcode command ─────────────────────────────────────────────
    // Builds an other-transcode command from the Transcode track tables.
    //
    // other-transcode uses positional track numbers (1-based) rather than
    // the raw ffprobe stream indexes that mkvmerge uses.
    public static string BuildTranscodeCommand(
        string movieFolder,
        string fileName,
        string inputDirectory,
        ObservableCollection<TranscodeVideoTrack> videoTracks,
        ObservableCollection<TranscodeAudioTrack> audioTracks,
        ObservableCollection<TranscodeSubtitleTrack> subtitleTracks)
    {
        var nameNoExt       = Path.GetFileNameWithoutExtension(fileName);
        var inputFile       = Path.Combine(inputDirectory, movieFolder, fileName);
        var transcodePath   = AppSettings.Instance.OtherTranscode_Path;
        var options         = AppSettings.Instance.OtherTranscode_Options;

        var sb = new StringBuilder();

        // ── Video options ─────────────────────────────────────────────
        // Check if the user selected hevc output — adds —–hevc flag.
        // other-transcode defaults to h264 if no format is specified.
        var video = videoTracks.FirstOrDefault();
        var outputFormat = "";
        if (video != null &&
            video.OutputFormat.Equals("hevc (default)", StringComparison.OrdinalIgnoreCase))
        {
            outputFormat = "--hevc ";
        }

        // ── Audio options ─────────────────────────────────────────────
        // other-transcode uses 1-based track positions, not ffprobe indexes.
        // --main-audio 1=<width>  sets the primary audio track
        // --add-audio  N=<width>  adds additional tracks
        //
        // If a DTS track is set to Keep (copy), we need --pass-dts.
        var audioSb   = new StringBuilder();
        var needPassDts = false;
        var position  = 1;

        foreach (var t in audioTracks)
        {
            // "Keep" means copy without re-encoding — use "original" as width
            var width = (t.Width.Equals("keep", StringComparison.OrdinalIgnoreCase) ||
                         string.IsNullOrWhiteSpace(t.Width))
                ? "original"
                : t.Width.ToLower();

            if (position == 1)
                audioSb.Append($"--main-audio 1={width} ");
            else
                audioSb.Append($"--add-audio {position}={width} ");

            // Check if this is a DTS track being kept — needs --pass-dts
            if (t.Format.Contains("dts", StringComparison.OrdinalIgnoreCase) &&
                (t.Width.Equals("keep", StringComparison.OrdinalIgnoreCase) ||
                 string.IsNullOrWhiteSpace(t.Width)))
            {
                needPassDts = true;
            }

            // eac3 output requires --eac3 in options
            if (t.Format.Equals("eac3", StringComparison.OrdinalIgnoreCase))
                options = options.TrimEnd() + " --eac3";

            position++;
        }

        if (needPassDts)
            options = options.TrimEnd() + " --pass-dts";

        // ── Subtitle options ──────────────────────────────────────────
        // --burn-subtitle N  burns subtitle N into the video
        // --add-subtitle  N  passes subtitle N through as a stream
        var subSb    = new StringBuilder();
        var subPos   = 1;

        foreach (var t in subtitleTracks)
        {
            if (t.Burn)
                subSb.Append($"--burn-subtitle {subPos} ");
            else
                subSb.Append($"--add-subtitle {subPos} ");
            subPos++;
        }

        // ── Assemble command ──────────────────────────────────────────
        sb.Append($"\"{transcodePath}\"");

        if (!string.IsNullOrWhiteSpace(options))
            sb.Append($" {options.Trim()}");

        if (!string.IsNullOrEmpty(outputFormat))
            sb.Append($" {outputFormat.Trim()}");

        if (audioSb.Length > 0)
            sb.Append($" {audioSb.ToString().Trim()}");

        if (subSb.Length > 0)
            sb.Append($" {subSb.ToString().Trim()}");

        sb.Append($" \"{inputFile}\"");

        return sb.ToString();
    }

    // ── Helper: check if indexes are already in ascending order ───────
    // Returns true if the sequence is already sorted ascending.
    // If true, no --track-order argument is needed.
    // e.g. [1, 2, 3] → true (no reorder needed)
    //      [1, 3, 2] → false (user reordered, add --track-order)
    //
    // This is the C# equivalent of IsAlphabaticOrder() in the original,
    // but works on integers directly rather than converting to a char array.
    public static bool IsAscendingOrder(IEnumerable<int> indexes)
    {
        // Convert to list so we can compare adjacent elements
        var list = indexes.ToList();
        for (int i = 1; i < list.Count; i++)
        {
            if (list[i] < list[i - 1]) return false;
        }
        return true;
    }
}
