using System.Diagnostics;
using Microsoft.Win32;

namespace DriveFlow.App;

/// <summary>Abre o DriveFlow ao entrar no Windows, só para o usuário atual (startup.py).</summary>
public static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    // Nome próprio enquanto a versão Python continua instalada com o valor "DriveFlow".
    const string ValueName = "DriveFlowNet";

    static string Command => $"\"{Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName}\"";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
        if (enabled) key.SetValue(ValueName, Command);
        else key.DeleteValue(ValueName, false);
    }
}
