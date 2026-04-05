// ============================================================
// CommandBuilder.cs
// ------------------------------------------------------------
// Builds the mkvmerge command (Remux) or ffmpeg command (Transcode)
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
        else
        {
            // No subtitles selected — explicitly tell mkvmerge to drop them all.
            // Without this flag, mkvmerge includes all subtitle tracks by default.
            sb.Append(" --no-subtitles");
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
    // Builds a direct ffmpeg command using the NVIDIA CUDA hardware pipeline.
    //
    // Pipeline:
    //   -hwaccel cuda -hwaccel_output_format cuda   keep pipeline on GPU
    //   -c:v <cuvid decoder>                         hardware decode (app chooses)
    //   -i "input.mkv"
    //   -map 0:v:0 -c:v hevc_nvenc / h264_nvenc     hardware encode (user chooses)
    //   -preset p1—–p7                               quality/speed (user chooses)
    //   -highbitdepth true                           only when: output=hevc, source is 8-bit
    //   -filter:v scale_cuda=format=p010le           only when: output=hevc, source is 8-bit
    //   -map 0:a:N -c:a copy / eac3 / ac3           per audio track (user chooses)
    //   -b:a:N <bitrate>k                            when encoding audio (user chooses)
    //   -map 0:s:N -c:s copy                         per subtitle track
    //   "output.mkv"
    //
    // Note: -colorspace:v bt709 is intentionally omitted — breaks the CUDA pipeline.
    //
    // Throws InvalidOperationException if no audio tracks exist.
    public static string BuildTranscodeCommand(
        string outputDirectory,
        string movieFolder,
        string fileName,
        string inputDirectory,
        ObservableCollection<TranscodeVideoTrack> videoTracks,
        ObservableCollection<TranscodeAudioTrack> audioTracks,
        ObservableCollection<TranscodeSubtitleTrack> subtitleTracks)
    {
        if (audioTracks.Count == 0)
            throw new InvalidOperationException(
                "No audio tracks found. The source file may be invalid.");

        var inputFile  = Path.Combine(inputDirectory, movieFolder, fileName);
        var outputFile = Path.Combine(outputDirectory, movieFolder, fileName);
        var ffmpegPath = AppSettings.Instance.FFmpeg_Path;
        var video      = videoTracks.FirstOrDefault();
        var sb         = new StringBuilder();

        // ── Determine encode codec ────────────────────────────────────
        // User's OutputFormat choice drives this.
        var useHevc     = video == null ||
            !video.OutputFormat.Equals("h.264", StringComparison.OrdinalIgnoreCase);
        var encodeCodec = useHevc ? "hevc_nvenc" : "h264_nvenc";
        var preset      = video?.Preset ?? "p5";

        // ── Determine hardware decoder ────────────────────────────────
        // App maps source codec_name to the appropriate NVDEC decoder.
        var sourceCodec = video?.CodecName?.ToLowerInvariant() ?? "";
        var hwDecoder = sourceCodec switch
        {
            "h264"        => "h264_cuvid",
            "hevc"        => "hevc_cuvid",
            "mpeg2video"  => "mpeg2_cuvid",
            "vc1"         => "vc1_cuvid",
            "vp8"         => "vp8_cuvid",
            "vp9"         => "vp9_cuvid",
            "av1"         => "av1_cuvid",
            _             => "h264_cuvid"   // fallback — flagged for future revisit
        };

        // ── Determine whether 10-bit conversion is needed ─────────────
        // Only relevant when output is hevc. If the source pixel format
        // already contains "10" (e.g. yuv420p10le) it is already 10-bit
        // and no conversion is needed. 8-bit sources (e.g. yuv420p) need
        // -highbitdepth true and scale_cuda to convert to p010le.
        var sourcePixFmt  = video?.PixelFormat?.ToLowerInvariant() ?? "";
        var sourceIs10bit = sourcePixFmt.Contains("10");
        var needs10bit    = useHevc && !sourceIs10bit;

        // ── Assemble command ──────────────────────────────────────────
        sb.Append($"\"{ffmpegPath}\"");

        // -y: overwrite output without prompting — required for unattended batch runs
        // -loglevel error -stats: suppress informational noise, keep progress line
        sb.Append(" -y -loglevel error -stats");

        // -analyzeduration / -probesize: increases the amount of data ffmpeg reads
        // before starting. Required for PGS subtitle streams in MKV where the
        // dimensions are not declared in the container header (stream 4 in this case).
        // Only affects startup analysis time, not encode speed.
        sb.Append(" -analyzeduration 100M -probesize 100M");

        // Hardware acceleration flags and decoder must come before -i
        sb.Append(" -hwaccel cuda -hwaccel_output_format cuda");
        sb.Append($" -c:v {hwDecoder}");

        sb.Append($" -i \"{inputFile}\"");

        // Video encode
        sb.Append(" -map 0:v:0");
        sb.Append($" -c:v {encodeCodec}");
        sb.Append($" -preset {preset}");

        // Quality flags — confirmed optimal via pixel-level testing on RTX 3060:
        //   -cq 19          constant quality mode; cq16/17 indistinguishable from cq19
        //   -spatial-aq 1   enable spatial adaptive quantisation
        //   -aq-strength 10 maximum AQ strength — sweet spot for detail retention
        sb.Append(" -cq 19 -spatial-aq 1 -aq-strength 10");

        if (needs10bit)
        {
            // -highbitdepth true enables 10-bit output in hevc_nvenc.
            // scale_cuda converts the decoded frames to p010le (10-bit)
            // on the GPU before encoding, keeping the full pipeline on device.
            sb.Append(" -highbitdepth true");
            sb.Append(" -filter:v scale_cuda=format=p010le");
        }

        // ── Audio — per track ─────────────────────────────────────────
        // ffmpeg -map 0:a:N uses 0-based audio-relative index.
        // We iterate all tracks; the user's Format dropdown determines
        // copy vs encode. BitRate is used when encoding.
        for (int i = 0; i < audioTracks.Count; i++)
        {
            var t      = audioTracks[i];
            var format = t.Format.ToLowerInvariant();

            sb.Append($" -map 0:a:{i}");

            if (format == "keep" || string.IsNullOrWhiteSpace(format))
            {
                sb.Append($" -c:a:{i} copy");
            }
            else
            {
                // User has chosen a target codec (eac3 or ac3)
                sb.Append($" -c:a:{i} {format}");

                // Append bitrate if the user chose one
                var br = t.BitRate;
                if (!string.IsNullOrWhiteSpace(br) &&
                    !br.Equals("Keep", StringComparison.OrdinalIgnoreCase))
                {
                    sb.Append($" -b:a:{i} {br}k");
                }
            }
        }

        // ── Subtitles — per track ─────────────────────────────────────
        // All subtitle tracks are copied. Burn via overlay_cuda is backlog.
        for (int i = 0; i < subtitleTracks.Count; i++)
        {
            sb.Append($" -map 0:s:{i} -c:s:{i} copy");
        }

        sb.Append($" \"{outputFile}\"");

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
