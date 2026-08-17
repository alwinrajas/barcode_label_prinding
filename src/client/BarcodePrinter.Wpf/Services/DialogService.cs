using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BarcodePrinter.Wpf.Services;

/// <summary>
/// Design-system styled confirm / prompt dialogs, replacing MessageBox.Show.
/// Modal (ShowDialog) with keyboard support: Enter confirms, Escape cancels.
/// </summary>
public static class DialogService
{
    /// <summary>True when the user confirmed. Pass danger for destructive
    /// actions so the confirm button reads as such.</summary>
    public static Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK",
        bool danger = false)
    {
        var confirm = MakeButton(confirmText, danger ? "Button.Danger" : "Button.Primary");
        confirm.IsDefault = true;
        confirm.MinWidth = 88;
        var cancel = MakeButton("Cancel", "Button.Secondary");
        cancel.IsCancel = true;
        cancel.MinWidth = 88;

        var window = MakeWindow(title, new StackPanel
        {
            Children =
            {
                MakeBody(message),
                MakeButtonRow(cancel, confirm),
            },
        });
        confirm.Click += (_, _) => window.DialogResult = true;

        return Task.FromResult(window.ShowDialog() == true);
    }

    /// <summary>Single-line text prompt. Returns the entered text, or null
    /// when cancelled.</summary>
    public static Task<string?> PromptAsync(string title, string label, string initial = "")
    {
        var input = new TextBox { Text = initial, Margin = new Thickness(0, 8, 0, 0) };
        if (Application.Current?.TryFindResource("Input.Text") is Style inputStyle)
        {
            input.Style = inputStyle;
        }

        var ok = MakeButton("OK", "Button.Primary");
        ok.IsDefault = true;
        ok.MinWidth = 88;
        var cancel = MakeButton("Cancel", "Button.Secondary");
        cancel.IsCancel = true;
        cancel.MinWidth = 88;

        var window = MakeWindow(title, new StackPanel
        {
            Children =
            {
                MakeBody(label),
                input,
                MakeButtonRow(cancel, ok),
            },
        });
        ok.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
        };

        return Task.FromResult(window.ShowDialog() == true ? input.Text : null);
    }

    private static Window MakeWindow(string title, UIElement content)
    {
        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
            ?? Application.Current?.MainWindow;
        var window = new Window
        {
            Title = title,
            Width = 400,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.SingleBorderWindow,
            ShowInTaskbar = false,
            Background = Application.Current?.TryFindResource("Surface.Card") as Brush ?? Brushes.White,
            Content = new Border { Padding = new Thickness(24), Child = content },
        };
        if (owner is not null && owner.IsLoaded)
        {
            window.Owner = owner;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        return window;
    }

    private static TextBlock MakeBody(string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        if (Application.Current?.TryFindResource("Text.Body") is Style style)
        {
            block.Style = style;
        }
        return block;
    }

    private static Button MakeButton(string content, string styleKey)
    {
        var button = new Button { Content = content };
        if (Application.Current?.TryFindResource(styleKey) is Style style)
        {
            button.Style = style;
        }
        return button;
    }

    private static StackPanel MakeButtonRow(Button cancel, Button confirm)
    {
        cancel.Margin = new Thickness(0, 0, 8, 0);
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0),
            Children = { cancel, confirm },
        };
    }
}
