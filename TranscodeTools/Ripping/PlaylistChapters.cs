// ============================================================
// PlaylistChapters.cs
// ------------------------------------------------------------
// A Blu-ray playlist's own chapter count, for the info stage's
// chapter check (MakeMkvInfo): `mkvmerge -J <playlist.mpls>` reads
// the .mpls and reports container.properties.playlist_chapters.
//
// MEASURED (2026-11-18, BLUES_BROTHERS_DOM): mkvmerge gives 18
// chapters for 00800.mpls and 00801.mpls, the same as MakeMKV's
// field 8, and the same clips in the same order as its field 26.
//
// mkvmerge is the one Remux already runs (Preferences, MKVMerge_Path).
// ============================================================

using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace TranscodeTools;

public static class PlaylistChapters
{
    // The chapter count mkvmerge reports for the playlist at `mplsPath`.
    // Throws MakeMkvException (the info stage's failure type) when
    // mkvmerge can't be run or its answer has no chapter count.
    public static async Task<int> ReadAsync(string mkvMergePath, string mplsPath, CancellationToken token)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = mkvMergePath,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        psi.ArgumentList.Add("-J");
        psi.ArgumentList.Add(mplsPath);

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new MakeMkvException($"mkvmerge could not be started from {mkvMergePath}: {ex.Message}");
        }

        var output = process.StandardOutput.ReadToEndAsync(token);
        var errors = process.StandardError.ReadToEndAsync(token);
        try
        {
            await process.WaitForExitAsync(token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }

        // mkvmerge -J exits 0, or 1 for warnings, with the JSON on stdout.
        var json = await output;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("container", out var container) &&
                container.TryGetProperty("properties", out var properties) &&
                properties.TryGetProperty("playlist_chapters", out var chapters))
                return chapters.GetInt32();
        }
        catch (JsonException) { }

        var detail = (await errors).Trim();
        throw new MakeMkvException(
            $"mkvmerge gave no chapter count for {Path.GetFileName(mplsPath)} (exit {process.ExitCode})" +
            (detail.Length > 0 ? $": {detail}" : "."));
    }
}
