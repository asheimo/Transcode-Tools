// ============================================================
// ConfirmDialog.cs
// ------------------------------------------------------------
// A themed question whose buttons read Confirm and Cancel.
// MessageBox can only label its buttons Yes/No or OK/Cancel.
//
// Built in code, like MainWindow's rename dialog. Controls made in
// code miss implicit styles, so colours and button styles are set
// as resource references, which also follow a light/dark switch.
// ============================================================

using System.Windows;
using System.Windows.Controls;

namespace TranscodeTools;

internal static class ConfirmDialog
{
    // True when Confirm is clicked; false for Cancel, Esc or closing.
    public static bool Show(Window owner, string message, string title)
    {
        var win = new Window
        {
            Title                 = title,
            Owner                 = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent         = SizeToContent.WidthAndHeight,
            ResizeMode            = ResizeMode.NoResize,
            ShowInTaskbar         = false,
            MinWidth              = 340,
        };
        win.SetResourceReference(Control.BackgroundProperty, "WindowBg");

        var text = new TextBlock
        {
            Text         = message,
            FontSize     = 13,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth     = 460,
            Margin       = new Thickness(0, 0, 0, 16),
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "ForegroundColor");

        // IsCancel closes the dialog with no result on click or Esc.
        var cancel = new Button { Content = "Cancel", Width = 80, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        cancel.SetResourceReference(FrameworkElement.StyleProperty, "FlatButton");

        var confirm = new Button { Content = "Confirm", Width = 80, IsDefault = true };
        confirm.SetResourceReference(FrameworkElement.StyleProperty, "AccentButton");
        confirm.Click += (_, _) => win.DialogResult = true;

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(text);
        panel.Children.Add(buttons);
        win.Content = panel;

        return win.ShowDialog() == true;
    }
}
