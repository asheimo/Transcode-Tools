// ============================================================
// App.xaml.cs
// ------------------------------------------------------------
// This is the APPLICATION ENTRY POINT code-behind.
// App.xaml.cs pairs with App.xaml, just like a Form's .vb
// file pairs with its designer file in VB.NET.
//
// Its main job here is to detect the Windows light/dark theme
// and swap the correct colour dictionary in at startup,
// then keep watching for theme changes while the app runs.
// ============================================================

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32; // Needed to read the Windows registry

namespace TranscodeTools;

// "partial class" means this class is split across two files —
// App.xaml and App.xaml.cs. The XAML side is auto-generated;
// we only write code in this .cs file.
// In VB.NET: Partial Public Class App
public partial class App : Application
{
    // ── Single-instance guard ─────────────────────────────────────────
    // A Mutex is a kernel-level lock with a globally unique name. Only
    // one process at a time can "own" a named mutex, so we use it as
    // the canonical "is TranscodeTools already running?" signal.
    //
    // Why a field on the App class: we need the mutex to live as long as
    // the process does. If it went out of scope (e.g. local variable in
    // OnStartup), the GC could finalize it, releasing the lock and
    // letting a second instance start while the first is still running.
    // Holding it as a field keeps it alive for the application lifetime.
    //
    // The "Local\\" prefix scopes the mutex to the current Windows user
    // session. Two different users on the same machine can each run
    // their own instance — only same-user duplicates are blocked. Use
    // "Global\\" instead if you ever want to block across user sessions.
    private const string SingleInstanceMutexName = "Local\\TranscodeTools_SingleInstance_v1";
    private System.Threading.Mutex? _singleInstanceMutex;

    // Win32 imports for activating the existing instance's window.
    // user32.dll exposes the windowing functions Windows itself uses
    // for taskbar clicks, alt-tab, etc.
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(System.IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(System.IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(System.IntPtr hWnd);  // "iconic" = minimised

    private const int SW_RESTORE = 9;  // Restore a minimised window

    // OnStartup is called once when the application first launches.
    // It's the equivalent of Sub Main() or Form_Load in VB.NET.
    // "override" means we're replacing the base class version of this method.
    protected override void OnStartup(StartupEventArgs e)
    {
        // ── Single-instance check (must run BEFORE base.OnStartup) ────
        // We try to create-and-immediately-acquire the named mutex.
        // The "out createdNew" parameter tells us whether we made it
        // fresh (true → we're the first instance) or attached to an
        // existing one (false → another instance owns it).
        _singleInstanceMutex = new System.Threading.Mutex(
            initiallyOwned: true,
            name:           SingleInstanceMutexName,
            createdNew:     out bool createdNew);

        if (!createdNew)
        {
            // Another instance is running. Bring its main window to the
            // foreground, show the user a message, then exit cleanly.
            ActivateExistingInstance();

            MessageBox.Show(
                "TranscodeTools is already running.",
                "Already Running",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            // Shutdown() ends the WPF app loop. Returning here without
            // calling it would still run the rest of OnStartup, which
            // we don't want — no theme load, no MainWindow construction.
            Shutdown();
            return;
        }

        // Always call the base class version first so WPF can do its own setup.
        // In VB.NET: MyBase.OnStartup(e)
        base.OnStartup(e);

        // Apply the correct theme immediately on startup
        ApplySystemTheme();

        // Subscribe to the Windows system event that fires when the user
        // changes their theme in Windows Settings.
        // += is how you ATTACH an event handler in C#.
        // In VB.NET: AddHandler SystemEvents.UserPreferenceChanged, AddressOf ...
        SystemEvents.UserPreferenceChanged += (s, args) =>
        {
            // Only care about "General" preference changes, which includes theme
            if (args.Category == UserPreferenceCategory.General)
                // Dispatcher.Invoke ensures we update the UI on the UI thread.
                // WPF (like WinForms) requires UI updates to happen on the main thread.
                Dispatcher.Invoke(ApplySystemTheme);
        };
    }

    // OnExit fires when the WPF app is shutting down. Release the mutex
    // explicitly here. Windows would clean it up on process exit anyway,
    // but explicit release is cleaner and avoids any edge cases where
    // the kernel hasn't reclaimed it before a quick relaunch.
    protected override void OnExit(ExitEventArgs e)
    {
        if (_singleInstanceMutex != null)
        {
            try { _singleInstanceMutex.ReleaseMutex(); } catch { /* not owned — fine */ }
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }
        base.OnExit(e);
    }

    // Find the existing TranscodeTools process and bring its main window
    // to the foreground. If the window is minimised, restore it first.
    //
    // Caveat: Windows sometimes refuses foreground swaps from background
    // processes (anti-focus-stealing protection). When that happens
    // SetForegroundWindow returns false and the taskbar icon flashes
    // instead — which is fine, the user still notices.
    private static void ActivateExistingInstance()
    {
        // Process.GetCurrentProcess().Id is OUR PID; we want the OTHER
        // TranscodeTools process. GetProcessesByName returns all processes
        // matching the executable name (without .exe).
        var current  = Process.GetCurrentProcess();
        var existing = Process.GetProcessesByName(current.ProcessName)
                              .FirstOrDefault(p => p.Id != current.Id);

        if (existing == null) return;  // Mutex disagrees with process list; nothing to do

        var hWnd = existing.MainWindowHandle;
        if (hWnd == System.IntPtr.Zero) return;  // Window not yet created

        // If minimised, restore before bringing to front
        if (IsIconic(hWnd))
            ShowWindow(hWnd, SW_RESTORE);

        SetForegroundWindow(hWnd);
    }

    // "public static" means this method can be called from anywhere without
    // needing an instance of App. In VB.NET: Public Shared Sub ApplySystemTheme()
    public static void ApplySystemTheme()
    {
        bool isDark = IsSystemDarkTheme();

        // Build the path to the theme file we want to load.
        // The ? : is the TERNARY OPERATOR — a compact if/else.
        // isDark ? "Dark..." : "Light..." means:
        //   If isDark Then "DarkTheme.xaml" Else "LightTheme.xaml"
        var targetSource = isDark ? "Themes/DarkTheme.xaml" : "Themes/LightTheme.xaml";

        // MergedDictionaries is the list of ResourceDictionaries loaded into the app.
        // We need to find the currently loaded theme and swap it out.
        var existing = Current.Resources.MergedDictionaries;

        // Find whichever theme dictionary is currently loaded.
        // FirstOrDefault returns the first match, or null if none found.
        // The lambda (d => ...) is like an inline function — in VB.NET you'd
        // write this as a LINQ expression with a Function keyword.
        var current = existing.FirstOrDefault(d =>
            d.Source?.OriginalString.EndsWith("LightTheme.xaml", StringComparison.OrdinalIgnoreCase) == true ||
            d.Source?.OriginalString.EndsWith("DarkTheme.xaml",  StringComparison.OrdinalIgnoreCase) == true);

        // If we're already on the right theme, do nothing
        if (current?.Source?.OriginalString == targetSource) return;

        // Create the new theme dictionary
        var newDict = new ResourceDictionary
        {
            Source = new Uri(targetSource, UriKind.Relative)
        };

        // Insert the new theme at position 0 (before ThemeResources.xaml),
        // then remove the old one. DynamicResource bindings in the XAML will
        // automatically pick up the new colour values.
        existing.Insert(0, newDict);
        if (current != null) existing.Remove(current);
    }

    // Reads the Windows registry to determine if dark mode is enabled.
    // "private static" — only used inside this class, no instance needed.
    public static bool IsSystemDarkTheme()
    {
        try
        {
            // Open the registry key that stores the Windows theme preference.
            // "using" here is NOT the same as "using" at the top of the file —
            // this "using" ensures the registry key is closed/disposed automatically
            // when we're done. In VB.NET: Using key = Registry.CurrentUser.OpenSubKey(...)
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");

            var val = key?.GetValue("AppsUseLightTheme");

            // If the value is 0, dark mode is ON (light mode is OFF).
            // "val is int i" is a PATTERN MATCH — it checks if val is an int
            // AND assigns it to variable i at the same time.
            // In VB.NET: If TypeOf val Is Integer AndAlso CInt(val) = 0 Then ...
            return val is int i && i == 0;
        }
        catch
        {
            // If anything goes wrong reading the registry, default to light theme
            return false;
        }
    }
}
