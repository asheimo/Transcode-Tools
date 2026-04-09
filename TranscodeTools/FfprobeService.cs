// ============================================================
// FfprobeService.cs
// ------------------------------------------------------------
// Runs ffprobe against a media file and parses the JSON output
// into the six track model types used by the UI.
//
// ffprobe is part of the FFmpeg toolset and can report detailed
// information about every stream (track) in a media file.
// We ask it for JSON output, which is easy to parse in .NET 8
// using System.Text.Json (the same library used in AppSettings).
//
// WHY A SEPARATE CLASS?
// Keeping all ffprobe logic here means MainWindow.xaml.cs only
// needs to call one method and hand back the results — the same
// separation-of-concerns pattern used in the original VB.NET app
// where ffprobe logic lived in a dedicated module.
// ============================================================

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TranscodeTools;

// "static" class — no instances are created. All methods are called
// directly on the class, e.g. FfprobeService.ProbeFileAsync(...)
// In VB.NET: Module FfprobeService
public static class FfprobeService
{
    // ── Public result type ────────────────────────────────────────────
    // A simple container that holds all six track lists returned from
    // one ffprobe call. Using a record means we get equality, ToString,
    // and immutability for free — good for a data-only return type.
    // In VB.NET: a Structure or Class with six List properties.
    public record ProbeResult(
        List<RemuxVideoTrack>        RemuxVideo,
        List<RemuxAudioTrack>        RemuxAudio,
        List<RemuxSubtitleTrack>     RemuxSubtitle,
        List<TranscodeVideoTrack>    TranscodeVideo,
        List<TranscodeAudioTrack>    TranscodeAudio,
        List<TranscodeSubtitleTrack> TranscodeSubtitle,
        string                       Duration
    );

    // ── Main entry point ──────────────────────────────────────────────
    // Runs ffprobe on the given file and returns populated track lists.
    //
    // "async Task<T>" is the C# equivalent of an Async Function in VB.NET
    // that returns a value. "await" lets other UI work happen while ffprobe
    // runs — the window won't freeze.
    //
    // Throws InvalidOperationException if ffprobe path is not configured.
    // Throws Exception (from Process or JSON) if something goes wrong.
    public static async Task<ProbeResult> ProbeFileAsync(string filePath)
    {
        var ffprobePath = AppSettings.Instance.FFprobe_Path;

        if (string.IsNullOrWhiteSpace(ffprobePath))
            throw new InvalidOperationException(
                "FFprobe path is not set.\nPlease configure it in Preferences.");

        // ── Build the ffprobe command ─────────────────────────────────
        // Arguments explained:
        //   -v quiet            — suppress all log output except our result
        //   -print_format json  — output as JSON (easy to parse)
        //   -show_streams       — include one entry per stream (track)
        //   -show_format        — include container-level info (not used yet
        //                         but useful later for Step 6 processing)
        //   "<filePath>"        — the file to probe
        var args = $"-v quiet -print_format json -show_streams -show_format \"{filePath}\"";

        // ── Run ffprobe and capture output ────────────────────────────
        // ProcessStartInfo configures how to launch the external process.
        // RedirectStandardOutput lets us read its stdout in code.
        // UseShellExecute must be false to redirect streams.
        // CreateNoWindow stops a console window flashing on screen.
        var psi = new ProcessStartInfo
        {
            FileName               = ffprobePath,
            Arguments              = args,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        // ReadToEndAsync reads all stdout without blocking the UI thread.
        // We read stderr too so we can report it if something goes wrong.
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();

        // WaitForExitAsync is the async version of WaitForExit().
        // The CancellationToken.None means we don't support cancellation
        // for now — we'll add that in Step 6 (the run processing window).
        await process.WaitForExitAsync(CancellationToken.None);

        if (process.ExitCode != 0)
            throw new Exception(
                $"ffprobe exited with code {process.ExitCode}.\n{stderr}".Trim());

        if (string.IsNullOrWhiteSpace(stdout))
            throw new Exception("ffprobe returned no output. Check the file path.");

        // ── Parse the JSON output ─────────────────────────────────────
        return ParseProbeOutput(stdout);
    }

    // ── JSON parser ───────────────────────────────────────────────────
    // Converts the raw ffprobe JSON string into six typed track lists.
    //
    // We use JsonNode (the dynamic/tree-based API) rather than a rigid
    // deserialise-to-class approach, because ffprobe JSON structure varies
    // between file types and we only need specific fields.
    // This is similar to using a Dictionary in VB.NET to read JSON
    // without pre-defining every possible key.
    private static ProbeResult ParseProbeOutput(string json)
    {
        var remuxVideo      = new List<RemuxVideoTrack>();
        var remuxAudio      = new List<RemuxAudioTrack>();
        var remuxSubtitle   = new List<RemuxSubtitleTrack>();
        var transcodeVideo  = new List<TranscodeVideoTrack>();
        var transcodeAudio  = new List<TranscodeAudioTrack>();
        var transcodeSubtitle = new List<TranscodeSubtitleTrack>();

        // JsonNode.Parse returns a nullable node — the ! asserts it's not null.
        // If the JSON is malformed this will throw, which is fine — the caller
        // will catch it and show an error message.
        var root    = JsonNode.Parse(json)!;
        var streams = root["streams"]?.AsArray();

        if (streams == null) return new ProbeResult(
            remuxVideo, remuxAudio, remuxSubtitle,
            transcodeVideo, transcodeAudio, transcodeSubtitle,
            "");

        foreach (var stream in streams)
        {
            if (stream == null) continue;

            // "codec_type" is the key field: "video", "audio", or "subtitle"
            var codecType = stream["codec_type"]?.GetValue<string>() ?? "";

            // stream_index is the raw ffprobe index used for mkvmerge/ffmpeg args
            var streamIndex = stream["index"]?.GetValue<int>() ?? 0;

            switch (codecType)
            {
                case "video":
                    remuxVideo.Add(ParseRemuxVideo(stream, streamIndex));
                    transcodeVideo.Add(ParseTranscodeVideo(stream, streamIndex));
                    break;

                case "audio":
                    remuxAudio.Add(ParseRemuxAudio(stream, streamIndex));
                    transcodeAudio.Add(ParseTranscodeAudio(stream, streamIndex));
                    break;

                case "subtitle":
                    remuxSubtitle.Add(ParseRemuxSubtitle(stream, streamIndex));
                    transcodeSubtitle.Add(ParseTranscodeSubtitle(stream, streamIndex));
                    break;

                // Data/attachment streams (e.g. fonts embedded in MKV) are skipped
            }
        }

        return new ProbeResult(
            remuxVideo, remuxAudio, remuxSubtitle,
            transcodeVideo, transcodeAudio, transcodeSubtitle,
            ParseDuration(root));
    }

    // ── Per-track parsers ─────────────────────────────────────────────
    // Each method reads the relevant fields from one ffprobe stream node.
    // Helper methods at the bottom handle the awkward bits (FPS, bitrate, etc.)

    private static RemuxVideoTrack ParseRemuxVideo(JsonNode s, int index)
    {
        // "disposition" holds flags like default/forced set in the container
        var disposition = s["disposition"];

        return new RemuxVideoTrack
        {
            OriginalTrackIndex = index,
            VideoFormat        = s["codec_name"]?.GetValue<string>() ?? "",
            Resolution         = BuildResolution(s),
            Fps                = BuildFps(s),
            IsDefault          = disposition?["default"]?.GetValue<int>() == 1
        };
    }

    private static TranscodeVideoTrack ParseTranscodeVideo(JsonNode s, int index)
    {
        var codec      = s["codec_name"]?.GetValue<string>() ?? "";
        var pixFmt     = s["pix_fmt"]?.GetValue<string>()    ?? "";
        var resolution = BuildResolution(s);
        var fps        = BuildFps(s);

        // field_order: "tt" (top-first) or "bb" (bottom-first) are true interlaced.
        // "tb" and "bt" are mixed — also treated as interlaced for safety.
        // "progressive" or absent means no deinterlacing needed.
        var fieldOrder   = s["field_order"]?.GetValue<string>() ?? "";
        var isInterlaced = fieldOrder.Equals("tt", StringComparison.OrdinalIgnoreCase) ||
                           fieldOrder.Equals("bb", StringComparison.OrdinalIgnoreCase) ||
                           fieldOrder.Equals("tb", StringComparison.OrdinalIgnoreCase) ||
                           fieldOrder.Equals("bt", StringComparison.OrdinalIgnoreCase);

        // Map the raw resolution (e.g. "1920x1080") to the nearest dropdown item.
        // We extract the height and match it to a known "p" value.
        // If the height doesn't match a known item, fall back to "Keep".
        var height          = s["height"]?.GetValue<int>() ?? 0;
        var resolutionItem  = height switch
        {
            480  => "480p",
            720  => "720p",
            1080 => "1080p",
            2160 => "2160p",
            _    => "Keep"
        };

        // ── HDR color metadata ────────────────────────────────────────
        // ffprobe exposes color_primaries, color_transfer, color_space as
        // top-level stream fields. These are passed through verbatim to
        // ffmpeg's color flags so HDR10/HLG metadata survives the encode.
        var colorPrimaries = s["color_primaries"]?.GetValue<string>() ?? "";
        var colorTransfer  = s["color_transfer"]?.GetValue<string>()  ?? "";
        var colorSpace     = s["color_space"]?.GetValue<string>()     ?? "";

        // ── Dolby Vision detection ────────────────────────────────────
        // DoVi RPU data appears as a side_data_list entry whose
        // side_data_type is "DOVI configuration record".
        // If found, the stream must be copied — NVENC cannot preserve the RPU.
        var hasDoVi      = false;
        var hasHdr10Plus  = false;
        var masterDisplay = "";
        var maxCll        = "";
        var sideDataList = s["side_data_list"]?.AsArray();
        if (sideDataList != null)
        {
            foreach (var entry in sideDataList)
            {
                if (entry == null) continue;
                var sideType = entry["side_data_type"]?.GetValue<string>() ?? "";

                if (sideType.Equals("DOVI configuration record",
                        StringComparison.OrdinalIgnoreCase))
                {
                    hasDoVi = true;
                }
                else if (sideType.Equals("HDR Dynamic Metadata",
                        StringComparison.OrdinalIgnoreCase))
                {
                    // HDR10+ dynamic per-frame metadata detected.
                    // Passthrough is shelved pending external tool pipeline.
                    // Flag only — no functional effect on the encode command.
                    hasHdr10Plus = true;
                }
                else if (sideType.Equals("Mastering display metadata",
                        StringComparison.OrdinalIgnoreCase))
                {
                    masterDisplay = BuildMasterDisplay(entry);
                }
                else if (sideType.Equals("Content light level metadata",
                        StringComparison.OrdinalIgnoreCase))
                {
                    var maxContent = entry["max_content"]?.GetValue<int>() ?? 0;
                    var maxAverage = entry["max_average"]?.GetValue<int>() ?? 0;
                    maxCll = $"{maxContent},{maxAverage}";
                }
            }
        }

        // TrackInfo label — append [DoVi] so the user can see it in the table.
        // Interlaced is indicated via the dedicated Interlaced column, not TrackInfo.
        var trackInfo = hasDoVi
            ? $"Video: {codec} {resolution} @ {fps} [DoVi]"
            :  $"Video: {codec} {resolution} @ {fps}";

        // OutputFormat — locked to "copy (DoVi)" when DoVi is present;
        // otherwise default to hevc_nvenc.
        var outputFormat = hasDoVi ? "copy (DoVi)" : "hevc (default)";

        return new TranscodeVideoTrack
        {
            OriginalTrackIndex = index,
            CodecName      = codec,
            PixelFormat    = pixFmt,
            ColorPrimaries = colorPrimaries,
            ColorTransfer  = colorTransfer,
            ColorSpace     = colorSpace,
            MasterDisplay  = masterDisplay,
            MaxCll         = maxCll,
            HasDoVi        = hasDoVi,
            IsInterlaced   = isInterlaced,
            HasHdr10Plus   = hasHdr10Plus,
            TrackInfo      = trackInfo,
            Resolution     = resolutionItem,
            OutputFormat   = outputFormat,
            FrameRate      = fps,
            Preset         = AppSettings.Instance.DefaultPreset
        };
    }

    private static RemuxAudioTrack ParseRemuxAudio(JsonNode s, int index)
    {
        var disposition = s["disposition"];
        var tags        = s["tags"];

        return new RemuxAudioTrack
        {
            OriginalTrackIndex = index,
            AudioFormat        = BuildAudioFormatLabel(s),
            Width              = BuildChannelLayout(s),
            BitRate            = BuildBitRate(s),
            Language           = tags?["language"]?.GetValue<string>() ?? "",
            Title              = tags?["title"]?.GetValue<string>()    ?? "",
            IsDefault          = disposition?["default"]?.GetValue<int>() == 1,
            IsSelected         = false   // user has to select which they want
        };
    }

    private static TranscodeAudioTrack ParseTranscodeAudio(JsonNode s, int index)
    {
        var tags     = s["tags"];
        var codec    = s["codec_name"]?.GetValue<string>() ?? "";
        var profile  = s["profile"]?.GetValue<string>()    ?? "";
        var format   = BuildAudioFormatLabel(s);
        var channels = BuildChannelLayout(s);
        var bitrate  = BuildBitRate(s);
        var lang     = tags?["language"]?.GetValue<string>() ?? "";

        // ── Source channel count ──────────────────────────────────────
        // Read the raw integer channel count (e.g. 2, 6, 8) for use by
        // AvailableWidths — determines whether 5.1 and/or Stereo downmix
        // options are offered. "channels" in the JSON is always an integer.
        var sourceChannels = s["channels"]?.GetValue<int>() ?? 0;

        // ── Source bitrate ────────────────────────────────────────────
        // Used by AvailableBitRates to enforce the lossy source ceiling.
        // BuildBitRate already parsed this to a Kbps string; re-parse as
        // int here so the model can do numeric comparisons. 0 = unknown/
        // lossless (treated as uncapped in AvailableBitRates).
        int.TryParse(bitrate, out var sourceBitRateKbps);

        // ── Lossless detection ────────────────────────────────────────
        // Determines whether the + expander is shown in the UI and whether
        // the Format/BitRate dropdowns are locked on the parent row.
        // TrueHD (with or without Atmos), DTS-HD MA, DTS:X, FLAC, and all
        // PCM variants are considered lossless. All other codecs are lossy.
        var isLossless = codec.Equals("truehd", StringComparison.OrdinalIgnoreCase)
            || codec.Equals("flac", StringComparison.OrdinalIgnoreCase)
            || codec.StartsWith("pcm_", StringComparison.OrdinalIgnoreCase)
            || (codec.Equals("dts", StringComparison.OrdinalIgnoreCase) &&
                (profile.Contains("DTS-HD MA", StringComparison.OrdinalIgnoreCase) ||
                 profile.Contains("DTS:X",     StringComparison.OrdinalIgnoreCase)));

        return new TranscodeAudioTrack
        {
            OriginalTrackIndex  = index,
            IsLossless          = isLossless,
            SourceChannels      = sourceChannels,
            // Lossless tracks report no meaningful bitrate — leave 0 so
            // AvailableBitRates treats them as uncapped.
            SourceBitRateKbps   = isLossless ? 0 : sourceBitRateKbps,
            TrackInfo           = $"Audio: {format} {channels} {bitrate}Kbps [{lang}]".Trim(),
            // All three dropdowns default to "Keep" — meaning copy without re-encoding.
            // The user changes these only if they want to transcode a specific track.
            Format    = "Keep",
            Width     = "Keep",
            BitRate   = "Keep"
        };
    }

    private static RemuxSubtitleTrack ParseRemuxSubtitle(JsonNode s, int index)
    {
        var disposition = s["disposition"];
        var tags        = s["tags"];

        return new RemuxSubtitleTrack
        {
            OriginalTrackIndex = index,
            SubtitleFormat     = s["codec_name"]?.GetValue<string>() ?? "",
            Language           = tags?["language"]?.GetValue<string>() ?? "",
            // Frame count is stored as a MakeMKV tag, not a top-level field.
            // Try "NUMBER_OF_FRAMES-eng" first, then "NUMBER_OF_FRAMES" without
            // the language suffix — matching exactly what the original app did.
            FrameCount         = GetNumberOfFrames(s),
            IsDefault          = disposition?["default"]?.GetValue<int>() == 1,
            IsForced           = disposition?["forced"]?.GetValue<int>()  == 1,
            IsSelected         = false   // user has to select which they want
        };
    }

    private static TranscodeSubtitleTrack ParseTranscodeSubtitle(JsonNode s, int index)
    {
        var disposition = s["disposition"];
        var tags        = s["tags"];
        var codec       = s["codec_name"]?.GetValue<string>() ?? "";
        var lang        = tags?["language"]?.GetValue<string>() ?? "";

        return new TranscodeSubtitleTrack
        {
            OriginalTrackIndex = index,
            TrackInfo  = $"Subtitle: {codec} [{lang}]".Trim(),
            FrameCount = GetNumberOfFrames(s),
            IsDefault  = disposition?["default"]?.GetValue<int>() == 1,
            IsForced   = disposition?["forced"]?.GetValue<int>()  == 1,
            Burn       = false   // user sets this manually
        };
    }

    // ── Field builder helpers ─────────────────────────────────────────

    // Reads the subtitle frame count from MakeMKV tags.
    // Tries "NUMBER_OF_FRAMES-eng" first, then "NUMBER_OF_FRAMES" without
    // the language suffix, matching the original VB.NET app's behaviour.
    // If neither is present, returns an empty string.
    private static string GetNumberOfFrames(JsonNode s)
    {
        var tags = s["tags"];
        if (tags == null) return "";

        // Try with language suffix first
        var withSuffix = tags["NUMBER_OF_FRAMES-eng"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(withSuffix)) return withSuffix;

        // Fall back to without suffix
        return tags["NUMBER_OF_FRAMES"]?.GetValue<string>() ?? "";
    }

    // Builds "1920x1080" from width/height fields.
    // Returns empty string if either dimension is missing.
    private static string BuildResolution(JsonNode s)
    {
        var w = s["width"]?.GetValue<int>();
        var h = s["height"]?.GetValue<int>();
        return (w.HasValue && h.HasValue) ? $"{w}x{h}" : "";
    }

    // Builds a human-readable FPS string from avg_frame_rate.
    // ffprobe reports FPS as a fraction string e.g. "24000/1001".
    // We keep the fraction form to match the original app display.
    // If the fraction reduces to a clean integer (e.g. "25/1"), we
    // simplify it to just "25".
    private static string BuildFps(JsonNode s)
    {
        var raw = s["avg_frame_rate"]?.GetValue<string>() ?? "";
        if (string.IsNullOrEmpty(raw) || raw == "0/0") return "";

        // Try to simplify "25/1" → "25"
        var parts = raw.Split('/');
        if (parts.Length == 2 &&
            int.TryParse(parts[0], out var num) &&
            int.TryParse(parts[1], out var den) &&
            den != 0 && num % den == 0)
        {
            return (num / den).ToString();
        }

        return raw;
    }

    // Builds the audio format label. For DTS variants, ffprobe reports
    // "dts" as codec_name and the profile (DTS-HD MA, DTS:X) in
    // "profile". We combine them to match what the original app showed.
    private static string BuildAudioFormatLabel(JsonNode s)
    {
        var codec   = s["codec_name"]?.GetValue<string>() ?? "";
        var profile = s["profile"]?.GetValue<string>()   ?? "";

        // Only append profile for DTS since other codecs don't use it
        // in a way the original app showed. E.g. TrueHD doesn't need it.
        if (!string.IsNullOrEmpty(profile) &&
            codec.Equals("dts", StringComparison.OrdinalIgnoreCase))
        {
            return $"{codec} ({profile})";
        }

        return codec;
    }

    // Builds the channel layout string e.g. "5.1(side)", "7.1", "stereo".
    // ffprobe stores this in "channel_layout".
    private static string BuildChannelLayout(JsonNode s)
        => s["channel_layout"]?.GetValue<string>() ?? "";

    // Builds a Kbps bitrate string from the "bit_rate" field.
    // ffprobe reports bitrate in bits per second as a string (e.g. "3886080").
    // We convert to Kbps and round, matching the original app's display.
    private static string BuildBitRate(JsonNode s)
    {
        var raw = s["bit_rate"]?.GetValue<string>() ?? "";
        if (string.IsNullOrEmpty(raw)) return "";

        if (long.TryParse(raw, out var bps))
            return (bps / 1000).ToString();

        return "";
    }

    // Builds the mastering display string in the format ffmpeg's -master_display
    // flag expects: G(x,y)B(x,y)R(x,y)WP(x,y)L(max,min)
    //
    // ffprobe reports the chromaticity coordinates as rational strings
    // (e.g. "17000/50000") and luminance as rational strings too
    // (e.g. "10000000/10000" for peak luminance).
    // ffmpeg wants the raw numerator values when the denominator is 50000
    // for chromaticity, and luminance as integers (nits * 10000 for max,
    // nits * 10000 for min — i.e. the raw ffprobe numerator directly).
    //
    // Returns an empty string if any required field is missing.
    private static string BuildMasterDisplay(JsonNode entry)
    {
        try
        {
            // Each coordinate is stored as a fraction string "num/den".
            // We pass the raw rational string directly — ffmpeg accepts
            // "G(17000/50000,17000/50000)..." as well as integer forms.
            var rx = entry["red_x"]?.GetValue<string>()         ?? "";
            var ry = entry["red_y"]?.GetValue<string>()         ?? "";
            var gx = entry["green_x"]?.GetValue<string>()       ?? "";
            var gy = entry["green_y"]?.GetValue<string>()       ?? "";
            var bx = entry["blue_x"]?.GetValue<string>()        ?? "";
            var by = entry["blue_y"]?.GetValue<string>()        ?? "";
            var wx = entry["white_point_x"]?.GetValue<string>() ?? "";
            var wy = entry["white_point_y"]?.GetValue<string>() ?? "";
            var lmax = entry["max_luminance"]?.GetValue<string>() ?? "";
            var lmin = entry["min_luminance"]?.GetValue<string>() ?? "";

            // If any field is missing, don't emit a partial -master_display
            if (string.IsNullOrEmpty(rx) || string.IsNullOrEmpty(ry) ||
                string.IsNullOrEmpty(gx) || string.IsNullOrEmpty(gy) ||
                string.IsNullOrEmpty(bx) || string.IsNullOrEmpty(by) ||
                string.IsNullOrEmpty(wx) || string.IsNullOrEmpty(wy) ||
                string.IsNullOrEmpty(lmax) || string.IsNullOrEmpty(lmin))
                return "";

            return $"G({gx},{gy})B({bx},{by})R({rx},{ry})WP({wx},{wy})L({lmax},{lmin})";
        }
        catch
        {
            // If anything goes wrong parsing the entry, skip gracefully
            return "";
        }
    }

    // Reads the container duration from the format node and returns it as
    // h:mm:ss (e.g. "1:54:23"). ffprobe stores it as a decimal seconds string
    // in root["format"]["duration"]. Returns an empty string if not present.
    private static string ParseDuration(JsonNode root)
    {
        var raw = root["format"]?["duration"]?.GetValue<string>() ?? "";
        if (string.IsNullOrEmpty(raw)) return "";

        if (!double.TryParse(raw, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var totalSeconds))
            return "";

        var ts = TimeSpan.FromSeconds(totalSeconds);
        // Format as h:mm:ss — hours are not zero-padded, minutes and seconds are.
        return $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}";
    }
}
