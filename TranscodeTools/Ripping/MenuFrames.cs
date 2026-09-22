// ============================================================
// MenuFrames.cs
// ------------------------------------------------------------
// Seam 4: one still picture per menu screen worth showing, saved in
// the disc's Menu\ folder as <IFO>_<menu>_pgc<N>_cell<N>.png (the
// oracle's names) and recorded as the screen's Image.
//
// A port of the oracle's image step (keep_screen + grab_menu_image,
// tools\oracle\dvdmenumap.py):
//   - A screen is pictured unless every button on it provably leads
//     to a menu or an exit. A screen with no buttons is not pictured.
//     Anything unresolved, a resume or a loop keeps the picture: an
//     odd disc must cost extra pictures, never a missing one.
//   - The cell's first 3,000 sectors (about 6 MB) are cut out of the
//     menu VOB into a temporary file beside the pictures, and ffmpeg
//     takes one frame at 0.7 s, or at 0 s if that fails. A still only
//     needs the start of the cell, which is what the limit is for.
//   - Screens sharing the same sectors share one picture.
//
// Reads the mounted backup ISO, so VTS menus render: their VOBs are
// decrypted there, where off the drive they are CSS-scrambled.
// The per-button naming sheets the oracle drew are not made: the view
// draws the button outlines live over the picture.
// ============================================================

using System.Diagnostics;
using System.IO;

namespace TranscodeTools;

public static class MenuFrames
{
    private const int  CellSectors = 3000;
    private const long MinPngBytes = 1000;
    private static readonly string[] Seeks = { "0.7", "0" };

    public static bool Keep(ScreenRecord screen)
    {
        if (screen.ButtonSets.Count == 0) return false;
        foreach (var set in screen.ButtonSets)
            foreach (var button in set.Buttons)
                if (button.Resolved?.Kind is not ("menu" or "exit"))
                    return true;
        return false;
    }

    public static string PictureName(ScreenRecord screen) =>
        $"{Path.GetFileNameWithoutExtension(screen.Ifo)}_{screen.Menu}_pgc{screen.Pgc}_cell{screen.Cell}.png";

    public static void Extract(
        DiscRecord record, string videoTs, string menuFolder, string ffmpegPath,
        IProgress<string> progress, List<string> problems, CancellationToken token)
    {
        var wanted = record.Screens.Where(Keep).ToList();
        if (wanted.Count == 0) return;

        if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath))
        {
            problems.Add(string.IsNullOrWhiteSpace(ffmpegPath)
                ? "the ffmpeg path is not set in Preferences, so no menu pictures were made"
                : $"ffmpeg was not found at {ffmpegPath}, so no menu pictures were made");
            return;
        }

        Directory.CreateDirectory(menuFolder);
        var made = new Dictionary<(string, long, long), string?>();

        foreach (var group in wanted.GroupBy(s => s.Ifo))
        {
            MenuVob? vob;
            try
            {
                vob = MenuVob.Open(videoTs, group.Key);
            }
            catch (DiscSourceException ex)
            {
                problems.Add(ex.Message);
                continue;
            }
            if (vob == null)
            {
                problems.Add($"{group.Key}: no menu VOB to take pictures from");
                continue;
            }

            using (vob)
            {
                foreach (var screen in group)
                {
                    token.ThrowIfCancellationRequested();

                    var key = (screen.Ifo, screen.FirstSector, screen.LastSector);
                    if (!made.TryGetValue(key, out var picture))
                    {
                        picture = PictureName(screen);
                        progress.Report($"taking picture {picture}");
                        var problem = Grab(vob, screen, Path.Combine(menuFolder, picture), menuFolder, ffmpegPath, token);
                        if (problem != null)
                        {
                            problems.Add($"{picture}: {problem}");
                            picture = null;
                        }
                        made[key] = picture;
                    }
                    screen.Image = picture;
                }
            }
        }
    }

    // Returns null on success, or what went wrong.
    private static string? Grab(
        MenuVob vob, ScreenRecord screen, string outPng, string workFolder, string ffmpegPath, CancellationToken token)
    {
        var cell = Path.Combine(workFolder, "cell.vob.tmp");
        try
        {
            // A picture left from an earlier read must not pass for this one.
            try { File.Delete(outPng); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return $"the old picture could not be replaced: {ex.Message}";
            }

            try
            {
                using var w = new FileStream(cell, FileMode.Create, FileAccess.Write);
                vob.CopySectors(screen.FirstSector, Math.Min(screen.LastSector - screen.FirstSector + 1, CellSectors), w);
            }
            catch (Exception ex) when (ex is IOException or EndOfStreamException or UnauthorizedAccessException)
            {
                return $"the cell could not be cut out of {vob.FileName}: {ex.Message}";
            }

            string lastError = "";
            foreach (var seek in Seeks)
            {
                lastError = RunFfmpeg(ffmpegPath, new[] { "-v", "error", "-y", "-ss", seek, "-i", cell,
                                                          "-frames:v", "1", "-q:v", "2", outPng }, token);
                var png = new FileInfo(outPng);
                if (png.Exists && png.Length > MinPngBytes) return null;
            }
            return lastError.Length > 0 ? $"ffmpeg made no picture: {lastError}" : "ffmpeg made no picture";
        }
        finally
        {
            try { File.Delete(cell); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    // Runs ffmpeg and returns the first line it wrote to its error stream.
    private static string RunFfmpeg(string ffmpegPath, IEnumerable<string> args, CancellationToken token)
    {
        var psi = new ProcessStartInfo
        {
            FileName              = ffmpegPath,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute       = false,
            CreateNoWindow        = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return $"ffmpeg could not be started: {ex.Message}";
        }

        var stderr = process.StandardError.ReadToEndAsync();
        _ = process.StandardOutput.ReadToEndAsync();
        try
        {
            process.WaitForExitAsync(token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }

        var text = stderr.GetAwaiter().GetResult();
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
    }
}
