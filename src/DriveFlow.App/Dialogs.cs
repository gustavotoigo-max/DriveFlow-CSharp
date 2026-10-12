using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;

namespace DriveFlow.App;

/// <summary>Janelas de aviso, confirmação e texto no padrão da janela principal (sem borda, cabeçalho azul-marinho).</summary>
public static class Dialogs
{
    internal static (Window Window, StackPanel Body, StackPanel Buttons) Build(Window? owner, string title, double width = 440)
    {
        var window = new Window
        {
            Owner = owner, Title = title, SizeToContent = SizeToContent.Height, Width = width, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            ShowInTaskbar = owner is null, WindowStyle = WindowStyle.None,
        };
        window.SetResourceReference(Control.BackgroundProperty, "Surface");
        window.SetResourceReference(Control.BorderBrushProperty, "Border");
        window.BorderThickness = new Thickness(1);
        WindowChrome.SetWindowChrome(window, new WindowChrome { CaptionHeight = 36, ResizeBorderThickness = new Thickness(0), GlassFrameThickness = new Thickness(0) });
        var root = new DockPanel();
        var header = new Border { Height = 36, Padding = new Thickness(14, 0, 14, 0) };
        header.SetResourceReference(Border.BackgroundProperty, "Navy");
        var caption = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "OnNavy");
        header.Child = caption;
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        var line = new Border { Height = 2, Background = new LinearGradientBrush(Color.FromRgb(0x25, 0x63, 0xEB), Color.FromRgb(0x22, 0xD3, 0xEE), 0) };
        DockPanel.SetDock(line, Dock.Top);
        root.Children.Add(line);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(18, 0, 18, 16) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        var body = new StackPanel { Margin = new Thickness(18, 16, 18, 16) };
        root.Children.Add(body);
        window.Content = root;
        window.KeyDown += (_, e) => { if (e.Key == Key.Escape) window.Close(); };
        return (window, body, buttons);
    }

    internal static TextBlock Text(string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        block.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        return block;
    }

    internal static Button AddButton(StackPanel buttons, string text, bool primary, Action click)
    {
        var button = new Button { Content = text, IsDefault = primary, MinWidth = 88, Margin = new Thickness(4, 0, 4, 0) };
        if (primary) button.SetResourceReference(FrameworkElement.StyleProperty, "Primary");
        button.Click += (_, _) => click();
        buttons.Children.Add(button);
        return button;
    }

    public static void Message(Window? owner, string text, string title = "DriveFlow")
    {
        var (window, body, buttons) = Build(owner, title);
        body.Children.Add(Text(text));
        AddButton(buttons, "OK", true, window.Close);
        window.ShowDialog();
    }

    public static bool Ask(Window? owner, string title, string text)
    {
        var (window, body, buttons) = Build(owner, title);
        var answer = false;
        body.Children.Add(Text(text));
        AddButton(buttons, "Não", false, window.Close);
        AddButton(buttons, "Sim", true, () => { answer = true; window.Close(); });
        window.ShowDialog();
        return answer;
    }

    public static string? GetText(Window? owner, string title, string prompt, string text = "")
    {
        var (window, body, buttons) = Build(owner, title);
        string? answer = null;
        body.Children.Add(Text(prompt));
        var box = new TextBox { Text = text, Margin = new Thickness(0, 10, 0, 0) };
        body.Children.Add(box);
        AddButton(buttons, "Cancelar", false, window.Close);
        AddButton(buttons, "OK", true, () => { answer = box.Text; window.Close(); });
        window.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        window.ShowDialog();
        return answer;
    }
}
