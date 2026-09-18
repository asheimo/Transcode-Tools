# Development Rules

## Process Rules

- Ask for fresh solution zip at the start of every session — never assume the container matches the repo
- Read the reference doc in full at the start of every session before touching any code — this is why it exists
- Before packaging: grep entire solution, explain changes clearly, ask questions, package only on explicit approval
- Present summary of changes BEFORE asking to package — not after
- After every file edit, view the full file to verify formatting before zipping
- Zip files contain only changed files at root level — no subfolder prefix. Name the zip descriptively for what is being fixed (e.g. `TranscodeTools-CommandBuilder_qsv_color_fix.v1.zip`). If multiple iterations are produced in a session, version the filename (v1, v2, etc.). Always create a new zip file rather than overwriting an existing one.
- Modernize controls; do not carry forward old patterns just because they were used before
- App uses WPF Aero2 currently. Dark mode via DynamicResource theme switching. Fluent theme deferred to Step 12
- When fixing Aero2 theming: always fetch actual template from VS2022 `DesignTools/SystemThemes/wpf/Aero2.NormalColor.xaml` — never guess at brush key names
- Andy is learning — explain the why behind every decision, not just the what
- For scripts that will be executed in a shell (bash, PowerShell, cmd), grep for non-ASCII characters before delivering. Windows consoles read UTF-8 files as Windows-1252 and em-dashes / smart quotes inside strings cause parse errors.

## Technical Rules

- Custom ControlTemplates must wire Foreground via `TextElement.Foreground`
- Never remove a colour property without replacing it with a SystemColors equivalent
- `Background="Transparent"` belongs in global styles, not on individual elements
- Bindings that can return null must set `TargetNullValue`
- Dynamically-created controls in code-behind bypass implicit styles — set Foreground explicitly via `Application.Current.FindResource("ForegroundColor")`
- Case-only folder renames must use a two-step temp folder — Windows FS is case-insensitive and `Directory.Move` fails on case-only changes
- Case-only file renames: skip `File.Exists` conflict check (false positive on Windows); use two-step temp file

## File-Specific Rules

| File | Rule |
| --- | --- |
| **FfprobeService.cs** | `IsSelected` defaults to FALSE for RemuxAudioTrack and RemuxSubtitleTrack — do not revert |
| **CommandBuilder.cs** | Always include `using System.IO;` at the top |
| **XAML comments** | Use em dash + en dash (——) for `--` flag prefixes; copy from existing instance, never type fresh |
| **Transcode dropdowns** | Defined as `x:Array` in Window.Resources; always StaticResource binding, never RelativeSource |
| **ComboBox theming** | Full ControlTemplate in ThemeResources.xaml — do not revert to brush key overrides |
| **TreeViewItem theming** | Full ControlTemplate in ThemeResources.xaml — eliminates Aero2 RelativeSource binding errors |
| **RefreshHistoryDropdowns** | Sets ComboBox text inside suppress block — do not restore `SelectedItem=null` (clears Text) |
