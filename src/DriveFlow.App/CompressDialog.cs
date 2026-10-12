using System.Globalization;
using System.IO;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using DriveFlow.Core.Compress;
using Microsoft.Win32;

namespace DriveFlow.App;

/// <summary>Opções salvas em "compress_options", com os mesmos nomes da versão Python.</summary>
public sealed record CompressOptions(
    [property: JsonPropertyName("format")] string Format = "ZIP",
    [property: JsonPropertyName("method")] string Method = "Normal",
    [property: JsonPropertyName("split")] string Split = "20 GB",
    [property: JsonPropertyName("custom_gb")] double CustomGb = 4.0,
    [property: JsonPropertyName("output")] string Output = "");

/// <summary>Opções passadas ao WinRAR (compress_dialog.py). Uma pasta por vez; o dicionário é sempre 4096 KB.</summary>
public static class CompressDialog
{
    const string Custom = "Tamanho personalizado";

    public delegate Task<Compression> Starter(string source, string output, string name, CompressOptions options, long split, string password);

    public static Compression? Show(Window owner, string folder, CompressOptions options, Starter start)
    {
        var (window, body, buttons) = Dialogs.Build(owner, "Compactar com o WinRAR", 580);
        Compression? result = null;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        body.Children.Add(grid);

        void Row(string label, UIElement field)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var row = grid.RowDefinitions.Count - 1;
            var text = new TextBlock { Text = label, Margin = new Thickness(0, 0, 16, 10) };
            text.SetResourceReference(FrameworkElement.StyleProperty, "FormLabel");
            Grid.SetRow(text, row);
            Grid.SetRow(field, row);
            Grid.SetColumn(field, 1);
            if (field is FrameworkElement element) element.Margin = new Thickness(0, 0, 0, 10);
            grid.Children.Add(text);
            grid.Children.Add(field);
        }

        static DockPanel WithRight(UIElement main, UIElement right)
        {
            var panel = new DockPanel();
            DockPanel.SetDock(right, Dock.Right);
            panel.Children.Add(right);
            panel.Children.Add(main);
            return panel;
        }

        TextBox PathRow(string label, string value, string pickerTitle, Action<string>? picked = null)
        {
            var field = new TextBox { Text = value, IsReadOnly = true, Tag = "Selecione uma pasta…" };
            var pick = new Button { Content = "Selecionar…", Margin = new Thickness(8, 0, 0, 0) };
            pick.Click += (_, _) =>
            {
                var dialog = new OpenFolderDialog { Title = pickerTitle, Multiselect = false };
                if (Directory.Exists(field.Text)) dialog.InitialDirectory = field.Text;
                if (dialog.ShowDialog(window) != true) return;
                picked?.Invoke(dialog.FolderName);
                field.Text = dialog.FolderName;
            };
            Row(label, WithRight(field, pick));
            return field;
        }

        var name = new TextBox { Text = folder != "" ? Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)) : "" };
        TextBox? source = null;
        source = PathRow("Pasta de origem", folder, "Pasta para compactar", path =>
        {
            var previous = Path.GetFileName(Path.TrimEndingDirectorySeparator(source!.Text));
            if (name.Text.Trim() == "" || name.Text == previous) name.Text = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        });
        var output = PathRow("Salvar volumes em", Directory.Exists(options.Output) ? options.Output : "", "Onde salvar os volumes");
        Row("Nome da pasta", name);

        var formats = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var rar = new RadioButton { Content = "RAR", GroupName = "format", IsChecked = options.Format == "RAR", Margin = new Thickness(0, 0, 18, 0) };
        var zip = new RadioButton { Content = "ZIP", GroupName = "format", IsChecked = options.Format != "RAR" };
        formats.Children.Add(rar);
        formats.Children.Add(zip);
        Row("Formato", formats);

        var method = new ComboBox { ItemsSource = WinRar.Methods, SelectedItem = WinRar.Methods.Contains(options.Method) ? options.Method : "Normal" };
        Row("Método de compressão", method);

        string[] splits = [.. WinRar.Splits, Custom];
        var split = new ComboBox { ItemsSource = splits, SelectedItem = splits.Contains(options.Split) ? options.Split : "20 GB" };
        var custom = new TextBox { Width = 110, Margin = new Thickness(8, 0, 0, 0), Tag = "GB",
                                   Text = options.CustomGb.ToString("0.##", CultureInfo.GetCultureInfo("pt-BR")) + " GB" };
        void ShowCustom() => custom.Visibility = (string)split.SelectedItem == Custom ? Visibility.Visible : Visibility.Collapsed;
        split.SelectionChanged += (_, _) => ShowCustom();
        ShowCustom();
        Row("Dividir em volumes", WithRight(split, custom));

        // Senha escondida ou visível ("Mostrar"); as duas caixas ficam sincronizadas.
        var hidden = new PasswordBox();
        var shown = new TextBox { Visibility = Visibility.Collapsed, Tag = "Sem senha" };
        var hint = new TextBlock { Text = "Sem senha", Margin = new Thickness(11, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Disabled");
        hidden.PasswordChanged += (_, _) =>
        {
            hint.Visibility = hidden.Password == "" && hidden.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
            if (shown.Text != hidden.Password) shown.Text = hidden.Password;
        };
        shown.TextChanged += (_, _) => { if (hidden.Password != shown.Text) hidden.Password = shown.Text; };
        var show = new CheckBox { Content = "Mostrar", Margin = new Thickness(10, 0, 0, 0) };
        show.Checked += (_, _) => { shown.Visibility = Visibility.Visible; hidden.Visibility = hint.Visibility = Visibility.Collapsed; };
        show.Unchecked += (_, _) =>
        {
            shown.Visibility = Visibility.Collapsed;
            hidden.Visibility = Visibility.Visible;
            hint.Visibility = hidden.Password == "" ? Visibility.Visible : Visibility.Collapsed;
        };
        var boxes = new Grid();
        boxes.Children.Add(hidden);
        boxes.Children.Add(shown);
        boxes.Children.Add(hint);
        Row("Senha do arquivo", WithRight(boxes, show));

        CompressOptions Options() => new(rar.IsChecked == true ? "RAR" : "ZIP", (string)method.SelectedItem, (string)split.SelectedItem,
                                         ParseGb(custom.Text) ?? options.CustomGb, output.Text);

        async void Confirm(Button ok)
        {
            if (source!.Text == "" || output.Text == "")
            {
                Dialogs.Message(window, "Selecione a pasta de origem e onde salvar os volumes.");
                return;
            }
            try
            {
                WinRar.CheckOutput(source.Text, output.Text);
                if (WinRar.SameDisk(source.Text, output.Text) && !Dialogs.Ask(window, "Mesmo disco",
                        "A origem e os volumes estão no mesmo disco, o que deixa a compactação mais lenta. Continuar mesmo assim?"))
                    return;
                var chosen = Options();
                var bytes = chosen.Split == Custom
                    ? WinRar.SplitBytes((ParseGb(custom.Text) ?? throw new ArgumentException("Tamanho de divisão inválido.")).ToString(CultureInfo.InvariantCulture) + " GB")
                    : WinRar.SplitBytes(chosen.Split);
                ok.IsEnabled = false;
                result = await start(source.Text, output.Text, name.Text, chosen, bytes, hidden.Password);
            }
            catch (Exception exc) when (exc is IOException or ArgumentException or UnauthorizedAccessException or InvalidOperationException
                                        or System.ComponentModel.Win32Exception)
            {
                ok.IsEnabled = true;
                Dialogs.Message(window, exc.Message);
                return;
            }
            window.Close();
        }

        Dialogs.AddButton(buttons, "Cancelar", false, window.Close);
        Button? okButton = null;
        okButton = Dialogs.AddButton(buttons, "Compactar", true, () => Confirm(okButton!));
        window.ShowDialog();
        return result;
    }

    static double? ParseGb(string text)
    {
        var clean = text.Replace("GB", "", StringComparison.OrdinalIgnoreCase).Trim().Replace(',', '.');
        return double.TryParse(clean, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value is >= 0.01 and <= 10000 ? value : null;
    }
}
