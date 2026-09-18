// ============================================================
// DiscStore.cs
// ------------------------------------------------------------
// Saves and loads disc records. Layout under the Destination:
//
//   <Destination>\Discs\<DISC>\disc.json   the record
//   <Destination>\Discs\<DISC>\*.png       cached menu frames
//
// Same pattern as the Remux/Transcode settings, which live in a
// mode folder with a subfolder per item. JSON via System.Text.Json,
// the same way AppSettings is saved.
// ============================================================

using System.IO;
using System.Text.Json;

namespace TranscodeTools;

public static class DiscStore
{
    public const string DiscsFolderName = "Discs";
    public const string RecordFileName  = "disc.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string DiscsFolder(string destination) =>
        Path.Combine(destination, DiscsFolderName);

    public static string DiscFolder(string destination, string discName) =>
        Path.Combine(DiscsFolder(destination), discName);

    // Reads every disc record under <Destination>\Discs\, sorted by name.
    // A record that can't be read is not skipped silently: its folder and
    // the reason go into `problems` so the caller can show them.
    public static List<DiscRecord> LoadAll(string destination, List<string> problems)
    {
        var discs = new List<DiscRecord>();
        var root  = DiscsFolder(destination);
        if (!Directory.Exists(root)) return discs;

        foreach (var folder in Directory.GetDirectories(root).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var path = Path.Combine(folder, RecordFileName);
            if (!File.Exists(path)) continue;

            try
            {
                var disc = JsonSerializer.Deserialize<DiscRecord>(File.ReadAllText(path), JsonOptions);
                if (disc == null)
                    problems.Add($"{Path.GetFileName(folder)}: {RecordFileName} is empty");
                else
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
    }
}
