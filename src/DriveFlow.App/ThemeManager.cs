using System.Windows;
using Microsoft.Win32;

namespace DriveFlow.App;

public static class ThemeManager
{
    public static readonly string[] Themes = ["Automático", "Claro", "Escuro"];
    // Temas escuros das versões anteriores continuam escuros.
    static readonly HashSet<string> LegacyDark = ["Azul profundo", "Cinza grafite", "Verde escuro", "Spotify", "Grafite e verde"];

    public static string Normalize(string saved) => LegacyDark.Contains(saved) ? "Escuro" : Themes.Contains(saved) ? saved : "Automático";

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
        "Escuro" => true,
        "Claro" => false,
        _ => WindowsPrefersDark(),
    };

    public static void Apply(string theme)
    {
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var palette = new ResourceDictionary { Source = new Uri($"pack://application:,,,/DriveFlow;component/Themes/{(IsDark(theme) ? "Dark" : "Light")}.xaml") };
        dictionaries[0] = palette;
    }
}
