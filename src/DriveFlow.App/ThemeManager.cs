using System.Windows;
using Microsoft.Win32;

namespace DriveFlow.App;

public static class ThemeManager
{
    public const string Green = "Grafite e verde";
    public static readonly string[] Themes = ["Automático", "Claro", "Escuro", Green];
    // Temas das versões anteriores: os verdes viram Grafite e verde; o azul profundo, Escuro.
    static readonly HashSet<string> LegacyGreen = ["Cinza grafite", "Verde escuro", "Spotify"];

    public static string Normalize(string saved) =>
        LegacyGreen.Contains(saved) ? Green : saved == "Azul profundo" ? "Escuro" : Themes.Contains(saved) ? saved : "Automático";

    /// <summary>Modo escuro dos aplicativos em Configurações > Personalização > Cores.</summary>
    public static bool WindowsPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsDark(string theme) => Normalize(theme) switch
    {
        "Escuro" or Green => true,
        "Claro" => false,
        _ => WindowsPrefersDark(),
    };

    public static void Apply(string theme)
    {
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var file = Normalize(theme) == Green ? "Green" : IsDark(theme) ? "Dark" : "Light";
        var palette = new ResourceDictionary { Source = new Uri($"pack://application:,,,/DriveFlow;component/Themes/{file}.xaml") };
        dictionaries[0] = palette;
    }
}
