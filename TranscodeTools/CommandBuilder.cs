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
        ObservableCollection<TranscodeSubtitleTrack> subtitleTracks,
        bool verboseLogging = false)
    {
        // ── Validate audio selection ──────────────────────────────────
        // Block if no audio tracks exist at all (invalid source file)
        // or if the user has deselected every track.
        if (audioTracks.Count == 0)
            throw new InvalidOperationException(
                "No audio tracks found. The source file may be invalid.");

        if (!audioTracks.Any(t => t.IsSelected))
            throw new InvalidOperationException(
                "No audio tracks are selected. Please select at least one audio track.");

        var inputFile  = Path.Combine(inputDirectory, movieFolder, fileName);
        var outputFile = Path.Combine(outputDirectory, movieFolder, fileName);
        var ffmpegPath = AppSettings.Instance.FFmpeg_Path;
        var video      = videoTracks.FirstOrDefault();
        var sb         = new StringBuilder();

        // ── Dolby Vision: copy-only path ──────────────────────────────
        // DoVi RPU data cannot survive NVENC re-encoding — the encoder
        // discards it entirely. The only way to preserve Dolby Vision is
        // to stream-copy the video track. We detect this via the
        // OutputFormat value set by FfprobeService when DoVi is found.
        //
        // The copy path skips hwaccel/cuvid/nvenc entirely and emits
        // a much simpler command: ffmpeg -i input -c:v copy + audio/subs.
        var isDoVi = video != null &&
            video.OutputFormat.Equals("copy (DoVi)", StringComparison.OrdinalIgnoreCase);

        // Build the loglevel/stats flags once — used in both paths below.
        // Verbose mode: use the user-selected log level, no -stats (output
        // goes to log file; Run window shows progress bar instead).
        // Normal mode: -loglevel error -stats (current behaviour).
        var logFlags = verboseLogging
            ? $"-loglevel {AppSettings.Instance.FfmpegLogLevel}"
            : "-loglevel error -stats";

        if (isDoVi)
        {
            sb.Append($"\"{ffmpegPath}\"");
            sb.Append($" -y {logFlags}");
            sb.Append(" -analyzeduration 100M -probesize 100M");
            sb.Append($" -i \"{inputFile}\"");
            sb.Append(" -map 0:v:0 -c:v copy");

            // Audio — selection-aware, sub-row-aware loop (same logic as NVENC path).
            // outIdx tracks the 0-based output audio index for -c:a:N / -b:a:N flags.
            // Sub-rows map the parent's source stream index but encode independently.
            AppendAudioArgs(sb, audioTracks);

            // Subtitles — copy all tracks
            for (int i = 0; i < subtitleTracks.Count; i++)
                sb.Append($" -map 0:s:{i} -c:s:{i} copy");

            sb.Append($" \"{outputFile}\"");
            return sb.ToString();
        }

        // ── Normal NVENC encode path (SDR and HDR10/HLG) ─────────────

        // ── Determine encode codec ────────────────────────────────────
        // User's OutputFormat choice drives this.
        var useHevc     = video == null ||
            !video.OutputFormat.Equals("h.264", StringComparison.OrdinalIgnoreCase);
        var encodeCodec = useHevc ? "hevc_nvenc" : "h264_nvenc";
        var preset      = video?.Preset ?? AppSettings.Instance.DefaultPreset;

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
        sb.Append($" -y {logFlags}");

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
        // Only emit -preset if the preset is not set to "None".
        // "None" lets NVENC choose its own preset automatically.
        if (!preset.Equals("None", StringComparison.OrdinalIgnoreCase))
            sb.Append($" -preset {preset}");

        // Quality flags — sourced from User Preferences (NVENC Quality Flags field).
        // Defaults to confirmed optimal pixel-tested settings:
        //   -cq 19 -spatial-aq 1 -aq-strength 10
        var qualityFlags = AppSettings.Instance.NvencQualityFlags;
        if (!string.IsNullOrWhiteSpace(qualityFlags))
            sb.Append($" {qualityFlags.Trim()}");

        // ── HDR10 / HLG color metadata passthrough ────────────────────
        // When the source has bt2020 primaries (HDR10 or HLG), we must
        // explicitly tag the output stream with the same color metadata.
        // Without these flags, ffmpeg leaves the output untagged and
        // players fall back to SDR tone-mapping on HDR displays.
        //
        // We pass through the exact values read from ffprobe rather than
        // hardcoding, so HLG (arib-std-b67) is handled correctly alongside
        // HDR10 (smpte2084).
        //
        // DoVi is handled above via the copy path — this block is only
        // reached for SDR and HDR10/HLG sources.
        if (!string.IsNullOrWhiteSpace(video?.ColorPrimaries) &&
            video.ColorPrimaries.Equals("bt2020", StringComparison.OrdinalIgnoreCase))
        {
            sb.Append($" -color_primaries {video.ColorPrimaries}");

            if (!string.IsNullOrWhiteSpace(video.ColorTransfer))
                sb.Append($" -color_trc {video.ColorTransfer}");

            if (!string.IsNullOrWhiteSpace(video.ColorSpace))
                sb.Append($" -colorspace {video.ColorSpace}");

            // Static HDR10 SEI: mastering display color volume (SEI 137).
            // Encodes the display the master was graded on — primaries, white
            // point, and peak/floor luminance. Without this, players cannot
            // perform accurate HDR tone-mapping even if color tags are correct.
            if (!string.IsNullOrWhiteSpace(video.MasterDisplay))
                sb.Append($" -master_display \"{video.MasterDisplay}\"");

            // Static HDR10 SEI: content light level (SEI 144).
            // MaxCLL (max content light level) and MaxFALL (max frame-average
            // light level) tell displays the brightest highlights in the content.
            // Stored as "MaxCLL,MaxFALL" — e.g. "1000,400".
            if (!string.IsNullOrWhiteSpace(video.MaxCll))
                sb.Append($" -max_cll \"{video.MaxCll}\"");
        }

        if (needs10bit)
        {
            // -highbitdepth true enables 10-bit output in hevc_nvenc.
            // scale_cuda converts the decoded frames to p010le (10-bit)
            // on the GPU before encoding, keeping the full pipeline on device.
            sb.Append(" -highbitdepth true");
            sb.Append(" -filter:v scale_cuda=format=p010le");
        }

        // ── Audio — per track ─────────────────────────────────────────
        // Selection-aware and sub-row-aware. See AppendAudioArgs below.
        AppendAudioArgs(sb, audioTracks);

        // ── Subtitles — per track ─────────────────────────────────────
        // All subtitle tracks are copied. Burn via overlay_cuda is backlog.
        for (int i = 0; i < subtitleTracks.Count; i++)
        {
            sb.Append($" -map 0:s:{i} -c:s:{i} copy");
        }

        sb.Append($" \"{outputFile}\"");

        return sb.ToString();
    }

    // ── AppendAudioArgs ───────────────────────────────────────────────
    // Builds the -map / -c:a / -b:a arguments for all audio tracks,
    // respecting IsSelected and sub-row parent mapping.
    //
    // Key rules:
    //   - Tracks with IsSelected = false are skipped entirely (not mapped).
    //   - Normal parent rows map their own OriginalTrackIndex as the source.
    //   - Sub-rows (IsSubRow = true) map their ParentTrackIndex as the source
    //     (same lossless stream) but encode with their own Format/BitRate.
    //   - outIdx is the 0-based OUTPUT index used for -c:a:N and -b:a:N.
    //     It only increments for tracks that are actually included.
    //   - ffmpeg -map 0:a:N uses the 0-based index WITHIN all audio streams
    //     in the file. We convert OriginalTrackIndex (which is the overall
    //     stream index including video) to the audio-relative index by
    //     tracking the position of each audio stream in the collection.
    //
    // Example with 3 source audio streams (stream indexes 1, 2, 3):
    //   Track 0 (stream 1, DTS-HD MA, selected, lossless) → -map 0:a:0 copy
    //   SubRow  (stream 1, eac3 derived, selected)        → -map 0:a:0 eac3
    //   Track 1 (stream 2, DTS, NOT selected)             → skipped
    //   Track 2 (stream 3, AC3, selected)                 → -map 0:a:2 copy
    //                                                        (source index 2, outIdx 2)
    private static void AppendAudioArgs(
        StringBuilder sb,
        ObservableCollection<TranscodeAudioTrack> audioTracks)
    {
        // Build a lookup: OriginalTrackIndex → audio-relative source index.
        // We need this because ffmpeg -map 0:a:N counts only audio streams,
        // but OriginalTrackIndex is the overall ffprobe stream index which
        // includes video (stream 0 is usually video, stream 1 is first audio).
        // We derive the audio-relative index by collecting all unique parent
        // OriginalTrackIndexes in collection order (sub-rows share their parent's).
        var sourceIndexMap = new Dictionary<int, int>();
        int audioRelIdx = 0;
        foreach (var t in audioTracks.Where(t => !t.IsSubRow))
        {
            sourceIndexMap[t.OriginalTrackIndex] = audioRelIdx++;
        }

        int outIdx = 0;
        foreach (var t in audioTracks)
        {
            if (!t.IsSelected) continue;

            // Determine which source audio stream to map.
            // Sub-rows use their parent's stream; parent rows use their own.
            var sourceTrackIdx = t.IsSubRow ? t.ParentTrackIndex : t.OriginalTrackIndex;

            if (!sourceIndexMap.TryGetValue(sourceTrackIdx, out var srcAudioIdx))
                continue;   // safety: skip if index not found

            var format = t.Format.ToLowerInvariant();

            sb.Append($" -map 0:a:{srcAudioIdx}");

            if (format == "keep" || string.IsNullOrWhiteSpace(format))
            {
                sb.Append($" -c:a:{outIdx} copy");
            }
            else
            {
                sb.Append($" -c:a:{outIdx} {format}");

                var br = t.BitRate;
                if (!string.IsNullOrWhiteSpace(br) &&
                    !br.Equals("Keep", StringComparison.OrdinalIgnoreCase))
                {
                    sb.Append($" -b:a:{outIdx} {br}k");
                }

                // ── Width / channel downmix ───────────────────────────
                // Width = "5.1"    → -ac:a:N 6   (ffmpeg outputs 6 channels)
                // Width = "Stereo" → -ac:a:N 2   (ffmpeg outputs 2 channels)
                //                    -af:a:N aresample=matrix_encoding=dplii
                //                    (Pro Logic II encode preserves surround
                //                     information in the stereo downmix —
                //                     much better than a plain fold-down)
                // Width = "Keep"   → no -ac or -af flags (source layout kept)
                //
                // Note: ffmpeg handles the actual downmix automatically when
                // -ac sets a lower channel count than the source. The aresample
                // filter is layered on top only for the Stereo case to improve
                // downmix quality. 7.1 → 5.1 via -ac 6 is also automatic and
                // clean — ffmpeg folds the side channels into surrounds with
                // proper level compensation.
                var width = t.Width;
                if (width.Equals("5.1", StringComparison.OrdinalIgnoreCase))
                {
                    sb.Append($" -ac:a:{outIdx} 6");
                }
                else if (width.Equals("Stereo", StringComparison.OrdinalIgnoreCase))
                {
                    sb.Append($" -ac:a:{outIdx} 2");
                    sb.Append($" -af:a:{outIdx} aresample=matrix_encoding=dplii");
                }
            }

            outIdx++;
        }
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
