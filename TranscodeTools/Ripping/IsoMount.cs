// ============================================================
// IsoMount.cs
// ------------------------------------------------------------
// Mounts a backup ISO so its files can be read, and unmounts it.
//
// Done through Windows PowerShell's Mount-DiskImage, the same
// commands measured by hand:
//   - mounted with -NoDriveLetter, so the image never gets a drive
//     letter and never shows up in the drives box;
//   - read through the volume path Windows hands back, e.g.
//     \\?\Volume{cc9e7c33-...}\ -- the five IFOs of THE_CENTENNIAL
//     were listed through such a path;
//   - about 4 s to mount, well under a second to unmount.
//
// An image that is already mounted cannot be mounted again, so it is
// asked for first:
//   - mounted WITH a drive letter: someone mounted it by hand. It is
//     read where it is and left mounted.
//   - mounted with NO drive letter: a mount of the app's own left over
//     from a crash (a hand mount always gets a letter). It is read and
//     then unmounted, which cleans the leftover up.
//
// The ISO path is passed to PowerShell in an environment variable
// rather than on the command line, so a path with spaces or quotes
// needs no escaping. Progress output is switched off: PowerShell
// started from another program writes progress to the error stream.
// ============================================================

using System.Diagnostics;
using System.IO;
using System.Text;

namespace TranscodeTools;

public sealed class IsoMountException : Exception
{
    public IsoMountException(string message) : base(message) { }
}

public sealed class IsoMount
{
    // Where the image's files are, e.g. \\?\Volume{...}\
    public string VolumePath { get; }

    // True when this app mounted the image (or its own leftover mount
    // was found), so it is the app's to unmount.
    public bool Ours { get; }

    private IsoMount(string volumePath, bool ours)
    {
        VolumePath = volumePath;
        Ours       = ours;
    }

    private const string MountScript = @"
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$img = Get-DiskImage -ImagePath $env:TT_ISO
$ours = $true
if ($img.Attached) {
    $vol = $img | Get-Volume
    if (""$($vol.DriveLetter)"".Trim([char]0).Length -gt 0) { $ours = $false }
} else {
    $img = Mount-DiskImage -ImagePath $env:TT_ISO -NoDriveLetter -PassThru
    $vol = $img | Get-Volume
}
""$($vol.Path)|$ours""
";

    private const string DismountScript = @"
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Dismount-DiskImage -ImagePath $env:TT_ISO | Out-Null
";

    public static async Task<IsoMount> MountAsync(string isoPath)
    {
        var output = await RunAsync(MountScript, isoPath);

        // The script prints one line: <volume path>|<True or False>.
        var line  = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
        var parts = line.Split('|');
        if (parts.Length != 2 || !parts[0].StartsWith(@"\\?\Volume", StringComparison.OrdinalIgnoreCase))
            throw new IsoMountException($"Windows did not report a volume for {isoPath}. PowerShell said: {line}");

        return new IsoMount(parts[0], parts[1].Equals("True", StringComparison.OrdinalIgnoreCase));
    }

    public static Task DismountAsync(string isoPath) => RunAsync(DismountScript, isoPath);

    // Runs one script in Windows PowerShell and returns what it printed.
    // With ErrorActionPreference set to Stop, any error ends the script
    // with a non-zero exit code; that is the failure test, and the error
    // stream supplies PowerShell's own message for it.
    private static async Task<string> RunAsync(string script, string isoPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = "powershell.exe",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding  = Encoding.UTF8,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add("[Console]::OutputEncoding = [Text.Encoding]::UTF8;" + script);
        psi.Environment["TT_ISO"] = isoPath;

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new IsoMountException($"PowerShell could not be started: {ex.Message}");
        }

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var output = await stdout;
        var errors = (await stderr).Trim();

        if (process.ExitCode != 0)
            throw new IsoMountException(errors.Length > 0
                ? $"{Path.GetFileName(isoPath)}: {FirstLine(errors)}"
                : $"{Path.GetFileName(isoPath)}: PowerShell exited with code {process.ExitCode}.");

        return output;
    }

    // PowerShell's error text runs to several lines of position detail;
    // the first line is the message itself.
    private static string FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? text;
}
