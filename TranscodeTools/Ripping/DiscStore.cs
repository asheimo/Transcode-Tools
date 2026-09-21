// ============================================================
// DiscStore.cs
// ------------------------------------------------------------
// Rip mode's folder layout under the Destination, and saving and
// loading the disc records kept in it.
//
//   <Destination>\
//       ISO\<DISC>.iso                    MakeMKV's backup image
//       Rip\<DISC>\                       the rips; remux reads this folder
//           Menu\disc.json                the disc record
//           Menu\*.png                    cached menu frames
//       Reserve\                          space held for running jobs
//       Logs\Backup\<DISC>.log            one per disc, overwritten on a repeat
//       Logs\<timestamp>\<DISC>\          info/rip/naming logs
//
// The record lives inside the rip folder on purpose: when that
// folder is deleted after remux, the record goes with it. The
// backup log under Logs\Backup\ is what survives, as the history
// of which discs have been processed.
//
// JSON via System.Text.Json, the same way AppSettings is saved.
// ============================================================

using System.IO;
using System.Text.Json;

namespace TranscodeTools;

public static class DiscStore
{
    public const string IsoFolderName    = "ISO";
    public const string RipFolderName    = "Rip";
    public const string MenuFolderName   = "Menu";
    public const string LogsFolderName   = "Logs";
    public const string BackupFolderName = "Backup";
    public const string RecordFileName   = "disc.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    // ── Paths ────────────────────────────────────────────────────────

    public static string IsoFolder(string destination) =>
        Path.Combine(destination, IsoFolderName);

    public static string IsoPath(string destination, string discName) =>
        Path.Combine(IsoFolder(destination), discName + ".iso");

    public static string RipRoot(string destination) =>
        Path.Combine(destination, RipFolderName);

    public static string RipFolder(string destination, string discName) =>
        Path.Combine(RipRoot(destination), discName);

    // The disc's Menu folder: its record and its cached menu frames.
    public static string DiscFolder(string destination, string discName) =>
        Path.Combine(RipFolder(destination, discName), MenuFolderName);

    public static string BackupLogFolder(string destination) =>
        Path.Combine(destination, LogsFolderName, BackupFolderName);

    public static string BackupLogPath(string destination, string discName) =>
        Path.Combine(BackupLogFolder(destination), discName + ".log");

    // ── Records ──────────────────────────────────────────────────────

    // Reads every disc record under <Destination>\Rip\, sorted by name.
    // A rip folder with no Menu\disc.json is not a problem; it is skipped.
    // A record that can't be read is not skipped silently: its folder and
    // the reason go into `problems` so the caller can show them.
    public static List<DiscRecord> LoadAll(string destination, List<string> problems)
    {
        var discs = new List<DiscRecord>();
        var root  = RipRoot(destination);
        if (!Directory.Exists(root)) return discs;

        foreach (var folder in Directory.GetDirectories(root).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var path = Path.Combine(folder, MenuFolderName, RecordFileName);
            if (!File.Exists(path)) continue;

            try
            {
                var disc = JsonSerializer.Deserialize<DiscRecord>(File.ReadAllText(path), JsonOptions);
                if (disc == null)
                {
                    problems.Add($"{Path.GetFileName(folder)}: {RecordFileName} is empty");
                    continue;
                }

                // Not stored: the rip folder is wherever the record was found.
                disc.RipFolder = folder;
                discs.Add(disc);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                problems.Add($"{Path.GetFileName(folder)}: {ex.Message}");
            }
        }
        return discs;
    }

    // Writes the record to a temporary file first, then swaps it into
    // place, so a crash mid-write never leaves a half-written disc.json.
    public static void Save(string destination, DiscRecord disc)
    {
        var folder = DiscFolder(destination, disc.Name);
        Directory.CreateDirectory(folder);

        var path = Path.Combine(folder, RecordFileName);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(disc, JsonOptions));
        File.Move(temp, path, overwrite: true);

        disc.RipFolder = RipFolder(destination, disc.Name);
    }
}
