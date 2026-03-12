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

using System.Windows;
using Microsoft.Win32; // Needed to read the Windows registry

namespace TranscodeTools;

// "partial class" means this class is split across two files —
// App.xaml and App.xaml.cs. The XAML side is auto-generated;
// we only write code in this .cs file.
// In VB.NET: Partial Public Class App
public partial class App : Application
{
    // OnStartup is called once when the application first launches.
    // It's the equivalent of Sub Main() or Form_Load in VB.NET.
    // "override" means we're replacing the base class version of this method.
    protected override void OnStartup(StartupEventArgs e)
    {
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
